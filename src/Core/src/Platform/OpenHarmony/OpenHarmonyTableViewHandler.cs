// Legacy TableView handler for OpenHarmony: the tabular control renders on the shared
// virtualized list pipeline (the same one the ListView and CollectionView use). Rows are read
// through ITableViewController.Model — the platform contract Controls implements on TableView —
// so the built-in TableSectionModel and app-supplied TableModel subclasses both drive the
// section/cell layout: each section title (or header cell) becomes a section row and each cell
// is materialized by OpenHarmonyCellFactory, exactly like a ListView cell.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyTableViewHandler : OpenHarmonyViewHandler<TableView>
{
    private OpenHarmonyItemListMaterializer? _materializer;
    // The flat row projection (section header markers + cells) the materializer shows. Rebuilt
    // when ITableViewController.ModelChanged fires; the same list reference is handed back to
    // SetItems on every arrange so the unchanged-source fast path keeps the materialized window.
    private List<object?> _rows = new();

    public static readonly IPropertyMapper<TableView, OpenHarmonyTableViewHandler> Mapper =
        new PropertyMapper<TableView, OpenHarmonyTableViewHandler>(ViewMapper)
        {
            [nameof(TableView.RowHeight)] = MapRowHeight,
            [nameof(TableView.HasUnevenRows)] = MapHasUnevenRows,
        };

    public OpenHarmonyTableViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsScrollView = true };
        view.ScrollOffsetChanged = () => _materializer?.Update();
        _materializer = new OpenHarmonyItemListMaterializer(view, CreateItemView, Select)
        {
            // A row is one cell: the cell owns its bindings (they must keep the inherited
            // BindingContext) and its platform view carries the cell's content, so pooling a
            // row for a different cell would show the previous cell's text.
            BindRowContext = false,
            DisablePooling = true,
            // HasUnevenRows: a positive Cell.Height wins over the measured content, the Cell
            // RenderHeight precedence the platform renderers use.
            preferredItemHeight = PreferredRowHeight,
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } table)
        {
            table.ModelChanged += OnModelChanged;
        }
        RebuildRows();
        VirtualView?.InvalidateMeasure();
        OpenHarmonyBridge.RequestRedraw();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } table)
        {
            table.ModelChanged -= OnModelChanged;
        }
        base.DisconnectHandler(platformView);
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
        if (_materializer is not { } materializer || VirtualView is not { } table)
        {
            return;
        }
        // The height contract: HasUnevenRows measures each row (Cell.Height wins when positive),
        // otherwise RowHeight is the single slot height as before.
        ApplyRowMetrics(materializer, table);
        materializer.SetItems(_rows);
        materializer.Update(force: true);
        PlatformView.ScrollContentWidth = (float)frame.Width;
        PlatformView.ScrollContentHeight = (float)materializer.TotalHeight;
    }

    private void OnModelChanged(object? sender, EventArgs e) => RebuildRows();

    /// <summary>Projects ITableViewController.Model into the flat row list the pipeline shows.</summary>
    private void RebuildRows()
    {
        if (_materializer is not { } materializer || VirtualView is not { } table)
        {
            return;
        }
        _rows = BuildRows(table);
        materializer.SetItems(_rows);
        materializer.Update(force: true);
        OpenHarmonyBridge.RequestRedraw();
    }

    private static List<object?> BuildRows(TableView table)
    {
        ITableModel model = ((ITableViewController)table).Model;
        var rows = new List<object?>();
        int sectionCount = model.GetSectionCount();
        for (int section = 0; section < sectionCount; section++)
        {
            string title = model.GetSectionTitle(section) ?? string.Empty;
            Cell? header = model.GetHeaderCell(section);
            if (header is not null || !string.IsNullOrEmpty(title))
            {
                // A section without a title or header cell (the plain TableIntent data shape)
                // draws no header row, matching the platform renderers.
                rows.Add(new TableSectionHeader(header, title, model.GetSectionTextColor(section)));
            }
            int rowCount = model.GetRowCount(section);
            for (int row = 0; row < rowCount; row++)
            {
                Cell? cell = model.GetCell(section, row);
                rows.Add(cell ?? new TextCell { Text = model.GetItem(section, row)?.ToString() ?? string.Empty });
            }
        }
        return rows;
    }

    private View CreateItemView(object? item)
    {
        if (item is TableSectionHeader header)
        {
            return CreateSectionHeader(header);
        }
        return OpenHarmonyCellFactory.Create(item, item);
    }

    /// <summary>Section row: the model's header cell when it has one, else the title text.</summary>
    private static View CreateSectionHeader(TableSectionHeader header)
    {
        if (header.Cell is { } cell)
        {
            return OpenHarmonyCellFactory.Create(cell, cell);
        }
        var label = new Label
        {
            Text = header.Title,
            FontSize = 22,
            TextColor = header.TextColor ?? Colors.Gray,
            VerticalOptions = LayoutOptions.Center,
        };
        OpenHarmonyHandlerConnector.Connect(label);
        return label;
    }

    /// <summary>
    /// A tapped row selects through ITableViewController.Model.RowSelected, the path the platform
    /// renderers use: the model raises its ItemSelected and calls Cell.OnTapped. Section rows and
    /// disabled cells do not select.
    /// </summary>
    private void Select(object? item)
    {
        if (VirtualView is not { } table || item is not Cell cell || !cell.IsEnabled)
        {
            return;
        }
        ((ITableViewController)table).Model.RowSelected(cell);
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapRowHeight(OpenHarmonyTableViewHandler handler, TableView tableView)
    {
        if (handler._materializer is { } materializer)
        {
            ApplyRowMetrics(materializer, tableView);
            // The height contract only affects rows materialized after it is set; re-materializing
            // the window is what applies a change to the rows already on screen.
            materializer.Reset();
        }
    }

    /// <summary>
    /// TableView.HasUnevenRows switches the shared list pipeline to per-row measured heights:
    /// RowHeight (when positive) becomes the estimate for rows that have not been measured yet,
    /// and a positive Cell.Height wins over the measured content (the Cell.RenderHeight
    /// precedence). Turning it back off restores the one-RowHeight slot model.
    /// </summary>
    public static void MapHasUnevenRows(OpenHarmonyTableViewHandler handler, TableView tableView)
    {
        if (handler._materializer is { } materializer)
        {
            ApplyRowMetrics(materializer, tableView);
            // The rows already on screen keep their old frames until the window is rebuilt:
            // Reset drops and re-materializes them under the new height contract.
            materializer.Reset();
        }
    }

    /// <summary>Writes the TableView height contract onto the shared materializer.</summary>
    private static void ApplyRowMetrics(OpenHarmonyItemListMaterializer materializer, TableView table)
    {
        materializer.VariableItemHeights = table.HasUnevenRows;
        materializer.EstimatedItemHeight = table.RowHeight > 0 ? table.RowHeight : 44;
        materializer.FixedItemHeight = table.RowHeight > -1 ? table.RowHeight : 0;
    }

    /// <summary>Cell.Height when it is positive (0 = measure the row's content).</summary>
    private static double PreferredRowHeight(object? item)
        => item is Cell cell && cell.Height > 0 ? cell.Height : 0;

    /// <summary>Flat row projection: a section whose title/header should draw a header row.</summary>
    private sealed record TableSectionHeader(Cell? Cell, string Title, Color? TextColor);
}
