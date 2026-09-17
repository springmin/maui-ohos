// Shape handler for OpenHarmony: shapes are drawn from IShape.PathForBounds, so every
// Microsoft.Maui.Controls.Shapes element (Rectangle/Ellipse/Line/Path/Polygon/Polyline/...)
// works through this one handler.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyShapeHandler : OpenHarmonyViewHandler<IShapeView>
{
    public static readonly IPropertyMapper<IShapeView, OpenHarmonyShapeHandler> Mapper =
        new PropertyMapper<IShapeView, OpenHarmonyShapeHandler>(ViewMapper)
        {
            [nameof(IShapeView.Shape)] = MapShape,
            [nameof(IShapeView.Fill)] = MapShape,
            [nameof(IStroke.Stroke)] = MapShape,
            [nameof(IStroke.StrokeThickness)] = MapShape,
        };

    public OpenHarmonyShapeHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsShape = true };

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double width = element?.WidthRequest > 0 ? element.WidthRequest : 40;
        double height = element?.HeightRequest > 0 ? element.HeightRequest : 40;
        return new Size(Math.Min(width, widthConstraint), Math.Min(height, heightConstraint));
    }

    public static void MapShape(OpenHarmonyShapeHandler handler, IShapeView shapeView)
    {
        OpenHarmonyView view = handler.PlatformView;
        view.Shape = shapeView.Shape;
        view.ShapeFill = shapeView.Fill;
        if (shapeView is IStroke stroke)
        {
            view.ShapeStroke = (stroke.Stroke as SolidPaint)?.Color ?? Colors.White;
            view.ShapeStrokeThickness = (float)stroke.StrokeThickness;
        }
    }
}
