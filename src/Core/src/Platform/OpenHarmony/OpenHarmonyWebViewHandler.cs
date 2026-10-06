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

public sealed partial class OpenHarmonyWebViewHandler : OpenHarmonyViewHandler<IWebView>, IOpenHarmonyOverlaySlotOwner, IOpenHarmonyOverlaySlotLifetime
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

    /// <summary>
    /// Overlay slot this handler claimed while connected (MULTI-OVL); the slot tags every
    /// per-overlay command (frame/load/show/history/eval) and routes the shell's page events
    /// back to this handler. MULTI-OVERLAY-FULL/SLOTS-DYNAMIC makes the claim owner-aware and
    /// dynamic: a web control beyond the hot pair grows the shell's overlay set on demand (up to
    /// the pool's MaxOverlays, default 4), and only a claim at the shell's capacity is served by
    /// an LRU preemption; a preempted handler is restored (with a load replay) the next time it
    /// is used. -1 while suspended between a preemption and the restore.
    /// </summary>
    private int _overlaySlot = -1;

    /// <summary>
    /// MULTIWINDOW-L2: the managed window this control belongs to. The primary id keeps the
    /// historical single-host path; a secondary id ("sub-N") routes every overlay command,
    /// claim and page event through the subwindow's own ArkWeb pool (OpenHarmonyChildWeb).
    /// Resolved at connect time from the host's window table, which is stamped before the
    /// window's tree connects.
    /// </summary>
    private string _overlayWindowId = OpenHarmonyWindowSurface.PrimaryWindowId;

    /// <summary>
    /// MULTIWINDOW-L2: set when the managed window ended (the shell closed the subwindow): a
    /// still-connected handler must not re-claim an overlay for a window that no longer exists.
    /// </summary>
    private bool _overlayWindowClosed;

    /// <summary>True while this handler lost its slot to an LRU preemption (replay on restore).</summary>
    private bool _overlayPreempted;

    /// <summary>
    /// True while this handler was removed from the visual tree (AUTODISCONNECT). The slot was
    /// released (a dynamic slot is destroyed in the shell); the next arrange or re-attach
    /// re-claims a slot and replays the page. Cleared by <see cref="EnsureOverlaySlot"/>.
    /// </summary>
    private bool _overlayDetached;

    /// <summary>True after a frame/load was sent for the current claim (the victim heuristic).</summary>
    private bool _overlayEngaged;

    /// <summary>The claimed overlay slot (MULTI-OVL); -1 while suspended or beyond the cap.</summary>
    internal int OverlaySlot => _overlaySlot;

    /// <summary>MULTIWINDOW-L2: the window this handler's overlay belongs to (the primary id
    /// for the historical single-host path; a "sub-N" id for the subwindow's child pool).</summary>
    internal string OverlayWindowId => _overlayWindowId;
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
        // MULTIWINDOW-L2: the window id decides which ArkWeb host serves this control. The
        // host table is stamped before the window's element tree connects, so a control of the
        // subwindow claims from the child pool and never occupies a primary overlay slot.
        _overlayWindowId = OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView);
        _overlaySlot = OpenHarmonyChildWeb.AcquireForWindow(_overlayWindowId, this);
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
        OpenHarmonyChildWeb.ReleaseForWindow(_overlayWindowId, _overlaySlot, this);
        _overlaySlot = -1;
        _overlayEngaged = false;
        _overlayDetached = false;
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// MULTI-OVERLAY-FULL restore hook: claims a slot when this handler has none (a preemption
    /// or an AUTODISCONNECT detach left it suspended) and reports whether the claim is a restore
    /// that has to replay the load. A live claim is only touched (LRU bookkeeping). Never throws.
    /// </summary>
    private bool EnsureOverlaySlot()
    {
        if (_overlayWindowClosed)
        {
            // MULTIWINDOW-L2: the managed window ended; a still-connected handler must not
            // re-claim an overlay for a window that no longer exists.
            return false;
        }
        if (_overlaySlot >= 0)
        {
            OpenHarmonyChildWeb.TouchForWindow(_overlayWindowId, _overlaySlot, this);
            return false;
        }
        int slot = OpenHarmonyChildWeb.AcquireForWindow(_overlayWindowId, this);
        if (slot < 0)
        {
            return false;
        }
        _overlaySlot = slot;
        bool replay = _overlayPreempted || _overlayDetached;
        _overlayPreempted = false;
        _overlayDetached = false;
        if (replay)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] web overlay restored: slot {slot}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// MULTIWINDOW-L2: a managed window ended (the shell closed the subwindow or its surface
    /// was destroyed) while its element handlers are still connected: release every web claim
    /// that belongs to the window, so no child ArkWeb survives its window. The handlers stay
    /// connected (MAUI semantics); they simply hold no slot afterwards. The primary window is
    /// never touched. Called by <see cref="OpenHarmonyWindowHost.Reset"/>.
    /// </summary>
    internal static void ReleaseWindowOverlays(string windowId)
    {
        if (string.IsNullOrEmpty(windowId) || windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            return;
        }
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (string.Equals(handler._overlayWindowId, windowId, StringComparison.Ordinal) &&
                    handler._overlaySlot >= 0)
                {
                    OpenHarmonyChildWeb.ReleaseForWindow(windowId, handler._overlaySlot, handler);
                    handler._overlaySlot = -1;
                    handler._overlayEngaged = false;
                    handler._overlayWindowClosed = true;
                }
            }
        }
        OpenHarmonyHybridWebViewHandler.ReleaseWindowOverlays(windowId);
        OpenHarmonyBlazorWebViewHandler.ReleaseWindowOverlays(windowId);
    }

    /// <summary>
    /// MULTIWINDOW-L2: re-resolves the handler's window and migrates a claim that was made
    /// before the window id was known (the deferred OpenWindow path stamps the host table just
    /// before the tree connects, and this is the belt: a first message/arrange after a late
    /// stamp moves the claim to the right pool before anything is really shown/loaded).
    /// </summary>
    private void RefreshOverlayWindow()
    {
        if (_overlayWindowClosed)
        {
            return;
        }
        string resolved = OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView);
        if (string.Equals(resolved, _overlayWindowId, StringComparison.Ordinal))
        {
            return;
        }
        OpenHarmonyChildWeb.ReleaseForWindow(_overlayWindowId, _overlaySlot, this);
        _overlaySlot = -1;
        _overlayPreempted = false;
        _overlayDetached = false;
        _overlayWindowId = resolved;
        OpenHarmonyBridge.WriteStatus($"[maui] web overlay window: {resolved}");
    }

    /// <summary>
    /// Replays this control's page on the slot it just re-acquired: the Source load (url/html)
    /// or the show, exactly what MapSource sends on a fresh connect.
    /// </summary>
    private void ReplayOverlay()
    {
        if (VirtualView is null || _overlaySlot < 0)
        {
            return;
        }
        OpenHarmonyBridge.WriteStatus($"[maui] web overlay replay: slot {_overlaySlot}");
        MapSource(this, VirtualView);
    }

    /// <summary>LRU preemption (MULTI-OVERLAY-FULL): drop the claim, stay suspended until reused.</summary>
    void IOpenHarmonyOverlaySlotOwner.OnOverlaySlotPreempted(int slot)
    {
        if (_overlaySlot != slot)
        {
            return;
        }
        _overlaySlot = -1;
        _overlayPreempted = true;
        _overlayEngaged = false;
        OpenHarmonyBridge.WriteStatus($"[maui] web overlay preempted: slot {slot}");
    }

    /// <summary>
    /// AUTODISCONNECT: the control was removed from its layout. Drop the claim (the shell
    /// destroys a dynamic slot), keep the handler connected (MAUI semantics) and mark the
    /// control for a replay so a re-add rebuilds the page. Idempotent.
    /// </summary>
    void IOpenHarmonyOverlaySlotLifetime.OnOverlaySlotDetached()
    {
        _overlayDetached = true;
        if (_overlaySlot < 0)
        {
            return;
        }
        int slot = _overlaySlot;
        _overlaySlot = -1;
        _overlayEngaged = false;
        // Hide before the release: a hot slot keeps its ArkWeb component, and the tagged hide is
        // sent while the slot still exists (after a dynamic destroy it would be deferred and
        // replayed onto the next incarnation of the slot). MULTIWINDOW-L2: both commands follow
        // the handler's window (the child pool destroys every released slot).
        OpenHarmonyChildWeb.CommandForWindow(_overlayWindowId, "hide", OpenHarmonyOverlays.Tag(slot));
        OpenHarmonyChildWeb.ReleaseForWindow(_overlayWindowId, slot, this);
        OpenHarmonyBridge.WriteStatus($"[maui] web overlay detached: slot {slot}");
    }

    /// <summary>
    /// AUTODISCONNECT: the control re-entered the visual tree. A detached/preempted handler
    /// re-claims a slot and replays the page; a live claim is only touched.
    /// </summary>
    void IOpenHarmonyOverlaySlotLifetime.OnOverlaySlotAttached()
    {
        if (_overlaySlot >= 0)
        {
            _overlayDetached = false;
            OpenHarmonyChildWeb.TouchForWindow(_overlayWindowId, _overlaySlot, this);
            return;
        }
        if (!_overlayDetached && !_overlayPreempted)
        {
            return;
        }
        if (EnsureOverlaySlot())
        {
            ReplayOverlay();
        }
        if (_overlaySlot >= 0)
        {
            _overlayEngaged = true;
        }
    }

    /// <summary>True while this control's overlay is showing (the pool preempts idle owners first).</summary>
    bool IOpenHarmonyOverlaySlotOwner.IsOverlaySlotEngaged => _overlaySlot >= 0 && _overlayEngaged;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(400, heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        // MULTIWINDOW-L2 safety net: a window stamp that arrived after the connect-time claim
        // is corrected here, before any frame is sent. Nothing has been shown for a stale
        // claim (the first frame is what mounts the overlay), so the re-claim is free.
        RefreshOverlayWindow();
        // The native Web component is a shell overlay: place it on the control's frame (the
        // command also shows it). The values are MAUI DIP, which the shell applies as ArkUI vp.
        // A handler suspended by an LRU preemption must NOT restore from an arrange (a layout
        // pass runs for every control, so restoring here would let two suspended handlers
        // preempt each other forever); its next explicit use - source/load, eval, history or a
        // new arrange after it re-acquired - drives the restore and its load replay.
        // AUTODISCONNECT: a handler detached from the tree is not arranged while it is gone, so
        // a later arrange means it was re-attached: restore and replay here (the attach signal
        // normally does this already; this is the safety net when no watcher saw the re-add).
        if (_overlayDetached)
        {
            if (EnsureOverlaySlot())
            {
                ReplayOverlay();
            }
        }
        else if (!_overlayPreempted)
        {
            EnsureOverlaySlot();
        }
        if (_overlaySlot >= 0)
        {
            _overlayEngaged = true;
            SendPlatformFrame(_overlayWindowId, frame, _overlaySlot);
        }
    }

    /// <summary>
    /// Sends the shell overlay frame to the ArkWeb component ("frame", arg "x\ny\nw\nh" in
    /// MAUI DIP applied as ArkUI vp); a zero width/height keeps that dimension full-window.
    /// Shared by the WebView, HybridWebView and BlazorWebView handlers, which each own an
    /// overlay slot (MULTI-OVL): the slot is tagged onto the argument, and a legacy untagged
    /// frame lands on the first overlay.
    /// </summary>
    internal static void SendPlatformFrame(Rect frame, int slot)
        => SendPlatformFrame(OpenHarmonyWindowSurface.PrimaryWindowId, frame, slot);

    /// <summary>
    /// MULTIWINDOW-L2 window-aware form: the primary/unknown window keeps the historical
    /// command byte-for-byte; a secondary window's frame goes to the subwindow's own host.
    /// </summary>
    internal static void SendPlatformFrame(string windowId, Rect frame, int slot)
        => OpenHarmonyChildWeb.CommandForWindow(windowId, "frame", OpenHarmonyOverlays.Tag(slot, FormattableString.Invariant(
            $"{frame.X:0.###}\n{frame.Y:0.###}\n{frame.Width:0.###}\n{frame.Height:0.###}")));

    // MAUI raises the JavaScript commands through IElementHandler.Invoke (Controls.WebView
    // wraps the script in try{JSON.stringify(eval(...))}catch(e){'null'} before calling us).
    public override void Invoke(string command, object? args)
    {
        switch (command)
        {
            case nameof(IWebView.EvaluateJavaScriptAsync) when args is EvaluateJavaScriptAsyncRequest request:
                _ = CompleteEvaluateAsync(request, UseOverlaySlot(), _overlayWindowId);
                return;
            case nameof(IWebView.Eval) when args is string script:
                // Fire-and-forget evaluation (IWebView.Eval has no result).
                _ = EvaluateJavaScriptAsyncCore(script, UseOverlaySlot(), _overlayWindowId);
                return;
            case nameof(IWebView.GoBack):
                SendHistoryCommand(_overlayWindowId, "back", WebNavigationEvent.Back, UseOverlaySlot());
                return;
            case nameof(IWebView.GoForward):
                SendHistoryCommand(_overlayWindowId, "forward", WebNavigationEvent.Forward, UseOverlaySlot());
                return;
            case nameof(IWebView.Reload):
                SendHistoryCommand(_overlayWindowId, "refresh", WebNavigationEvent.Refresh, UseOverlaySlot());
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    /// <summary>
    /// MULTI-OVERLAY-FULL use gate: ensures a live claim before a per-overlay command and replays
    /// the page when the claim is a restore; answers the claim to tag the command with.
    /// </summary>
    private int UseOverlaySlot()
    {
        // MULTIWINDOW-L2: an explicit use is also a settle point for a late window stamp.
        RefreshOverlayWindow();
        // AUTODISCONNECT: an explicit use (eval/history) of a control that is out of the tree
        // stays suspended (-1 completes the request without an overlay); the attach signal
        // restores it when the control is added back. A preempted but attached handler still
        // restores here (the LRU use gate).
        if (_overlayDetached)
        {
            return -1;
        }
        if (EnsureOverlaySlot())
        {
            ReplayOverlay();
        }
        if (_overlaySlot >= 0)
        {
            _overlayEngaged = true;
        }
        return _overlaySlot;
    }

    /// <summary>
    /// Sends an ArkWeb history command (back/forward/refresh) and remembers the matching
    /// WebNavigationEvent for the next page event. The shell reports the resulting history
    /// availability afterwards, so the command itself needs no reply.
    /// </summary>
    private static void SendHistoryCommand(string windowId, string op, WebNavigationEvent navigationEvent, int slot)
    {
        s_pendingNavigation = navigationEvent;
        OpenHarmonyChildWeb.CommandForWindow(windowId, op, OpenHarmonyOverlays.Tag(slot));
    }

    /// <summary>Evaluates a script on the shell's ArkWeb page; null when no page or host answers.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script) => EvaluateJavaScriptAsyncCore(script, UseOverlaySlot(), _overlayWindowId);

    /// <summary>
    /// Sends the script to the ArkTS shell (registerWebEvalSink) and awaits the runJavaScript
    /// result by request id. A missing host library or sink answers null instead of throwing.
    /// The global form (no slot) targets the first overlay: cookie reads and diagnostics only
    /// need some live page, and the cookie store is shared by every overlay.
    /// </summary>
    internal static Task<string?> EvaluateJavaScriptAsyncCore(string script)
        => EvaluateJavaScriptAsyncCore(script, 0, null);

    /// <summary>
    /// Slot-tagged form (MULTI-OVL): the shell runs the script on the WebviewController of the
    /// matching overlay ("s&lt;slot&gt;\n&lt;script&gt;"), so a Blazor IPC eval lands in the
    /// BlazorWebView's document and never in the HybridWebView's.
    /// </summary>
    internal static Task<string?> EvaluateJavaScriptAsyncCore(string script, int slot)
        => EvaluateJavaScriptAsyncCore(script, slot, null);

    /// <summary>
    /// MULTIWINDOW-L2 window-aware form: the primary/unknown window keeps the historical wire
    /// ("s&lt;slot&gt;\n&lt;script&gt;"); a secondary window's script goes to the subwindow
    /// page's own eval sink through OpenHarmonyChildWeb (a slot-less child control has no page
    /// and answers null immediately).
    /// </summary>
    internal static async Task<string?> EvaluateJavaScriptAsyncCore(string script, int slot, string? windowId)
    {
        if (string.IsNullOrEmpty(script))
        {
            return null;
        }
        if (OpenHarmonyChildWeb.IsApplicable(windowId))
        {
            if (slot < 0)
            {
                return null;
            }
            return await SendHostRequestAsync(
                requestId => OpenHarmonyChildWeb.Eval(windowId!, slot, script, requestId)).ConfigureAwait(false);
        }
        string tagged = OpenHarmonyOverlays.TagScript(slot, script);
        return await SendHostRequestAsync(requestId => WebEvalNative(tagged, requestId) == 0).ConfigureAwait(false);
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

    private static async Task CompleteEvaluateAsync(EvaluateJavaScriptAsyncRequest request, int slot = 0, string? windowId = null)
    {
        string? result = await EvaluateJavaScriptAsyncCore(request.Script, slot, windowId).ConfigureAwait(false);
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
    /// Parses "__OHNAV|&lt;url&gt;|&lt;id&gt;" or the slot-tagged MULTI-OVL form
    /// "__OHNAV|s&lt;slot&gt;|&lt;url&gt;|&lt;id&gt;", raises Navigating on the WebViews that
    /// own the slot and, when none cancelled, approves exactly that URL back to the shell
    /// ("nav" command carrying the same slot, so the shell reloads the right overlay). The
    /// shell reloads only the URL it cancelled for the id it issued, so a forged approval is
    /// inert.
    /// </summary>
    internal static void HandleNavigationRequest(string payload)
    {
        if (!OpenHarmonyOverlays.TryParseNavigationRequest(payload, out int slot, out string url, out string requestId))
        {
            OpenHarmonyBridge.WriteStatus("[maui] web navigation rejected: malformed request");
            return;
        }
        if (requestId.Length > 128 || url.Length == 0 || url.Length > MaxNavUrlLength)
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
        if (!RaiseNavigating(url, WebNavigationEvent.NewPage, slot))
        {
            // The app cancelled: leave the load blocked (no approval is sent).
            return;
        }
        ApproveNavigation(url);
        NavigationApprovalSent?.Invoke(requestId, url);
        OpenHarmonyBridge.WebCommand("nav", OpenHarmonyOverlays.Tag(slot, requestId + "\n" + url));
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
        => RaiseNavigating(url, WebNavigationEvent.NewPage, -1, null);

    /// <summary>
    /// Raises Navigating with the load's event kind (Back/Forward/Refresh for history loads) on
    /// the views that own <paramref name="slot"/> (MULTI-OVL). A negative slot stays a fan-out
    /// to every connected view, the legacy single-overlay behavior. MULTIWINDOW-L2:
    /// <paramref name="windowId"/> scopes the fan-out to one host (null = the primary window).
    /// </summary>
    private static bool RaiseNavigating(string url, WebNavigationEvent navigationEvent, int slot = -1, string? windowId = null)
    {
        bool allowed = true;
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (!HandlerMatches(handler, windowId, slot))
                {
                    continue;
                }
                if (handler.VirtualView is { } webView && webView.Navigating(navigationEvent, url))
                {
                    allowed = false;
                }
            }
        }
        return allowed;
    }

    /// <summary>
    /// True when the handler owns the event: the primary host for an untagged/primary state,
    /// the tagged window otherwise; a non-negative slot additionally scopes the event to the
    /// handler that claimed it. The primary filter keeps the two pools' slot numbers (and the
    /// child window's controls) from receiving each other's page events.
    /// </summary>
    private static bool HandlerMatches(OpenHarmonyWebViewHandler handler, string? windowId, int slot)
    {
        if (!string.IsNullOrEmpty(windowId))
        {
            return string.Equals(handler._overlayWindowId, windowId, StringComparison.Ordinal) &&
                   (slot < 0 || handler._overlaySlot == slot);
        }
        return handler._overlayWindowId == OpenHarmonyWindowSurface.PrimaryWindowId &&
               (slot < 0 || handler._overlaySlot == slot);
    }

    /// <summary>
    /// Mirrors a completed/failed shell load into IWebView.Navigated with the event kind the
    /// load was started with (Back/Forward/Refresh for history loads, NewPage otherwise) and
    /// consumes the pending kind. Slot-tagged events only reach the views that own the slot
    /// (MULTI-OVL); MULTIWINDOW-L2 scopes by window on top of that.
    /// </summary>
    private static void RaiseNavigated(string url, WebNavigationResult result, int slot = -1, string? windowId = null)
    {
        WebNavigationEvent navigationEvent = s_pendingNavigation;
        s_pendingNavigation = WebNavigationEvent.NewPage;
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (!HandlerMatches(handler, windowId, slot))
                {
                    continue;
                }
                handler.VirtualView?.Navigated(navigationEvent, url, result);
            }
        }
    }

    /// <summary>
    /// Applies the shell's history state ("history|&lt;back&gt;|&lt;forward&gt;", 1/0 flags) to
    /// the views that own <paramref name="slot"/>; MULTIWINDOW-L2 scopes by window on top of
    /// that. A negative slot stays the legacy fan-out.
    /// </summary>
    private static void ApplyHistoryState(string state, int slot = -1, string? windowId = null)
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
                if (!HandlerMatches(handler, windowId, slot))
                {
                    continue;
                }
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
        // AUTODISCONNECT: the control is out of the tree; an attach (or the arrange safety net)
        // replays the current Source when it is added back, so the mapper must not claim an
        // overlay for a removed control.
        if (handler._overlayDetached)
        {
            return;
        }
        // MULTIWINDOW-L2: the load that follows a connect must already target the control's
        // window; a late stamp is applied here before the claim.
        handler.RefreshOverlayWindow();
        // A source load is a fresh navigation, not a history move.
        s_pendingNavigation = WebNavigationEvent.NewPage;
        // MULTI-OVERLAY-FULL: claiming here is enough; the source load itself is the replay a
        // restored handler needs (PlatformArrange's ReplayOverlay funnels through this mapper).
        int slot = handler.EnsureOverlaySlot() ? handler._overlaySlot : handler.UseOverlaySlot();
        if (handler._overlaySlot >= 0)
        {
            handler._overlayEngaged = true;
        }
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
                SendOverlayCommand(handler, "load", OpenHarmonyOverlays.Tag(slot, url.Url));
                break;
            case HtmlWebViewSource html:
                SendOverlayCommand(handler, "data", OpenHarmonyOverlays.Tag(slot, html.Html));
                break;
            default:
                SendOverlayCommand(handler, "show", OpenHarmonyOverlays.Tag(slot));
                break;
        }
    }

    /// <summary>
    /// MULTIWINDOW-L2: one per-overlay command on the handler's window. A child-window control
    /// without a claim (the child pool is full) has no page and drops the command instead of
    /// letting an untagged argument land on the child pool's slot 0; the primary/unknown window
    /// keeps the historical wire byte-for-byte.
    /// </summary>
    private static void SendOverlayCommand(OpenHarmonyWebViewHandler handler, string op, string? arg)
    {
        if (OpenHarmonyChildWeb.IsApplicable(handler._overlayWindowId) && handler._overlaySlot < 0)
        {
            return;
        }
        OpenHarmonyChildWeb.CommandForWindow(handler._overlayWindowId, op, arg);
    }

    /// <summary>Mirrors a shell page event into the MAUI WebView events.</summary>
    public static void OnPageEvent(string state, string url)
    {
        // MULTIWINDOW-L2: a subwindow-tagged state ("w:<window>|s<slot>|<state>") belongs to the
        // child host's own pool; it is filtered by window and never touches the primary pool's
        // capacity/LRU state. "w:<window>|capacity|<n>" marks that host ready.
        string? windowId = null;
        string effectiveState = state;
        int slot = -1;
        if (OpenHarmonyChildWeb.TryParseState(state, out string childWindow, out string childRest))
        {
            windowId = childWindow;
            effectiveState = childRest;
            if (effectiveState.StartsWith("capacity|", StringComparison.Ordinal))
            {
                // The subwindow page declared its ArkWeb pool; the managed child pool flushes
                // the commands queued before the page loaded (a lost advertisement only delays
                // the flush; the queue is bounded).
                if (int.TryParse(effectiveState.Substring("capacity|".Length), out int childCapacity))
                {
                    OpenHarmonyChildWeb.SetCapacity(childWindow, childCapacity);
                    OpenHarmonyBridge.WriteStatus($"[maui] child web capacity: {childWindow} {childCapacity}");
                }
                return;
            }
            if (effectiveState == "capacity")
            {
                // A shell variant that carries the count in the event URL (the primary page's
                // shape) instead of the state; accepted so a mixed pairing can never drop the
                // advertisement silently (the first device round's failure mode).
                if (int.TryParse(url, out int urlCapacity))
                {
                    OpenHarmonyChildWeb.SetCapacity(childWindow, urlCapacity);
                    OpenHarmonyBridge.WriteStatus($"[maui] child web capacity: {childWindow} {urlCapacity}");
                }
                return;
            }
            if (!OpenHarmonyOverlays.TryParseEventState(effectiveState, out int childSlot, out string childState))
            {
                // A child-tagged state without a slot tag is not part of this wire; ignore it.
                return;
            }
            slot = childSlot;
            effectiveState = childState;
        }
        // MULTI-OVL: the shell prefixes each per-overlay event with its slot ("s0|finished").
        // An untagged state (a shell that predates the second overlay) stays a fan-out.
        else if (OpenHarmonyOverlays.TryParseEventState(state, out int eventSlot, out string rest))
        {
            slot = eventSlot;
            effectiveState = rest;
        }
        if (effectiveState == "capacity" && windowId is null)
        {
            // SLOTS-DYNAMIC: the shell advertises how many ArkWeb overlays it can declare
            // (untagged "capacity" state, the count in the URL slot). The pool clamps itself to
            // that capacity; a legacy 2-overlay shell preempts any claim beyond it through the
            // usual suspend/replay path, so the managed side never tags a slot the shell cannot
            // serve. The default (no event) is the pool's MaxOverlays, matching the lockstep
            // shell.
            if (int.TryParse(url, out int shellCapacity))
            {
                OpenHarmonyOverlays.SetShellCapacity(shellCapacity);
                OpenHarmonyBridge.WriteStatus($"[maui] web capacity: {shellCapacity}");
            }
            return;
        }
        if (effectiveState == "activate")
        {
            // MULTI-OVERLAY-FULL: the shell forwards a user touch on an overlay as
            // "s<slot>|activate" so the LRU pool refreshes the slot's owner (the shell cannot
            // touch the pool itself). Only the handler that owns the slot is refreshed. The
            // child pool has no LRU preemption in this wave, so a child activation is ignored.
            if (windowId is null && slot >= 0)
            {
                lock (s_handlers)
                {
                    foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
                    {
                        if (handler._overlaySlot == slot && OpenHarmonyOverlays.Touch(slot, handler))
                        {
                            break;
                        }
                    }
                }
            }
            return;
        }
        if (effectiveState == "started")
        {
            // A load the shell already asked about (B6) raised Navigating before it started;
            // do not raise it a second time. App-origin loads never take that path. The event
            // kind is the one the triggering command set (Back/Forward/Refresh or NewPage).
            if (windowId is null && ConsumeApprovedNavigation(url))
            {
                return;
            }
            RaiseNavigating(url, s_pendingNavigation, slot, windowId);
            return;
        }
        if (effectiveState.StartsWith(HistoryStatePrefix, StringComparison.Ordinal))
        {
            // ArkWeb history availability after a page end/back/forward/refresh; mirrors into
            // IWebView.CanGoBack/CanGoForward and consumes the pending history kind.
            ApplyHistoryState(effectiveState, slot, windowId);
            return;
        }
        if (effectiveState == "error")
        {
            // A failed main-frame load: clear only that overlay (the managed surface shows
            // through) and report the failure through IWebView.Navigated. The shell reports
            // this outside the page's control, so a script cannot turn a failure into a
            // success. MULTI-OVERLAY-FULL: the slot-tagged hide clears this overlay only;
            // an untagged command (legacy shell) keeps the global hide.
            OpenHarmonyChildWeb.CommandForWindow(windowId, "hide", OpenHarmonyOverlays.Tag(slot));
            RaiseNavigated(url, WebNavigationResult.Failure, slot, windowId);
        }
        else if (effectiveState == "finished")
        {
            RaiseNavigated(url, WebNavigationResult.Success, slot, windowId);
            // The page is done: mirror the ArkWeb cookie store back into IWebView.Cookies
            // (best-effort; the container stays authoritative for what the app set). The cookie
            // jar is process-wide, so the read may run on either host's page.
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
                    OpenHarmonyBridge.WriteStatus($"[maui] web {effectiveState}: {loggedUrl}");
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
