// Window overlays for the OpenHarmony compositor (MAUI's IWindowOverlay).
//
// MAUI's overlay contract: an IWindowOverlay (normally a Microsoft.Maui.WindowOverlay subclass) is
// attached with IWindow.AddOverlay, initialized by the platform, drawn above the page content and
// optionally allowed to consume touches. Verified against Microsoft.Maui.Controls
// 11.0.0-rc.1.26451.6: Window.AddOverlay calls overlay.Initialize() and Window.RemoveOverlay calls
// overlay.Deinitialize(), independently of the window handler.
//
// The slice has no platform view per overlay - the whole tree is drawn by
// OpenHarmonyWindowRenderer into one XComponent surface. This file therefore adds:
//
//   * OpenHarmonyWindowOverlay: a self-contained IWindowOverlay (element list, visibility,
//     density, Tapped) whose Initialize/Deinitialize register with the host below and whose
//     Drawing/Add/Remove/Invalidate entry points request a redraw.
//   * OpenHarmonyWindowOverlayHost: installed once by UseOpenHarmony (through the platform
//     application initializer, before the first frame), it chains the renderer's existing public
//     SurfacePresent seam - the same hook OpenHarmonyToolTipManager uses - and on every frame
//     draws every visible, initialized overlay found in IWindow.Overlays (plus this type's
//     registry) with the shared frame canvas, before the previous present runs. Nothing is drawn
//     while no overlay is attached, so the idle frame path is unchanged.
//
// The pool of drawn overlays is discovered per frame instead of at AddOverlay time because the
// slice's window handler (OpenHarmonyWindowHandler) has no AddOverlay/RemoveOverlay mapper entry
// and is not ours to change: scanning IWindow.Overlays also draws overlays that derive from
// Microsoft.Maui.WindowOverlay directly, which is the documented MAUI pattern.
//
// Documented limitations of this implementation (once, no silent gaps):
//
//   * Touch passthrough cannot be suppressed. OpenHarmonyBridge.Touch is a multicast event and
//     the slice's single tree entry point (OpenHarmonyMauiAppHost.HandleTouch ->
//     OpenHarmonyWindowRenderer.HandleTouch) runs regardless of what the overlay reports, so
//     DisableUITouchEventPassthrough/EnableDrawableTouchHandling are recorded and visible to an
//     app but do not stop a control underneath from also receiving the touch. Real suppression
//     needs the host (or renderer) to ask the overlay host first and skip the tree when the
//     overlay consumes the event. Tapped is raised on every touch-up while the overlay is
//     visible and initialized (the whole surface is the overlay's view, matching the other
//     platforms), with the elements whose Contains(point) is true - possibly none.
//
//   * Tapped can only be raised for OpenHarmonyWindowOverlay. Microsoft.Maui.WindowOverlay's
//     event raiser is internal to Microsoft.Maui.dll, so a package-derived overlay is drawn but
//     its Tapped never fires; deriving from OpenHarmonyWindowOverlay gets both.
//
//   * Window.VisualDiagnosticsOverlay is drawn only after its Initialize() was called; Controls
//     creates it uninitialized and the slice's window handler does not initialize it (see the
//     IAdorner note in OpenHarmonyMauiApplication.cs for what the real wiring needs).
//
//   * Draw receives Microsoft.Maui.Graphics.Point coordinates in device pixels from the bridge;
//     Density reports IWindow.RequestDisplayDensity() (1 when the window has no handler yet).
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The slice's <see cref="IWindowOverlay"/>: a managed element list painted by
/// <see cref="OpenHarmonyWindowOverlayHost"/> on the compositor frame, with the same
/// add/remove/visibility/density surface the other MAUI platforms expose.
/// </summary>
public class OpenHarmonyWindowOverlay : IWindowOverlay
{
    private readonly object _sync = new();
    private readonly List<IWindowOverlayElement> _elements = new();
    private bool _platformInitialized;

