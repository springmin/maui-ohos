// Animated programmatic scrolling for the self-drawn compositor.
//
// CollectionView.ScrollTo(..., animate: true) previously jumped: the compositor had no scroll
// animation. This module tweens the offset through the shared OpenHarmonyAnimationLoop (the same
// frame source the fling and the fade effects use), with an ease-out cubic curve and a duration
// proportional to the distance, bounded by MinDurationMs..MaxDurationMs. The caller supplies the
// apply callback so the materializer can slide its window and mirror IScrollView on every step.
//
// One animation per platform view (starting a new one cancels the previous), cancelled by a new
// press, a data change or a direct ScrollTo. The device "reduce animations" setting snaps to the
// target synchronously instead of animating.
using System.Runtime.CompilerServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyScrollAnimation
{
    /// <summary>Shortest animation, so a tiny correction is still perceptible.</summary>
    internal const long MinDurationMs = 160;

    /// <summary>Longest animation, so a full-list jump does not feel sluggish.</summary>
    internal const long MaxDurationMs = 420;

    /// <summary>Pixels per millisecond used to scale the duration with the distance.</summary>
    internal const float SpeedPxPerMs = 1.6f;

    private sealed class State : IOpenHarmonyAnimation
    {
        public State(OpenHarmonyView view) => View = view;

        public readonly OpenHarmonyView View;
        public float From;
        public float To;
        public long StartMs;
        public long DurationMs;
        public Action<float>? Apply;
        public bool Registered;

        public bool NeedsRedraw => true;

        public bool Step(long nowMs, float dtSeconds)
        {
            float t = DurationMs <= 0 ? 1f : (nowMs - StartMs) / (float)DurationMs;
            if (t >= 1f)
            {
                Apply?.Invoke(To);
                Finish(this);
                return false;
            }
            float eased = 1f - MathF.Pow(1f - t, 3f);
            Apply?.Invoke(From + (To - From) * eased);
            return true;
        }
    }

    private static readonly object s_sync = new();
    // CWT keeps the state tied to the platform view (and dies with it); replaced (not cleared) by
    // ResetForTests because ConditionalWeakTable has no Clear.
    private static ConditionalWeakTable<OpenHarmonyView, State> s_states = new();

    /// <summary>Number of animations currently registered (tests/diagnostics).</summary>
    internal static int ActiveCount
    {
        get
        {
            lock (s_sync)
            {
                int count = 0;
                foreach (KeyValuePair<OpenHarmonyView, State> entry in s_states)
                {
                    if (entry.Value.Registered)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    /// <summary>True while an animated scroll is moving <paramref name="view"/>.</summary>
    internal static bool IsAnimating(OpenHarmonyView? view)
        => view is not null && s_states.TryGetValue(view, out State? state) && state.Registered;

    /// <summary>Current animation target (tests), NaN when idle.</summary>
    internal static float TargetFor(OpenHarmonyView? view)
        => view is not null && s_states.TryGetValue(view, out State? state) && state.Registered ? state.To : float.NaN;

    /// <summary>Starts (or restarts) the tween of <paramref name="view"/>'s offset to the target.</summary>
    internal static void Start(OpenHarmonyView view, float target, Action<float> apply)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(apply);
        Cancel(view);
        if (!OpenHarmonyAnimationLoop.Enabled || OpenHarmonyMotion.ReduceMotion)
        {
            // Reduced motion: the jump lands synchronously, without a frame of intermediate pose.
            apply(target);
            return;
        }
        lock (s_sync)
        {
            State state = s_states.GetValue(view, static v => new State(v));
            state.From = view.ScrollOffsetY;
            state.To = target;
            state.Apply = apply;
            state.StartMs = OpenHarmonyAnimationLoop.NowMs;
            float distance = MathF.Abs(target - state.From);
            state.DurationMs = Math.Clamp((long)(distance / SpeedPxPerMs), MinDurationMs, MaxDurationMs);
            if (!state.Registered)
            {
                state.Registered = true;
                OpenHarmonyAnimationLoop.Register(state);
            }
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>Cancels the in-flight animation of a view (press, data change, direct jump).</summary>
    internal static void Cancel(OpenHarmonyView? view)
    {
        if (view is null || !s_states.TryGetValue(view, out State? state))
        {
            return;
        }
        lock (s_sync)
        {
            if (!state.Registered)
            {
                return;
            }
            state.Registered = false;
            state.Apply = null;
            s_states.Remove(view);
            OpenHarmonyAnimationLoop.Unregister(state);
        }
    }

    /// <summary>Cancels every in-flight animated scroll (a fresh press grabs the scroller).</summary>
    internal static void CancelAll()
    {
        List<State> active = new();
        lock (s_sync)
        {
            foreach (KeyValuePair<OpenHarmonyView, State> entry in s_states)
            {
                if (entry.Value.Registered)
                {
                    active.Add(entry.Value);
                }
            }
        }
        foreach (State state in active)
        {
            Cancel(state.View);
        }
    }

    private static void Finish(State state)
    {
        lock (s_sync)
        {
            state.Registered = false;
            state.Apply = null;
            s_states.Remove(state.View);
            OpenHarmonyAnimationLoop.Unregister(state);
        }
    }

    /// <summary>Drops all state and stops the loop (tests only).</summary>
    internal static void ResetForTests()
    {
        OpenHarmonyAnimationLoop.Stop();
        lock (s_sync)
        {
            s_states = new ConditionalWeakTable<OpenHarmonyView, State>();
        }
    }
}
