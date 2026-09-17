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
        };

    public OpenHarmonyWindowHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformElement() => new();

    public static void MapContent(OpenHarmonyWindowHandler handler, IWindow window)
    {
        // The app host renders window.Content; nothing to do here yet.
    }
}
