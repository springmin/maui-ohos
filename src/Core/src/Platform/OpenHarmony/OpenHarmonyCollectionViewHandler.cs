// CollectionView handler for OpenHarmony: materializes only the items that intersect the
// viewport (plus a small margin) and slides that window while the list scrolls. Items are
// assumed to have a uniform height (measured from the first one).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyCollectionViewHandler : OpenHarmonyViewHandler<CollectionView>
{
    private const double Spacing = 6;
    private const double WindowMargin = 120;

    public static readonly IPropertyMapper<CollectionView, OpenHarmonyCollectionViewHandler> Mapper =
        new PropertyMapper<CollectionView, OpenHarmonyCollectionViewHandler>(ViewMapper)
        {
            [nameof(ItemsView.ItemsSource)] = MapItemsSource,
            [nameof(ItemsView.ItemTemplate)] = MapItemTemplate,
        };

    private readonly List<object?> _data = new();
    private readonly Dictionary<int, View> _materialized = new();
    private readonly List<View> _pool = new();
    private double _itemHeight = 40;
    private int _windowFirst = -1;
    private int _windowLast = -1;

    public OpenHarmonyCollectionViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new()
    {
        // Collection views reuse the scroll view machinery (clip, translate, drag scrolling).
        IsScrollView = true,
    };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        // Scrolling slides the materialization window.
        platformView.ScrollOffsetChanged = () => UpdateWindow();
        VirtualView?.InvalidateMeasure();
        OpenHarmonyBridge.RequestRedraw();
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        double height = VirtualView?.HeightRequest > 0
            ? VirtualView.HeightRequest
            : Math.Min(TotalHeight > 0 ? TotalHeight : 200, heightConstraint);
        return new Size(widthConstraint, height);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        RefreshData();
        UpdateWindow(force: true);
        PlatformView.ScrollContentWidth = (float)frame.Width;
        PlatformView.ScrollContentHeight = (float)TotalHeight;
    }

    private double SlotHeight => _itemHeight + Spacing;

    private double TotalHeight => _data.Count == 0 ? 0 : _data.Count * SlotHeight;

    private void RefreshData()
    {
        _data.Clear();
        if (VirtualView?.ItemsSource is { } source)
        {
            foreach (object? item in source)
            {
                _data.Add(item);
            }
        }
    }

    /// <summary>Materializes the items intersecting the viewport (plus margin).</summary>
    private void UpdateWindow(bool force = false)
    {
        RectF frame = PlatformView.Frame;
        double offset = PlatformView.ScrollOffsetY;
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

        // Recycle the views that left the window.
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
        // Materialize the items entering the window.
        for (int index = first; index <= last; index++)
        {
            if (!_materialized.ContainsKey(index))
            {
                _materialized[index] = Materialize(index);
            }
        }
        if (ReferenceEquals(PlatformView, null))
        {
            return;
        }
        PlatformView.ViewChildren.Clear();
        foreach (KeyValuePair<int, View> entry in _materialized.OrderBy(pair => pair.Key))
        {
            PlatformView.ViewChildren.Add(entry.Value);
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    private View Materialize(int index)
    {
        object? item = _data[index];
        View view = TakeFromPool() ?? CreateItemView(item);
        view.BindingContext = item;
        if (view.Handler?.PlatformView is OpenHarmonyView itemPlatform)
        {
            object? captured = item;
            itemPlatform.Tap = () => Select(captured);
        }
        RectF frame = PlatformView.Frame;
        double width = frame.Width > 0 ? frame.Width : 1080;
        view.Measure(width, double.PositiveInfinity);
        Size size = view.DesiredSize;
        if (index == 0 && size.Height > 0)
        {
            _itemHeight = size.Height;
        }
        double y = index * SlotHeight;
        view.Arrange(new Rect(frame.X, frame.Y + y, width, Math.Max(size.Height, _itemHeight)));
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
        handler.RefreshData();
        handler.UpdateWindow(force: true);
    }

    public static void MapItemTemplate(OpenHarmonyCollectionViewHandler handler, CollectionView collectionView)
    {
        handler._pool.Clear();
        handler._materialized.Clear();
        handler._windowFirst = -1;
        handler._windowLast = -1;
        handler.UpdateWindow(force: true);
    }
}
