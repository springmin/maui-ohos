// Minimal shared-element transition for the self-drawn compositor (P1a-ANIM).
//
// Scope and contract (deliberately small, documented constraints):
//   * The key is the element's AutomationId with the reserved prefix "shared:". Set the same
//     AutomationId (e.g. "shared:cover") on the source element of the outgoing page and on the
//     target element of the incoming page; the first match per key is used.
//   * Only the INCOMING element is animated. The compositor draws exactly one page, so there is
//     no outgoing-page pass to hand off from: the target starts at the source's captured
//     position/size/opacity and settles into its arranged pose (a hero move, not a hand-off).
//   * The transition interpolates the element's arranged frame (TranslationX/Y + uniform Scale)
//     and opacity. An Image with AspectFit keeps its own letterboxing; the frame - not the
//     decoded bitmap - is what morphs.
//   * One shared element per navigation is the tested path; extra matches animate too, but the
//     capture keeps only the first occurrence of each key.
//
// Capture happens before the navigation commits (NavigationPage's RequestNavigation / Shell's
// Navigating), the pass starts after the commit (Pushed/Popped / Navigated) and runs on the
// shared frame loop with the page transition's configuration discipline: configurable duration
// and curve, opt-out switch, and a skip when the device asks to reduce animations.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Frame/opacity morph for AutomationId-keyed elements across a navigation.</summary>
internal static class OpenHarmonySharedTransition
{
    /// <summary>The AutomationId prefix that marks an element as shared.</summary>
    internal const string KeyPrefix = "shared:";

    /// <summary>Master switch; false disables the shared pass only.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Morph duration in milliseconds.</summary>
    internal static double DurationMs { get; set; } = 220;

    /// <summary>Curve applied to the normalized progress.</summary>
    internal static Easing Curve { get; set; } = Easing.CubicOut;

    /// <summary>True while a morph is running (diagnostics/tests).</summary>
    internal static bool IsActive => s_animation.Active;

    /// <summary>Morphs started since load (diagnostics/tests).</summary>
    internal static int Started { get; private set; }

    /// <summary>Morphs completed since load (diagnostics/tests).</summary>
    internal static int Completed => s_animation.Completed;

    private static readonly List<Captured> s_sources = new();
    private static readonly SharedAnimation s_animation = new();

    /// <summary>
    /// Records the shared elements of the page that is about to leave the draw tree. Call before
    /// the navigation commit; a page without "shared:" elements simply clears the previous set.
    /// </summary>
    internal static void Capture(IView? outgoingPage)
    {
        s_sources.Clear();
        if (outgoingPage is IVisualTreeElement root)
        {
            Collect(root, s_sources);
        }
    }

    /// <summary>
    /// Starts the morph for the shared elements of the page that just became visible. Without a
    /// captured counterpart, with the feature off or under reduce-motion this is a no-op.
    /// </summary>
    internal static void Run(IView? incomingPage)
    {
        if (!Enabled || OpenHarmonyMotion.ReduceMotion || DurationMs <= 0 ||
            s_sources.Count == 0 || incomingPage is not IVisualTreeElement root)
        {
            return;
        }
        var targets = new List<Captured>();
        Collect(root, targets);
        if (targets.Count == 0)
        {
            return;
        }
        // Match by key and keep the pairs that have a real frame on both sides.
        var matched = new List<(Captured Source, Captured Target)>();
        for (int i = 0; i < targets.Count; i++)
        {
            Captured target = targets[i];
            for (int j = 0; j < s_sources.Count; j++)
            {
                if (string.Equals(s_sources[j].Key, target.Key, StringComparison.Ordinal))
                {
                    matched.Add((s_sources[j], target));
                    break;
                }
            }
        }
        if (matched.Count == 0)
        {
            return;
        }
        s_animation.Commit();
        if (s_animation.Begin(matched, DurationMs, Curve ?? Easing.Linear))
        {
            Started++;
            OpenHarmonyAnimationLoop.Register(s_animation);
        }
    }

