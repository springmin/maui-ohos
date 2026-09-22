// Shape handler for OpenHarmony: shapes are drawn from IShape.PathForBounds, so every
// Microsoft.Maui.Controls.Shapes element (Rectangle/Ellipse/Line/Path/Polygon/Polyline/...)
// works through this one handler. Fills and strokes keep their MAUI Paint, so gradient and image
// brushes render through the canvas paint facilities instead of being dropped by the solid-only
// path; the platform view reports paints the canvas cannot stroke.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyShapeHandler : OpenHarmonyViewHandler<IShapeView>
{
    public static readonly IPropertyMapper<IShapeView, OpenHarmonyShapeHandler> Mapper =
        new PropertyMapper<IShapeView, OpenHarmonyShapeHandler>(ViewMapper)
        {
            [nameof(IShapeView.Shape)] = MapShape,
            [nameof(IShapeView.Fill)] = MapFill,
            [nameof(IStroke.Stroke)] = MapStroke,
            [nameof(IStroke.StrokeThickness)] = MapStrokeThickness,
        };

    public OpenHarmonyShapeHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new OpenHarmonyShapeView { IsShape = true };

    private OpenHarmonyShapeView ShapeView => (OpenHarmonyShapeView)PlatformView;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double width = element?.WidthRequest > 0 ? element.WidthRequest : 40;
        double height = element?.HeightRequest > 0 ? element.HeightRequest : 40;
        return new Size(Math.Min(width, widthConstraint), Math.Min(height, heightConstraint));
    }

    public static void MapShape(OpenHarmonyShapeHandler handler, IShapeView shapeView)
    {
        OpenHarmonyShapeView view = handler.ShapeView;
        view.Shape = shapeView.Shape;
        view.ShapeFill = shapeView.Fill;
    }

    public static void MapFill(OpenHarmonyShapeHandler handler, IShapeView shapeView)
        => handler.ShapeView.ShapeFill = shapeView.Fill;

    public static void MapStroke(OpenHarmonyShapeHandler handler, IShapeView shapeView)
    {
        OpenHarmonyShapeView view = handler.ShapeView;
        Paint? stroke = (shapeView as IStroke)?.Stroke;
        view.ShapeStrokePaint = stroke;
        // Keep the solid colour mirror coherent for the base view state/tests; a non-solid paint
        // is drawn (or reported) by OpenHarmonyShapeView at paint time.
        view.ShapeStroke = stroke is null
            ? Colors.White
            : OpenHarmonyPaintRenderer.ApproximateColor(stroke) ?? Colors.White;
    }

    public static void MapStrokeThickness(OpenHarmonyShapeHandler handler, IShapeView shapeView)
        => handler.ShapeView.ShapeStrokeThickness = (float)((shapeView as IStroke)?.StrokeThickness ?? 0);
}
