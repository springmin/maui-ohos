// Minimal HybridWebView handler for OpenHarmony: the MAUI HybridWebView contracts
// (EvaluateJavaScriptAsync, SendRawMessage, RawMessageReceived and InvokeJavaScriptAsync) ride
// the same shell channel as the WebView handler: scripts run through ohos_host_web_eval and
// page messages arrive through the shell's dotnetHost proxy, which prefixes every payload with a
// "__OHORIGIN|<document url>|<document id>\n" envelope (B1/B2/B3). Messages are only accepted
// for this handler's own HybridWebView origin and registration id, raw-message dispatch is
// scoped to the matching handler and __InvokeJavaScript completions must match the handler and
// document that started the invocation; host -> page evals check the shell-stamped
// window.__ohHybridId marker first.
//
// Asset serving: HybridRoot/DefaultFile are wired to the ArkTS shell. When the handler is
// connected (or either property changes) it extracts the framework bootstrap script
// (_framework/hybridwebview.js) out of the Microsoft.Maui assembly into the extracted app
// payload directory and registers that directory + HybridRoot + DefaultFile with the shell
// through the "hybrid" web command. The shell answers requests for the MAUI hybrid origin
// (https://0.0.0.1/, see HybridWebViewHandler.AppOrigin) from the payload with
// onInterceptRequest (file reads through @ohos.file.fs) and loads the origin, so a stock
// HybridWebView page renders and window.HybridWebView.SendRawMessage reaches
// RawMessageReceived through the shell's __hwvSendMessage forwarding.
// The app context (and with it <c>AppDir</c>) can be published after the handler connects
// (host startup ordering), so registration is lazy: a connect without a payload directory
// is remembered and retried from the bridge's Initialized/SurfaceChanged signals (late
// subscribers get the current state replayed) and from the first PlatformArrange, and every
// pending root is registered exactly once (repeated mapper passes or event replays do not
// re-issue the command).
//
// JS -> .NET invocation: the stock hybridwebview.js POSTs { MethodName, ParamValues } to
// <origin>/__hwvInvokeDotNet. The ArkTS shell holds the intercepted WebResourceResponse open
// with setResponseIsReady(false), forwards the invocation through
// host.notifyHybridInvoke(requestId, method, argsJson), and completes the response when this
// handler calls ohos_host_hwv_invoke_result(requestId, payloadJson) with the
// DotNetInvokeResult JSON the page's fetch expects. A missing invoker, a failed or timed-out
// target method and a missing host/shell path all answer an error payload, so the page's
// promise rejects instead of hanging. Off-device (no host library / no app context) the
// registration is remembered as pending and retried when the bridge publishes the context;
// callers can still drive the managed half directly.
//
// Accepted corner cases (documented, deliberately not fixed here):
//  - The shell stamps one hybridDocId per served document, so with two HybridWebViews registered
//    on the same page the ids are not unique per registration: a message from an older, still
//    loaded page can be attributed to the newest handler. A proper fix needs a per-registration
//    document id and matching shell changes, which is out of scope for this slice.
//  - If the shell's dotnetHost proxy were injected into subframes, a hostile iframe's raw
//    messages would carry the main document's envelope and be attributed to the main document;
//    completing a __InvokeJavaScript call still additionally requires the unguessable per-call
//    taskId, so an iframe cannot finish (or hijack) another page's invocation.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

public sealed partial class OpenHarmonyHybridWebViewHandler : OpenHarmonyViewHandler<IHybridWebView>, IOpenHarmonyOverlaySlotOwner, IOpenHarmonyOverlaySlotLifetime
{
    /// <summary>Message prefix the stock HybridWebView JavaScript uses for raw messages.</summary>
    internal const string RawMessagePrefix = "__RawMessage|";

    /// <summary>
    /// Document-origin envelope the ArkTS shell prepends to every dotnetHost payload:
    /// <c>__OHORIGIN|&lt;document url&gt;|&lt;document id&gt;\n</c>. The shell writes both header
    /// fields (page scripts cannot), so they are trusted protocol data: the url binds a message
    /// to a page origin and the id binds it to the registration that stamped
    /// <c>window.__ohHybridId</c> into the document (see <see cref="PageDocumentId"/>). A message
    /// without a valid envelope is rejected (B1/B2).
    /// </summary>
    internal const string OriginEnvelopePrefix = "__OHORIGIN|";

    /// <summary>
    /// Largest JS -&gt; .NET payload the managed boundary accepts from one page message, in UTF-16
    /// code units (4 MiB). The host marshals the whole UTF-8 string out of the shell before managed
    /// code runs, so this cap cannot undo that first native copy; it bounds everything managed does
    /// with the string (the <see cref="Uri.UnescapeDataString(string)"/> copy, prefix parsing,
    /// per-handler dispatch, JSON parsing of invocation arguments/results and any app-level work)
    /// and rejects oversized payloads with a clear error instead of letting a hostile page grow the
    /// managed heap without bound. 4 MiB is ~130x the largest legitimate payload the interaction
    /// harness exercises (32 KiB) and far above any window.HybridWebView control message.
    /// </summary>
    internal const int MaxPagePayloadLength = 4 * 1024 * 1024;

    internal const string InvokeCompletedPrefix = "__InvokeJavaScriptCompleted|";
    internal const string InvokeFailedPrefix = "__InvokeJavaScriptFailed|";

    /// <summary>Embedded framework script path (resource name in the Microsoft.Maui assembly).</summary>
    internal const string HybridWebViewScriptPath = "_framework/hybridwebview.js";

    /// <summary>MAUI hybrid origin the shell serves the app package from.</summary>
    internal const string HybridAppOrigin = "https://0.0.0.1/";

    private const string HostLibrary = "libopenharmonyhost.so";

    private static readonly TimeSpan s_invokeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_dotNetInvokeTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri s_appOriginUri = new(HybridAppOrigin, UriKind.Absolute);
    private static readonly List<OpenHarmonyHybridWebViewHandler> s_handlers = new();
    private static readonly ConcurrentDictionary<string, PendingInvoke> s_invokeRequests = new();

