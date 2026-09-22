// IAppInfo / IDeviceInfo / IVersionTracking for OpenHarmony.
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyAppInfo : IAppInfo
{
    /// <summary>
    /// The HAP's bundleName, reported by the host (OpenHarmonyBridge.Context.BundleName). The
    /// fallback only appears when the host library is absent (desktop builds).
    /// </summary>
    public string PackageName => OpenHarmonyBridge.Context?.BundleName ?? "com.example.app";

    /// <summary>
    /// OpenHarmony's app label lives in the HAP resources; the bundle name is the only app
    /// identity the host bridge exposes, so it is used here too.
    /// </summary>
    public string Name => OpenHarmonyBridge.Context?.BundleName ?? "OpenHarmony app";

    // VersionString/Version/BuildString: the version is declared in the HAP's module.json5 and
    // the SDK reads it with bundleManager.getBundleInfoForSelf (an ArkTS API). The host bridge
    // has no bundle-info export yet, so these stay the documented constants until one is added
    // (the same shape as the shell's bundle manager would need: ohos_host_bundle_version).
    public string VersionString => "1.0.0";

    public Version Version => new(1, 0, 0);

    public string BuildString => VersionString;

    /// <summary>
    /// The OS colour mode last reported by the ArkTS shell through the theme bridge
    /// (Environment.envProp 'colorMode' -> host.notifyTheme -> OpenHarmonyTheme.LastIsDark).
    /// Unspecified until the shell reports the first mode, matching MAUI's "no platform theme".
    /// </summary>
    public AppTheme RequestedTheme => OpenHarmonyTheme.LastIsDark switch
    {
        true => AppTheme.Dark,
        false => AppTheme.Light,
        _ => AppTheme.Unspecified,
    };

    public AppPackagingModel PackagingModel => AppPackagingModel.Packaged;

    public LayoutDirection RequestedLayoutDirection => LayoutDirection.LeftToRight;

    /// <summary>
    /// Documented no-op: OpenHarmony opens app settings through an explicit startAbility Want
    /// with bundleName com.ohos.settings + its MainAbility (there is no settings: URI handler),
    /// and this slice's ability bridge only carries URI/action Wants. Needs a new host/shell
    /// ability kind that starts an explicit bundleName/abilityName Want (reported, no shell
    /// change in this increment).
    /// </summary>
    public void ShowSettingsUI()
    {
    }

    /// <inheritdoc cref="ShowSettingsUI()"/>
    public void ShowSettingsUI(string page)
    {
    }
}

public sealed class OpenHarmonyDeviceInfo : IDeviceInfo
{
    // Model/Manufacturer/Name: the host bridge exposes no product/device builder API, so these
    // keep the runtime's values (the machine name on desktop; on device the kernel host name).
    public string Model => Environment.MachineName;

    public string Manufacturer => "OpenHarmony";

    public string Name => Environment.MachineName;

    // VersionString/Version: the OS the process runs on (the device kernel on device).
    public string VersionString => Environment.OSVersion.VersionString;

    public Version Version => Environment.OSVersion.Version;

    /// <summary>
    /// MAUI ships no DevicePlatform.OpenHarmony value (the struct only defines Android, iOS,
    /// macOS, MacCatalyst, tvOS, Tizen, UWP, WinUI, watchOS and Unknown), so the platform is
    /// reported as Unknown with this note until MAUI adds one.
    /// </summary>
    public DevicePlatform Platform => DevicePlatform.Unknown;

    /// <summary>
    /// Classified from the shell's display info (the OHNativeWindow surface size delivered by
    /// SurfaceChanged): a short side of at least 1200 px is treated as a tablet, anything
    /// smaller as a phone; with no surface (desktop/headless) the desktop idiom is kept. The
    /// bridge exposes no display density, so the threshold is pixels - documented as a heuristic.
    /// </summary>
    public DeviceIdiom Idiom
    {
        get
        {
            if (OpenHarmonyBridge.Surface is not { Width: > 0, Height: > 0 } surface)
            {
                return DeviceIdiom.Desktop;
            }
            return Math.Min(surface.Width, surface.Height) >= 1200 ? DeviceIdiom.Tablet : DeviceIdiom.Phone;
        }
    }

    /// <summary>
    /// Physical (documented constant): HAPs are installed on devices and the bridge has no
    /// emulator/virtual-device probe, so a virtual device is not distinguishable here.
    /// </summary>
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
