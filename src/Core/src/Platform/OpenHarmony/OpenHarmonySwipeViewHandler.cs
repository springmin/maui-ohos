// SwipeView handler for OpenHarmony: the content is drawn by the compositor and a completed
// horizontal drag over the row reveals the leading/trailing SwipeItems as a panel of buttons.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySwipeViewHandler : OpenHarmonyViewHandler<SwipeView>
{
    public static readonly IPropertyMapper<SwipeView, OpenHarmonySwipeViewHandler> Mapper =
        new PropertyMapper<SwipeView, OpenHarmonySwipeViewHandler>(ViewMapper)
        {
            [nameof(SwipeView.LeftItems)] = MapItems,
            [nameof(SwipeView.RightItems)] = MapItems,
            ["IsOpen"] = MapIsOpen,
        };

    public OpenHarmonySwipeViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsSwipeView = true };
        view.Swipe = (dx, dy) => view.SwipeDrag(dx, dy);
        view.SwipeOpenChanged = open =>
        {
            if (VirtualView is ISwipeView swipeView && swipeView.IsOpen != open)
            {
                swipeView.IsOpen = open;
            }
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        RebuildItems();
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

    private void RebuildItems()
    {
        PlatformView.SwipeItems.Clear();
        if (VirtualView is { } swipeView)
        {
            var swipe = (ISwipeView)swipeView;
            ISwipeItems items = swipe.RightItems.Count > 0 ? swipe.RightItems : swipe.LeftItems;
            foreach (ISwipeItem item in items)
            {
                if (item is SwipeItem swipeItem && swipeItem.IsVisible)
                {
                    SwipeItem captured = swipeItem;
                    PlatformView.SwipeItems.Add((
                        captured.Text ?? string.Empty,
                        captured.BackgroundColor ?? Colors.DimGray,
                        () => Activate(captured)));
                }
            }
            PlatformView.SetSwipeOpen(((ISwipeView)swipeView).IsOpen, notify: false);
        }
    }

    private static void Activate(SwipeItem item)
    {
        if (!item.IsEnabled)
        {
            return;
        }
        // Same activation path as toolbar items: Clicked handlers and Commands both run.
        if (item is IMenuItemController controller)
        {
            controller.Activate();
        }
        else
        {
            item.Command?.Execute(item.CommandParameter);
        }
    }

    public static void MapItems(OpenHarmonySwipeViewHandler handler, SwipeView swipeView)
        => handler.RebuildItems();

    public static void MapIsOpen(OpenHarmonySwipeViewHandler handler, SwipeView swipeView)
        => handler.PlatformView.SetSwipeOpen(((ISwipeView)swipeView).IsOpen, notify: false);
}
