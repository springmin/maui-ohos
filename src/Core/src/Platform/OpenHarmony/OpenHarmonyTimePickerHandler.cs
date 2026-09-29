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
            [nameof(ITimePicker.IsOpen)] = MapIsOpen,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
        };

    public OpenHarmonyTimePickerHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsPicker = true, Background = Colors.DimGray, FontSize = 26 };
        view.Tap = () =>
        {
            RebuildItems();
            ShowPopup(view);
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
            HidePopup(view);
        };
        // The renderer invokes this when a touch outside the dropdown dismisses it.
        view.PopupClosed = () => HidePopup(view);
        return view;
    }

    /// <summary>Opens the half-hour dropdown and mirrors the state onto the picker's IsOpen.</summary>
    private void ShowPopup(OpenHarmonyView view)
    {
        view.PopupVisible = true;
        SyncIsOpen(open: true);
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>Closes the dropdown and mirrors the state onto the picker's IsOpen.</summary>
    private void HidePopup(OpenHarmonyView view)
    {
        view.PopupVisible = false;
        SyncIsOpen(open: false);
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>
    /// Writes the platform popup state back to the picker: the property change raises Opened/
    /// Closed on the control and re-enters MapIsOpen, which then sees the popup already in the
    /// requested state (so the sync terminates).
    /// </summary>
    private void SyncIsOpen(bool open)
    {
        if (VirtualView is { } picker && picker.IsOpen != open)
        {
            picker.IsOpen = open;
        }
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

    /// <summary>
    /// Maps ITimePicker.IsOpen: true opens the half-hour dropdown (the same path as a field
    /// tap), false closes it. The control raises Opened/Closed itself when the value changes; a
    /// platform open/close writes IsOpen back through the shared sync methods.
    /// </summary>
    public static void MapIsOpen(OpenHarmonyTimePickerHandler handler, ITimePicker picker)
    {
        if (picker.IsOpen)
        {
            handler.RebuildItems();
            handler.ShowPopup(handler.PlatformView);
        }
        else
        {
            handler.HidePopup(handler.PlatformView);
        }
    }
}
