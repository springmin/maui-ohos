// WebAuthenticator (MAUI Essentials IWebAuthenticator) for OpenHarmony: the real browser
// redirect flow, run end to end through the activation channel the shell already forwards.
//
// Flow:
//  1. AuthenticateAsync validates the options like the platform implementations (null /
//     non-absolute arguments), claims the process-wide single active request
//     (InvalidOperationException on a second one) and opens options.Url with the external system
//     handler through the same ability bridge the Launcher/Browser use
//     (OpenHarmonyAbilityBridge.TryOpenUri -> an implicit ohos.want.action.viewData want). The
//     hand-off runs on a pool thread, never on the activation callback thread: that thread is
//     the shell's JS thread, which the startAbility round trip would have to wait on (it is
//     still inside the notifyActivation call), so a synchronous launch there can only time out.
//  2. The browser's redirect to the callback scheme comes back as a new want: the ability's
//     onNewWant (or the cold-start onCreate) hands the want uri to the host
//     (host.notifyActivation), OpenHarmonyBridge.Activation delivers it here, and this class
//     matches the route against options.CallbackUrl (scheme/host ignore case, the effective port
//     and a non-root path must match - the same rules as the MAUI request manager) and parses
//     the result through WebAuthenticatorResult, which consumes the query string AND the
//     fragment and honours options.ResponseDecoder. A duplicate or non-matching delivery is
//     ignored, never a second completion.
//  3. Cancellation is the caller's token (MAUI defines no built-in timeout; a wait limit is a
//     CancellationTokenSource passed by the app) and surfaces as the canceled await. A launch
//     failure is the platform contract's InvalidOperationException; a missing platform host
//     (desktop, minimal shell) keeps the documented FeatureNotSupportedException degrade.
//
// What the app/HAP must declare (runtime cannot verify it): module.json5 needs a
// browsable/viewData skill carrying the callback scheme and host; the workload packaging
// injects one from OpenHarmonyWebAuthenticatorCallbackUrls through
// OpenHarmonyGenerateModuleJson (Callbacks), so the system can dispatch the redirect back to
// the app. The shell side needs nothing new: the shipped EntryAbility template already captures
// onCreate/onNewWant wants and forwards them (P2c).
//
// Not representable here (noted once per process instead of failing the flow):
//  - form_post responses: a POST to the redirect route has no want carrier; only a URI redirect
//    (query or fragment) can reach the app.
//  - PrefersEphemeralWebBrowserSession: startAbility opens the regular external browser and has
//    no ephemeral-session knob.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// WebAuthenticator for OpenHarmony. Starts the flow in the external system browser through the
/// ability bridge and completes when the shell forwards the redirect want (callback scheme,
/// host and path matched against <see cref="WebAuthenticatorOptions.CallbackUrl"/>), parsing the
/// callback URI into a <see cref="WebAuthenticatorResult"/> with the MAUI semantics. Installed as
/// the Essentials <see cref="WebAuthenticator.Default"/> and registered in DI by
/// <see cref="MauiOpenHarmonyExtensions.UseOpenHarmony"/>.
/// </summary>
public sealed class OpenHarmonyWebAuthenticator : IWebAuthenticator
{
    /// <summary>The singleton installed as <see cref="WebAuthenticator.Default"/> and in DI.</summary>
    public static readonly OpenHarmonyWebAuthenticator Instance = new();

    /// <summary>
    /// Diagnoses the degrade when no platform host is present: the flow needs the HAP's callback
    /// skill (injected from the packaging's OpenHarmonyWebAuthenticatorCallbackUrls) and the
    /// shell's want forwarding, plus the native host the browser hand-off goes through.
    /// </summary>
    internal const string UnsupportedMessage =
        "WebAuthenticator cannot run on this platform host: the browser hand-off goes through " +
        "OpenHarmonyAbilityBridge (libopenharmonyhost.so + the shell's startAbility sink), which is " +
        "not available here. On a device the HAP must declare the callback route " +
        "(module.json5 abilities[].skills[].uris with the callback scheme/host; the packaging injects " +
        "it from OpenHarmonyWebAuthenticatorCallbackUrls) and the shipped shell forwards the redirect " +
        "want (onCreate/onNewWant -> host.notifyActivation) to reach AuthenticateAsync.";

    /// <summary>
    /// Test seam replacing the platform browser hand-off: returns whether the authorize URL was
    /// dispatched. Null (the shipped default) asks the ability bridge; the suite sets it so the
    /// awaiting flow can be driven off-device. Always restored by the test after use.
    /// </summary>
    internal static Func<Uri, bool>? OpenBrowserOverride { get; set; }

    private static readonly object s_sync = new();
    private static Request? s_active;

    // One cached static delegate: subscribing and unsubscribing the same instance keeps the
    // per-request add/remove balanced (a fresh method-group conversion would leak a handler).
    private static readonly Action<OpenHarmonyActivationEventArgs> s_onActivation = OnActivation;

    private OpenHarmonyWebAuthenticator()
    {
    }

