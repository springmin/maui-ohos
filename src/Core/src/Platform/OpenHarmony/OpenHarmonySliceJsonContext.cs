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
//   * OpenHarmonyWebViewHandler: WasmSiteConfig (the Blazor WebAssembly site registration),
//   * OpenHarmonyGeocoding: string (the address inside the GeoCodeRequest JSON).
//
// JsonElement[] is the BlazorWebView IPC's inbound wire shape (IpcCommon.TryDeserialize parses
// every "__bwv:" message into JsonElement[]). That parse runs inside the WebView package with
// its own static JsonSerializerOptions, whose reflection resolver constructs
// ArrayConverter<JsonElement[], JsonElement> through reflection - and NativeAOT has no native
// code for that generic instantiation unless something roots it (FIX-BWVMount: the array
// converter was missing, so the payload threw and the page never attached). Carrying
// JsonElement[] in this context makes the generated metadata (JsonMetadataServices.
// CreateArrayInfo<JsonElement>) statically reference the converter, and the Blazor handler
// touches the type info at startup to keep it from being trimmed; the package's options do
// not consume this context, they only need the instantiation to exist.
//
// The nested types had to become internal (they were private) so the context can reference
// them; keep the JsonPropertyName annotations on them in sync with the shell's readers.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.Platform;

[JsonSerializable(typeof(OpenHarmonyHybridWebViewHandler.HybridAssetsConfig))]
[JsonSerializable(typeof(OpenHarmonyHybridWebViewHandler.DotNetInvokeResult))]
[JsonSerializable(typeof(OpenHarmonyWebViewHandler.WasmSiteConfig))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
// The BlazorWebView IPC payload shape; see the header. Kept outside the OPENHARMONY_BLAZOR_WEBVIEW
// gate so the AOT root exists in every vehicle that compiles the slice.
[JsonSerializable(typeof(JsonElement[]))]
#if OPENHARMONY_BLAZOR_WEBVIEW
// The BlazorWebView handler (and therefore its wire type) only compiles in vehicles that
// define OPENHARMONY_BLAZOR_WEBVIEW; the harness compiles the slice sources without it.
[JsonSerializable(typeof(OpenHarmonyBlazorWebViewHandler.BlazorAssetsConfig))]
#endif
internal sealed partial class OpenHarmonySliceJsonContext : JsonSerializerContext
{
}
