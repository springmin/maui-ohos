// WebView handler for OpenHarmony: the ArkTS shell owns a hidden ArkWeb component; this handler
// shows it and drives it through OpenHarmonyBridge.WebCommand, and mirrors page events back.
// JavaScript: scripts run on the same ArkWeb component through the host's ohos_host_web_eval /
// notifyWebEvalResult pair, and page scripts reach managed code through the shell's dotnetHost
// proxy (window.__ohosDotNet), which raises JsMessage.
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>Shell web-event state carrying ArkWeb history availability ("history|b|f").</summary>
    private const string HistoryStatePrefix = "history|";

    /// <summary>Longest URL the shell may hand over for a decision (bounds the copy).</summary>
    private const int MaxNavUrlLength = 8 * 1024;

    /// <summary>Longest Set-Cookie value the cookie surface accepts (the shell bounds it too).</summary>
    private const int MaxCookieLength = 8 * 1024;

    /// <summary>Longest URL a status line may carry (B7).</summary>
    internal const int MaxLoggedUrlLength = 2048;

    /// <summary>
    /// Origin a Blazor WebAssembly site registered through <see cref="RegisterWasmSite"/> is
    /// served from. It is deliberately not the BlazorWebView app origin
    /// (<c>https://0.0.0.0/</c>): a WASM site is a self-contained static site with its own
    /// bootstrap (<c>_framework/blazor.webassembly*.js</c> plus <c>dotnet.js</c> /
    /// <c>dotnet.native.wasm</c>), so the shell must not inject the Blazor Hybrid bootstrap
    /// into it. The shell intercepts this origin only while a site is registered.
    /// </summary>
    public const string WasmSiteOrigin = "https://blazorwasm.local/";

    /// <summary>
    /// Default payload directory a staged Blazor WebAssembly site lives in (the pack target
    /// <c>_OpenHarmonyStageWasmSite</c> stages <c>OpenHarmonyWasmSiteDir</c> there).
    /// </summary>
    public const string WasmSiteRoot = "wasmsite";

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

    /// <summary>
    /// The <see cref="WebNavigationEvent"/> of the load the shell is about to start: set by
    /// GoBack/GoForward/Reload and consumed by the next shell page event so Navigating and
    /// Navigated report Back/Forward/Refresh instead of NewPage. Reset by a new source load and
    /// by every completion/failure event.
    /// </summary>
    private static WebNavigationEvent s_pendingNavigation = WebNavigationEvent.NewPage;

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
            [nameof(IWebView.Cookies)] = MapCookies,
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
        // The native Web component is a shell overlay: place it on the control's frame (the
        // command also shows it). The values are MAUI DIP, which the shell applies as ArkUI vp.
        SendPlatformFrame(frame);
    }

    /// <summary>
    /// Sends the shell overlay frame to the ArkWeb component ("frame", arg "x\ny\nw\nh" in
    /// MAUI DIP applied as ArkUI vp); a zero width/height keeps that dimension full-window.
    /// Shared by the WebView, HybridWebView and BlazorWebView handlers, which all render into
    /// the same shell overlay.
    /// </summary>
    internal static void SendPlatformFrame(Rect frame)
        => OpenHarmonyBridge.WebCommand("frame", FormattableString.Invariant(
            $"{frame.X:0.###}\n{frame.Y:0.###}\n{frame.Width:0.###}\n{frame.Height:0.###}"));

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
            case nameof(IWebView.GoBack):
                SendHistoryCommand("back", WebNavigationEvent.Back);
                return;
            case nameof(IWebView.GoForward):
                SendHistoryCommand("forward", WebNavigationEvent.Forward);
                return;
            case nameof(IWebView.Reload):
                SendHistoryCommand("refresh", WebNavigationEvent.Refresh);
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    /// <summary>
    /// Sends an ArkWeb history command (back/forward/refresh) and remembers the matching
    /// WebNavigationEvent for the next page event. The shell reports the resulting history
    /// availability afterwards, so the command itself needs no reply.
    /// </summary>
    private static void SendHistoryCommand(string op, WebNavigationEvent navigationEvent)
    {
        s_pendingNavigation = navigationEvent;
        OpenHarmonyBridge.WebCommand(op);
    }

    /// <summary>Evaluates a script on the shell's ArkWeb page; null when no page or host answers.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script) => EvaluateJavaScriptAsyncCore(script);

    /// <summary>
    /// Sends the script to the ArkTS shell (registerWebEvalSink) and awaits the runJavaScript
    /// result by request id. A missing host library or sink answers null instead of throwing.
    /// </summary>
    internal static async Task<string?> EvaluateJavaScriptAsyncCore(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return null;
        }
        return await SendHostRequestAsync(requestId => WebEvalNative(script, requestId) == 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one host request through the shared request/result table the shell answers with
    /// <c>host.notifyWebEvalResult</c> (scripts and cookie reads use the same channel): registers
    /// the awaiting entry, sends with <paramref name="send"/> (given the request id) and waits up
    /// to <see cref="s_evalTimeout"/>. A missing host library degrades to null before anything is
    /// sent; a timeout answers null and removes the entry. Never throws.
    /// </summary>
    private static async Task<string?> SendHostRequestAsync(Func<int, bool> send)
    {
        if (s_evalUnavailable)
        {
            return null;
        }
        try
        {
            EnsureEvalRegistered();
            if (s_evalUnavailable)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_evalUnavailable = true;
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextRequestId);
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_evalRequests[requestId] = source;
        try
        {
            if (!send(requestId))
            {
                s_evalRequests.TryRemove(requestId, out _);
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_evalUnavailable = true;
            s_evalRequests.TryRemove(requestId, out _);
            return null;
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_evalTimeout)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_evalRequests.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] web request timed out");
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

    /// <summary>
    /// Minimal cookie surface: sets an HTTP cookie for an absolute http(s) URL through the
    /// shell's ArkWeb <c>WebCookieManager.configCookieSync</c>. The value is the Set-Cookie
    /// header form ("name=value; path=/; ..."). An unsafe URL, an empty value or a value above
    /// <see cref="MaxCookieLength"/> is dropped; without a host library the command is a no-op.
    /// </summary>
    public static void SetCookie(string url, string cookie) => TrySetCookie(url, cookie);

    /// <summary>
    /// Validating form of <see cref="SetCookie"/> (shared with the CookieContainer sync): true
    /// when the command reached the shell bridge, false when the pair was rejected.
    /// </summary>
    internal static bool TrySetCookie(string url, string cookie)
    {
        if (!IsCookieUrl(url) || string.IsNullOrEmpty(cookie) ||
            cookie.Length > MaxCookieLength || ContainsControlCharacter(url) || ContainsControlCharacter(cookie))
        {
            return false;
        }
        OpenHarmonyBridge.WebCommand("cookie", url + "\n" + cookie);
        return true;
    }

    /// <summary>
    /// Minimal cookie surface: reads the cookies the ArkWeb cookie store has for an absolute
    /// http(s) URL (the shell's <c>WebCookieManager.fetchCookieSync</c>). Answers null for an
    /// unsafe URL, when no page/host answers within the script timeout, or without a host
    /// library. The read rides the same request/result channel as the JavaScript evaluation.
    /// </summary>
    public static async Task<string?> GetCookieAsync(string url)
    {
        if (!IsCookieUrl(url) || ContainsControlCharacter(url))
        {
            return null;
        }
        return await SendHostRequestAsync(requestId =>
        {
            OpenHarmonyBridge.WebCommand("cookieGet", requestId + "\n" + url);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>True when the cookie surface accepts the URL (absolute http(s) with a host).</summary>
    internal static bool IsCookieUrl(string url)
        => !string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out Uri? target)
            && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(target.Host);

    /// <summary>Upper bound on one CookieContainer sync pass (the same bound the shell keeps).</summary>
    internal const int MaxSyncedCookies = 64;

    /// <summary>
    /// Cross-platform CookieContainer sync (IWebView.Cookies, the property mapper):
    /// container -> ArkWeb. Every cookie is written through the shell's configCookieSync with a
    /// URL rebuilt from the cookie's domain/scheme/path, so the platform store sees the same
    /// cookies the app set. Answers the number of cookies the bridge accepted (unsafe domains,
    /// nameless cookies and anything past <see cref="MaxSyncedCookies"/> are skipped).
    /// </summary>
    internal static int SyncContainerToPlatform(IWebView? webView)
    {
        if (webView?.Cookies is not CookieContainer container)
        {
            return 0;
        }
        int synced = 0;
        foreach (Cookie cookie in container.GetAllCookies())
        {
            if (synced >= MaxSyncedCookies)
            {
                break;
            }
            string? url = CookieUrl(cookie);
            if (url is null || string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Value))
            {
                continue;
            }
            if (TrySetCookie(url, CookieHeaderValue(cookie)))
            {
                synced++;
            }
        }
        return synced;
    }

    /// <summary>
    /// The URL the cookie is set for: the scheme follows Secure, the host drops the domain
    /// cookie's leading dot (host-only cookies carry the host there too), the path defaults
    /// to "/". Null for a cookie without a usable domain/host.
    /// </summary>
    private static string? CookieUrl(Cookie cookie)
    {
        string domain = cookie.Domain;
        if (string.IsNullOrEmpty(domain))
        {
            return null;
        }
        string host = domain[0] == '.' ? domain[1..] : domain;
        if (host.Length == 0)
        {
            return null;
        }
        string path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
        if (path[0] != '/')
        {
            path = "/" + path;
        }
        return (cookie.Secure ? Uri.UriSchemeHttps : Uri.UriSchemeHttp) + "://" + host + path;
    }

    /// <summary>The Set-Cookie value for one container cookie (name=value plus attributes).</summary>
    private static string CookieHeaderValue(Cookie cookie)
    {
        var builder = new System.Text.StringBuilder(cookie.Name.Length + cookie.Value.Length + 40);
        builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        builder.Append("; path=").Append(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path);
        if (cookie.Secure)
        {
            builder.Append("; secure");
        }
        if (cookie.HttpOnly)
        {
            builder.Append("; httponly");
        }
        if (cookie.Expires != DateTime.MinValue)
        {
            builder.Append("; expires=")
                .Append(cookie.Expires.ToUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Cross-platform CookieContainer sync (the page-finished event):
    /// ArkWeb -> container. The shell's fetchCookieSync answer rides the eval-result channel,
    /// then <see cref="MergeCookieHeader"/> parses the "name=value; ..." header into every
    /// connected handler's container. Best-effort: a missing host, a timeout or a header the
    /// container rejects leaves the container as it was.
    /// </summary>
    private static void ScheduleCookieRead(string url)
    {
        if (!IsCookieUrl(url) || ContainsControlCharacter(url))
        {
            return;
        }
        _ = ReadCookiesFromPlatformAsync(url);
    }

    private static async Task ReadCookiesFromPlatformAsync(string url)
    {
        try
        {
            string? header = await GetCookieAsync(url).ConfigureAwait(false);
            if (string.IsNullOrEmpty(header))
            {
                return;
            }
            lock (s_handlers)
            {
                foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
                {
                    MergeCookieHeader(handler.VirtualView, url, header);
                }
            }
        }
        catch (Exception ex)
        {
            // The sync is opportunistic: a failure must never take the page or app down.
            OpenHarmonyBridge.WriteStatus($"[maui] web cookie read failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Parses one shell cookie header ("name=value; name2=value2", the Cookie-header shape
    /// <c>fetchCookieSync</c> answers) into the webView's CookieContainer for
    /// <paramref name="url"/>. Each pair is added on its own so one malformed pair cannot drop
    /// the rest; false when nothing was added (missing container, unsafe URL, empty/oversized/
    /// control-carrying header, or a header the container rejects).
    /// </summary>
    internal static bool MergeCookieHeader(IWebView? webView, string url, string? header)
    {
        if (webView?.Cookies is not CookieContainer container || string.IsNullOrEmpty(header) ||
            header.Length > MaxCookieLength || !IsCookieUrl(url) || ContainsControlCharacter(header))
        {
            return false;
        }
        var target = new Uri(url, UriKind.Absolute);
        bool added = false;
        foreach (string rawPair in header.Split(';'))
        {
            string pair = rawPair.Trim();
            int separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            try
            {
                container.SetCookies(target, pair);
                added = true;
            }
            catch (CookieException)
            {
                // A malformed pair is skipped; the remaining cookies still land.
            }
        }
        if (!added)
        {
            OpenHarmonyStatus.Once("web.cookie.merge",
                "[maui] web cookie header rejected by the CookieContainer");
        }
        return added;
    }

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
        => RaiseNavigating(url, WebNavigationEvent.NewPage);

    /// <summary>Raises Navigating with the load's event kind (Back/Forward/Refresh for history loads).</summary>
    private static bool RaiseNavigating(string url, WebNavigationEvent navigationEvent)
    {
        bool allowed = true;
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (handler.VirtualView is { } webView && webView.Navigating(navigationEvent, url))
                {
                    allowed = false;
                }
            }
        }
        return allowed;
    }

    /// <summary>
    /// Mirrors a completed/failed shell load into IWebView.Navigated with the event kind the
    /// load was started with (Back/Forward/Refresh for history loads, NewPage otherwise) and
    /// consumes the pending kind.
    /// </summary>
    private static void RaiseNavigated(string url, WebNavigationResult result)
    {
        WebNavigationEvent navigationEvent = s_pendingNavigation;
        s_pendingNavigation = WebNavigationEvent.NewPage;
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                handler.VirtualView?.Navigated(navigationEvent, url, result);
            }
        }
    }

    /// <summary>
    /// Applies the shell's history state ("history|&lt;back&gt;|&lt;forward&gt;", 1/0 flags) to
    /// every connected WebView's IWebView.CanGoBack/CanGoForward.
    /// </summary>
    private static void ApplyHistoryState(string state)
    {
        string[] parts = state.Split('|');
        if (parts.Length != 3 || !TryParseFlag(parts[1], out bool canGoBack) || !TryParseFlag(parts[2], out bool canGoForward))
        {
            return;
        }
        s_pendingNavigation = WebNavigationEvent.NewPage;
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (handler.VirtualView is { } webView)
                {
                    webView.CanGoBack = canGoBack;
                    webView.CanGoForward = canGoForward;
                }
            }
        }
    }

    private static bool TryParseFlag(string value, out bool flag)
    {
        flag = value == "1";
        return value is "0" or "1";
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

    /// <summary>
    /// IWebView.Cookies mapper: pushes the app's CookieContainer into the ArkWeb cookie store.
    /// The container is mutable without a property change, so assigning it (or calling
    /// UpdateValue("Cookies") after adding cookies) is the sync point; the page-finished event
    /// mirrors the store back. The count is logged once so a huge container cannot spam.
    /// </summary>
    public static void MapCookies(OpenHarmonyWebViewHandler handler, IWebView webView)
    {
        int synced = SyncContainerToPlatform(webView);
        if (synced > 0)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] web cookies synced to platform: {synced}");
        }
    }

    /// <summary>
    /// Registers a Blazor WebAssembly site root with the ArkTS shell (the "blazor" web command
    /// with mode "wasm"): requests on <see cref="WasmSiteOrigin"/> are then served from
    /// <c>&lt;AppDir&gt;/&lt;contentRoot&gt;</c> with the normal payload MIME types. The pack
    /// target <c>_OpenHarmonyStageWasmSite</c> (property <c>OpenHarmonyWasmSiteDir</c>) stages a
    /// published <c>wwwroot</c> into the payload's <see cref="WasmSiteRoot"/> directory, so an
    /// app hosts a WASM site by registering that root once and pointing a WebView at
    /// <see cref="WasmSiteOrigin"/>; the site's own bootstrap boots the runtime (no Blazor
    /// Hybrid bootstrap is injected). A registration is not a load: the WebView's Source drives
    /// the load, so the page is fetched exactly once.
    /// </summary>
    /// <param name="contentRoot">
    /// Payload-relative site root (default <see cref="WasmSiteRoot"/>). Rooted paths, '\'
    /// separators and "." / ".." segments are refused, exactly like the BlazorWebView
    /// registration, so the shell can never be pointed outside the payload.
    /// </param>
    /// <returns>
    /// True when the registration reached the shell bridge; false without an app directory
    /// (off-device) or for an unsafe root, leaving no arming behind.
    /// </returns>
    public static bool RegisterWasmSite(string? contentRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(contentRoot) ? WasmSiteRoot : contentRoot.Trim();
        if (root.Length == 0 || root[0] is '/' or '\\')
        {
            // The shell's own layout guard refuses these too (isSafeLayoutPart); fail here so
            // a rejected root never arms a half-registration.
            OpenHarmonyBridge.WriteStatus("[maui] wasm site registration rejected: unsafe root");
            return false;
        }
        OpenHarmonyAppContext? context = OpenHarmonyBridge.Context;
        string appDir = context?.AppDir?.TrimEnd('/') ?? string.Empty;
        if (appDir.Length == 0 || OpenHarmonyBlazorWebView.ResolveContentRoot(appDir, root) is null)
        {
            return false;
        }
        OpenHarmonyBridge.WebCommand("blazor", JsonSerializer.Serialize(new WasmSiteConfig
        {
            Origin = WasmSiteOrigin,
            Base = appDir,
            ContentRoot = root,
            HostFile = OpenHarmonyBlazorWebView.DefaultHostFile,
        }, OpenHarmonySliceJsonContext.Default.WasmSiteConfig));
        return true;
    }

    public static void MapSource(OpenHarmonyWebViewHandler handler, IWebView webView)
    {
        // A source load is a fresh navigation, not a history move.
        s_pendingNavigation = WebNavigationEvent.NewPage;
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
            // do not raise it a second time. App-origin loads never take that path. The event
            // kind is the one the triggering command set (Back/Forward/Refresh or NewPage).
            if (ConsumeApprovedNavigation(url))
            {
                return;
            }
            RaiseNavigating(url, s_pendingNavigation);
            return;
        }
        if (state.StartsWith(HistoryStatePrefix, StringComparison.Ordinal))
        {
            // ArkWeb history availability after a page end/back/forward/refresh; mirrors into
            // IWebView.CanGoBack/CanGoForward and consumes the pending history kind.
            ApplyHistoryState(state);
            return;
        }
        if (state == "error")
        {
            // A failed main-frame load: clear the overlay (the managed surface shows through)
            // and report the failure through IWebView.Navigated. The shell reports this outside
            // the page's control, so a script cannot turn a failure into a success.
            OpenHarmonyBridge.WebCommand("hide");
            RaiseNavigated(url, WebNavigationResult.Failure);
        }
        else if (state == "finished")
        {
            RaiseNavigated(url, WebNavigationResult.Success);
            // The page is done: mirror the ArkWeb cookie store back into IWebView.Cookies
            // (best-effort; the container stays authoritative for what the app set).
            ScheduleCookieRead(url);
        }
        // Any completion/failure event is the timely-cleanup point for approval entries whose
        // reload never started (MB-1), so they do not linger until the page-driven cap evicts
        // them. The URL is stripped of its query/fragment and truncated (B7) before it reaches
        // the status file.
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

    /// <summary>
    /// Payload descriptor the shell consumes from the "blazor" web command for a Blazor
    /// WebAssembly site (the same shape the BlazorWebView handler sends, plus the explicit
    /// mode). The shell parses it with the same reader and only skips the Blazor Hybrid
    /// bootstrap when <c>mode</c> is "wasm".
    /// </summary>
    internal sealed class WasmSiteConfig
    {
        [JsonPropertyName("origin")]
        public string Origin { get; init; } = string.Empty;

        [JsonPropertyName("base")]
        public string Base { get; init; } = string.Empty;

        [JsonPropertyName("root")]
        public string ContentRoot { get; init; } = string.Empty;

        [JsonPropertyName("defaultFile")]
        public string HostFile { get; init; } = string.Empty;

        /// <summary>Registration mode; "wasm" keeps the hybrid bootstrap out of the page.</summary>
        [JsonPropertyName("mode")]
        public string Mode { get; init; } = "wasm";
    }
}