    public OpenHarmonyWindowOverlay(IWindow window)
    {
        Window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <inheritdoc />
    public event EventHandler<WindowOverlayTappedEventArgs>? Tapped;

    /// <inheritdoc />
    public bool DisableUITouchEventPassthrough { get; set; }

    /// <inheritdoc />
    public bool EnableDrawableTouchHandling { get; set; }

    /// <inheritdoc />
    public bool IsVisible { get; set; } = true;

    /// <inheritdoc />
    public IWindow Window { get; }

    /// <inheritdoc />
    public float Density
    {
        get
        {
            try
            {
                float density = Window.RequestDisplayDensity();
                return density > 0 ? density : 1f;
            }
            catch (Exception)
            {
                // No handler/density source yet: 1 is the documented fallback.
                return 1f;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IWindowOverlayElement> WindowElements
    {
        get
        {
            lock (_sync)
            {
                return _elements.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool IsPlatformViewInitialized => _platformInitialized;

    /// <summary>
    /// Registers the overlay with the frame host (idempotent). Called by
    /// <c>IWindow.AddOverlay</c>; returns true, matching the platforms whose overlay has no
    /// native peer to create.
    /// </summary>
    public bool Initialize()
    {
        _platformInitialized = true;
        OpenHarmonyWindowOverlayHost.Register(this);
        return true;
    }

    /// <summary>Unregisters the overlay and stops drawing it (idempotent).</summary>
    public bool Deinitialize()
    {
        _platformInitialized = false;
        OpenHarmonyWindowOverlayHost.Unregister(this);
        return true;
    }

    /// <summary>Requests the next compositor frame so a drawing change is presented.</summary>
    public void Invalidate() => OpenHarmonyWindowOverlayHost.RequestRedraw();

    /// <summary>Requests a redraw after a UI change (density, rotation, theme).</summary>
    public void HandleUIChange() => OpenHarmonyWindowOverlayHost.RequestRedraw();

    /// <inheritdoc />
    public bool AddWindowElement(IWindowOverlayElement drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        lock (_sync)
        {
            if (_elements.Contains(drawable))
            {
                return false;
            }
            _elements.Add(drawable);
        }
        OpenHarmonyWindowOverlayHost.RequestRedraw();
        return true;
    }

    /// <inheritdoc />
    public bool RemoveWindowElement(IWindowOverlayElement drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        bool removed;
        lock (_sync)
        {
            removed = _elements.Remove(drawable);
        }
        if (removed)
        {
            OpenHarmonyWindowOverlayHost.RequestRedraw();
        }
        return removed;
    }

    /// <summary>Removes every element (and requests one redraw).</summary>
    public void RemoveWindowElements()
    {
        lock (_sync)
        {
            if (_elements.Count == 0)
            {
                return;
            }
            _elements.Clear();
        }
        OpenHarmonyWindowOverlayHost.RequestRedraw();
    }

    /// <summary>Draws every element; the host calls this once per frame, right before present.</summary>
    public virtual void Draw(ICanvas canvas, RectF dirtyRect)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        IWindowOverlayElement[] elements;
        lock (_sync)
        {
            elements = _elements.ToArray();
        }
        foreach (IWindowOverlayElement element in elements)
        {
            element.Draw(canvas, dirtyRect);
        }
    }

    /// <summary>
    /// Raises <see cref="Tapped"/> for a touch-up at <paramref name="point"/>. Internal because
    /// the platform host is the only raiser (the base type's raiser is internal to Microsoft.Maui).
    /// </summary>
    internal void RaiseTapped(Point point, IList<IWindowOverlayElement> overlayElements)
    {
        EventHandler<WindowOverlayTappedEventArgs>? handler = Tapped;
        handler?.Invoke(this, new WindowOverlayTappedEventArgs(
            point,
            Array.Empty<IVisualTreeElement>(),
            overlayElements));
    }
}

/// <summary>
/// Frame hook that draws the window's overlays. Installed once by the platform application
/// initializer; each present call paints the visible overlays on the same canvas the renderer
/// used and then chains the previous <see cref="OpenHarmonyWindowRenderer.SurfacePresent"/> hook
/// (or presents through the hosting canvas when there is none).
/// </summary>
internal static class OpenHarmonyWindowOverlayHost
{
    private static readonly object s_sync = new();
    private static readonly List<IWindowOverlay> s_registered = new();
    private static bool s_installed;
    private static Action? s_presentDelegate;
    private static Action? s_presentPrevious;
    private static MauiCanvas? s_canvas;

    /// <summary>Canvas factory for overlay paint passes; tests substitute a recording canvas.</summary>
    internal static Func<ICanvas>? OverlayCanvasFactory { get; set; }

    /// <summary>Overlay paint passes performed (test/diagnostics counter).</summary>
    internal static int Draws { get; private set; }

    /// <summary>True while the frame hook is installed (idempotent).</summary>
    internal static bool IsInstalled
    {
        get
        {
            lock (s_sync)
            {
                return s_installed;
            }
        }
    }

    /// <summary>Installs the present hook and the touch-up reporter exactly once.</summary>
    internal static void Install()
    {
        lock (s_sync)
        {
            if (s_installed)
            {
                return;
            }
            s_installed = true;
            s_presentDelegate = OnSurfacePresent;
            s_presentPrevious = OpenHarmonyWindowRenderer.SurfacePresent;
            OpenHarmonyWindowRenderer.SurfacePresent = s_presentDelegate;
        }
        OpenHarmonyBridge.Touch += OnTouch;
    }

    internal static void Register(IWindowOverlay overlay)
    {
        // The initializer installs at Build; calling it here as well keeps a manually constructed
        // slice (no MauiAppBuilder) working and is idempotent.
        Install();
        lock (s_sync)
        {
            if (!s_registered.Contains(overlay))
            {
                s_registered.Add(overlay);
            }
        }
        RequestRedraw();
    }

    internal static void Unregister(IWindowOverlay overlay)
    {
        lock (s_sync)
        {
            s_registered.Remove(overlay);
        }
        RequestRedraw();
    }

    /// <summary>Asks the host for the next frame; failures must never surface into the app.</summary>
    internal static void RequestRedraw()
    {
        try
        {
            OpenHarmonyBridge.RequestRedraw();
        }
        catch (Exception)
        {
            // Diagnostics only: a redraw request has no failure contract.
        }
    }

    private static void OnSurfacePresent()
    {
        try
        {
            DrawOverlays();
        }
        catch (Exception)
        {
            // A paint failure must not break the frame (the tooltip hook's contract).
        }
        if (s_presentPrevious is { } previous)
        {
            previous();
        }
        else
        {
            HostCanvas.Present();
        }
    }

    /// <summary>Paints every visible, initialized overlay on the current frame canvas.</summary>
    internal static void DrawOverlays()
    {
        List<IWindowOverlay> active = ActiveOverlays();
        if (active.Count == 0)
        {
            return;
        }
        RectF rect = SurfaceRect();
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }
        ICanvas canvas = OverlayCanvasFactory?.Invoke() ?? (s_canvas ??= new MauiCanvas());
        foreach (IWindowOverlay overlay in active)
        {
            overlay.Draw(canvas, rect);
            Draws++;
        }
    }

    /// <summary>
    /// Overlays to draw: this type's registry plus every overlay attached to the application's
    /// windows (the documented IWindow.AddOverlay path, for package-derived overlays too).
    /// </summary>
    private static List<IWindowOverlay> ActiveOverlays()
    {
        var active = new List<IWindowOverlay>();
        lock (s_sync)
        {
            if (s_registered.Count > 0)
            {
                active.AddRange(s_registered);
            }
        }
        try
        {
            IReadOnlyList<IWindow>? windows = IPlatformApplication.Current?.Application?.Windows;
            if (windows is not null)
            {
                foreach (IWindow? window in windows)
                {
                    if (window?.Overlays is not { } overlays)
                    {
                        continue;
                    }
                    foreach (IWindowOverlay overlay in overlays)
                    {
                        if (overlay is not null && !active.Contains(overlay))
                        {
                            active.Add(overlay);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // No application (or a resolving application): the registry above still works.
        }
        active.RemoveAll(overlay => !overlay.IsVisible || !overlay.IsPlatformViewInitialized);
        return active;
    }

    /// <summary>Frame rectangle from the platform surface, falling back to the last render size.</summary>
    private static RectF SurfaceRect()
    {
        OpenHarmonySurfaceInfo? surface = OpenHarmonyBridge.Surface;
        if (surface is not null && surface.Width > 0 && surface.Height > 0)
        {
            return new RectF(0, 0, surface.Width, surface.Height);
        }
        if (OpenHarmonyAlertHost.Width > 0 && OpenHarmonyAlertHost.Height > 0)
        {
            return new RectF(0, 0, (float)OpenHarmonyAlertHost.Width, (float)OpenHarmonyAlertHost.Height);
        }
        return new RectF(0, 0, 0, 0);
    }

    private static void OnTouch(OpenHarmonyTouchEventArgs args)
    {
        try
        {
            if (args.Action != OpenHarmonyTouchAction.Up)
            {
                return;
            }
            var point = new Point(args.X, args.Y);
            List<IWindowOverlay> active = ActiveOverlays();
            foreach (IWindowOverlay overlay in active)
            {
                // Only this type exposes a raiser; see the file header for the package-derived case.
                if (overlay is not OpenHarmonyWindowOverlay own)
                {
                    continue;
                }
                var hits = new List<IWindowOverlayElement>();
                foreach (IWindowOverlayElement element in own.WindowElements)
                {
                    if (element.Contains(point))
                    {
                        hits.Add(element);
                    }
                }
                own.RaiseTapped(point, hits);
            }
        }
        catch (Exception)
        {
            // Reverse P/Invoke boundary: a touch report must never throw.
        }
    }
}
