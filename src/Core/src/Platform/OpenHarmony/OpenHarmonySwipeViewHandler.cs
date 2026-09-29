// SwipeView handler for OpenHarmony: the content is drawn by the compositor and a completed
// horizontal drag over the row reveals the leading/trailing SwipeItems as a panel of buttons.
// The drag direction picks the side (left drag reveals RightItems, right drag LeftItems);
// SwipeItemView content and SwipeItem icons/text colours are overlaid on the revealed panel, and
// item kinds the panel cannot draw are reported once through OpenHarmonyStatus.Once.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySwipeViewHandler : OpenHarmonyViewHandler<SwipeView>
{
    // Minimum horizontal drag the platform view needs to open a side (OpenHarmonyView.SwipeDrag).
    private const float OpenThreshold = 40f;

    private readonly List<View?> _itemContent = new();
    // Overlay content is created once per SwipeItem (a side switch re-uses the same view instead
    // of re-creating the icon/colour stack on every drag).
    private readonly Dictionary<ISwipeItem, View?> _contentCache = new();
    private bool _sideInitialized;
    private bool _itemsToRight;

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
        view.Swipe = (dx, dy) => OnSwipeCompleted(dx, dy);
        view.SwipeOpenChanged = open =>
        {
            if (VirtualView is ISwipeView swipeView && swipeView.IsOpen != open)
            {
                swipeView.IsOpen = open;
            }
            RefreshItemContent();
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        RebuildItems();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        platformView.ViewChildren.Clear();
        platformView.SwipeItems.Clear();
        _itemContent.Clear();
        _contentCache.Clear();
        base.DisconnectHandler(platformView);
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
        RefreshItemContent();
    }

    /// <summary>A completed drag picks the side and lets the platform view reveal it.</summary>
    private void OnSwipeCompleted(float dx, float dy)
    {
        if (Math.Abs(dx) < OpenThreshold)
        {
            return;
        }
        // Dragging left reveals the trailing (right) items, dragging right the leading ones.
        RebuildItems(toRight: dx < 0);
        PlatformView.SwipeDrag(dx, dy);
        RefreshItemContent();
    }

    /// <summary>
    /// Materializes one side's items into the panel (the previous side's list is replaced).
    /// A null <paramref name="toRight"/> keeps the current side; the first call prefers the
    /// trailing items when both sides are populated.
    /// </summary>
    private void RebuildItems(bool? toRight = null)
    {
        PlatformView.SwipeItems.Clear();
        _itemContent.Clear();
        if (VirtualView is not { } swipeView)
        {
            return;
        }
        var swipe = (ISwipeView)swipeView;
        bool right = toRight ?? (_sideInitialized
            ? _itemsToRight
            : swipe.RightItems.Count > 0 || swipe.LeftItems.Count == 0);
        _sideInitialized = true;
        _itemsToRight = right;
        ISwipeItems items = right ? swipe.RightItems : swipe.LeftItems;
        foreach (ISwipeItem item in items)
        {
            switch (item)
            {
                case SwipeItem swipeItem when swipeItem.IsVisible:
                    AddItem(swipeItem, () => Activate(swipeItem));
                    break;
                case SwipeItemView itemView when itemView.IsVisible:
                    // Custom content is overlaid on the panel; the panel itself only provides the
                    // background colour and the hit target.
                    AddItem(string.Empty, itemView.BackgroundColor ?? Colors.DimGray,
                        () => Activate(itemView), ContentFor(itemView));
                    break;
                default:
                    OpenHarmonyStatus.Once("swipe.customitem",
                        "custom ISwipeItem implementations are not rendered here; use SwipeItem or SwipeItemView");
                    break;
            }
        }
        PlatformView.SetSwipeOpen(swipe.IsOpen, notify: false);
        // With every item invisible the panel cannot open; keep the virtual IsOpen honest.
        if (PlatformView.IsSwipeOpen != swipe.IsOpen)
        {
            swipe.IsOpen = PlatformView.IsSwipeOpen;
        }
        RefreshItemContent();
    }

    /// <summary>
    /// Adds a swipe item: the panel draws the text in its default colour, while an icon (or an
    /// explicit text colour) is drawn by an overlay view (the panel has no image API).
    /// </summary>
    private void AddItem(SwipeItem item, Action activate)
    {
        View? content = ContentFor(item);
        AddItem(content is null ? item.Text ?? string.Empty : string.Empty,
            item.BackgroundColor ?? Colors.DimGray, activate, content);
    }

    private void AddItem(string text, Color background, Action activate, View? content)
    {
        PlatformView.SwipeItems.Add((text, background, activate));
        _itemContent.Add(content);
    }

    /// <summary>Overlay content of an item (cached so drags do not re-create it).</summary>
    private View? ContentFor(ISwipeItem item)
    {
        if (_contentCache.TryGetValue(item, out View? cached))
        {
            return cached;
        }
        View? created = item switch
        {
            SwipeItem swipeItem => CreateSwipeItemContent(swipeItem),
            SwipeItemView itemView => itemView.Content as View,
            _ => null,
        };
        _contentCache[item] = created;
        return created;
    }

    private static View? CreateSwipeItemContent(SwipeItem item)
    {
        bool hasIcon = item.IconImageSource is not null;
        bool hasText = !string.IsNullOrEmpty(item.Text);
        if (!hasIcon && (!hasText || item.TextColor is null))
        {
            return null;
        }
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            Padding = new Thickness(4),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center,
        };
        if (hasIcon)
        {
            ImageSource source = item.IconImageSource!;
            if (source is FontImageSource font && item.IconColor is { } iconColor)
            {
                // The canvas draws font glyphs directly, so the tint is a source clone.
                source = new FontImageSource
                {
                    Glyph = font.Glyph,
                    FontFamily = font.FontFamily,
                    FontAutoScalingEnabled = font.FontAutoScalingEnabled,
                    Size = font.Size,
                    Color = iconColor,
                };
            }
            else if (item.IconColor is not null)
            {
                OpenHarmonyStatus.Once("swipe.iconcolor",
                    "SwipeItem.IconColor tints only FontImageSource glyphs on this platform");
            }
            stack.Add(new Image
            {
                Source = source,
                HeightRequest = 28,
                WidthRequest = 28,
                HorizontalOptions = LayoutOptions.Center,
            });
        }
        if (hasText)
        {
            stack.Add(new Label
            {
                Text = item.Text ?? string.Empty,
                FontSize = 22,
                TextColor = item.TextColor ?? Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            });
        }
        OpenHarmonyHandlerConnector.ConnectTree(stack);
        return stack;
    }

    /// <summary>Positions the custom/icon content over each revealed panel slot.</summary>
    private void RefreshItemContent()
    {
        PlatformView.ViewChildren.Clear();
        if (!PlatformView.IsSwipeOpen)
        {
            return;
        }
        for (int index = 0; index < _itemContent.Count && index < PlatformView.SwipeItems.Count; index++)
        {
            if (_itemContent[index] is not { } content)
            {
                continue;
            }
            // SwipeItemView content is a plain view from the app tree; its handler may not be
            // connected yet (only the SwipeView itself was).
            OpenHarmonyHandlerConnector.ConnectTree(content);
            RectF rect = PlatformView.SwipeItemRect(index);
            content.Measure(rect.Width, rect.Height);
            content.Arrange(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
            PlatformView.ViewChildren.Add(content);
        }
    }

    private static void Activate(ISwipeItem item)
    {
        if (item is SwipeItem swipeItem && !swipeItem.IsEnabled)
        {
            return;
        }
        if (item is SwipeItemView itemView && !itemView.IsEnabled)
        {
            return;
        }
        // SwipeItem's own invoked path is ISwipeItem.OnInvoked(), which raises Invoked (and runs
        // the command); SwipeItemView implements the same interface with its own OnInvoked.
        item.OnInvoked();
    }

    public static void MapItems(OpenHarmonySwipeViewHandler handler, SwipeView swipeView)
        => handler.RebuildItems();

    public static void MapIsOpen(OpenHarmonySwipeViewHandler handler, SwipeView swipeView)
    {
        handler.PlatformView.SetSwipeOpen(((ISwipeView)swipeView).IsOpen, notify: false);
        handler.RefreshItemContent();
    }
}
