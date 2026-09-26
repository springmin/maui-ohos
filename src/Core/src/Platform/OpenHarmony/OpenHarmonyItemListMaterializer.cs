// Shared virtualization for list-like controls: materializes only the items intersecting the
// viewport (plus a margin), pools the views and slides the window while scrolling. Optional
// list header/footer rows and the ItemsView.EmptyView content are measured alongside the item
// window, and grouped sources track their group boundaries for ScrollTo.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal sealed class OpenHarmonyItemListMaterializer
{
    private const double Spacing = 6;
    private const double WindowMargin = 120;

    private readonly OpenHarmonyView _platformView;
    private readonly Func<object?, View> _createItemView;
    private readonly Action<object?> _select;
    private readonly List<object?> _data = new();
    private readonly HashSet<int> _headerRows = new();
    private readonly Dictionary<int, View> _materialized = new();
    private readonly List<View> _pool = new();
    // Serializes materialized-window reads/writes: the source can be replaced from a dispatcher
    // (frame/timer) thread while the arrange thread is filling or refreshing the window.
    private readonly object _gate = new();
    private readonly List<(int HeaderRow, int FirstItemRow, int ItemCount)> _groups = new();
    private int _windowFirst = -1;
    private int _windowLast = -1;
    // The source the current window was built from. SetItems re-runs on every arrange (it is the
    // path that notices source changes that never raise a mapper call), so an unchanged source
    // must not drop the pooled rows and rematerialise the whole visible window once per frame.
    private System.Collections.IEnumerable? _source;
    private bool _sourceGrouped;
    private Func<object?, string>? _sourceHeaderText;
    private View? _headerView;
    private View? _footerView;
    private View? _emptyView;
    private double _headerHeight;
    private double _footerHeight;
    private double _emptyHeight;

    /// <summary>Group header factory, supplied by the list handlers.</summary>
    public Func<object?, string>? headerTextFactory;

    /// <summary>Creates the platform view of a group header row.</summary>
    public Func<string, View>? headerViewFactory;

    /// <summary>Creates the ItemsView.Header row (null when the control has none).</summary>
    public Func<View?>? listHeaderFactory;

    /// <summary>Creates the ItemsView.Footer row (null when the control has none).</summary>
    public Func<View?>? listFooterFactory;

    /// <summary>Creates the ItemsView.EmptyView content (null when the control has none).</summary>
    public Func<View?>? emptyViewFactory;

    /// <summary>Invoked after a real window update (threshold checks, scroll reporting).</summary>
    public Action? windowChanged;

    public OpenHarmonyItemListMaterializer(OpenHarmonyView platformView, Func<object?, View> createItemView, Action<object?> select)
    {
        _platformView = platformView;
        _createItemView = createItemView;
        _select = select;
    }

    public double ItemHeight { get; private set; } = 40;

    /// <summary>Columns per row (CollectionView GridItemsLayout span).</summary>
    public int Span { get; set; } = 1;

    public double SlotHeight => ItemHeight + Spacing;

    private int RowCount => _data.Count == 0 ? 0 : (_data.Count + Span - 1) / Span;

    /// <summary>Scrolled content height: header + item rows + footer.</summary>
    public double TotalHeight => _headerHeight + RowCount * SlotHeight + _footerHeight;

    /// <summary>Number of data items (group header rows are not items).</summary>
    public int ItemCount => _data.Count - _headerRows.Count;

    /// <summary>True when the source has no rows (EmptyView territory).</summary>
    public bool IsEmpty => _data.Count == 0;

    /// <summary>True when the source was built from groups (group headers present).</summary>
    public bool HasGroups => _groups.Count > 0;

    /// <summary>Height measured for the EmptyView content (0 when none/width unknown).</summary>
    public double EmptyHeight => _emptyHeight;

    /// <summary>Item views currently materialized (selected-state highlighting walks these).</summary>
    /// <remarks>
    /// The copy is taken under the materializer gate: the first arrange of an asynchronously
    /// loaded source can overlap a SetItems/Update on another thread (the OpenHarmony dispatcher
    /// drains on frame/timer threads), and enumerating the live dictionary mid-write returned a
    /// null row, which crashed the CollectionView's RefreshSelection with a NullReferenceException.
    /// </remarks>
    public IReadOnlyCollection<View> MaterializedItems
    {
        get
        {
            lock (_gate)
            {
                return _materialized.Values.ToArray();
            }
        }
    }

    /// <summary>Index of the last visible data item (-1 when none is visible).</summary>
    public int LastVisibleItemIndex
    {
        get
        {
            for (int row = _windowLast; row >= _windowFirst; row--)
            {
                if (!_headerRows.Contains(row))
                {
                    return ItemIndexOfRow(row);
                }
            }
            return -1;
        }
    }

    /// <summary>Replaces the data behind the list (grouped sources become header rows).</summary>
    public void SetItems(System.Collections.IEnumerable? source, bool grouped = false, Func<object?, string>? headerText = null)
    {
        // An unchanged plain source cannot notify the platform, so rebuilding it on the next
        // arrange would only discard the pool and rematerialise every visible row. Observable
        // sources keep the rebuild: their content can change without a reference swap, and this
        // slice has no collection-changed adapter to hear about it earlier.
        if (ReferenceEquals(source, _source) && grouped == _sourceGrouped && headerText == _sourceHeaderText &&
            source is not System.Collections.Specialized.INotifyCollectionChanged)
        {
            return;
        }
        // A data change invalidates the momentum (the content height may have shrunk under it).
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        // The rebuild and the resulting window reset are one atomic step for the arrange thread
        // (which may be reading _data/_materialized while the source is swapped).
        lock (_gate)
        {
            _data.Clear();
            _headerRows.Clear();
            _groups.Clear();
            if (source is not null)
            {
                foreach (object? item in source)
                {
                    if (grouped && item is System.Collections.IEnumerable group and not string)
                    {
                        int headerRow = _data.Count;
                        _data.Add(item);
                        _headerRows.Add(headerRow);
                        int firstItemRow = _data.Count;
                        int count = 0;
                        foreach (object? child in group)
                        {
                            _data.Add(child);
                            count++;
                        }
                        _groups.Add((headerRow, firstItemRow, count));
                    }
                    else
                    {
                        _data.Add(item);
                    }
                }
            }
            Reset();
            // Recorded under the same gate as the data: a concurrent source swap must not leave
            // the memo pointing at a source the window was not actually built from.
            _source = source;
            _sourceGrouped = grouped;
            _sourceHeaderText = headerText;
        }
    }

    /// <summary>True when the row at the index is a group header.</summary>
    public bool IsHeader(int index) => _headerRows.Contains(index);

    /// <summary>Text of a group header row.</summary>
    public string HeaderText(int index, Func<object?, string>? headerText)
        => headerText?.Invoke(_data[index]) ?? _data[index]?.ToString() ?? string.Empty;

    public double GetItemY(int index) => _headerHeight + (Span <= 1 ? index * SlotHeight : (index / Span) * SlotHeight);

    public double GetItemX(int index, double width)
    {
        if (Span <= 1)
        {
            return 0;
        }
        int column = index % Span;
        return column * (width / Span);
    }

    public double GetItemWidth(double width) => Span <= 1 ? width : width / Span - Spacing;

    /// <summary>Drops all materialized views (item template changed).</summary>
    public void Reset()
    {
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        lock (_gate)
        {
            _pool.Clear();
            _materialized.Clear();
            _windowFirst = -1;
            _windowLast = -1;
        }
        Update(force: true);
    }

    /// <summary>Drops the header/footer/empty content (their template or value changed).</summary>
    public void ResetExtras()
    {
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        _headerView = null;
        _footerView = null;
        _emptyView = null;
        _headerHeight = 0;
        _footerHeight = 0;
        _emptyHeight = 0;
        Update(force: true);
    }

    /// <summary>Materializes the items intersecting the viewport (plus margin).</summary>
    public void Update(bool force = false)
    {
        RectF frame = _platformView.Frame;
        double width = frame.Width > 0 ? frame.Width : 1080;
        PrepareExtras(width);
        double offset = _platformView.ScrollOffsetY;
        int first = 0;
        int last = -1;
        if (frame.Height > 0 && frame.Width > 0 && _data.Count > 0)
        {
            double contentTop = _headerHeight;
            int firstRow = Math.Max(0, (int)Math.Floor((offset - WindowMargin - contentTop) / SlotHeight));
            int lastRow = Math.Min(RowCount - 1, (int)Math.Ceiling((offset + frame.Height + WindowMargin - contentTop) / SlotHeight));
            first = firstRow * Span;
            last = Math.Min(_data.Count - 1, (lastRow + 1) * Span - 1);
        }
        if (!force && first == _windowFirst && last == _windowLast)
        {
            return;
        }
        _windowFirst = first;
        _windowLast = last;

        // The window update is serialized with the source swap and with the MaterializedItems
        // snapshot, so a row can never be observed while it is being added or removed.
        List<View> ordered;
        lock (_gate)
        {
            var stale = new List<int>();
            foreach (int index in _materialized.Keys)
            {
                if (index < first || index > last)
                {
                    stale.Add(index);
                }
            }
            foreach (int index in stale)
            {
                View view = _materialized[index];
                view.Handler?.DisconnectHandler();
                _materialized.Remove(index);
                _pool.Add(view);
            }
            for (int index = first; index <= last; index++)
            {
                if (!_materialized.ContainsKey(index))
                {
                    _materialized[index] = Materialize(index);
                }
            }
            ordered = _materialized.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        }
        ArrangeExtras(width);
        _platformView.ViewChildren.Clear();
        if (_headerView is not null)
        {
            _platformView.ViewChildren.Add(_headerView);
        }
        foreach (View child in ordered)
        {
            _platformView.ViewChildren.Add(child);
        }
        if (_footerView is not null)
        {
            _platformView.ViewChildren.Add(_footerView);
        }
        if (_emptyView is not null)
        {
            _platformView.ViewChildren.Add(_emptyView);
        }
        _platformView.ScrollContentHeight = (float)TotalHeight;
        OpenHarmonyBridge.RequestRedraw();
        windowChanged?.Invoke();
    }

    /// <summary>Scrolls the row so that it lands at the requested position in the viewport.</summary>
    public void ScrollTo(int row, ScrollToPosition position)
    {
        // The jump owns the offset from here: stop any momentum moving the same view.
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        RectF frame = _platformView.Frame;
        if (row < 0 || row >= _data.Count || frame.Height <= 0)
        {
            return;
        }
        double y = GetItemY(row);
        // The item's visual extent (the slot adds the row spacing, which should stay out of the
        // alignment maths: End means the item's bottom sits on the viewport bottom). A row that
        // is already materialized reports its measured height (group headers differ from items).
        double extent = Math.Max(1, ItemHeight);
        View? materialized;
        lock (_gate)
        {
            _materialized.TryGetValue(row, out materialized);
        }
        if (materialized is not null && materialized.DesiredSize.Height > 0)
        {
            extent = materialized.DesiredSize.Height;
        }
        double offset = _platformView.ScrollOffsetY;
        double target = position switch
        {
            ScrollToPosition.Start => y,
            ScrollToPosition.Center => y + extent / 2 - frame.Height / 2,
            ScrollToPosition.End => y + extent - frame.Height,
            _ => y < offset ? y : (y + extent > offset + frame.Height ? y + extent - frame.Height : offset),
        };
        double maxOffset = Math.Max(0, TotalHeight - frame.Height);
        _platformView.ScrollOffsetY = (float)Math.Clamp(target, 0, maxOffset);
        if (_platformView.VirtualView is Microsoft.Maui.IScrollView virtualScroll)
        {
            virtualScroll.VerticalOffset = _platformView.ScrollOffsetY;
        }
        Update(force: true);
    }

    /// <summary>Row index of the n-th data item (group headers are skipped), -1 when out of range.</summary>
    public int RowForItemIndex(int itemIndex)
    {
        if (itemIndex < 0)
        {
            return -1;
        }
        int seen = -1;
        for (int row = 0; row < _data.Count; row++)
        {
            if (_headerRows.Contains(row))
            {
                continue;
            }
            seen++;
            if (seen == itemIndex)
            {
                return row;
            }
        }
        return -1;
    }

    /// <summary>Row index of a data item (reference or value equality), -1 when absent.</summary>
    public int RowForItem(object? item)
    {
        if (item is null)
        {
            return -1;
        }
        for (int row = 0; row < _data.Count; row++)
        {
            if (!_headerRows.Contains(row) && Equals(_data[row], item))
            {
                return row;
            }
        }
        return -1;
    }

    /// <summary>Row index of an item inside a group (group selector or group object), -1 when absent.</summary>
    public int RowForGroupItem(object? group, object? item)
    {
        if (group is null || item is null)
        {
            return -1;
        }
        foreach ((int headerRow, int firstItemRow, int count) in _groups)
        {
            if (!Equals(_data[headerRow], group))
            {
                continue;
            }
            for (int row = firstItemRow; row < firstItemRow + count; row++)
            {
                if (Equals(_data[row], item))
                {
                    return row;
                }
            }
            return firstItemRow;
        }
        return -1;
    }

    /// <summary>Row index of the item with the flat index inside a group, -1 when out of range.</summary>
    public int RowForGroupItemIndex(int groupIndex, int itemIndex)
    {
        if (groupIndex < 0 || groupIndex >= _groups.Count)
        {
            return -1;
        }
        (int headerRow, int firstItemRow, int count) = _groups[groupIndex];
        return itemIndex >= 0 && itemIndex < count ? firstItemRow + itemIndex : -1;
    }

    /// <summary>Zero-based data item index of a row (group headers are skipped).</summary>
    private int ItemIndexOfRow(int row)
    {
        int index = -1;
        for (int i = 0; i <= row && i < _data.Count; i++)
        {
            if (!_headerRows.Contains(i))
            {
                index++;
            }
        }
        return index;
    }

    /// <summary>Creates/measures the header, footer and empty-view content for this pass.</summary>
    private void PrepareExtras(double width)
    {
        if (listHeaderFactory is not null)
        {
            _headerView ??= listHeaderFactory();
            MeasureExtra(_headerView, width, ref _headerHeight);
        }
        if (listFooterFactory is not null)
        {
            _footerView ??= listFooterFactory();
            MeasureExtra(_footerView, width, ref _footerHeight);
        }
        if (_data.Count == 0 && emptyViewFactory is not null)
        {
            _emptyView ??= emptyViewFactory();
            if (_emptyView is not null)
            {
                double viewport = Math.Max(0, _platformView.Frame.Height - _headerHeight - _footerHeight);
                _emptyView.Measure(width, viewport > 0 ? viewport : double.PositiveInfinity);
                _emptyHeight = _emptyView.DesiredSize.Height;
            }
        }
        else if (_emptyView is not null)
        {
            _emptyView = null;
            _emptyHeight = 0;
        }
    }

    private static void MeasureExtra(View? view, double width, ref double height)
    {
        if (view is null)
        {
            height = 0;
            return;
        }
        view.Measure(width, double.PositiveInfinity);
        Size size = view.DesiredSize;
        if (size.Height > 0)
        {
            height = size.Height;
        }
    }

    private void ArrangeExtras(double width)
    {
        RectF frame = _platformView.Frame;
        if (_headerView is not null)
        {
            _headerView.Arrange(new Rect(frame.X, frame.Y, width, Math.Max(_headerHeight, 1)));
        }
        if (_footerView is not null)
        {
            _footerView.Arrange(new Rect(frame.X, frame.Y + _headerHeight + (_data.Count == 0 ? 0 : RowCount * SlotHeight),
                width, Math.Max(_footerHeight, 1)));
        }
        if (_emptyView is not null)
        {
            double space = Math.Max(1, frame.Height - _headerHeight - _footerHeight);
            _emptyView.Arrange(new Rect(frame.X, frame.Y + _headerHeight, width, space));
        }
    }

    private View Materialize(int index)
    {
        object? item = _data[index];
        bool isHeader = IsHeader(index);
        View view;
        if (isHeader)
        {
            view = CreateHeaderView(HeaderText(index, headerTextFactory));
        }
        else
        {
            view = TakeFromPool() ?? _createItemView(item);
            // A pooled view's handler tree was disconnected when the row left the window; a
            // re-used row must be reconnected or it would render/tap as an empty platform view.
            OpenHarmonyHandlerConnector.ConnectTree(view);
        }
        view.BindingContext = isHeader ? null : item;
        if (view.Handler?.PlatformView is OpenHarmonyView itemPlatform)
        {
            if (isHeader)
            {
                itemPlatform.Tap = null;
            }
            else
            {
                object? captured = item;
                itemPlatform.Tap = () => _select(captured);
            }
        }
        RectF frame = _platformView.Frame;
        double width = frame.Width > 0 ? frame.Width : 1080;
        double itemWidth = GetItemWidth(width);
        view.Measure(itemWidth, double.PositiveInfinity);
        Size size = view.DesiredSize;
        if (index == 0 && size.Height > 0)
        {
            ItemHeight = size.Height;
        }
        view.Arrange(new Rect(frame.X + GetItemX(index, width), frame.Y + GetItemY(index),
            itemWidth, Math.Max(size.Height, ItemHeight)));
        return view;
    }

    private View CreateHeaderView(string text)
    {
        // Headers are re-created (they are cheap and pooling would fight template bindings).
        View view = headerViewFactory?.Invoke(text) ?? new Label { Text = text, FontSize = 24 };
        OpenHarmonyHandlerConnector.ConnectTree(view);
        return view;
    }

    private View? TakeFromPool()
    {
        if (_pool.Count == 0)
        {
            return null;
        }
        View view = _pool[^1];
        _pool.RemoveAt(_pool.Count - 1);
        return view;
    }
}
