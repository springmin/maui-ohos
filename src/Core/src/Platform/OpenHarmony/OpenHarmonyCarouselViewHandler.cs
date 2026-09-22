// CarouselView handler for OpenHarmony: shows the current item and pages on a horizontal
// swipe (no page animation yet). PeekAreaInsets materializes the neighbouring slides inside the
// platform view's clipped scroll path; Loop wraps the position and Position/CurrentItem are kept
// in sync. IsSwipeEnabled(false) pins the carousel; IsBounceEnabled has no compositor animation
// and is reported once instead of pretending.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyCarouselViewHandler : OpenHarmonyViewHandler<CarouselView>
{
    private const float SwipeThreshold = 50f;
    private IView? _current;
    private bool _syncingItem;

    public static readonly IPropertyMapper<CarouselView, OpenHarmonyCarouselViewHandler> Mapper =
        new PropertyMapper<CarouselView, OpenHarmonyCarouselViewHandler>(ViewMapper)
        {
            [nameof(ItemsView.ItemsSource)] = MapItems,
            [nameof(ItemsView.ItemTemplate)] = MapItems,
            [nameof(CarouselView.Position)] = MapPosition,
            [nameof(CarouselView.CurrentItem)] = MapCurrentItem,
            [nameof(CarouselView.Loop)] = MapItems,
            [nameof(CarouselView.PeekAreaInsets)] = MapItems,
            [nameof(CarouselView.IsBounceEnabled)] = MapIsBounceEnabled,
        };

    public OpenHarmonyCarouselViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { Background = Colors.Black };
        // Horizontal swipes change the position: re-use the pan recognizer plumbing by
        // handling the swipe in the platform view's touch. IsSwipeEnabled(false) keeps the
        // carousel pinned (the flag is read on every completed drag, so no mapper is needed).
        view.Swipe = (deltaX, _) =>
        {
            if (VirtualView is not { } carousel || !carousel.IsSwipeEnabled ||
                Math.Abs(deltaX) < SwipeThreshold)
            {
                return;
            }
            int count = Count(carousel);
            if (count == 0)
            {
                return;
            }
            int position = carousel.Position + (deltaX < 0 ? 1 : -1);
            // Loop around the ends when the carousel asks for it, otherwise clamp.
            position = carousel.Loop && count > 0
                ? (position % count + count) % count
                : Math.Clamp(position, 0, Math.Max(0, count - 1));
            carousel.Position = position;
            Rebuild();
            OpenHarmonyBridge.RequestRedraw();
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } carousel)
        {
            carousel.ScrollToRequested += OnScrollToRequested;
        }
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } carousel)
        {
            carousel.ScrollToRequested -= OnScrollToRequested;
        }
        base.DisconnectHandler(platformView);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), ResolveHeight(heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        Rebuild();
    }

    private void Rebuild()
    {
        // The previous slides are slice-created views; drop their handlers before the window is
        // rebuilt (an arrange/swipe would otherwise leak one handler per slide).
        foreach (IView child in PlatformView.ViewChildren)
        {
            child.Handler?.DisconnectHandler();
        }
        PlatformView.ViewChildren.Clear();
        _current = null;
        if (VirtualView is not { } carousel || Count(carousel) == 0 || carousel.ItemsSource is not { } source)
        {
            PlatformView.IsScrollView = false;
            return;
        }
        int count = Count(carousel);
        int position = Math.Clamp(carousel.Position, 0, count - 1);
        Thickness peek = carousel.PeekAreaInsets;
        bool peeked = peek.Left != 0 || peek.Right != 0 || peek.Top != 0 || peek.Bottom != 0;
        // The peek strips need the renderer's clip + translate path (IsScrollView); without peeks
        // the carousel keeps the plain single-slide arrangement.
        PlatformView.IsScrollView = peeked;
        Rect frame = PlatformView.Frame;
        double itemWidth = Math.Max(1, frame.Width - peek.Left - peek.Right);
        double itemHeight = Math.Max(1, frame.Height - peek.Top - peek.Bottom);
        double itemX = frame.X + peek.Left;
        double itemY = frame.Y + peek.Top;
        _current = AddSlide(source, carousel, count, position, itemX, itemY, itemWidth, itemHeight);
        if (peeked)
        {
            // The neighbouring slides sit one slide width away and are clipped by the frame, so
            // the Left/Right (and Top/Bottom) peek strips show their edges.
            PlatformView.ScrollOffsetX = 0;
            PlatformView.ScrollOffsetY = 0;
            PlatformView.ScrollContentWidth = (float)frame.Width;
            PlatformView.ScrollContentHeight = (float)frame.Height;
            AddSlide(source, carousel, count, position - 1, itemX - itemWidth, itemY, itemWidth, itemHeight);
            AddSlide(source, carousel, count, position + 1, itemX + itemWidth, itemY, itemWidth, itemHeight);
        }
    }

    private View? AddSlide(System.Collections.IEnumerable source, CarouselView carousel, int count,
        int index, double x, double y, double width, double height)
    {
        if (index < 0 || index >= count)
        {
            if (!carousel.Loop)
            {
                return null;
            }
            index = (index % count + count) % count;
        }
        object? item = source.Cast<object?>().ElementAtOrDefault(index);
        View? view = null;
        if (carousel.ItemTemplate?.CreateContent() is View templated)
        {
            view = templated;
        }
        else
        {
            view = new Label { Text = item?.ToString() ?? string.Empty, FontSize = 30, TextColor = Colors.White };
        }
        view.BindingContext = item;
        OpenHarmonyHandlerConnector.ConnectTree(view);
        view.Measure(width, height);
        view.Arrange(new Rect(x, y, width, height));
        PlatformView.ViewChildren.Add(view);
        return view;
    }

    /// <summary>Infinite constraints arrive from stack layouts; fall back to HeightRequest.</summary>
    private double ResolveHeight(double heightConstraint)
    {
        if (VirtualView?.HeightRequest > 0)
        {
            return VirtualView.HeightRequest;
        }
        return double.IsFinite(heightConstraint) ? heightConstraint : 200;
    }

    private static int Count(CarouselView carousel)
        => carousel.ItemsSource?.Cast<object?>().Count() ?? 0;

    /// <summary>Mirrors the selected slide into CurrentItem (Position is the source of truth).</summary>
    private void SyncCurrentItem()
    {
        if (_syncingItem || VirtualView is not { } carousel)
        {
            return;
        }
        int count = Count(carousel);
        object? item = count > 0 && carousel.ItemsSource is { } source
            ? source.Cast<object?>().ElementAtOrDefault(Math.Clamp(carousel.Position, 0, count - 1))
            : null;
        if (Equals(carousel.CurrentItem, item))
        {
            return;
        }
        _syncingItem = true;
        try
        {
            carousel.CurrentItem = item;
        }
        finally
        {
            _syncingItem = false;
        }
    }

    /// <summary>Moves the position to the slide an app assigned through CurrentItem.</summary>
    private void SyncPositionFromCurrentItem()
    {
        if (_syncingItem || VirtualView is not { } carousel || carousel.ItemsSource is not { } source)
        {
            return;
        }
        int index = 0;
        bool found = false;
        foreach (object? item in source)
        {
            if (Equals(item, carousel.CurrentItem))
            {
                found = true;
                break;
            }
            index++;
        }
        if (!found || index == carousel.Position)
        {
            return;
        }
        _syncingItem = true;
        try
        {
            carousel.Position = index;
        }
        finally
        {
            _syncingItem = false;
        }
    }

    private void OnScrollToRequested(object? sender, ScrollToRequestEventArgs args)
    {
        if (VirtualView is not { } carousel || carousel.ItemsSource is not { } source || Count(carousel) == 0)
        {
            return;
        }
        int index;
        if (args.Mode == ScrollToMode.Position)
        {
            index = args.Index;
        }
        else
        {
            index = 0;
            bool found = false;
            foreach (object? item in source)
            {
                if (Equals(item, args.Item))
                {
                    found = true;
                    break;
                }
                index++;
            }
            if (!found)
            {
                return;
            }
        }
        if (index < 0 || index >= Count(carousel) || index == carousel.Position)
        {
            return;
        }
        if (args.IsAnimated)
        {
            OpenHarmonyStatus.Once("carousel.scrollto.animated",
                "CarouselView.ScrollTo jumps to the slide; the compositor has no page animation");
        }
        // PositionChanged fires through the bindable property, like the swipe path.
        carousel.Position = index;
        Rebuild();
    }

    public static void MapItems(OpenHarmonyCarouselViewHandler handler, CarouselView carousel)
        => handler.Rebuild();

    public static void MapPosition(OpenHarmonyCarouselViewHandler handler, CarouselView carousel)
    {
        handler.Rebuild();
        handler.SyncCurrentItem();
    }

    public static void MapCurrentItem(OpenHarmonyCarouselViewHandler handler, CarouselView carousel)
    {
        handler.SyncPositionFromCurrentItem();
        handler.Rebuild();
    }

    public static void MapIsBounceEnabled(OpenHarmonyCarouselViewHandler handler, CarouselView carousel)
    {
        if (carousel.IsBounceEnabled && !carousel.Loop)
        {
            OpenHarmonyStatus.Once("carousel.bounce",
                "CarouselView.IsBounceEnabled clamps at the ends; the compositor has no bounce animation");
        }
    }
}
