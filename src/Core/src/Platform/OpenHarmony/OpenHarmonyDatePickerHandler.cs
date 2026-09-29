// DatePicker handler for OpenHarmony: the field opens a month calendar (header with < / >, a
// weekday row and a 6x7 day grid). Only days inside MinimumDate..MaximumDate are selectable -
// the day grid draws the rest disabled and refuses to hit-test them - and the header clamps to
// the range's first/last month, so no tap can navigate outside the selectable range.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyDatePickerHandler : OpenHarmonyViewHandler<IDatePicker>
{
    public static readonly IPropertyMapper<IDatePicker, OpenHarmonyDatePickerHandler> Mapper =
        new PropertyMapper<IDatePicker, OpenHarmonyDatePickerHandler>(ViewMapper)
        {
            [nameof(IDatePicker.Date)] = MapDate,
            [nameof(IDatePicker.Format)] = MapDate,
            [nameof(IDatePicker.MinimumDate)] = MapCalendarRange,
            [nameof(IDatePicker.MaximumDate)] = MapCalendarRange,
            [nameof(IDatePicker.IsOpen)] = MapIsOpen,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
        };

    public OpenHarmonyDatePickerHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsPicker = true, IsCalendar = true, Background = Colors.DimGray, FontSize = 26 };
        view.Tap = () => ShowCalendar(view, (VirtualView?.Date ?? DateTime.Today).Date);
        view.CalendarPreviousMonth = () => ShiftCalendarMonth(view, -1);
        view.CalendarNextMonth = () => ShiftCalendarMonth(view, +1);
        view.CalendarSelectDay = selected =>
        {
            DateTime date = ClampToRange(view, selected.Date);
            if (VirtualView is { } picker)
            {
                if (picker is Microsoft.Maui.Controls.DatePicker control)
                {
                    control.Date = date;
                }
                // The control's own coercion (MinimumDate/MaximumDate) has the last word.
                date = ClampToRange(view, (picker.Date ?? date).Date);
                MapDate(this, picker);
            }
            view.CalendarYear = date.Year;
            view.CalendarMonth = date.Month;
            view.CalendarSelectedDay = date.Day;
            HideCalendar(view);
        };
        // The renderer invokes this when a touch outside the calendar dismisses it.
        view.PopupClosed = () => HideCalendar(view);
        return view;
    }

    /// <summary>Opens the calendar on the date's month and mirrors the state onto IsOpen.</summary>
    private void ShowCalendar(OpenHarmonyView view, DateTime date)
    {
        OpenCalendar(view, date);
        SyncIsOpen(open: true);
    }

    /// <summary>Closes the calendar and mirrors the state onto the date picker's IsOpen.</summary>
    private void HideCalendar(OpenHarmonyView view)
    {
        view.PopupVisible = false;
        SyncIsOpen(open: false);
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>
    /// Writes the platform calendar state back to the picker: the property change raises
    /// Opened/Closed on the control and re-enters MapIsOpen, which then sees the calendar
    /// already in the requested state (so the sync terminates).
    /// </summary>
    private void SyncIsOpen(bool open)
    {
        if (VirtualView is { } picker && picker.IsOpen != open)
        {
            picker.IsOpen = open;
        }
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(260, widthConstraint), Math.Min(48, heightConstraint));

    /// <summary>Clamps the picker's date into range and opens the calendar on that month.</summary>
    private static void OpenCalendar(OpenHarmonyView view, DateTime date)
    {
        DateTime clamped = ClampToRange(view, date);
        view.CalendarYear = clamped.Year;
        view.CalendarMonth = clamped.Month;
        view.CalendarSelectedDay = clamped.Day;
        view.PopupVisible = true;
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>
    /// Moves the shown month by <paramref name="delta"/>, clamped to the month of MinimumDate /
    /// MaximumDate, so the header can never navigate outside the selectable range.
    /// </summary>
    private static void ShiftCalendarMonth(OpenHarmonyView view, int delta)
    {
        DateTime first = new(view.CalendarMinimum.Year, view.CalendarMinimum.Month, 1);
        DateTime last = new(view.CalendarMaximum.Year, view.CalendarMaximum.Month, 1);
        DateTime shown = new(view.CalendarYear, view.CalendarMonth, 1);
        if (shown < first)
        {
            shown = first;
        }
        if (shown > last)
        {
            shown = last;
        }
        // AddMonths cannot step past DateTime.MinValue/MaxValue (the unset-range defaults).
        DateTime target = shown;
        if (delta < 0 && shown > first)
        {
            target = shown.AddMonths(-1);
        }
        else if (delta > 0 && shown < last)
        {
            target = shown.AddMonths(1);
        }
        if (target < first)
        {
            target = first;
        }
        if (target > last)
        {
            target = last;
        }
        if (target.Year == view.CalendarYear && target.Month == view.CalendarMonth)
        {
            return;
        }
        view.CalendarYear = target.Year;
        view.CalendarMonth = target.Month;
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>Clamps a date into the picker's MinimumDate..MaximumDate range (date only).</summary>
    private static DateTime ClampToRange(OpenHarmonyView view, DateTime date)
    {
        DateTime min = view.CalendarMinimum.Date;
        DateTime max = view.CalendarMaximum.Date;
        if (date < min)
        {
            return min;
        }
        if (date > max)
        {
            return max;
        }
        return date;
    }

    public static void MapDate(OpenHarmonyDatePickerHandler handler, IDatePicker picker)
    {
        DateTime date = picker.Date ?? DateTime.Today;
        OpenHarmonyView view = handler.PlatformView;
        view.Text = date.ToString(string.IsNullOrEmpty(picker.Format) ? "yyyy-MM-dd" : picker.Format);
        if (view.PopupVisible)
        {
            // The date changed while the calendar is open (programmatic or a clamped selection):
            // keep the shown month and the highlighted day in sync.
            DateTime clamped = ClampToRange(view, date.Date);
            view.CalendarYear = clamped.Year;
            view.CalendarMonth = clamped.Month;
            view.CalendarSelectedDay = clamped.Day;
        }
    }

    public static void MapCalendarRange(OpenHarmonyDatePickerHandler handler, IDatePicker picker)
    {
        OpenHarmonyView view = handler.PlatformView;
        view.CalendarMinimum = picker.MinimumDate ?? DateTime.MinValue;
        view.CalendarMaximum = picker.MaximumDate ?? DateTime.MaxValue;
        if (!view.PopupVisible)
        {
            return;
        }
        // A range change can exclude the shown month or the highlighted day; re-clamp in place.
        int day = Math.Clamp(view.CalendarSelectedDay, 1, DateTime.DaysInMonth(view.CalendarYear, view.CalendarMonth));
        DateTime clamped = ClampToRange(view, new DateTime(view.CalendarYear, view.CalendarMonth, day));
        view.CalendarYear = clamped.Year;
        view.CalendarMonth = clamped.Month;
        view.CalendarSelectedDay = clamped.Day;
    }

    public static void MapTextColor(OpenHarmonyDatePickerHandler handler, IDatePicker picker)
    {
        if ((picker as ITextStyle)?.TextColor is { } color)
        {
            handler.PlatformView.TextColor = color;
        }
    }

    /// <summary>
    /// Maps IDatePicker.IsOpen: true opens the calendar on the picker's date, false closes it
    /// (the same paths as a field tap and a day selection). The control raises Opened/Closed
    /// itself when the value changes; a platform open/close writes IsOpen back through the
    /// shared sync methods.
    /// </summary>
    public static void MapIsOpen(OpenHarmonyDatePickerHandler handler, IDatePicker picker)
    {
        if (picker.IsOpen)
        {
            handler.ShowCalendar(handler.PlatformView, (picker.Date ?? DateTime.Today).Date);
        }
        else
        {
            handler.HideCalendar(handler.PlatformView);
        }
    }
}
