// Gesture dispatch for the platform slice: MAUI's platform gesture managers are not part of
// this slice, so the renderer hands taps/pans to the recognizers directly.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyGestures
{
    private static int s_panGestureId;

    /// <summary>Invokes tap recognizers of a view (returns true when one handled the tap).</summary>
    public static bool SendTap(IView view, float x, float y)
    {
        if (view is not View controlsView)
        {
            return false;
        }
        bool handled = false;
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is TapGestureRecognizer tap)
            {
                tap.SendTapped(controlsView, _ => new Point(x, y));
                handled = true;
            }
        }
        return handled;
    }

    /// <summary>Starts pan tracking for a view (returns a gesture id, or -1 when it has no pan).</summary>
    public static int StartPan(IView view, float x, float y)
    {
        if (view is not View controlsView || !HasPan(controlsView))
        {
            return -1;
        }
        int id = ++s_panGestureId;
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is PanGestureRecognizer pan)
            {
                ((IPanGestureController)pan).SendPanStarted(controlsView, id);
            }
        }
        return id;
    }

    public static bool SendPan(IView view, float totalX, float totalY, int id)
    {
        if (view is not View controlsView || id < 0)
        {
            return false;
        }
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is PanGestureRecognizer pan)
            {
                ((IPanGestureController)pan).SendPan(controlsView, totalX, totalY, id);
            }
        }
        return true;
    }

    public static void CompletePan(IView view, int id)
    {
        if (view is not View controlsView || id < 0)
        {
            return;
        }
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is PanGestureRecognizer pan)
            {
                ((IPanGestureController)pan).SendPanCompleted(controlsView, id);
            }
        }
    }

    /// <summary>Dispatches a completed swipe to the view's SwipeGestureRecognizers.</summary>
    /// <remarks>
    /// MAUI's recognizer contract is two phase: <c>SendSwipe</c> only records the travelled
    /// deltas, and <c>DetectSwipe</c> evaluates them against the threshold and raises the
    /// Swiped event (also executing the command).
    /// </remarks>
    public static bool SendSwiped(IView view, float totalX, float totalY)
    {
        if (view is not View controlsView)
        {
            return false;
        }
        SwipeDirection direction;
        if (Math.Abs(totalX) >= Math.Abs(totalY))
        {
            direction = totalX < 0 ? SwipeDirection.Left : SwipeDirection.Right;
        }
        else
        {
            direction = totalY < 0 ? SwipeDirection.Up : SwipeDirection.Down;
        }
        bool handled = false;
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is SwipeGestureRecognizer swipe)
            {
                if ((swipe.Direction & direction) == 0)
                {
                    continue;
                }
                var controller = (ISwipeGestureController)swipe;
                controller.SendSwipe(controlsView, totalX, totalY);
                handled |= controller.DetectSwipe(controlsView, direction);
            }
        }
        return handled;
    }

    public static bool HasSwipe(IView view)
        => view is View controlsView && controlsView.GestureRecognizers.OfType<SwipeGestureRecognizer>().Any();

    public static bool HasPan(IView view)
        => view is View controlsView && controlsView.GestureRecognizers.OfType<PanGestureRecognizer>().Any();

    public static bool HasGestures(IView view)
        => view is View controlsView && controlsView.GestureRecognizers.Count > 0;
}
