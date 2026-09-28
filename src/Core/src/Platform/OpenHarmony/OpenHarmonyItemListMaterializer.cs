// Shared virtualization for list-like controls: materializes only the items intersecting the
// viewport (plus a margin), pools the views and slides the window while scrolling. Optional
// list header/footer rows, group header/footer rows and the ItemsView.EmptyView content are
// measured alongside the item window. The data always keeps the source's row layout; collapse
// and the item-window maths work on a "slot" projection of it, so a collapsed group hides its
// rows from the viewport without touching ItemsSource.
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
    private readonly HashSet<int> _footerRows = new();
    private readonly List<object> _collapsedGroups = new();
    private readonly Dictionary<int, View> _materialized = new();
    private readonly List<View> _pool = new();
    // Serializes materialized-window reads/writes: the source can be replaced from a dispatcher
    // (frame/timer) thread while the arrange thread is filling or refreshing the window.
    private readonly object _gate = new();
    private readonly List<(int HeaderRow, int FirstItemRow, int ItemCount, int FooterRow, object Group)> _groups = new();
    // The display projection: _slotRows[slot] is the data row drawn in that slot and _rowSlot[row]
    // is the number of visible rows before it, so a visible row satisfies
    // _rowSlot[row + 1] > _rowSlot[row] and _rowSlot[_data.Count] is the visible slot count.
    private readonly List<int> _slotRows = new();
    private int[] _rowSlot = Array.Empty<int>();
    private bool[] _rowVisible = Array.Empty<bool>();
    private int[] _rowGroup = Array.Empty<int>();
    private int _windowFirst = -1;
    private int _windowLast = -1;
    // Set whenever the slot projection changes (source swap, collapse/expand, span): the
    // materialized rows keep their old frames until the next update re-arranges them.
    private bool _slotsDirty;
    // The source the current window was built from. SetItems re-runs on every arrange (it is the
    // path that notices source changes that never raise a mapper call), so an unchanged source
    // must not drop the pooled rows and rematerialise the whole visible window once per frame.
    private System.Collections.IEnumerable? _source;
    private bool _sourceGrouped;
    private Func<object?, string>? _sourceHeaderText;
    private bool _sourceGroupFooters;
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

    /// <summary>Group footer factory, supplied by the list handlers.</summary>
    public Func<object?, string>? footerTextFactory;

    /// <summary>Creates the platform view of a group footer row.</summary>
    public Func<string, View>? footerViewFactory;

    /// <summary>True when the grouped source draws a footer row after every group.</summary>
    public bool groupFootersEnabled;

    /// <summary>Invoked when a group header row is tapped (when tapping is enabled).</summary>
    public Action<object?>? groupHeaderTapped;

    /// <summary>Opt-in: tapping a group header toggles that group's collapsed state.</summary>
    public bool GroupHeaderTapToCollapse { get; set; }

    /// <summary>Creates the ItemsView.Header row (null when the control has none).</summary>
    public Func<View?>? listHeaderFactory;

    /// <summary>Creates the ItemsView.Footer row (null when the control has none).</summary>
    public Func<View?>? listFooterFactory;

    /// <summary>Creates the ItemsView.EmptyView content (null when the control has none).</summary>
    public Func<View?>? emptyViewFactory;

    /// <summary>Invoked after a real window update (threshold checks, scroll reporting).</summary>
    public Action? windowChanged;

    /// <summary>
    /// How the viewport reacts to a source update: KeepItemsInView anchors the first visible
    /// item, KeepLastItemInView anchors the last one (chat-style appends) and KeepScrollOffset
    /// leaves the raw offset alone.
    /// </summary>
    public ItemsUpdatingScrollMode UpdateMode { get; set; } = ItemsUpdatingScrollMode.KeepItemsInView;

    public OpenHarmonyItemListMaterializer(OpenHarmonyView platformView, Func<object?, View> createItemView, Action<object?> select)
    {
        _platformView = platformView;
        _createItemView = createItemView;
        _select = select;
    }

    public double ItemHeight { get; private set; } = 40;

    /// <summary>
    /// Fixed height for every row (0 = use the measured height of the first row). The legacy
    /// TableView handler sets it from TableView.RowHeight, whose contract is one height for all
    /// rows; a fixed height also keeps the row maths off the header row's measured height when
    /// the first row is a section header.
    /// </summary>
    public double FixedItemHeight
    {
        get => _fixedItemHeight;
        set
        {
            _fixedItemHeight = value;
            if (value > 0)
            {
                // The window maths run before the first row is arranged; seed the slot height so
                // the first pass already computes the right window for the fixed rows.
                ItemHeight = value;
            }
        }
    }

    private double _fixedItemHeight;

    /// <summary>
    /// When false (TableView cells) the materializer leaves each row's binding context alone:
    /// the rows are the cells themselves and carry their own inherited bindings, unlike a
    /// ListView/CollectionView item template whose view binds to the item it was materialized
    /// for.
    /// </summary>
    public bool BindRowContext { get; set; } = true;

    /// <summary>
    /// When true rows are never pooled: a TableView cell's platform view carries the cell's
    /// text/content directly, so re-using a recycled row for a different cell would show the
    /// previous cell's content.
    /// </summary>
    public bool DisablePooling { get; set; }

    /// <summary>Columns per row (CollectionView GridItemsLayout span).</summary>
    public int Span
    {
        get => _span;
        set
        {
            if (_span != value)
            {
                _span = value;
                _slotsDirty = true;
            }
        }
    }

    private int _span = 1;

    public double SlotHeight => ItemHeight + Spacing;

    /// <summary>Number of data items (group header/footer rows are not items).</summary>
    public int ItemCount => _data.Count - _headerRows.Count - _footerRows.Count;

    /// <summary>Number of rows the viewport can currently reach (collapsed groups hidden).</summary>
    public int VisibleRowCount => VisibleSlotCount;

    private int VisibleSlotCount => _data.Count == 0 ? 0 : _rowSlot[_data.Count];

    // Grid cells: for Span == 1 every slot is a row, otherwise the slots are items packed Span
    // per row (the same shape the previous row-count maths used).
    private int GridRowCount
    {
        get
        {
            int slots = VisibleSlotCount;
            return Span <= 1 ? slots : (slots + Span - 1) / Span;
        }
    }

    /// <summary>Scrolled content height: header + visible item slots + footer.</summary>
    public double TotalHeight => _headerHeight + GridRowCount * SlotHeight + _footerHeight;

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
                if (IsDataRow(row) && IsRowVisible(row))
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
            groupFootersEnabled == _sourceGroupFooters &&
            source is not System.Collections.Specialized.INotifyCollectionChanged)
        {
            return;
        }
        // A data change invalidates the momentum (the content height may have shrunk under it).
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        OpenHarmonyScrollAnimation.Cancel(_platformView);
        ItemsUpdatingScrollMode mode = UpdateMode;
        object? anchorItem = null;
        double anchorOffset = 0;
        // The rebuild and the resulting window reset are one atomic step for the arrange thread
        // (which may be reading _data/_materialized while the source is swapped). The anchor is
        // captured under the same gate so it refers to the window the old source was showing:
        // the identity of the first/last visible item (an index would silently point at a
        // different item when the update inserts or removes rows before the viewport).
        lock (_gate)
        {
            if ((mode == ItemsUpdatingScrollMode.KeepItemsInView || mode == ItemsUpdatingScrollMode.KeepLastItemInView) &&
                _windowFirst >= 0 && _windowLast >= _windowFirst)
            {
                int anchorRow = mode == ItemsUpdatingScrollMode.KeepItemsInView
                    ? FirstVisibleDataRow()
                    : LastVisibleDataRow();
                if (anchorRow >= 0)
                {
                    anchorItem = _data[anchorRow];
                    anchorOffset = GetItemY(anchorRow) - _platformView.ScrollOffsetY;
                }
            }
            _data.Clear();
            _headerRows.Clear();
            _footerRows.Clear();
            _groups.Clear();
            _rowGroup = Array.Empty<int>();
            if (source is not null)
            {
                BuildRows(source, grouped);
            }
            RebuildSlots();
            // The materialized views hold the previous source's contexts and the previous
            // projection's frames: drop them (the pool keeps the view instances) and let the
            // update below rebuild the window from the new rows.
            DropMaterializedLocked();
            _slotsDirty = true;
            ResetWindowLocked();
            // Recorded under the same gate as the data: a concurrent source swap must not leave
            // the memo pointing at a source the window was not actually built from.
            _source = source;
            _sourceGrouped = grouped;
            _sourceHeaderText = headerText;
            _sourceGroupFooters = groupFootersEnabled;
        }
        if (anchorItem is not null)
        {
            ApplyAnchorOffset(mode, anchorItem, anchorOffset);
        }
        else
        {
            // A shrunk source can leave the raw offset past the new content end.
            double max = Math.Max(0, TotalHeight - _platformView.Frame.Height);
            if (_platformView.ScrollOffsetY > max + 0.01)
            {
                ApplyScrollOffset(max);
            }
        }
        Update(force: true);
    }

    /// <summary>True when the row at the index is a group header.</summary>
    public bool IsHeader(int index) => _headerRows.Contains(index);

    /// <summary>True when the row at the index is a group footer.</summary>
    public bool IsFooter(int index) => _footerRows.Contains(index);

    /// <summary>True when the row is currently drawn (collapsed groups hide their rows).</summary>
    public bool IsRowVisible(int row)
        => row >= 0 && row < _data.Count && _rowVisible.Length == _data.Count && _rowVisible[row];

    /// <summary>True when the group is collapsed (its item/footer rows are hidden).</summary>
    public bool IsGroupCollapsed(object? group)
        => group is not null && _collapsedGroups.Exists(candidate => ReferenceEquals(candidate, group));

    /// <summary>
    /// Collapses or expands a group without touching ItemsSource: only the viewport projection
    /// changes, the offset is adjusted so content below the group does not jump, and the window
    /// is rebuilt. Returns false when the group is not part of the current grouped source.
    /// </summary>
    public bool SetGroupCollapsed(object? group, bool collapsed)
    {
        if (group is null || !HasGroups)
        {
            return false;
        }
        int groupIndex = -1;
        for (int i = 0; i < _groups.Count; i++)
        {
            if (ReferenceEquals(_groups[i].Group, group))
            {
                groupIndex = i;
                break;
            }
        }
        if (groupIndex < 0)
        {
            return false;
        }
        if (IsGroupCollapsed(group) == collapsed)
        {
            return true;
        }
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        OpenHarmonyScrollAnimation.Cancel(_platformView);
        double offset = _platformView.ScrollOffsetY;
        lock (_gate)
        {
            int headerRow = _groups[groupIndex].HeaderRow;
            int itemCount = _groups[groupIndex].ItemCount;
            bool hasFooterRow = _groups[groupIndex].FooterRow >= 0;
            if (collapsed)
            {
                _collapsedGroups.Add(group);
            }
            else
            {
                _collapsedGroups.RemoveAll(candidate => ReferenceEquals(candidate, group));
            }
            // Hiding rows above the viewport must not shift the content below it: pull the
            // offset up by however much hidden content sat above the viewport top.
            if (collapsed)
            {
                double top = GetItemY(headerRow);
                if (top < offset)
                {
                    double hidden = (itemCount + (hasFooterRow ? 1 : 0)) * SlotHeight;
                    offset = Math.Max(0, offset - Math.Min(hidden, offset - top));
                }
            }
            RebuildSlots();
            _slotsDirty = true;
            double max = Math.Max(0, TotalHeight - _platformView.Frame.Height);
            offset = Math.Min(offset, max);
        }
        ApplyScrollOffset(offset);
        Update(force: true);
        return true;
    }

    /// <summary>Text of a group header row.</summary>
    public string HeaderText(int index, Func<object?, string>? headerText)
        => headerText?.Invoke(_data[index]) ?? _data[index]?.ToString() ?? string.Empty;

    /// <summary>Text of a group footer row (the row carries its group object).</summary>
    public string FooterText(int index, Func<object?, string>? footerText)
        => footerText?.Invoke(_data[index]) ?? _data[index]?.ToString() ?? string.Empty;

    public double GetItemY(int index)
    {
        int slot = index >= 0 && index < _rowSlot.Length ? _rowSlot[index] : index;
        return _headerHeight + (Span <= 1 ? slot : slot / Span) * SlotHeight;
    }

    public double GetItemX(int index, double width)
    {
        if (Span <= 1)
        {
            return 0;
        }
        int slot = index >= 0 && index < _rowSlot.Length ? _rowSlot[index] : index;
        int column = slot % Span;
        return column * (width / Span);
    }

    public double GetItemWidth(double width) => Span <= 1 ? width : width / Span - Spacing;

    /// <summary>Drops all materialized views (item template changed).</summary>
    public void Reset()
    {
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        OpenHarmonyScrollAnimation.Cancel(_platformView);
        lock (_gate)
        {
            _pool.Clear();
            _materialized.Clear();
            ResetWindowLocked();
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
        int slotCount = VisibleSlotCount;
        int gridRows = GridRowCount;
        if (frame.Height > 0 && frame.Width > 0 && gridRows > 0)
        {
            double contentTop = _headerHeight;
            int firstGridRow = Math.Max(0, (int)Math.Floor((offset - WindowMargin - contentTop) / SlotHeight));
            int lastGridRow = Math.Min(gridRows - 1, (int)Math.Ceiling((offset + frame.Height + WindowMargin - contentTop) / SlotHeight));
            if (firstGridRow <= lastGridRow)
            {
                int firstSlot = Math.Min(slotCount - 1, firstGridRow * Span);
                int lastSlot = Math.Min(slotCount - 1, (lastGridRow + 1) * Span - 1);
                first = _slotRows[firstSlot];
                last = _slotRows[lastSlot];
            }
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
                if (index < first || index > last || !IsRowVisible(index))
                {
                    stale.Add(index);
                }
            }
            foreach (int index in stale)
            {
                View view = _materialized[index];
                _materialized.Remove(index);
                RecycleLocked(index, view);
            }
            for (int index = first; index <= last; index++)
            {
                if (IsRowVisible(index) && !_materialized.ContainsKey(index))
                {
                    _materialized[index] = Materialize(index);
                }
            }
            if (_slotsDirty)
            {
                // A source swap or a collapse/expand moved rows to new slots: re-arrange the
                // materialized set, otherwise the surviving rows keep the old projection's
                // frames (the slide-the-window path assumes a stable row-to-slot mapping).
                foreach (KeyValuePair<int, View> row in _materialized)
                {
                    ArrangeRow(row.Key, row.Value);
                }
                _slotsDirty = false;
            }
            ordered = OrderedMaterializedLocked();
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

    // Rows in materialization order without LINQ/LINQ allocations: a short key list is sorted in
    // place and the views are copied straight out (long lists change their window on every few
    // scrolled rows, so this is the hot allocation on the scroll path).
    private List<View> OrderedMaterializedLocked()
    {
        var keys = new List<int>(_materialized.Count);
        foreach (int key in _materialized.Keys)
        {
            keys.Add(key);
        }
        keys.Sort();
        var ordered = new List<View>(keys.Count);
        foreach (int key in keys)
        {
            ordered.Add(_materialized[key]);
        }
        return ordered;
    }

    /// <summary>Scrolls the row so that it lands at the requested position in the viewport.</summary>
    public void ScrollTo(int row, ScrollToPosition position) => ScrollTo(row, position, animated: false);

    /// <summary>
    /// Scrolls the row into the requested viewport position, animating the offset through the
    /// shared frame loop when <paramref name="animated"/> is set (reduced motion snaps).
    /// </summary>
    public void ScrollTo(int row, ScrollToPosition position, bool animated)
    {
        // The jump owns the offset from here: stop any momentum moving the same view.
        OpenHarmonyScrollPhysics.Cancel(_platformView);
        OpenHarmonyScrollAnimation.Cancel(_platformView);
        RectF frame = _platformView.Frame;
        if (row < 0 || row >= _data.Count || frame.Height <= 0)
        {
            return;
        }
        if (!IsRowVisible(row))
        {
            ExpandRow(row);
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
        target = Math.Clamp(target, 0, maxOffset);
        if (animated && Math.Abs(target - offset) > 0.5)
        {
            OpenHarmonyScrollAnimation.Start(_platformView, (float)target, ApplyScrollOffset);
        }
        else
        {
            ApplyScrollOffset(target);
        }
    }

    /// <summary>Writes a programmatic offset, keeps the virtual view in sync and refreshes the window.</summary>
    private void ApplyScrollOffset(double target)
    {
        // A programmatic write (ScrollTo, animation frames, collapse) must not feed the drag
        // velocity sampler: a long jump would otherwise look like a fling-speed sample.
        OpenHarmonyScrollPhysics.BeginProgrammatic();
        try
        {
            _platformView.ScrollOffsetY = (float)target;
            if (_platformView.VirtualView is IScrollView virtualScroll)
            {
                virtualScroll.VerticalOffset = _platformView.ScrollOffsetY;
            }
            Update(force: true);
        }
        finally
        {
            OpenHarmonyScrollPhysics.EndProgrammatic();
        }
    }

    /// <summary>Re-materializes after an offset-only change (used by the scroll animation steps).</summary>
    private void ApplyScrollOffset(float target) => ApplyScrollOffset((double)target);

    /// <summary>Row index of the n-th data item (group header rows are skipped), -1 when out of range.</summary>
    public int RowForItemIndex(int itemIndex)
    {
        if (itemIndex < 0)
        {
            return -1;
        }
        int seen = -1;
        for (int row = 0; row < _data.Count; row++)
        {
            if (!IsDataRow(row))
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
            if (IsDataRow(row) && Equals(_data[row], item))
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
        foreach ((int headerRow, int firstItemRow, int count, _, _) in _groups)
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
        (int headerRow, int firstItemRow, int count, _, _) = _groups[groupIndex];
        return itemIndex >= 0 && itemIndex < count ? firstItemRow + itemIndex : -1;
    }

    /// <summary>Zero-based data item index of a row (group header/footer rows are skipped).</summary>
    private int ItemIndexOfRow(int row)
    {
        int index = -1;
        for (int i = 0; i <= row && i < _data.Count; i++)
        {
            if (IsDataRow(i))
            {
                index++;
            }
        }
        return index;
    }

    private bool IsDataRow(int row) => row >= 0 && row < _data.Count && !IsHeader(row) && !IsFooter(row);

    /// <summary>Builds the flat row list for a (possibly grouped) source.</summary>
    private void BuildRows(System.Collections.IEnumerable source, bool grouped)
    {
        var rowGroup = new List<int>();
        foreach (object? item in source)
        {
            if (grouped && item is System.Collections.IEnumerable group and not string)
            {
                int groupIndex = _groups.Count;
                int headerRow = _data.Count;
                _data.Add(item);
                _headerRows.Add(headerRow);
                rowGroup.Add(groupIndex);
                int firstItemRow = _data.Count;
                int count = 0;
                foreach (object? child in group)
                {
                    _data.Add(child);
                    rowGroup.Add(groupIndex);
                    count++;
                }
                int footerRow = -1;
                if (groupFootersEnabled)
                {
                    footerRow = _data.Count;
                    _data.Add(item);
                    _footerRows.Add(footerRow);
                    rowGroup.Add(groupIndex);
                }
                _groups.Add((headerRow, firstItemRow, count, footerRow, item));
            }
            else
            {
                _data.Add(item);
                rowGroup.Add(-1);
            }
        }
        _rowGroup = rowGroup.ToArray();
    }

    /// <summary>Recomputes the slot projection after a structural change.</summary>
    private void RebuildSlots()
    {
        int count = _data.Count;
        if (_rowSlot.Length != count + 1)
        {
            _rowSlot = new int[count + 1];
        }
        if (_rowVisible.Length != count)
        {
            _rowVisible = new bool[count];
        }
        _slotRows.Clear();
        int slot = 0;
        for (int row = 0; row < count; row++)
        {
            _rowSlot[row] = slot;
            bool visible = IsRowVisibleCore(row);
            _rowVisible[row] = visible;
            if (visible)
            {
                _slotRows.Add(row);
                slot++;
            }
        }
        _rowSlot[count] = slot;
    }

    private bool IsRowVisibleCore(int row)
    {
        if (IsHeader(row))
        {
            return true;
        }
        int group = row < _rowGroup.Length ? _rowGroup[row] : -1;
        return group < 0 || !IsGroupCollapsed(_groups[group].Group);
    }

    /// <summary>Expands the group owning the row when it is collapsed (ScrollTo target).</summary>
    private void ExpandRow(int row)
    {
        int group = row < _rowGroup.Length ? _rowGroup[row] : -1;
        if (group >= 0)
        {
            SetGroupCollapsed(_groups[group].Group, collapsed: false);
        }
    }

    /// <summary>First row intersecting the viewport (window margin rows excluded).</summary>
    private int FirstVisibleDataRow()
    {
        double top = _platformView.ScrollOffsetY;
        for (int row = 0; row < _data.Count; row++)
        {
            if (IsDataRow(row) && IsRowVisible(row) && GetItemY(row) + ItemHeight > top + 0.5)
            {
                return row;
            }
        }
        return -1;
    }

    /// <summary>Last row starting inside the viewport (the item that can be seen at the bottom).</summary>
    private int LastVisibleDataRow()
    {
        double bottom = _platformView.ScrollOffsetY + _platformView.Frame.Height;
        for (int row = _data.Count - 1; row >= 0; row--)
        {
            if (IsDataRow(row) && IsRowVisible(row) && GetItemY(row) < bottom - 0.5)
            {
                return row;
            }
        }
        return -1;
    }

    /// <summary>Applies the ItemsUpdatingScrollMode anchor after a rebuild.</summary>
    private void ApplyAnchorOffset(ItemsUpdatingScrollMode mode, object anchorItem, double anchorOffset)
    {
        int row = RowForItem(anchorItem);
        if (row < 0)
        {
            // The anchored item is gone from the new source: keep the raw offset (clamped).
            double clampMax = Math.Max(0, TotalHeight - _platformView.Frame.Height);
            if (_platformView.ScrollOffsetY > clampMax + 0.01)
            {
                ApplyScrollOffset(clampMax);
            }
            return;
        }
        if (!IsRowVisible(row))
        {
            ExpandRow(row);
        }
        RectF frame = _platformView.Frame;
        double extent = Math.Max(1, ItemHeight);
        double target = mode == ItemsUpdatingScrollMode.KeepLastItemInView
            ? GetItemY(row) + extent - frame.Height
            : GetItemY(row) - anchorOffset;
        double max = Math.Max(0, TotalHeight - frame.Height);
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(target - _platformView.ScrollOffsetY) > 0.01)
        {
            ApplyScrollOffset(target);
        }
    }

    private void ResetWindowLocked()
    {
        _windowFirst = -1;
        _windowLast = -1;
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
            _footerView.Arrange(new Rect(frame.X, frame.Y + _headerHeight + GridRowCount * SlotHeight,
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
        bool isFooter = IsFooter(index);
        View view;
        if (isHeader)
        {
            view = CreateHeaderView(HeaderText(index, headerTextFactory));
        }
        else if (isFooter)
        {
            view = CreateFooterView(FooterText(index, footerTextFactory));
        }
        else
        {
            view = TakeFromPool() ?? _createItemView(item);
            // A pooled view's handler tree was disconnected when the row left the window; a
            // re-used row must be reconnected or it would render/tap as an empty platform view.
            OpenHarmonyHandlerConnector.ConnectTree(view);
        }
        if (BindRowContext)
        {
            view.BindingContext = isHeader || isFooter ? null : item;
        }
        SetRowTap(index, view);
        ArrangeRow(index, view);
        return view;
    }

    /// <summary>Measures/positions a row for the current slot projection.</summary>
    private void ArrangeRow(int index, View view)
    {
        RectF frame = _platformView.Frame;
        double width = frame.Width > 0 ? frame.Width : 1080;
        double itemWidth = GetItemWidth(width);
        view.Measure(itemWidth, double.PositiveInfinity);
        Size size = view.DesiredSize;
        if (FixedItemHeight > 0)
        {
            // Fixed rows: the first row seeds the slot height regardless of its kind (a section
            // header must not shrink the cells), and every row is clipped to the fixed height.
            ItemHeight = FixedItemHeight;
        }
        else if (index == 0 && size.Height > 0)
        {
            ItemHeight = size.Height;
        }
        double height = FixedItemHeight > 0 ? ItemHeight : Math.Max(size.Height, ItemHeight);
        view.Arrange(new Rect(frame.X + GetItemX(index, width), frame.Y + GetItemY(index),
            itemWidth, height));
    }

    /// <summary>Wires the row's tap: item selection, group-header toggle or nothing.</summary>
    private void SetRowTap(int index, View view)
    {
        if (view.Handler?.PlatformView is not OpenHarmonyView itemPlatform)
        {
            return;
        }
        if (IsHeader(index))
        {
            object? group = _data[index];
            itemPlatform.Tap = GroupHeaderTapToCollapse && groupHeaderTapped is not null
                ? () => groupHeaderTapped(group)
                : null;
        }
        else if (IsFooter(index))
        {
            itemPlatform.Tap = null;
        }
        else
        {
            object? captured = _data[index];
            itemPlatform.Tap = () => _select(captured);
        }
    }

    /// <summary>Re-wires the materialized header rows after the tap opt-in changed.</summary>
    public void RefreshGroupHeaderTaps()
    {
        lock (_gate)
        {
            foreach (KeyValuePair<int, View> row in _materialized)
            {
                if (IsHeader(row.Key))
                {
                    SetRowTap(row.Key, row.Value);
                }
            }
        }
    }

    private void DropMaterializedLocked()
    {
        foreach (KeyValuePair<int, View> row in _materialized)
        {
            RecycleLocked(row.Key, row.Value);
        }
        _materialized.Clear();
    }

    /// <summary>
    /// Disconnects a row that left the window. Only item-template rows go to the pool: header/
    /// footer rows carry their text directly (no binding), so reusing one as an item row kept
    /// the previous group's header/footer text; they are re-created instead.
    /// </summary>
    private void RecycleLocked(int index, View view)
    {
        view.Handler?.DisconnectHandler();
        if (!DisablePooling && !IsHeader(index) && !IsFooter(index))
        {
            _pool.Add(view);
        }
    }

    private View CreateHeaderView(string text)
    {
        // Headers are re-created (they are cheap and pooling would fight template bindings).
        View view = headerViewFactory?.Invoke(text) ?? new Label { Text = text, FontSize = 24 };
        OpenHarmonyHandlerConnector.ConnectTree(view);
        return view;
    }

    private View CreateFooterView(string text)
    {
        View view = footerViewFactory?.Invoke(text) ?? new Label { Text = text, FontSize = 24 };
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
