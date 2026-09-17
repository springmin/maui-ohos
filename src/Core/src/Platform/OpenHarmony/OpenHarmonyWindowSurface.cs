// Surface + canvas access for MAUI window handlers.
//
// OpenHarmonyBridge raises SurfaceChanged when the ArkUI XComponent hands over an
// OHNativeWindow*; this type keeps the current surface and creates Microsoft.Maui.Graphics
// canvases over it (OpenHarmonyCanvas, backed by native_drawing).
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWindowSurface
{
    private readonly object _sync = new();
    private OpenHarmonySurfaceInfo? _surface;

    public OpenHarmonyWindowSurface()
    {
        OpenHarmonyBridge.SurfaceChanged += info =>
        {
            lock (_sync)
            {
                _surface = info;
            }
            SurfaceChanged?.Invoke(this, info);
        };
    }

    public event EventHandler<OpenHarmonySurfaceInfo>? SurfaceChanged;

    public OpenHarmonySurfaceInfo? Surface
    {
        get { lock (_sync) { return _surface; } }
    }

    /// <summary>True while a usable surface is available.</summary>
    public bool IsAvailable => Surface is { State: not OpenHarmonySurfaceState.Destroyed, Width: > 0, Height: > 0 };

    /// <summary>Creates a MauiGraphics canvas for the current surface (null when unavailable).</summary>
    public ICanvas? CreateCanvas()
    {
        OpenHarmonySurfaceInfo? surface = Surface;
        if (surface is null || surface.State == OpenHarmonySurfaceState.Destroyed || surface.Width <= 0 || surface.Height <= 0)
        {
            return null;
        }
        if (!HostCanvas.Begin(surface.Width, surface.Height))
        {
            return null;
        }
        return new MauiCanvas();
    }

    /// <summary>Presents the frame drawn through <see cref="CreateCanvas"/>.</summary>
    public void Present() => HostCanvas.Present();
}
