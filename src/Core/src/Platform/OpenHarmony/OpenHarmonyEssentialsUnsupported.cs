// Deterministic behaviour for the Essentials APIs whose ArkTS kits are not wired yet: instead
// of an unresolved-service exception, apps get documented results (no-op, denied, or a clear
// FeatureNotSupportedException). Each one is replaced by a real bridge as the kits land.
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Media;
using Microsoft.Maui.Networking;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyVibration : IVibration
{
    /// <summary>Vibration goes through the platform NDK (OH_Vibrator_PlayVibration).</summary>
    public bool IsSupported => OpenHarmonyBridge.CheckSelfPermission("ohos.permission.VIBRATE");

    public void Vibrate()
    {
        if (!OpenHarmonyBridge.Vibrate(100))
        {
            OpenHarmonyBridge.WriteStatus("[maui] vibration unavailable (permission or host)");
        }
    }

    public void Vibrate(TimeSpan duration)
    {
        int ms = (int)Math.Clamp(duration.TotalMilliseconds, 1, 3000);
        OpenHarmonyBridge.Vibrate(ms);
    }

    public void Cancel()
    {
    }
}

public sealed class OpenHarmonyPermissions : IPermissions
{
    /// <summary>MAUI permission types mapped to OpenHarmony permission names.</summary>
    private static readonly Dictionary<Type, string> PermissionNames = new()
    {
        [typeof(Permissions.Camera)] = "ohos.permission.CAMERA",
        [typeof(Permissions.Microphone)] = "ohos.permission.MICROPHONE",
        [typeof(Permissions.LocationWhenInUse)] = "ohos.permission.APPROXIMATELY_LOCATION",
        [typeof(Permissions.LocationAlways)] = "ohos.permission.LOCATION",
        [typeof(Permissions.StorageRead)] = "ohos.permission.READ_IMAGEVIDEO",
        [typeof(Permissions.StorageWrite)] = "ohos.permission.WRITE_IMAGEVIDEO",
        [typeof(Permissions.Photos)] = "ohos.permission.READ_IMAGEVIDEO",
        [typeof(Permissions.Vibrate)] = "ohos.permission.VIBRATE",
        [typeof(Permissions.NetworkState)] = "ohos.permission.GET_NETWORK_INFO",
    };

    public Task<PermissionStatus> CheckStatusAsync<TPermission>() where TPermission : Permissions.BasePermission, new()
    {
        if (!PermissionNames.TryGetValue(typeof(TPermission), out string? name))
        {
            return Task.FromResult(PermissionStatus.Unknown);
        }
        return Task.FromResult(OpenHarmonyBridge.CheckSelfPermission(name)
            ? PermissionStatus.Granted
            : PermissionStatus.Denied);
    }

    public Task<PermissionStatus> RequestAsync<TPermission>() where TPermission : Permissions.BasePermission, new()
    {
        OpenHarmonyBridge.WriteStatus("[maui] permission requests need the abilityAccessCtrl kit in the ArkTS shell");
        return Task.FromResult(PermissionStatus.Denied);
    }

    public bool ShouldShowRationale<TPermission>() where TPermission : Permissions.BasePermission, new()
        => false;

    public void EnsureDeclared<TPermission>() where TPermission : Permissions.BasePermission, new()
    {
    }
}

public sealed class OpenHarmonyGeolocation : IGeolocation
{
    private static bool s_listening;

    /// <summary>Geolocation comes from the platform NDK (OH_Location_*).</summary>
    public bool IsEnabled => OpenHarmonyBridge.CheckSelfPermission("ohos.permission.APPROXIMATELY_LOCATION");

    public bool IsListeningForeground => s_listening;

    public Task<Location?> GetLastKnownLocationAsync()
        => Task.FromResult(ReadLocation());

    public async Task<Location?> GetLocationAsync(GeolocationRequest request, CancellationToken cancellationToken = default)
    {
        if (!OpenHarmonyBridge.StartLocation())
        {
            return null;
        }
        try
        {
            int timeoutMs = request?.Timeout is { TotalMilliseconds: > 0 } timeout ? (int)timeout.TotalMilliseconds : 10000;
            for (int waited = 0; waited < timeoutMs; waited += 200)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                if (ReadLocation() is { } location)
                {
                    return location;
                }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            return ReadLocation();
        }
        finally
        {
            OpenHarmonyBridge.StopLocation();
        }
    }

    public Task<bool> StartListeningForegroundAsync(GeolocationListeningRequest request)
    {
        if (!OpenHarmonyBridge.StartLocation())
        {
            return Task.FromResult(false);
        }
        s_listening = true;
        _ = Task.Run(async () =>
        {
            Location? previous = null;
            while (s_listening)
            {
                Location? current = ReadLocation();
                if (current is not null &&
                    (previous is null || current.Latitude != previous.Latitude || current.Longitude != previous.Longitude))
                {
                    previous = current;
                    s_locationChanged?.Invoke(this, new GeolocationLocationChangedEventArgs(current));
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
        });
        return Task.FromResult(true);
    }

    public void StopListeningForeground()
    {
        s_listening = false;
        OpenHarmonyBridge.StopLocation();
    }

    private static EventHandler<GeolocationLocationChangedEventArgs>? s_locationChanged;

    private static Location? ReadLocation()
        => OpenHarmonyBridge.TryGetLocation(out double latitude, out double longitude, out double altitude)
            ? new Location(latitude, longitude) { Altitude = altitude }
            : null;

    public event EventHandler<GeolocationLocationChangedEventArgs>? LocationChanged
    {
        add => s_locationChanged += value;
        remove => s_locationChanged -= value;
    }

    public event EventHandler<GeolocationListeningFailedEventArgs>? ListeningFailed
    {
        add { }
        remove { }
    }
}

