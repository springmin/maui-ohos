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

    /// <summary>
    /// SEC-SCAN-5c E: the scale bounds a running pinch report is clamped to. The shell computes
    /// <c>scale = distance / startDistance</c> from the platform touch points (host_napi.cpp
    /// ComputePinch); a real two-finger gesture on a display is far inside these bounds, so the
    /// clamp never changes a normal pinch.
    /// </summary>
    internal const double MinScale = 1e-3;   // 1000x zoom out
    internal const double MaxScale = 1e3;    // 1000x zoom in

    /// <summary>
    /// SEC-SCAN-5c E: validates one pinch report before it can reach an app recognizer.
    /// Non-finite values are rejected where the report consumes them (a NaN running scale
    /// poisons app zoom state permanently through <c>Scale *= e.Scale</c>); a non-positive or
    /// extreme running scale - values the shell's two-point distance cannot produce - is
    /// rejected or clamped to <see cref="MinScale"/>/<see cref="MaxScale"/>. The completed
    /// phase carries a placeholder centre and scale that the dispatch never reads, so it is
    /// always delivered (dropping it would leave a recognizer stuck mid-pinch). Returns false
    /// when the report must be dropped.
    /// </summary>
    public static bool TryNormalize(int phase, double scale, float x, float y, out double normalizedScale)
    {
        normalizedScale = scale;
        if (phase >= 2)
        {
            return true;
        }
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            // The centre steers the managed hit test and the recognizer's point on the
            // started/running phases; a non-finite one can only produce a bogus target.
            return false;
        }
        if (phase == 1)
        {
            // A running scale is a positive distance ratio; NaN/Inf, zero or a negative cannot
            // come from the shell's sqrt() distance and would make zoom arithmetic degenerate.
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return false;
            }
            if (scale < MinScale)
            {
                normalizedScale = MinScale;
            }
            else if (scale > MaxScale)
            {
                normalizedScale = MaxScale;
            }
        }
        return true;
    }

    public static bool Dispatch(IView view, int phase, double scale, float x, float y)
    {
        if (view is not View controlsView || !TryNormalize(phase, scale, x, y, out double normalizedScale))
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
                controller.SendPinch(controlsView, normalizedScale, point);
            }
            handled = true;
        }
        return handled;
    }
}
