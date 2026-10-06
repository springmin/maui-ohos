// Surface + canvas access for MAUI window handlers.
//
// OpenHarmonyBridge raises SurfaceChanged when the ArkUI XComponent hands over an
// OHNativeWindow*; this type keeps the current surface and creates Microsoft.Maui.Graphics
// canvases over it (OpenHarmonyCanvas, backed by native_drawing).
//
// M2 (MULTIWINDOW-L): one instance per window. The parameterless constructor is the primary
// window's surface (window id "main") and keeps the historical bridge subscription; a
// secondary window's surface is created with its window id and is fed by the app host's
// window-id routing (OpenHarmonyMauiAppHost.RouteSurface), because the bridge events are
// untagged until the host's per-window dispatch lands (M2-ow). Begin/Present are the draw
// target seam: the default is the shared host canvas (single-surface behaviour), and
// BeginHook/PresentHook let off-device tests bind a window's own frame target.
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWindowSurface
{
    /// <summary>Window id the first XComponent claims; the primary MAUI window (M1 registry).</summary>
    public const string PrimaryWindowId = "main";

    private readonly object _sync = new();
    private OpenHarmonySurfaceInfo? _surface;

    /// <summary>The primary window's surface: attaches to the bridge's untagged surface event.</summary>
    public OpenHarmonyWindowSurface()
        : this(PrimaryWindowId, attachToBridge: true)
    {
    }

    /// <summary>
    /// A window's surface. <paramref name="attachToBridge"/> is true only for the primary
    /// window: a secondary surface must never consume the shell's untagged primary events.
    /// </summary>
    internal OpenHarmonyWindowSurface(string windowId, bool attachToBridge)
    {
        WindowId = windowId;
        if (attachToBridge)
        {
            OpenHarmonyBridge.SurfaceChanged += OnSurfaceReported;
        }
    }

    /// <summary>The host window id this surface belongs to ("main" for the primary window).</summary>
    public string WindowId { get; }

    /// <summary>Draw-target seam: begins a frame on this window's surface (default: the shared
    /// host canvas). Off-device tests substitute a recording target per window.</summary>
    public Func<int, int, bool>? BeginHook { get; set; }

    /// <summary>Draw-target seam: presents the frame begun by <see cref="Begin"/>.</summary>
    public Action? PresentHook { get; set; }

    public event EventHandler<OpenHarmonySurfaceInfo>? SurfaceChanged;

    public OpenHarmonySurfaceInfo? Surface
    {
        get { lock (_sync) { return _surface; } }
    }

    /// <summary>True while a usable surface is available.</summary>
    public bool IsAvailable => Surface is { State: not OpenHarmonySurfaceState.Destroyed, Width: > 0, Height: > 0 };

    /// <summary>
    /// Records one surface report for this window and raises <see cref="SurfaceChanged"/>.
    /// The bridge calls this for the primary window; the app host's window-id routing calls it
    /// for a secondary window (and for the primary in off-device tests).
    /// </summary>
    internal void OnSurface(OpenHarmonySurfaceInfo info)
    {
        lock (_sync)
        {
            _surface = info;
        }
        SurfaceChanged?.Invoke(this, info);
    }

    private void OnSurfaceReported(OpenHarmonySurfaceInfo info) => OnSurface(info);

    /// <summary>Creates a MauiGraphics canvas for the current surface (null when unavailable).</summary>
    public ICanvas? CreateCanvas()
    {
        OpenHarmonySurfaceInfo? surface = Surface;
        if (surface is null || surface.State == OpenHarmonySurfaceState.Destroyed || surface.Width <= 0 || surface.Height <= 0)
        {
            return null;
        }
        if (!Begin(surface.Width, surface.Height))
        {
            return null;
        }
        return new MauiCanvas();
    }

    /// <summary>Begins a frame on this window's surface (the renderer's draw target). The
    /// primary window keeps the shared legacy canvas; a secondary window targets its own
    /// per-window canvas (MULTIWINDOW-L M3), keyed by the registry id whose surface also
    /// receives the present.</summary>
    public bool Begin(int width, int height)
    {
        if (BeginHook is not null)
        {
            return BeginHook(width, height);
        }
        if (WindowId != PrimaryWindowId)
        {
            bool begun = OpenHarmonyCanvas.BeginWindow(WindowId, width, height);
            if (!begun && !_beginFailedLogged)
            {
                _beginFailedLogged = true;
                OpenHarmonyBridge.WriteStatus($"[maui] window '{WindowId}' canvas begin failed");
            }
            return begun;
        }
        return HostCanvas.Begin(width, height);
    }

    /// <summary>Presents the frame drawn through <see cref="Begin"/>.</summary>
    public void Present()
    {
        if (PresentHook is not null)
        {
            PresentHook();
        }
        else if (WindowId != PrimaryWindowId)
        {
            if (!OpenHarmonyCanvas.PresentWindow(WindowId) && !_presentFailedLogged)
            {
                _presentFailedLogged = true;
                OpenHarmonyBridge.WriteStatus($"[maui] window '{WindowId}' canvas present failed");
            }
        }
        else
        {
            HostCanvas.Present();
        }
    }

    private bool _beginFailedLogged;
    private bool _presentFailedLogged;
}
