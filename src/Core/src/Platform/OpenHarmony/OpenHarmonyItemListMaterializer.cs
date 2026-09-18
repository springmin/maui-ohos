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
    private readonly Dictionary<int, View> _materialized = new();
    private readonly List<View> _pool = new();
    private int _windowFirst = -1;
    private int _windowLast = -1;

    public OpenHarmonyItemListMaterializer(OpenHarmonyView platformView, Func<object?, View> createItemView, Action<object?> select)
    {
        _platformView = platformView;
        _createItemView = createItemView;
        _select = select;
    }

    public double ItemHeight { get; private set; } = 40;

    public double SlotHeight => ItemHeight + Spacing;

    public double TotalHeight => _data.Count == 0 ? 0 : _data.Count * SlotHeight;

    /// <summary>Replaces the data behind the list.</summary>
    public void SetItems(System.Collections.IEnumerable? source)
    {
        _data.Clear();
        if (source is not null)
        {
            foreach (object? item in source)
            {
                _data.Add(item);
            }
        }
        Reset();
    }

    public double GetItemY(int index) => index * SlotHeight;

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
            first = Math.Max(0, (int)Math.Floor((offset - WindowMargin) / SlotHeight));
            last = Math.Min(_data.Count - 1, (int)Math.Ceiling((offset + frame.Height + WindowMargin) / SlotHeight));
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
        View view = TakeFromPool() ?? _createItemView(item);
        view.BindingContext = item;
        if (view.Handler?.PlatformView is OpenHarmonyView itemPlatform)
        {
            object? captured = item;
            itemPlatform.Tap = () => _select(captured);
        }
        RectF frame = _platformView.Frame;
        double width = frame.Width > 0 ? frame.Width : 1080;
        view.Measure(width, double.PositiveInfinity);
        Size size = view.DesiredSize;
        if (index == 0 && size.Height > 0)
        {
            ItemHeight = size.Height;
        }
        view.Arrange(new Rect(frame.X, frame.Y + GetItemY(index), width, Math.Max(size.Height, ItemHeight)));
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
