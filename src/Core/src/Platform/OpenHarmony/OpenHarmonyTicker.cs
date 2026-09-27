// Ticker for MAUI's animation manager, aligned with the self-drawn compositor.
//
// MAUI's AnimationManager samples every animation from one ITicker.Fire at a fixed cadence and
// reads the elapsed time from Environment.TickCount itself, so a platform timer is the right
// sampling source. What the timer alone cannot do is paint: the compositor only repaints a
// surface that was marked dirty (OpenHarmonyMauiAppHost.Render runs when RedrawRequested set
// the flag, or a frame callback finds a running activity indicator), so a FadeTo that only
// changes view properties would sit unpainted until the next touch.
//
// The ticker therefore keeps a frame-loop registration alive for as long as it runs: every
// platform frame requests a redraw, and the loop unsubscribes again when the ticker stops, so
// an idle UI still costs nothing. Sampling direction stays timer -> AnimationManager -> view
// properties; repaint direction becomes frame -> RequestRedraw -> Render.
using Microsoft.Maui.Animations;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyTicker : ITicker
{
    private Timer? _timer;
    private bool _systemEnabled = true;

    // One cached driver per ticker: registering with the frame loop allocates nothing per frame.
    private readonly RedrawDriver _driver;

    public OpenHarmonyTicker()
    {
        _driver = new RedrawDriver(this);
    }

    public Action? Fire { get; set; }

    public bool IsRunning { get; private set; }

    public int MaxFps { get; set; } = 60;

    /// <summary>
    /// True while animations may run. The device's "reduce animations" setting (forwarded by
    /// the ArkTS shell through OpenHarmonyMotion) makes this false; AnimationManager then drops
    /// new animations and force-finishes the running ones on the next fire.
    /// </summary>
    public bool SystemEnabled
    {
        get => _systemEnabled && !OpenHarmonyMotion.ReduceMotion;
        set => _systemEnabled = value;
    }

    public void Start()
    {
        if (IsRunning || !SystemEnabled)
        {
            return;
        }
        IsRunning = true;
        int period = Math.Max(1, 1000 / Math.Max(1, MaxFps));
        _timer = new Timer(_ => Fire?.Invoke(), null, period, period);
        // Keep the surface dirty while the manager samples: this is the repaint half of the
        // alignment (the timer above stays the sampling half).
        OpenHarmonyAnimationLoop.Register(_driver);
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
        OpenHarmonyAnimationLoop.Unregister(_driver);
    }

    /// <summary>
    /// Frame-loop registration that only asks for repaints. Step returns the ticker's running
    /// state so a driver left registered by a missed Stop retires on the next frame instead of
    /// keeping the loop alive forever.
    /// </summary>
    private sealed class RedrawDriver : IOpenHarmonyAnimation
    {
        private readonly OpenHarmonyTicker _ticker;

        internal RedrawDriver(OpenHarmonyTicker ticker)
        {
            _ticker = ticker;
        }

        public bool NeedsRedraw => true;

        public bool Step(long nowMs, float dtSeconds) => _ticker.IsRunning;
    }
}
