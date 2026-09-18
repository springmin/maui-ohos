// IAppInfo / IDeviceInfo / IVersionTracking for OpenHarmony.
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyAppInfo : IAppInfo
{
    public string PackageName => OpenHarmonyBridge.Context?.BundleName ?? "com.example.app";

    public string Name => OpenHarmonyBridge.Context?.BundleName ?? "OpenHarmony app";

    public string VersionString => "1.0.0";

    public Version Version => new(1, 0, 0);

    public string BuildString => VersionString;

    public AppTheme RequestedTheme => AppTheme.Unspecified;

    public AppPackagingModel PackagingModel => AppPackagingModel.Packaged;

    public LayoutDirection RequestedLayoutDirection => LayoutDirection.LeftToRight;

    public void ShowSettingsUI()
    {
    }

    public void ShowSettingsUI(string page)
    {
    }
}

public sealed class OpenHarmonyDeviceInfo : IDeviceInfo
{
    public string Model => Environment.MachineName;

    public string Manufacturer => "OpenHarmony";

    public string Name => Environment.MachineName;

    public string VersionString => Environment.OSVersion.VersionString;

    public Version Version => Environment.OSVersion.Version;

    public DevicePlatform Platform => DevicePlatform.Unknown;

    public DeviceIdiom Idiom => DeviceIdiom.Desktop;

    public DeviceType DeviceType => DeviceType.Physical;
}

public sealed class OpenHarmonyVersionTracking : IVersionTracking
{
    private const string LastVersionKey = "versionTracking.last";
    private const string LaunchesKey = "versionTracking.launches";
    private const string FirstLaunchKey = "versionTracking.first";

    private readonly IPreferences _preferences;

    public OpenHarmonyVersionTracking(IPreferences preferences)
    {
        _preferences = preferences;
    }

    public bool IsFirstLaunchEver => !_preferences.ContainsKey(FirstLaunchKey);

    public bool IsFirstLaunchForVersion => _preferences.Get(LastVersionKey, string.Empty) != CurrentVersion;

    public bool IsFirstLaunchForBuild => IsFirstLaunchForVersion;

    public string CurrentVersion => new OpenHarmonyAppInfo().VersionString;

    public string CurrentBuild => CurrentVersion;

    public string PreviousVersion => _preferences.Get(LastVersionKey, string.Empty);

    public string PreviousBuild => PreviousVersion;

    public string FirstInstalledVersion => _preferences.Get("versionTracking.firstVersion", CurrentVersion);

    public string FirstInstalledBuild => FirstInstalledVersion;

    public IReadOnlyList<string> VersionHistory => new[] { CurrentVersion };

    public IReadOnlyList<string> BuildHistory => VersionHistory;

    public bool IsFirstLaunchForCurrentVersion => IsFirstLaunchForVersion;

    public bool IsFirstLaunchForCurrentBuild => IsFirstLaunchForVersion;

    // Parameterised overloads are explicit: the interface also exposes nameless properties.
    bool Microsoft.Maui.ApplicationModel.IVersionTracking.IsFirstLaunchForVersion(string version)
        => _preferences.Get(LastVersionKey, string.Empty) != version;

    bool Microsoft.Maui.ApplicationModel.IVersionTracking.IsFirstLaunchForBuild(string build)
        => _preferences.Get(LastVersionKey, string.Empty) != build;

    public void Track()
    {
        if (!_preferences.ContainsKey(FirstLaunchKey))
        {
            _preferences.Set("versionTracking.firstVersion", CurrentVersion);
        }
        _preferences.Set(FirstLaunchKey, true);
        _preferences.Set(LaunchesKey, _preferences.Get(LaunchesKey, 0) + 1);
        _preferences.Set(LastVersionKey, CurrentVersion);
    }
}
