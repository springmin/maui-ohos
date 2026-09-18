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
    public bool IsEnabled => false;

    public bool IsListeningForeground => false;

    public Task<Location?> GetLastKnownLocationAsync() => Task.FromResult<Location?>(null);

    public Task<Location?> GetLocationAsync(GeolocationRequest request, CancellationToken cancellationToken = default)
        => throw new FeatureNotSupportedException("Geolocation needs the location kit in the ArkTS shell");

    public Task<bool> StartListeningForegroundAsync(GeolocationListeningRequest request)
        => Task.FromResult(false);

    public void StopListeningForeground()
    {
    }

    public event EventHandler<GeolocationLocationChangedEventArgs>? LocationChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<GeolocationListeningFailedEventArgs>? ListeningFailed
    {
        add { }
        remove { }
    }
}

public sealed class OpenHarmonyFilePicker : IFilePicker
{
    public Task<FileResult?> PickAsync(PickOptions? options = null)
        => throw new FeatureNotSupportedException("File picking needs the picker kit in the ArkTS shell");

    public Task<IEnumerable<FileResult>> PickMultipleAsync(PickOptions? options = null)
        => throw new FeatureNotSupportedException("File picking needs the picker kit in the ArkTS shell");
}

public sealed class OpenHarmonyMediaPicker : IMediaPicker
{
    public bool IsCaptureSupported => false;

    public Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Media capture needs the camera kit in the ArkTS shell");

    public Task<FileResult?> CaptureVideoAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Media capture needs the camera kit in the ArkTS shell");

    public Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Photo picking needs the photoAccessHelper kit in the ArkTS shell");

    public Task<FileResult?> PickVideoAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Video picking needs the photoAccessHelper kit in the ArkTS shell");

    public Task<List<FileResult>> PickPhotosAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Photo picking needs the photoAccessHelper kit in the ArkTS shell");

    public Task<List<FileResult>> PickVideosAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Video picking needs the photoAccessHelper kit in the ArkTS shell");
}
