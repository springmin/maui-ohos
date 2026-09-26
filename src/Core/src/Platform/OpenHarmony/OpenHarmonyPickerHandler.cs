// Picker handler for OpenHarmony: a field that opens an inline dropdown (no native dialog).
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyPickerHandler : OpenHarmonyViewHandler<IPicker>
{
    public static readonly IPropertyMapper<IPicker, OpenHarmonyPickerHandler> Mapper =
        new PropertyMapper<IPicker, OpenHarmonyPickerHandler>(ViewMapper)
        {
            [nameof(IPicker.Items)] = MapItems,
            [nameof(IPicker.SelectedIndex)] = MapSelectedIndex,
            [nameof(IPicker.Title)] = MapTitle,
            [nameof(IPicker.TitleColor)] = MapTitleColor,
        };

    public OpenHarmonyPickerHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsPicker = true, Background = Colors.DimGray, FontSize = 26 };
        view.Tap = () =>
        {
            view.PopupVisible = true;
            OpenHarmonyBridge.RequestRedraw();
        };
        view.PopupSelect = index =>
        {
            if (VirtualView is { } picker && index >= 0 && index < picker.Items.Count)
            {
                switch (picker)
                {
                    case Microsoft.Maui.Controls.Picker control:
                        control.SelectedIndex = index;
                        break;
                }
                MapSelectedIndex(this, picker);
            }
            view.PopupVisible = false;
            view.PopupClosed?.Invoke();
            OpenHarmonyBridge.RequestRedraw();
        };
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        // HorizontalStackLayout measures children with +∞ width. Never echo a non-finite
        // constraint back as the desired size: the arranged frame then becomes ∞/NaN and the
        // compositor/rasterizer spins. Prefer an explicit WidthRequest (like the BoxView,
        // RefreshView and SwipeView handlers do); otherwise fall back to a finite default.
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double requestedWidth = element?.WidthRequest ?? double.NaN;
        double width = requestedWidth > 0 && double.IsFinite(requestedWidth)
            ? requestedWidth
            : (double.IsFinite(widthConstraint) ? widthConstraint : DefaultWidth);

        // Same guard for height: Math.Min(48, NaN) would propagate NaN.
        double height = double.IsFinite(heightConstraint) ? Math.Min(48, heightConstraint) : 48;
        return new(width, height);
    }

    // Fallback for pickers that set no WidthRequest and are measured with a non-finite constraint.
    const double DefaultWidth = 160;

    public static void MapItems(OpenHarmonyPickerHandler handler, IPicker picker)
    {
        handler.PlatformView.PopupItems.Clear();
        foreach (string item in picker.Items)
        {
            handler.PlatformView.PopupItems.Add(item);
        }
        MapSelectedIndex(handler, picker);
    }

    public static void MapSelectedIndex(OpenHarmonyPickerHandler handler, IPicker picker)
    {
        handler.PlatformView.PopupSelectedIndex = picker.SelectedIndex;
        handler.PlatformView.Text = picker.SelectedIndex >= 0 && picker.SelectedIndex < picker.Items.Count
            ? picker.Items[picker.SelectedIndex]
            : picker.Title ?? string.Empty;
    }

    public static void MapTitle(OpenHarmonyPickerHandler handler, IPicker picker)
    {
        handler.PlatformView.PopupTitle = picker.Title;
        if (picker.SelectedIndex < 0)
        {
            handler.PlatformView.Text = picker.Title ?? string.Empty;
        }
    }

    public static void MapTitleColor(OpenHarmonyPickerHandler handler, IPicker picker)
    {
        if (picker.TitleColor is { } color)
        {
            handler.PlatformView.TextColor = color;
        }
    }
}
