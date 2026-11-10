# OpenHarmony platform slice: build and test gate

This directory is the reproducible entry point for the `net*-openharmony` platform band. The
OpenHarmony workload is an external platform workload, so the default MAUI build and CI jobs stay
workload-free; a dedicated leg installs the workload and runs this gate.

## Modes

```sh
eng/openharmony/verify-slice.sh            # net11.0 vehicle + host unit tests (25 tests)
eng/openharmony/verify-slice.sh --tfm      # Microsoft.Maui.Core + Microsoft.Maui.Controls
                                            # for net11.0-openharmony20.0
eng/openharmony/verify-slice.sh --slice-tfm # only the standalone slice vehicle on the real TFM
eng/openharmony/verify-slice.sh --all      # --tfm + the net11.0 tests
```

`--tfm` is the primary gate: it builds `Microsoft.Maui.dll` and `Microsoft.Maui.Controls.dll` with
`TreatWarningsAsErrors=true`, using the workload framework reference and the OpenHarmony platform
symbols. The slice is linked into `Microsoft.Maui.Controls` for the OpenHarmony TFM because its
handlers consume Controls types; Core stays platform-neutral. The Release build also runs
`PublicApiAnalyzers` in validate mode against the Core and Controls `net-openharmony` baselines.

`--tfm` resolves `Microsoft.OpenHarmony.Hosting` and `Microsoft.OpenHarmony.Maui.Graphics` from
the workload Ref pack's `FrameworkList.xml`; no explicit assembly path is needed. The
`--slice-tfm` and default test modes still use the standalone net11.0 vehicle, which takes the
assemblies explicitly (the script discovers them from the workload layout):

```sh
OpenHarmonyHostingAssembly=/path/to/Microsoft.OpenHarmony.Hosting.dll \
OpenHarmonyGraphicsAssembly=/path/to/Microsoft.OpenHarmony.Maui.Graphics.dll \
  eng/openharmony/verify-slice.sh --tfm
```

`--tfm` fails closed when the workload Ref pack does not contain the bridge/graphics assemblies. `VERIFY_MSBUILD_ARGS` passes extra
MSBuild/restore switches (the local host needs
`-p:MicrosoftCodeAnalysisNetAnalyzersVersion=9.0.0 -p:NuGetAudit=false` for an offline restore).

## CI

The intended CI leg is:

```text
dotnet workload install openharmony --source <OpenHarmony feed>
eng/openharmony/verify-slice.sh --tfm
```

The checked-in job definition is `.github/workflows/openharmony-slice.yml` (manual
`workflow_dispatch`): it downloads an OpenHarmony-hosted .NET SDK from `sdk_url`, optionally
installs the workload from `workload_feed`, and runs `eng/openharmony/verify-slice.sh --tfm`.

Until the workload manifest and packs are published to a public feed, the leg is run from the
OpenHarmony workload repository, which supplies the two assemblies. Do not wire `--tfm` into the
default MAUI CI job: a missing workload must not fail the platform-neutral build.

## Test-host note

The default mode prefers the xunit console runner when it is present in the NuGet cache, then
falls back to the normal VSTest host. The OpenHarmony development host's VSTest resolves
`DOTNET_ROOT` through a home-directory symlink and cannot launch testhost, while the console
runner passes `25/25`; CI uses a normal .NET host and can use either path. `--tfm` does not run
the tests, so the Core/Controls gate is unaffected.

## Boundaries

- This gate covers the platform slice and the Core/Controls assemblies. Device and HAP packaging
  tests stay in the OpenHarmony workload repository (`test/maui-platform-verify`,
  `test/hello-maui-*`).
- The `maui-openharmony` workload extension is declared in
  `src/Workload/Microsoft.NET.Sdk.Maui.Manifest/WorkloadManifest.in.json`; it becomes installable
  when the `openharmony` workload is on a registered feed.
