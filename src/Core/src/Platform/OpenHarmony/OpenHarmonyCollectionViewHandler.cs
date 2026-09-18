// CollectionView handler for OpenHarmony: virtualized vertical list (shared materializer).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyCollectionViewHandler : OpenHarmonyViewHandler<CollectionView>
{
    private OpenHarmonyItemListMaterializer? _materializer;

    public static readonly IPropertyMapper<CollectionView, OpenHarmonyCollectionViewHandler> Mapper =
        new PropertyMapper<CollectionView, OpenHarmonyCollectionViewHandler>(ViewMapper)
        {
            [nameof(ItemsView.ItemsSource)] = MapItemsSource,
            [nameof(ItemsView.ItemTemplate)] = MapItemTemplate,
        };

    public OpenHarmonyCollectionViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsScrollView = true };
        view.ScrollOffsetChanged = () => _materializer?.Update();
        _materializer = new OpenHarmonyItemListMaterializer(view, CreateItemView, Select);
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView?.InvalidateMeasure();
        OpenHarmonyBridge.RequestRedraw();
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        double total = _materializer?.TotalHeight ?? 0;
        double height = VirtualView?.HeightRequest > 0
            ? VirtualView.HeightRequest
            : Math.Min(total > 0 ? total : 200, heightConstraint);
        return new Size(widthConstraint, height);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (_materializer is not null && VirtualView is { } collection)
        {
            _materializer.Span = collection.ItemsLayout is GridItemsLayout grid ? Math.Max(1, grid.Span) : 1;
            _materializer.headerTextFactory = HeaderText;
            _materializer.headerViewFactory = HeaderView;
            _materializer.SetItems(collection.ItemsSource, collection.IsGrouped);
            _materializer.Update(force: true);
        }
        PlatformView.ScrollContentWidth = (float)frame.Width;
        PlatformView.ScrollContentHeight = (float)(_materializer?.TotalHeight ?? 0);
    }

    private View HeaderView(string text)
        => new Label { Text = text, FontSize = 24, TextColor = Colors.Gold };

    private string HeaderText(object? group)
    {
        if (VirtualView?.GroupHeaderTemplate?.CreateContent() is Label label)
        {
            label.BindingContext = group;
            return label.Text ?? group?.ToString() ?? string.Empty;
        }
        return group?.ToString() ?? string.Empty;
    }

    private void Select(object? item)
    {
        if (VirtualView is { } collection && collection.SelectionMode != SelectionMode.None)
        {
            collection.SelectedItem = item;
            OpenHarmonyBridge.RequestRedraw();
        }
    }

    private View CreateItemView(object? item)
    {
        if (VirtualView?.ItemTemplate?.CreateContent() is View templated)
        {
            OpenHarmonyHandlerConnector.ConnectTree(templated);
            return templated;
        }
        var label = new Label { Text = item?.ToString() ?? string.Empty, FontSize = 26 };
        OpenHarmonyHandlerConnector.Connect(label);
        return label;
    }

    public static void MapItemsSource(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        handler._materializer?.SetItems(collectionView.ItemsSource);
        handler._materializer?.Update(force: true);
    }

    public static void MapItemTemplate(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.Reset();
}
