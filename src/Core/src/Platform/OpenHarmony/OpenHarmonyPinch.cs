// Pinch gestures for OpenHarmony: the shell reports a phase, a scale factor and the current centre,
// and this dispatches them to the view's PinchGestureRecognizer through the public
// IPinchGestureController interface (SendPinchStarted / SendPinch / SendPinchEnded).
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyPinch
{
    /// <summary>Phases reported by the shell: 0 = started, 1 = running, 2 = completed.</summary>
    public static bool HasPinch(IView view)
    {
        // Indexed scan, not OfType().Any(): the pinch walk asks this once per node.
        if (view is not View controlsView)
        {
            return false;
        }
        IList<IGestureRecognizer> recognizers = controlsView.GestureRecognizers;
        for (int i = 0; i < recognizers.Count; i++)
        {
            if (recognizers[i] is PinchGestureRecognizer)
            {
                return true;
            }
        }
        return false;
    }

    public static bool Dispatch(IView view, int phase, double scale, float x, float y)
    {
        if (view is not View controlsView)
        {
            return false;
        }
        bool handled = false;
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is not PinchGestureRecognizer pinch)
            {
                continue;
            }
            var controller = (IPinchGestureController)pinch;
            var point = new Point(x, y);
            if (phase <= 0)
            {
                controller.SendPinchStarted(controlsView, point);
            }
            else if (phase >= 2)
            {
                controller.SendPinchEnded(controlsView);
            }
            else
            {
                controller.SendPinch(controlsView, scale, point);
            }
            handled = true;
        }
        return handled;
    }
}
