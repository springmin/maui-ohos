// CarouselView handler for OpenHarmony: shows the current item and pages on a horizontal
// swipe (no page animation yet). PeekAreaInsets materializes the neighbouring slides inside the
// platform view's clipped scroll path; Loop wraps the position and Position/CurrentItem are kept
// in sync. A grouped ItemsSource (elements are collections) is materialized to its items with a
// one-time status note - the carousel has no group concept, so headers/footers cannot be drawn.
// IsSwipeEnabled(false) pins the carousel; IsBounceEnabled has no compositor animation
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
        // The compositor arranges the tree on every frame, and a frame that neither moved nor
        // resized cannot change what the slides look like: every input that does (ItemsSource,
        // ItemTemplate, Position, CurrentItem, Loop, PeekAreaInsets) rebuilds through its mapper or
        // event, so an unchanged arrange keeps the existing slides instead of disconnecting and
        // recreating one handler-bearing view per frame.
        if (_arranged && _arrangedFrame == frame)
        {
            return;
        }
        _arranged = true;
        _arrangedFrame = frame;
        Rebuild();
    }

    private bool _arranged;
    private Rect _arrangedFrame;

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
        if (VirtualView is not { } carousel)
        {
            PlatformView.IsScrollView = false;
            return;
        }
        List<object?> items = MaterializeItems(carousel);
        if (items.Count == 0)
        {
            PlatformView.IsScrollView = false;
            return;
        }
        int position = Math.Clamp(carousel.Position, 0, items.Count - 1);
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
        _current = AddSlide(items, carousel, position, itemX, itemY, itemWidth, itemHeight);
        if (peeked)
        {
            // The neighbouring slides sit one slide width away and are clipped by the frame, so
            // the Left/Right (and Top/Bottom) peek strips show their edges.
            PlatformView.ScrollOffsetX = 0;
            PlatformView.ScrollOffsetY = 0;
            PlatformView.ScrollContentWidth = (float)frame.Width;
            PlatformView.ScrollContentHeight = (float)frame.Height;
            AddSlide(items, carousel, position - 1, itemX - itemWidth, itemY, itemWidth, itemHeight);
            AddSlide(items, carousel, position + 1, itemX + itemWidth, itemY, itemWidth, itemHeight);
        }
    }

    private View? AddSlide(List<object?> items, CarouselView carousel,
        int index, double x, double y, double width, double height)
    {
        if (index < 0 || index >= items.Count)
        {
            if (!carousel.Loop)
            {
                return null;
            }
            index = (index % items.Count + items.Count) % items.Count;
        }
        object? item = items[index];
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

    private static int Count(CarouselView carousel) => MaterializeItems(carousel).Count;

    // Page-indicator slide list. The renderer asks for the slide count on every frame, and a
    // rebuild asks for the list itself; materialising the ItemsSource is O(N) per call (plus the
    // grouping scan), so the list is cached per carousel (a weak key: the entry never keeps a
    // carousel alive) and handed out again while it is still the list the carousel shows: the
    // source reference is unchanged and - when the source is an ICollection - its element count
    // is unchanged, so an Add/Remove/Reset or a reassigned ItemsSource materialises fresh. A
    // grouped source (the count is the sum over the groups, which the outer collection's count
    // cannot predict) and a plain IEnumerable (every enumeration may differ) are never cached.
    // The returned list is read-only by contract: every caller only reads Count/index/items.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemsView, SlideCache> s_slides = new();

    private sealed class SlideCache
    {
        public object? Source;
        public int SourceCount = -1;
        public List<object?> Items = new();
    }

    /// <summary>
    /// The slides the carousel pages through. CarouselView has no grouping API in this MAUI
    /// version (it derives from <c>ItemsView</c>), so a grouped data source - every element is
    /// itself a non-string collection, the shape CollectionView uses for groups - is
    /// materialised into its items: each item gets a slide and group headers/footers cannot be
    /// drawn, which is reported once instead of silently binding a group object.
    /// </summary>
    internal static List<object?> MaterializeItems(ItemsView itemsView)
    {
        object? source = itemsView.ItemsSource;
        SlideCache? cached = null;
        if (source is System.Collections.ICollection collection
            && s_slides.TryGetValue(itemsView, out SlideCache? found)
            && ReferenceEquals(found.Source, source)
            && found.SourceCount == collection.Count)
        {
            return found.Items;
        }
        if (source is System.Collections.ICollection)
        {
            s_slides.TryGetValue(itemsView, out cached);
        }
        var items = new List<object?>();
        if (source is System.Collections.IEnumerable sequence)
        {
            foreach (object? item in sequence)
            {
                items.Add(item);
            }
        }
        if (items.Count == 0 || !items.All(IsGroup))
        {
            StoreSlides(itemsView, source, cached, items);
            return items;
        }
        OpenHarmonyStatus.Once("carousel.grouped",
            "CarouselView.ItemsSource is grouped (every item is a collection): each group's items " +
            "are shown as slides; group headers/footers cannot be expressed by this carousel");
        var flattened = new List<object?>();
        foreach (object? group in items)
        {
            foreach (object? item in (System.Collections.IEnumerable)group!)
            {
                flattened.Add(item);
            }
        }
        return flattened;
    }

    /// <summary>Caches a materialised slide list while the source is still the same collection.</summary>
    private static void StoreSlides(ItemsView itemsView, object? source, SlideCache? cached, List<object?> items)
    {
        if (source is not System.Collections.ICollection current)
        {
            return;
        }
        SlideCache entry = cached ?? new SlideCache();
        entry.Source = source;
        entry.SourceCount = current.Count;
        entry.Items = items;
        if (cached is null)
        {
            s_slides.Add(itemsView, entry);
        }
    }

    private static bool IsGroup(object? item)
        => item is System.Collections.IEnumerable && item is not string;

    /// <summary>Mirrors the selected slide into CurrentItem (Position is the source of truth).</summary>
    private void SyncCurrentItem()
    {
        if (_syncingItem || VirtualView is not { } carousel)
        {
            return;
        }
        List<object?> items = MaterializeItems(carousel);
        object? item = items.Count > 0
            ? items[Math.Clamp(carousel.Position, 0, items.Count - 1)]
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
        if (_syncingItem || VirtualView is not { } carousel)
        {
            return;
        }
        int index = 0;
        bool found = false;
        foreach (object? item in MaterializeItems(carousel))
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
        if (VirtualView is not { } carousel)
        {
            return;
        }
        List<object?> items = MaterializeItems(carousel);
        if (items.Count == 0)
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
            foreach (object? item in items)
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
        if (index < 0 || index >= items.Count || index == carousel.Position)
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
