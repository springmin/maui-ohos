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
        OpenHarmonyHandlerConnector.Context = _app.Context;
        lock (_sync)
        {
            OpenHarmonyHandlerConnector.ConnectTree(_window);
            OpenHarmonyHandlerConnector.ConnectTree(_window.Content);
            _ready = true;
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
        lock (_sync)
        {
            _window = null;
            _created = false;
            _activated = false;
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
            // N5: an active overlay that disables touch passthrough owns the press/release; the
            // page tree underneath is skipped so a control there never also receives the gesture.
            if (OpenHarmonyWindowOverlayHost.ShouldConsumeTouch(down, up))
            {
                return true;
            }
            return Content is IView content && Renderer.HandleTouch(content, down, up, x, y, pointerId);
        }
    }

    /// <summary>Cancels the gesture stream (the shell reports a canceled touch): CancelInteraction.</summary>
    public bool HandleCancel(float x, float y)
    {
        lock (_sync)
        {
            if (OpenHarmonyWindowOverlayHost.ShouldConsumeCancel())
            {
                return true;
            }
            return Renderer.HandleCancel(x, y);
        }
    }

    /// <summary>Handles a move of one pointer (the shell's touch stream carries the id).</summary>
    public bool HandleMove(float x, float y, int pointerId)
    {
        lock (_sync)
        {
            return OpenHarmonyWindowOverlayHost.ShouldConsumeMove() || Renderer.HandleMove(x, y, pointerId);
        }
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

    /// <summary>Diagnostic description of this window's tree (the headless harness reads it).</summary>
    public string Describe()
    {
        lock (_sync)
        {
            return Content is IView content ? Renderer.Describe(content) : "(no window content)";
        }
    }
}
