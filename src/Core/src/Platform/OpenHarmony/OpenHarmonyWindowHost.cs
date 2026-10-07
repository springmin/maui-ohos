// ONE MAUI window on OpenHarmony beyond the primary one: the window object, its own
// surface/renderer pair, the last arranged surface size and the window's own input/frame
// state. M2 (MULTIWINDOW-L) made window state per-window: the first window keeps the
// historical single-slot path on OpenHarmonyMauiAppHost (zero change), while every window
// adopted through OpenWindow owns an instance of this class, so two windows arrange, render,
// resize and receive input independently. See OpenHarmonyMauiAppHost for the owner and the
// window-id routing entry points (RouteSurface/RouteTouch/RouteFrame).
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The per-window state owner for a secondary window. Every method here reads or mutates only
/// this window's tree, surface, renderer and input state; the process-wide pieces
/// (accessibility shadow tree, overlay host, pinch listener, IME, safe-area, font scale) stay
/// with the app host and are partitioned per window in M4.
/// </summary>
internal sealed class OpenHarmonyWindowHost
{
    private readonly OpenHarmonyMauiAppHost _app;
    // Serializes every entry point that reads or mutates this window's element tree. The
    // managed app runs on the host's launch thread while the ArkTS shell delivers
    // surface/frame/input callbacks on the shell thread; without this, a JIT-slowed startup
    // lets a callback wire handlers mid-connect (device WX-JIT: "Handler is already being set
    // elsewhere", "PlatformView cannot be null here", non-concurrent collection corruption).
    private readonly object _sync = new();
    private IWindow? _window;
    private int _width;
    private int _height;
    private bool _dirty = true;
    private bool _created;
    private bool _activated;
    // MULTIWINDOW-L M4: the focus/background half of the IWindow lifecycle. The shell reports
    // the child window's WINDOW_ACTIVE/INACTIVE (focus) and the main window's hidden/Home
    // suspension; these flags keep Activated/Deactivated/Stopped/Resumed a well-formed sequence
    // (no double Stopped/Resumed, Deactivated only after Activated) the same way the primary
    // host's process lifecycle does.
    private bool _deactivated;
    private bool _stopped;
    // Platform callbacks stay out of the tree until the window finished connecting it, so a
    // callback can never observe a half-connected tree.
    private bool _ready;

    public OpenHarmonyWindowHost(
        OpenHarmonyMauiAppHost app,
        string windowId,
        OpenHarmonyWindowSurface surface,
        OpenHarmonyWindowRenderer renderer)
    {
        _app = app;
        WindowId = windowId;
        Surface = surface;
        Renderer = renderer;
        surface.SurfaceChanged += (_, info) => OnSurfaceChanged(info);
    }

    /// <summary>The host-assigned window id (the id the shell's surface registry reported).</summary>
    public string WindowId { get; }

    // MULTIWINDOW-L M4 (fonts): the secondary window re-arranges its own tree when the system
    // font scale changes (the primary host does the same through its own subscription). The
    // subscription lives exactly as long as the adoption: Reset unsubscribes, so a closed
    // session cannot be rooted by the static scale event.
    private bool _fontScaleSubscribed;

    private void OnSystemFontScaleChanged()
    {
        int width;
        int height;
        lock (_sync)
        {
            if (!_ready || _width <= 0 || _height <= 0)
            {
                return;
            }
            width = _width;
            height = _height;
            _dirty = true;
        }
        Arrange(width, height);
        Render();
    }

    /// <summary>The window's surface state (fed by the app host's window-id routing).</summary>
    public OpenHarmonyWindowSurface Surface { get; }

    /// <summary>The window's compositor; never shared with another window.</summary>
    public OpenHarmonyWindowRenderer Renderer { get; }

    public IWindow? Window => _window;

    /// <summary>The modal-aware render root (mirrors the primary host's RootView contract).</summary>
    internal IView? Content
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

