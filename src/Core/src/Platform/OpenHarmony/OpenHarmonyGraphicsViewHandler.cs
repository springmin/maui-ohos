// GraphicsView handler for OpenHarmony: the IDrawable paints through the compositor's canvas.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyGraphicsViewHandler : OpenHarmonyViewHandler<IGraphicsView>
{
    public static readonly IPropertyMapper<IGraphicsView, OpenHarmonyGraphicsViewHandler> Mapper =
        new PropertyMapper<IGraphicsView, OpenHarmonyGraphicsViewHandler>(ViewMapper);

    public OpenHarmonyGraphicsViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsGraphicsView = true };
        view.GraphicsTap = point =>
        {
            if (VirtualView is { } graphics)
            {
                // IGraphicsView interaction takes point arrays (multi-touch capable).
                var points = new[] { point };
                graphics.StartInteraction(points);
                graphics.EndInteraction(points, true);
            }
        };
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double width = element?.WidthRequest > 0 ? element.WidthRequest : Math.Min(200, widthConstraint);
        double height = element?.HeightRequest > 0 ? element.HeightRequest : Math.Min(200, heightConstraint);
        return new Size(Math.Min(width, widthConstraint), Math.Min(height, heightConstraint));
    }
}
