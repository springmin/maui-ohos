// MAUI application host for OpenHarmony: creates the app window through IApplication, keeps
// the visual tree's handlers connected to our platform handlers, and drives rendering from
// the platform contract (surface, touch, frame ticks, lifecycle).
//
// M2 (MULTIWINDOW-L): the host is windowed. The primary window keeps the historical
// single-surface path below (zero change); a window adopted through OpenWindow when the shell
// has reported a second surface gets its own OpenHarmonyWindowHost (per-window
// surface/renderer/size/input), bound by window id. The bridge's own events are untagged, so
// in M2 the untagged stream drives the primary window only; the window-id routing entry points
// (RouteSurface/RouteTouch/RouteFrame) are the adapter the host's per-window dispatch (M2-ow)
// and the shell's sub-window events (M3) call.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyMauiAppHost
{
    private readonly MauiContext _context;
    private readonly OpenHarmonyWindowRenderer _renderer;
    private readonly OpenHarmonyWindowSurface _surface;
    private IWindow? _window;
    private int _width;
    private int _height;
    private bool _dirty = true;
    private bool _created;
    private bool _activated;
    // The platform Create event can be delivered before Run creates the window; it is then
    // completed (Created, then Activated) when the window exists.
    private bool _createReceived;
    // Serializes every entry point that reads or mutates the element tree. The managed app runs
    // on the host's launch thread while the ArkTS shell delivers surface/frame/lifecycle/touch
    // callbacks on the shell thread; without this, a JIT-slowed startup lets a callback wire
    // handlers mid-connect (device WX-JIT: "Handler is already being set elsewhere",
    // "PlatformView cannot be null here", non-concurrent collection corruption).
    private readonly object _sync = new();
    // Platform callbacks stay out of the tree until Run/TryAdoptWindow finished connecting it,
    // so a callback can never observe a half-connected tree.
    private bool _ready;
    // T21: font-scale reports that re-arranged the tree (the interaction-suite seam).
    internal int SystemFontScaleRelayouts { get; private set; }
    // M2: one per-window state owner (surface + renderer + size + input) for every window beyond
    // the primary one, plus the surface reports that arrived for a window id no window has
    // adopted yet ("无窗时保持记录态"; bound at adoption). Guarded by _sync.
    private readonly Dictionary<string, OpenHarmonyWindowHost> _secondaryWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenHarmonySurfaceInfo> _pendingSurfaces = new(StringComparer.Ordinal);
    private readonly List<string> _pendingSurfaceOrder = new();
    // M3: realized windows waiting for the shell's subwindow XComponent surface, keyed by the
    // id the shell was asked to bring up. Guarded by _sync.
    private readonly Dictionary<string, IWindow> _awaitingWindows = new(StringComparer.Ordinal);

    public OpenHarmonyMauiAppHost(IServiceProvider services)
    {
        // NativeAOT launch ordering: the host dlopens the application library to probe its
        // openharmony_app_main export *before* it publishes the launch context (setenv
        // OHOS_HOST_APP_CONTEXT / the app handle), so the bridge's module initializer may have
        // cached an empty context. Re-attach here, now that the app thread runs with the launch
        // context published: Attach refreshes the snapshot and is idempotent (a no-op on the JIT
        // route, whose context is already current). Without this, OpenHarmonyBridge.Context
        // (AppDir/FilesDir) stays empty in AOT apps and every registration that validates
        // against it (a BlazorWebView/HybridWebView content root, a WASM site, WriteStatus)
        // degrades silently.
        OpenHarmonyBridge.Attach();
        _context = new MauiContext(services);
        // Deep links: subscribe to the shell's activation transport (cold-start want and
        // onNewWant) before any window exists, so an activation that arrives early is queued
        // and applied as soon as Run/lifecycle provides a Shell or NavigationPage target.
        OpenHarmonyAppLinks.Install(services);
        // Keystore results arrive from the ArkTS sink through the bridge.
        OpenHarmonyBridge.KeystoreResult += (requestId, rc, data) => OpenHarmonyKeystore.Complete(requestId, rc, data);
        OpenHarmonyBridge.PickerResult += (requestId, rc, name, data) => OpenHarmonyPickerClient.Complete(requestId, rc, name, data);
        OpenHarmonyBridge.WebEvent += (state, url) => OpenHarmonyWebViewHandler.OnPageEvent(state, url);
        // While a prompt overlay is open the keyboard text edits it instead of an Entry.
        OpenHarmonyBridge.TextInput += text => { if (OpenHarmonyAlertHost.Current?.Kind == OpenHarmonyAlertKind.Prompt) { OpenHarmonyAlertHost.PromptAppend(text); } };
        OpenHarmonyBridge.TextSubmitted += () => { if (OpenHarmonyAlertHost.Current?.Kind == OpenHarmonyAlertKind.Prompt) { OpenHarmonyAlertHost.Hide(); OpenHarmonyAlertHost.Current?.Complete(true); } };
        // Bindable objects created outside the service scope (TabbedPage/MultiPage, ...) resolve
        // their dispatcher through this provider.
        Microsoft.Maui.Dispatching.DispatcherProvider.SetCurrent(
            new OpenHarmonyDispatcherProvider(services.GetRequiredService<Microsoft.Maui.Dispatching.IDispatcher>()));
        _renderer = services.GetRequiredService<OpenHarmonyWindowRenderer>();
        _surface = services.GetRequiredService<OpenHarmonyWindowSurface>();
        // The primary renderer draws through the primary surface (the static test seams still
        // win); a secondary window's renderer is bound to its own surface at construction.
        _renderer.WindowSurface = _surface;

        OpenHarmonyBridge.SurfaceChanged += OnSurfaceArranged;

    // MULTIWINDOW-L M3: production consumption of the host's window-id tagged channel. The
    // primary window already rides the untagged events above (zero change), so the tagged
    // subscription ignores "main" and routes every other id into the per-window paths below:
    // the shell's subwindow XComponent (id = the requested session id) feeds its managed window
    // without any per-app wiring.
    OpenHarmonyBridge.WindowSurfaceChanged += (id, info) =>
    {
        if (id != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            RouteSurface(id, info);
        }
    };
    OpenHarmonyBridge.WindowTouch += (id, args) =>
    {
        if (id != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            RouteTouch(id, args);
        }
    };
    OpenHarmonyBridge.WindowFrame += (id, args) =>
    {
        if (id != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            RouteFrame(id);
        }
    };

        OpenHarmonyBridge.Touch += args =>
        {
            bool handled = args.Action switch
            {
                OpenHarmonyTouchAction.Down => HandleTouch(true, false, args.X, args.Y, args.PointerId),
                OpenHarmonyTouchAction.Up => HandleTouch(false, true, args.X, args.Y, args.PointerId),
                OpenHarmonyTouchAction.Move => HandleMoveCore(args.X, args.Y, args.PointerId),
                OpenHarmonyTouchAction.Cancel => HandleCancel(args.X, args.Y),
                _ => false,
            };
            if (handled)
            {
                _dirty = true;
            }
        };

        OpenHarmonyBridge.RedrawRequested += () =>
        {
            lock (_sync)
            {
                _dirty = true;
            }
        };

        // T21: the shell reports the system font scale (ArkTS Configuration.fontSizeScale ->
        // host.notifyFontScale -> this listener). A changed value re-measures/re-arranges the
        // whole tree and repaints; the scale alone cannot relayout.
        OpenHarmonySystemFontScale.Changed += OnSystemFontScaleChanged;

        OpenHarmonyBridge.Frame += _ => OnFrameTick();

        OpenHarmonyBridge.LifecycleChanged += e =>
        {
            // MAUI's IWindow lifecycle expects a platform window handler; until this slice
            // provides one the events are best-effort (an exception must not stop the app).
            try
            {
                lock (_sync)
                {
                    switch (e)
                    {
                        case OpenHarmonyLifecycleEvent.Create:
                            // MAUI's window lifecycle starts with Created (the platform window now
                            // exists); the platform event carries the activation, so raise Created
                            // first and only once. Before Run the window does not exist yet, so Run
                            // completes the activation for this event.
                            _createReceived = true;
                            EnsureWindowCreated();
                            EnsureWindowActivated();
                            OpenHarmonyAppLinks.OnHostReady();
                            break;
                        case OpenHarmonyLifecycleEvent.Foreground:
                            // Heartbeat: the shell may have reset the window title while the app was
                            // backgrounded and Window.Title only pushes on change (W9D §3). Pushed
                            // before Resumed so a lifecycle guard cannot skip the chrome.
                            OpenHarmonyWindowHandler.RepublishWindowChrome(_window);
                            _window?.Resumed();
                            OpenHarmonyAppLinks.OnHostReady();
                            break;
                        case OpenHarmonyLifecycleEvent.Background:
                            _window?.Stopped();
                            break;
                        case OpenHarmonyLifecycleEvent.Destroy:
                            _window?.Destroying();
                            break;
                    }
                }
                OpenHarmonyBridge.WriteStatus($"[maui] lifecycle {e} (window={_window?.GetType().Name})");
            }
            catch (Exception ex)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] lifecycle {e} ignored: {ex.GetType().Name}: {ex.Message}");
            }
        };
    }

    public IWindow? Window => _window;

    /// <summary>The MauiContext the host connects handlers with (one per process in M2;
    /// window-scoped contexts are part of M4).</summary>
    internal MauiContext Context => _context;

    /// <summary>The view to render: the top modal page when one is pushed, else the window content.</summary>
    private IView? RootView
    {
        get
        {
            if (_window is Microsoft.Maui.Controls.Window window &&
                window.Navigation.ModalStack.Count > 0 &&
                window.Navigation.ModalStack[^1] is IView modal)
            {
                return modal;
            }
            return _window?.Content as IView;
        }
    }

    /// <summary>Creates the application window and connects the visual tree's handlers.</summary>
    public void Run(IApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        // The ArkTS shell may report the device theme before the application exists; apply it now.
        OpenHarmonyTheme.Attach(application as Microsoft.Maui.Controls.Application);
        // Application.Handler comes first so Application.Windows and the application-level
        // commands (OpenWindow/CloseWindow/ActivateWindow/Quit) are live before any window.
        AttachApplicationHandler(application);
        _window = application.CreateWindow(null) ?? application.Windows.FirstOrDefault();
        if (_window is null)
        {
            OpenHarmonyBridge.WriteStatus("[maui] application did not create a window");
            return;
        }
        OpenHarmonyHandlerConnector.Context = _context;
        lock (_sync)
        {
            // The tree is connected before platform callbacks are allowed in; a shell
            // surface/frame callback that arrived during startup blocks here instead of
            // wiring handlers mid-connect.
            OpenHarmonyHandlerConnector.ConnectTree(_window);
            OpenHarmonyHandlerConnector.ConnectTree(_window.Content);
            _ready = true;
            if (_width > 0 && _height > 0)
            {
                Arrange(_width, _height);
            }
        }
        OpenHarmonyBridge.WriteStatus($"[maui] window created ({_window.GetType().Name}), content={_window.Content?.GetType().Name}");
        // The platform's Create lifecycle event activates the window (and may arrive before Run
        // when the shell is fast); Created must precede it either way. When Create arrived first
        // the handler had no window yet, so the activation is completed here.
        EnsureWindowCreated();
        _dirty = true;
        if (_createReceived)
        {
            EnsureWindowActivated();
        }
        // A cold-start deep link was queued while no navigation target existed; the window (and
        // its Shell/NavigationPage) now exists, so retry it.
        OpenHarmonyAppLinks.OnHostReady();
    }

    /// <summary>
    /// Raises <see cref="IWindow.Created"/> exactly once, as soon as the window exists. MAUI's
    /// window throws on a second Created, and the platform lifecycle event carries only the
    /// activation, so this is the single entry point that starts the MAUI window lifecycle
    /// (called from <see cref="Run"/> and from the platform Create event, whichever is first).
    /// </summary>
    private void EnsureWindowCreated()
    {
        if (_created || _window is null)
        {
            return;
        }
        _created = true;
        _window.Created();
    }

    /// <summary>
    /// Raises <see cref="IWindow.Activated"/> exactly once per window: the platform Create event
    /// carries the activation, and a window adopted later (through
    /// <see cref="OpenHarmonyApplicationHandler"/>'s OpenWindow) is activated directly, so the
    /// guard keeps a repeated platform event from raising Activated twice.
    /// </summary>
    private void EnsureWindowActivated()
    {
        if (_activated || _window is null)
        {
            return;
        }
        _activated = true;
        _window.Activated();
    }

    /// <summary>
    /// Attaches the OpenHarmony <see cref="IApplication"/> handler before any window is created,
    /// so <c>Application.Handler</c> is non-null and the application-level commands are live from
    /// the start. An application that already has a handler (a custom bootstrap) keeps it.
    /// </summary>
    private void AttachApplicationHandler(IApplication application)
    {
        if (application.Handler is OpenHarmonyApplicationHandler attached)
        {
            attached.Host = this;
            return;
        }
        if (application.Handler is not null)
        {
            return;
        }
        var handler = new OpenHarmonyApplicationHandler();
        handler.SetMauiContext(_context);
        application.Handler = handler;
        handler.Host = this;
        OpenHarmonyBridge.WriteStatus("[maui] application handler attached");
    }

    /// <summary>
    /// Makes <paramref name="window"/> the host's single live window: connects the slice handlers,
    /// arranges it for the last reported surface size, raises Created and (the platform is already
    /// foregrounded when a window is adopted after startup) Activated, and starts rendering.
    /// Returns false when another window is already live.
    /// </summary>
    internal bool TryAdoptWindow(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null)
        {
            // One live window: the same window is already adopted, a different one cannot be shown.
            return ReferenceEquals(_window, window);
        }
        _window = window;
        _created = false;
        _activated = false;
        OpenHarmonyHandlerConnector.Context = _context;
        lock (_sync)
        {
            OpenHarmonyHandlerConnector.ConnectTree(_window);
            OpenHarmonyHandlerConnector.ConnectTree(_window.Content);
            _ready = true;
        }
        OpenHarmonyBridge.WriteStatus(
            $"[maui] window adopted ({_window.GetType().Name}), content={_window.Content?.GetType().Name}");
        EnsureWindowCreated();
        EnsureWindowActivated();
        if (_width > 0)
        {
            Arrange(_width, _height);
        }
        _dirty = true;
        // The adopted window may be the first navigation target a queued deep link can use.
        OpenHarmonyAppLinks.OnHostReady();
        return true;
    }

    /// <summary>
    /// Drops the host's reference to a window the application handler closed. Destroying already
    /// removed it from Application.Windows and disconnected its handler; the shell's window stays
    /// open, so the next OpenWindow can adopt a window on the same surface. A secondary window's
    /// session is removed; the primary window's slot stays (its surface keeps the last size).
    /// </summary>
    internal void NotifyWindowClosed(IWindow window)
    {
        string? shellCloseId = null;
        lock (_sync)
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                _created = false;
                _activated = false;
                _dirty = true;
                OpenHarmonyBridge.WriteStatus("[maui] window closed; the host has no live window");
                return;
            }
            string? closedId = null;
            foreach (KeyValuePair<string, OpenHarmonyWindowHost> entry in _secondaryWindows)
            {
                if (ReferenceEquals(entry.Value.Window, window))
                {
                    closedId = entry.Key;
                    break;
                }
            }
            if (closedId is not null)
            {
                OpenHarmonyWindowHost closed = _secondaryWindows[closedId];
                _secondaryWindows.Remove(closedId);
                closed.Reset();
                OpenHarmonyBridge.WriteStatus($"[maui] window '{closedId}' closed");
                // M3: a closed secondary window owns a shell child; ask the shell to destroy it so
                // the XComponent unregisters and the surface/registry are reclaimed (the shell's
                // Closed report and the Destroyed surface then find no live session and no-op).
                shellCloseId = closedId;
            }
            else
            {
                // M3: a window closed before its requested surface came up cancels the request too.
                foreach (KeyValuePair<string, IWindow> entry in _awaitingWindows)
                {
                    if (ReferenceEquals(entry.Value, window))
                    {
                        shellCloseId = entry.Key;
                        break;
                    }
                }
                if (shellCloseId is not null)
                {
                    _awaitingWindows.Remove(shellCloseId);
                }
            }
            // A window the host never adopted is a no-op (the legacy single-window contract).
        }
        if (shellCloseId is not null)
        {
            try
            {
                OpenHarmonySubWindow.Close();
            }
            catch (Exception ex)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] subwindow close request failed: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// True while an <see cref="IApplication.OpenWindow"/> request can be served: either no
    /// window is live yet (the requested window becomes the primary one on the shell's main
    /// surface) or the shell already reported a second surface (<see cref="RouteSurface"/>) that
    /// a secondary window can bind to.
    /// </summary>
    internal bool CanOpenWindow
    {
        get { lock (_sync) { return _window is null || _pendingSurfaces.Count > 0; } }
    }

    /// <summary>
    /// Test/embedding seam for the M3 deferred OpenWindow path: when set, the host asks this
    /// delegate (instead of the shell's subwindow sink) to bring up the surface id. The headless
    /// suite drives the request/answer cycle with it.
    /// </summary>
    internal Func<string, bool>? SubWindowSurfaceRequester { get; set; }

    /// <summary>
    /// True when the host can ask the shell to create a subwindow surface for a realized window
    /// (MULTIWINDOW-L M3): the shell registered its subwindow sink, or a test seam stands in.
    /// </summary>
    internal bool CanRequestWindow
    {
        get
        {
            if (SubWindowSurfaceRequester is not null)
            {
                return true;
            }
            try
            {
                return OpenHarmonySubWindow.IsSupported;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Adopts a window for an OpenWindow request: with no live window the requested window takes
    /// the primary slot (the historical path); with a live one it binds to the next recorded
    /// secondary surface and gets its own per-window state owner. Returns the window id, or null
    /// when no surface is available (the caller keeps the current window, the documented
    /// single-surface degrade).
    /// </summary>
    internal string? TryOpenWindow(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            if (_window is null)
            {
                if (!TryAdoptWindow(window))
                {
                    return null;
                }
                return OpenHarmonyWindowSurface.PrimaryWindowId;
            }
            if (ReferenceEquals(_window, window))
            {
                return OpenHarmonyWindowSurface.PrimaryWindowId;
            }
            string? windowId = null;
            for (int i = 0; i < _pendingSurfaceOrder.Count; i++)
            {
                string candidate = _pendingSurfaceOrder[i];
                if (_pendingSurfaces.ContainsKey(candidate) && !_secondaryWindows.ContainsKey(candidate))
                {
                    windowId = candidate;
                    break;
                }
            }
            if (windowId is null)
            {
                return null;
            }
            var surface = new OpenHarmonyWindowSurface(windowId, attachToBridge: false);
            var host = new OpenHarmonyWindowHost(this, windowId, surface, new OpenHarmonyWindowRenderer(surface));
            _secondaryWindows.Add(windowId, host);
            if (_pendingSurfaces.Remove(windowId, out OpenHarmonySurfaceInfo? pending) && pending is not null)
            {
                // The surface came up before its window: bind it now, then adopt (which arranges
                // for the size the surface reported).
                _pendingSurfaceOrder.Remove(windowId);
                surface.OnSurface(pending);
            }
            host.Adopt(window);
            return windowId;
        }
    }

    /// <summary>
    /// M3 deferred window: with no second surface reported yet, asks the shell to bring up a
    /// subwindow XComponent for the realized window and parks the window until
    /// <see cref="RouteSurface"/> reports that id. The id is the lowest free "sub-N", so a close
    /// followed by a re-open reuses the same id (the shell re-registers it). Returns the id, or
    /// null when the shell could not be asked (the caller falls back to the documented decline).
    /// </summary>
    internal string? TryOpenWindowDeferred(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            // The shell carries one managed child (its create answers 801 for a second managed
            // surface id), so a deferred request while one surface is already awaiting or bound
            // can never be served; returning null lets the caller close the realized window
            // honestly instead of parking it for a surface that will not come (SEC-SCAN-5b).
            if (_awaitingWindows.Count > 0 || _secondaryWindows.Count > 0)
            {
                return null;
            }
            string? windowId = null;
            for (int n = 1; n <= 1024; n++)
            {
                string candidate = "sub-" + n;
                if (!_awaitingWindows.ContainsKey(candidate) &&
                    !_secondaryWindows.ContainsKey(candidate) &&
                    !_pendingSurfaces.ContainsKey(candidate))
                {
                    windowId = candidate;
                    break;
                }
            }
            if (windowId is null)
            {
                return null;
            }
            Func<string, bool>? requester = SubWindowSurfaceRequester;
            bool requested;
            try
            {
                requested = requester is not null
                    ? requester(windowId)
                    : OpenHarmonySubWindow.CreateManagedSurface(
                        windowId, "maui-child", 120, 160, 720, 480, null);
            }
            catch (Exception ex)
            {
                OpenHarmonyBridge.WriteStatus(
                    $"[maui] subwindow surface request failed: {ex.GetType().Name}");
                return null;
            }
            if (!requested)
            {
                return null;
            }
            _awaitingWindows.Add(windowId, window);
            OpenHarmonyBridge.WriteStatus($"[maui] OpenWindow requested subwindow surface '{windowId}' from the shell");
            return windowId;
        }
    }

    /// <summary>Number of realized windows waiting for their requested shell surface.</summary>
    internal int AwaitingWindowCount
    {
        get { lock (_sync) { return _awaitingWindows.Count; } }
    }

    /// <summary>Every live secondary window's state owner (registration order).</summary>
    internal IReadOnlyList<OpenHarmonyWindowHost> SecondaryWindows
    {
        get { lock (_sync) { return _secondaryWindows.Values.ToList(); } }
    }

    /// <summary>The live secondary window with this id, or null.</summary>
    internal OpenHarmonyWindowHost? FindSecondaryWindow(string windowId)
    {
        lock (_sync)
        {
            return _secondaryWindows.TryGetValue(windowId, out OpenHarmonyWindowHost? host) ? host : null;
        }
    }

    /// <summary>Number of surface reports recorded for a window that does not exist yet.</summary>
    internal int PendingSurfaceCount
    {
        get { lock (_sync) { return _pendingSurfaces.Count; } }
    }

    /// <summary>
    /// Routes one surface report of the host's window registry to the window it belongs to.
    /// The primary window's reports also arrive through the bridge's untagged event; this entry
    /// is how a secondary window's report reaches its session, and how off-device tests drive
    /// both. A report for a window that does not exist yet is recorded and bound by
    /// <see cref="TryOpenWindow"/> (the shell may bring the XComponent up first); a report for a
    /// window realized through the M3 deferred path (<see cref="TryOpenWindowDeferred"/>) is the
    /// shell answering that request, so the window binds (and draws its first frame) here. A
    /// Destroyed report closes the window's session. Returns true when a live window consumed
    /// the report.
    /// </summary>
    internal bool RouteSurface(string windowId, OpenHarmonySurfaceInfo info)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        ArgumentNullException.ThrowIfNull(info);
        IWindow? destroyedWindow = null;
        bool result;
        lock (_sync)
        {
            if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
            {
                _surface.OnSurface(info);
                OnSurfaceArranged(info);
                result = true;
            }
            else if (_secondaryWindows.TryGetValue(windowId, out OpenHarmonyWindowHost? host))
            {
                if (info.State == OpenHarmonySurfaceState.Destroyed)
                {
                    // The shell child (and its XComponent) went away: close the managed session so
                    // the application sees the window destroyed instead of a dead session.
                    _secondaryWindows.Remove(windowId);
                    destroyedWindow = host.Window;
                    host.Reset();
                    OpenHarmonyBridge.WriteStatus($"[maui] window '{windowId}' closed (surface destroyed)");
                }
                else
                {
                    host.Surface.OnSurface(info);
                }
                result = true;
            }
            else if (_awaitingWindows.TryGetValue(windowId, out IWindow? awaiting))
            {
                _awaitingWindows.Remove(windowId);
                if (info.State == OpenHarmonySurfaceState.Destroyed)
                {
                    OpenHarmonyBridge.WriteStatus($"[maui] window '{windowId}' surface destroyed before adoption");
                    // The parked window never saw a live surface: close it instead of leaving it
                    // in Application.Windows forever (the tail raises Destroying outside _sync).
                    destroyedWindow = awaiting;
                    result = false;
                }
                else
                {
                    // The shell answered the deferred request: bind the parked window to the new
                    // surface, adopt it and draw the first frame immediately (the frame tick follows).
                    var surface = new OpenHarmonyWindowSurface(windowId, attachToBridge: false);
                    var windowHost = new OpenHarmonyWindowHost(this, windowId, surface, new OpenHarmonyWindowRenderer(surface));
                    _secondaryWindows.Add(windowId, windowHost);
                    surface.OnSurface(info);
                    windowHost.Adopt(awaiting);
                    bool firstFrame = windowHost.Render();
                    OpenHarmonyBridge.WriteStatus(
                        $"[maui] window '{windowId}' bound to the shell subwindow surface {info.Width}x{info.Height} (first frame={firstFrame})");
                    result = true;
                }
            }
            else if (info.State == OpenHarmonySurfaceState.Destroyed)
            {
                // A destroyed surface for a window that does not exist is not a binding target:
                // drop any earlier record instead of leaving a dead surface for the next
                // OpenWindow to adopt.
                _pendingSurfaces.Remove(windowId);
                _pendingSurfaceOrder.Remove(windowId);
                result = false;
            }
            else
            {
                _pendingSurfaces[windowId] = info;
                if (!_pendingSurfaceOrder.Contains(windowId))
                {
                    _pendingSurfaceOrder.Add(windowId);
                }
                result = false;
            }
        }
        // The destroyed window's Destroying runs outside _sync: the event's handlers may touch
        // the application tree or dispatch, and none of that may run under the host lock.
        if (destroyedWindow is not null)
        {
            try
            {
                destroyedWindow.Destroying();
            }
            catch (Exception ex)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] window Destroying failed: {ex.GetType().Name}");
            }
        }
        return result;
    }

    /// <summary>Routes one touch/mouse stream event to its window (M3 feeds secondary input
    /// here; off-device tests drive both windows' input through it). Returns true when the
    /// window consumed the event.</summary>
    internal bool RouteTouch(string windowId, OpenHarmonyTouchEventArgs args)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        ArgumentNullException.ThrowIfNull(args);
        lock (_sync)
        {
            if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
            {
                bool handled = args.Action switch
                {
                    OpenHarmonyTouchAction.Down => HandleTouch(true, false, args.X, args.Y, args.PointerId),
                    OpenHarmonyTouchAction.Up => HandleTouch(false, true, args.X, args.Y, args.PointerId),
                    OpenHarmonyTouchAction.Move => HandleMoveCore(args.X, args.Y, args.PointerId),
                    OpenHarmonyTouchAction.Cancel => HandleCancel(args.X, args.Y),
                    _ => false,
                };
                if (handled)
                {
                    _dirty = true;
                }
                return handled;
            }
            return _secondaryWindows.TryGetValue(windowId, out OpenHarmonyWindowHost? host)
                && DispatchSecondaryTouch(host, args);
        }
    }

    /// <summary>Routes one frame tick to its window's renderer (off-device tests drive a
    /// secondary window's frame loop; the device's secondary frame routing is M3).</summary>
    internal bool RouteFrame(string windowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        lock (_sync)
        {
            if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
            {
                OnFrameTick();
                return true;
            }
            if (!_secondaryWindows.TryGetValue(windowId, out OpenHarmonyWindowHost? host))
            {
                return false;
            }
            host.OnFrame();
            return true;
        }
    }

    private static bool DispatchSecondaryTouch(OpenHarmonyWindowHost host, OpenHarmonyTouchEventArgs args)
    {
        bool handled = args.Action switch
        {
            OpenHarmonyTouchAction.Down => host.HandleTouch(true, false, args.X, args.Y, args.PointerId),
            OpenHarmonyTouchAction.Up => host.HandleTouch(false, true, args.X, args.Y, args.PointerId),
            OpenHarmonyTouchAction.Move => host.HandleMove(args.X, args.Y, args.PointerId),
            OpenHarmonyTouchAction.Cancel => host.HandleCancel(args.X, args.Y),
            _ => false,
        };
        if (handled)
        {
            host.MarkDirty();
        }
        return handled;
    }

    /// <summary>
    /// True for a surface report the host can arrange for: the first Created publish and every
    /// live Changed one (window resize, split, 2in1 free-window size change) with a positive
    /// size. Destroyed and the transient 0x0 report keep the last arranged frame.
    /// </summary>
    internal static bool CanArrangeSurface(OpenHarmonySurfaceInfo info)
        => info.State != OpenHarmonySurfaceState.Destroyed && info.Width > 0 && info.Height > 0;

    /// <summary>
    /// The primary window's surface reaction, shared by the bridge's untagged event and the
    /// window-id routing's "main" report: arrange/render for a usable size and report a live
    /// resize through the status line (the shell's dotnet-status poll carries it to hilog on a
    /// device).
    /// </summary>
    private void OnSurfaceArranged(OpenHarmonySurfaceInfo info)
    {
        if (CanArrangeSurface(info))
        {
            lock (_sync)
            {
                Arrange(info.Width, info.Height);
                _dirty = true;
                Render();
            }
            if (info.State == OpenHarmonySurfaceState.Changed)
            {
                // A0 multi-window form adaptation: a live resize/split/free-window size change
                // re-arranges the tree for the new surface (see CanArrangeSurface).
                // The status line lets the shell's dotnet-status poll carry the resize to
                // hilog on a device.
                OpenHarmonyBridge.WriteStatus($"[maui] window size {info.Width}x{info.Height}");
            }
        }
    }

    /// <summary>The primary window's frame tick: animation advance + dirty repaint.</summary>
    private void OnFrameTick()
    {
        lock (_sync)
        {
            if (!_ready)
            {
                return;
            }
            // Activity indicators keep animating: advance the shared angle and redraw.
            if (_renderer.HasAnimations(_window?.Content as IView))
            {
                OpenHarmonyView.AnimationAngle = (OpenHarmonyView.AnimationAngle + 24f) % 360f;
                _dirty = true;
            }
            if (_dirty)
            {
                _dirty = false;
                Render();
            }
        }
    }

    /// <summary>Measures/arranges the current window content for the given surface size.</summary>
    public void Arrange(int width, int height)
    {
        lock (_sync)
        {
            _width = width;
            _height = height;
            if (!_ready || _window?.Content is not IView content)
            {
                return;
            }
            ArrangeContent(_window, content, new Rect(0, 0, width, height));
        }
    }

    /// <summary>
    /// The shared arrange tail for one window: connect the content's handlers, mirror the
    /// surface size onto the virtual window (Window.Width/Height and SizeChanged become real
    /// values, like the other platforms' window handlers) and run the safe-area walk. The
    /// window's avoid area is applied per page/content view through SafeAreaEdges (see
    /// OpenHarmonySafeAreaArrange); the surface itself is arranged edge to edge.
    /// </summary>
    internal static void ArrangeContent(IWindow window, IView content, Rect bounds)
    {
        // MAUI measures/arranges through handlers; Page/ContentView have no platform handler
        // in this slice, so arrange the first descendant that has one.
        OpenHarmonyHandlerConnector.ConnectTree(content);
        window.FrameChanged(bounds);
        Thickness insets = OpenHarmonySafeArea.GetWindowInsets();
        OpenHarmonySafeAreaArrange.Arrange(content, bounds, bounds, insets);
        IView? root = FindArrangableRoot(content);
        if (root is not null && !ReferenceEquals(root, content))
        {
            OpenHarmonySafeAreaArrange.Arrange(root, bounds, bounds, insets);
        }
    }

    private static IView? FindArrangableRoot(IView view)
    {
        if (view.Handler is not null)
        {
            return view;
        }
        if (view is ILayout layout)
        {
            foreach (IView child in layout)
            {
                IView? found = FindArrangableRoot(child);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        if (view is IContentView contentView && contentView.PresentedContent is IView presented)
        {
            return FindArrangableRoot(presented);
        }
        return null;
    }

    public bool Render()
    {
        lock (_sync)
        {
            if (!_ready || _window?.Content is not IView content || _width <= 0)
            {
                return false;
            }
            return _renderer.Render(content, _width, _height);
        }
    }

    /// <summary>
    /// Re-arranges and repaints after the system font scale changed (T21). The re-arrange walks
    /// the tree again, so every handler re-measures through the generation-keyed text caches; the
    /// frame loop paints the dirty surface (the surface may be gone between reports, in which
    /// case only the dirty flag remains).
    /// </summary>
    private void OnSystemFontScaleChanged()
    {
        lock (_sync)
        {
            if (_ready && _width > 0 && _height > 0)
            {
                Arrange(_width, _height);
                SystemFontScaleRelayouts++;
            }
            _dirty = true;
        }
    }

    private bool _pinchWired;

    public bool HandleTouch(bool down, bool up, float x, float y)
        => HandleTouch(down, up, x, y, 0);

    /// <summary>
    /// Handles a press/release of one pointer. The pointer id comes from the shell's touch stream
    /// so multi-touch gestures keep their pointer identity (see the bridge's touch arguments).
    /// </summary>
    public bool HandleTouch(bool down, bool up, float x, float y, int pointerId)
    {
        lock (_sync)
        {
            EnsureInputWired();
            // N5: an active overlay that disables touch passthrough owns the press/release; the page
            // tree underneath is skipped so a control there never also receives the gesture.
            if (OpenHarmonyWindowOverlayHost.ShouldConsumeTouch(down, up))
            {
                return true;
            }
            return RootView is IView content && _renderer.HandleTouch(content, down, up, x, y, pointerId);
        }
    }

    /// <summary>Cancels the gesture stream (the shell reports a canceled touch): CancelInteraction.</summary>
    public bool HandleCancel(float x, float y)
    {
        lock (_sync)
        {
            EnsureInputWired();
            if (OpenHarmonyWindowOverlayHost.ShouldConsumeCancel())
            {
                return true;
            }
            return _renderer.HandleCancel(x, y);
        }
    }

    private void EnsureInputWired()
    {
        if (_pinchWired)
        {
            return;
        }
        _pinchWired = true;
        OpenHarmonyBridge.RegisterPinchListener();
        OpenHarmonyBridge.Pinch += OnPinch;
        OpenHarmonyAccessibility.SetActionHandler((nodeId, action) => HandleAccessibilityAction(nodeId, action));
    }

    /// <summary>
    /// Executes an accessibility action routed from the provider. It maps exactly - and only - the
    /// actions <see cref="OpenHarmonyAccessibility.ActionsFor"/> advertises: CLICK simulates a tap
    /// at the node centre, SCROLL_FORWARD/BACKWARD move an IScrollView or step an ISlider, and the
    /// textInput actions work on the node's Entry/Editor (COPY/CUT write the clipboard, PASTE
    /// reads it back at the caret, SELECT_TEXT selects the whole text).
    /// </summary>
    public bool HandleAccessibilityAction(int nodeId, int action)
    {
        lock (_sync)
        {
            return HandleAccessibilityActionCore(nodeId, action);
        }
    }

    private bool HandleAccessibilityActionCore(int nodeId, int action)
    {
        if (!OpenHarmonyAccessibility.TryFindNode(nodeId, out OpenHarmonyAccessibilityNode node))
        {
            return false;
        }
        // Modal focus trap: while an alert is open, stale background nodes are inert; only the
        // alert's own subtree accepts actions (its buttons route through the same tap path).
        if (OpenHarmonyAlertHost.Current is not null && !OpenHarmonyAccessibility.IsModalNode(nodeId))
        {
            return false;
        }
        OpenHarmonyAccessibility.TryFindView(nodeId, out IView? target);
        switch ((OpenHarmonyAccessibilityAction)action)
        {
            case OpenHarmonyAccessibilityAction.Click:
                return ClickAccessibilityNode(node);
            case OpenHarmonyAccessibilityAction.ScrollForward:
                return ScrollAccessibilityNode(target, forward: true);
            case OpenHarmonyAccessibilityAction.ScrollBackward:
                return ScrollAccessibilityNode(target, forward: false);
            case OpenHarmonyAccessibilityAction.Copy:
                return CopyAccessibilityNode(target, node, cut: false);
            case OpenHarmonyAccessibilityAction.Cut:
                return CopyAccessibilityNode(target, node, cut: true);
            case OpenHarmonyAccessibilityAction.Paste:
                return PasteAccessibilityNode(target);
            case OpenHarmonyAccessibilityAction.SelectText:
                return SelectAllAccessibilityNode(target);
            default:
                // Never advertised: SET_TEXT/SET_CURSOR_POSITION need a value payload the listener
                // does not carry, and this slice has no long-press path. Stale requests stay unhandled.
                return false;
        }
    }

    /// <summary>CLICK simulates a tap at the node centre, exactly what a real touch would do.</summary>
    private bool ClickAccessibilityNode(OpenHarmonyAccessibilityNode node)
    {
        if (RootView is not IView content)
        {
            return false;
        }
        float x = (float)(node.Bounds.X + node.Bounds.Width / 2);
        float y = (float)(node.Bounds.Y + node.Bounds.Height / 2);
        // Two phases like a real touch: the renderer's press tracking only fires a button's Tap on
        // the release phase, so a single down+up call would only set Pressed and never click.
        _renderer.HandleTouch(content, true, false, x, y);
        _renderer.HandleTouch(content, false, true, x, y);
        _dirty = true;
        return true;
    }

    /// <summary>Scrolls an IScrollView by most of a viewport, or steps an ISlider by 10% of its range.</summary>
    private bool ScrollAccessibilityNode(IView? target, bool forward)
    {
        if (target?.Handler?.PlatformView is not OpenHarmonyView platform)
        {
            return false;
        }
        if (platform.IsScrollView)
        {
            bool horizontal = target is IScrollView { Orientation: ScrollOrientation.Horizontal };
            float viewport = horizontal ? platform.Frame.Width : platform.Frame.Height;
            float content = horizontal ? platform.ScrollContentWidth : platform.ScrollContentHeight;
            float current = horizontal ? platform.ScrollOffsetX : platform.ScrollOffsetY;
            float step = Math.Max(40f, viewport * 0.8f);
            float limit = Math.Max(0f, content - viewport);
            float offset = Math.Clamp(current + (forward ? step : -step), 0f, limit);
            if (Math.Abs(offset - current) > 0.01f)
            {
                if (horizontal)
                {
                    platform.ScrollOffsetX = offset;
                    if (target is IScrollView scrollViewX)
                    {
                        scrollViewX.HorizontalOffset = offset;
                    }
                }
                else
                {
                    platform.ScrollOffsetY = offset;
                    // Keep the virtual view in sync the same way a real drag does.
                    if (target is IScrollView scrollViewY)
                    {
                        scrollViewY.VerticalOffset = offset;
                    }
                }
                platform.ScrollOffsetChanged?.Invoke();
            }
            _dirty = true;
            return true;
        }
        if (target is ISlider slider)
        {
            double range = slider.Maximum - slider.Minimum;
            double step = range > 0 ? range / 10.0 : 1.0;
            double value = Math.Clamp(slider.Value + (forward ? step : -step), slider.Minimum, slider.Maximum);
            if (value != slider.Value)
            {
                slider.Value = value;
            }
            _dirty = true;
            return true;
        }
        return false;
    }

    /// <summary>COPY/CUT put the node's text on the system clipboard; CUT clears the editable text.</summary>
    private bool CopyAccessibilityNode(IView? target, OpenHarmonyAccessibilityNode node, bool cut)
    {
        string? text = target is IText textPart && !string.IsNullOrEmpty(textPart.Text) ? textPart.Text : node.Text;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        _ = WriteClipboardAsync(text);
        if (cut)
        {
            SetEditableText(target, string.Empty);
        }
        _dirty = true;
        return true;
    }

    /// <summary>SELECT_TEXT selects the whole editable text (caret at the end).</summary>
    private bool SelectAllAccessibilityNode(IView? target)
    {
        if (target is not ITextInput input)
        {
            return false;
        }
        int length = (input.Text ?? string.Empty).Length;
        input.CursorPosition = length;
        input.SelectionLength = length;
        _dirty = true;
        return true;
    }

    /// <summary>PASTE reads the clipboard and applies the edit on the UI thread when one is available.</summary>
    private bool PasteAccessibilityNode(IView? target)
    {
        if (target is not ITextInput)
        {
            return false;
        }
        _ = PasteClipboardAsync(target);
        return true;
    }

    private async Task PasteClipboardAsync(IView target)
    {
        try
        {
            Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard? clipboard =
                _context.Services.GetService(typeof(Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard))
                as Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard;
            string? text = clipboard is null ? null : await clipboard.GetTextAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            // The action arrives on the platform accessibility thread; the text edit belongs on the UI thread.
            Microsoft.Maui.Dispatching.IDispatcher? dispatcher =
                _context.Services.GetService(typeof(Microsoft.Maui.Dispatching.IDispatcher))
                as Microsoft.Maui.Dispatching.IDispatcher;
            if (dispatcher is not null && dispatcher.IsDispatchRequired)
            {
                dispatcher.Dispatch(() => ApplyPaste(target, text));
                return;
            }
            ApplyPaste(target, text);
        }
        catch (Exception ex)
        {
            // Reverse P/Invoke boundary: the continuation must never throw either.
            OpenHarmonyBridge.WriteStatus(
                $"[maui] accessibility paste failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyPaste(IView target, string pasted)
    {
        lock (_sync)
        {
            if (target is not ITextInput input)
            {
                return;
            }
            string current = input.Text ?? string.Empty;
            int index = Math.Clamp(input.CursorPosition, 0, current.Length);
            int selection = Math.Clamp(input.SelectionLength, 0, current.Length - index);
            string updated = current.Remove(index, selection).Insert(index, pasted);
            SetEditableText(target, updated, index + pasted.Length);
            _dirty = true;
        }
    }

    /// <summary>Writes the text through the DI clipboard (the documented text pasteboard path).</summary>
    private async Task WriteClipboardAsync(string text)
    {
        try
        {
            Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard? clipboard =
                _context.Services.GetService(typeof(Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard))
                as Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus(
                $"[maui] accessibility copy failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a text edit through the Controls types: IText.Text is read-only, exactly like the
    /// Entry/Editor handlers, so the assignment raises TextChanged and the platform mapper runs.
    /// </summary>
    private static void SetEditableText(IView? target, string text, int? caret = null)
    {
        switch (target)
        {
            case Microsoft.Maui.Controls.Entry entry:
                entry.Text = text;
                break;
            case Microsoft.Maui.Controls.Editor editor:
                editor.Text = text;
                break;
            default:
                return;
        }
        if (caret is int position && target is ITextInput input)
        {
            input.CursorPosition = position;
            input.SelectionLength = 0;
        }
    }

    private void OnPinch(int phase, double scale, float x, float y)
    {
        lock (_sync)
        {
            if (_ready && RootView is IView content)
            {
                _renderer.HandlePinch(content, phase, scale, x, y);
            }
        }
    }

    /// <summary>Handles a touch move (drag scrolling).</summary>
    public bool HandleMove(float x, float y)
    {
        lock (_sync)
        {
            if (RootView is IView content)
            {
                _renderer.HandlePointerMove(content, x, y);
            }
            return HandleMoveCore(x, y, 0);
        }
    }

    /// <summary>Handles a move of one pointer (the shell's touch stream carries the id).</summary>
    public bool HandleMove(float x, float y, int pointerId) => HandleMoveCore(x, y, pointerId);

    // N5: a move that belongs to a suppressed press stays with the overlay.
    private bool HandleMoveCore(float x, float y, int pointerId)
    {
        lock (_sync)
        {
            return OpenHarmonyWindowOverlayHost.ShouldConsumeMove() || _renderer.HandleMove(x, y, pointerId);
        }
    }

    public string Describe()
    {
        lock (_sync)
        {
            return RootView is IView content ? _renderer.Describe(content) : "(no window content)";
        }
    }

}