    internal bool Dirty { get { lock (_sync) { return _dirty; } } }

    internal void MarkDirty()
    {
        lock (_sync)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// Adopts the window after startup: connects its handler tree, raises Created and (the
    /// platform is already foregrounded) Activated, arranges for the last reported surface size
    /// and starts rendering.
    /// </summary>
    internal void Adopt(IWindow window)
    {
        _window = window;
        _created = false;
        _activated = false;
        _deactivated = false;
        _stopped = false;
        OpenHarmonyHandlerConnector.Context = _app.Context;
        // MULTIWINDOW-L2: stamp the host's window table before the element tree connects, so a
        // handler that resolves its window during ConnectTree (the web handlers claim their
        // overlay at connect) sees this secondary id instead of the primary fallback. The
        // window handler's own stamp below needs the handler the connect creates, so it stays
        // after; ResolveWindowId prefers the table.
        OpenHarmonyMauiAppHost.SetWindowId(_window, WindowId);
        lock (_sync)
        {
            OpenHarmonyHandlerConnector.ConnectTree(_window);
            OpenHarmonyHandlerConnector.ConnectTree(_window.Content);
            _ready = true;
        }
        // MULTIWINDOW-L M4: stamp the window id on the window's own platform handler too, so
        // elements of this window can resolve which window they belong to (per-window focus /
        // input routing; see OpenHarmonyMauiAppHost.ResolveWindowId).
        if (_window.Handler is OpenHarmonyWindowHandler windowHandler)
        {
            windowHandler.WindowId = WindowId;
        }
        if (!_fontScaleSubscribed)
        {
            OpenHarmonySystemFontScale.Changed += OnSystemFontScaleChanged;
            _fontScaleSubscribed = true;
        }
        OpenHarmonyBridge.WriteStatus(
            $"[maui] window '{WindowId}' adopted ({_window.GetType().Name}), content={_window.Content?.GetType().Name}");
        EnsureWindowCreated();
        EnsureWindowActivated();
        if (_width > 0)
        {
            Arrange(_width, _height);
        }
        MarkDirty();
    }

    /// <summary>Drops this window's reference (it was closed); its surface stays bound so a
    /// later report for the same id is recorded until a window adopts it again.</summary>
    internal void Reset()
    {
        if (_fontScaleSubscribed)
        {
            OpenHarmonySystemFontScale.Changed -= OnSystemFontScaleChanged;
            _fontScaleSubscribed = false;
        }
        // MULTIWINDOW-L2: the window ended. Its element handlers are still connected at this
        // point (the app host removes the window, but MAUI does not disconnect the content
        // tree), so the window-scoped web claims are released here: no child ArkWeb survives
        // the window that hosted it.
        OpenHarmonyWebViewHandler.ReleaseWindowOverlays(WindowId);
        // SEC-SCAN-6 C: the closed window's managed residual state goes with it - the child web
        // pool (capacity/pending) and the a11y shadow frame + first-publish marker - so a window
        // reusing the id starts from not-ready and re-publishes instead of inheriting them.
        // MULTIWINDOW-L3 M3: its alert slot goes too, so a dialog left open when the window
        // closed can never resurface on the id's next incarnation.
        OpenHarmonyAlertHost.Hide(WindowId);
        OpenHarmonyChildWeb.ReleaseWindow(WindowId);
        OpenHarmonyAccessibility.ReleaseWindow(WindowId);
        lock (_sync)
        {
            _window = null;
            _created = false;
            _activated = false;
            _deactivated = false;
            _stopped = false;
            _dirty = true;
        }
    }

    /// <summary>
    /// Raises <see cref="IWindow.Created"/> exactly once per adopted window; a window adopted
    /// after startup never saw the process-level platform Create event, so this is the single
    /// entry point that starts its MAUI lifecycle.
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

    /// <summary>Raises <see cref="IWindow.Activated"/> exactly once per adopted window (the
    /// platform is already foregrounded when a window is adopted through OpenWindow).</summary>
    private void EnsureWindowActivated()
    {
        if (_activated || _window is null)
        {
            return;
        }
        _activated = true;
        _window.Activated();
    }

    /// <summary>This window's surface reaction: arrange/render on a usable size and report a
    /// live resize through the status line (the shell's dotnet-status poll carries it to hilog).</summary>
    private void OnSurfaceChanged(OpenHarmonySurfaceInfo info)
    {
        if (OpenHarmonyMauiAppHost.CanArrangeSurface(info))
        {
            lock (_sync)
            {
                Arrange(info.Width, info.Height);
                _dirty = true;
                Render();
            }
            if (info.State == OpenHarmonySurfaceState.Changed)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] window '{WindowId}' size {info.Width}x{info.Height}");
            }
        }
    }

    /// <summary>Measures/arranges this window's content for the given surface size through the
    /// app host's shared safe-area arrange tail.</summary>
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
            OpenHarmonyMauiAppHost.ArrangeContent(_window, content, new Rect(0, 0, width, height));
        }
    }

    /// <summary>Draws this window's current content into its surface.</summary>
    public bool Render()
    {
        lock (_sync)
        {
            if (!_ready || _window?.Content is not IView content || _width <= 0)
            {
                return false;
            }
            return Renderer.Render(content, _width, _height);
        }
    }

    /// <summary>Handles a press/release of one pointer (the shell's touch stream carries the
    /// pointer id so multi-touch gestures keep their pointer identity).</summary>
    public bool HandleTouch(bool down, bool up, float x, float y, int pointerId)
    {
        lock (_sync)
        {
            // MULTIWINDOW-L M4: the process-global overlay host is still primary-window state
            // (its SurfacePresent hook is bypassed by this window's renderer), so a primary
            // overlay must not consume this window's touches. The window's own tree handles
            // them; per-window overlays arrive with the M4 remainder.
            return Content is IView content && Renderer.HandleTouch(content, down, up, x, y, pointerId);
        }
    }

    /// <summary>Cancels the gesture stream (the shell reports a canceled touch): CancelInteraction.</summary>
    public bool HandleCancel(float x, float y)
    {
        lock (_sync)
        {
            return Renderer.HandleCancel(x, y);
        }
    }

    /// <summary>Handles a move of one pointer (the shell's touch stream carries the id).</summary>
    public bool HandleMove(float x, float y, int pointerId)
    {
        lock (_sync)
        {
            return Renderer.HandleMove(x, y, pointerId);
        }
    }

    /// <summary>
    /// MULTIWINDOW-L M4-04: dispatches one phase of this window's own pinch stream through the
    /// window's renderer (the managed hit-test stays per window; the primary window keeps the
    /// untagged pinch path in the app host).
    /// </summary>
    public bool HandlePinch(int phase, double scale, float x, float y)
    {
        lock (_sync)
        {
            bool handled = Content is IView content && Renderer.HandlePinch(content, phase, scale, x, y);
            if (handled)
            {
                PinchCount++;
            }
            return handled;
        }
    }

    /// <summary>Pinch phases this window's renderer handled (M4-04 routing evidence).</summary>
    internal int PinchCount { get; private set; }

    /// <summary>
    /// MULTIWINDOW-L2 a: executes an accessibility action routed from this window's provider
    /// instance. The app host's shared action core runs under this window's tree lock, so the
    /// action resolves against this window's frame/view tree and marks this window dirty; the
    /// primary window's provider path is not involved.
    /// </summary>
    internal bool HandleAccessibilityAction(int nodeId, int action)
    {
        lock (_sync)
        {
            return _app.HandleAccessibilityActionCore(WindowId, nodeId, action);
        }
    }

    /// <summary>
    /// MULTIWINDOW-L3 M3: runs this window's own Back handling (the modal stack, then the page's
    /// SendBackButtonPressed chain) for a Back key the shell delivered to this window. The shell
    /// channel is one-way (no synchronous consume answer), so the platform default still runs;
    /// this is what makes Back act on the focused window's tree instead of the primary's.
    /// </summary>
    internal bool HandleBackRequested()
    {
        IWindow? window;
        lock (_sync)
        {
            window = _window;
            if (window is null || _stopped || !_created)
            {
                return false;
            }
        }
        return window.BackButtonClicked();
    }

    /// <summary>The frame tick: advances this window's animation state and paints the dirty
    /// surface (the device's secondary frame routing arrives with M3; off-device tests drive it).</summary>
    internal void OnFrame()
    {
        lock (_sync)
        {
            if (!_ready)
            {
                return;
            }
            FrameTicks++;
            // Activity indicators keep animating: advance the shared angle and redraw.
            if (Renderer.HasAnimations(_window?.Content as IView))
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

    /// <summary>Frame ticks this window's renderer consumed (M3 routing evidence).</summary>
    internal int FrameTicks { get; private set; }

    // MULTIWINDOW-L M4 lifecycle counters (off-device assertions; also readable on a device
    // through the status line's per-window close report).
    internal int ActivatedCount { get; private set; }
    internal int DeactivatedCount { get; private set; }
    internal int StoppedCount { get; private set; }
    internal int ResumedCount { get; private set; }

    /// <summary>True between a Stopped and its Resumed (the window is backgrounded).</summary>
    internal bool IsStopped { get { lock (_sync) { return _stopped; } } }

    /// <summary>True while the window lost focus (Deactivated without a later Activated).</summary>
    internal bool IsDeactivated { get { lock (_sync) { return _deactivated; } } }

    /// <summary>
    /// Focus returned to this window (the shell's child WINDOW_ACTIVE): raises IWindow.Activated
    /// for a window Controls has deactivated. MULTIWINDOW-L3 M2: focus transitions are ignored
    /// while the window is stopped (the app is backgrounded; a late child WINDOW_ACTIVE must not
    /// clear the stopped flag or deliver an event the matching Resumed owns), and only the
    /// Resumed path leaves the stopped state - so Stopped/Resumed stay a well-formed pair no
    /// matter how focus events interleave.
    /// </summary>
    internal void Activated()
    {
        IWindow? window;
        lock (_sync)
        {
            window = _window;
            if (window is null || _stopped)
            {
                return;
            }
            if (_activated)
            {
                return;
            }
            _activated = true;
            _deactivated = false;
            ActivatedCount++;
        }
        window.Activated();
    }

    /// <summary>Focus left this window (the shell's child WINDOW_INACTIVE): Deactivated once.
    /// A focus loss while the window is stopped is ignored (the suspend owns the lifecycle).</summary>
    internal void Deactivated()
    {
        IWindow? window;
        lock (_sync)
        {
            window = _window;
            if (window is null || _stopped || !_activated)
            {
                return;
            }
            _activated = false;
            _deactivated = true;
            DeactivatedCount++;
        }
        window.Deactivated();
    }

    /// <summary>The app left the foreground (main window hidden / Home focus loss): Stopped once.</summary>
    internal void Stopped()
    {
        IWindow? window;
        lock (_sync)
        {
            window = _window;
            if (window is null || _stopped || !_created)
            {
                return;
            }
            _stopped = true;
            StoppedCount++;
        }
        window.Stopped();
    }

    /// <summary>The app came back: Resumed once after a Stopped.</summary>
    internal void Resumed()
    {
        IWindow? window;
        lock (_sync)
        {
            window = _window;
            if (window is null || !_stopped)
            {
                return;
            }
            _stopped = false;
            ResumedCount++;
        }
        window.Resumed();
    }

    /// <summary>Diagnostic description of this window's tree (the headless harness reads it).</summary>
    public string Describe()
    {
        lock (_sync)
        {
            return Content is IView content ? Renderer.Describe(content) : "(no window content)";
        }
    }
}
