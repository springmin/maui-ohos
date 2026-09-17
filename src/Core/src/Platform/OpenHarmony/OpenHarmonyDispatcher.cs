// MAUI dispatcher for OpenHarmony.
//
// The ArkTS shell owns the platform UI thread; managed rendering runs on the app's own
// threads and is driven by the XComponent frame callback (OpenHarmonyBridge.Frame). Work
// posted through this dispatcher is therefore queued and drained on frame ticks, with a
// safety-net timer so nothing starves when no frame is being produced.
using System.Collections.Concurrent;
using Microsoft.Maui.Dispatching;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyDispatcher : IDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Timer _safetyNet;
    private readonly int _drainThreadId0 = Environment.CurrentManagedThreadId;
    private volatile int _drainThreadId;

    public OpenHarmonyDispatcher()
    {
        OpenHarmonyBridge.Frame += _ => Drain();
        _safetyNet = new Timer(_ => Drain(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
    }

    public bool IsDispatchRequired => Environment.CurrentManagedThreadId != _drainThreadId;

    public bool Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
        return true;
    }

    public bool DispatchDelayed(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            Dispatch(action);
            timer?.Dispose();
        }, null, delay, Timeout.InfiniteTimeSpan);
        return true;
    }

    public IDispatcherTimer CreateTimer()
    {
        var timer = new OpenHarmonyDispatcherTimer(this);
        _timers.Add(timer);
        return timer;
    }

    private readonly List<OpenHarmonyDispatcherTimer> _timers = new();

    private void Drain()
    {
        _drainThreadId = Environment.CurrentManagedThreadId;
        while (_queue.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                OpenHarmonyBridge.WriteStatus($"dispatcher action failed: {e.GetType().Name}: {e.Message}");
            }
        }
        long now = Environment.TickCount64;
        foreach (OpenHarmonyDispatcherTimer timer in _timers.ToArray())
        {
            timer.TickOnFrame(now);
        }
    }
}

internal sealed class OpenHarmonyDispatcherTimer : IDispatcherTimer
{
    private readonly OpenHarmonyDispatcher _dispatcher;
    private long _nextTick;
    private bool _running;

    public OpenHarmonyDispatcherTimer(OpenHarmonyDispatcher dispatcher) => _dispatcher = dispatcher;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(16);
    public bool IsRepeating { get; set; } = true;
    public bool IsRunning => _running;

    public event EventHandler? Tick;

    public void Start()
    {
        _running = true;
        _nextTick = Environment.TickCount64 + (long)Interval.TotalMilliseconds;
    }

    public void Stop() => _running = false;

    internal void TickOnFrame(long now)
    {
        if (!_running || now < _nextTick)
        {
            return;
        }
        _nextTick = now + Math.Max(1, (long)Interval.TotalMilliseconds);
        Tick?.Invoke(this, EventArgs.Empty);
        if (!IsRepeating)
        {
            _running = false;
        }
    }
}
