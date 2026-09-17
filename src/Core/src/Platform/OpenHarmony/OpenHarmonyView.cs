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

    // Scroll support
    public bool IsScrollView { get; set; }
    public float ScrollOffsetX { get; set; }
    public float ScrollOffsetY { get; set; }
    public float ScrollContentWidth { get; set; }
    public float ScrollContentHeight { get; set; }

    public List<OpenHarmonyView> Children { get; } = new();

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
                canvas.FillColor = Colors.White;
                canvas.FillRectangle(frame.X + 12, frame.Y + 8, 2, frame.Height - 16);
            }
            return;
        }
        if (!string.IsNullOrEmpty(text))
        {
            canvas.FontColor = TextColor;
            canvas.FontSize = FontSize;
            float padding = IsTextEntry ? 12f : (CornerRadius > 0 ? 24f : 0f);
            canvas.DrawString(Text, frame.X + padding, frame.Y, frame.Width - padding * 2, frame.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        // Children are drawn by OpenHarmonyWindowRenderer walking the MAUI tree; the list here
        // is used for hit-testing.
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
        if (Tap is null)
        {
            return handled;
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
