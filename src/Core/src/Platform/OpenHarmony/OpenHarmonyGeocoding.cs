// Geocoding (Microsoft.Maui.Devices.Sensors.IGeocoding) for OpenHarmony over the host/ArkTS
// request bridge (C1 implements the host + shell half in parallel; this is the frozen contract):
//
//   managed:  ohos_host_geocode_request(op, arg, id)   -> asked of the shell
//             ohos_host_register_geocode_result(cb)    -> cb(id, rc, json) from the host
//   shell:    host.geocodeResult(id, rc, json)
//
// Op 0 forward-geocodes (address -> locations; arg is a JSON object carrying the address in its
// "description" field, {"description":"..."}, the GeoCodeRequest shape the shell parses) and
// op 1 reverse-geocodes (location -> address/placemarks; arg is "latitude,longitude" in the
// invariant culture). The host request returns 0 when it was queued for the shell's
// registerGeocodeSink and -1 when there is no sink or the arg is invalid. The answer arrives as the ArkTS GeoAddress array (the shell's getAddressesFromLocationName /
// getAddressesFromLocation result, @ohos.geoLocationManager) with rc 0; any other rc, a missing
// shell sink, a missing host library and a request that is not answered inside RequestTimeout
// all answer null, which the implementation maps to an empty result (the same "deny" shape the
// permissions bridge uses - never an exception, never a hang).
//
// The result JSON is parsed tolerantly because the shell builds it from the geo kit output: a
// top-level array or a {"placemarks":[...]} / {"locations":[...]} envelope, coordinates either
// on the record or in a nested "coordinates"/"location" object, "latitude"/"lat" and
// "longitude"/"lon"/"lng" spellings, numbers or numeric strings, and both the GeoAddress field
// names (placeName, administrativeArea, subAdministrativeArea, streetNumber, ...) and the
// MAUI-style aliases (featureName, adminArea, subAdminArea, subThoroughfare, ...). Anything
// malformed yields the empty result instead of an exception.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>One geocoding request over the host/ArkTS bridge.</summary>
internal static partial class OpenHarmonyGeocodingBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Forward geocode (op 0): address -> locations; arg is a JSON object of one address.</summary>
    internal const int ForwardOp = 0;

    /// <summary>Reverse geocode (op 1): location -> address/placemarks; arg is "latitude,longitude".</summary>
    internal const int ReverseOp = 1;

    /// <summary>
    /// Bounds a lost geocode answer. The shell call is a kit request that can need the network, so
    /// the window is wider than the clipboard's but far below the permission prompt's; a timeout
    /// answers "no results" exactly like a deny.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_geocode_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GeocodeRequestNative(int op, string arg, int requestId);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_register_geocode_result")]
    private static partial void RegisterGeocodeResultNative(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GeocodeResultCallback(int requestId, int rc, IntPtr jsonUtf8);

    private static readonly object s_sync = new();
    private static readonly Dictionary<int, TaskCompletionSource<string?>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, void>)&OnNativeGeocodeResult;
    private static bool s_registered;
    private static bool s_unavailable;
    private static int s_nextRequestId;

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Registers the native result callback; a guarded no-op off-device.</summary>
    internal static void Register()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            RegisterGeocodeResultNative(s_callback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
        }
    }

    /// <summary>
    /// Asks the shell for one geocode result. Returns the shell's JSON on rc 0; null when the
    /// host library/sink is unavailable, the request could not be dispatched, the shell answered
    /// a failure code, or the answer did not arrive inside <paramref name="timeout"/>.
    /// </summary>
    internal static async Task<string?> RequestAsync(int op, string arg, TimeSpan timeout)
    {
        if (!s_registered)
        {
            Register();
            if (s_unavailable)
            {
                return null;
            }
        }
        int requestId = Interlocked.Increment(ref s_nextRequestId);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (s_sync)
        {
            s_pending[requestId] = completion;
        }
        bool dispatched = true;
        try
        {
            // The host request reports whether it queued the request for the shell; -1 (no sink,
            // invalid arg) is a fast "deny" without waiting out the timeout.
            dispatched = GeocodeRequestNative(op, arg ?? string.Empty, requestId) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            dispatched = false;
        }
        if (!dispatched)
        {
            lock (s_sync)
            {
                s_pending.Remove(requestId);
            }
            return null;
        }
        Task finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != completion.Task)
        {
            lock (s_sync)
            {
                s_pending.Remove(requestId);
            }
            return null;
        }
        return await completion.Task.ConfigureAwait(false);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeGeocodeResult(int requestId, int rc, IntPtr jsonUtf8)
    {
        TaskCompletionSource<string?>? completion;
        lock (s_sync)
        {
            if (!s_pending.Remove(requestId, out completion))
            {
                return;
            }
        }
        if (rc != 0)
        {
            completion?.TrySetResult(null);
            return;
        }
        completion?.TrySetResult(jsonUtf8 == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUTF8(jsonUtf8) ?? string.Empty);
    }
}

/// <summary>
/// MAUI Essentials geocoding on OpenHarmony. Results degrade to empty without throwing when the
/// bridge is unavailable (off-device) or the shell does not answer; every failure is logged.
/// Internal until device results have been validated (the app surface is the MAUI IGeocoding).
/// </summary>
internal sealed class OpenHarmonyGeocoding : IGeocoding
{
    private static readonly string[] LatitudeNames = { "latitude", "lat" };
    private static readonly string[] LongitudeNames = { "longitude", "lon", "lng", "long" };
    private static readonly string[] AltitudeNames = { "altitude", "alt" };
    private static readonly string[] AccuracyNames = { "accuracy", "acc" };
    private static readonly string[] CoordinateObjectNames = { "coordinates", "coordinate", "location", "position" };