    /// <summary>
    /// Overlay slot this handler claimed while connected (MULTI-OVL): the hybrid frame, eval
    /// and registration all carry it, so the shell serves this handler's page on its own
    /// ArkWeb overlay instead of arbitrating a single one (FIX-WVP). MULTI-OVERLAY-FULL: the
    /// claim is owner-aware and LRU, so a third web control preempts the least-recently-used
    /// idle slot and a preempted hybrid replays its registration (load/attach) on restore.
    /// -1 while suspended between a preemption and the restore.
    /// </summary>
    private int _overlaySlot = -1;

    /// <summary>
    /// MULTIWINDOW-L2: the managed window this hybrid belongs to. The primary id keeps the
    /// historical single-host path; a secondary id routes the claim, registration and page
    /// events through the subwindow's own ArkWeb pool, where the asset bridge does not run in
    /// this wave (the child page refuses the registration and the control stays unmounted -
    /// documented degradation, no primary-window corruption).
    /// </summary>
    private string _overlayWindowId = OpenHarmonyWindowSurface.PrimaryWindowId;

    /// <summary>True while this handler lost its slot to an LRU preemption (registration replay).</summary>
    private bool _overlayPreempted;

    /// <summary>
    /// True while this handler was removed from the visual tree (AUTODISCONNECT). The slot was
    /// released (a dynamic slot is destroyed in the shell); the next arrange or re-attach
    /// re-claims a slot and replays the registration. Cleared by <see cref="EnsureOverlaySlot"/>.
    /// </summary>
    private bool _overlayDetached;

    /// <summary>True after a frame/load was sent for the current claim (the victim heuristic).</summary>
    private bool _overlayEngaged;

    /// <summary>The claimed overlay slot (MULTI-OVL); -1 while suspended or beyond the cap.</summary>
    internal int OverlaySlot => _overlaySlot;

    /// <summary>
    /// One in-flight window.HybridWebView.__InvokeJavaScript call. The completion is only
    /// accepted from the handler that started it and with the document id the shell stamped for
    /// that handler's page, so a harvested task id cannot be completed by another page (B2).
    /// </summary>
    private sealed class PendingInvoke
    {
        public PendingInvoke(TaskCompletionSource<string?> source, OpenHarmonyHybridWebViewHandler handler)
        {
            Source = source;
            Handler = handler;
        }

        public TaskCompletionSource<string?> Source { get; }

        public OpenHarmonyHybridWebViewHandler Handler { get; }
    }

    // Handlers that connected before the app context published an AppDir. Registration is
    // retried from the bridge signals below (and from the first arrange) and the handler is
    // removed as soon as its assets land, so each pending root registers exactly once.
    private static readonly HashSet<OpenHarmonyHybridWebViewHandler> s_pendingRegistration = new();
    private static bool s_registrationHooksAttached;
    // FIX-BACKSIZE: connected handlers whose assets actually reached the shell. The shell's
    // single ArkWeb overlay is arbitrated on registration (a hybrid registration keeps it,
    // FIX-WVP), so while this is non-zero other web handlers must not send overlay frames.
    private static int s_registeredOverlayOwners;
    private bool _registeredOverlay;
    private string? _registeredAssets;
    // B2/B3 identity of this handler's page: generated once per handler, handed to the shell
    // with the asset registration (HybridAssetsConfig.id), stamped into the served documents as
    // window.__ohHybridId and echoed in every message envelope.
    private readonly string _pageId = Guid.NewGuid().ToString("N");

    /// <summary>Raised after the "hybrid" shell command is issued (test/diagnostic hook).</summary>
    internal event Action<string, string, string>? HybridAssetsRegistered;

    /// <summary>True while this handler's asset registration waits for the app context.</summary>
    internal bool IsHybridAssetsRegistrationPending
    {
        get
        {
            lock (s_handlers)
            {
                return s_pendingRegistration.Contains(this);
            }
        }
    }

    /// <summary>
    /// True while a connected HybridWebView has registered its assets with the shell: the
    /// shell's single ArkWeb overlay then serves the hybrid origin (FIX-WVP registration
    /// arbitration), so another web handler's overlay frame would move the hybrid page onto
    /// its own box (observed on the device once the BlazorWebView got a real desired size,
    /// FIX-BACKSIZE).
    /// </summary>
    internal static bool HasRegisteredOverlay
    {
        get
        {
            lock (s_handlers)
            {
                return s_registeredOverlayOwners > 0;
            }
        }
    }

    /// <summary>The payload layout last registered with the shell, or null (test/diagnostic hook).</summary>
    internal string? RegisteredHybridAssets
    {
        get
        {
            lock (s_handlers)
            {
                return _registeredAssets;
            }
        }
    }

    /// <summary>
    /// The per-registration document id this handler sends to the shell (B2/B3). The shell
    /// stamps it into the pages it serves for this registration and echoes it in the message
    /// envelope, so messages can be matched to this exact handler. Test/diagnostic hook.
    /// </summary>
    internal string PageDocumentId => _pageId;

