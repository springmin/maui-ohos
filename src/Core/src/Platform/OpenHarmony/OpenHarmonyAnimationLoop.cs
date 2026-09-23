// Shared frame-driven animation loop for the OpenHarmony compositor.
//
// The self-drawn route has no per-view timers: OpenHarmonyBridge.Frame is the vsync-aligned
// callback the app host already renders from, so frame-driven effects (scroll flings, the
// scrollbar auto-hide fade) register here instead of owning their own timers. The loop
// reuses that frame path: it subscribes to OpenHarmonyBridge.Frame lazily when the first
// animation registers, steps every registered animation once per tick, and only asks the
// host for a redraw while at least one animation reports NeedsRedraw. When the last
// animation finishes the loop unsubscribes, so an idle UI never ticks or redraws - the
// delegate list is empty and OpenHarmonyBridge.Frame has no subscriber from this class.
//
// Register/Unregister may be called from the touch thread while the frame callback pumps on
// the render thread, so the animation list is guarded by a lock. The list is snapshotted under
// it and each Step runs outside it, so an effect that starts/cancels another one (the scroll
// physics on a press/fling) can never deadlock against the frame thread; the redraw request is
// made after the lock is released.
using Microsoft.OpenHarmony.Hosting;
using System.Buffers;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One frame-driven effect. <see cref="Step"/> is called once per platform frame (or once per
/// manual <see cref="OpenHarmonyAnimationLoop.Pump"/>) while the animation is registered;
/// returning false unregisters it. <see cref="NeedsRedraw"/> tells the loop whether the last
/// step changed what is on screen (tracking-only animations keep the loop alive without
/// forcing a repaint).
/// </summary>
internal interface IOpenHarmonyAnimation
{
    bool Step(long nowMs, float dtSeconds);

    bool NeedsRedraw { get; }
}

/// <summary>
/// The slice's single animation ticker: frame-driven, idle-stopping, opt-out for tests.
/// </summary>
internal static class OpenHarmonyAnimationLoop
{
    private static readonly object s_sync = new();
    private static readonly List<IOpenHarmonyAnimation> s_animations = new();
    private static bool s_subscribed;
    private static long s_lastTickMs;
    private static bool s_enabled = true;

    /// <summary>
    /// Opt-out knob. False makes every frame-driven effect inert (registrations are dropped)
    /// and unsubscribes the loop, so the slice keeps the previous static behaviour.
    /// </summary>
    internal static bool Enabled
    {
        get => s_enabled;
        set
        {
            s_enabled = value;
            if (!value)
            {
                Stop();
            }
        }
    }

    /// <summary>Time source in milliseconds; tests replace it to step deterministically.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    internal static long NowMs => Clock();

    /// <summary>True while registered animations are being ticked (false when idle).</summary>
    internal static bool IsRunning
    {
        get
        {
            lock (s_sync)
            {
                return s_subscribed && s_animations.Count > 0;
            }
        }
    }

    internal static int ActiveCount
    {
        get
        {
            lock (s_sync)
            {
                return s_animations.Count;
            }
        }
    }

    /// <summary>Steps pumped since load (bookkeeping for tests/diagnostics).</summary>
    internal static int Ticks { get; private set; }

    /// <summary>Redraw requests issued while animations were active.</summary>
    internal static int RedrawRequests { get; private set; }

    /// <summary>Starts ticking <paramref name="animation"/> (idempotent; subscribes on first use).</summary>
    internal static void Register(IOpenHarmonyAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (!Enabled)
        {
            return;
        }
        lock (s_sync)
        {
            if (s_animations.Contains(animation))
            {
                return;
            }
            s_animations.Add(animation);
            s_lastTickMs = NowMs;
            if (!s_subscribed)
            {
                s_subscribed = true;
                OpenHarmonyBridge.Frame += OnFrame;
            }
        }
    }

    /// <summary>Stops ticking <paramref name="animation"/> and unsubscribes when the loop empties.</summary>
    internal static void Unregister(IOpenHarmonyAnimation animation)
    {
        lock (s_sync)
        {
            s_animations.Remove(animation);
            StopIfIdleLocked();
        }
    }

    /// <summary>Drops every animation and unsubscribes (loop off / tests).</summary>
    internal static void Stop()
    {
        lock (s_sync)
        {
            s_animations.Clear();
            StopIfIdleLocked();
        }
    }

    /// <summary>
    /// Steps every registered animation once. The frame handler passes the current time and 0
    /// (the loop derives dt from its own clock); tests pass both explicitly.
    /// </summary>
    internal static void Pump(long nowMs, float dtSeconds)
    {
        if (!Enabled)
        {
            return;
        }
        // Snapshot: Step runs outside the loop lock, so an animation that registers or cancels
        // another one (the scroll physics does when a fling starts/stops) can never deadlock
        // against the frame thread. Registrations made during a tick start on the next one. The
        // snapshot is a pooled copy instead of ToArray(): ticking an animation must not allocate.
        IOpenHarmonyAnimation[] batch;
        int count;
        lock (s_sync)
        {
            if (s_animations.Count == 0)
            {
                StopIfIdleLocked();
                return;
            }
            if (dtSeconds <= 0f)
            {
                dtSeconds = (nowMs - s_lastTickMs) / 1000f;
            }
            if (dtSeconds <= 0f || dtSeconds > 0.25f)
            {
                // First tick after a pause (or a wildly late frame): use the nominal frame.
                dtSeconds = 1f / 60f;
            }
            s_lastTickMs = nowMs;
            Ticks++;
            count = s_animations.Count;
            batch = ArrayPool<IOpenHarmonyAnimation>.Shared.Rent(count);
            s_animations.CopyTo(batch, 0);
        }
        bool requestRedraw = false;
        List<IOpenHarmonyAnimation>? finished = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                IOpenHarmonyAnimation animation = batch[i];
                bool keep;
                try
                {
                    keep = animation.Step(nowMs, dtSeconds);
                }
                catch
                {
                    // A broken effect must not take the frame callback down with it.
                    keep = false;
                }
                if (keep)
                {
                    requestRedraw |= animation.NeedsRedraw;
                }
                else
                {
                    (finished ??= new List<IOpenHarmonyAnimation>()).Add(animation);
                }
            }
        }
        finally
        {
            Array.Clear(batch, 0, count);
            ArrayPool<IOpenHarmonyAnimation>.Shared.Return(batch);
        }
        lock (s_sync)
        {
            if (finished is not null)
            {
                foreach (IOpenHarmonyAnimation animation in finished)
                {
                    s_animations.Remove(animation);
                }
            }
            StopIfIdleLocked();
            if (requestRedraw)
            {
                // The next platform frame repaints; the app host's RedrawRequested handler
                // marks it dirty. Only animated steps ask for this, never idle tracking.
                RedrawRequests++;
            }
        }
        if (requestRedraw)
        {
            OpenHarmonyBridge.RequestRedraw();
        }
    }

    private static void StopIfIdleLocked()
    {
        if (s_animations.Count > 0 || !s_subscribed)
        {
            return;
        }
        s_subscribed = false;
        OpenHarmonyBridge.Frame -= OnFrame;
    }

    private static void OnFrame(OpenHarmonyFrameEventArgs args) => Pump(NowMs, 0f);
}