    public async Task<IEnumerable<Placemark>> GetPlacemarksAsync(double latitude, double longitude)
    {
        // Op 1 (location -> address): "lat,lon" (the shell feeds it to a ReverseGeoCodeRequest).
        string arg = BuildReverseArg(latitude, longitude);
        string? json = await OpenHarmonyGeocodingBridge
            .RequestAsync(OpenHarmonyGeocodingBridge.ReverseOp, arg, OpenHarmonyGeocodingBridge.RequestTimeout)
            .ConfigureAwait(false);
        if (json is null)
        {
            OpenHarmonyBridge.WriteStatus("[maui] reverse geocoding was not answered; returning no placemarks");
            return Array.Empty<Placemark>();
        }
        return ParsePlacemarks(json);
    }

    public async Task<IEnumerable<Location>> GetLocationsAsync(string address)
    {
        // Op 0 (address -> location): a JSON object of one address (the shell's GeoCodeRequest
        // description comes out of it).
        string arg = BuildForwardArg(address);
        string? json = await OpenHarmonyGeocodingBridge
            .RequestAsync(OpenHarmonyGeocodingBridge.ForwardOp, arg, OpenHarmonyGeocodingBridge.RequestTimeout)
            .ConfigureAwait(false);
        if (json is null)
        {
            OpenHarmonyBridge.WriteStatus("[maui] forward geocoding was not answered; returning no locations");
            return Array.Empty<Location>();
        }
        return ParseLocations(json);
    }

    /// <summary>The op 1 argument: the location in the invariant culture, comma separated.</summary>
    internal static string BuildReverseArg(double latitude, double longitude)
        => latitude.ToString(CultureInfo.InvariantCulture) +
            "," + longitude.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The op 0 argument: one JSON object carrying the address in "description" (the
    /// GeoCodeRequest field the shell reads). JsonSerializer.Serialize handles the escaping
    /// (quotes, backslashes, control characters) so a hostile address cannot break the envelope
    /// the shell parses.
    /// </summary>
    internal static string BuildForwardArg(string? address)
        => "{\"description\":" + JsonSerializer.Serialize(address ?? string.Empty, OpenHarmonySliceJsonContext.Default.String) + "}";

    /// <summary>Parses a reverse-geocode answer into placemarks (malformed items are skipped).</summary>
    internal static IReadOnlyList<Placemark> ParsePlacemarks(string? json)
    {
        var results = new List<Placemark>();
        if (!TryReadResultArray(json, "placemarks", out JsonElement array))
        {
            return results;
        }
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || ReadLocation(item) is not { } location)
            {
                continue;
            }
            results.Add(new Placemark
            {
                Location = location,
                CountryCode = ReadString(item, "countryCode", "country_code"),
                CountryName = ReadString(item, "countryName", "country"),
                // GeoAddress.placeName is the address's display name (MAUI's FeatureName).
                FeatureName = ReadString(item, "placeName", "featureName", "feature", "name"),
                PostalCode = ReadString(item, "postalCode", "postcode", "zip"),
                SubLocality = ReadString(item, "subLocality", "district"),
                Thoroughfare = ReadString(item, "thoroughfare", "street"),
                SubThoroughfare = ReadString(item, "subThoroughfare", "streetNumber", "houseNumber"),
                Locality = ReadString(item, "locality", "city"),
                AdminArea = ReadString(item, "administrativeArea", "adminArea", "province", "state"),
                SubAdminArea = ReadString(item, "subAdministrativeArea", "subAdminArea", "county"),
            });
        }
        return results;
    }

    /// <summary>Parses a forward-geocode answer into locations (malformed items are skipped).</summary>
    internal static IReadOnlyList<Location> ParseLocations(string? json)
    {
        var results = new List<Location>();
        if (!TryReadResultArray(json, "locations", out JsonElement array))
        {
            return results;
        }
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && ReadLocation(item) is { } location)
            {
                results.Add(location);
            }
        }
        return results;
    }

    /// <summary>Reads one location: coordinates on the record or under a nested object.</summary>
    private static Location? ReadLocation(JsonElement item)
    {
        JsonElement source = item;
        if (TryFindProperty(item, CoordinateObjectNames, out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            source = nested;
        }
        if (!TryReadDouble(source, LatitudeNames, out double latitude) ||
            !TryReadDouble(source, LongitudeNames, out double longitude))
        {
            return null;
        }
        double? altitude = TryReadDouble(source, AltitudeNames, out double altitudeValue) ? altitudeValue : null;
        double? accuracy = TryReadDouble(source, AccuracyNames, out double accuracyValue) ? accuracyValue : null;
        return new Location(latitude, longitude) { Altitude = altitude, Accuracy = accuracy };
    }

    /// <summary>
    /// The JSON array of results: either the top-level value or the named envelope property
    /// ("placemarks"/"locations"); the JsonDocument is disposed and the element is cloned so the
    /// returned tree outlives the parse.
    /// </summary>
    private static bool TryReadResultArray(string? json, string envelope, out JsonElement array)
    {
        array = default;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
                return true;
            }
            if (root.ValueKind == JsonValueKind.Object &&
                TryFindProperty(root, new[] { envelope }, out JsonElement found) &&
                found.ValueKind == JsonValueKind.Array)
            {
                array = found;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement item, params string[] names)
        => TryFindProperty(item, names, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadDouble(JsonElement item, string[] names, out double value)
    {
        value = 0;
        if (!TryFindProperty(item, names, out JsonElement element))
        {
            return false;
        }
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }
        return element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Case-insensitive property lookup over the aliases.</summary>
    private static bool TryFindProperty(JsonElement item, string[] names, out JsonElement value)
    {
        value = default;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (JsonProperty property in item.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        return false;
    }
}
