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
            SwitchCell switchCell => CreateSwitchCell(switchCell),
            EntryCell entryCell => CreateEntryCell(entryCell),
            View v => v,
            _ => new Label { Text = content?.ToString() ?? item?.ToString() ?? string.Empty, FontSize = 26 },
        };
        OpenHarmonyHandlerConnector.ConnectTree(view);
        return view;
    }

    /// <summary>Materialises a SwitchCell as a label + Switch row.</summary>
    private static View CreateSwitchCell(SwitchCell cell)
    {
        var label = new Label { Text = cell.Text ?? string.Empty, FontSize = 26, VerticalOptions = LayoutOptions.Center };
        var toggle = new Switch { IsToggled = cell.On, HorizontalOptions = LayoutOptions.End };
        if (cell.OnColor is { } onColor)
        {
            toggle.OnColor = onColor;
        }
        // The platform toggle is the source of truth for taps; bindable changes flow back in.
        toggle.Toggled += (_, e) => cell.On = e.Value;
        cell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == SwitchCell.OnProperty.PropertyName && toggle.IsToggled != cell.On)
            {
                toggle.IsToggled = cell.On;
            }
            else if (e.PropertyName == SwitchCell.TextProperty.PropertyName)
            {
                label.Text = cell.Text ?? string.Empty;
            }
        };
        return CreateCellRow(label, toggle);
    }

    /// <summary>Materialises an EntryCell as a label + Entry row.</summary>
    private static View CreateEntryCell(EntryCell cell)
    {
        var label = new Label { Text = cell.Label ?? string.Empty, FontSize = 26, VerticalOptions = LayoutOptions.Center };
        if (cell.LabelColor is { } labelColor)
        {
            label.TextColor = labelColor;
        }
        // A real Entry keeps the standard keyboard behaviour of the Entry handler (tap focuses,
        // the shell's text input/completed events update the control).
        var entry = new Entry
        {
            Text = cell.Text ?? string.Empty,
            Placeholder = cell.Placeholder ?? string.Empty,
            Keyboard = cell.Keyboard ?? Keyboard.Default,
            FontSize = 26,
            VerticalOptions = LayoutOptions.Center,
        };
        entry.TextChanged += (_, e) => cell.Text = e.NewTextValue;
        entry.Completed += (_, _) => cell.SendCompleted();
        cell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == EntryCell.TextProperty.PropertyName && entry.Text != cell.Text)
            {
                entry.Text = cell.Text;
            }
            else if (e.PropertyName == EntryCell.LabelProperty.PropertyName)
            {
                label.Text = cell.Label ?? string.Empty;
            }
            else if (e.PropertyName == EntryCell.PlaceholderProperty.PropertyName)
            {
                entry.Placeholder = cell.Placeholder ?? string.Empty;
            }
            else if (e.PropertyName == EntryCell.LabelColorProperty.PropertyName && cell.LabelColor is { } color)
            {
                label.TextColor = color;
            }
        };
        return CreateCellRow(label, entry);
    }

    /// <summary>Shared cell row: content left, interactive control right.</summary>
    private static View CreateCellRow(View content, View trailing)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 12,
            Padding = new Thickness(12, 8),
        };
        row.Children.Add(content);
        row.Children.Add(trailing);
        Grid.SetColumn(trailing, 1);
        return row;
    }

    public static void MapItemsSource(OpenHarmonyListViewHandler handler, ListView listView)
    {
        handler._materializer?.SetItems(listView.ItemsSource);
        handler._materializer?.Update(force: true);
    }

    public static void MapItemTemplate(OpenHarmonyListViewHandler handler, ListView listView)
        => handler._materializer?.Reset();
}
