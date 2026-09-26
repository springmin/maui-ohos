// CollectionView handler for OpenHarmony: virtualized vertical list (shared materializer).
// Selection (single and multiple), EmptyView, header/footer rows, the remaining-items threshold
// and ScrollTo requests are all handled through the materializer; features the compositor cannot
// express are reported once through OpenHarmonyStatus.Once instead of failing silently.
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
            [nameof(ItemsView.EmptyView)] = MapEmptyView,
            [nameof(ItemsView.EmptyViewTemplate)] = MapEmptyView,
            [nameof(StructuredItemsView.Header)] = MapHeaderFooter,
            [nameof(StructuredItemsView.HeaderTemplate)] = MapHeaderFooter,
            [nameof(StructuredItemsView.Footer)] = MapHeaderFooter,
            [nameof(StructuredItemsView.FooterTemplate)] = MapHeaderFooter,
            [nameof(SelectableItemsView.SelectedItem)] = MapSelectedItem,
            [nameof(SelectableItemsView.SelectedItems)] = MapSelectedItems,
            [nameof(SelectableItemsView.SelectionMode)] = MapSelectionMode,
            [nameof(ItemsView.RemainingItemsThreshold)] = MapRemainingItemsThreshold,
        };

    public OpenHarmonyCollectionViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsScrollView = true };
        view.ScrollOffsetChanged = () => _materializer?.Update();
        _materializer = new OpenHarmonyItemListMaterializer(view, CreateItemView, Select)
        {
            listHeaderFactory = CreateListHeader,
            listFooterFactory = CreateListFooter,
            emptyViewFactory = CreateEmptyView,
        };
        _materializer.windowChanged = ReportWindowChanged;
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } collection)
        {
            collection.ScrollToRequested += OnScrollToRequested;
        }
        VirtualView?.InvalidateMeasure();
        OpenHarmonyBridge.RequestRedraw();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } collection)
        {
            collection.ScrollToRequested -= OnScrollToRequested;
        }
        base.DisconnectHandler(platformView);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        double total = _materializer?.TotalHeight ?? 0;
        if (_materializer is { IsEmpty: true } empty && empty.EmptyHeight > total)
        {
            total = empty.EmptyHeight;
        }
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
            _materializer.listHeaderFactory = CreateListHeader;
            _materializer.listFooterFactory = CreateListFooter;
            _materializer.emptyViewFactory = CreateEmptyView;
            _materializer.SetItems(collection.ItemsSource, collection.IsGrouped);
            _materializer.Update(force: true);
            RefreshSelection(this, collection);
        }
        PlatformView.ScrollContentWidth = (float)frame.Width;
        PlatformView.ScrollContentHeight = (float)(_materializer?.TotalHeight ?? 0);
    }

    /// <summary>Rows of the header/footer templates (a View, a string or a bound template).</summary>
    private View? CreateListHeader() => CreateListExtra(VirtualView?.HeaderTemplate, VirtualView?.Header);

    private View? CreateListFooter() => CreateListExtra(VirtualView?.FooterTemplate, VirtualView?.Footer);

    /// <summary>EmptyView content: template, view, or the default centred text.</summary>
    private View? CreateEmptyView()
    {
        if (VirtualView is not { } collection)
        {
            return null;
        }
        View? templated = RealizeTemplate(collection.EmptyViewTemplate, collection.EmptyView);
        if (templated is not null)
        {
            return templated;
        }
        return collection.EmptyView switch
        {
            View view => ConnectExtra(view),
            "" or null => null,
            _ => CreateCenteredLabel(collection.EmptyView.ToString() ?? string.Empty),
        };
    }

    private static View? CreateListExtra(DataTemplate? template, object? content)
    {
        View? templated = RealizeTemplate(template, content);
        if (templated is not null)
        {
            return templated;
        }
        return content switch
        {
            View view => ConnectExtra(view),
            "" or null => null,
            _ => CreateCenteredLabel(content.ToString() ?? string.Empty),
        };
    }

    private static View? RealizeTemplate(DataTemplate? template, object? content)
    {
        if (template is null || template.CreateContent() is not View view)
        {
            return null;
        }
        view.BindingContext = content;
        OpenHarmonyHandlerConnector.ConnectTree(view);
        return view;
    }

    private static View ConnectExtra(View view)
    {
        OpenHarmonyHandlerConnector.ConnectTree(view);
        return view;
    }

    /// <summary>A stretched grid with a centred label (the canvas draws strings left-aligned).</summary>
    private static View CreateCenteredLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 26,
            TextColor = Colors.Gray,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        var host = new Grid { Children = { label } };
        OpenHarmonyHandlerConnector.ConnectTree(host);
        return host;
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
        if (VirtualView is not { } collection)
        {
            return;
        }
        switch (collection.SelectionMode)
        {
            case SelectionMode.None:
                return;
            case SelectionMode.Multiple:
                // Multiple selection toggles the row in the SelectedItems collection (the
                // collection raises the change notifications; SelectedItem stays untouched).
                if (item is null || collection.SelectedItems is not { } selected)
                {
                    return;
                }
                if (selected.Contains(item))
                {
                    selected.Remove(item);
                }
                else
                {
                    selected.Add(item);
                }
                break;
            default:
                collection.SelectedItem = item;
                break;
        }
        RefreshSelection(this, collection);
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

    /// <summary>Raises RemainingItemsThresholdReached when the visible window enters the threshold.</summary>
    private void ReportWindowChanged()
    {
        if (VirtualView is not { } collection || _materializer is not { } materializer || materializer.IsEmpty)
        {
            return;
        }
        int last = materializer.LastVisibleItemIndex;
        if (last < 0)
        {
            return;
        }
        int count = materializer.ItemCount;
        switch (collection.RemainingItemsThreshold)
        {
            case -1:
                return;
            case 0:
                if (last == count - 1)
                {
                    collection.SendRemainingItemsThresholdReached();
                }
                break;
            default:
                // Mirrors the platform semantics: fire while at most Threshold items remain.
                if (count - 1 - last <= collection.RemainingItemsThreshold)
                {
                    collection.SendRemainingItemsThresholdReached();
                }
                break;
        }
    }

    private void OnScrollToRequested(object? sender, ScrollToRequestEventArgs args)
    {
        if (_materializer is not { } materializer)
        {
            return;
        }
        int row = args.Mode == ScrollToMode.Position
            ? (args.GroupIndex >= 0
                ? materializer.RowForGroupItemIndex(args.GroupIndex, args.Index)
                : materializer.RowForItemIndex(args.Index))
            : (args.Group is not null
                ? materializer.RowForGroupItem(args.Group, args.Item)
                : materializer.RowForItem(args.Item));
        if (row < 0)
        {
            return;
        }
        if (args.IsAnimated)
        {
            OpenHarmonyStatus.Once("collection.scrollto.animated",
                "CollectionView.ScrollTo jumps to the target; the compositor has no scroll animation");
        }
        // A jump requested by app code takes over from an in-flight fling.
        OpenHarmonyScrollPhysics.Cancel(PlatformView);
        materializer.ScrollTo(row, args.ScrollToPosition);
    }

    public static void MapItemsSource(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        handler._materializer?.SetItems(collectionView.ItemsSource, collectionView.IsGrouped);
        handler._materializer?.Update(force: true);
        RefreshSelection(handler, collectionView);
    }

    public static void MapItemTemplate(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.Reset();

    public static void MapEmptyView(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.ResetExtras();

    public static void MapHeaderFooter(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.ResetExtras();

    public static void MapRemainingItemsThreshold(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler.ReportWindowChanged();

    /// <summary>Highlights the selected row(s) by tinting the materialized items.</summary>
    public static void MapSelectedItem(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => RefreshSelection(handler, collectionView);

    public static void MapSelectedItems(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => RefreshSelection(handler, collectionView);

    public static void MapSelectionMode(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        // Switching the mode to None drops the selection (the upstream platform handlers do the
        // same); without this the last highlight would survive a disabled selection.
        if (collectionView.SelectionMode == SelectionMode.None)
        {
            collectionView.SelectedItem = null;
            collectionView.SelectedItems?.Clear();
        }
        RefreshSelection(handler, collectionView);
    }

    private static void RefreshSelection(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        if (handler._materializer is not { } materializer)
        {
            return;
        }
        bool multiple = collectionView.SelectionMode == SelectionMode.Multiple;
        bool none = collectionView.SelectionMode == SelectionMode.None;
        foreach (View? child in materializer.MaterializedItems)
        {
            // Defensive: a snapshot row can be null/handler-less if a source swap lands between
            // the materializer's snapshot and this walk (see MaterializedItems).
            if (child is null || child.Handler?.PlatformView is not OpenHarmonyView itemPlatform)
            {
                continue;
            }
            object? context = child.BindingContext;
            bool selected = !none && context is not null &&
                            (multiple
                                ? collectionView.SelectedItems?.Contains(context) == true
                                : Equals(context, collectionView.SelectedItem));
            itemPlatform.Background = selected ? Colors.DodgerBlue.WithAlpha(0.35f) : null;
        }
        OpenHarmonyBridge.RequestRedraw();
    }
}
