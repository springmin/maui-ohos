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

    public static bool HasPan(IView view)
        => view is View controlsView && controlsView.GestureRecognizers.OfType<PanGestureRecognizer>().Any();

    public static bool HasGestures(IView view)
        => view is View controlsView && controlsView.GestureRecognizers.Count > 0;
}
