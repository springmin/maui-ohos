// DatePicker handler for OpenHarmony: the field opens an inline dropdown of candidate dates
// (the current date +/- a week). A full calendar view is a later iteration.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyDatePickerHandler : OpenHarmonyViewHandler<IDatePicker>
{
    private const int DaysAroundCurrent = 7;

    public static readonly IPropertyMapper<IDatePicker, OpenHarmonyDatePickerHandler> Mapper =
        new PropertyMapper<IDatePicker, OpenHarmonyDatePickerHandler>(ViewMapper)
        {
            [nameof(IDatePicker.Date)] = MapDate,
            [nameof(IDatePicker.Format)] = MapDate,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
        };

    public OpenHarmonyDatePickerHandler() : base(Mapper) { }

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
            if (VirtualView is { } picker && index >= 0 && index < view.PopupItems.Count)
            {
                DateTime today = (picker.Date ?? DateTime.Today).Date;
                DateTime chosen = today.AddDays(index - DaysAroundCurrent);
                switch (picker)
                {
                    case Microsoft.Maui.Controls.DatePicker control:
                        control.Date = chosen;
                        break;
                }
                MapDate(this, picker);
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
        if (VirtualView is not { } picker)
        {
            return;
        }
        DateTime today = (picker.Date ?? DateTime.Today).Date;
        for (int offset = -DaysAroundCurrent; offset <= DaysAroundCurrent; offset++)
        {
            view.PopupItems.Add(today.AddDays(offset).ToString("yyyy-MM-dd ddd"));
        }
        view.PopupSelectedIndex = DaysAroundCurrent;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(260, widthConstraint), Math.Min(48, heightConstraint));

    public static void MapDate(OpenHarmonyDatePickerHandler handler, IDatePicker picker)
    {
        DateTime date = picker.Date ?? DateTime.Today;
        handler.PlatformView.Text = date.ToString(string.IsNullOrEmpty(picker.Format) ? "yyyy-MM-dd" : picker.Format);
    }

    public static void MapTextColor(OpenHarmonyDatePickerHandler handler, IDatePicker picker)
    {
        if ((picker as ITextStyle)?.TextColor is { } color)
        {
            handler.PlatformView.TextColor = color;
        }
    }
}