    // The shell forwards __hwvInvokeDotNet invocations to ohos_host_hwv_register_invoke's
    // callback; the handler that last registered its assets with the shell is the page a
    // request belongs to (the shell serves one hybrid origin at a time).
    private static unsafe IntPtr s_hybridInvokeCallback = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void>)&OnHybridInvokeNative;
    private static bool s_invokeRegistered;
    private static bool s_invokeUnavailable;
    private static OpenHarmonyHybridWebViewHandler? s_activeInvokeHandler;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_hwv_register_invoke")]
    private static partial void RegisterInvokeNative(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_hwv_invoke_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int InvokeResultNative(int requestId, string payloadJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HybridInvokeCallback(int requestId, IntPtr methodUtf8, IntPtr argsUtf8);

    /// <summary>Receives the completed invocation payload when the host library is present.</summary>
    internal static event Action<int, string>? HybridInvokeResultSent;

    public static readonly IPropertyMapper<IHybridWebView, OpenHarmonyHybridWebViewHandler> Mapper =
        new PropertyMapper<IHybridWebView, OpenHarmonyHybridWebViewHandler>(ViewMapper)
        {
            [nameof(IHybridWebView.HybridRoot)] = MapHybridAssets,
            [nameof(IHybridWebView.DefaultFile)] = MapHybridAssets,
        };

    public OpenHarmonyHybridWebViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsWebView = true, Background = Colors.White };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        // MULTIWINDOW-L2: the window decides the host; the child pool is a separate number
        // space, so a child hybrid never occupies a primary overlay slot.
        _overlayWindowId = OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView);
        _overlaySlot = OpenHarmonyChildWeb.AcquireForWindow(_overlayWindowId, this);
        lock (s_handlers)
        {
            s_handlers.Add(this);
        }
        // The hybrid page posts its messages through the shell's JS message sink, so the sink
        // must be bound even when the app never connects the regular WebView handler.
        OpenHarmonyWebViewHandler.EnsureMessageRegistered();
        EnsureHybridInvokeRegistered();
        s_activeInvokeHandler = this;
        RegisterHybridAssets();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        lock (s_handlers)
        {
            s_handlers.Remove(this);
            s_pendingRegistration.Remove(this);
            if (_registeredOverlay)
            {
                // The hybrid registration no longer backs the shell overlay (FIX-BACKSIZE).
                _registeredOverlay = false;
                s_registeredOverlayOwners--;
            }
        }
        if (ReferenceEquals(s_activeInvokeHandler, this))
        {
            s_activeInvokeHandler = null;
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
    /// that has to replay its registration. A live claim is only touched (LRU bookkeeping).
    /// Never throws.
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
            OpenHarmonyBridge.WriteStatus($"[maui] hybrid overlay restored: slot {slot}");
            return true;
        }
        return false;
    }

    private bool _overlayWindowClosed;

    /// <summary>
    /// MULTIWINDOW-L2: the window ended while this handler is still connected; release the
    /// child-pool claim so no ArkWeb survives its window (the asset bridge is
    /// primary-window-only in this wave, so a child claim is unmounted anyway). Called by
    /// OpenHarmonyWebViewHandler.ReleaseWindowOverlays.
    /// </summary>
    internal static void ReleaseWindowOverlays(string windowId)
    {
        lock (s_handlers)
        {
            foreach (OpenHarmonyHybridWebViewHandler handler in s_handlers.ToArray())
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
    }

    /// <summary>
    /// Replays this hybrid's registration on the slot it just re-acquired: the shell serves the
    /// layout for the new slot and loads the hybrid origin, so the page (and its document id
    /// stamp) comes back after a preemption.
    /// </summary>
    private void ReplayOverlay()
    {
        if (_overlaySlot < 0)
        {
            return;
        }
        OpenHarmonyBridge.WriteStatus($"[maui] hybrid overlay replay: slot {_overlaySlot}");
        lock (s_handlers)
        {
            _registeredAssets = null;
        }
        RegisterHybridAssets();
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
        OpenHarmonyBridge.WriteStatus($"[maui] hybrid overlay preempted: slot {slot}");
    }

    /// <summary>
    /// AUTODISCONNECT: the hybrid was removed from its layout. Drop the claim (the shell
    /// destroys a dynamic slot), keep the handler connected (MAUI semantics) and mark the
    /// control for a registration replay so a re-add rebuilds the page. Idempotent.
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
        // the handler's window.
        OpenHarmonyChildWeb.CommandForWindow(_overlayWindowId, "hide", OpenHarmonyOverlays.Tag(slot));
        OpenHarmonyChildWeb.ReleaseForWindow(_overlayWindowId, slot, this);
        OpenHarmonyBridge.WriteStatus($"[maui] hybrid overlay detached: slot {slot}");
    }

    /// <summary>
    /// AUTODISCONNECT: the hybrid re-entered the visual tree. A detached/preempted handler
    /// re-claims a slot and replays its registration; a live claim is only touched.
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

    /// <summary>True while this hybrid's overlay is showing (the pool preempts idle owners first).</summary>
    bool IOpenHarmonyOverlaySlotOwner.IsOverlaySlotEngaged => _overlaySlot >= 0 && _overlayEngaged;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(400, heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        // The ArkWeb component is a shell overlay: place it on the control's frame (tagged with
        // this handler's slot, MULTI-OVL). MULTI-OVERLAY-FULL: a handler suspended by an LRU
        // preemption must NOT restore from an arrange (a layout pass runs for every control, so
        // restoring here would let two suspended hybrids preempt each other forever); an
        // explicit use (registration/eval/raw message) or a first-time claim does it.
        // AUTODISCONNECT: a detached handler is not arranged while it is gone, so an arrange
        // after a detach means it was re-attached: restore and replay here (safety net; the
        // attach signal normally did it).
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
            OpenHarmonyWebViewHandler.SendPlatformFrame(_overlayWindowId, frame, _overlaySlot);
        }
        // First render: if ConnectHandler ran before the app context was published, this is
        // the point where the shell (and the extracted payload) is definitely available.
        if (IsHybridAssetsRegistrationPending)
        {
            RegisterHybridAssets();
        }
    }

    /// <summary>
    /// HybridRoot/DefaultFile mapper: (re)registers the asset root with the shell. Without a
    /// host library (tests) or an extracted app directory the call is remembered and retried
    /// when the app context becomes available.
    /// </summary>
    public static void MapHybridAssets(OpenHarmonyHybridWebViewHandler handler, IHybridWebView webView)
        => handler.RegisterHybridAssets();

    private void RegisterHybridAssets()
    {
        // MULTI-OVERLAY-FULL: a suspended handler claims (or re-claims) a slot here; the
        // registration below is the load/attach replay, so no extra replay pass is needed.
        // AUTODISCONNECT: a detached hybrid must not re-claim/register (a pending-context
        // replay or a HybridRoot change would otherwise resurrect the overlay of a control
        // that is out of the tree); the attach signal registers again with the current root.
        if (_overlayDetached)
        {
            return;
        }
        EnsureOverlaySlot();
        OpenHarmonyAppContext? context = OpenHarmonyBridge.Context;
        if (context is null || string.IsNullOrEmpty(context.AppDir))
        {
            // The shell may publish the app directory after the handler connected; do not drop
            // the registration. The bridge replays its current context/surface to late
            // subscribers, so the hooks fire both when the state already exists and when it
            // appears later.
            MarkRegistrationPending(this);
            return;
        }
        string root = VirtualView?.HybridRoot is { Length: > 0 } hybridRoot ? hybridRoot : "wwwroot";
        string defaultFile = VirtualView?.DefaultFile is { Length: > 0 } file ? file : "index.html";
        // B5: mirror the shell's registration-time guard. The shell answers app-origin requests
        // by joining <base>/<root>/<path>, so a control value that is not an ordinary relative
        // path could escape the payload directory. Fall back to the defaults with a status log
        // instead of registering a hostile layout.
        if (!IsSafeAssetLayoutPart(root))
        {
            OpenHarmonyBridge.WriteStatus($"[maui] hybrid root '{root}' is not a safe relative path; using 'wwwroot'");
            root = "wwwroot";
        }
        if (!IsSafeAssetLayoutPart(defaultFile))
        {
            OpenHarmonyBridge.WriteStatus(
                $"[maui] hybrid default file '{defaultFile}' is not a safe relative path; using 'index.html'");
            defaultFile = "index.html";
        }
        string payloadDir = context.AppDir.TrimEnd('/');
        // MULTI-OVERLAY-FULL: the slot is part of the registration key, so a restore that lands
        // on a different slot (or the same slot after another owner took it) re-issues the shell
        // command and reloads the hybrid page there.
        string key = payloadDir + "|" + root + "|" + defaultFile + "|s" + _overlaySlot;
        bool register;
        lock (s_handlers)
        {
            // The shell is serving this handler's page, so invocations belong to it.
            s_activeInvokeHandler = this;
            register = key != _registeredAssets;
            _registeredAssets = key;
            s_pendingRegistration.Remove(this);
            if (!_registeredOverlay)
            {
                // First successful registration for this handler: the shell's single overlay now
                // serves the hybrid origin until this handler disconnects (FIX-BACKSIZE).
                _registeredOverlay = true;
                s_registeredOverlayOwners++;
            }
        }
        if (!register)
        {
            // ConnectHandler and the HybridRoot/DefaultFile mapper both land here; only
            // re-issue the shell command when the payload layout or the slot changed.
            return;
        }
        _overlayEngaged = true;
        EnsureHybridWebViewScript(payloadDir);
        OpenHarmonyChildWeb.CommandForWindow(_overlayWindowId, "hybrid", JsonSerializer.Serialize(new HybridAssetsConfig
        {
            Base = payloadDir,
            Root = root,
            DefaultFile = defaultFile,
            Id = _pageId,
            Slot = _overlaySlot,
        }, OpenHarmonySliceJsonContext.Default.HybridAssetsConfig));
        HybridAssetsRegistered?.Invoke(payloadDir, root, defaultFile);
    }

    /// <summary>
    /// Registration-time layout guard (B5, the mirror of the shell's <c>isSafeLayoutPart</c>): a
    /// HybridRoot or DefaultFile may only be an ordinary relative path - no empty, "." or ".."
    /// segment, no '\', no embedded NUL and no leading '/' - so the shell's
    /// <c>&lt;base&gt;/&lt;root&gt;/&lt;path&gt;</c> join cannot escape the extracted payload
    /// directory. A drive-style ':' before the first '/' ("C:/x", "c:foo") is rejected as well:
    /// those forms are absolute under a Windows-style join, while a ':' from the second segment
    /// on is an ordinary POSIX file-name character that cannot change the join target.
    /// </summary>
    private static bool IsSafeAssetLayoutPart(string path)
    {
        if (path.Length == 0 ||
            path.IndexOf('\0', StringComparison.Ordinal) >= 0 ||
            path.IndexOf('\\', StringComparison.Ordinal) >= 0 ||
            path.StartsWith('/', StringComparison.Ordinal))
        {
            return false;
        }
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }
        // A ':' only passes from the second path component on (see the summary above).
        int firstSeparator = path.IndexOf('/', StringComparison.Ordinal);
        int colon = path.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || (firstSeparator >= 0 && colon > firstSeparator);
    }

    /// <summary>Remembers a handler whose assets are waiting for the app context.</summary>
    private static void MarkRegistrationPending(OpenHarmonyHybridWebViewHandler handler)
    {
        lock (s_handlers)
        {
            s_pendingRegistration.Add(handler);
        }
        EnsureRegistrationHooks();
    }

    /// <summary>
    /// Installs one process-wide retry subscription on the bridge signals that can announce a
    /// late app context (Initialized replays the current context, SurfaceChanged the last
    /// surface). Idempotent; the hooks stay for the process lifetime.
    /// </summary>
    private static void EnsureRegistrationHooks()
    {
        lock (s_handlers)
        {
            if (s_registrationHooksAttached)
            {
                return;
            }
            s_registrationHooksAttached = true;
        }
        OpenHarmonyBridge.Initialized += _ => RetryPendingRegistrations();
        OpenHarmonyBridge.SurfaceChanged += _ => RetryPendingRegistrations();
    }

    /// <summary>
    /// Re-attempts the asset registration of every connected handler that is still waiting for
    /// the app context. Exposed for tests; the bridge signals and the first arrange call it.
    /// </summary>
    internal static void RetryPendingRegistrations()
    {
        OpenHarmonyHybridWebViewHandler[] pending;
        lock (s_handlers)
        {
            if (s_pendingRegistration.Count == 0)
            {
                return;
            }
            pending = s_pendingRegistration.Where(s_handlers.Contains).ToArray();
        }
        foreach (OpenHarmonyHybridWebViewHandler handler in pending)
        {
            handler.RegisterHybridAssets();
        }
    }

    /// <summary>
    /// Copies the framework bootstrap script (embedded in the Microsoft.Maui assembly) next to
    /// the extracted payload, where the shell serves it from as _framework/hybridwebview.js.
    /// In the payload-in-libs launch modes (JIT DEVCOMPAT and NativeAOT) the payload root is the
    /// read-only bundle libs directory and the pack already staged the script there
    /// (OpenHarmony.Hap.targets _OpenHarmonyStageHybridWebViewScript, SAMPLE-FIX); File.Create
    /// then fails while the file the shell serves is already in place, so that case is not an
    /// extraction failure. Writable payloads (the dotnet.zip extraction tree) keep the previous
    /// overwrite semantics, so a refreshed app always serves the script of its own package.
    /// </summary>
    private static void EnsureHybridWebViewScript(string payloadDir)
    {
        string destination = Path.Combine(payloadDir, "_framework", "hybridwebview.js");
        try
        {
            using Stream? script = typeof(Microsoft.Maui.Handlers.HybridWebViewHandler).Assembly
                .GetManifestResourceStream(HybridWebViewScriptPath);
            if (script is null)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using FileStream file = File.Create(destination);
            script.CopyTo(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (File.Exists(destination))
            {
                // Pack-staged copy in a read-only payload root: nothing to extract, and the
                // version matches the Microsoft.Maui assembly the app was built against.
                return;
            }
            OpenHarmonyBridge.WriteStatus($"[maui] hybrid bootstrap script extraction failed: {ex.Message}");
        }
    }

    // --- JS -> .NET invocation (__hwvInvokeDotNet) --------------------------------------

    /// <summary>True once the host library accepted the invocation callback registration.</summary>
    internal static bool IsInvokeBridgeAvailable => s_invokeRegistered && !s_invokeUnavailable;

    /// <summary>Registers the managed invocation callback with the host (idempotent, device only).</summary>
    internal static void EnsureHybridInvokeRegistered()
    {
        if (s_invokeRegistered || s_invokeUnavailable)
        {
            return;
        }
        try
        {
            // Keep the delegate alive for the process lifetime: the host stores the raw
            // function pointer and calls it on an arbitrary thread.
            RegisterInvokeNative(s_hybridInvokeCallback);
            s_invokeRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_invokeUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] hybrid invoke bridge unavailable (no host library)");
        }
    }

    // A reverse P/Invoke entry (host.notifyHybridInvoke): the invocation runs the app's
    // IHybridWebView.Invoker, so an exception must not unwind into the native frame (MB-2).
    // The async half already turns every failure into an error payload; the guard covers the
    // synchronous boundary as well.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnHybridInvokeNative(int requestId, IntPtr methodUtf8, IntPtr argsUtf8)
    {
        try
        {
            string method = methodUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(methodUtf8) ?? string.Empty;
            string args = argsUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(argsUtf8) ?? string.Empty;
            _ = CompleteHybridInvokeAsync(requestId, method, args);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("hybrid invoke", ex);
        }
    }

    /// <summary>
    /// Managed half of host.notifyHybridInvoke: services one JS invocation and completes it.
    /// Exposed for tests; the native callback feeds the same path. MULTI-OVERLAY-FULL: the
    /// request id carries the overlay slot (<see cref="OpenHarmonyOverlays.TryDecodeInvokeRequestId"/>),
    /// so the invocation runs on the HybridWebView that owns that slot; an untagged legacy id
    /// falls back to the last registered hybrid.
    /// </summary>
    internal static Task OnHybridInvokeAsync(int requestId, string methodName, string argsJson)
        => CompleteHybridInvokeAsync(requestId, methodName, argsJson);

    private static async Task CompleteHybridInvokeAsync(int requestId, string methodName, string argsJson)
    {
        // JS -> .NET invocation arguments are page -> .NET payloads too, so the same cap applies
        // before anything is parsed or dispatched; the page gets a clear error result back.
        if (methodName.Length > MaxPagePayloadLength || argsJson.Length > MaxPagePayloadLength)
        {
            SendInvokeResult(requestId, ErrorPayload(new InvalidOperationException(
                $"the invocation payload exceeds the {MaxPagePayloadLength} character page payload cap " +
                $"(method={methodName.Length}, args={argsJson.Length})")));
            return;
        }
        string payload;
        try
        {
            OpenHarmonyHybridWebViewHandler? handler;
            if (OpenHarmonyOverlays.TryDecodeChildInvokeRequestId(requestId, out int childWindow, out int childSlot, out _))
            {
                // MULTIWINDOW-L3: the subwindow page tagged its request id with the child flag, so
                // the invocation goes to the child-window hybrid that owns the slot (the child
                // pool is a separate number space from the primary overlays). M4: the id also
                // carries the window index, so two subwindows that both own slot 0 dispatch to
                // their own handler instead of failing closed.
                handler = ChildHandlerForWindow(childWindow, childSlot);
                OpenHarmonyBridge.WriteStatus($"[maui] hybrid invoke (child window {childWindow} slot {childSlot}): {methodName}");
                if (handler is null)
                {
                    SendInvokeResult(requestId, ErrorPayload(new InvalidOperationException(
                        $"no child HybridWebView owns window {childWindow} slot {childSlot} in the subwindow pool")));
                    return;
                }
            }
            else if (OpenHarmonyOverlays.TryDecodeInvokeRequestId(requestId, out int invokeSlot, out _))
            {
                handler = HandlerForSlot(invokeSlot);
                OpenHarmonyBridge.WriteStatus($"[maui] hybrid invoke (slot {invokeSlot}): {methodName}");
                if (handler is null)
                {
                    SendInvokeResult(requestId, ErrorPayload(new InvalidOperationException(
                        $"no HybridWebView owns overlay slot {invokeSlot}")));
                    return;
                }
            }
            else
            {
                handler = s_activeInvokeHandler;
            }
            payload = handler is null
                ? ErrorPayload(new InvalidOperationException("no HybridWebView page is registered with the shell"))
                : await handler.InvokeDotNetAsync(methodName, argsJson).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            payload = ErrorPayload(ex);
        }
        SendInvokeResult(requestId, payload);
    }

    /// <summary>The connected handler that owns <paramref name="slot"/> (or null).</summary>
    private static OpenHarmonyHybridWebViewHandler? HandlerForSlot(int slot)
    {
        lock (s_handlers)
        {
            foreach (OpenHarmonyHybridWebViewHandler handler in s_handlers)
            {
                if (handler._overlaySlot == slot)
                {
                    return handler;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The connected child-window hybrid that owns <paramref name="slot"/> in the subwindow pool
    /// of the window at <paramref name="windowIndex"/>, or null when none claims it. The M4 child
    /// wire carries both the window index and the slot, so two concurrent subwindows that both
    /// own slot 0 each dispatch to their own handler.
    /// </summary>
    private static OpenHarmonyHybridWebViewHandler? ChildHandlerForWindow(int windowIndex, int slot)
    {
        OpenHarmonyHybridWebViewHandler? found = null;
        lock (s_handlers)
        {
            foreach (OpenHarmonyHybridWebViewHandler handler in s_handlers)
            {
                if (OpenHarmonyChildWeb.IsApplicable(handler._overlayWindowId)
                    && OpenHarmonyChildWeb.WindowIndexOf(handler._overlayWindowId) == windowIndex
                    && handler._overlaySlot == slot)
                {
                    if (found is not null)
                    {
                        return null;
                    }
                    found = handler;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Dispatches one __hwvInvokeDotNet invocation to <see cref="IHybridWebView.Invoker"/> and
    /// returns the DotNetInvokeResult JSON the stock hybridwebview.js expects. Never throws:
    /// every failure becomes an error payload the page rejects with.
    /// </summary>
    internal async Task<string> InvokeDotNetAsync(string methodName, string argsJson)
    {
        if (string.IsNullOrEmpty(methodName))
        {
            return ErrorPayload(new InvalidOperationException("the invocation did not provide a method name"));
        }
        string[]? paramValues = null;
        if (!string.IsNullOrEmpty(argsJson) && argsJson != "[]")
        {
            try
            {
                paramValues = JsonSerializer.Deserialize(argsJson, OpenHarmonySliceJsonContext.Default.StringArray);
            }
            catch (JsonException ex)
            {
                return ErrorPayload(new InvalidOperationException(
                    $"the invocation parameters were not a JSON string array: {ex.Message}"));
            }
        }
        if (VirtualView is not { } webView)
        {
            return ErrorPayload(new InvalidOperationException("the HybridWebView is not connected"));
        }
        try
        {
            // Controls.HybridWebView.Invoker throws when no InvokeJavaScriptTarget was set;
            // other implementations may return null. Both become an error payload.
            Task<string?> invocation = webView.Invoker.InvokeMethodAsync(methodName, paramValues);
            Task completed = await Task.WhenAny(invocation, Task.Delay(s_dotNetInvokeTimeout)).ConfigureAwait(false);
            if (completed != invocation)
            {
                return ErrorPayload(new TimeoutException(
                    $"the .NET method '{methodName}' did not complete within {s_dotNetInvokeTimeout.TotalSeconds:0} seconds"));
            }
            return SuccessPayload(await invocation.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ErrorPayload(ex);
        }
    }

    private static void SendInvokeResult(int requestId, string payload)
    {
        try
        {
            InvokeResultNative(requestId, payload);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_invokeUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] hybrid invoke bridge unavailable (no host library)");
        }
        // Tests and diagnostics observe the payload even when the native export is absent.
        HybridInvokeResultSent?.Invoke(requestId, payload);
    }

    /// <summary>Result shape the stock invokeDotNet code reads (see HybridWebViewHandler).</summary>
    private static string SuccessPayload(string? jsonResult)
        => JsonSerializer.Serialize(new DotNetInvokeResult
        {
            Result = jsonResult,
            IsJson = jsonResult is not null,
        }, OpenHarmonySliceJsonContext.Default.DotNetInvokeResult);

    private static string ErrorPayload(Exception ex)
        => JsonSerializer.Serialize(new DotNetInvokeResult
        {
            IsError = true,
            ErrorMessage = ex.Message,
            ErrorType = ex.GetType().Name,
            ErrorStackTrace = ex.StackTrace,
        }, OpenHarmonySliceJsonContext.Default.DotNetInvokeResult);

    internal sealed class DotNetInvokeResult
    {
        [JsonPropertyName("Result")]
        public string? Result { get; init; }

        [JsonPropertyName("IsJson")]
        public bool IsJson { get; init; }

        [JsonPropertyName("IsError")]
        public bool IsError { get; init; }

        [JsonPropertyName("ErrorMessage")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("ErrorType")]
        public string? ErrorType { get; init; }

        [JsonPropertyName("ErrorStackTrace")]
        public string? ErrorStackTrace { get; init; }
    }

    /// <summary>Payload descriptor the shell consumes from the "hybrid" web command.</summary>
    internal sealed class HybridAssetsConfig
    {
        [JsonPropertyName("base")]
        public string Base { get; init; } = string.Empty;

        [JsonPropertyName("root")]
        public string Root { get; init; } = string.Empty;

        [JsonPropertyName("defaultFile")]
        public string DefaultFile { get; init; } = string.Empty;

        /// <summary>Per-registration document id (B2/B3), echoed in the shell message envelope.</summary>
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        /// <summary>Overlay slot this registration owns (MULTI-OVL); the shell loads the page there.</summary>
        [JsonPropertyName("slot")]
        public int Slot { get; init; } = -1;
    }

    // Controls.HybridWebView raises these commands through IElementHandler.Invoke/InvokeAsync;
    // each command carries a TaskCompletionSource the caller awaits, so every branch must
    // complete it (an unmapped command would leave the caller waiting forever).
    public override void Invoke(string command, object? args)
    {
        switch (command)
        {
            case nameof(IHybridWebView.EvaluateJavaScriptAsync) when args is EvaluateJavaScriptAsyncRequest request:
                _ = CompleteEvaluateAsync(request, UseOverlaySlot(), _overlayWindowId);
                return;
            case nameof(IHybridWebView.SendRawMessage) when args is HybridWebViewRawMessage message:
                if (message.Message is { } rawMessage)
                {
                    SendRawMessage(rawMessage);
                }
                return;
            case nameof(IHybridWebView.InvokeJavaScriptAsync) when args is HybridWebViewInvokeJavaScriptRequest request:
                _ = CompleteInvokeAsync(request);
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    /// <summary>
    /// MULTI-OVERLAY-FULL use gate: ensures a live claim before a per-overlay command and
    /// replays the registration (load/attach) when the claim is a restore; answers the claim.
    /// </summary>
    private int UseOverlaySlot()
    {
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

    /// <summary>Evaluates a script on the shell's ArkWeb page; null when no page or host answers.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script)
        => OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(script, UseOverlaySlot(), _overlayWindowId);

    /// <summary>
    /// Delivers a raw message to the hybrid page. The stock HybridWebView JavaScript receives
    /// host messages through window.external.receiveMessage; the fallback dispatches the same
    /// HybridWebViewMessageReceived event that the stock script raises. The eval only lands in
    /// this handler's own document (B3): it checks the shell-stamped marker window.__ohHybridId
    /// first and reports "skip" when the loaded document is not this handler's page.
    /// </summary>
    public void SendRawMessage(string rawMessage)
    {
        if (string.IsNullOrEmpty(rawMessage))
        {
            return;
        }
        _ = SendRawMessageCoreAsync(JsonSerializer.Serialize(rawMessage, OpenHarmonySliceJsonContext.Default.String));
    }

    /// <summary>
    /// Sends one raw message through a marker-checked eval. A skipped delivery (the document no
    /// longer carries this handler's id: a reload before the next stamp, another handler's page
    /// or a foreign navigation) is logged; the off-device no-host path evaluates to null and
    /// stays silent.
    /// </summary>
    private async Task SendRawMessageCoreAsync(string json)
    {
        string? result = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(
            "(function(id,m){" +
            "if(window.__ohHybridId!==id){return 'skip';}" +
            "if(window.external&&typeof window.external.receiveMessage==='function')" +
            "{window.external.receiveMessage(m);}" +
            "else{window.dispatchEvent(new CustomEvent('HybridWebViewMessageReceived',{detail:{message:m}}));}" +
            "return 'ok';})(" +
            JsonSerializer.Serialize(_pageId, OpenHarmonySliceJsonContext.Default.String) + "," + json + ")",
            UseOverlaySlot(), _overlayWindowId).ConfigureAwait(false);
        if (result is not null && result.Trim().Trim('"') == "skip")
        {
            OpenHarmonyBridge.WriteStatus(
                "[maui] hybrid raw message skipped: the loaded document is not this handler's page");
        }
    }

    /// <summary>
    /// Routes a shell dotnetHost payload to the HybridWebView protocol. The payload must carry
    /// the shell's document-origin envelope (B1/B2): the reported origin has to be the
    /// HybridWebView page origin and the reported document id has to be the registration id of
    /// a connected handler (the id the shell stamped into that handler's page). Messages that
    /// do not match are rejected and logged. Matching messages are scoped to that one handler:
    /// __InvokeJavaScriptCompleted/__InvokeJavaScriptFailed complete JS invocations started by
    /// it, and every other payload (with or without the stock __RawMessage prefix) is a raw
    /// message for it alone.
    /// </summary>
    internal static void OnJsMessage(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }
        if (!TryParseOriginEnvelope(payload, out Uri? origin, out string documentId, out string message))
        {
            OpenHarmonyBridge.WriteStatus(
                "[maui] hybrid message rejected: missing or malformed document-origin envelope");
            return;
        }
        OpenHarmonyHybridWebViewHandler? handler = ResolveMessageHandler(origin, documentId);
        if (handler is null)
        {
            OpenHarmonyBridge.WriteStatus(
                "[maui] hybrid message rejected: the reported origin/document does not match a HybridWebView page");
            return;
        }
        if (message.Length > MaxPagePayloadLength)
        {
            RejectOversizedPayload(handler, documentId, message);
            return;
        }
        if (message.StartsWith(InvokeCompletedPrefix, StringComparison.Ordinal) ||
            message.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal))
        {
            CompleteInvoke(handler, documentId, message);
            return;
        }
        string raw;
        try
        {
            raw = message.StartsWith(RawMessagePrefix, StringComparison.Ordinal)
                ? Uri.UnescapeDataString(message.Substring(RawMessagePrefix.Length))
                : message;
        }
        catch (UriFormatException)
        {
            // UnescapeDataString's contract still allows UriFormatException for a malformed escape
            // sequence; never let that cross back into the native callback, drop the message.
            OpenHarmonyBridge.WriteStatus("[maui] hybrid raw message rejected: invalid escape sequence");
            return;
        }
        handler.VirtualView?.RawMessageReceived(raw);
    }

    /// <summary>
    /// Parses the document-origin envelope the shell prepends:
    /// <c>__OHORIGIN|&lt;document url&gt;|&lt;document id&gt;\n&lt;payload&gt;</c>. False for a
    /// missing prefix, a missing newline, an empty payload or a url that is not an absolute URI.
    /// Shared with the Blazor handler (B1), which validates the Blazor origin and its own id.
    /// </summary>
    internal static bool TryParseOriginEnvelope(string payload, out Uri? origin, out string documentId, out string message)
    {
        origin = null;
        documentId = string.Empty;
        message = string.Empty;
        if (!payload.StartsWith(OriginEnvelopePrefix, StringComparison.Ordinal))
        {
            return false;
        }
        int newline = payload.IndexOf('\n', StringComparison.Ordinal);
        if (newline < 0)
        {
            return false;
        }
        string header = payload.Substring(OriginEnvelopePrefix.Length, newline - OriginEnvelopePrefix.Length);
        int separator = header.LastIndexOf('|', StringComparison.Ordinal);
        string url = separator >= 0 ? header.Substring(0, separator) : header;
        documentId = separator >= 0 ? header.Substring(separator + 1) : string.Empty;
        message = payload.Substring(newline + 1);
        return message.Length > 0
            && Uri.TryCreate(url, UriKind.Absolute, out origin);
    }

    /// <summary>
    /// Finds the one connected handler a message belongs to: its origin has to be the
    /// HybridWebView page origin and its per-registration id has to be the document id the
    /// shell reported. Null (reject) for a foreign origin, an unstamped document (empty id) or
    /// an unknown id - the message is then not delivered to any handler.
    /// </summary>
    private static OpenHarmonyHybridWebViewHandler? ResolveMessageHandler(Uri? origin, string documentId)
    {
        if (origin is null || documentId.Length == 0 || !IsHybridPageOrigin(origin))
        {
            return null;
        }
        lock (s_handlers)
        {
            foreach (OpenHarmonyHybridWebViewHandler handler in s_handlers)
            {
                if (handler._pageId == documentId)
                {
                    return handler;
                }
            }
        }
        return null;
    }

    /// <summary>True when the reported document url is on the HybridWebView page origin (B2).</summary>
    private static bool IsHybridPageOrigin(Uri origin)
        => string.Equals(origin.Scheme, s_appOriginUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.IdnHost, s_appOriginUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && origin.Port == s_appOriginUri.Port;

    /// <summary>
    /// Rejects one page payload above <see cref="MaxPagePayloadLength"/> that was already matched
    /// to <paramref name="handler"/> and its document id: a pending JS invocation whose result is
    /// oversized is completed with a clear error (the page's promise rejects) instead of waiting
    /// for its timeout, and every other oversized payload is dropped with a status log. A
    /// completion that does not belong to this handler/document never touches a pending task.
    /// Never throws.
    /// </summary>
    private static void RejectOversizedPayload(OpenHarmonyHybridWebViewHandler handler, string documentId, string payload)
    {
        string prefix = payload.StartsWith(InvokeCompletedPrefix, StringComparison.Ordinal) ? InvokeCompletedPrefix
            : payload.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal) ? InvokeFailedPrefix
            : string.Empty;
        if (prefix.Length > 0)
        {
            int separator = payload.IndexOf('|', prefix.Length, StringComparison.Ordinal);
            if (separator > prefix.Length)
            {
                string taskId = payload.Substring(prefix.Length, separator - prefix.Length);
                if (s_invokeRequests.TryGetValue(taskId, out PendingInvoke? pending) &&
                    ReferenceEquals(pending.Handler, handler) &&
                    documentId == pending.Handler.PageDocumentId &&
                    s_invokeRequests.TryRemove(taskId, out pending))
                {
                    pending.Source.TrySetException(new InvalidOperationException(
                        $"the JavaScript invocation payload exceeds the {MaxPagePayloadLength} character page payload cap"));
                }
            }
        }
        OpenHarmonyBridge.WriteStatus(
            $"[maui] hybrid page payload rejected: {payload.Length} characters exceed the {MaxPagePayloadLength} character cap");
    }

    private static async Task CompleteEvaluateAsync(EvaluateJavaScriptAsyncRequest request, int slot, string? windowId)
    {
        string? result = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(request.Script, slot, windowId).ConfigureAwait(false);
        // An empty string stands in for "the platform had no result" (HybridWebView returns null
        // for null, "null" and "undefined" results alike).
        request.TrySetResult(result ?? string.Empty);
    }

    private async Task CompleteInvokeAsync(HybridWebViewInvokeJavaScriptRequest request)
    {
        try
        {
            request.TrySetResult(await InvokeJavaScriptAsyncCore(request).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            request.TrySetException(ex);
        }
    }

    /// <summary>
    /// Starts window.HybridWebView.__InvokeJavaScript and waits for the page to post the result
    /// back through dotnetHost (__InvokeJavaScriptCompleted|taskId|json). The pending entry
    /// records this handler and its document id, so only this handler's own page can complete it
    /// (B2). The stock MAUI JavaScript already speaks this protocol.
    /// </summary>
    private async Task<object?> InvokeJavaScriptAsyncCore(HybridWebViewInvokeJavaScriptRequest request)
    {
        string argList = string.Empty;
        if (request.ParamValues is { } values)
        {
            argList = string.Join(", ", values.Select((value, index) =>
                value is null ? "null" : JsonSerializer.Serialize(value, request.ParamJsonTypeInfos![index]!)));
        }
        string taskId = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_invokeRequests[taskId] = new PendingInvoke(source, this);
        _ = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(
            $"window.HybridWebView.__InvokeJavaScript({JsonSerializer.Serialize(taskId, OpenHarmonySliceJsonContext.Default.String)}, " +
            $"{JsonSerializer.Serialize(request.MethodName, OpenHarmonySliceJsonContext.Default.String)}, [{argList}])",
            UseOverlaySlot(), _overlayWindowId).ConfigureAwait(false);
        if (!OpenHarmonyWebViewHandler.IsJavaScriptBridgeAvailable)
        {
            // No host library: the page never received the kickoff, so do not wait for a reply.
            s_invokeRequests.TryRemove(taskId, out _);
            return null;
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_invokeTimeout)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_invokeRequests.TryRemove(taskId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] hybrid JavaScript invocation timed out");
            return null;
        }
        string? result = await source.Task.ConfigureAwait(false);
        if (string.IsNullOrEmpty(result) || result == "null" || result == "undefined")
        {
            return null;
        }
        if (request.ReturnTypeJsonTypeInfo is null)
        {
            return null;
        }
        return JsonSerializer.Deserialize(result, request.ReturnTypeJsonTypeInfo);
    }

    private static void CompleteInvoke(OpenHarmonyHybridWebViewHandler handler, string documentId, string payload)
    {
        bool failed = payload.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal);
        string content = payload.Substring(failed ? InvokeFailedPrefix.Length : InvokeCompletedPrefix.Length);
        int separator = content.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }
        string taskId = content.Substring(0, separator);
        string result = content.Substring(separator + 1);
        if (!s_invokeRequests.TryGetValue(taskId, out PendingInvoke? pending))
        {
            return;
        }
        // B2: the completion is only accepted from the handler that started the invocation and
        // with the document id the shell stamped for that handler's page. A page (or another
        // handler's page) that harvested the task id cannot complete it.
        if (!ReferenceEquals(pending.Handler, handler) || documentId != pending.Handler.PageDocumentId)
        {
            OpenHarmonyBridge.WriteStatus("[maui] hybrid invoke completion rejected: document/handler mismatch");
            return;
        }
        if (s_invokeRequests.TryRemove(taskId, out pending))
        {
            if (failed)
            {
                pending.Source.TrySetException(new InvalidOperationException($"JavaScript invocation failed: {result}"));
            }
            else
            {
                pending.Source.TrySetResult(result);
            }
        }
    }
}
