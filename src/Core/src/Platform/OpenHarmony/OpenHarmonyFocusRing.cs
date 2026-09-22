// Focus ring for the OpenHarmony compositor.
//
// When the PE2/OpenHarmonyFocusManager path (or a text handler) marks a platform view focused
// - OpenHarmonyView.IsFocused - the view paints a visible outline around its frame in its Draw
// pass. This is the only focus affordance the self-drawn route has; ArkUI does not know about
// the individual virtual views, so the ring must come from the compositor.
//
// Style: FocusRingThickness px of FocusRingColor, inset by half the thickness so the whole
// outline stays inside the view's frame, following the view's corner radius when it has one.
// The colour is an internal static field and the thickness an internal const, so tests and
// future theming can adjust the style without touching the drawing call sites.
//
// Opt-out: OpenHarmonyFocusRing.Enabled = false keeps the previous (ring-less) draw output.
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyFocusRing
{
    /// <summary>Opt-out knob: false never paints the ring.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Outline thickness in device pixels (documented style).</summary>
    internal const float Thickness = 3f;

    /// <summary>Outline colour (documented style; mutable for theming/tests).</summary>
    internal static Color Color { get; set; } = Colors.DodgerBlue;

    /// <summary>Rings painted since load (bookkeeping for tests/diagnostics).</summary>
    internal static int Draws { get; private set; }

    /// <summary>Paints the ring when the view is focused (called from OpenHarmonyView.Draw).</summary>
    internal static void Draw(ICanvas canvas, OpenHarmonyView view)
    {
        if (!Enabled || !view.IsFocused)
        {
            return;
        }
        RectF frame = view.Frame;
        if (frame.Width <= Thickness * 2f || frame.Height <= Thickness * 2f)
        {
            return;
        }
        float inset = Thickness / 2f;
        canvas.StrokeColor = Color;
        canvas.StrokeSize = Thickness;
        float x = frame.X + inset;
        float y = frame.Y + inset;
        float width = frame.Width - Thickness;
        float height = frame.Height - Thickness;
        if (view.CornerRadius > 0f)
        {
            canvas.DrawRoundedRectangle(x, y, width, height, Math.Max(1f, view.CornerRadius - inset));
        }
        else
        {
            canvas.DrawRectangle(x, y, width, height);
        }
        Draws++;
    }

    /// <summary>Resets the draw counter (tests only).</summary>
    internal static void ResetForTests() => Draws = 0;
}
