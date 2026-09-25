// WebView handler for OpenHarmony: the ArkTS shell owns a hidden ArkWeb component; this handler
// shows it and drives it through OpenHarmonyBridge.WebCommand, and mirrors page events back.
// JavaScript: scripts run on the same ArkWeb component through the host's ohos_host_web_eval /
// notifyWebEvalResult pair, and page scripts reach managed code through the shell's dotnetHost
// proxy (window.__ohosDotNet), which raises JsMessage.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

public sealed partial class OpenHarmonyWebViewHandler : OpenHarmonyViewHandler<IWebView>
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Scripts that a shell sink never answers must not leave callers waiting forever.</summary>
    private static readonly TimeSpan s_evalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Shell envelope asking for a Navigating decision on a cancelled load (B6).</summary>
    private const string NavRequestPrefix = "__OHNAV|";

    /// <summary>Longest URL the shell may hand over for a decision (bounds the copy).</summary>
    private const int MaxNavUrlLength = 8 * 1024;

    /// <summary>Longest URL a status line may carry (B7).</summary>
    internal const int MaxLoggedUrlLength = 2048;

    /// <summary>How long an approval stays valid for its one matching reload.</summary>
    private static readonly TimeSpan s_navApprovalWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Upper bound on the one-shot approval table (MB-1). The shell keeps at most
    /// navPendingLimit=8 decisions and answers each with one reload, so the live set is tiny;
    /// 64 leaves room for a burst that outlives the shell's 5 s pending TTL while capping the
    /// page-supplied keys at 64 * <see cref="MaxNavUrlLength"/> = 512 KiB. A full table first
    /// drops the expired markers and then evicts the entry closest to expiry (the oldest: the
    /// window is constant), so a page looping main-frame navigations cannot grow the table.
    /// </summary>
    private const int MaxApprovedNavigations = 64;

    private static readonly List<OpenHarmonyWebViewHandler> s_handlers = new();
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> s_evalRequests = new();
    private static readonly object s_navSync = new();
    private static readonly Dictionary<string, long> s_approvedNavigations = new();
    private static unsafe IntPtr s_evalResultCallback = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr, int, void>)&OnEvalResultNative;
    private static unsafe IntPtr s_jsMessageCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnJsMessageNative;
    private static bool s_evalRegistered;
    private static bool s_evalUnavailable;
    private static bool s_messageRegistered;
    private static bool s_messageUnavailable;
    private static int s_nextRequestId;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_web_eval", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int WebEvalNative(string script, int requestId);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_web_js_register_result")]
    private static partial void WebRegisterResultNative(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_web_js_register_message")]
    private static partial void WebRegisterMessageNative(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WebEvalResultCallback(int requestId, IntPtr resultUtf8, int error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WebJsMessageCallback(IntPtr payloadUtf8);

    public static readonly IPropertyMapper<IWebView, OpenHarmonyWebViewHandler> Mapper =
        new PropertyMapper<IWebView, OpenHarmonyWebViewHandler>(ViewMapper)
        {
            [nameof(IWebView.Source)] = MapSource,
        };

    public OpenHarmonyWebViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsWebView = true, Background = Colors.White };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        lock (s_handlers)
        {
            s_handlers.Add(this);
        }
        // The managed half of the JavaScript bridge is bound as soon as a page can exist.
        EnsureMessageRegistered();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        lock (s_handlers)
        {
            s_handlers.Remove(this);
        }
        base.DisconnectHandler(platformView);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(400, heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        // The native Web component is a shell overlay, so it only needs to know it is visible.
        OpenHarmonyBridge.WebCommand("show");
    }

    // MAUI raises the JavaScript commands through IElementHandler.Invoke (Controls.WebView
    // wraps the script in try{JSON.stringify(eval(...))}catch(e){'null'} before calling us).
    public override void Invoke(string command, object? args)
    {
        switch (command)
        {
            case nameof(IWebView.EvaluateJavaScriptAsync) when args is EvaluateJavaScriptAsyncRequest request:
                _ = CompleteEvaluateAsync(request);
                return;
            case nameof(IWebView.Eval) when args is string script:
                // Fire-and-forget evaluation (IWebView.Eval has no result).
                _ = EvaluateJavaScriptAsyncCore(script);
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    /// <summary>Evaluates a script on the shell's ArkWeb page; null when no page or host answers.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script) => EvaluateJavaScriptAsyncCore(script);

    /// <summary>
    /// Sends the script to the ArkTS shell (registerWebEvalSink) and awaits the runJavaScript
    /// result by request id. A missing host library or sink answers null instead of throwing.
    /// </summary>
    internal static async Task<string?> EvaluateJavaScriptAsyncCore(string script)
    {
        if (string.IsNullOrEmpty(script) || s_evalUnavailable)
        {
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextRequestId);
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_evalRequests[requestId] = source;
        try
        {
            EnsureEvalRegistered();
            if (s_evalUnavailable || WebEvalNative(script, requestId) != 0)
            {
                s_evalRequests.TryRemove(requestId, out _);
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            s_evalUnavailable = true;
            s_evalRequests.TryRemove(requestId, out _);
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            s_evalUnavailable = true;
            s_evalRequests.TryRemove(requestId, out _);
            return null;
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_evalTimeout)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_evalRequests.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] web eval request timed out");
            return null;
        }
        return await source.Task.ConfigureAwait(false);
    }

    private static async Task CompleteEvaluateAsync(EvaluateJavaScriptAsyncRequest request)
    {
        string? result = await EvaluateJavaScriptAsyncCore(request.Script).ConfigureAwait(false);
        // An empty string stands in for "the platform had no result"; Controls.WebView maps
        // "null" to null and trims the quotes JSON.stringify adds on non-Android platforms.
        request.TrySetResult(result ?? string.Empty);
    }

    /// <summary>True once the host library answered a script evaluation or registration.</summary>
    internal static bool IsJavaScriptBridgeAvailable => !s_evalUnavailable;

    /// <summary>The shell answers a script evaluation (host.notifyWebEvalResult).</summary>
    internal static void CompleteEvalResult(int requestId, string result, bool error)
    {
        if (s_evalRequests.TryRemove(requestId, out TaskCompletionSource<string?>? source))
        {
            source.TrySetResult(error ? null : result);
        }
    }

    /// <summary>
    /// A shell dotnetHost payload: page scripts post through the proxy (host.notifyJsMessage)
    /// and the shell itself uses the same channel for the navigation approval protocol
    /// (B6, "__OHNAV|&lt;url&gt;|&lt;id&gt;"), which is handled here and never fanned out.
    /// The page payload is raised on <see cref="JsMessage"/> and offered to HybridWebView
    /// handlers (see <see cref="OpenHarmonyHybridWebViewHandler.OnJsMessage"/>).
    /// </summary>
    internal static void HandleJsMessage(string payload)
    {
        if (payload.StartsWith(NavRequestPrefix, StringComparison.Ordinal))
        {
            HandleNavigationRequest(payload);
            return;
        }
        JsMessage?.Invoke(payload);
        OpenHarmonyHybridWebViewHandler.OnJsMessage(payload);
    }

    /// <summary>
    /// The shell cancelled a main-frame load it did not originate and asks for a decision (B6).
    /// Parses "__OHNAV|&lt;url&gt;|&lt;id&gt;", raises Navigating on the connected WebViews and,
    /// when none cancelled, approves exactly that URL back to the shell ("nav" command). The
    /// shell reloads only the URL it cancelled for the id it issued, so a forged approval is
    /// inert.
    /// </summary>
    internal static void HandleNavigationRequest(string payload)
    {
        int separator = payload.LastIndexOf('|', StringComparison.Ordinal);
        if (separator <= NavRequestPrefix.Length)
        {
            return;
        }
        string url = payload.Substring(NavRequestPrefix.Length, separator - NavRequestPrefix.Length);
        string requestId = payload.Substring(separator + 1);
        if (requestId.Length == 0 || requestId.Length > 128 || url.Length == 0 || url.Length > MaxNavUrlLength)
        {
            OpenHarmonyBridge.WriteStatus("[maui] web navigation rejected: malformed request");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target) ||
            ContainsControlCharacter(url) || ContainsControlCharacter(requestId))
        {
            OpenHarmonyBridge.WriteStatus("[maui] web navigation rejected: unsafe url");
            return;
        }
        if (!IsApprovableNavigation(target))
        {
            // Fail closed: the shell's decision channel is for external navigations only
            // (H-C2); "//host/x" reaches this branch as a parsed file:// URI.
            OpenHarmonyBridge.WriteStatus("[maui] web navigation rejected: not an http(s) navigation");
            return;
        }
        if (!RaiseNavigating(url))
        {
            // The app cancelled: leave the load blocked (no approval is sent).
            return;
        }
        ApproveNavigation(url);
        NavigationApprovalSent?.Invoke(requestId, url);
        OpenHarmonyBridge.WebCommand("nav", requestId + "\n" + url);
    }

    /// <summary>
    /// True when a URL the shell cancelled may be approved back to it (B6). The shell
    /// short-circuits its own origins, local files, inline documents and relative references,
    /// so only an absolute http/https target with a host is a real external navigation; letting
    /// anything else through would let the approval channel reload a target whose meaning the
    /// URI parser chose (H-C2: "//host/x" parses as file://host/x, not as a relative reference,
    /// so the check must run on the parsed URI and never on the raw string).
    /// </summary>
    internal static bool IsApprovableNavigation(Uri? target)
        => target is { IsAbsoluteUri: true }
           && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(target.Host);

    /// <summary>
    /// True when an app-requested <see cref="UrlWebViewSource"/> may be handed to the shell.
    /// App-internal resources keep working: relative references, the in-page '/'/'#'/'?'
    /// forms and the file:/data:/about:/blob: schemes the shell loads without a decision. An
    /// absolute http(s) target must carry a host. The scheme-relative "//host/x" spellings (also
    /// "/\host" and "\\host") and unknown schemes are refused: the browser resolves them
    /// against the current page (or, for Uri, to file://host), so none of them is an
    /// app-internal asset and a page must not be able to make one load look like one (H-C2).
    /// </summary>
    internal static bool IsLoadableSourceUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || ContainsControlCharacter(url))
        {
            return false;
        }
        if (url[0] is '/' or '#' or '?')
        {
            // "//" and "/\" are network-path references (a foreign host with the current
            // scheme); a plain "/x" or "#fragment" is app-internal.
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }
        if (url[0] == '\\')
        {
            // "\\host\share" is a rooted/UNC spelling, never an app-package asset.
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target))
        {
            // A plain relative reference ("page2.html"): the shell resolves it against the
            // page and its own short-circuit/marker rules decide. A scheme-like prefix
            // ("https:foo") that Uri refuses to parse as absolute is NOT treated as a plain
            // relative reference: a browser resolves special schemes like that to a foreign
            // host ("https://foo/"), which would bypass the http(s)-with-host rule.
            return !HasSchemeLikePrefix(url);
        }
        if (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
        {
            return !string.IsNullOrEmpty(target.Host);
        }
        return target.Scheme switch
        {
            "file" => target.Host.Length == 0,
            "data" or "about" or "blob" => true,
            _ => false,
        };
    }

    /// <summary>
    /// True when the raw text carries an RFC 3986 scheme prefix ("name:"), whether or not Uri
    /// can turn it into an absolute URI. Callers use it to refuse the scheme-like spellings
    /// that Uri cannot parse but a browser resolves against the page (H-C2).
    /// </summary>
    private static bool HasSchemeLikePrefix(string url)
    {
        for (int i = 0; i < url.Length; i++)
        {
            char c = url[i];
            if (c == ':')
            {
                return i > 0;
            }
            if (c is '/' or '?' or '#' or '\\')
            {
                return false;
            }
            if (!(char.IsLetter(c) || (i > 0 && (char.IsDigit(c) || c is '+' or '-' or '.'))))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Records the one-shot approval for <paramref name="url"/> (B6). The table is bounded
    /// (MB-1): expired markers are dropped on every insert, and when the table is still full
    /// the entry closest to expiry (the oldest, the window is constant) is evicted, so a page
    /// looping main-frame navigations cannot grow it. The matching reload still consumes its
    /// entry exactly once, so the approval stays one-shot.
    /// </summary>
    private static void ApproveNavigation(string url)
    {
        long now = Environment.TickCount64;
        long expires = now + (long)s_navApprovalWindow.TotalMilliseconds;
        lock (s_navSync)
        {
            if (s_approvedNavigations.Count >= MaxApprovedNavigations)
            {
                PruneExpiredApprovals(now);
            }
            if (s_approvedNavigations.Count >= MaxApprovedNavigations)
            {
                string? oldest = null;
                long oldestExpiry = long.MaxValue;
                foreach (KeyValuePair<string, long> entry in s_approvedNavigations)
                {
                    if (entry.Value < oldestExpiry)
                    {
                        oldestExpiry = entry.Value;
                        oldest = entry.Key;
                    }
                }
                if (oldest is not null)
                {
                    s_approvedNavigations.Remove(oldest);
                }
            }
            s_approvedNavigations[url] = expires;
        }
    }

    /// <summary>
    /// Drops the approvals whose one matching load never started (the shell dropped its pending
    /// decision, or the reload failed), so an entry lives at most
    /// <see cref="s_navApprovalWindow"/> after its navigation decision. Runs before an insert
    /// once the table is full and on every page event. The caller holds
    /// <see cref="s_navSync"/>.
    /// </summary>
    private static void PruneExpiredApprovals(long now)
    {
        if (s_approvedNavigations.Count == 0)
        {
            return;
        }
        List<string>? expired = null;
        foreach (KeyValuePair<string, long> entry in s_approvedNavigations)
        {
            if (entry.Value < now)
            {
                (expired ??= new List<string>()).Add(entry.Key);
            }
        }
        if (expired is null)
        {
            return;
        }
        foreach (string key in expired)
        {
            s_approvedNavigations.Remove(key);
        }
    }

    /// <summary>
    /// Raises Navigating (NewPage) on every connected WebView; false when any handler set
    /// Cancel (IWebView.Navigating returns the cancel flag).
    /// </summary>
    private static bool RaiseNavigating(string url)
    {
        bool allowed = true;
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (handler.VirtualView is { } webView && webView.Navigating(WebNavigationEvent.NewPage, url))
                {
                    allowed = false;
                }
            }
        }
        return allowed;
    }

    /// <summary>
    /// True when the app already decided this started URL through the shell's approval channel
    /// (B6), so the page-begin event must not raise Navigating a second time for one load.
    /// One-shot: the entry is removed here, and it expires on its own if the load never starts.
    /// </summary>
    private static bool ConsumeApprovedNavigation(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        lock (s_navSync)
        {
            if (!s_approvedNavigations.TryGetValue(url, out long expires))
            {
                return false;
            }
            s_approvedNavigations.Remove(url);
            return expires >= Environment.TickCount64;
        }
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Makes a URL safe for the one-line status log (B7): drops the query and the fragment so
    /// tokens a page put there do not land in dotnet-status.txt, flattens control characters so
    /// one event cannot forge extra lines, and truncates to
    /// <see cref="MaxLoggedUrlLength"/> characters.
    /// </summary>
    internal static string SanitizeUrlForLog(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }
        int cut = url.Length;
        int query = url.IndexOf('?', StringComparison.Ordinal);
        int fragment = url.IndexOf('#', StringComparison.Ordinal);
        if (query >= 0 && query < cut)
        {
            cut = query;
        }
        if (fragment >= 0 && fragment < cut)
        {
            cut = fragment;
        }
        string trimmed = cut == url.Length ? url : url.Substring(0, cut);
        if (trimmed.Length > MaxLoggedUrlLength)
        {
            trimmed = trimmed.Substring(0, MaxLoggedUrlLength - 3) + "...";
        }
        char[]? flattened = null;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsControl(trimmed[i]))
            {
                flattened ??= trimmed.ToCharArray();
                flattened[i] = ' ';
            }
        }
        return flattened is null ? trimmed : new string(flattened);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnEvalResultNative(int requestId, IntPtr resultUtf8, int error)
    {
        string result = resultUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(resultUtf8) ?? string.Empty;
        CompleteEvalResult(requestId, result, error != 0);
    }

    // The shell's JS-message sink (host.notifyJsMessage). This is a reverse P/Invoke entry that
    // fans out to application code (JsMessage subscribers, HybridWebView.RawMessageReceived and
    // the WebView.Navigating raised for a "__OHNAV" decision), so an exception must never unwind
    // into the native frame (MB-2).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnJsMessageNative(IntPtr payloadUtf8)
    {
        try
        {
            string payload = payloadUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
            HandleJsMessage(payload);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("web js message", ex);
        }
    }

    private static void EnsureEvalRegistered()
    {
        if (s_evalRegistered || s_evalUnavailable)
        {
            return;
        }
        try
        {
            WebRegisterResultNative(s_evalResultCallback);
            s_evalRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_evalUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] web eval bridge unavailable (no host library)");
        }
    }

    internal static void EnsureMessageRegistered()
    {
        if (s_messageRegistered || s_messageUnavailable)
        {
            return;
        }
        try
        {
            WebRegisterMessageNative(s_jsMessageCallback);
            s_messageRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_messageUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] web message bridge unavailable (no host library)");
        }
    }

    /// <summary>Raised for every message a page posts through the shell's dotnetHost proxy.</summary>
    public static event Action<string>? JsMessage;

    /// <summary>Raised when a navigation approval is handed back to the shell (B6 diagnostics).</summary>
    internal static event Action<string, string>? NavigationApprovalSent;

    public static void MapSource(OpenHarmonyWebViewHandler handler, IWebView webView)
    {
        switch (webView.Source)
        {
            case UrlWebViewSource url when !string.IsNullOrEmpty(url.Url):
                if (!IsLoadableSourceUrl(url.Url))
                {
                    // The URL is not an app-internal resource and not a real http(s) target
                    // (H-C2): refuse the load instead of letting the shell/browser resolve it.
                    OpenHarmonyBridge.WriteStatus(
                        $"[maui] web load rejected: {SanitizeUrlForLog(url.Url)}");
                    return;
                }
                OpenHarmonyBridge.WebCommand("load", url.Url);
                break;
            case HtmlWebViewSource html:
                OpenHarmonyBridge.WebCommand("data", html.Html);
                break;
            default:
                OpenHarmonyBridge.WebCommand("show");
                break;
        }
    }

    /// <summary>Mirrors a shell page event into the MAUI WebView events.</summary>
    public static void OnPageEvent(string state, string url)
    {
        if (state == "started")
        {
            // A load the shell already asked about (B6) raised Navigating before it started;
            // do not raise it a second time. App-origin loads never take that path.
            if (ConsumeApprovedNavigation(url))
            {
                return;
            }
            RaiseNavigating(url);
            return;
        }
        // Any completion/failure event is the timely-cleanup point for approval entries whose
        // reload never started (MB-1), so they do not linger until the page-driven cap evicts
        // them. IWebView only exposes Navigating, so completion is logged for now; the URL is
        // stripped of its query/fragment and truncated (B7) before it reaches the status file.
        lock (s_navSync)
        {
            PruneExpiredApprovals(Environment.TickCount64);
        }
        string loggedUrl = SanitizeUrlForLog(url);
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (handler.VirtualView is not null)
                {
                    OpenHarmonyBridge.WriteStatus($"[maui] web {state}: {loggedUrl}");
                }
            }
        }
    }
}
