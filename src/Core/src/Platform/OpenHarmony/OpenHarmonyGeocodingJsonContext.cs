// Source-generated JSON for the geocoding wire string (FIX-INTEROP #4).
//
// OpenHarmonyGeocoding lives in Microsoft.Maui.Core with the other dependency-clean services, so
// it cannot use OpenHarmonySliceJsonContext (which stays in Microsoft.Maui.Controls with the
// WebView/HybridWebView/BlazorWebView wire types). The geocoder only serializes a string; this
// context roots that metadata for the AOT/trim route the same way the slice context does for its
// users.
using System.Text.Json.Serialization;

namespace Microsoft.Maui.Platform;

[JsonSerializable(typeof(string))]
internal sealed partial class OpenHarmonyGeocodingJsonContext : JsonSerializerContext
{
}
