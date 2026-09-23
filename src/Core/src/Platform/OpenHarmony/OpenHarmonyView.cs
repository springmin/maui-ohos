// Platform view for the OpenHarmony compositor: a lightweight drawing element. Handlers set
// its properties from the virtual view; OpenHarmonyWindowRenderer draws and hit-tests it.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public class OpenHarmonyView
{
    public IView? VirtualView { get; set; }

    public string? Text { get; set; }
    public float FontSize { get; set; } = 14f;
    public Color TextColor { get; set; } = Colors.White;
    public Color? Background { get; set; }
    public float CornerRadius { get; set; }

    /// <summary>Invoked when a tap lands inside this view (buttons wire it to SendClicked).</summary>
    public Action? Tap { get; set; }

    public bool Pressed { get; set; }

    // Entry support
    public bool IsTextEntry { get; set; }
    public string? Placeholder { get; set; }
    public bool IsFocused { get; set; }
    public int CursorPosition { get; set; } = -1;
    public int SelectionLength { get; set; }
    /// <summary>Anchor of an in-progress selection drag (-1 when none).</summary>
    public int TextAnchor { get; set; } = -1;

    // Image support
    public byte[]? ImageBytes { get; set; }
    public Aspect ImageAspect { get; set; } = Aspect.AspectFit;

    // CheckBox support
    public bool IsCheckBox { get; set; }
    public bool IsChecked { get; set; }
    public Color CheckBoxColor { get; set; } = Colors.White;

    // Switch support
    public bool IsSwitch { get; set; }
    public bool IsOn { get; set; }
    public Color SwitchTrackColor { get; set; } = Colors.DimGray;
    public Color SwitchThumbColor { get; set; } = Colors.White;

    // Slider support
    public bool IsSlider { get; set; }
    public double SliderValue { get; set; }
    public double SliderMinimum { get; set; }
    public double SliderMaximum { get; set; } = 1;
    public Color SliderMinimumTrackColor { get; set; } = Colors.DodgerBlue;
    public Color SliderMaximumTrackColor { get; set; } = Colors.DimGray;
    public Color SliderThumbColor { get; set; } = Colors.White;

    /// <summary>(fraction, completed) invoked while a drag updates the slider value.</summary>
    public Action<float, bool>? SliderDrag { get; set; }

    /// <summary>Current drag fraction, used when the drag completes.</summary>
    public float SliderFraction { get; set; }

    // Progress bar support
    public bool IsProgressBar { get; set; }
    public double Progress { get; set; }
    public Color ProgressColor { get; set; } = Colors.DodgerBlue;

    // Activity indicator support. IsActivityIndicator/IsRunning feed a global registration
    // count so the frame loop can ask "is any spinner running?" in O(1) instead of walking the
    // whole tree every frame (the tree walk stays as the exact answer once some view is
    // registered, so hidden spinners behave exactly like before).
    private bool _isActivityIndicator;
    private bool _isRunning;
    private bool _animationRegistered;

    public bool IsActivityIndicator
    {
        get => _isActivityIndicator;
        set
        {
            if (_isActivityIndicator == value)
            {
                return;
            }
            _isActivityIndicator = value;
            UpdateAnimationRegistration();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }
            _isRunning = value;
            UpdateAnimationRegistration();
        }
    }

    public Color IndicatorColor { get; set; } = Colors.White;

    /// <summary>Shared rotation (degrees) advanced by the renderer for animated views.</summary>
    public static float AnimationAngle { get; set; }

    /// <summary>True when this view keeps redrawing on its own (activity indicators).</summary>
    public bool NeedsAnimation => _isActivityIndicator && _isRunning;

    private static int s_animationCount;

    /// <summary>
    /// Platform views that currently want continuous redraws (their <see cref="NeedsAnimation"/>
    /// is true). The frame loop reads this to skip the per-frame tree walk when nothing animates;
    /// the walk still decides whether a running indicator is actually visible.
    /// </summary>
    internal static int AnimationCount => Volatile.Read(ref s_animationCount);

    private void UpdateAnimationRegistration()
    {
        bool active = _isActivityIndicator && _isRunning;
        if (active == _animationRegistered)
        {
            return;
        }
        _animationRegistered = active;
        if (active)
        {
            Interlocked.Increment(ref s_animationCount);
        }
        else
        {
            Interlocked.Decrement(ref s_animationCount);
        }
    }

    /// <summary>Raised when image bytes are blitted (tests observe the destination rect).</summary>
    public static Action<RectF>? ImageDrawn;

    // WebView support (the shell owns the ArkWeb component; this view is a placeholder)
    public bool IsWebView { get; set; }

    // GraphicsView support
    public bool IsGraphicsView { get; set; }
    public Action<PointF>? GraphicsTap { get; set; }

    // Indicator view support
    public bool IsIndicatorView { get; set; }
    public int IndicatorCount { get; set; }
    public int IndicatorPosition { get; set; }
    public Color DotsColor { get; set; } = Colors.Gray;
    public Color SelectedDotsColor { get; set; } = Colors.White;
    public float DotsSize { get; set; } = 6f;

    // Shape support (Rectangle/Ellipse/Line/Path/Polygon/Polyline/RoundRectangle)
    public bool IsShape { get; set; }
    public Microsoft.Maui.Graphics.IShape? Shape { get; set; }
    public Paint? ShapeFill { get; set; }
    public Color ShapeStroke { get; set; } = Colors.White;
    public float ShapeStrokeThickness { get; set; } = 1f;

    // Border support
    public bool IsBorder { get; set; }
    public Color BorderStroke { get; set; } = Colors.Gray;
    public float BorderStrokeThickness { get; set; } = 1f;

    /// <summary>Optional stroke drawn around the view's frame (buttons, frames).</summary>
    public Color? StrokeColor { get; set; }
    public float StrokeThickness { get; set; }

    /// <summary>Disabled views paint their colours at half alpha (set by the renderer).</summary>
    public bool Dimmed { get; set; }

    // Stepper support
    public bool IsStepper { get; set; }
    public double StepperValue { get; set; }
    public double StepperMinimum { get; set; }
    public double StepperMaximum { get; set; } = 100;
    public double StepperInterval { get; set; } = 1;
    /// <summary>Invoked with true (increment) or false (decrement) when a stepper half is tapped.</summary>
    public Action<bool>? StepperStep { get; set; }

    // Radio button support
    public bool IsRadioButton { get; set; }
    public bool RadioChecked { get; set; }
    public Color RadioColor { get; set; } = Colors.DodgerBlue;

    /// <summary>(deltaX, deltaY) when a pan ends on this view (carousel paging).</summary>
    public Action<float, float>? Swipe { get; set; }

    // Shell chrome (title bar + back button)
    public bool ShowsTitleBar { get; set; }
    /// <summary>Re-syncs the chrome (title/back) with the virtual view before drawing.</summary>
    public Action? ChromeRefresh { get; set; }
    public string? TitleText { get; set; }
    public bool ShowsBack { get; set; }
    public const float TitleBarHeight = 56f;

    public bool InBackButton(float x, float y)
    {
        RectF frame = Frame;
        return ShowsTitleBar && ShowsBack &&
               x >= frame.X && x <= frame.X + 56 &&
               y >= frame.Y && y <= frame.Y + TitleBarHeight;
    }

    // Shell flyout support
    public bool ShowsHamburger { get; set; }
    public bool FlyoutOpen { get; set; }
    public List<string> FlyoutItems { get; } = new();
    public Action<int>? FlyoutSelect { get; set; }
    public Action? FlyoutRequested { get; set; }

    public void DrawFlyoutPanel(MauiCanvas canvas)
    {
        RectF frame = Frame;
        float width = Math.Min(320f, frame.Width);
        canvas.FillColor = Colors.Black.WithAlpha(0.5f);
        canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
        canvas.FillColor = Colors.DimGray;
        canvas.FillRectangle(frame.X, frame.Y, width, frame.Height);
        canvas.FontSize = 26;
        canvas.FontColor = Colors.White;
        for (int i = 0; i < FlyoutItems.Count; i++)
        {
            float rowY = frame.Y + 16 + i * 52;
            canvas.DrawString(FlyoutItems[i], frame.X + 20, rowY, width - 40, 44,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    /// <summary>Index of the flyout row at the point (-1 outside the panel, -2 dismiss).</summary>
    public int FlyoutItemAt(float x, float y)
    {
        RectF frame = Frame;
        float width = Math.Min(320f, frame.Width);
        if (x > frame.X + width)
        {
            return -2;
        }
        float relative = y - frame.Y - 16;
        if (relative < 0)
        {
            return -1;
        }
        int index = (int)(relative / 52);
        return index >= 0 && index < FlyoutItems.Count ? index : -1;
    }

    // Calendar (DatePicker) support
    public bool IsCalendar { get; set; }
    public int CalendarYear { get; set; }
    public int CalendarMonth { get; set; } = 1;
    public int CalendarSelectedDay { get; set; }
    public Action? CalendarPreviousMonth { get; set; }
    public Action? CalendarNextMonth { get; set; }
    public Action<DateTime>? CalendarSelectDay { get; set; }

    public const float CalendarWidth = 380f;
    public const float CalendarHeaderHeight = 52f;
    public const float CalendarRowHeight = 46f;
    public const int CalendarWeeks = 6;

    public static float CalendarHeight => CalendarHeaderHeight + CalendarRowHeight * (CalendarWeeks + 1);

    /// <summary>Draws the month calendar overlay.</summary>
    public void DrawCalendar(MauiCanvas canvas)
    {
        RectF frame = Frame;
        float x = frame.X;
        float y = frame.Y + frame.Height;
        float width = CalendarWidth;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(x, y, width, CalendarHeight);
        canvas.StrokeColor = Colors.Gray;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(x, y, width, CalendarHeight);

        // Header: < Month Year >
        canvas.FontSize = 26;
        canvas.FontColor = Colors.White;
        canvas.DrawString("<", x + 8, y, 40, CalendarHeaderHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.DrawString($"{CalendarYear:0000}-{CalendarMonth:00}", x + 48, y, width - 96, CalendarHeaderHeight,
            HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.DrawString(">", x + width - 48, y, 40, CalendarHeaderHeight, HorizontalAlignment.Center, VerticalAlignment.Center);

        // Weekday header (Monday first).
        string[] names = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
        canvas.FontSize = 20;
        canvas.FontColor = Colors.Gray;
        for (int column = 0; column < 7; column++)
        {
            canvas.DrawString(names[column], x + column * width / 7f, y + CalendarHeaderHeight,
                width / 7f, CalendarRowHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        // Day grid.
        DateTime first = new(CalendarYear, CalendarMonth, 1);
        int leading = ((int)first.DayOfWeek + 6) % 7;
        int days = DateTime.DaysInMonth(CalendarYear, CalendarMonth);
        DateTime today = DateTime.Today;
        canvas.FontSize = 22;
        for (int day = 1; day <= days; day++)
        {
            int cell = leading + day - 1;
            int row = cell / 7;
            int column = cell % 7;
            float cellX = x + column * width / 7f;
            float cellY = y + CalendarHeaderHeight + (row + 1) * CalendarRowHeight;
            if (row >= CalendarWeeks)
            {
                break;
            }
            bool isSelected = day == CalendarSelectedDay;
            bool isToday = CalendarYear == today.Year && CalendarMonth == today.Month && day == today.Day;
            if (isSelected)
            {
                canvas.FillColor = Colors.DodgerBlue;
                canvas.FillCircle(cellX + width / 14f, cellY + CalendarRowHeight / 2f, CalendarRowHeight / 2f - 2);
            }
            canvas.FontColor = isSelected ? Colors.White : isToday ? Colors.DodgerBlue : Colors.White;
            canvas.DrawString(day.ToString(), cellX, cellY, width / 7f, CalendarRowHeight,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    /// <summary>Hit test for the calendar: -1 outside, -2 previous, -3 next, 1..31 day.</summary>
    public int CalendarHit(float x, float y)
    {
        RectF frame = Frame;
        float left = frame.X;
        float top = frame.Y + frame.Height;
        float width = CalendarWidth;
        if (x < left || x > left + width || y < top || y > top + CalendarHeight)
        {
            return -1;
        }
        if (y < top + CalendarHeaderHeight)
        {
            if (x < left + 48)
            {
                return -2;
            }
            return x > left + width - 48 ? -3 : 0;
        }
        float rowY = y - top - CalendarHeaderHeight;
        int row = (int)(rowY / CalendarRowHeight);
        int column = (int)((x - left) / (width / 7f));
        if (row < 1 || column < 0 || column > 6)
        {
            return 0;
        }
        DateTime first = new(CalendarYear, CalendarMonth, 1);
        int leading = ((int)first.DayOfWeek + 6) % 7;
        int day = (row - 1) * 7 + column - leading + 1;
        int days = DateTime.DaysInMonth(CalendarYear, CalendarMonth);
        return day >= 1 && day <= days ? day : 0;
    }

    // Flyout page support
    public bool IsFlyoutPage { get; set; }
    public bool FlyoutPresented { get; set; }
    public float FlyoutWidth { get; set; } = 320f;
    public Action? OpenFlyout { get; set; }
    public Action? FlyoutDismiss { get; set; }
    public const float HamburgerSize = 40f;

    public bool InFlyoutPanel(float x, float y)
    {
        RectF frame = Frame;
        return FlyoutPresented && x >= frame.X && x <= frame.X + FlyoutWidth;
    }

    public bool InHamburger(float x, float y)
    {
        RectF frame = Frame;
        return (IsFlyoutPage && !FlyoutPresented || ShowsHamburger) &&
               x >= frame.X && x <= frame.X + HamburgerSize &&
               y >= frame.Y && y <= frame.Y + HamburgerSize;
    }

    // Picker support (inline dropdown rendered as an overlay)
    public bool IsPicker { get; set; }
    public List<string> PopupItems { get; } = new();
    public bool PopupVisible { get; set; }
    public int PopupSelectedIndex { get; set; } = -1;
    public string? PopupTitle { get; set; }
    public Action<int>? PopupSelect { get; set; }
    public Action? PopupClosed { get; set; }

    /// <summary>Height of one dropdown row.</summary>
    public const float PopupRowHeight = 46f;

    // Tabbed page support
    public bool IsTabbedPage { get; set; }
    public List<string> TabTitles { get; } = new();

    /// <summary>False hides the bottom bar (Shell.TabBarIsVisible).</summary>
    public bool TabTitlesVisible { get; set; } = true;

    /// <summary>Optional tab icons (encoded bytes) aligned with TabTitles.</summary>
    public List<byte[]?> TabIcons { get; } = new();
    public int SelectedTab { get; set; }
    public Action<int>? TabSelected { get; set; }
    public const float TabBarHeight = 56f;

    // Navigation page support
    public bool IsNavigationPage { get; set; }
    public float NavBarHeight { get; set; } = 48f;
    public string? NavTitle { get; set; }
    public bool CanGoBack { get; set; }
    public Color NavBarColor { get; set; } = Colors.Black;
    public Color NavBarTextColor { get; set; } = Colors.White;
    public Action? BackTapped { get; set; }

    /// <summary>Width of the tappable back area in the navigation bar.</summary>
    public const float NavBackWidth = 88f;

    // RefreshView support
    public bool IsRefreshView { get; set; }
    public bool IsRefreshing { get; set; }
    public Color RefreshColor { get; set; } = Colors.DodgerBlue;
    public Action<bool>? RefreshChanged { get; set; }

    /// <summary>Called by the renderer when a drag over this view completes.</summary>
    public void RefreshDrag(float dx, float dy)
    {
        if (!IsRefreshView || dy < 60 || IsRefreshing)
        {
            return;
        }
        // A pull past the threshold starts a refresh; MAUI runs the command when IsRefreshing flips.
        IsRefreshing = true;
        RefreshChanged?.Invoke(true);
    }

    // SwipeView support
    public bool IsSwipeView { get; set; }
    public bool IsSwipeOpen { get; private set; }
    public bool SwipeOpenToRight { get; private set; }
    public List<(string Text, Color Background, Action Activate)> SwipeItems { get; } = new();
    public float SwipeItemWidth { get; set; } = 140f;
    public Action<bool>? SwipeOpenChanged { get; set; }

    /// <summary>Revealed swipe item rectangle (drawing and hit testing share it).</summary>
    public RectF SwipeItemRect(int index)
    {
        RectF frame = Frame;
        float height = frame.Height / Math.Max(1, SwipeItems.Count);
        float x = SwipeOpenToRight ? frame.Right - SwipeItemWidth : frame.X;
        return new RectF(x, frame.Y + index * height, SwipeItemWidth, height);
    }

    public int SwipeItemAt(float x, float y)
    {
        if (!IsSwipeOpen)
        {
            return -1;
        }
        for (int i = 0; i < SwipeItems.Count; i++)
        {
            if (SwipeItemRect(i).Contains(x, y))
            {
                return i;
            }
        }
        return -1;
    }

    public void SetSwipeOpen(bool open, bool notify = true)
    {
        if (open && SwipeItems.Count == 0)
        {
            open = false;
        }
        if (IsSwipeOpen == open)
        {
            return;
        }
        IsSwipeOpen = open;
        if (notify)
        {
            SwipeOpenChanged?.Invoke(open);
        }
    }

    /// <summary>Called by the renderer when a drag over this view completes.</summary>
    public void SwipeDrag(float dx, float dy)
    {
        if (!IsSwipeView || Math.Abs(dx) < 40)
        {
            return;
        }
        // Dragging left reveals the trailing (right) items, dragging right the leading ones.
        SwipeOpenToRight = dx < 0;
        SetSwipeOpen(true);
    }

    /// <summary>Toolbar items of the current page: label plus activation, drawn right-aligned.</summary>
    public List<(string Text, Action Activate)> ToolbarItems { get; } = new();
    public float ToolbarItemWidth { get; set; } = 140f;

    /// <summary>Rectangle of a toolbar item (drawing and hit testing share it).</summary>
    public RectF ToolbarItemRect(int index)
    {
        RectF frame = Frame;
        return new RectF(frame.Right - (index + 1) * ToolbarItemWidth, frame.Y, ToolbarItemWidth, NavBarHeight);
    }

    public int ToolbarItemAt(float x, float y)
    {
        for (int i = 0; i < ToolbarItems.Count; i++)
        {
            if (ToolbarItemRect(i).Contains(x, y))
            {
                return i;
            }
        }
        return -1;
    }

    public bool InBackRegion(float x, float y)
    {
        RectF frame = Frame;
        return IsNavigationPage && CanGoBack &&
               y >= frame.Y && y <= frame.Y + NavBarHeight &&
               x >= frame.X && x <= frame.X + NavBackWidth;
    }

    // Scroll support
    public bool IsScrollView { get; set; }

    private float _scrollOffsetX;
    private float _scrollOffsetY;

    /// <summary>
    /// Horizontal scroll offset. Writes feed the shared scroll physics so a fling can pick up
    /// the velocity of a drag; with the physics disabled the property is a plain assignment.
    /// </summary>
    public float ScrollOffsetX
    {
        get => _scrollOffsetX;
        set
        {
            if (value == _scrollOffsetX)
            {
                return;
            }
            float previous = _scrollOffsetX;
            _scrollOffsetX = value;
            OpenHarmonyScrollPhysics.OnOffsetChanged(this, horizontal: true, previous, value);
        }
    }

    /// <summary>Vertical scroll offset (see <see cref="ScrollOffsetX"/>).</summary>
    public float ScrollOffsetY
    {
        get => _scrollOffsetY;
        set
        {
            if (value == _scrollOffsetY)
            {
                return;
            }
            float previous = _scrollOffsetY;
            _scrollOffsetY = value;
            OpenHarmonyScrollPhysics.OnOffsetChanged(this, horizontal: false, previous, value);
        }
    }

    public float ScrollContentWidth { get; set; }
    public float ScrollContentHeight { get; set; }
    /// <summary>Invoked when the scroll offset changes (virtualized lists slide their window).</summary>
    public Action? ScrollOffsetChanged { get; set; }

    public List<OpenHarmonyView> Children { get; } = new();

    /// <summary>
    /// Views owned by the platform side (collection view items materialized from a template):
    /// the renderer draws and hit-tests them like ordinary children.
    /// </summary>
    public List<IView> ViewChildren { get; } = new();

    public RectF Frame => VirtualView?.Frame is Rect frame
        ? new RectF((float)frame.X, (float)frame.Y, (float)frame.Width, (float)frame.Height)
        : default;

    /// <summary>
    /// Draws the view's MAUI shadow (<see cref="IView.Shadow"/>) behind its content. The host
    /// canvas shadow layer takes an offset, a blur and one colour, so the view's frame is filled
    /// with a fully transparent brush (the geometry casts the shadow, the brush itself stays
    /// invisible); the effect is cleared before the view draws. A shadow the drawing cannot
    /// express (non-solid paint) is reported once instead of being dropped.
    /// </summary>
    public void DrawShadow(MauiCanvas canvas)
    {
        if (VirtualView?.Shadow is not { } shadow || shadow.Opacity <= 0 || shadow.Radius <= 0)
        {
            return;
        }
        Paint? paint = shadow.Paint;
        if (paint is PatternPaint { Pattern: PaintPattern wrapper })
        {
            paint = wrapper.Paint;
        }
        if (paint is not SolidPaint { Color: { } color })
        {
            OpenHarmonyStatus.Once("shadow.paint." + (paint?.GetType().Name ?? "null"),
                $"IShadow.Paint '{paint?.GetType().Name ?? "<null>"}' is not a solid colour: the " +
                "OpenHarmony shadow layer carries a single colour, so this shadow was not drawn");
            return;
        }
        RectF frame = Frame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        float alpha = Math.Clamp(shadow.Opacity, 0f, 1f) * color.Alpha *
            (float)Math.Clamp(VirtualView.Opacity, 0d, 1d);
        if (alpha <= 0)
        {
            return;
        }
        Color previousFill = canvas.FillColor;
        // FillColor resets the host effects; the shadow layer then applies to the next fill.
        canvas.FillColor = Colors.Transparent;
        canvas.SetShadow(new SizeF((float)shadow.Offset.X, (float)shadow.Offset.Y), shadow.Radius,
            color.WithAlpha(alpha));
        if (CornerRadius > 0)
        {
            canvas.FillRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
        }
        else
        {
            canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
        }
        // Never leak the shadow layer into the view's own fills/text.
        canvas.FillColor = previousFill;
        Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.ClearEffects();
    }

    public virtual void Draw(MauiCanvas canvas)
    {
        RectF frame = Frame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        if (Background is not null)
        {
            Color background = Background;
            canvas.FillColor = Pressed ? Colors.OrangeRed : (Dimmed ? background.WithAlpha(0.5f) : background);
            if (CornerRadius > 0)
            {
                canvas.FillRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
            }
            else
            {
                canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
            }
        }
        // Focus affordance for the self-drawn route: the PE2/OpenHarmonyFocusManager path marks
        // this platform view's IsFocused, so the outline is painted here (opt-out via
        // OpenHarmonyFocusRing.Enabled).
        OpenHarmonyFocusRing.Draw(canvas, this);
        if (IsNavigationPage)
        {
            DrawNavigationBar(canvas, frame);
            return;
        }
        if (IsShape || IsBorder)
        {
            DrawShape(canvas, frame);
            return;
        }
        if (IsFlyoutPage)
        {
            DrawFlyoutChrome(canvas, frame);
            return;
        }
        if (IsSwipeView && IsSwipeOpen)
        {
            DrawSwipePanel(canvas, frame);
        }
        if (IsRefreshView && IsRefreshing)
        {
            DrawRefreshIndicator(canvas, frame);
        }
        if (IsGraphicsView)
        {
            if (VirtualView is IGraphicsView graphics && graphics.Drawable is { } drawable)
            {
                drawable.Draw(canvas, new RectF(frame.X, frame.Y, frame.Width, frame.Height));
            }
            return;
        }
        if (IsIndicatorView)
        {
            DrawIndicatorView(canvas, frame);
            return;
        }
        if (IsPicker)
        {
            DrawPicker(canvas, frame);
            return;
        }
        if (IsTabbedPage)
        {
            if (ShowsTitleBar)
            {
                DrawTitleBar(canvas, frame);
            }
            DrawTabBar(canvas, frame);
            if (ShowsHamburger && !ShowsBack && !FlyoutOpen)
            {
                DrawHamburger(canvas, frame);
            }
            return;
        }
        if (IsStepper)
        {
            DrawStepper(canvas, frame);
            return;
        }
        if (IsRadioButton)
        {
            DrawRadioButton(canvas, frame);
            return;
        }
        if (IsCheckBox)
        {
            DrawCheckBox(canvas, frame);
            return;
        }
        if (IsSwitch)
        {
            DrawSwitch(canvas, frame);
            return;
        }
        if (IsSlider)
        {
            DrawSlider(canvas, frame);
            return;
        }
        if (IsProgressBar)
        {
            DrawProgress(canvas, frame);
            return;
        }
        if (IsActivityIndicator)
        {
            DrawActivityIndicator(canvas, frame);
            return;
        }
        if (StrokeColor is { } outline && StrokeThickness > 0)
        {
            canvas.StrokeColor = outline;
            canvas.StrokeSize = StrokeThickness;
            if (CornerRadius > 0)
            {
                canvas.DrawRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
            }
            else
            {
                canvas.DrawRectangle(frame.X, frame.Y, frame.Width, frame.Height);
            }
        }
        if (ImageBytes is { Length: > 0 })
        {
            DrawImage(canvas, frame);
            return;
        }
        string? text = Text;
        if (IsTextEntry && string.IsNullOrEmpty(text))
        {
            canvas.FontColor = Colors.Gray;
            canvas.FontSize = FontSize;
            canvas.DrawString(Placeholder ?? string.Empty, frame.X + 12, frame.Y, frame.Width - 24, frame.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center);
            if (IsFocused)
            {
                DrawCaret(canvas, frame, string.Empty);
            }
            return;
        }
        if (!string.IsNullOrEmpty(text))
        {
            canvas.FontColor = Dimmed ? TextColor.WithAlpha(0.5f) : TextColor;
            canvas.FontSize = FontSize;
            float padding = IsTextEntry ? 12f : (CornerRadius > 0 ? 24f : 0f);
            if (IsTextEntry && IsFocused)
            {
                DrawSelection(canvas, frame, text);
                DrawCaret(canvas, frame, text);
            }
            canvas.DrawString(Text, frame.X + padding, frame.Y, frame.Width - padding * 2, frame.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        if (IsScrollView)
        {
            // Auto-hiding vertical thumb, painted from the view's own pass (opt-out via
            // OpenHarmonyScrollbars.Enabled).
            OpenHarmonyScrollbars.Draw(canvas, this);
        }
        // Children are drawn by OpenHarmonyWindowRenderer walking the MAUI tree; the list here
        // is used for hit-testing.
    }

    private float[]? _charWidths;
    private string? _charWidthsText;
    private float _charWidthsFontSize;

    /// <summary>Per-character widths (cached per text/font size) for caret hit testing.</summary>
    private float[] CharWidths(string text)
    {
        if (_charWidths is not null && _charWidthsText == text && Math.Abs(_charWidthsFontSize - FontSize) < 0.01f)
        {
            return _charWidths;
        }
        var widths = new float[text.Length];
        float previous = 0;
        float accumulated = 0;
        for (int i = 0; i < text.Length; i++)
        {
            string prefix = text[..(i + 1)];
            float prefixWidth;
            if (Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.MeasureText(prefix, FontSize, out int measured, out int _) && measured > 0)
            {
                prefixWidth = measured;
            }
            else
            {
                prefixWidth = prefix.Length * FontSize * 0.55f;
            }
            widths[i] = Math.Max(0f, prefixWidth - previous);
            previous = prefixWidth;
            accumulated += widths[i];
        }
        _charWidths = widths;
        _charWidthsText = text;
        _charWidthsFontSize = FontSize;
        return widths;
    }

    /// <summary>Caret index for an x coordinate inside a text entry (midpoint hit testing).</summary>
    public int CursorIndexFromX(float x)
    {
        string text = Text ?? string.Empty;
        if (text.Length == 0)
        {
            return 0;
        }
        float left = Frame.X + 12;
        float relative = x - left;
        if (relative <= 0)
        {
            return 0;
        }
        float[] widths = CharWidths(text);
        float accumulated = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            if (relative < accumulated + widths[i] / 2f)
            {
                return i;
            }
            accumulated += widths[i];
        }
        return text.Length;
    }

    /// <summary>Draws the caret at the entry's cursor position (falls back to the text end).</summary>
    private void DrawCaret(MauiCanvas canvas, RectF frame, string text)
    {
        int caretIndex = CursorPosition >= 0 && CursorPosition <= text.Length ? CursorPosition : text.Length;
        float caretWidth = CaretWidth(text, caretIndex);
        canvas.FillColor = Colors.White;
        canvas.FillRectangle(frame.X + 12 + caretWidth, frame.Y + 8, 2, frame.Height - 16);
    }

    /// <summary>
    /// Width of the text before the caret from the cached per-character widths - the same array
    /// caret hit-testing uses, so the drawn caret and <see cref="CursorIndexFromX"/> always agree.
    /// <see cref="CharWidths"/> stores each glyph as the difference of two prefix measurements
    /// (and the estimate fallback is a per-character constant), so summing the prefix is exactly
    /// the whole-prefix measurement the previous implementation took per frame, without the
    /// substring allocation and the native call.
    /// </summary>
    private float CaretWidth(string text, int caretIndex)
    {
        if (caretIndex <= 0)
        {
            return 0;
        }
        if (caretIndex > text.Length)
        {
            caretIndex = text.Length;
        }
        float[] widths = CharWidths(text);
        float width = 0;
        for (int i = 0; i < caretIndex && i < widths.Length; i++)
        {
            width += widths[i];
        }
        return width;
    }

    private void DrawSelection(MauiCanvas canvas, RectF frame, string text)
    {
        if (SelectionLength <= 0)
        {
            return;
        }
        int start = Math.Clamp(Math.Min(CursorPosition, CursorPosition - SelectionLength), 0, text.Length);
        int end = Math.Clamp(Math.Max(CursorPosition, CursorPosition - SelectionLength), 0, text.Length);
        float left = frame.X + 12;
        float right = left + CaretWidth(text, text.Length);
        float startX = left + (text.Length > 0 ? (right - left) * start / text.Length : 0);
        float endX = left + (text.Length > 0 ? (right - left) * end / text.Length : 0);
        if (endX > startX)
        {
            canvas.FillColor = Colors.DodgerBlue.WithAlpha(0.4f);
            canvas.FillRectangle(startX, frame.Y + 8, endX - startX, frame.Height - 16);
        }
    }

    private void DrawImage(MauiCanvas canvas, RectF frame)
    {
        float imageWidth = frame.Width;
        float imageHeight = frame.Height;
        float x = frame.X;
        float y = frame.Y;
        // Aspect fitting is limited to what the platform view knows (intrinsic size is not
        // tracked yet): AspectFill/Center keep the frame, Fit preserves the square case.
        if (ImageAspect == Aspect.AspectFit && Math.Abs(imageWidth - imageHeight) > 0.5f)
        {
            float side = Math.Min(imageWidth, imageHeight);
            x += (imageWidth - side) / 2f;
            y += (imageHeight - side) / 2f;
            imageWidth = imageHeight = side;
        }
        var destination = new RectF(x, y, imageWidth, imageHeight);
        ImageDrawn?.Invoke(destination);
        Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytes(
            ImageBytes!, (int)x, (int)y, (int)imageWidth, (int)imageHeight);
    }

    private void DrawFlyoutChrome(MauiCanvas canvas, RectF frame)
    {
        if (ShowsHamburger)
        {
            DrawHamburger(canvas, frame);
            return;
        }
        if (!FlyoutPresented)
        {
            // Hamburger button in the top-left corner of the detail.
            canvas.FillColor = Colors.Black;
            canvas.FillRoundedRectangle(frame.X + 8, frame.Y + 8, 28, 28, 4);
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 2;
            for (int line = 0; line < 3; line++)
            {
                float lineY = frame.Y + 15 + line * 7;
                canvas.DrawLine(frame.X + 13, lineY, frame.X + 31, lineY);
            }
            return;
        }
        float width = Math.Min(FlyoutWidth, frame.Width);
        canvas.FillColor = Colors.DimGray;
        canvas.FillRectangle(frame.X, frame.Y, width, frame.Height);
        canvas.StrokeColor = Colors.Gray;
        canvas.StrokeSize = 1;
        canvas.DrawLine(frame.X + width, frame.Y, frame.X + width, frame.Y + frame.Height);
    }

    private void DrawHamburger(MauiCanvas canvas, RectF frame)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRoundedRectangle(frame.X + 8, frame.Y + 8, 28, 28, 4);
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 2;
        for (int line = 0; line < 3; line++)
        {
            float lineY = frame.Y + 15 + line * 7;
            canvas.DrawLine(frame.X + 13, lineY, frame.X + 31, lineY);
        }
    }

    private void DrawIndicatorView(MauiCanvas canvas, RectF frame)
    {
        if (IndicatorCount <= 1)
        {
            return;
        }
        float spacing = Math.Max(DotsSize * 2.5f, 12f);
        float startX = frame.X + (frame.Width - IndicatorCount * spacing) / 2f + spacing / 2f;
        float y = frame.Y + frame.Height / 2f;
        for (int i = 0; i < IndicatorCount; i++)
        {
            bool active = i == IndicatorPosition;
            canvas.FillColor = active ? SelectedDotsColor : DotsColor;
            canvas.FillCircle(startX + i * spacing, y, active ? DotsSize * 1.2f : DotsSize);
        }
    }

    private void DrawPicker(MauiCanvas canvas, RectF frame)
    {
        canvas.FontColor = TextColor;
        canvas.FontSize = FontSize;
        string text = Text ?? string.Empty;
        canvas.DrawString(text, frame.X + 12, frame.Y, frame.Width - 44, frame.Height,
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.StrokeColor = TextColor;
        canvas.StrokeSize = 2;
        float cx = frame.X + frame.Width - 20;
        float cy = frame.Y + frame.Height / 2f;
        canvas.DrawLine(cx - 8, cy - 4, cx, cy + 5);
        canvas.DrawLine(cx, cy + 5, cx + 8, cy - 4);
    }

    /// <summary>Draws the open dropdown on top of everything else.</summary>
    public void DrawPopup(MauiCanvas canvas)
    {
        RectF frame = Frame;
        if (!PopupVisible || PopupItems.Count == 0)
        {
            return;
        }
        float width = Math.Max(frame.Width, 220f);
        float height = PopupItems.Count * PopupRowHeight;
        float x = frame.X;
        float y = frame.Y + frame.Height;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(x, y, width, height);
        canvas.StrokeColor = Colors.Gray;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(x, y, width, height);
        canvas.FontSize = FontSize;
        // Only rows the surface can show are drawn. A long dropdown (a picker with hundreds of
        // items) otherwise pays one native text draw per item on every frame, and the canvas
        // clips every row below the surface anyway, so the pixels are unchanged.
        int lastRow = PopupItems.Count;
        if (SurfaceViewportHeight > 0)
        {
            lastRow = (int)Math.Ceiling((SurfaceViewportHeight - y) / PopupRowHeight);
            lastRow = Math.Clamp(lastRow, 0, PopupItems.Count);
        }
        for (int i = 0; i < lastRow; i++)
        {
            float rowY = y + i * PopupRowHeight;
            canvas.FontColor = i == PopupSelectedIndex ? Colors.DodgerBlue : Colors.White;
            canvas.DrawString(PopupItems[i], x + 16, rowY, width - 32, PopupRowHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    /// <summary>
    /// Size of the surface the compositor last drew into. The popup and carousel indicator draws
    /// use it to skip rows/dots the canvas clips; 0 means unknown and draws everything.
    /// </summary>
    internal static int SurfaceViewportWidth { get; private set; }

    internal static int SurfaceViewportHeight { get; private set; }

    internal static void SetSurfaceViewport(int width, int height)
    {
        SurfaceViewportWidth = width;
        SurfaceViewportHeight = height;
    }

    /// <summary>Index of the dropdown row at the point (-1 when outside the popup).</summary>
    public int PopupIndexAt(float x, float y)
    {
        RectF frame = Frame;
        if (!PopupVisible)
        {
            return -1;
        }
        float width = Math.Max(frame.Width, 220f);
        float top = frame.Y + frame.Height;
        if (x < frame.X || x > frame.X + width || y < top)
        {
            return -1;
        }
        int index = (int)((y - top) / PopupRowHeight);
        return index >= 0 && index < PopupItems.Count ? index : -1;
    }

    private void DrawTitleBar(MauiCanvas canvas, RectF frame)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(frame.X, frame.Y, frame.Width, TitleBarHeight);
        if (ShowsBack)
        {
            float cx = frame.X + 26f;
            float cy = frame.Y + TitleBarHeight / 2f;
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 3;
            canvas.DrawLine(cx + 9, cy - 11, cx - 4, cy);
            canvas.DrawLine(cx - 4, cy, cx + 9, cy + 11);
        }
        canvas.FontColor = Colors.White;
        canvas.FontSize = 28;
        canvas.DrawString(TitleText ?? string.Empty, frame.X + 56, frame.Y, frame.Width - 112, TitleBarHeight,
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private void DrawTabBar(MauiCanvas canvas, RectF frame)
    {
        float barY = frame.Y + frame.Height - TabBarHeight;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(frame.X, barY, frame.Width, TabBarHeight);
        if (!TabTitlesVisible || TabTitles.Count == 0)
        {
            return;
        }
        float tabWidth = frame.Width / TabTitles.Count;
        canvas.FontSize = FontSize;
        for (int i = 0; i < TabTitles.Count; i++)
        {
            float tabX = frame.X + i * tabWidth;
            bool active = i == SelectedTab;
            if (i < TabIcons.Count && TabIcons[i] is { Length: > 0 } icon)
            {
                // Icon above a smaller caption, like the platform tab bars.
                const float iconSize = 24f;
                int width = (int)Math.Min(iconSize, tabWidth - 8);
                int height = width;
                Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytes(
                    icon, (int)(tabX + (tabWidth - width) / 2f), (int)(barY + 6), width, height);
                canvas.FontColor = active ? Colors.DodgerBlue : Colors.Gray;
                canvas.FontSize = 18;
                canvas.DrawString(TabTitles[i], tabX, barY + 6 + iconSize, tabWidth, TabBarHeight - iconSize - 8,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
                canvas.FontSize = FontSize;
                continue;
            }
            canvas.FontColor = active ? Colors.DodgerBlue : Colors.Gray;
            canvas.DrawString(TabTitles[i], tabX, barY, tabWidth, TabBarHeight,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    /// <summary>Tab index at the point (-1 when outside the tab bar).</summary>
    public int TabIndexAt(float x, float y)
    {
        RectF frame = Frame;
        if (!IsTabbedPage || TabTitles.Count == 0)
        {
            return -1;
        }
        if (y < frame.Y + frame.Height - TabBarHeight || x < frame.X || x > frame.X + frame.Width)
        {
            return -1;
        }
        float tabWidth = frame.Width / TabTitles.Count;
        int index = (int)((x - frame.X) / tabWidth);
        return index >= 0 && index < TabTitles.Count ? index : -1;
    }

    /// <summary>Fills/strokes a shape (IShape.PathForBounds) inside the frame.</summary>
    private void DrawShape(MauiCanvas canvas, RectF frame)
    {
        // MAUI's Shape.PathForBounds returns geometry in the shape's own coordinate space
        // (starting near 0,0), so the canvas is translated to the view's frame instead.
        Microsoft.Maui.Graphics.PathF? path = Shape?.PathForBounds(new RectF(0, 0, frame.Width, frame.Height));
        if (path is null)
        {
            return;
        }
        canvas.SaveState();
        canvas.Translate(frame.X, frame.Y);
        Color? fill = (ShapeFill as SolidPaint)?.Color;
        if (fill is not null)
        {
            canvas.FillColor = fill;
            canvas.FillPath(path);
        }
        Color stroke = IsBorder ? BorderStroke : ShapeStroke;
        float thickness = IsBorder ? BorderStrokeThickness : ShapeStrokeThickness;
        if (thickness > 0 && stroke.Alpha > 0)
        {
            canvas.StrokeColor = stroke;
            canvas.StrokeSize = thickness;
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    private void DrawStepper(MauiCanvas canvas, RectF frame)
    {
        float height = Math.Min(frame.Height, 32f);
        float y = frame.Y + (frame.Height - height) / 2f;
        canvas.FillColor = Background ?? Colors.DimGray;
        canvas.FillRoundedRectangle(frame.X, y, frame.Width, height, 6);
        canvas.FontColor = TextColor;
        canvas.FontSize = 26;
        canvas.DrawString("-", frame.X, y, frame.Width / 2, height, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.DrawString("+", frame.X + frame.Width / 2, y, frame.Width / 2, height, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.DrawString($"{StepperValue:0.##}", frame.X, frame.Y - 22, frame.Width, 20, HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private void DrawRadioButton(MauiCanvas canvas, RectF frame)
    {
        float side = Math.Min(Math.Min(frame.Width, frame.Height), 26f);
        float cx = frame.X + side / 2 + 2;
        float cy = frame.Y + frame.Height / 2f;
        canvas.StrokeColor = RadioColor;
        canvas.StrokeSize = 2;
        canvas.DrawCircle(cx, cy, side / 2);
        if (RadioChecked)
        {
            canvas.FillColor = RadioColor;
            canvas.FillCircle(cx, cy, side / 2 - 4);
        }
        if (!string.IsNullOrEmpty(Text))
        {
            canvas.FontColor = TextColor;
            canvas.FontSize = FontSize;
            canvas.DrawString(Text, cx + side, frame.Y, frame.Width - side - 4, frame.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    private void DrawRefreshIndicator(MauiCanvas canvas, RectF frame)
    {
        float cx = frame.Center.X;
        float cy = frame.Y + 20;
        canvas.StrokeColor = RefreshColor;
        canvas.StrokeSize = 4;
        canvas.DrawArc(cx - 14, cy - 14, 28, 28, 20, 300, false, false);
    }

    private void DrawSwipePanel(MauiCanvas canvas, RectF frame)
    {
        canvas.FontSize = 24;
        for (int i = 0; i < SwipeItems.Count; i++)
        {
            RectF item = SwipeItemRect(i);
            canvas.FillColor = SwipeItems[i].Background;
            canvas.FillRectangle(item.X, item.Y, item.Width, item.Height);
            canvas.FontColor = Colors.White;
            canvas.DrawString(SwipeItems[i].Text, item.X, item.Y, item.Width, item.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    private void DrawNavigationBar(MauiCanvas canvas, RectF frame)
    {
        canvas.FillColor = NavBarColor;
        canvas.FillRectangle(frame.X, frame.Y, frame.Width, NavBarHeight);
        if (CanGoBack)
        {
            float cx = frame.X + 26f;
            float cy = frame.Y + NavBarHeight / 2f;
            canvas.StrokeColor = NavBarTextColor;
            canvas.StrokeSize = 3;
            canvas.DrawLine(cx + 9, cy - 11, cx - 4, cy);
            canvas.DrawLine(cx - 4, cy, cx + 9, cy + 11);
        }
        canvas.FontColor = NavBarTextColor;
        canvas.FontSize = 30;
        canvas.DrawString(NavTitle ?? string.Empty, frame.X + NavBackWidth, frame.Y,
            frame.Width - NavBackWidth * 2, NavBarHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.FontSize = 26;
        for (int i = 0; i < ToolbarItems.Count; i++)
        {
            RectF item = ToolbarItemRect(i);
            canvas.DrawString(ToolbarItems[i].Text, item.X, item.Y, item.Width, item.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        // The current page is drawn by the renderer, translated below the bar.
    }

    private void DrawCheckBox(MauiCanvas canvas, RectF frame)
    {
        float side = Math.Min(frame.Width, frame.Height) * 0.7f;
        float x = frame.X + (frame.Width - side) / 2f;
        float y = frame.Y + (frame.Height - side) / 2f;
        canvas.StrokeColor = CheckBoxColor;
        canvas.StrokeSize = 2;
        canvas.DrawRoundedRectangle(x, y, side, side, 4);
        if (IsChecked)
        {
            canvas.StrokeColor = CheckBoxColor;
            canvas.StrokeSize = 3;
            canvas.DrawLine(x + side * 0.25f, y + side * 0.55f, x + side * 0.45f, y + side * 0.75f);
            canvas.DrawLine(x + side * 0.45f, y + side * 0.75f, x + side * 0.78f, y + side * 0.28f);
        }
    }

    private void DrawSwitch(MauiCanvas canvas, RectF frame)
    {
        float height = Math.Min(frame.Height, 28f);
        float width = Math.Min(frame.Width, height * 1.9f);
        float x = frame.X + (frame.Width - width) / 2f;
        float y = frame.Y + (frame.Height - height) / 2f;
        float radius = height / 2f;
        canvas.FillColor = IsOn ? SliderMinimumTrackColor : SwitchTrackColor;
        canvas.FillRoundedRectangle(x, y, width, height, radius);
        float thumbRadius = radius - 3f;
        float thumbX = IsOn ? x + width - radius : x + radius;
        canvas.FillColor = SwitchThumbColor;
        canvas.FillCircle(thumbX, y + radius, thumbRadius);
    }

    private void DrawSlider(MauiCanvas canvas, RectF frame)
    {
        float thickness = 6f;
        float cy = frame.Y + frame.Height / 2f;
        float left = frame.X + 10f;
        float right = frame.X + frame.Width - 10f;
        double span = SliderMaximum - SliderMinimum;
        float fraction = span > 0 ? (float)Math.Clamp((SliderValue - SliderMinimum) / span, 0, 1) : 0f;
        SliderFraction = fraction;
        float thumbX = left + (right - left) * fraction;
        canvas.FillColor = SliderMaximumTrackColor;
        canvas.FillRoundedRectangle(left, cy - thickness / 2f, right - left, thickness, thickness / 2f);
        canvas.FillColor = SliderMinimumTrackColor;
        canvas.FillRoundedRectangle(left, cy - thickness / 2f, Math.Max(0f, thumbX - left), thickness, thickness / 2f);
        canvas.FillColor = SliderThumbColor;
        canvas.FillCircle(thumbX, cy, 11f);
    }

    private void DrawProgress(MauiCanvas canvas, RectF frame)
    {
        float thickness = Math.Min(frame.Height, 8f);
        float y = frame.Y + (frame.Height - thickness) / 2f;
        canvas.FillColor = Colors.DimGray;
        canvas.FillRoundedRectangle(frame.X, y, frame.Width, thickness, thickness / 2f);
        float filled = (float)(frame.Width * Math.Clamp(Progress, 0, 1));
        if (filled > 0)
        {
            canvas.FillColor = ProgressColor;
            canvas.FillRoundedRectangle(frame.X, y, filled, thickness, thickness / 2f);
        }
    }

    private void DrawActivityIndicator(MauiCanvas canvas, RectF frame)
    {
        float radius = Math.Min(frame.Width, frame.Height) / 2f - 2f;
        float cx = frame.X + frame.Width / 2f;
        float cy = frame.Y + frame.Height / 2f;
        canvas.StrokeColor = IndicatorColor;
        canvas.StrokeSize = 3;
        // A 90-degree arc rotating with the renderer's shared angle: motion without a timer.
        canvas.DrawArc(cx - radius, cy - radius, radius * 2, radius * 2, AnimationAngle, AnimationAngle + 90, false, false);
    }

    public bool HitTest(float x, float y)
    {
        RectF frame = Frame;
        return x >= frame.X && x <= frame.X + frame.Width && y >= frame.Y && y <= frame.Y + frame.Height;
    }

    /// <summary>Updates the slider value from a touch x inside the view (returns the fraction).</summary>
    public float SliderValueFromX(float x)
    {
        RectF frame = Frame;
        float left = frame.X + 10f;
        float right = frame.X + frame.Width - 10f;
        float fraction = right > left ? Math.Clamp((x - left) / (right - left), 0f, 1f) : 0f;
        SliderFraction = fraction;
        return fraction;
    }

    /// <summary>Routes a tap through this view and its children (children first).</summary>
    public bool OnTouch(bool down, bool up, float x, float y)
    {
        bool handled = false;
        foreach (OpenHarmonyView child in Children)
        {
            handled |= child.OnTouch(down, up, x, y);
        }
        if (IsTabbedPage)
        {
            int tab = TabIndexAt(x, y);
            if (down && tab >= 0)
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (tab >= 0)
                {
                    TabSelected?.Invoke(tab);
                }
                return true;
            }
        }
        if (IsGraphicsView)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    GraphicsTap?.Invoke(new PointF(x, y));
                }
                return true;
            }
        }
        if (IsPicker)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    Tap?.Invoke();
                }
                return true;
            }
        }
        if (IsStepper)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    bool increment = x >= Frame.X + Frame.Width / 2;
                    StepperStep?.Invoke(increment);
                }
                return true;
            }
        }
        if (IsRadioButton)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    Tap?.Invoke();
                }
                return true;
            }
        }
        if (IsNavigationPage)
        {
            if (down && (InBackRegion(x, y) || ToolbarItemAt(x, y) >= 0))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                int toolbarIndex = ToolbarItemAt(x, y);
                if (toolbarIndex >= 0)
                {
                    ToolbarItems[toolbarIndex].Activate();
                }
                else if (InBackRegion(x, y))
                {
                    BackTapped?.Invoke();
                }
                return true;
            }
        }
        if (IsSwipeView && IsSwipeOpen)
        {
            if (down && SwipeItemAt(x, y) >= 0)
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                int swipeIndex = SwipeItemAt(x, y);
                if (swipeIndex >= 0)
                {
                    (string Text, Color Background, Action Activate) item = SwipeItems[swipeIndex];
                    SetSwipeOpen(false);
                    item.Activate();
                }
                return true;
            }
        }
        if (Tap is null)
        {
            return handled || Pressed;
        }
        if (down && HitTest(x, y))
        {
            Pressed = true;
            return true;
        }
        if (up && Pressed)
        {
            Pressed = false;
            if (HitTest(x, y))
            {
                Tap();
            }
            return true;
        }
        return handled || Pressed;
    }
}

/// <summary>
/// MAUI maps <see cref="IView.Shadow"/> through the shared ViewMapper; the slice has no other
/// sink for it. This replaces the entry once so a shadow change requests a frame. The drawing
/// itself happens in <see cref="OpenHarmonyWindowRenderer"/> through
/// <see cref="OpenHarmonyView.DrawShadow"/>, which reads the virtual view's shadow directly.
/// </summary>
internal static class OpenHarmonyShadow
{
    private static bool s_installed;

    /// <summary>Installs the redraw hook (idempotent; called by the renderer's constructor).</summary>
    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        if (ViewHandler.ViewMapper is PropertyMapper<IView, IViewHandler> mapper)
        {
            Action<IViewHandler, IView>? previous = mapper[nameof(IView.Shadow)];
            mapper[nameof(IView.Shadow)] = (handler, view) =>
            {
                try
                {
                    previous?.Invoke(handler, view);
                }
                catch (Exception)
                {
                    // The default rc.1 mapping has no OpenHarmony platform view contract; ignore.
                }
                try
                {
                    Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
                }
                catch (Exception)
                {
                    // No host: the next input/frame event repaints anyway.
                }
            };
        }
    }
}
