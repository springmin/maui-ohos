// Border handler for OpenHarmony: draws the stroke shape and arranges the content inside the
// border padding.
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
        };

    public OpenHarmonyBorderHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsBorder = true };

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
        OpenHarmonyView view = handler.PlatformView;
        view.Shape = border.Shape;
        if (border is IStroke stroke)
        {
            view.BorderStroke = (stroke.Stroke as SolidPaint)?.Color ?? Colors.Gray;
            view.BorderStrokeThickness = (float)stroke.StrokeThickness;
        }
    }
}