    /// <summary>Commits any in-flight morph immediately (navigation stack replaced/cleared).</summary>
    internal static void Cancel() => s_animation.Commit();

    private static void Collect(IVisualTreeElement node, List<Captured> into)
    {
        if (node is VisualElement element && element.AutomationId is { Length: > 0 } id &&
            id.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            string key = id.Substring(KeyPrefix.Length);
            bool seen = false;
            for (int i = 0; i < into.Count; i++)
            {
                if (string.Equals(into[i].Key, key, StringComparison.Ordinal))
                {
                    seen = true;
                    break;
                }
            }
            if (!seen)
            {
                // Prefer the last drawn platform frame when the virtual frame is unset: the
                // outgoing page's elements are measured out of the tree by the time the
                // navigation raises its events.
                Rect frame = element.Frame;
                if ((frame.Width <= 0 || frame.Height <= 0) &&
                    element.Handler?.PlatformView is OpenHarmonyView platform &&
                    platform.Frame.Width > 0 && platform.Frame.Height > 0)
                {
                    RectF platformFrame = platform.Frame;
                    frame = new Rect(platformFrame.X, platformFrame.Y,
                        platformFrame.Width, platformFrame.Height);
                }
                into.Add(new Captured(key, frame, double.IsFinite(element.Opacity) ? element.Opacity : 1, element));
            }
        }
        IReadOnlyList<IVisualTreeElement> children = node.GetVisualChildren();
        for (int i = 0; i < children.Count; i++)
        {
            Collect(children[i], into);
        }
    }

    private sealed class Captured
    {
        internal Captured(string key, Rect frame, double opacity, VisualElement element)
        {
            Key = key;
            Frame = frame;
            Opacity = opacity;
            Element = element;
        }

        internal string Key { get; }
        internal Rect Frame { get; }
        internal double Opacity { get; }
        internal VisualElement Element { get; }
    }

    private sealed class SharedAnimation : IOpenHarmonyAnimation
    {
        private VisualElement[] _elements = Array.Empty<VisualElement>();
        private Captured[] _sources = Array.Empty<Captured>();
        private double[] _baseOpacity = Array.Empty<double>();
        private double[] _baseTranslationX = Array.Empty<double>();
        private double[] _baseTranslationY = Array.Empty<double>();
        private double[] _baseScale = Array.Empty<double>();
        private double[] _startTranslationX = Array.Empty<double>();
        private double[] _startTranslationY = Array.Empty<double>();
        private double[] _startScale = Array.Empty<double>();
        private double[] _startOpacity = Array.Empty<double>();
        private int _count;
        private long _startMs;
        private long _durationMs = 1;
        private Easing _curve = Easing.Linear;
        private bool _prepared;

        internal bool Active { get; private set; }
        internal int Completed { get; private set; }

        public bool NeedsRedraw => true;

        internal bool Begin(List<(Captured Source, Captured Target)> pairs, double durationMs, Easing curve)
        {
            int count = pairs.Count;
            if (count == 0)
            {
                return false;
            }
            EnsureCapacity(count);
            _count = count;
            for (int i = 0; i < count; i++)
            {
                (Captured source, Captured target) = pairs[i];
                _sources[i] = source;
                _elements[i] = target.Element;
            }
            _startMs = OpenHarmonyAnimationLoop.NowMs;
            _durationMs = (long)Math.Max(1, durationMs);
            _curve = curve;
            _prepared = false;
            Active = true;
            // The morph needs the incoming page's arranged frame; the first step runs after the
            // compositor's arrange pass, so the start pose is computed there (see Prepare).
            RequestRepaint();
            return true;
        }

