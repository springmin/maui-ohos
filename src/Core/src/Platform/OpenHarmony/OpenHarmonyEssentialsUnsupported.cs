// Deterministic behaviour for the Essentials APIs whose ArkTS kits are not wired yet: instead
// of an unresolved-service exception, apps get documented results (no-op, denied, or a clear
// FeatureNotSupportedException). Each one is replaced by a real bridge as the kits land:
// IPermissions.RequestAsync now runs through OpenHarmonyPermissionBridge
// (abilityAccessCtrl.requestPermissionsFromUser in the shell) and keeps the documented Denied
// answer when the shell or the host library is unavailable. IPermissions covers the full MAUI
// permission set (every nested type of Permissions in Microsoft.Maui.Essentials rc.1) against
// the permission names declared by the installed OpenHarmony SDK (ets/api/permissions.d.ts and
// toolchains/lib/PermissionDefinitions.json); the one type the permission model cannot answer
// (PostNotifications) is documented in the map below.
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
    /// <summary>
    /// MAUI permission types mapped to the OpenHarmony permission names that gate the same
    /// capability, read from the installed SDK's permission list
    /// (ets/api/permissions.d.ts + toolchains/lib/PermissionDefinitions.json, SDK 26.0.0.18).
    /// An empty array means "not gated by abilityAccessCtrl" (check/request answer Granted); a
    /// type absent from the map is unmappable and answers Unknown/Denied. The MAUI version in
    /// this repo has no separate BluetoothScan or PhoneCall types (scanning/connecting share
    /// Permissions.Bluetooth, calls share Permissions.Phone), so 27 nested types are covered.
    /// </summary>
    private static readonly Dictionary<Type, string[]> PermissionNames = new()
    {
        // @ohos.batteryInfo is an open API: no permission to check or prompt for.
        [typeof(Permissions.Battery)] = Array.Empty<string>(),
        // ACCESS_BLUETOOTH is normal/user_grant and covers discovery/connection (@ohos.bluetooth.*).
        [typeof(Permissions.Bluetooth)] = new[] { "ohos.permission.ACCESS_BLUETOOTH" },
        [typeof(Permissions.CalendarRead)] = new[] { "ohos.permission.READ_CALENDAR" },
        [typeof(Permissions.CalendarWrite)] = new[] { "ohos.permission.WRITE_CALENDAR" },
        [typeof(Permissions.Camera)] = new[] { "ohos.permission.CAMERA" },
        // READ_CONTACTS/WRITE_CONTACTS are system_basic/user_grant in this SDK.
        [typeof(Permissions.ContactsRead)] = new[] { "ohos.permission.READ_CONTACTS" },
        [typeof(Permissions.ContactsWrite)] = new[] { "ohos.permission.WRITE_CONTACTS" },
        // cameraManager.setTorchMode carries no @permission annotation in SDK 26.0.0.18, but
        // MAUI's Permissions.Flashlight includes the camera runtime permission on Android and
        // the torch is a Camera Kit feature, so CAMERA is the closest documented gate.
        [typeof(Permissions.Flashlight)] = new[] { "ohos.permission.CAMERA" },
        // Foreground ability starts need no permission (Android's LaunchApp has no runtime
        // permission either; it checks package visibility, which OpenHarmony does not gate).
        [typeof(Permissions.LaunchApp)] = Array.Empty<string>(),
        // APPROXIMATELY_LOCATION is the coarse gate; LOCATION is the precise one.
        [typeof(Permissions.LocationWhenInUse)] = new[] { "ohos.permission.APPROXIMATELY_LOCATION" },
        // "Always" needs foreground + background location: check both, prompt in order.
        [typeof(Permissions.LocationAlways)] = new[] { "ohos.permission.LOCATION", "ohos.permission.LOCATION_IN_BACKGROUND" },
        [typeof(Permissions.Maps)] = new[] { "ohos.permission.LOCATION" },
        // MAUI's Media permission is audio read; @ohos.multimedia.media declares READ_MEDIA.
        [typeof(Permissions.Media)] = new[] { "ohos.permission.READ_MEDIA" },
        [typeof(Permissions.Microphone)] = new[] { "ohos.permission.MICROPHONE" },
        // Android's NearbyWifiDevices gates Wi-Fi scanning; @ohos.wifiManager scan APIs declare
        // GET_WIFI_INFO (normal/system_grant, no prompt).
        [typeof(Permissions.NearbyWifiDevices)] = new[] { "ohos.permission.GET_WIFI_INFO" },
        [typeof(Permissions.NetworkState)] = new[] { "ohos.permission.GET_NETWORK_INFO" },
        // Closest telephony-state name (system_basic/system_grant on this SDK).
        [typeof(Permissions.Phone)] = new[] { "ohos.permission.GET_TELEPHONY_STATE" },
        [typeof(Permissions.Photos)] = new[] { "ohos.permission.READ_IMAGEVIDEO" },
        [typeof(Permissions.PhotosAddOnly)] = new[] { "ohos.permission.WRITE_IMAGEVIDEO" },
        // Android ships no runtime permission for Reminders either; OpenHarmony has no dedicated
        // reminders permission (reminder entries live in the calendar), so nothing to gate.
        [typeof(Permissions.Reminders)] = Array.Empty<string>(),
        // Android gates BODY_SENSORS here; ACCELEROMETER/GYROSCOPE are system_grant in this SDK,
        // so the closest user-grant sensor permission is READ_HEALTH_DATA.
        [typeof(Permissions.Sensors)] = new[] { "ohos.permission.READ_HEALTH_DATA" },
        [typeof(Permissions.Sms)] = new[] { "ohos.permission.SEND_MESSAGES" },
        // The SDK ships no speech recognizer; the microphone is the only gate on its input.
        [typeof(Permissions.Speech)] = new[] { "ohos.permission.MICROPHONE" },
        [typeof(Permissions.StorageRead)] = new[] { "ohos.permission.READ_IMAGEVIDEO" },
        [typeof(Permissions.StorageWrite)] = new[] { "ohos.permission.WRITE_IMAGEVIDEO" },
        [typeof(Permissions.Vibrate)] = new[] { "ohos.permission.VIBRATE" },
        // UNMAPPABLE, kept out of the map on purpose: Permissions.PostNotifications has no
        // abilityAccessCtrl counterpart - OpenHarmony does not gate notification publishing
        // behind a permission (the per-app notification switch is
        // notificationManager.requestEnableNotification, a system dialog, and
        // NOTIFICATION_CONTROLLER is system_core). CheckStatusAsync answers Unknown and
        // RequestAsync answers Denied for it, documented instead of pretending a permission.
    };

    public Task<PermissionStatus> CheckStatusAsync<TPermission>() where TPermission : Permissions.BasePermission, new()
    {
        if (!PermissionNames.TryGetValue(typeof(TPermission), out string[]? names))
        {
            // Unmappable MAUI permission (see PostNotifications above).
            return Task.FromResult(PermissionStatus.Unknown);
        }
        foreach (string name in names)
        {
            if (!OpenHarmonyBridge.CheckSelfPermission(name))
            {
                return Task.FromResult(PermissionStatus.Denied);
            }
        }
        return Task.FromResult(PermissionStatus.Granted);
    }

    public async Task<PermissionStatus> RequestAsync<TPermission>() where TPermission : Permissions.BasePermission, new()
    {
        if (!PermissionNames.TryGetValue(typeof(TPermission), out string[]? names))
        {
            // Unmappable MAUI permission: keep the documented Denied answer (CheckStatusAsync
            // reports Unknown for the same input).
            return PermissionStatus.Denied;
        }
        foreach (string name in names)
        {
            if (OpenHarmonyBridge.CheckSelfPermission(name))
            {
                continue;
            }
            PermissionStatus status = await RequestMappedAsync(name).ConfigureAwait(false);
            if (status != PermissionStatus.Granted)
            {
                // A denied/non-answerable entry fails the whole permission; multi-name entries
                // (LocationAlways) stop here instead of prompting for the rest.
                return status;
            }
        }
        return PermissionStatus.Granted;
    }

    /// <summary>
    /// Prompts for one mapped OpenHarmony permission through the shell (abilityAccessCtrl) and
    /// maps the answer to MAUI's status. A timeout, a missing shell sink or a missing host
    /// library answers Denied, exactly like the pre-bridge no-op did.
    /// </summary>
    private static async Task<PermissionStatus> RequestMappedAsync(string name)
    {
        bool? granted = await OpenHarmonyPermissionBridge
            .RequestAsync(name, OpenHarmonyPermissionBridge.RequestTimeout)
            .ConfigureAwait(false);
        if (granted is null)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] permission request for {name} was not answered; denying");
            return PermissionStatus.Denied;
        }
        return granted.Value ? PermissionStatus.Granted : PermissionStatus.Denied;
    }

    /// <summary>
    /// OpenHarmony's abilityAccessCtrl has no should-show-rationale query: there is no
    /// counterpart to Android's shouldShowRequestPermissionRationale (the request result's
    /// dialogShownResults is only known from a completed request and is not carried by the host
    /// bridge). The documented constant false is kept; MAUI guidance still applies - explain
    /// before requesting and send the user to Settings after a denial.
    /// </summary>
    public bool ShouldShowRationale<TPermission>() where TPermission : Permissions.BasePermission, new()
        => false;

    /// <summary>
    /// No-op by design: permissions are declared once in the HAP's module.json5
    /// requestPermissions, and the SDK reads them back with
    /// bundleManager.getBundleInfoForSelf(GET_BUNDLE_INFO_WITH_REQUESTED_PERMISSION) - an ArkTS
    /// API with no export on the host bridge, so the managed side cannot inspect declarations.
    /// Requesting a permission that was not declared still fails through the normal Denied path.
    /// </summary>
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

