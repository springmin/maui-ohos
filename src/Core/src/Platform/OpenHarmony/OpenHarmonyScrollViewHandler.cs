// ScrollView handler for OpenHarmony: keeps the platform view's offsets in sync; the
// renderer clips and translates the content, and drags update the offsets.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyScrollViewHandler : OpenHarmonyViewHandler<IScrollView>
{
    public static readonly IPropertyMapper<IScrollView, OpenHarmonyScrollViewHandler> Mapper =
        new PropertyMapper<IScrollView, OpenHarmonyScrollViewHandler>(ViewMapper)
        {
            [nameof(IScrollView.ContentSize)] = MapContentSize,
            [nameof(IScrollView.VerticalOffset)] = MapVerticalOffset,
            [nameof(IScrollView.HorizontalOffset)] = MapHorizontalOffset,
        };

    public OpenHarmonyScrollViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsScrollView = true };

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if (VirtualView is Microsoft.Maui.Controls.ScrollView { Content: IView content } scrollView)
        {
            Size contentSize = content.Measure(widthConstraint, double.PositiveInfinity);
            PlatformView.ScrollContentWidth = (float)contentSize.Width;
            PlatformView.ScrollContentHeight = (float)contentSize.Height;
            // HeightRequest acts as a fixed viewport (a scroll view must be smaller than its
            // content for scrolling to be possible).
            double height = scrollView.HeightRequest > 0
                ? scrollView.HeightRequest
                : Math.Min(contentSize.Height, heightConstraint);
            return new Size(Math.Min(contentSize.Width, widthConstraint), Math.Min(height, heightConstraint));
        }
        return base.GetDesiredSize(widthConstraint, heightConstraint);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (VirtualView is Microsoft.Maui.Controls.ScrollView { Content: IView content })
        {
            content.Measure(frame.Width, double.PositiveInfinity);
            double contentHeight = Math.Max(frame.Height, content.DesiredSize.Height);
            content.Arrange(new Rect(0, 0, frame.Width, contentHeight));
            PlatformView.ScrollContentWidth = (float)frame.Width;
            PlatformView.ScrollContentHeight = (float)contentHeight;
        }
    }

    public static void MapContentSize(OpenHarmonyScrollViewHandler handler, IScrollView scrollView)
    {
        handler.PlatformView.ScrollContentWidth = (float)scrollView.ContentSize.Width;
        handler.PlatformView.ScrollContentHeight = (float)scrollView.ContentSize.Height;
    }

    public static void MapVerticalOffset(OpenHarmonyScrollViewHandler handler, IScrollView scrollView)
        => handler.PlatformView.ScrollOffsetY = (float)scrollView.VerticalOffset;

    public static void MapHorizontalOffset(OpenHarmonyScrollViewHandler handler, IScrollView scrollView)
        => handler.PlatformView.ScrollOffsetX = (float)scrollView.HorizontalOffset;
}
