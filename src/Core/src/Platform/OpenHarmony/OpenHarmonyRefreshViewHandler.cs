// RefreshView handler for OpenHarmony: a downward pull over the content starts a refresh, the
// spinner is drawn while IsRefreshing is true, and the content is arranged like a content view.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyRefreshViewHandler : OpenHarmonyViewHandler<RefreshView>
{
    public static readonly IPropertyMapper<RefreshView, OpenHarmonyRefreshViewHandler> Mapper =
        new PropertyMapper<RefreshView, OpenHarmonyRefreshViewHandler>(ViewMapper)
        {
            [nameof(RefreshView.IsRefreshing)] = MapIsRefreshing,
            [nameof(RefreshView.RefreshColor)] = MapRefreshColor,
        };

    public OpenHarmonyRefreshViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsRefreshView = true };
        view.Swipe = (dx, dy) => view.RefreshDrag(dx, dy);
        view.RefreshChanged = refreshing =>
        {
            if (VirtualView is { } refreshView && refreshView.IsRefreshing != refreshing)
            {
                refreshView.IsRefreshing = refreshing;
            }
        };
        return view;
    }

    private IView? Content => (VirtualView as IContentView)?.PresentedContent as IView;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if (VirtualView is VisualElement element && element.HeightRequest > 0)
        {
            return new Size(element.WidthRequest > 0 ? element.WidthRequest : widthConstraint, element.HeightRequest);
        }
        return Content is { } content
            ? content.Measure(widthConstraint, heightConstraint)
            : base.GetDesiredSize(widthConstraint, heightConstraint);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (Content is { } content)
        {
            OpenHarmonyHandlerConnector.ConnectTree(content);
            content.Measure(frame.Width, frame.Height);
            content.Arrange(frame);
        }
    }

    public static void MapIsRefreshing(OpenHarmonyRefreshViewHandler handler, RefreshView refreshView)
        => handler.PlatformView.IsRefreshing = refreshView.IsRefreshing;

    public static void MapRefreshColor(OpenHarmonyRefreshViewHandler handler, RefreshView refreshView)
    {
        if (refreshView.RefreshColor is { } color)
        {
            handler.PlatformView.RefreshColor = color;
        }
    }
}
