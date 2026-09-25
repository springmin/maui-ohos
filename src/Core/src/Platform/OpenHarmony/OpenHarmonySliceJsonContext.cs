// Source-generated JSON for the OpenHarmony platform slice (FIX-INTEROP #4).
//
// The slice is AOT/trim-compatible (IsAotCompatible + EnableTrimAnalyzer/EnableAotAnalyzer in
// Microsoft.Maui.Platform.OpenHarmony.csproj), so every System.Text.Json call must go through a
// JsonTypeInfo/JsonSerializerContext: the reflection-based overloads raise IL2026/IL3050 and
// need runtime code generation the NativeAOT route does not have. This context carries the
// wire types of the three reflection users in the slice:
//   * OpenHarmonyHybridWebViewHandler: HybridAssetsConfig, DotNetInvokeResult, string[] (the
//     JS -> .NET invocation parameters), string (page id / task id / method name),
//   * OpenHarmonyBlazorWebViewHandler: BlazorAssetsConfig, string (document id / message),
//   * OpenHarmonyGeocoding: string (the address inside the GeoCodeRequest JSON).
//
// The nested types had to become internal (they were private) so the context can reference
// them; keep the JsonPropertyName annotations on them in sync with the shell's readers.
using System.Text.Json.Serialization;

namespace Microsoft.Maui.Platform;

[JsonSerializable(typeof(OpenHarmonyHybridWebViewHandler.HybridAssetsConfig))]
[JsonSerializable(typeof(OpenHarmonyHybridWebViewHandler.DotNetInvokeResult))]
[JsonSerializable(typeof(OpenHarmonyBlazorWebViewHandler.BlazorAssetsConfig))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
internal sealed partial class OpenHarmonySliceJsonContext : JsonSerializerContext
{
}
