# OpenHarmony MAUI platform slice — where it is and how to wire it in

`main` in this repository mirrors `dotnet/maui` (upstream); it does **not** carry the
OpenHarmony platform slice. The slice is published from `feature/openharmony` and as the
source drop `ohos-slice-1.0.1.tar.gz` on the `ohos-slice-1.0.1` release.

## Where the slice is

| Item | Value |
|---|---|
| Branch | `feature/openharmony` (tip of the slice work) |
| Directory | `src/Core/src/Platform/OpenHarmony/` — 107 `.cs` files + `README.md` + standalone `Microsoft.Maui.Platform.OpenHarmony.csproj` |
| Tag / asset | `ohos-slice-1.0.1` → `ohos-slice-1.0.1.tar.gz` + `.sha256` |
| Standalone project | plain `net11.0`; references `Microsoft.Maui.*` 11.0.0-rc.1.26451.6 and the workload bridge DLLs `Microsoft.OpenHarmony.Hosting` / `Microsoft.OpenHarmony.Maui.Graphics` |

Clone the slice branch (the default branch will not have it):

```sh
git clone --depth 1 --branch feature/openharmony https://github.com/springmin/maui-ohos.git maui-ohos
test -f maui-ohos/src/Core/src/Platform/OpenHarmony/OpenHarmonyMauiAppHost.cs && echo "slice present"
```

Or take the tarball (same tree, prefixed `ohos-slice-1.0.1/`):

```sh
curl -LO https://github.com/springmin/maui-ohos/releases/download/ohos-slice-1.0.1/ohos-slice-1.0.1.tar.gz
tar xzf ohos-slice-1.0.1.tar.gz
test -f ohos-slice-1.0.1/src/Core/src/Platform/OpenHarmony/OpenHarmonyMauiAppHost.cs && echo "slice present"
```

## Type-name map (names referenced by the workload / test harness)

| Expected name | Actual name in the slice | Status |
|---|---|---|
| `UseOpenHarmony()` | `Microsoft.Maui.Platform.MauiOpenHarmonyExtensions.UseOpenHarmony(this MauiAppBuilder)` | present, same name |
| `OpenHarmonyMauiAppHost` | `public sealed class` in `Microsoft.Maui.Platform`; ctor `OpenHarmonyMauiAppHost(IServiceProvider)`, method `Run(IApplication)` | present, same name |
| `OpenHarmonyBlazorWebViewHandler` | `public sealed class OpenHarmonyBlazorWebViewHandler : OpenHarmonyViewHandler<IBlazorWebView>, IBlazorWebViewHandler` | present; compiled only when `OPENHARMONY_BLAZOR_WEBVIEW` is defined **and** the `Microsoft.AspNetCore.Components.WebView.Maui` package is referenced |
| `MauiOpenHarmonyExtensions` | `public static class` holding `UseOpenHarmony` plus the internal `SliceHandlers` registry | present, same name |

No compatibility aliases are needed: the names used by `ohos-workload`
(`test/hello-maui-app`, `test/hello-maui-razor`, `test/maui-platform-verify`) are the actual
slice names, and those projects compile the slice sources directly. A build that cannot see
`OpenHarmonyBlazorWebViewHandler` is missing the `OPENHARMONY_BLAZOR_WEBVIEW` constant and the
BlazorWebView package (see the file header of `OpenHarmonyBlazorWebViewHandler.cs`).

## Wiring

### A. Workload test projects — source include (what `ohos-workload` does)

Point the project at the slice directory and compile the files in:

```xml
<PropertyGroup>
  <OpenHarmonyMauiPlatformDir Condition=" '$(OpenHarmonyMauiPlatformDir)' == '' ">../../../maui-ohos/src/Core/src/Platform/OpenHarmony</OpenHarmonyMauiPlatformDir>
  <DefineConstants>$(DefineConstants);OPENHARMONY_BLAZOR_WEBVIEW</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Maui.Controls" Version="11.0.0-rc.1.26451.6" />
  <PackageReference Include="Microsoft.AspNetCore.Components.WebView.Maui" Version="11.0.0-rc.1.26451.6" />
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
`test/hello-maui-app` uses `-p:OpenHarmonyMauiPlatformDir=...`, with a sibling checkout at
`../../maui-ohos` as the default. Because the branch tip is a partial checkout (slice files
only), the source include works without restoring or building the rest of MAUI.

### B. Full fork build — TFM gating (`MultiTargeting.targets`)

The slice is compiled into `Microsoft.Maui.Core` only for `net11.0-openharmony*`:

- `Directory.Build.props`: `IncludeOpenHarmonyTargetFrameworks=true` (auto-enabled when the
  `microsoft.net.sdk.openharmony` workload manifest is installed); `MauiPlatforms` then gets
  `net11.0-openharmony26.0`, and `OPENHARMONY` is defined.
- `src/MultiTargeting.targets`: `**/OpenHarmony/**/*.cs` and `*.OpenHarmony.cs` are removed for
  every TFM where `_MauiTargetPlatformIsOpenHarmony != True`, so the `Microsoft.OpenHarmony.*`
  dependencies cannot leak into other platform builds; for the OpenHarmony TFM the `OPENHARMONY`
  constant is defined (inside the slice, use `#if OPENHARMONY`).
- `src/Core/src/Core.csproj`: for `-openharmony` TFMs it references
  `Microsoft.OpenHarmony.Maui.Graphics` (override `-p:OpenHarmonyGraphicsAssembly=...`).
- `src/Workload/Microsoft.NET.Sdk.Maui.Manifest/WorkloadManifest.in.json`: the
  `maui-openharmony` workload extends `["maui-blazor", "openharmony"]`.

Standalone slice build (0 errors). The project evaluates in a slice-only checkout (this branch
or the tarball): when `eng/` is absent the root `Directory.Build.props`/`.targets` are skipped
instead of failing on the Arcade bootstrap. The two assembly paths default to a sibling
`ohos-workload` checkout; override them for any other layout:

```sh
dotnet build src/Core/src/Platform/OpenHarmony/Microsoft.Maui.Platform.OpenHarmony.csproj \
  -p:OpenHarmonyHostingAssembly=/abs/path/Microsoft.OpenHarmony.Hosting.dll \
  -p:OpenHarmonyGraphicsAssembly=/abs/path/Microsoft.OpenHarmony.Maui.Graphics.dll
```

## Why `main` is not fast-forwarded to the slice

`main` == `dotnet/maui` commit `1cd2e15b` (an upstream mirror; verified against the upstream
repository). `feature/openharmony` descends from it, but the branch's first slice commit
removed the rest of the upstream tree, so fast-forwarding `main` would delete ~26,300 upstream
files. The slice is therefore published as a branch + tag + tarball, with this file added to
`main` as the pointer for anyone who lands on the default branch.
