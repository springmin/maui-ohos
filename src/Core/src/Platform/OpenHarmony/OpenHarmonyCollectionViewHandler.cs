// CollectionView handler for OpenHarmony: materializes ItemTemplate content for every item and
// stacks it vertically inside a scrollable viewport. Virtualization is not implemented yet, so
// all items are created up front (keep large virtualized lists on the roadmap).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyCollectionViewHandler : OpenHarmonyViewHandler<CollectionView>
{
    public static readonly IPropertyMapper<CollectionView, OpenHarmonyCollectionViewHandler> Mapper =
        new PropertyMapper<CollectionView, OpenHarmonyCollectionViewHandler>(ViewMapper)
        {
            [nameof(ItemsView.ItemsSource)] = MapItemsSource,
            [nameof(ItemsView.ItemTemplate)] = MapItemTemplate,
        };

    private readonly List<IView> _items = new();
    private double _contentHeight;

    public OpenHarmonyCollectionViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new()
    {
        // Collection views reuse the scroll view machinery (clip, translate, drag scrolling).
        IsScrollView = true,
    };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        // The parent layout may have measured this view before its handler existed; make sure it
        // measures again now that GetDesiredSize can build the items.
        VirtualView?.InvalidateMeasure();
        OpenHarmonyBridge.RequestRedraw();
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        double height = VirtualView?.HeightRequest > 0
            ? VirtualView.HeightRequest
            : Math.Min(_contentHeight > 0 ? _contentHeight : 200, heightConstraint);
        return new Size(widthConstraint, height);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        RebuildItems(frame.Width);
        double contentHeight = Math.Max(frame.Height, _contentHeight);
        PlatformView.ScrollContentWidth = (float)frame.Width;
        PlatformView.ScrollContentHeight = (float)contentHeight;
    }

    private void RebuildItems(double width)
    {
        PlatformView.ViewChildren.Clear();
        _items.Clear();
        RectF frame = PlatformView.Frame;
        double y = 0;
        if (VirtualView?.ItemsSource is not null)
        {
            foreach (object? item in VirtualView.ItemsSource)
            {
                IView view = CreateItemView(item);
                view.Measure(width, double.PositiveInfinity);
                Size size = view.DesiredSize;
                view.Arrange(new Rect(frame.X, frame.Y + y, width, size.Height));
                if (view.Handler?.PlatformView is OpenHarmonyView itemPlatform)
                {
                    // Tapping anywhere in an item selects it (the slice dispatches taps itself).
                    object? captured = item;
                    itemPlatform.Tap = () => Select(captured);
                }
                PlatformView.ViewChildren.Add(view);
                _items.Add(view);
                y += size.Height + 6;
            }
        }
        _contentHeight = y;
    }

    private void Select(object? item)
    {
        if (VirtualView is { } collection && collection.SelectionMode != SelectionMode.None)
        {
            collection.SelectedItem = item;
            OpenHarmonyBridge.RequestRedraw();
        }
    }

    private IView CreateItemView(object? item)
    {
        if (VirtualView?.ItemTemplate?.CreateContent() is View templated)
        {
            templated.BindingContext = item;
            OpenHarmonyHandlerConnector.ConnectTree(templated);
            return templated;
        }
        var label = new Label { Text = item?.ToString() ?? string.Empty, FontSize = 26 };
        OpenHarmonyHandlerConnector.Connect(label);
        return label;
    }

    public static void MapItemsSource(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler.RebuildItems(handler.PlatformView.Frame.Width > 0 ? handler.PlatformView.Frame.Width : 1080);

    public static void MapItemTemplate(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler.RebuildItems(handler.PlatformView.Frame.Width > 0 ? handler.PlatformView.Frame.Width : 1080);
}
