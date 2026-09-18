// BoxView handler for OpenHarmony: a plain colour rectangle (MAUI's simplest drawing control).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyBoxViewHandler : OpenHarmonyViewHandler<BoxView>
{
    public static readonly IPropertyMapper<BoxView, OpenHarmonyBoxViewHandler> Mapper =
        new PropertyMapper<BoxView, OpenHarmonyBoxViewHandler>(ViewMapper)
        {
            [nameof(BoxView.Color)] = MapColor,
            [nameof(BoxView.CornerRadius)] = MapColor,
        };

    public OpenHarmonyBoxViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as VisualElement;
        double width = element?.WidthRequest > 0 ? element.WidthRequest : Math.Min(40, widthConstraint);
        double height = element?.HeightRequest > 0 ? element.HeightRequest : Math.Min(40, heightConstraint);
        return new Size(Math.Min(width, widthConstraint), Math.Min(height, heightConstraint));
    }

    public static void MapColor(OpenHarmonyBoxViewHandler handler, BoxView box)
    {
        handler.PlatformView.Background = box.Color ?? Colors.Transparent;
        if (box.CornerRadius.TopLeft > 0)
        {
            handler.PlatformView.CornerRadius = (float)box.CornerRadius.TopLeft;
        }
    }
}