    /// <summary>
    /// Starts the flow for <paramref name="webAuthenticatorOptions"/> (see the type remarks) and
    /// completes with the callback's parsed result.
    /// </summary>
    public Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions webAuthenticatorOptions)
        => AuthenticateAsync(webAuthenticatorOptions, CancellationToken.None);

    /// <inheritdoc cref="AuthenticateAsync(WebAuthenticatorOptions)"/>
    public async Task<WebAuthenticatorResult> AuthenticateAsync(
        WebAuthenticatorOptions webAuthenticatorOptions,
        CancellationToken cancellationToken)
    {
        if (webAuthenticatorOptions is null)
        {
            throw new ArgumentNullException(nameof(webAuthenticatorOptions));
        }
        Uri? url = webAuthenticatorOptions.Url;
        Uri? callbackUrl = webAuthenticatorOptions.CallbackUrl;
        if (url is null)
        {
            throw new ArgumentNullException(nameof(webAuthenticatorOptions.Url));
        }
        if (callbackUrl is null)
        {
            throw new ArgumentNullException(nameof(webAuthenticatorOptions.CallbackUrl));
        }
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("The authentication URL must be absolute.", nameof(webAuthenticatorOptions.Url));
        }
        if (!callbackUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The callback URL must be absolute.", nameof(webAuthenticatorOptions.CallbackUrl));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (webAuthenticatorOptions.PrefersEphemeralWebBrowserSession)
        {
            OpenHarmonyWebAuthenticatorLog.EphemeralOnce();
        }

        var request = new Request(url, callbackUrl, webAuthenticatorOptions.ResponseDecoder, cancellationToken);
        bool subscribed = false;
        CancellationTokenRegistration registration = default;
        try
        {
            lock (s_sync)
            {
                if (s_active is not null)
                {
                    // The process-wide contract: one authentication operation at a time.
                    throw new InvalidOperationException("Another WebAuthenticator operation is already in progress.");
                }
                s_active = request;
                // Subscribe before the browser opens so the redirect cannot arrive before the
                // listener exists (the bridge replays activations that reached it earlier).
                OpenHarmonyBridge.Activation += s_onActivation;
                subscribed = true;
            }
            registration = cancellationToken.Register(
                static state =>
                {
                    var pending = (Request)state!;
                    pending.TrySetCanceled(pending.CancellationToken);
                },
                request);

            // The browser hand-off must not run inside an activation dispatch: the shell forwards
            // a want to the managed side through a synchronous JS -> managed callback, so a
            // startAbility round trip from this thread would wait on the JS thread that is still
            // inside the callback (device-observed: the ability probe timed out after ~5 s and the
            // flow degraded, because AuthenticateAsync is normally called from the activation
            // handler). Dispatch it on a pool thread; the request stays pending until the
            // callback, the launch failure or the caller's cancellation completes it.
            _ = Task.Run(() =>
            {
                try
                {
                    if (!TryOpenBrowser(url))
                    {
                        // The platform contract for a failed browser launch (Android/Windows alike).
                        request.TrySetException(
                            new InvalidOperationException("Failed to launch the browser for authentication."));
                        OpenHarmonyWebAuthenticatorLog.LaunchFailedOnce(callbackUrl);
                    }
                }
                catch (Exception ex)
                {
                    // A platform-less host keeps the FeatureNotSupportedException degrade; the
                    // exception surfaces from the awaited task exactly like a synchronous one.
                    request.TrySetException(ex);
                }
            });

            return await request.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
            lock (s_sync)
            {
                if (subscribed)
                {
                    OpenHarmonyBridge.Activation -= s_onActivation;
                }
                if (ReferenceEquals(s_active, request))
                {
                    s_active = null;
                }
            }
        }
    }

    /// <summary>
    /// Dispatches the authorize URL: the test seam when set, otherwise the platform bridge. The
    /// capability probe keeps the documented FeatureNotSupportedException degrade on a host that
    /// cannot run the flow at all.
    /// </summary>
    private static bool TryOpenBrowser(Uri url)
    {
        if (OpenBrowserOverride is { } openBrowser)
        {
            return openBrowser(url);
        }
        if (!OpenHarmonyAbilityBridge.IsAvailable)
        {
            throw new FeatureNotSupportedException(UnsupportedMessage);
        }
        return OpenHarmonyAbilityBridge.TryOpenUri(url.AbsoluteUri);
    }

    /// <summary>
    /// The bridge activation hook: completes the active request when the delivered want uri
    /// matches its callback route. Runs on the shell callback thread, so nothing may escape into
    /// the native frame; the route match and parse failures are silent/no-op by design.
    /// </summary>
    private static void OnActivation(OpenHarmonyActivationEventArgs activation)
    {
        try
        {
            Request? request;
            lock (s_sync)
            {
                request = s_active;
            }
            if (request is null || request.CompletionClaimed || string.IsNullOrEmpty(activation.Uri))
            {
                return;
            }
            if (!Uri.TryCreate(activation.Uri, UriKind.Absolute, out Uri? callbackUri) ||
                !CanHandleCallback(request.CallbackUrl, callbackUri))
            {
                return;
            }
            request.CompletionClaimed = true;
            try
            {
                request.TrySetResult(new WebAuthenticatorResult(callbackUri, request.ResponseDecoder));
            }
            catch (Exception ex)
            {
                // A throwing IWebAuthenticatorResponseDecoder is delivered to the caller.
                request.TrySetException(ex);
            }
        }
        catch (Exception)
        {
            // Never throw across the native activation callback boundary.
        }
    }

    /// <summary>
    /// The callback route match, byte-for-byte the MAUI request manager rules (do not diverge):
    /// scheme and host ignore case, the effective port participates when the expected host is
    /// non-empty, and an expected non-root path must match exactly; query/fragment never do.
    /// </summary>
    internal static bool CanHandleCallback(Uri expectedUrl, Uri callbackUrl)
    {
        if (!expectedUrl.IsAbsoluteUri || !callbackUrl.IsAbsoluteUri)
        {
            return false;
        }
        if (!callbackUrl.Scheme.Equals(expectedUrl.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.IsNullOrEmpty(expectedUrl.Host))
        {
            if (!callbackUrl.Host.Equals(expectedUrl.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (callbackUrl.Port != expectedUrl.Port)
            {
                return false;
            }
        }
        if (!string.IsNullOrEmpty(expectedUrl.AbsolutePath) && expectedUrl.AbsolutePath != "/" &&
            !callbackUrl.AbsolutePath.Equals(expectedUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    /// <summary>Installs this implementation as the MAUI Essentials WebAuthenticator default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            // The entry points (WebAuthenticator.Default / Current) are get-only, so the backing
            // field (WebAuthenticator.defaultImplementation) is the settable surface, the same
            // pattern as the sensors/haptics/battery/TextToSpeech/app-launch installers.
            foreach (FieldInfo field in typeof(WebAuthenticator).GetFields(flags))
            {
                if (field.FieldType.IsInstanceOfType(Instance))
                {
                    field.SetValue(null, Instance);
                }
            }
        }
        catch (Exception)
        {
            // The default stays in place when the entry point cannot be replaced.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => InstallDefault();

    /// <summary>One in-flight authentication request (the single process-wide active one).</summary>
    private sealed class Request
    {
        private readonly TaskCompletionSource<WebAuthenticatorResult> s_source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Request(
            Uri url, Uri callbackUrl, IWebAuthenticatorResponseDecoder? responseDecoder, CancellationToken cancellationToken)
        {
            Url = url;
            CallbackUrl = callbackUrl;
            ResponseDecoder = responseDecoder;
            CancellationToken = cancellationToken;
        }

        public Uri Url { get; }

        public Uri CallbackUrl { get; }

        public IWebAuthenticatorResponseDecoder? ResponseDecoder { get; }

        public CancellationToken CancellationToken { get; }

        /// <summary>Set once a matching delivery claimed the completion (duplicates are dropped).</summary>
        public bool CompletionClaimed { get; set; }

        public Task<WebAuthenticatorResult> Task => s_source.Task;

        public bool TrySetResult(WebAuthenticatorResult result) => s_source.TrySetResult(result);

        public bool TrySetCanceled(CancellationToken cancellationToken) => s_source.TrySetCanceled(cancellationToken);

        public bool TrySetException(Exception exception) => s_source.TrySetException(exception);
    }
}

/// <summary>
/// One-time status notes for the WebAuthenticator flow. dotnet-status.txt is bounded, so each
/// unrepresentable part is named once per process instead of per attempt (the same pattern the
/// ability bridge's dropped-title note uses).
/// </summary>
internal static class OpenHarmonyWebAuthenticatorLog
{
    private static bool s_launchFailed;
    private static bool s_ephemeral;

    /// <summary>
    /// The browser hand-off did not dispatch: name the callback route and the manifest/shell
    /// requirements so a device run can tell a missing declaration from other failures.
    /// </summary>
    public static void LaunchFailedOnce(Uri? callbackUrl)
    {
        if (s_launchFailed)
        {
            return;
        }
        s_launchFailed = true;
        string callback = callbackUrl is null
            ? "<no callback url>"
            : callbackUrl.Scheme + "://" + callbackUrl.Host;
        OpenHarmonyBridge.WriteStatus(
            $"[maui] webauthenticator could not open the browser for callback '{callback}'; " +
            "the HAP needs the callback route declared (module.json5 abilities[].skills[].uris, injected " +
            "from OpenHarmonyWebAuthenticatorCallbackUrls) and the shell forwards the redirect want " +
            "(onCreate/onNewWant -> host.notifyActivation)");
    }

    /// <summary>The ephemeral-session request has no startAbility representation.</summary>
    public static void EphemeralOnce()
    {
        if (s_ephemeral)
        {
            return;
        }
        s_ephemeral = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] webauthenticator: PrefersEphemeralWebBrowserSession was not applied (startAbility opens " +
            "the regular external browser; the platform has no ephemeral-session knob)");
    }
}