        public bool Step(long nowMs, float dtSeconds)
        {
            if (!Active || _count == 0)
            {
                Active = false;
                return false;
            }
            if (OpenHarmonyMotion.ReduceMotion)
            {
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
                Prepare();
            }
            double eased = _curve.Ease(t);
            for (int i = 0; i < _count; i++)
            {
                VisualElement element = _elements[i];
                element.TranslationX = _startTranslationX[i] + (_baseTranslationX[i] - _startTranslationX[i]) * eased;
                element.TranslationY = _startTranslationY[i] + (_baseTranslationY[i] - _startTranslationY[i]) * eased;
                element.Scale = _startScale[i] + (_baseScale[i] - _startScale[i]) * eased;
                element.Opacity = _startOpacity[i] + (_baseOpacity[i] - _startOpacity[i]) * eased;
            }
            return true;
        }

        /// <summary>
        /// Computes each pair's start pose from the frames as they are at the first step (the
        /// incoming element is arranged by then) and writes it, so the first visible frame is
        /// already the hero pose.
        /// </summary>
        private void Prepare()
        {
            _prepared = true;
            for (int i = 0; i < _count; i++)
            {
                VisualElement element = _elements[i];
                // The source frame is the outgoing page's captured pose; the target frame is
                // read live, because the incoming element is arranged after Run and may have
                // moved before this first step.
                Rect sourceFrame = _sources[i].Frame;
                Rect targetFrame = element.Frame;
                double baseScale = double.IsFinite(element.Scale) && element.Scale > 0 ? element.Scale : 1;
                double scale = 1;
                if (sourceFrame.Width > 0 && targetFrame.Width > 0)
                {
                    scale = Math.Clamp(sourceFrame.Width / targetFrame.Width, 0.2, 5.0);
                }
                _baseOpacity[i] = double.IsFinite(element.Opacity) ? Math.Clamp(element.Opacity, 0, 1) : 1;
                _baseTranslationX[i] = double.IsFinite(element.TranslationX) ? element.TranslationX : 0;
                _baseTranslationY[i] = double.IsFinite(element.TranslationY) ? element.TranslationY : 0;
                _baseScale[i] = baseScale;
                _startTranslationX[i] = _baseTranslationX[i] + (sourceFrame.X - targetFrame.X);
                _startTranslationY[i] = _baseTranslationY[i] + (sourceFrame.Y - targetFrame.Y);
                _startScale[i] = baseScale * scale;
                _startOpacity[i] = Math.Clamp(_sources[i].Opacity, 0, 1);
                element.TranslationX = _startTranslationX[i];
                element.TranslationY = _startTranslationY[i];
                element.Scale = _startScale[i];
                element.Opacity = _startOpacity[i];
            }
        }

        internal void Commit()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            for (int i = 0; i < _count; i++)
            {
                VisualElement element = _elements[i];
                if (_prepared)
                {
                    element.TranslationX = _baseTranslationX[i];
                    element.TranslationY = _baseTranslationY[i];
                    element.Scale = _baseScale[i];
                    element.Opacity = _baseOpacity[i];
                }
                _elements[i] = null!;
                _sources[i] = null!;
            }
            _count = 0;
            _prepared = false;
            Completed++;
            OpenHarmonyAnimationLoop.Unregister(this);
            RequestRepaint();
        }

        private static void RequestRepaint()
        {
            try
            {
                OpenHarmonyBridge.RequestRedraw();
            }
            catch (Exception)
            {
                // No host: the next input/frame repaint shows the settled element.
            }
        }

        private void EnsureCapacity(int count)
        {
            if (_elements.Length >= count)
            {
                return;
            }
            _elements = new VisualElement[count];
            _sources = new Captured[count];
            _baseOpacity = new double[count];
            _baseTranslationX = new double[count];
            _baseTranslationY = new double[count];
            _baseScale = new double[count];
            _startTranslationX = new double[count];
            _startTranslationY = new double[count];
            _startScale = new double[count];
            _startOpacity = new double[count];
        }
    }
}
