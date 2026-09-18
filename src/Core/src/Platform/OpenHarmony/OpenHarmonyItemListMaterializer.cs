// Shared virtualization for list-like controls: materializes only the items intersecting the
// viewport (plus a margin), pools the views and slides the window while scrolling.
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
    private int _windowFirst = -1;
    private int _windowLast = -1;
    /// <summary>Group header factory, supplied by the list handlers.</summary>
    public Func<object?, string>? headerTextFactory;

    /// <summary>Creates the platform view of a group header row.</summary>
    public Func<string, View>? headerViewFactory;

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

    public double TotalHeight => RowCount * SlotHeight;

    /// <summary>Replaces the data behind the list (grouped sources become header rows).</summary>
    public void SetItems(System.Collections.IEnumerable? source, bool grouped = false, Func<object?, string>? headerText = null)
    {
        _data.Clear();
        _headerRows.Clear();
        if (source is not null)
        {
            foreach (object? item in source)
            {
                if (grouped && item is System.Collections.IEnumerable group and not string)
                {
                    _data.Add(item);
                    _headerRows.Add(_data.Count - 1);
                    foreach (object? child in group)
                    {
                        _data.Add(child);
                    }
                }
                else
                {
                    _data.Add(item);
                }
            }
        }
        Reset();
    }

    /// <summary>True when the row at the index is a group header.</summary>
    public bool IsHeader(int index) => _headerRows.Contains(index);

    /// <summary>Text of a group header row.</summary>
    public string HeaderText(int index, Func<object?, string>? headerText)
        => headerText?.Invoke(_data[index]) ?? _data[index]?.ToString() ?? string.Empty;

    public double GetItemY(int index) => Span <= 1 ? index * SlotHeight : (index / Span) * SlotHeight;

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

    /// <summary>Drops all materialized views (template changed).</summary>
    public void Reset()
    {
        _pool.Clear();
        _materialized.Clear();
        _windowFirst = -1;
        _windowLast = -1;
        Update(force: true);
    }

    /// <summary>Materializes the items intersecting the viewport (plus margin).</summary>
    public void Update(bool force = false)
    {
        RectF frame = _platformView.Frame;
        double offset = _platformView.ScrollOffsetY;
        int first = 0;
        int last = -1;
        if (frame.Height > 0 && frame.Width > 0 && _data.Count > 0)
        {
            int firstRow = Math.Max(0, (int)Math.Floor((offset - WindowMargin) / SlotHeight));
            int lastRow = Math.Min(RowCount - 1, (int)Math.Ceiling((offset + frame.Height + WindowMargin) / SlotHeight));
            first = firstRow * Span;
            last = Math.Min(_data.Count - 1, (lastRow + 1) * Span - 1);
        }
        if (!force && first == _windowFirst && last == _windowLast)
        {
            return;
        }
        _windowFirst = first;
        _windowLast = last;

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
        _platformView.ViewChildren.Clear();
        foreach (KeyValuePair<int, View> entry in _materialized.OrderBy(pair => pair.Key))
        {
            _platformView.ViewChildren.Add(entry.Value);
        }
        _platformView.ScrollContentHeight = (float)TotalHeight;
        OpenHarmonyBridge.RequestRedraw();
    }

    private View Materialize(int index)
    {
        object? item = _data[index];
        bool isHeader = IsHeader(index);
        View view = isHeader
            ? CreateHeaderView(HeaderText(index, headerTextFactory))
            : TakeFromPool() ?? _createItemView(item);
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
