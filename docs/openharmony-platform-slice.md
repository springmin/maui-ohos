# OpenHarmony platform slice (start)

Status (2026-09-22): implemented and off-device verified. The slice registers 41 view-handler
entries (pages/controls, WebView/HybridWebView, build-gated BlazorWebView) and draws the whole
tree through the self-drawn compositor route: one ArkUI `XComponent` surface + canvas, custom
`IView`/list materializer, and a shadow accessibility tree published to the ArkUI provider
(route decision: `runtime-ohos` `docs/plans/2026-09-22-ohos-render-route-decision.md`).
Baseline: the `ohos-workload` `test/maui-platform-verify` harness at 288 `[verify]` checks
(floor 268). Device bring-up is in progress and independent of the render route.

The fork's platform block (`Directory.Build.props`) enables the
`net11.0-openharmony<api>` TFM automatically when the
`microsoft.net.sdk.openharmony` workload is installed (`IncludeOpenHarmonyTargetFrameworks`),
defines `OPENHARMONY` for compiled code, and the `maui-openharmony` workload extends
`["maui-blazor", "openharmony"]`.

## What the platform workload provides (already implemented)

`Microsoft.OpenHarmony.Hosting` (in the platform SDK pack) is the bridge between the ArkTS
shell and managed code:

| Managed surface | Purpose |
|---|---|
| `OpenHarmonyBridge.Context` | app dirs, bundle/ability names, `NodeContent` handle |
| `OpenHarmonyBridge.LifecycleChanged` | Create/Destroy/Foreground/Background, forwarded from the ability |
| `OpenHarmonyBridge.NodeContent` | native ArkUI node attachment point (overlays, custom widgets) |
| `OpenHarmonyBridge.SurfaceChanged` / `.Surface` | the `OHNativeWindow*` handed over by the ArkUI `XComponent` (surface type) |
| `OpenHarmonyBridge.FillSurface(argb)` | managed-driven frame; the placeholder a real renderer replaces |
| `OpenHarmonyRuntime.IsOpenHarmony` (`Microsoft.OpenHarmony.dll`) | platform check for user code |

The shell (`templates/ets/**` in the platform pack) is prebuilt both headless
(`modules.abc`) and with a UI page + `XComponent` (`modules.ui.abc`); the MAUI slice only
needs the UI variant.

## Slice layout (to add under `src/Core/src/Platform/OpenHarmony/`)

| MAUI concept | OpenHarmony implementation sketch |
|---|---|
| `MauiContext` | wraps the app context (dirs, dispatcher, `SurfaceChanged` source) |
| `MauiDispatcher` | posts to the ArkTS UI thread (NAPI call into the shell) or a managed UI thread driven by `Choreographer`-style callbacks |
| `PlatformView` / handlers | native ArkUI nodes created through `NodeContent` (for simple views) or drawn into the `XComponent` surface (for custom/MAUI-rendered views) |
| `WindowHandler` | maps MAUI `Window` to the ability lifecycle + surface (`SurfaceChanged`, size) |
| Input | `OH_NativeXComponent` touch/key callbacks forwarded to MAUI gestures |
| `Microsoft.Maui.Graphics` backend | `ISkiaSharpApiLease`-style backend over the `OHNativeWindow*` (Skia OHOS build) |

## Immediate steps

1. Confirm the surface handshake on a device (`SurfaceChanged` + `FillSurface`).
2. Bring up Skia over `OHNativeWindow*` (CPU write first, then GPU) and publish it as the
   `Microsoft.Maui.Graphics` backend for `OPENHARMONY`.
3. Port the first handlers (Label/Button/Layout) either through native ArkUI nodes
   (`NodeContent`) or the Skia surface, and wire `MauiProgram`/`WindowHandler` to
   `OpenHarmonyBridge`.
