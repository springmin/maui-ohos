// Window handler for OpenHarmony: gives IWindow a platform handler so MAUI's window lifecycle
// (Created/Activated/Resumed/Stopped/Destroying) can run.
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWindowHandler : ElementHandler<IWindow, OpenHarmonyView>
{
    public static readonly IPropertyMapper<IWindow, OpenHarmonyWindowHandler> Mapper =
        new PropertyMapper<IWindow, OpenHarmonyWindowHandler>(ElementMapper)
        {
            [nameof(IWindow.Content)] = MapContent,
            [nameof(IWindow.Title)] = MapTitle,
            // X/Y/Width/Height are deliberately not mapped. The ArkUI window belongs to the
            // shell: the managed bridge reports the surface size (OpenHarmonyBridge.Surface/
            // SurfaceChanged, mirrored onto IWindow.FrameChanged by the app host) but exposes no
            // export that accepts a window position or size, so moving/resizing the native
            // window from MAUI would need new host exports (for example
            // ohos_host_set_window_rect(x, y, width, height)) wired to the ArkUI window stage.
        };

    public OpenHarmonyWindowHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformElement() => new();

    public static void MapContent(OpenHarmonyWindowHandler handler, IWindow window)
    {
        // The app host renders window.Content; nothing to do here yet.
    }

    /// <summary>
    /// Records the title requested through <see cref="IWindow.Title"/>. Applying it needs a host
    /// export the slice does not have yet - the bridge covers the surface, the avoid area and
    /// the app context, but nothing that sets the shell's native window title (for example
    /// ohos_host_set_window_title(utf8) forwarded to the ArkUI window stage) - so the shell
    /// keeps its own title and this value is kept for the host/tests to observe.
    /// </summary>
    public static void MapTitle(OpenHarmonyWindowHandler handler, IWindow window) => handler.Title = window.Title;

    /// <summary>Last title mapped from the window; there is no platform title setter in this slice.</summary>
    public string? Title { get; private set; }
}
