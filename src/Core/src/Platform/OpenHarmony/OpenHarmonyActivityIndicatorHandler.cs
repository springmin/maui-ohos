// ActivityIndicator handler for OpenHarmony: a rotating arc redrawn by the renderer.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyActivityIndicatorHandler : OpenHarmonyViewHandler<IActivityIndicator>
{
    public static readonly IPropertyMapper<IActivityIndicator, OpenHarmonyActivityIndicatorHandler> Mapper =
        new PropertyMapper<IActivityIndicator, OpenHarmonyActivityIndicatorHandler>(ViewMapper)
        {
            [nameof(IActivityIndicator.IsRunning)] = MapIsRunning,
            [nameof(IActivityIndicator.Color)] = MapColor,
        };

    public OpenHarmonyActivityIndicatorHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsActivityIndicator = true };

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        // Retract the animation registration: a spinner whose handler is gone is not part of the
        // rendered tree any more, and the frame loop asks the registration count before walking.
        platformView.IsRunning = false;
        base.DisconnectHandler(platformView);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(24, widthConstraint), Math.Min(24, heightConstraint));

    public static void MapIsRunning(OpenHarmonyActivityIndicatorHandler handler, IActivityIndicator indicator)
        => handler.PlatformView.IsRunning = indicator.IsRunning;

    public static void MapColor(OpenHarmonyActivityIndicatorHandler handler, IActivityIndicator indicator)
    {
        if (indicator.Color is not null)
        {
            handler.PlatformView.IndicatorColor = indicator.Color;
        }
    }
}
