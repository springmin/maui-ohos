// IndicatorView handler for OpenHarmony: a row of dots driven by IIindicatorView.Count/Position.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyIndicatorViewHandler : OpenHarmonyViewHandler<IIndicatorView>
{
    public static readonly IPropertyMapper<IIndicatorView, OpenHarmonyIndicatorViewHandler> Mapper =
        new PropertyMapper<IIndicatorView, OpenHarmonyIndicatorViewHandler>(ViewMapper)
        {
            [nameof(IIndicatorView.Count)] = MapIndicator,
            [nameof(IIndicatorView.Position)] = MapIndicator,
            [nameof(IIndicatorView.IndicatorColor)] = MapIndicator,
            [nameof(IIndicatorView.SelectedIndicatorColor)] = MapIndicator,
            [nameof(IIndicatorView.IndicatorSize)] = MapIndicator,
        };

    public OpenHarmonyIndicatorViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsIndicatorView = true };

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(200, widthConstraint), Math.Min(20, heightConstraint));

    public static void MapIndicator(OpenHarmonyIndicatorViewHandler handler, IIndicatorView indicator)
    {
        OpenHarmonyView view = handler.PlatformView;
        view.IndicatorCount = indicator.Count;
        view.IndicatorPosition = indicator.Position;
        view.DotsColor = (indicator.IndicatorColor as SolidPaint)?.Color ?? Colors.Gray;
        view.SelectedDotsColor = (indicator.SelectedIndicatorColor as SolidPaint)?.Color ?? Colors.White;
        view.DotsSize = (float)indicator.IndicatorSize;
        OpenHarmonyBridge.RequestRedraw();
    }
}
