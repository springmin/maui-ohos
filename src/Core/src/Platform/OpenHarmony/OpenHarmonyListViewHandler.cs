// Legacy ListView handler for OpenHarmony: shares the virtualized list pipeline. Cells are
// rendered through their inner view (ViewCell) or as text (TextCell/ImageCell).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyListViewHandler : OpenHarmonyViewHandler<ListView>
{
    private OpenHarmonyItemListMaterializer? _materializer;

    public static readonly IPropertyMapper<ListView, OpenHarmonyListViewHandler> Mapper =
        new PropertyMapper<ListView, OpenHarmonyListViewHandler>(ViewMapper)
        {
            [nameof(ListView.ItemsSource)] = MapItemsSource,
            [nameof(ListView.ItemTemplate)] = MapItemTemplate,
        };

    public OpenHarmonyListViewHandler() : base(Mapper) { }

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
        _materializer?.SetItems(VirtualView?.ItemsSource);
        _materializer?.Update(force: true);
        PlatformView.ScrollContentWidth = (float)frame.Width;
        PlatformView.ScrollContentHeight = (float)(_materializer?.TotalHeight ?? 0);
    }

    private void Select(object? item)
    {
        if (VirtualView is { } listView)
        {
            listView.SelectedItem = item;
            OpenHarmonyBridge.RequestRedraw();
        }
    }

    private View CreateItemView(object? item)
    {
        object? content = VirtualView?.ItemTemplate?.CreateContent();
        if (content is BindableObject bindable && bindable.BindingContext is null)
        {
            // Cells resolve their bindings from the item before we read their text.
            bindable.BindingContext = item;
        }
        View view = content switch
        {
            ViewCell cell when cell.View is View inner => inner,
            TextCell textCell => new Label { Text = textCell.Text ?? string.Empty, FontSize = 26 },
            View v => v,
            _ => new Label { Text = content?.ToString() ?? item?.ToString() ?? string.Empty, FontSize = 26 },
        };
        OpenHarmonyHandlerConnector.ConnectTree(view);
        return view;
    }

    public static void MapItemsSource(OpenHarmonyListViewHandler handler, ListView listView)
    {
        handler._materializer?.SetItems(listView.ItemsSource);
        handler._materializer?.Update(force: true);
    }

    public static void MapItemTemplate(OpenHarmonyListViewHandler handler, ListView listView)
        => handler._materializer?.Reset();
}
