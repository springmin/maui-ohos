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

    // Weight of the newest instantaneous velocity in the exponential moving average.
    private const float VelocitySmoothing = 0.4f;

    [ThreadStatic]
    private static bool t_applying;

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
        public bool Registered;

        public bool NeedsRedraw => Flinging;

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
            FlingVelocity = hitEdge || Math.Abs(velocity) < StopVelocity ? 0f : velocity;
            ApplyOffset(this, value);
            if (FlingVelocity == 0f || !Flinging)
            {
                StopFling(this);
                return false;
            }
            return true;
        }
    }

    // CWT keeps the state tied to the platform view (and dies with it); it is replaced (not
    // cleared) by ResetForTests because ConditionalWeakTable has no Clear.
    private static ConditionalWeakTable<OpenHarmonyView, Tracked> s_tracked = new();
    private static readonly List<Tracked> s_flinging = new();
    private static WeakReference<OpenHarmonyView>? s_lastScrolled;
    private static bool s_touchHooked;

    // True between a platform touch down and its up/cancel. Set from the platform touch thread
    // and read on the frame thread; it keeps the stalled-sample fallback from starting a fling
    // while the finger is still down.
    private static volatile bool s_pointerDown;

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
        if (!Enabled || t_applying || newValue == oldValue)
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
        if (!Enabled || view is null || !s_tracked.TryGetValue(view, out Tracked? tracked))
        {
            return false;
        }
        lock (s_flinging)
        {
            if (tracked.Flinging || tracked.Samples < MinSamples)
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
            StopFling(tracked);
            tracked.Samples = 0;
            tracked.Velocity = 0f;
            Unregister(tracked);
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
                StopFling(tracked);
                tracked.Samples = 0;
                tracked.Velocity = 0f;
                Unregister(tracked);
            }
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
            return;
        }
        if (args.Action == OpenHarmonyTouchAction.Cancel)
        {
            s_pointerDown = false;
            CancelAll();
            return;
        }
        if (args.Action != OpenHarmonyTouchAction.Up)
        {
            return;
        }
        s_pointerDown = false;
        if (s_lastScrolled is not { } reference ||
            !reference.TryGetTarget(out OpenHarmonyView? view) ||
            !view.Frame.Contains(args.X, args.Y))
        {
            return;
        }
        TryStartFling(view);
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
            OpenHarmonyBridge.Touch += OnTouch;
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
