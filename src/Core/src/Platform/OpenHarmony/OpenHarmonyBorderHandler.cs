// Border handler for OpenHarmony: draws the stroke shape and arranges the content inside the
// border padding. The stroke and background keep their MAUI Paint so gradient/image brushes render
// through the canvas paint facilities (strokes are approximated to their first stop colour and
// reported once - the canvas strokes solid colours only).
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyBorderHandler : OpenHarmonyViewHandler<IBorderView>
{
    public static readonly IPropertyMapper<IBorderView, OpenHarmonyBorderHandler> Mapper =
        new PropertyMapper<IBorderView, OpenHarmonyBorderHandler>(ViewMapper)
        {
            [nameof(IBorderStroke.Shape)] = MapBorder,
            [nameof(IStroke.Stroke)] = MapBorder,
            [nameof(IStroke.StrokeThickness)] = MapBorder,
            [nameof(IView.Background)] = MapBackground,
        };

    public OpenHarmonyBorderHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new OpenHarmonyShapeView { IsBorder = true };

    private OpenHarmonyShapeView ShapeView => (OpenHarmonyShapeView)PlatformView;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if (Content is { } content)
        {
            Size size = content.Measure(widthConstraint, heightConstraint);
            return new Size(Math.Min(size.Width, widthConstraint), Math.Min(size.Height, heightConstraint));
        }
        return base.GetDesiredSize(widthConstraint, heightConstraint);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        // Only the content is arranged here: MAUI calls PlatformArrange from Arrange, so handing
        // the border itself to the content-arrange helper would recurse.
        if (Content is { } content)
        {
            var padding = (VirtualView as IPadding)?.Padding ?? Thickness.Zero;
            var contentFrame = new Rect(
                frame.X + padding.Left,
                frame.Y + padding.Top,
                Math.Max(0, frame.Width - padding.HorizontalThickness),
                Math.Max(0, frame.Height - padding.VerticalThickness));
            content.Measure(contentFrame.Width, contentFrame.Height);
            content.Arrange(contentFrame);
        }
    }

    // Border.PresentedContent reports the border itself in MAUI, which would recurse here; the
    // concrete Content property is what the border wraps.
    private IView? Content => (VirtualView as Microsoft.Maui.Controls.Border)?.Content as IView;

    public static void MapBorder(OpenHarmonyBorderHandler handler, IBorderView border)
    {
        OpenHarmonyShapeView view = handler.ShapeView;
        view.Shape = border.Shape;
        Paint? stroke = (border as IStroke)?.Stroke;
        view.ShapeStrokePaint = stroke;
        // Keep the solid colour mirror coherent for the base view state/tests; a non-solid paint
        // is drawn (or reported) by OpenHarmonyShapeView at paint time.
        view.BorderStroke = stroke is null
            ? Colors.Gray
            : OpenHarmonyPaintRenderer.ApproximateColor(stroke) ?? Colors.Gray;
        if (border is IStroke strokePart)
        {
            view.BorderStrokeThickness = (float)strokePart.StrokeThickness;
        }
    }

    /// <summary>Maps the border's background brush: solid through the base colour, paints through the canvas.</summary>
    public static void MapBackground(OpenHarmonyBorderHandler handler, IBorderView border)
    {
        Paint? background = (border as IView)?.Background;
        OpenHarmonyShapeView view = handler.ShapeView;
        view.BackgroundPaint = background is SolidPaint or null ? null : background;
        view.Background = (background as SolidPaint)?.Color;
    }
}
