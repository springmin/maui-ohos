// Platform view for the OpenHarmony compositor: a lightweight drawing element. Handlers set
// its properties from the virtual view; OpenHarmonyWindowRenderer draws and hit-tests it.
using Microsoft.Maui.Graphics;
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

    // Activity indicator support
    public bool IsActivityIndicator { get; set; }
    public bool IsRunning { get; set; }
    public Color IndicatorColor { get; set; } = Colors.White;

    /// <summary>Shared rotation (degrees) advanced by the renderer for animated views.</summary>
    public static float AnimationAngle { get; set; }

    /// <summary>True when this view keeps redrawing on its own (activity indicators).</summary>
    public bool NeedsAnimation => IsActivityIndicator && IsRunning;

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
        return !FlyoutPresented && x >= frame.X && x <= frame.X + HamburgerSize &&
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

    public bool InBackRegion(float x, float y)
    {
        RectF frame = Frame;
        return IsNavigationPage && CanGoBack &&
               y >= frame.Y && y <= frame.Y + NavBarHeight &&
               x >= frame.X && x <= frame.X + NavBackWidth;
    }

    // Scroll support
    public bool IsScrollView { get; set; }
    public float ScrollOffsetX { get; set; }
    public float ScrollOffsetY { get; set; }
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

    public virtual void Draw(MauiCanvas canvas)
    {
        RectF frame = Frame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        if (Background is not null)
        {
            canvas.FillColor = Pressed ? Colors.OrangeRed : Background;
            if (CornerRadius > 0)
            {
                canvas.FillRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
            }
            else
            {
                canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
            }
        }
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
        if (IsPicker)
        {
            DrawPicker(canvas, frame);
            return;
        }
        if (IsTabbedPage)
        {
            DrawTabBar(canvas, frame);
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
            canvas.FontColor = TextColor;
            canvas.FontSize = FontSize;
            float padding = IsTextEntry ? 12f : (CornerRadius > 0 ? 24f : 0f);
            if (IsTextEntry && IsFocused)
            {
                DrawCaret(canvas, frame, text);
            }
            canvas.DrawString(Text, frame.X + padding, frame.Y, frame.Width - padding * 2, frame.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        // Children are drawn by OpenHarmonyWindowRenderer walking the MAUI tree; the list here
        // is used for hit-testing.
    }

    /// <summary>Draws the caret at the entry's cursor position (falls back to the text end).</summary>
    private void DrawCaret(MauiCanvas canvas, RectF frame, string text)
    {
        int caretIndex = CursorPosition >= 0 && CursorPosition <= text.Length ? CursorPosition : text.Length;
        float caretWidth = 0;
        if (caretIndex > 0)
        {
            string prefix = text[..caretIndex];
            if (Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.MeasureText(prefix, FontSize, out int measured, out int _) && measured > 0)
            {
                caretWidth = measured;
            }
            else
            {
                caretWidth = prefix.Length * FontSize * 0.55f;
            }
        }
        canvas.FillColor = Colors.White;
        canvas.FillRectangle(frame.X + 12 + caretWidth, frame.Y + 8, 2, frame.Height - 16);
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
        Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytes(
            ImageBytes!, (int)x, (int)y, (int)imageWidth, (int)imageHeight);
    }

    private void DrawFlyoutChrome(MauiCanvas canvas, RectF frame)
    {
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
        for (int i = 0; i < PopupItems.Count; i++)
        {
            float rowY = y + i * PopupRowHeight;
            canvas.FontColor = i == PopupSelectedIndex ? Colors.DodgerBlue : Colors.White;
            canvas.DrawString(PopupItems[i], x + 16, rowY, width - 32, PopupRowHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
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

    private void DrawTabBar(MauiCanvas canvas, RectF frame)
    {
        float barY = frame.Y + frame.Height - TabBarHeight;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(frame.X, barY, frame.Width, TabBarHeight);
        if (TabTitles.Count == 0)
        {
            return;
        }
        float tabWidth = frame.Width / TabTitles.Count;
        canvas.FontSize = FontSize;
        for (int i = 0; i < TabTitles.Count; i++)
        {
            canvas.FontColor = i == SelectedTab ? Colors.DodgerBlue : Colors.Gray;
            canvas.DrawString(TabTitles[i], frame.X + i * tabWidth, barY, tabWidth, TabBarHeight,
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
        Microsoft.Maui.Graphics.PathF? path = Shape?.PathForBounds(new RectF(frame.X, frame.Y, frame.Width, frame.Height));
        if (path is null)
        {
            return;
        }
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
            if (down && InBackRegion(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (InBackRegion(x, y))
                {
                    BackTapped?.Invoke();
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
