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

    public bool HitTest(float x, float y)
    {
        RectF frame = Frame;
        return x >= frame.X && x <= frame.X + frame.Width && y >= frame.Y && y <= frame.Y + frame.Height;
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
