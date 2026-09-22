// WebAuthenticator (MAUI Essentials IWebAuthenticator) for OpenHarmony: documented degrade plus
// the exact platform work a real OAuth redirect flow still needs.
//
// What MAUI rc.1 does without a platform implementation (verified against the shipped
// net11.0 Microsoft.Maui.Essentials.dll, not assumed): WebAuthenticator.Default/Current return
// the internal WebAuthenticatorImplementation, whose AuthenticateAsync throws
// Microsoft.Maui.ApplicationModel.NotImplementedInReferenceAssemblyException synchronously
// ("This functionality is not implemented in the portable version of this assembly..."), an
// exception type that derives from System.NotImplementedException. The static default is
// cached in the WebAuthenticator.defaultImplementation field, so the first auth attempt is
// what fails, not app startup.
//
// Why this slice cannot run the flow today (SDK 26.0.0.18, API 26):
// - The browser hand-off is expressible and already exists: an implicit
//   ohos.want.action.viewData Want carrying the authorize URL
//   (OpenHarmonyAbilityBridge.TryOpenUri -> ohos_host_ability_start, the
//   Launcher/Browser path).
// - The callback half is not. The browser redirect to the callback scheme can only reach the
//   app through an ability skill that matches that scheme
//   (module.json5 abilities[].skills[].uris, read back through BundleManager's
//   Skill/SkillUri: scheme, host, port, path, pathStartWith, pathRegex, type). Skills are
//   static manifest data: SDK 26.0.0.18 has no runtime API that registers a URI scheme, and
//   @ohos.app.ability.wantAgent only creates/compares/triggers/cancels deferred Wants
//   (getWantAgent/trigger/equal/getBundleName/getUid/getOperationType) - it does not register
//   one. The HAP this slice ships in declares only the entity.system.home/action.system.home
//   skill (ohos-workload packs/Microsoft.OpenHarmony.Sdk/.../templates/module.json.template).
// - Even with the skill, the launched UIAbility must hand the incoming want's uri to the
//   managed host. The shipped EntryAbility.ets has no onNewWant handler and never reads
//   want.uri, and Microsoft.OpenHarmony.Hosting exposes no want/URI event, so a redirect URI
//   cannot reach IWebAuthenticator.
//
// AuthenticateAsync therefore fails fast with Microsoft.Maui.ApplicationModel
// .FeatureNotSupportedException (a platform-specific diagnosis, not the reference-assembly
// exception) and writes one [maui] status line naming the missing manifest skill and shell
// forward. WebAuthenticator.Default (via the ModuleInitializer field install) and the
// UseOpenHarmony DI registration both resolve this implementation, so no reference-assembly
// exception can surface for WebAuthenticator. A real flow additionally needs the app-side
// PKCE/state storage (MAUI's contract already leaves that to the app: the redirect query is
// parsed by WebAuthenticatorResult, which this slice reuses unchanged).
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Authentication;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// WebAuthenticator for OpenHarmony. The redirect back from the system browser cannot be
/// delivered yet (no callback ability skill in the HAP and no want forwarding in the shell), so
/// <see cref="AuthenticateAsync(WebAuthenticatorOptions)"/> answers a documented
/// <see cref="Microsoft.Maui.ApplicationModel.FeatureNotSupportedException"/> instead of
/// starting a flow that can never complete. Installed as the Essentials
/// <see cref="WebAuthenticator.Default"/> and registered in DI by
/// <see cref="MauiOpenHarmonyExtensions.UseOpenHarmony"/>.
/// </summary>
public sealed class OpenHarmonyWebAuthenticator : IWebAuthenticator
{
    /// <summary>The singleton installed as <see cref="WebAuthenticator.Default"/> and in DI.</summary>
    public static readonly OpenHarmonyWebAuthenticator Instance = new();

    internal const string UnsupportedMessage =
        "WebAuthenticator cannot complete an authentication flow on OpenHarmony yet: the browser redirect " +
        "cannot come back to the app. The HAP must declare an ability skill for the callback scheme " +
        "(module.json5 abilities[].skills[].uris, e.g. \"uris\": [{ \"scheme\": \"<scheme>\", \"host\": \"<host>\" }]) " +
        "and the ArkTS shell EntryAbility must forward the want it receives after the redirect " +
        "(onNewWant/onCreate want.uri) to the managed host. Neither exists in this slice's shell: SDK 26.0.0.18 " +
        "has no runtime URI-scheme registration API, and @ohos.app.ability.wantAgent only creates, compares and " +
        "triggers deferred Wants. A real flow also needs the app-side PKCE/state storage required by MAUI.";

    private OpenHarmonyWebAuthenticator()
    {
    }

    /// <summary>
    /// Fails the flow with <see cref="Microsoft.Maui.ApplicationModel.FeatureNotSupportedException"/>
    /// and writes one status line; a pre-cancelled token reports cancellation instead.
    /// </summary>
    public Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions webAuthenticatorOptions)
        => AuthenticateAsync(webAuthenticatorOptions, CancellationToken.None);

    /// <inheritdoc cref="AuthenticateAsync(WebAuthenticatorOptions)"/>
    public Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions webAuthenticatorOptions, CancellationToken cancellationToken)
    {
        if (webAuthenticatorOptions is null)
        {
            return Task.FromException<WebAuthenticatorResult>(new ArgumentNullException(nameof(webAuthenticatorOptions)));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            // The async contract's cancellation answer, same as the platform implementations.
            return Task.FromCanceled<WebAuthenticatorResult>(cancellationToken);
        }
        OpenHarmonyWebAuthenticatorLog.UnsupportedOnce(webAuthenticatorOptions.CallbackUrl);
        return Task.FromException<WebAuthenticatorResult>(
            new Microsoft.Maui.ApplicationModel.FeatureNotSupportedException(UnsupportedMessage));
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
}

/// <summary>
/// One-time status note for the unsupported WebAuthenticator. dotnet-status.txt is bounded, so
/// the missing pieces are named once per process instead of per auth attempt (the same pattern
/// the ability bridge's dropped-title note uses).
/// </summary>
internal static class OpenHarmonyWebAuthenticatorLog
{
    private static bool s_unsupportedLogged;

    /// <summary>Writes one [maui] line naming what the callback route still needs.</summary>
    public static void UnsupportedOnce(Uri? callbackUrl)
    {
        if (s_unsupportedLogged)
        {
            return;
        }
        s_unsupportedLogged = true;
        string callback = callbackUrl is null
            ? "<no callback url>"
            : callbackUrl.Scheme + "://" + callbackUrl.Host;
        OpenHarmonyBridge.WriteStatus(
            $"[maui] webauthenticator unsupported: the browser redirect for '{callback}' cannot reach the app - " +
            "the HAP needs an ability skill for the callback scheme (module.json5 abilities[].skills[].uris) and the " +
            "shell EntryAbility needs onNewWant/onCreate want.uri forwarding to the managed host; " +
            "AuthenticateAsync answers FeatureNotSupportedException instead of starting a flow that cannot complete");
    }
}
