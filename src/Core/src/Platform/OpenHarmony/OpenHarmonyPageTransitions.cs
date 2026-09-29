// Page enter transitions for the self-drawn compositor (P1a-ANIM).
//
// The compositor draws exactly one page (the visible page of a NavigationPage / the current
// Shell page), so a push/pop cannot cross-fade two pages: MAUI's navigation pipeline replaces
// the stack in managed code and the outgoing page leaves the draw tree in the same commit. What
// the route can express natively is an enter pass on the page that becomes visible - a fade-in
// from 0 to its own opacity plus a short horizontal settle - and that is what this class runs.
//
// The pass is frame-driven through OpenHarmonyAnimationLoop: the first step writes the entering
// pose, every step interpolates with a configurable duration/curve, and finishing restores the
// captured opacity/translation exactly (so an app that bound those properties gets its values
// back untouched). The loop registers the pass once and unregisters it when it commits.
//
// Configuration surface (internal; the transitions are opt-out, not opt-in):
//   * Enabled      - master switch, false restores the instant navigation commit.
//   * DurationMs   - enter duration in milliseconds (default 180).
//   * Curve        - MAUI Easing applied to the normalized progress (default Easing.CubicOut).
//   * SlideFactor  - horizontal start offset as a fraction of the page width (default 0.05).
//   * MaxSlideDip  - upper bound for that offset in surface pixels (default 48).
// The device's "reduce animations" setting (OpenHarmonyMotion.ReduceMotion) skips the pass
// entirely, and a pass already running commits itself on its next frame.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Fade + slide enter pass for a newly visible NavigationPage/Shell page.</summary>
internal static class OpenHarmonyPageTransitions
{
    /// <summary>Master switch; false keeps the previous instant commit.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Enter duration in milliseconds.</summary>
    internal static double DurationMs { get; set; } = 180;

    /// <summary>Curve applied to the normalized progress.</summary>
    internal static Easing Curve { get; set; } = Easing.CubicOut;

    /// <summary>Horizontal start offset as a fraction of the page width.</summary>
    internal static double SlideFactor { get; set; } = 0.05;

    /// <summary>Upper bound for the start offset in surface pixels.</summary>
    internal static double MaxSlideDip { get; set; } = 48;

    private static readonly EnterAnimation s_enter = new();

    /// <summary>True while a page is mid-enter (diagnostics/tests).</summary>
    internal static bool IsActive => s_enter.Active;

    /// <summary>The page the running enter pass belongs to (null when idle).</summary>
    internal static VisualElement? PendingPage => s_enter.Page;

    /// <summary>Completed enter passes since load (diagnostics/tests).</summary>
    internal static int Completed;

    /// <summary>
    /// Starts the enter pass for the page that just became visible. <paramref name="forward"/>
    /// is true for a push (the page settles from the right) and false for a pop (from the left).
    /// </summary>
    internal static void Enter(VisualElement? page, bool forward)
    {
        if (page is null)
        {
            return;
        }
        if (!Enabled || OpenHarmonyMotion.ReduceMotion || DurationMs <= 0)
        {
            return;
        }
        // A rapid push/pop sequence: commit the previous page's values before moving on, so an
        // interrupted pass can never leave a page translated or faded.
        s_enter.Commit();
        s_enter.Begin(page, forward, DurationMs, Curve ?? Easing.Linear, SlideFactor, MaxSlideDip);
        OpenHarmonyAnimationLoop.Register(s_enter);
    }

    /// <summary>Commits any in-flight pass immediately (navigation stack replaced/cleared).</summary>
    internal static void Cancel() => s_enter.Commit();

    private sealed class EnterAnimation : IOpenHarmonyAnimation
    {
        private VisualElement? _page;
        private double _baseOpacity = 1;
        private double _baseTranslationX;
        private long _startMs;
        private long _durationMs = 1;
        private double _offset;
        private Easing _curve = Easing.Linear;
        private bool _forward;
        private double _slideFactor;
        private double _maxSlideDip;
        private bool _prepared;

        internal bool Active { get; private set; }

        internal VisualElement? Page => _page;

        public bool NeedsRedraw => true;

        internal void Begin(VisualElement page, bool forward, double durationMs, Easing curve,
            double slideFactor, double maxSlideDip)
        {
            _page = page;
            _baseOpacity = double.IsFinite(page.Opacity) ? Math.Clamp(page.Opacity, 0, 1) : 1;
            _baseTranslationX = double.IsFinite(page.TranslationX) ? page.TranslationX : 0;
            _forward = forward;
            _slideFactor = slideFactor;
            _maxSlideDip = maxSlideDip;
            _offset = 0;
            _prepared = false;
            _startMs = OpenHarmonyAnimationLoop.NowMs;
            _durationMs = (long)Math.Max(1, durationMs);
            _curve = curve;
            Active = true;
            // The enter fade is written synchronously (it needs no layout), so a paint between
            // the navigation commit and the first frame tick already hides the new page; the
            // slide offset is prepared on the first step, when the page has a real width.
            page.Opacity = 0;
            page.TranslationX = _baseTranslationX;
        }

        public bool Step(long nowMs, float dtSeconds)
        {
            if (!Active || _page is null)
            {
                Active = false;
                return false;
            }
            if (OpenHarmonyMotion.ReduceMotion)
            {
                // The setting flipped mid-pass: snap to the settled pose and retire.
                Commit();
                return false;
            }
            double t = (nowMs - _startMs) / (double)_durationMs;
            if (t >= 1)
            {
                Commit();
                return false;
            }
            if (t < 0)
            {
                t = 0;
            }
            if (!_prepared)
            {
                // The page may not be arranged on the first frame after the navigation commit
                // (MAUI resets Width when a page leaves the tree); keep looking until a real
                // width is available, with the surface viewport as the last resort.
                double width = _page.Width > 0
                    ? _page.Width
                    : _page.Frame.Width > 0 ? _page.Frame.Width : OpenHarmonyView.SurfaceViewportWidth;
                if (width > 0)
                {
                    double slide = Math.Min(_maxSlideDip, Math.Max(0, width * _slideFactor));
                    _offset = _forward ? slide : -slide;
                    _prepared = true;
                }
            }
            double eased = _curve.Ease(t);
            _page.Opacity = _baseOpacity * eased;
            _page.TranslationX = _baseTranslationX + _offset * (1 - eased);
            return true;
        }

        internal void Commit()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            if (_page is { } page)
            {
                page.Opacity = _baseOpacity;
                page.TranslationX = _baseTranslationX;
                Completed++;
            }
            _page = null;
            OpenHarmonyAnimationLoop.Unregister(this);
            try
            {
                OpenHarmonyBridge.RequestRedraw();
            }
            catch (Exception)
            {
                // No host: the next input/frame repaint shows the settled page.
            }
        }
    }
}
