// Alert overlay state: the alert manager stores what should be shown and the renderer draws it
// (scrim + dialog box + buttons) and routes button taps back through the completion callbacks.
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal sealed class OpenHarmonyAlertState
{
    public required string? Title { get; init; }
    public required string? Message { get; init; }
    public required string? Accept { get; init; }
    public required string? Cancel { get; init; }
    public required Action<bool> Complete { get; init; }
}

internal static class OpenHarmonyAlertHost
{
    public static OpenHarmonyAlertState? Current { get; private set; }

    public static double Width { get; private set; }
    public static double Height { get; private set; }

    public static bool IsVisible => Current is not null;

    public static event Action? Changed;

    public static void Show(OpenHarmonyAlertState state)
    {
        Current = state;
        Changed?.Invoke();
    }

    public static void Hide()
    {
        Current = null;
        Changed?.Invoke();
    }

    public static void SetSurface(double width, double height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>Dialog geometry (shared by drawing and hit testing).</summary>
    public static RectF BoxRect
    {
        get
        {
            float width = (float)Math.Min(640, Math.Max(320, Width - 160));
            float height = 260;
            return new RectF((float)((Width - width) / 2), (float)((Height - height) / 2), width, height);
        }
    }

    public static RectF AcceptRect
    {
        get
        {
            RectF box = BoxRect;
            float buttonWidth = 160;
            return new RectF(box.Right - buttonWidth - 16, box.Bottom - 64, buttonWidth, 48);
        }
    }

    public static RectF CancelRect
    {
        get
        {
            RectF box = BoxRect;
            RectF accept = AcceptRect;
            return new RectF(accept.X - 176, accept.Y, 160, 48);
        }
    }
}
