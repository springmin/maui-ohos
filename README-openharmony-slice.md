# OpenHarmony MAUI platform slice

The OpenHarmony platform slice lives in `src/Core/src/Platform/OpenHarmony/`. It is compiled
only for the `net*-openharmony` target framework: `src/MultiTargeting.targets` removes the
folder for every other platform, so the `Microsoft.OpenHarmony.*` dependencies cannot leak into
a platform-neutral MAUI build.

The platform toolchain is an external workload (`microsoft.net.sdk.openharmony`). The
`maui-openharmony` workload extends `["maui-blazor", "openharmony"]` and becomes installable
when that workload is available from a registered manifest source.

## Where the slice is

| Item | Value |
|---|---|
| Directory | `src/Core/src/Platform/OpenHarmony/` — the platform sources plus the standalone `Microsoft.Maui.Platform.OpenHarmony.csproj` compile vehicle |
| Standalone project | plain `net11.0`; references `Microsoft.Maui.*` and the workload bridge assemblies `Microsoft.OpenHarmony.Hosting` / `Microsoft.OpenHarmony.Maui.Graphics` |
| Tests | `src/Core/tests/OpenHarmony.UnitTests/` — host-runnable tests for the pure managed contracts |
| Gate | `eng/openharmony/verify-slice.sh` — build with warnings-as-errors and run the tests |

## Type-name map (names referenced by the workload / test harness)

| Expected name | Actual name in the slice | Status |
|---|---|---|
| `UseOpenHarmony()` | `Microsoft.Maui.Platform.MauiOpenHarmonyExtensions.UseOpenHarmony(this MauiAppBuilder)` | present, same name |
| `OpenHarmonyMauiAppHost` | `public sealed class` in `Microsoft.Maui.Platform`; ctor `OpenHarmonyMauiAppHost(IServiceProvider)`, method `Run(IApplication)` | present, same name |
| `OpenHarmonyBlazorWebViewHandler` | `public sealed class OpenHarmonyBlazorWebViewHandler : OpenHarmonyViewHandler<IBlazorWebView>, IBlazorWebViewHandler` | present; compiled only when `OPENHARMONY_BLAZOR_WEBVIEW` is defined **and** the `Microsoft.AspNetCore.Components.WebView.Maui` package is referenced |
| `MauiOpenHarmonyExtensions` | `public static class` holding `UseOpenHarmony` plus the internal `SliceHandlers` registry | present, same name |

No compatibility aliases are needed: the names used by the OpenHarmony workload test projects
(`test/hello-maui-app`, `test/hello-maui-razor`, `test/maui-platform-verify`) are the actual
slice names, and those projects compile the slice sources directly. A build that cannot see
`OpenHarmonyBlazorWebViewHandler` is missing the `OPENHARMONY_BLAZOR_WEBVIEW` constant and the
BlazorWebView package (see the file header of `OpenHarmonyBlazorWebViewHandler.cs`).

## Wiring

### A. Workload test projects — source include

Point the project at the slice directory and compile the files in:

```xml
<PropertyGroup>
  <OpenHarmonyMauiPlatformDir Condition=" '$(OpenHarmonyMauiPlatformDir)' == '' ">../../../maui-ohos/src/Core/src/Platform/OpenHarmony</OpenHarmonyMauiPlatformDir>
  <DefineConstants>$(DefineConstants);OPENHARMONY_BLAZOR_WEBVIEW</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Maui.Controls" Version="11.0.0-rc.2.26505.5" />
  <PackageReference Include="Microsoft.AspNetCore.Components.WebView.Maui" Version="11.0.0-rc.2.26505.5" />
  <Reference Include="Microsoft.OpenHarmony.Hosting">
    <HintPath>.../Microsoft.OpenHarmony.Hosting.dll</HintPath><Private>false</Private>
  </Reference>
  <Reference Include="Microsoft.OpenHarmony.Maui.Graphics">
    <HintPath>.../Microsoft.OpenHarmony.Maui.Graphics.dll</HintPath><Private>false</Private>
  </Reference>
  <Compile Include="$(OpenHarmonyMauiPlatformDir)/*.cs" />
</ItemGroup>
```

The verifier harness exposes the same knob as `-p:MauiSliceDir=...` / `MAUI_SLICE_DIR`;
`test/hello-maui-app` uses `-p:OpenHarmonyMauiPlatformDir=...`.

### B. Full tree — TFM gating (`MultiTargeting.targets`)

The slice is compiled into `Microsoft.Maui.Core` only for `net11.0-openharmony*`:

- `Directory.Build.props`: `IncludeOpenHarmonyTargetFrameworks=true` (auto-enabled when the
  `microsoft.net.sdk.openharmony` workload manifest is installed); `MauiPlatforms` then gets
  `net11.0-openharmony20.0` (the public SDK band; override with
  `-p:OpenHarmonyTargetFrameworkVersion=26.0` for the device SDK), and `OPENHARMONY` is defined.
- `src/MultiTargeting.targets`: `**/OpenHarmony/**/*.cs` and `*.OpenHarmony.cs` are removed for
  every TFM where `_MauiTargetPlatformIsOpenHarmony != True`, so the `Microsoft.OpenHarmony.*`
  dependencies cannot leak into other platform builds; for the OpenHarmony TFM the `OPENHARMONY`
  constant is defined (inside the slice, use `#if OPENHARMONY`).
- `src/Core/src/Core.csproj`: for `-openharmony` TFMs the folder is removed from Core so Core
  stays platform-neutral.
- `src/Controls/src/Core/Controls.Core.csproj`: for `-openharmony` TFMs it links the slice
  sources and includes the Standard fallbacks for the shared Controls handlers. The workload
  Ref pack (FrameworkList) supplies both `Microsoft.OpenHarmony.Hosting` and
  `Microsoft.OpenHarmony.Maui.Graphics`; no explicit assembly path is needed.
- `src/Workload/Microsoft.NET.Sdk.Maui.Manifest/WorkloadManifest.in.json`: the
  `maui-openharmony` workload extends `["maui-blazor", "openharmony"]`.

## Standalone build and test gate

The standalone project evaluates in a slice-only checkout: when `eng/` is absent the root
`Directory.Build.props`/`.targets` are skipped instead of failing on the Arcade bootstrap. The
two workload assembly paths are required; there is no hard-coded sibling-checkout default:

```sh
dotnet build src/Core/src/Platform/OpenHarmony/Microsoft.Maui.Platform.OpenHarmony.csproj \
  -p:OpenHarmonyHostingAssembly=/abs/path/Microsoft.OpenHarmony.Hosting.dll \
  -p:OpenHarmonyGraphicsAssembly=/abs/path/Microsoft.OpenHarmony.Maui.Graphics.dll
```

The reproducible gate builds `Microsoft.Maui.Core` and `Microsoft.Maui.Controls` for the real
OpenHarmony TFM with warnings-as-errors:

```sh
eng/openharmony/verify-slice.sh --tfm
```

The separate `net11.0` vehicle and its 25 host-runnable unit tests (text transforms, the
accessibility action map, source-path validation) run with the default mode:

```sh
eng/openharmony/verify-slice.sh
```

See `eng/openharmony/README.md` for the CI leg and the workload-discovery rules.
