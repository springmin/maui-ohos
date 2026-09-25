// IAppInfo / IDeviceInfo / IVersionTracking for OpenHarmony.
using System.Runtime.InteropServices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The HAP bundle metadata the ArkTS shell publishes once at page load
/// (bundleManager.getBundleInfoForSelfSync -> host.setBundleInfo) and the native host stores.
/// Each read is guarded: a desktop build without libopenharmonyhost.so (or an older host
/// library without the export) reports null and the callers keep their documented fallbacks.
/// </summary>
internal static partial class OpenHarmonyBundleInfoBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_get_bundle_version")]
    private static partial IntPtr GetVersionNative();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_get_bundle_build")]
    private static partial IntPtr GetBuildNative();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_get_bundle_name")]
    private static partial IntPtr GetNameNative();

    private static bool s_unavailable;

    internal static string? Version => Read(GetVersionNative);
    internal static string? Build => Read(GetBuildNative);
    internal static string? Name => Read(GetNameNative);

    private static string? Read(Func<IntPtr> getter)
    {
        if (s_unavailable)
        {
            return null;
        }
        try
        {
            IntPtr value = getter();
            string text = value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return null;
        }
    }
}

/// <summary>
/// Opens the system settings app (IAppInfo.ShowSettingsUI) through the shared ability-start host
/// entry point with kind 4 (uri = bundle name, text = ability name). The ArkTS shell tries the
/// explicit Want (com.ohos.settings / com.ohos.settings.MainAbility) first and falls back to the
/// implicit 'ohos.settings' action; false means neither the host library nor the shell sink
/// answered, and the caller logs the documented no-op.
/// </summary>
internal static partial class OpenHarmonySettingsBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Ability kind: explicit Want carried as (uri = bundle name, text = ability name).</summary>
    private const int KindSettings = 4;

    private const string SettingsBundle = "com.ohos.settings";
    private const string SettingsAbility = "com.ohos.settings.MainAbility";

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_ability_start", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AbilityStart(int kind, string uri, string text);

    private static bool s_unavailable;

    internal static bool ShowSettings()
    {
        if (s_unavailable)
        {
            return false;
        }
        try
        {
            return AbilityStart(KindSettings, SettingsBundle, SettingsAbility) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return false;
        }
    }
}

public sealed class OpenHarmonyAppInfo : IAppInfo
{
    /// <summary>The documented package-name fallback when the host has none (desktop builds).</summary>
    internal const string FallbackPackageName = "com.example.app";

    /// <summary>The documented version fallback (the pre-bridge constant).</summary>
    internal const string FallbackVersion = "1.0.0";

    /// <summary>
    /// The HAP's bundleName: the live bundle info published by the shell (host.setBundleInfo)
    /// first, then the context bridge (OpenHarmonyBridge.Context.BundleName), then the
    /// documented fallback.
    /// </summary>
    public string PackageName => OpenHarmonyBundleInfoBridge.Name ?? OpenHarmonyBridge.Context?.BundleName ?? FallbackPackageName;

    /// <summary>
    /// OpenHarmony's app label lives in the HAP resources; the bundle name is the only app
    /// identity the bridge exposes, so it is used here too (context first, bundle info second).
    /// </summary>
    public string Name => OpenHarmonyBridge.Context?.BundleName ?? OpenHarmonyBundleInfoBridge.Name ?? "OpenHarmony app";

    /// <summary>
    /// The HAP's versionName (module.json5 versionName), read from the bundle manager by the
    /// shell and stored by the native host; the documented constant is kept as the fallback when
    /// the host library or the shell publish is unavailable (desktop builds, older shells).
    /// </summary>
    public string VersionString => OpenHarmonyBundleInfoBridge.Version ?? FallbackVersion;

    /// <summary>Parsed from <see cref="VersionString"/>; the fallback is the documented 1.0.0.</summary>
    public Version Version =>
        System.Version.TryParse(VersionString, out System.Version? parsed) ? parsed : new Version(1, 0, 0);

    /// <summary>
    /// The HAP's versionCode as text, published by the shell; falls back to
    /// <see cref="VersionString"/> when the host has no build value.
    /// </summary>
    public string BuildString => OpenHarmonyBundleInfoBridge.Build ?? VersionString;

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
    /// Opens the system settings app through the shell: kind 4 carries the explicit
    /// com.ohos.settings Want and the shell falls back to the implicit 'ohos.settings' action
    /// when it does not resolve. A failed dispatch (no host library, no shell sink) is logged
    /// and otherwise a no-op, like every unavailable platform path in this slice.
    /// </summary>
    public void ShowSettingsUI()
    {
        if (!OpenHarmonySettingsBridge.ShowSettings())
        {
            OpenHarmonyBridge.WriteStatus("[maui] settings UI could not be dispatched");
        }
    }

    /// <summary>
    /// Same as <see cref="ShowSettingsUI()"/> with the page name ignored: this slice's ability
    /// bridge carries one bundle/ability pair per launch and OpenHarmony settings sub-pages are
    /// addressed with app-specific URIs the slice cannot map, so the settings home is opened.
    /// </summary>
    public void ShowSettingsUI(string page)
    {
        ShowSettingsUI();
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
