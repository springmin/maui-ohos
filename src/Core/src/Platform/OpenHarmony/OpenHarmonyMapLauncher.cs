// Essentials Map (Microsoft.Maui.ApplicationModel.IMap) for the OpenHarmony slice: opens the
// system map on a location or placemark through the existing startAbility bridge
// (OpenHarmonyAbilityBridge -> ohos_host_ability_start kind 0 -> the shell's implicit
// 'ohos.want.action.viewData' Want), so no new host export or shell sink is required.
//
// The URI is the geo: scheme the OpenHarmony ability manager matches to the installed map
// applications (the documented platform pattern for opening a map: viewData Want +
// 'geo:latitude,longitude?q=...'). The slice mirrors Android's URI shape, which is also what
// the OpenHarmony ecosystem samples use:
//   location:  geo:{lat},{lng}?q={lat},{lng}
//   placemark: geo:0,0?q={escaped address}   (Thoroughfare Locality AdminArea PostalCode
//                                              CountryName, Uri.EscapeDataString'd)
//   both append ({escaped name}) when MapLaunchOptions.Name is non-empty, like Android.
//
// Documented limits, mirroring the slice's no-silent-gaps rule:
//   * MapLaunchOptions.NavigationMode: the geo: scheme has no navigation-mode carrier on
//     OpenHarmony (Android's google.navigation: scheme and the vendor amap/baidu/petalmaps
//     URIs are not generic), so a non-None mode still opens the plain location and the
//     unrepresentable part is reported once per process through the status channel.
//   * TryOpenAsync reports whether the ability bridge dispatched the Want, not whether an
//     installed map application matched it: OpenHarmony has no synchronous URI-handler query
//     (the same limit OpenHarmonyLauncher.CanOpenAsync documents). Off-device (no host
//     library, no shell sink) every call answers false and OpenAsync completes without
//     throwing, exactly like Launcher/Browser.
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// MAUI Essentials map launching on OpenHarmony: opens the system map application on a
/// location or placemark with a <c>geo:</c> URI over the startAbility bridge.
/// </summary>
public sealed class OpenHarmonyMapLauncher : IMap
{
    /// <summary>The singleton installed as <see cref="Map.Default"/>.</summary>
    public static readonly OpenHarmonyMapLauncher Instance = new();

    /// <inheritdoc />
    public Task OpenAsync(double latitude, double longitude, MapLaunchOptions options)
    {
        string uri = GetMapsUri(latitude, longitude, options);
        NoteNavigationMode(options);
        Dispatch(uri, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OpenAsync(Placemark placemark, MapLaunchOptions options)
    {
        string uri = GetMapsUri(placemark, options);
        NoteNavigationMode(options);
        Dispatch(uri, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryOpenAsync(double latitude, double longitude, MapLaunchOptions options)
    {
        string uri = GetMapsUri(latitude, longitude, options);
        NoteNavigationMode(options);
        Dispatch(uri, out bool dispatched);
        return Task.FromResult(dispatched);
    }

    /// <inheritdoc />
    public Task<bool> TryOpenAsync(Placemark placemark, MapLaunchOptions options)
    {
        string uri = GetMapsUri(placemark, options);
        NoteNavigationMode(options);
        Dispatch(uri, out bool dispatched);
        return Task.FromResult(dispatched);
    }

    /// <summary>Installs this implementation as the MAUI Essentials Map default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            // The entry point is get-only, so the backing field is the settable surface
            // (Map.defaultImplementation), the same pattern as the haptics/sensors/launcher
            // installers.
            foreach (FieldInfo field in typeof(Map).GetFields(flags))
            {
                if (field.FieldType.IsInstanceOfType(Instance))
                {
                    field.SetValue(null, Instance);
                }
            }
        }
        catch (Exception)
        {
            // The default stays in place when the entry point cannot be replaced.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => InstallDefault();

    /// <summary>
    /// The Android-shaped geo: URI for a coordinate pair: <c>geo:{lat},{lng}?q={lat},{lng}</c>
    /// (the q point doubles as the label position), with the optional name in parentheses.
    /// Numbers use the invariant culture so the shell's map handler never sees a comma decimal.
    /// </summary>
    internal static string GetMapsUri(double latitude, double longitude, MapLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string lat = latitude.ToString(CultureInfo.InvariantCulture);
        string lng = longitude.ToString(CultureInfo.InvariantCulture);
        return $"{GeoPrefix}{lat},{lng}?q={lat},{lng}{NameSuffix(options)}";
    }

    /// <summary>
    /// The Android-shaped geo: URI for a placemark: <c>geo:0,0?q={escaped address}</c> with the
    /// optional name in parentheses. The address is the same five fields MAUI's own platforms
    /// join (Thoroughfare Locality AdminArea PostalCode CountryName).
    /// </summary>
    internal static string GetMapsUri(Placemark placemark, MapLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(placemark);
        ArgumentNullException.ThrowIfNull(options);
        string address = Uri.EscapeDataString(
            $"{placemark.Thoroughfare} {placemark.Locality} {placemark.AdminArea} {placemark.PostalCode} {placemark.CountryName}");
        return $"{GeoPrefix}0,0?q={address}{NameSuffix(options)}";
    }

    private const string GeoPrefix = "geo:";

    // Android's (label) suffix: the map applications show it next to the pin. Percent-encoded so
    // a space or parenthesis in the name cannot break the URI.
    private static string NameSuffix(MapLaunchOptions options)
        => string.IsNullOrWhiteSpace(options.Name)
            ? string.Empty
            : $"({Uri.EscapeDataString(options.Name)})";

    /// <summary>
    /// Sends one URI through the ability bridge and reports whether it was dispatched; a failed
    /// dispatch is noted once per process through the status channel (never thrown: the caller
    /// contract is a completed task / false).
    /// </summary>
    private static void Dispatch(string uri, out bool dispatched)
    {
        dispatched = OpenHarmonyAbilityBridge.TryOpenUri(uri);
        if (!dispatched)
        {
            NoteDispatchFailureOnce();
        }
    }

    private static bool s_navigationModeIgnored;
    private static bool s_dispatchFailed;

    private static void NoteNavigationMode(MapLaunchOptions options)
    {
        if (options.NavigationMode == NavigationMode.None || s_navigationModeIgnored)
        {
            return;
        }
        s_navigationModeIgnored = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] map: MapLaunchOptions.NavigationMode has no geo: representation on OpenHarmony; " +
            "the system map opens the plain location (vendor navigation URIs are not generic)");
    }

    private static void NoteDispatchFailureOnce()
    {
        if (s_dispatchFailed)
        {
            return;
        }
        s_dispatchFailed = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] map: the ability bridge could not dispatch the geo: URI " +
            "(no libopenharmonyhost.so or no shell registerAbilitySink)");
    }
}
