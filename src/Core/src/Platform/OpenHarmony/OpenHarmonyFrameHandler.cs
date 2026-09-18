// Frame handler for OpenHarmony: border colour + corner radius around the content.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFrameHandler : OpenHarmonyViewHandler<Frame>
{
    public static readonly IPropertyMapper<Frame, OpenHarmonyFrameHandler> Mapper =
        new PropertyMapper<Frame, OpenHarmonyFrameHandler>(ViewMapper)
        {
            [nameof(Frame.BorderColor)] = MapBorder,
            [nameof(Frame.CornerRadius)] = MapBorder,
        };

    public OpenHarmonyFrameHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if (VirtualView?.Content is IView content)
        {
            Size size = content.Measure(widthConstraint, heightConstraint);
            return new Size(Math.Min(size.Width, widthConstraint), Math.Min(size.Height, heightConstraint));
        }
        return base.GetDesiredSize(widthConstraint, heightConstraint);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (VirtualView?.Content is IView content)
        {
            double padding = 6;
            var contentFrame = new Rect(frame.X + padding, frame.Y + padding,
                Math.Max(0, frame.Width - padding * 2), Math.Max(0, frame.Height - padding * 2));
            content.Measure(contentFrame.Width, contentFrame.Height);
            content.Arrange(contentFrame);
        }
    }

    public static void MapBorder(OpenHarmonyFrameHandler handler, Frame frame)
    {
        handler.PlatformView.Background = frame.BackgroundColor ?? Colors.Transparent;
        handler.PlatformView.StrokeColor = frame.BorderColor ?? Colors.Transparent;
        handler.PlatformView.StrokeThickness = 2;
        handler.PlatformView.CornerRadius = (float)frame.CornerRadius;
    }
}
