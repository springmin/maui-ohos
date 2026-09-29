// CarouselView handler for OpenHarmony: shows the current item and pages on a horizontal
// swipe (no page animation yet). PeekAreaInsets materializes the neighbouring slides inside the
// platform view's clipped scroll path; Loop wraps the position and Position/CurrentItem are kept
// in sync. A grouped ItemsSource (elements are collections) is materialized to its items; when
// the group templates from OpenHarmonyCarouselView are set, a slide is emitted for every group's
// header and footer too, so the grouping is expressible instead of only reported.
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
        // ItemTemplate, group templates, Position, CurrentItem, Loop, PeekAreaInsets) rebuilds
        // through its mapper, attached-property callback or event, so an unchanged arrange keeps
        // the existing slides instead of disconnecting and recreating one handler-bearing view
        // per frame.
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
        List<CarouselSlide> slides = MaterializeSlides(carousel);
        if (slides.Count == 0)
        {
            PlatformView.IsScrollView = false;
            return;
        }
        int position = Math.Clamp(carousel.Position, 0, slides.Count - 1);
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
        _current = AddSlide(slides, carousel, position, itemX, itemY, itemWidth, itemHeight);
        if (peeked)
        {
            // The neighbouring slides sit one slide width away and are clipped by the frame, so
            // the Left/Right (and Top/Bottom) peek strips show their edges.
            PlatformView.ScrollOffsetX = 0;
            PlatformView.ScrollOffsetY = 0;
            PlatformView.ScrollContentWidth = (float)frame.Width;
            PlatformView.ScrollContentHeight = (float)frame.Height;
            AddSlide(slides, carousel, position - 1, itemX - itemWidth, itemY, itemWidth, itemHeight);
            AddSlide(slides, carousel, position + 1, itemX + itemWidth, itemY, itemWidth, itemHeight);
        }
    }

    /// <summary>
    /// Re-materialises the slides after a group template changed (the attached properties call
    /// this; the template set also carries the rebuild for a carousel whose handler is not
    /// connected yet, which simply materialises on connect). CurrentItem follows the slide the
    /// position now points at, like every other position change.
    /// </summary>
    internal void RebuildFromGroupTemplates()
    {
        Rebuild();
        SyncCurrentItem();
        OpenHarmonyBridge.RequestRedraw();
    }

    private View? AddSlide(List<CarouselSlide> slides, CarouselView carousel,
        int index, double x, double y, double width, double height)
    {
        if (index < 0 || index >= slides.Count)
        {
            if (!carousel.Loop)
            {
                return null;
            }
            index = (index % slides.Count + slides.Count) % slides.Count;
        }
        CarouselSlide slide = slides[index];
        View? view = slide.Kind switch
        {
            CarouselSlideKind.GroupHeader => CreateGroupSlide(carousel, isFooter: false),
            CarouselSlideKind.GroupFooter => CreateGroupSlide(carousel, isFooter: true),
            _ => null,
        };
        if (view is null)
        {
            // An item slide, or a group slide whose template did not materialise a view: the
            // group object goes through the item template (or the text fallback), so a broken
            // group template still shows the slide instead of an empty page.
            if (carousel.ItemTemplate?.CreateContent() is View templated)
            {
                view = templated;
            }
            else
            {
                view = new Label { Text = slide.Data?.ToString() ?? string.Empty, FontSize = 30, TextColor = Colors.White };
            }
        }
        view.BindingContext = slide.Data;
        OpenHarmonyHandlerConnector.ConnectTree(view);
        view.Measure(width, height);
        view.Arrange(new Rect(x, y, width, height));
        PlatformView.ViewChildren.Add(view);
        return view;
    }

    /// <summary>
    /// A group header/footer slide: the group template's own view, bound to the group object (a
    /// Label template included - its bindings resolve to the group like any item template).
    /// Null when the template did not materialise a view (reported once; the caller falls back
    /// to the item template) or was cleared between materialisation and render.
    /// </summary>
    private static View? CreateGroupSlide(CarouselView carousel, bool isFooter)
    {
        DataTemplate? template = isFooter
            ? OpenHarmonyCarouselView.GetGroupFooterTemplate(carousel)
            : OpenHarmonyCarouselView.GetGroupHeaderTemplate(carousel);
        if (template is null)
        {
            return null;
        }
        if (template.CreateContent() is View view)
        {
            return view;
        }
        OpenHarmonyStatus.Once(
            isFooter ? "carousel.groupfooter.view" : "carousel.groupheader.view",
            "CarouselView group " + (isFooter ? "footer" : "header") +
            " template must create a View; the group is drawn through the item template instead");
        return null;
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

    private static int Count(CarouselView carousel) => MaterializeSlides(carousel).Count;

    // Slide list. The renderer asks for the slide count on every frame, and a rebuild asks for
    // the list itself; materialising the ItemsSource is O(N) per call (plus the grouping scan),
    // so the list is cached per carousel (a weak key: the entry never keeps a carousel alive)
    // and handed out again while it is still the list the carousel shows: the source reference
    // is unchanged and - when the source is an ICollection - its element count is unchanged, so
    // an Add/Remove/Reset or a reassigned ItemsSource materialises fresh. A grouped source (the
    // slide count is the sum over the groups, which the outer collection's count cannot predict,
    // and the group templates add header/footer slides on top) and a plain IEnumerable (every
    // enumeration may differ) are never cached. The returned list is read-only by contract:
    // every caller only reads Count/index/slides.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemsView, SlideCache> s_slides = new();

    private sealed class SlideCache
    {
        public object? Source;
        public int SourceCount = -1;
        public List<CarouselSlide> Slides = new();
    }

    /// <summary>One slide of the carousel: an item, or a group header/footer.</summary>
    internal readonly struct CarouselSlide
    {
        public CarouselSlide(object? data, CarouselSlideKind kind)
        {
            Data = data;
            Kind = kind;
        }

        /// <summary>The data bound to the slide: the item, or the group for header/footer slides.</summary>
        public object? Data { get; }

        public CarouselSlideKind Kind { get; }
    }

    internal enum CarouselSlideKind
    {
        Item,
        GroupHeader,
        GroupFooter,
    }

    /// <summary>
    /// The slides the carousel pages through. A grouped data source - every element is itself a
    /// non-string collection, the shape CollectionView uses for groups - is materialised into its
    /// items. When OpenHarmonyCarouselView.GroupHeaderTemplate/GroupFooterTemplate is set, a
    /// header/footer slide (bound to the group object) is emitted around every group; without
    /// them group headers/footers cannot be drawn (CarouselView has no group templates of its
    /// own), which is reported once instead of silently binding a group object.
    /// </summary>
    internal static List<CarouselSlide> MaterializeSlides(ItemsView itemsView)
    {
        object? source = itemsView.ItemsSource;
        SlideCache? cached = null;
        if (source is System.Collections.ICollection collection
            && s_slides.TryGetValue(itemsView, out SlideCache? found)
            && ReferenceEquals(found.Source, source)
            && found.SourceCount == collection.Count)
        {
            return found.Slides;
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
            var slides = new List<CarouselSlide>(items.Count);
            foreach (object? item in items)
            {
                slides.Add(new CarouselSlide(item, CarouselSlideKind.Item));
            }
            StoreSlides(itemsView, source, cached, slides);
            return slides;
        }
        CarouselView? carousel = itemsView as CarouselView;
        DataTemplate? headerTemplate = carousel is null ? null : OpenHarmonyCarouselView.GetGroupHeaderTemplate(carousel);
        DataTemplate? footerTemplate = carousel is null ? null : OpenHarmonyCarouselView.GetGroupFooterTemplate(carousel);
        var grouped = new List<CarouselSlide>();
        if (headerTemplate is null && footerTemplate is null)
        {
            OpenHarmonyStatus.Once("carousel.grouped",
                "CarouselView.ItemsSource is grouped (every item is a collection): each group's items " +
                "are shown as slides; set OpenHarmonyCarouselView.GroupHeaderTemplate/GroupFooterTemplate " +
                "to draw the group headers/footers as slides");
            foreach (object? group in items)
            {
                foreach (object? item in (System.Collections.IEnumerable)group!)
                {
                    grouped.Add(new CarouselSlide(item, CarouselSlideKind.Item));
                }
            }
            return grouped;
        }
        foreach (object? group in items)
        {
            if (headerTemplate is not null)
            {
                grouped.Add(new CarouselSlide(group, CarouselSlideKind.GroupHeader));
            }
            foreach (object? item in (System.Collections.IEnumerable)group!)
            {
                grouped.Add(new CarouselSlide(item, CarouselSlideKind.Item));
            }
            if (footerTemplate is not null)
            {
                grouped.Add(new CarouselSlide(group, CarouselSlideKind.GroupFooter));
            }
        }
        return grouped;
    }

    /// <summary>Caches a materialised slide list while the source is still the same collection.</summary>
    private static void StoreSlides(ItemsView itemsView, object? source, SlideCache? cached, List<CarouselSlide> slides)
    {
        if (source is not System.Collections.ICollection current)
        {
            return;
        }
        SlideCache entry = cached ?? new SlideCache();
        entry.Source = source;
        entry.SourceCount = current.Count;
        entry.Slides = slides;
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
        List<CarouselSlide> slides = MaterializeSlides(carousel);
        object? item = slides.Count > 0
            ? slides[Math.Clamp(carousel.Position, 0, slides.Count - 1)].Data
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
        foreach (CarouselSlide slide in MaterializeSlides(carousel))
        {
            if (Equals(slide.Data, carousel.CurrentItem))
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
        List<CarouselSlide> slides = MaterializeSlides(carousel);
        if (slides.Count == 0)
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
            foreach (CarouselSlide slide in slides)
            {
                if (Equals(slide.Data, args.Item))
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
        if (index < 0 || index >= slides.Count || index == carousel.Position)
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
