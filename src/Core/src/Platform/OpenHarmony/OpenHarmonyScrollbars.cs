// Auto-hiding scrollbars for the OpenHarmony compositor.
//
// Scroll views (ScrollView/CollectionView/ListView platform views) draw one thin vertical bar
// at their trailing edge while the content overflows the viewport. It is drawn from the view's
// own Draw pass, so there is no extra host/shell dependency: the compositor's canvas is used
// exactly like the rest of the slice's painting. The compositor paints a view's own pass before
// its children, so a scrolled child with an opaque background paints over the bar; the slice's
// rows are transparent in practice, and the geometry tests pin the bar's own output.
//
// The bar is visible for HoldMs after the last real scroll offset change, then fades over
// FadeMs through the shared OpenHarmonyAnimationLoop (the loop unsubscribes when the fade
// ends, so an idle scroller draws no bar and causes no frame ticks). The one final repaint
// when the fade completes is requested explicitly, otherwise the last faint frame would stay
// on screen.
//
// Opt-out: OpenHarmonyScrollbars.Enabled = false keeps the previous draw output (no bar).
using System.Runtime.CompilerServices;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyScrollbars
{
    /// <summary>Opt-out knob: false never draws a scrollbar (and never starts a fade).</summary>
    internal static bool Enabled { get; set; } = true;

    // Documented style: a 4 px rounded bar, 3 px in from the trailing edge.
    /// <summary>Bar thickness in device pixels.</summary>
    internal const float Thickness = 4f;

    /// <summary>Distance between the bar and the viewport's trailing edge.</summary>
    internal const float Margin = 3f;

    /// <summary>The thumb never shrinks below this length.</summary>
    internal const float MinThumbLength = 24f;

    /// <summary>Full opacity after the last scroll for this long.</summary>
    internal const long HoldMs = 900;

    /// <summary>Fade-out duration.</summary>
    internal const long FadeMs = 250;

    /// <summary>Thumb colour; the opacity is scaled by the fade.</summary>
    internal static Color ThumbColor { get; set; } = Colors.White;

    /// <summary>Maximum thumb opacity while fully visible.</summary>
    internal const float ThumbOpacity = 0.35f;

    /// <summary>Below this the bar is not drawn at all.</summary>
    private const float VisibleOpacity = 0.01f;

    private sealed class BarState : IOpenHarmonyAnimation
    {
        public BarState(OpenHarmonyView view) => View = view;

        public readonly OpenHarmonyView View;
        public float Opacity;
        public long LastActivityMs;
        public bool Registered;

        public bool NeedsRedraw => Opacity > VisibleOpacity && Opacity < 1f;

        public bool Step(long nowMs, float dtSeconds)
        {
            if (!Enabled)
            {
                return false;
            }
            if (nowMs - LastActivityMs <= HoldMs)
            {
                return true;
            }
            Opacity -= dtSeconds * (1000f / FadeMs);
            if (Opacity > VisibleOpacity)
            {
                return true;
            }
            Opacity = 0f;
            // The fade's last frame is already painted with a faint bar; repaint once more so
            // the settled state (no bar) is what stays on screen.
            OpenHarmonyBridge.RequestRedraw();
            return false;
        }
    }

    private static ConditionalWeakTable<OpenHarmonyView, BarState> s_states = new();
    private static readonly object s_sync = new();

    /// <summary>Bars painted since load (bookkeeping for tests/diagnostics).</summary>
    internal static int Draws { get; private set; }

    /// <summary>Marks scroll activity on <paramref name="view"/> and starts the auto-hide fade.</summary>
    internal static void NotifyScrolled(OpenHarmonyView view)
    {
        if (!Enabled)
        {
            return;
        }
        lock (s_sync)
        {
            BarState state = s_states.GetValue(view, static v => new BarState(v));
            state.Opacity = 1f;
            state.LastActivityMs = OpenHarmonyAnimationLoop.NowMs;
            if (!state.Registered)
            {
                state.Registered = true;
                OpenHarmonyAnimationLoop.Register(state);
            }
        }
    }

    /// <summary>Current bar opacity (0 when idle).</summary>
    internal static float OpacityFor(OpenHarmonyView view)
        => s_states.TryGetValue(view, out BarState? state) ? state.Opacity : 0f;

    /// <summary>True when the bar should be painted this pass.</summary>
    internal static bool ShouldDraw(OpenHarmonyView view)
        => Enabled && OpacityFor(view) > VisibleOpacity && ThumbRect(view) is not null;

    /// <summary>
    /// Vertical thumb rectangle for the current viewport/content/offset, or null when the
    /// content does not overflow vertically (nothing to scroll).
    /// </summary>
    internal static RectF? ThumbRect(OpenHarmonyView view)
    {
        RectF frame = view.Frame;
        float viewport = frame.Height;
        float content = view.ScrollContentHeight;
        if (frame.Width <= Thickness + Margin || viewport <= 0f || content <= viewport + 0.5f)
        {
            return null;
        }
        float maxOffset = content - viewport;
        float length = Math.Max(MinThumbLength, viewport * (viewport / content));
        length = Math.Min(length, viewport);
        float travel = viewport - length;
        float offset = Math.Clamp(view.ScrollOffsetY, 0f, maxOffset);
        float y = frame.Y + (maxOffset > 0f ? travel * (offset / maxOffset) : 0f);
        float x = frame.Right - Margin - Thickness;
        return new RectF(x, y, Thickness, length);
    }

    /// <summary>Paints the bar when it is visible (called from OpenHarmonyView.Draw).</summary>
    internal static void Draw(ICanvas canvas, OpenHarmonyView view)
    {
        float fade = OpacityFor(view);
        if (!Enabled || fade <= VisibleOpacity || ThumbRect(view) is not { } thumb)
        {
            return;
        }
        canvas.FillColor = TintFor(fade);
        canvas.FillRoundedRectangle(thumb.X, thumb.Y, thumb.Width, thumb.Height, thumb.Width / 2f);
        Draws++;
    }

    // One Color per distinct (colour, opacity): the bar holds full opacity for HoldMs, so the
    // frames in that window reuse the same tint instead of allocating a Color per draw.
    private static Color s_tint = Colors.White;
    private static float s_tintOpacity = -1f;
    private static Color? s_tintSource;

    private static Color TintFor(float fade)
    {
        float opacity = fade * ThumbOpacity;
        if (s_tintSource is null || !ReferenceEquals(s_tintSource, ThumbColor) || s_tintOpacity != opacity)
        {
            s_tintSource = ThumbColor;
            s_tintOpacity = opacity;
            s_tint = ThumbColor.WithAlpha(opacity);
        }
        return s_tint;
    }

    /// <summary>Drops all bar state (tests only).</summary>
    internal static void ResetForTests()
    {
        lock (s_sync)
        {
            s_states = new ConditionalWeakTable<OpenHarmonyView, BarState>();
            s_tintSource = null;
            Draws = 0;
        }
    }
}
