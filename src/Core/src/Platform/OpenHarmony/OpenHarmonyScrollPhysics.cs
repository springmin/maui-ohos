// Inertial scrolling (fling/momentum) for the OpenHarmony compositor.
//
// The renderer's drag path moves a scroll view's offset while the finger is down; when it is
// released this module keeps the offset moving with exponential friction until it settles or
// hits an edge. Reachable without touching the renderer because every offset write funnels
// through OpenHarmonyView.ScrollOffsetX/Y: the property setter records a sample here, and the
// shared OpenHarmonyAnimationLoop steps the momentum on the platform frame path. The release
// trigger is the platform touch stream (OpenHarmonyBridge.Touch) when it is available; the
// loop also detects a stalled sample stream so a container-driven drag (tests, hosts without
// the bridge event) can still fling.
//
// Edges clamp cleanly (the offset stops exactly at 0 / content-viewport, no bounce). A new
// press, a programmatic offset write through the handler mappers, a ScrollTo and list data
// changes all cancel an in-flight fling so app code never fights the momentum.
//
// Everything is opt-out: OpenHarmonyScrollPhysics.Enabled = false makes the module inert (the
// offset properties keep their plain assignment behaviour) and disables the loop's ticker.
using System.Runtime.CompilerServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyScrollPhysics
{
    /// <summary>Opt-out knob: false disables sampling, momentum and handler cancellation.</summary>
    internal static bool Enabled { get; set; } = true;

    // Tunables (internal so tests can pin the physics without magic numbers).
    /// <summary>Smallest release speed that starts a fling (device pixels per second).</summary>
    internal const float MinFlingVelocity = 320f;

    /// <summary>Momentum stops below this speed.</summary>
    internal const float StopVelocity = 25f;

    /// <summary>Velocity decay per second: v(t) = v0 * exp(-friction * t).</summary>
    internal const float FrictionPerSecond = 4.5f;

    /// <summary>Maximum integration step; longer frames are split so the decay stays stable.</summary>
    internal const float MaxStepSeconds = 0.032f;

    /// <summary>A sample older than this at release time does not fling (the finger paused).</summary>
    internal const long ReleaseWindowMs = 90;

    /// <summary>Fallback: start a fling when the sample stream stalls at high speed.</summary>
    internal const long StallWindowMs = 80;

    /// <summary>Two samples farther apart than this start a new velocity window.</summary>
    internal const long SampleGapMs = 120;

    /// <summary>Minimum samples in a window before a velocity is trusted.</summary>
    internal const int MinSamples = 3;

    /// <summary>Hard cap on one fling's duration (runaway guard).</summary>
    internal const long MaxFlingMs = 4000;

    /// <summary>Largest drag/fling excursion past an edge (the spring returns to the edge).</summary>
    internal const float MaxOverscroll = 64f;

    /// <summary>Share of the raw drag distance that shows past an edge (rubber band).</summary>
    internal const float RubberBandFactor = 0.45f;

    /// <summary>Edge-return spring stiffness (omega^2, omega = 18 rad/s).</summary>
    internal const float SpringStiffness = 324f;

    /// <summary>Edge-return damping (2 * zeta * omega, zeta = 0.75).</summary>
    internal const float SpringDamping = 27f;

    // Weight of the newest instantaneous velocity in the exponential moving average.
    private const float VelocitySmoothing = 0.4f;

    [ThreadStatic]
    private static bool t_applying;

    // Count of programmatic offset writes in flight (ScrollTo jumps and their animation steps):
    // these must not be sampled as drag velocity, or a long jump would look like a fling-speed
    // sample and the stalled-sample fallback could start a momentum nobody asked for.
    [ThreadStatic]
    private static int t_programmatic;

    private sealed class Tracked : IOpenHarmonyAnimation
    {
        public Tracked(OpenHarmonyView view) => View = view;

        public readonly OpenHarmonyView View;

        public float Offset;
        public float Velocity;
        public int Samples;
        public long LastSampleMs;
        public bool Horizontal;
        public bool Flinging;
        public float FlingVelocity;
        public long FlingStartMs;
        public bool Bouncing;
        public float BounceEdge;
        public float BounceValue;
        public float BounceVelocity;
        public bool Registered;

        public bool NeedsRedraw => Flinging || Bouncing;

        public bool Step(long nowMs, float dtSeconds)
        {
            if (!OpenHarmonyScrollPhysics.Enabled)
            {
                return false;
            }
            // Serialize with the touch thread (press cancel, mapper cancel, new samples): a
            // grab during momentum must land before the next integration step, never mid-way.
            lock (OpenHarmonyScrollPhysics.s_flinging)
            {
                if (Flinging)
                {
                    return Fling(nowMs, dtSeconds);
                }
                if (Bouncing)
                {
                    return Spring(dtSeconds);
                }
                // No momentum is in flight, so the only frame work left is the stalled-sample
                // release fallback. Keep asking for frames exactly while that fallback can still
                // fire: the finger is up, the last sample is inside the stall window and the
                // sampled speed can still start a fling. A settled or slow drag stops the ticker
                // right away instead of spinning for the old 300 ms expire window (18 idle
                // frames); a press has its own release path (OnTouch) and the next offset write,
                // release hint or fling re-registers this tracker.
                if (!OpenHarmonyScrollPhysics.s_pointerDown &&
                    nowMs - LastSampleMs <= OpenHarmonyScrollPhysics.StallWindowMs &&
                    Samples >= OpenHarmonyScrollPhysics.MinSamples &&
                    Math.Abs(Velocity) >= OpenHarmonyScrollPhysics.MinFlingVelocity)
                {
                    OpenHarmonyScrollPhysics.TryStartFling(View, StallWindowMs);
                    return true;
                }
                Unregister(this);
                return false;
            }
        }

        private bool Fling(long nowMs, float dtSeconds)
        {
            if (nowMs - FlingStartMs > MaxFlingMs)
            {
                StopFling(this);
                return false;
            }
            float max = OpenHarmonyScrollPhysics.MaxOffsetFor(this);
            float value = Offset;
            float velocity = FlingVelocity;
            float remaining = Math.Min(dtSeconds, MaxStepSeconds * 2f);
            bool hitEdge = false;
            while (remaining > 0f)
            {
                float step = Math.Min(remaining, MaxStepSeconds);
                remaining -= step;
                velocity *= MathF.Exp(-FrictionPerSecond * step);
                value += velocity * step;
                if (value <= 0f)
                {
                    value = 0f;
                    hitEdge = true;
                    break;
                }
                if (value >= max)
                {
                    value = max;
                    hitEdge = true;
                    break;
                }
            }
            if (hitEdge)
            {
                if (OpenHarmonyMotion.ReduceMotion)
                {
                    // Reduced motion: the content stops at the edge, no return excursion.
                    FlingVelocity = 0f;
                    ApplyOffset(this, value);
                    StopFling(this);
                    return false;
                }
                // The remaining speed carries the content past the edge and the spring returns
                // it; the amplitude is bounded so a very fast fling cannot fly off-screen.
                OpenHarmonyScrollPhysics.StartBounce(this, value, value <= 0f ? 0f : max, velocity);
                return true;
            }
            FlingVelocity = Math.Abs(velocity) < StopVelocity ? 0f : velocity;
            ApplyOffset(this, value);
            if (FlingVelocity == 0f || !Flinging)
            {
                StopFling(this);
                return false;
            }
            return true;
        }

        private bool Spring(float dtSeconds)
        {
            float remaining = Math.Min(dtSeconds, MaxStepSeconds * 2f);
            while (remaining > 0f)
            {
                float step = Math.Min(remaining, MaxStepSeconds);
                remaining -= step;
                BounceVelocity += (-SpringStiffness * (BounceValue - BounceEdge) - SpringDamping * BounceVelocity) * step;
                BounceValue += BounceVelocity * step;
                BounceValue = Math.Clamp(BounceValue, BounceEdge - OpenHarmonyScrollPhysics.MaxOverscroll,
                    BounceEdge + OpenHarmonyScrollPhysics.MaxOverscroll);
            }
            if (Math.Abs(BounceValue - BounceEdge) < 0.5f && Math.Abs(BounceVelocity) < StopVelocity)
            {
                OpenHarmonyScrollPhysics.ApplyOffset(this, BounceEdge);
                OpenHarmonyScrollPhysics.StopBounce(this);
                return false;
            }
            OpenHarmonyScrollPhysics.ApplyOffset(this, BounceValue);
            return true;
        }
    }

    // CWT keeps the state tied to the platform view (and dies with it); it is replaced (not
    // cleared) by ResetForTests because ConditionalWeakTable has no Clear.
    private static ConditionalWeakTable<OpenHarmonyView, Tracked> s_tracked = new();
    private static readonly List<Tracked> s_flinging = new();
    private static WeakReference<OpenHarmonyView>? s_lastScrolled;
    private static bool s_touchHooked;

    // One cached delegate for the platform touch subscription: subscribing must not build a
    // method-group delegate (the touch stream is a frame-path input).
    private static readonly Action<OpenHarmonyTouchEventArgs> s_onTouch = OnTouch;

    // True between a platform touch down and its up/cancel. Set from the platform touch thread
    // and read on the frame thread; it keeps the stalled-sample fallback from starting a fling
    // while the finger is still down.
    private static volatile bool s_pointerDown;

    // True while a text selection gesture (drag or selection-handle drag) owns the pointer. The
    // renderer sets it on the press and clears it on the release; a release that is still
    // suppressed must not start a fling from the scroll samples (dragging a selection is not a
    // scroll gesture). Set/cleared from the touch thread only.
    private static volatile bool s_suppressReleaseFling;

    /// <summary>Marks the current gesture as selection work: no fling when the finger lifts.</summary>
    internal static void SuppressReleaseFling() => s_suppressReleaseFling = true;

    /// <summary>Clears the selection-drag fling suppression (release/cancel).</summary>
    internal static void ClearReleaseSuppression() => s_suppressReleaseFling = false;

    /// <summary>
    /// Subscribes to the platform touch stream when the assembly loads, so the pointer state is
    /// known from the very first press even though the physics is only touched during a scroll.
    /// </summary>
    [ModuleInitializer]
    internal static void Attach() => HookTouch();

    /// <summary>Number of in-flight flings (tests/diagnostics).</summary>
    internal static int ActiveFlings
    {
        get
        {
            lock (s_flinging)
            {
                return s_flinging.Count;
            }
        }
    }

    /// <summary>
    /// Called by the platform view's offset setters. Records a velocity sample and starts
    /// watching the view; it does not move anything by itself.
    /// </summary>
    internal static void OnOffsetChanged(OpenHarmonyView view, bool horizontal, float oldValue, float newValue)
    {
        if (!Enabled || t_applying || t_programmatic > 0 || newValue == oldValue)
        {
            return;
        }
        lock (s_flinging)
        {
            Tracked tracked = s_tracked.GetValue(view, static v => new Tracked(v));
            long now = OpenHarmonyAnimationLoop.NowMs;
            if (tracked.Samples > 0 && now - tracked.LastSampleMs <= SampleGapMs)
            {
                float dt = Math.Max(0.001f, (now - tracked.LastSampleMs) / 1000f);
                float instantaneous = (newValue - tracked.Offset) / dt;
                tracked.Velocity = tracked.Samples == 1
                    ? instantaneous
                    : tracked.Velocity * (1f - VelocitySmoothing) + instantaneous * VelocitySmoothing;
                tracked.Samples++;
            }
            else
            {
                tracked.Velocity = 0f;
                tracked.Samples = 1;
            }
            tracked.Offset = newValue;
            tracked.Horizontal = horizontal;
            tracked.LastSampleMs = now;
            if (tracked.Flinging)
            {
                // A real drag during momentum grabs the scroller instead of fighting it.
                StopFling(tracked);
            }
            // The release hint walks the last scrolled view through a weak reference so a static
            // never roots a view. It is only re-created when the scrolled view changes: a drag or
            // a fling emits one sample per frame, and a fresh WeakReference per sample was the
            // only allocation left on that path.
            if (s_lastScrolled is null ||
                !s_lastScrolled.TryGetTarget(out OpenHarmonyView? lastScrolled) ||
                !ReferenceEquals(lastScrolled, view))
            {
                s_lastScrolled = new WeakReference<OpenHarmonyView>(view);
            }
            OpenHarmonyScrollbars.NotifyScrolled(view);
            HookTouch();
            EnsureRegistered(tracked);
        }
    }

    /// <summary>
    /// Starts a fling for <paramref name="view"/> when its recent samples carry enough speed.
    /// Public to the assembly so tests can drive the release deterministically.
    /// </summary>
    internal static bool TryStartFling(OpenHarmonyView view, long maxSampleAgeMs = ReleaseWindowMs)
    {
        if (!Enabled || view is null || s_suppressReleaseFling || !s_tracked.TryGetValue(view, out Tracked? tracked))
        {
            return false;
        }
        lock (s_flinging)
        {
            if (tracked.Flinging || tracked.Bouncing || tracked.Samples < MinSamples)
            {
                return false;
            }
            long now = OpenHarmonyAnimationLoop.NowMs;
            if (now - tracked.LastSampleMs > maxSampleAgeMs)
            {
                return false;
            }
            float velocity = tracked.Velocity;
            if (Math.Abs(velocity) < MinFlingVelocity)
            {
                return false;
            }
            float max = MaxOffsetFor(tracked);
            if ((velocity < 0f && tracked.Offset <= 0.01f) || (velocity > 0f && tracked.Offset >= max - 0.01f))
            {
                return false;
            }
            tracked.Flinging = true;
            tracked.FlingVelocity = velocity;
            tracked.FlingStartMs = now;
            s_flinging.Add(tracked);
            EnsureRegistered(tracked);
        }
        OpenHarmonyBridge.RequestRedraw();
        return true;
    }

    /// <summary>Stops an in-flight fling on <paramref name="view"/> (a press, ScrollTo, data change).</summary>
    internal static void Cancel(OpenHarmonyView? view)
    {
        if (view is null || !s_tracked.TryGetValue(view, out Tracked? tracked))
        {
            return;
        }
        lock (s_flinging)
        {
            bool wasBouncing = tracked.Bouncing;
            StopFling(tracked);
            StopBounce(tracked);
            tracked.Samples = 0;
            tracked.Velocity = 0f;
            Unregister(tracked);
            if (wasBouncing)
            {
                // Never leave the content parked past an edge: the new owner (press, data change,
                // ScrollTo) starts from the clamped offset.
                ClampToEdge(tracked);
            }
        }
        if (s_lastScrolled is { } reference &&
            reference.TryGetTarget(out OpenHarmonyView? last) && ReferenceEquals(last, view))
        {
            s_lastScrolled = null;
        }
    }

    /// <summary>
    /// Cancels when the offset write did not come from this module (the handler mappers call
    /// this; a fling applying its own step must not cancel itself).
    /// </summary>
    internal static void CancelIfAppDriven(OpenHarmonyView? view)
    {
        if (!t_applying)
        {
            Cancel(view);
        }
    }

    /// <summary>Stops every in-flight fling (a fresh press grabs whichever scroller was moving).</summary>
    internal static void CancelAll()
    {
        List<Tracked> active;
        lock (s_flinging)
        {
            active = new List<Tracked>(s_flinging);
            foreach (Tracked tracked in active)
            {
                bool wasBouncing = tracked.Bouncing;
                StopFling(tracked);
                StopBounce(tracked);
                tracked.Samples = 0;
                tracked.Velocity = 0f;
                Unregister(tracked);
                if (wasBouncing)
                {
                    ClampToEdge(tracked);
                }
            }
        }
    }

    /// <summary>
    /// Offset the drag at <paramref name="current"/> + <paramref name="delta"/> should land on.
    /// Inside the content it is the raw sum; past an edge the movement is rubber-banded to at
    /// most <see cref="MaxOverscroll"/> so the edge resists the drag. Reduce-motion clamps hard.
    /// </summary>
    internal static float DragOffset(OpenHarmonyView view, float current, float delta, float maxOffset)
    {
        float target = current + delta;
        if (!Enabled || OpenHarmonyMotion.ReduceMotion)
        {
            return Math.Clamp(target, 0f, maxOffset);
        }
        if (target < 0f)
        {
            return RubberBand(target, 0f);
        }
        if (target > maxOffset)
        {
            return RubberBand(target, maxOffset);
        }
        return target;
    }

    /// <summary>True while past an edge (a release starts the return spring).</summary>
    internal static bool IsOverscrolled(OpenHarmonyView view)
    {
        if (!s_tracked.TryGetValue(view, out Tracked? tracked))
        {
            return false;
        }
        float max = MaxOffsetFor(tracked);
        float value = tracked.Horizontal ? view.ScrollOffsetX : view.ScrollOffsetY;
        return value < 0f || value > max;
    }

    /// <summary>True while the edge-return spring is moving <paramref name="view"/>.</summary>
    internal static bool IsBouncing(OpenHarmonyView view)
        => s_tracked.TryGetValue(view, out Tracked? tracked) && tracked.Bouncing;

    /// <summary>
    /// Starts the edge-return spring when the drag left the content past an edge. Returns true
    /// when a spring was started; reduced motion snaps to the edge and returns false.
    /// </summary>
    internal static bool TryReturnToEdge(OpenHarmonyView view)
    {
        if (!Enabled || view is null || !s_tracked.TryGetValue(view, out Tracked? tracked))
        {
            return false;
        }
        lock (s_flinging)
        {
            float max = MaxOffsetFor(tracked);
            float value = tracked.Horizontal ? view.ScrollOffsetX : view.ScrollOffsetY;
            float edge = value < 0f ? 0f : (value > max ? max : float.NaN);
            if (float.IsNaN(edge))
            {
                return false;
            }
            if (OpenHarmonyMotion.ReduceMotion)
            {
                ApplyOffset(tracked, edge);
                return false;
            }
            if (!tracked.Bouncing)
            {
                tracked.BounceEdge = edge;
                tracked.BounceValue = value;
                tracked.BounceVelocity = 0f;
                tracked.Bouncing = true;
                if (!s_flinging.Contains(tracked))
                {
                    s_flinging.Add(tracked);
                }
                EnsureRegistered(tracked);
            }
            return true;
        }
    }

    /// <summary>Marks an offset write as programmatic (no drag sampling).</summary>
    internal static void BeginProgrammatic() => t_programmatic++;

    /// <summary>Ends a programmatic offset write.</summary>
    internal static void EndProgrammatic()
    {
        if (t_programmatic > 0)
        {
            t_programmatic--;
        }
    }

    /// <summary>Convenience for tests: the smoothed release velocity of the last drag.</summary>
    internal static float DebugVelocity(OpenHarmonyView view)
        => s_tracked.TryGetValue(view, out Tracked? tracked) ? tracked.Velocity : 0f;

    /// <summary>True while momentum is moving <paramref name="view"/>.</summary>
    internal static bool IsFlinging(OpenHarmonyView view)
        => s_tracked.TryGetValue(view, out Tracked? tracked) && tracked.Flinging;

    /// <summary>Drops all state and stops the loop (tests only).</summary>
    internal static void ResetForTests()
    {
        OpenHarmonyAnimationLoop.Stop();
        lock (s_flinging)
        {
            s_flinging.Clear();
        }
        s_tracked = new ConditionalWeakTable<OpenHarmonyView, Tracked>();
        s_lastScrolled = null;
        t_programmatic = 0;
    }

    /// <summary>Pointer state + release hint from the platform touch stream.</summary>
    private static void OnTouch(OpenHarmonyTouchEventArgs args)
    {
        if (!Enabled)
        {
            return;
        }
        if (args.Action == OpenHarmonyTouchAction.Down)
        {
            s_pointerDown = true;
            // A fresh press grabs whichever scroller was still moving.
            CancelAll();
            OpenHarmonyScrollAnimation.CancelAll();
            return;
        }
        if (args.Action == OpenHarmonyTouchAction.Cancel)
        {
            s_pointerDown = false;
            s_suppressReleaseFling = false;
            CancelAll();
            OpenHarmonyScrollAnimation.CancelAll();
            return;
        }
        if (args.Action != OpenHarmonyTouchAction.Up)
        {
            return;
        }
        s_pointerDown = false;
        if (s_suppressReleaseFling)
        {
            // A text selection gesture owned this pointer; the renderer clears the flag when its
            // release handler runs. Never fling out of a selection drag.
            s_suppressReleaseFling = false;
            return;
        }
        if (s_lastScrolled is not { } reference ||
            !reference.TryGetTarget(out OpenHarmonyView? view) ||
            !view.CanvasFrame.Contains(args.X, args.Y))
        {
            return;
        }
        if (!TryStartFling(view))
        {
            // A drag that ended past an edge has no momentum to start, but still springs back.
            TryReturnToEdge(view);
        }
    }

    private static void HookTouch()
    {
        lock (s_flinging)
        {
            if (s_touchHooked)
            {
                return;
            }
            s_touchHooked = true;
            OpenHarmonyBridge.Touch += s_onTouch;
        }
    }

    private static void EnsureRegistered(Tracked tracked)
    {
        if (tracked.Registered)
        {
            return;
        }
        tracked.Registered = true;
        OpenHarmonyAnimationLoop.Register(tracked);
    }

    private static void Unregister(Tracked tracked)
    {
        if (!tracked.Registered)
        {
            return;
        }
        tracked.Registered = false;
        OpenHarmonyAnimationLoop.Unregister(tracked);
    }

    private static void StopFling(Tracked tracked)
    {
        if (!tracked.Flinging)
        {
            return;
        }
        tracked.Flinging = false;
        tracked.FlingVelocity = 0f;
        s_flinging.Remove(tracked);
    }

    /// <summary>Hands a fling that hit an edge to the return spring (bounded excursion).</summary>
    private static void StartBounce(Tracked tracked, float value, float edge, float velocity)
    {
        tracked.Flinging = false;
        tracked.FlingVelocity = 0f;
        tracked.Bouncing = true;
        tracked.BounceEdge = edge;
        tracked.BounceValue = value;
        tracked.BounceVelocity = velocity;
        if (!s_flinging.Contains(tracked))
        {
            s_flinging.Add(tracked);
        }
        EnsureRegistered(tracked);
    }

    private static void StopBounce(Tracked tracked)
    {
        if (!tracked.Bouncing)
        {
            return;
        }
        tracked.Bouncing = false;
        tracked.BounceValue = tracked.BounceEdge;
        tracked.BounceVelocity = 0f;
        s_flinging.Remove(tracked);
    }

    private static void ClampToEdge(Tracked tracked)
    {
        float max = MaxOffsetFor(tracked);
        float value = tracked.Horizontal ? tracked.View.ScrollOffsetX : tracked.View.ScrollOffsetY;
        float clamped = Math.Clamp(value, 0f, max);
        if (clamped != value)
        {
            ApplyOffset(tracked, clamped);
        }
    }

    private static float RubberBand(float target, float edge)
    {
        float overshoot = MathF.Abs(target - edge) * RubberBandFactor;
        if (overshoot > MaxOverscroll)
        {
            overshoot = MaxOverscroll;
        }
        return edge + MathF.CopySign(overshoot, target - edge);
    }

    private static float MaxOffsetFor(Tracked tracked)
    {
        RectF frame = tracked.View.Frame;
        float viewport = tracked.Horizontal ? frame.Width : frame.Height;
        float content = tracked.Horizontal ? tracked.View.ScrollContentWidth : tracked.View.ScrollContentHeight;
        return Math.Max(0f, content - viewport);
    }

    private static void ApplyOffset(Tracked tracked, float value)
    {
        tracked.Offset = value;
        t_applying = true;
        try
        {
            if (tracked.Horizontal)
            {
                tracked.View.ScrollOffsetX = value;
            }
            else
            {
                tracked.View.ScrollOffsetY = value;
            }
            // Mirror the renderer's drag path: the virtual view observes the position and the
            // handlers' ScrollOffsetChanged hook slides virtualized windows.
            if (tracked.View.VirtualView is IScrollView virtualScroll)
            {
                if (tracked.Horizontal)
                {
                    virtualScroll.HorizontalOffset = value;
                }
                else
                {
                    virtualScroll.VerticalOffset = value;
                }
            }
            tracked.View.ScrollOffsetChanged?.Invoke();
        }
        finally
        {
            t_applying = false;
        }
        OpenHarmonyBridge.RequestRedraw();
    }
}
