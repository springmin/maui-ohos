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
    // One-shot RemainingItemsThreshold: fires when the window enters the threshold zone and
    // re-arms when it leaves (or when the item count changes), so a scroll that stays near the
    // bottom does not raise Reached on every frame.
    private bool _thresholdArmed = true;
    private bool _thresholdFiring;
    private int _thresholdCount = -1;
    private bool _groupHeaderTogglesCollapse;

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
            [nameof(GroupableItemsView.GroupFooterTemplate)] = MapGroupFooter,
            [nameof(SelectableItemsView.SelectedItem)] = MapSelectedItem,
            [nameof(SelectableItemsView.SelectedItems)] = MapSelectedItems,
            [nameof(SelectableItemsView.SelectionMode)] = MapSelectionMode,
            [nameof(ItemsView.RemainingItemsThreshold)] = MapRemainingItemsThreshold,
            [nameof(ItemsView.ItemsUpdatingScrollMode)] = MapItemsUpdatingScrollMode,
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
            groupHeaderTapped = ToggleGroupCollapsed,
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
            _materializer.footerTextFactory = FooterText;
            _materializer.footerViewFactory = FooterView;
            _materializer.groupFootersEnabled = HasGroupFooters(collection);
            _materializer.UpdateMode = collection.ItemsUpdatingScrollMode;
            _materializer.GroupHeaderTapToCollapse = _groupHeaderTogglesCollapse;
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

    private View FooterView(string text)
        => new Label { Text = text, FontSize = 22, TextColor = Colors.Gray };

    private string HeaderText(object? group)
    {
        if (VirtualView?.GroupHeaderTemplate?.CreateContent() is Label label)
        {
            label.BindingContext = group;
            return label.Text ?? group?.ToString() ?? string.Empty;
        }
        return group?.ToString() ?? string.Empty;
    }

    /// <summary>Group footer text: the template's label text, else the group's own text.</summary>
    private string FooterText(object? group)
    {
        if (VirtualView?.GroupFooterTemplate is { } template)
        {
            if (template.CreateContent() is Label label)
            {
                label.BindingContext = group;
                return label.Text ?? string.Empty;
            }
            return ReportFooterViewUnsupported();
        }
        return group?.ToString() ?? string.Empty;
    }

    /// <summary>True when the grouped source should draw a footer row after every group.</summary>
    private static bool HasGroupFooters(CollectionView collection)
        => collection.IsGrouped && collection.GroupFooterTemplate is not null;

    private static string ReportFooterViewUnsupported()
    {
        OpenHarmonyStatus.Once("collection.groupfooter.view",
            "GroupFooterTemplate must create a Label; its text is drawn as the group footer row");
        return string.Empty;
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

    /// <summary>
    /// Raises RemainingItemsThresholdReached once when the visible window enters the threshold
    /// zone. The trigger re-arms when the window leaves the zone or the item count changes, so
    /// continuous scrolling inside the zone (and a Reached handler that grows the source) never
    /// fires per frame.
    /// </summary>
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
        if (count != _thresholdCount)
        {
            // A new source (incremental load) starts a fresh zone even if the viewport position
            // was already inside the old one.
            _thresholdCount = count;
            _thresholdArmed = true;
        }
        int threshold = collection.RemainingItemsThreshold;
        if (threshold == -1)
        {
            return;
        }
        bool inZone = threshold == 0 ? last == count - 1 : count - 1 - last <= threshold;
        if (!inZone)
        {
            _thresholdArmed = true;
            return;
        }
        if (!_thresholdArmed || _thresholdFiring)
        {
            return;
        }
        _thresholdFiring = true;
        try
        {
            collection.SendRemainingItemsThresholdReached();
        }
        finally
        {
            _thresholdFiring = false;
            _thresholdArmed = false;
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
        // A jump requested by app code takes over from an in-flight fling and owns the offset
        // from here; animate: true slides the window through the shared frame loop instead of
        // teleporting (reduced motion snaps to the target).
        OpenHarmonyScrollPhysics.Cancel(PlatformView);
        materializer.ScrollTo(row, args.ScrollToPosition, args.IsAnimated);
    }

    /// <summary>Collapses/expands a group (the public surface is OpenHarmonyCollectionViewExtensions).</summary>
    internal bool SetGroupCollapsed(object? group, bool collapsed)
        => _materializer?.SetGroupCollapsed(group, collapsed) == true;

    /// <summary>True when the group is collapsed in the materializer's viewport projection.</summary>
    internal bool IsGroupCollapsed(object? group)
        => _materializer?.IsGroupCollapsed(group) == true;

    /// <summary>Opt-in: tapping a group header row toggles that group's collapsed state.</summary>
    internal void SetGroupHeaderTogglesCollapse(bool enabled)
    {
        _groupHeaderTogglesCollapse = enabled;
        if (_materializer is not null)
        {
            _materializer.GroupHeaderTapToCollapse = enabled;
            _materializer.RefreshGroupHeaderTaps();
        }
    }

    /// <summary>Header tap path: invalidates the group through the materializer.</summary>
    private void ToggleGroupCollapsed(object? group)
    {
        if (group is null || _materializer is null)
        {
            return;
        }
        _materializer.SetGroupCollapsed(group, !_materializer.IsGroupCollapsed(group));
    }

    public static void MapItemsSource(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        handler._materializer?.SetItems(collectionView.ItemsSource, collectionView.IsGrouped);
        handler._materializer?.Update(force: true);
        handler.ReportWindowChanged();
        RefreshSelection(handler, collectionView);
    }

    public static void MapItemTemplate(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.Reset();

    public static void MapEmptyView(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.ResetExtras();

    public static void MapHeaderFooter(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
        => handler._materializer?.ResetExtras();

    /// <summary>Group footer structure changed: rebuild the row layout.</summary>
    public static void MapGroupFooter(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        if (handler._materializer is { } materializer)
        {
            materializer.groupFootersEnabled = HasGroupFooters(collectionView);
            materializer.SetItems(collectionView.ItemsSource, collectionView.IsGrouped);
            materializer.Update(force: true);
        }
    }

    public static void MapRemainingItemsThreshold(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        // A new threshold starts a fresh zone: arm and evaluate once.
        handler._thresholdArmed = true;
        handler._thresholdCount = -1;
        handler.ReportWindowChanged();
    }

    public static void MapItemsUpdatingScrollMode(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        if (handler._materializer is { } materializer)
        {
            materializer.UpdateMode = collectionView.ItemsUpdatingScrollMode;
        }
    }

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
