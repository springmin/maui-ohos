// TimePicker handler for OpenHarmony: the field opens an inline dropdown of half-hour slots.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyTimePickerHandler : OpenHarmonyViewHandler<ITimePicker>
{
    private const int SlotMinutes = 30;

    public static readonly IPropertyMapper<ITimePicker, OpenHarmonyTimePickerHandler> Mapper =
        new PropertyMapper<ITimePicker, OpenHarmonyTimePickerHandler>(ViewMapper)
        {
            [nameof(ITimePicker.Time)] = MapTime,
            [nameof(ITimePicker.Format)] = MapTime,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
        };

    public OpenHarmonyTimePickerHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsPicker = true, Background = Colors.DimGray, FontSize = 26 };
        view.Tap = () =>
        {
            RebuildItems();
            view.PopupVisible = true;
            OpenHarmonyBridge.RequestRedraw();
        };
        view.PopupSelect = index =>
        {
            var time = TimeSpan.FromMinutes(index * SlotMinutes);
            if (VirtualView is { } picker)
            {
                switch (picker)
                {
                    case Microsoft.Maui.Controls.TimePicker control:
                        control.Time = time;
                        break;
                }
                MapTime(this, picker);
            }
            view.PopupVisible = false;
            OpenHarmonyBridge.RequestRedraw();
        };
        return view;
    }

    private void RebuildItems()
    {
        OpenHarmonyView view = PlatformView;
        view.PopupItems.Clear();
        for (int slot = 0; slot < 24 * 60 / SlotMinutes; slot++)
        {
            view.PopupItems.Add(TimeSpan.FromMinutes(slot * SlotMinutes).ToString(@"hh\:mm"));
        }
        var current = VirtualView?.Time ?? TimeSpan.Zero;
        view.PopupSelectedIndex = (int)Math.Round(current.TotalMinutes / SlotMinutes) % view.PopupItems.Count;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(160, widthConstraint), Math.Min(48, heightConstraint));

    public static void MapTime(OpenHarmonyTimePickerHandler handler, ITimePicker picker)
    {
        TimeSpan time = picker.Time ?? TimeSpan.Zero;
        handler.PlatformView.Text = time.ToString(string.IsNullOrEmpty(picker.Format) ? @"hh\:mm" : picker.Format);
    }

    public static void MapTextColor(OpenHarmonyTimePickerHandler handler, ITimePicker picker)
    {
        if ((picker as ITextStyle)?.TextColor is { } color)
        {
            handler.PlatformView.TextColor = color;
        }
    }
}
