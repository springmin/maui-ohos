// Ticker for MAUI's animation manager: drives AnimationManager.OnFire from a platform timer
// (the app host's frame callbacks stay the rendering source of truth).
using Microsoft.Maui.Animations;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyTicker : ITicker
{
    private Timer? _timer;

    public Action? Fire { get; set; }

    public bool IsRunning { get; private set; }

    public int MaxFps { get; set; } = 60;

    public bool SystemEnabled { get; set; } = true;

    public void Start()
    {
        if (IsRunning || !SystemEnabled)
        {
            return;
        }
        IsRunning = true;
        int period = Math.Max(1, 1000 / Math.Max(1, MaxFps));
        _timer = new Timer(_ => Fire?.Invoke(), null, period, period);
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
    }
}
