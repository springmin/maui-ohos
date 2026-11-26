#!/bin/sh
# verify-slice.sh - build and test the OpenHarmony platform slice and the real MAUI TFMs.
#
# This is the external-platform gate for the net*-openharmony band. The platform workload is not
# part of the default MAUI toolchain, so this script is not wired into the default CI job: it is
# invoked by the OpenHarmony workload CI (or locally) with the workload packs available.
#
# Modes:
#   (default)     build the plain net11.0 compile vehicle and run the host-runnable unit tests
#   --tfm         build Microsoft.Maui.Core and Microsoft.Maui.Controls for
#                 net11.0-openharmony<api> with warnings-as-errors (the real-TFM gate)
#   --slice-tfm   build only the standalone slice vehicle for the real TFM
#   --all         --tfm plus the default build/tests
#
# The Core/Controls build is the gate that matters for upstream review: it compiles the platform
# slice in its real assemblies, with the workload framework reference, OPENHARMONY symbols and the
# platform analyzer. The unit tests stay on net11.0 because a net11.0 test host cannot reference a
# platform-TFM assembly.
#
# Usage:
#   eng/openharmony/verify-slice.sh [--tfm|--slice-tfm|--all]
#
# Env:
#   DOTNET                            dotnet host (default: dotnet)
#   OpenHarmonyHostingAssembly        Microsoft.OpenHarmony.Hosting.dll
#   OpenHarmonyGraphicsAssembly       Microsoft.OpenHarmony.Maui.Graphics.dll
#   OpenHarmonyTargetFrameworkVersion API band for --tfm (default 20.0)
#   VERIFY_MSBUILD_ARGS               extra MSBuild/restore switches, word-split
# Exit: 0 = every selected gate passed; 1 = missing workload inputs or a failing gate.
set -eu

W="$(cd "$(dirname "$0")/../.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
SLICE="$W/src/Core/src/Platform/OpenHarmony/Microsoft.Maui.Platform.OpenHarmony.csproj"
CORE="$W/src/Core/src/Core.csproj"
CONTROLS="$W/src/Controls/src/Core/Controls.Core.csproj"
BINDING_SOURCEGEN="$W/src/Controls/src/BindingSourceGen/Controls.BindingSourceGen.csproj"
HYBRID_SOURCEGEN="$W/src/Core/src/HybridWebViewSourceGen/Core.HybridWebViewSourceGen.csproj"
TESTS="$W/src/Core/tests/OpenHarmony.UnitTests/Microsoft.Maui.Core.OpenHarmony.UnitTests.csproj"
HOSTING="${OpenHarmonyHostingAssembly:-}"
GRAPHICS="${OpenHarmonyGraphicsAssembly:-}"
EXTRA_ARGS="${VERIFY_MSBUILD_ARGS:-}"
MODE=default

case "${1:-}" in
    --tfm) MODE=tfm ;;
    --slice-tfm) MODE=slice-tfm ;;
    --all) MODE=all ;;
    "") ;;
    *) printf 'usage: %s [--tfm|--slice-tfm|--all]\n' "$0" >&2; exit 2 ;;
esac

# Discover the assemblies in an installed workload layout. The ref pack carries the bridge
# surface; the runtime pack carries the graphics backend. The newest pack wins.
discover_pack() { # <file-name> <ref-or-lib>
    _root="${DOTNET_ROOT:-$(dirname "$(command -v "$DOTNET")")}"
    if [ "$2" = ref ]; then
        _pattern='*/ref/net11.0/'
    else
        _pattern='*/runtimes/openharmony-arm64/lib/net11.0/'
    fi
    for _f in "$_root"/packs/Microsoft.OpenHarmony.Ref.*/$_pattern"$1" \
              "$_root"/packs/Microsoft.OpenHarmony.Runtime.*/$_pattern"$1"; do
        [ -f "$_f" ] && printf '%s\n' "$_f"
    done | sort | tail -1
}

if [ -z "$HOSTING" ]; then
    HOSTING="$(discover_pack Microsoft.OpenHarmony.Hosting.dll ref || true)"
fi
if [ -z "$GRAPHICS" ]; then
    GRAPHICS="$(discover_pack Microsoft.OpenHarmony.Maui.Graphics.dll lib || true)"
fi

case "$MODE" in
    slice-tfm|all|default)
        if [ -z "$GRAPHICS" ] || [ ! -f "$GRAPHICS" ]; then
            printf 'verify-slice: Microsoft.OpenHarmony.Maui.Graphics.dll not found.\n' >&2
            printf '  Install the microsoft.net.sdk.openharmony workload or pass OpenHarmonyGraphicsAssembly=<path>.\n' >&2
            exit 1
        fi
        ;;
esac
case "$MODE" in
    default)
        if [ -z "$HOSTING" ] || [ ! -f "$HOSTING" ]; then
            printf 'verify-slice: Microsoft.OpenHarmony.Hosting.dll not found.\n' >&2
            printf '  Install the microsoft.net.sdk.openharmony workload or pass OpenHarmonyHostingAssembly=<path>.\n' >&2
            exit 1
        fi
        ;;
esac

printf '== OpenHarmony slice ==\n'
printf 'mode:     %s\n' "$MODE"
printf 'dotnet:   %s\n' "$DOTNET"
printf 'hosting:  %s\n' "${HOSTING:-<from the workload framework reference>}"
printf 'graphics: %s\n' "${GRAPHICS:-<not needed>}"

# Shared compilation is disabled by default: the OpenHarmony host has a known Roslyn server
# livelock, and a single deterministic compile is the CI-safe behavior. Callers can re-enable it
# by putting -p:UseSharedCompilation=true in VERIFY_MSBUILD_ARGS.
RESTORE_ARGS="$EXTRA_ARGS"
BUILD_ARGS="-p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false -p:BuildInParallel=false -nr:false -m:1 $EXTRA_ARGS"

restore() { # <project> <extra args...>
    _project=$1; shift
    if [ "${VERIFY_NO_RESTORE:-0}" = "1" ]; then
        return 0
    fi
    # shellcheck disable=SC2086
    "$DOTNET" restore "$_project" -v:q --nologo $RESTORE_ARGS "$@"
}

build() { # <project> <label> <extra args...>
    _project=$1; _label=$2; shift 2
    printf '\n== build %s (warnings are errors) ==\n' "$_label"
    # shellcheck disable=SC2086
    "$DOTNET" msbuild "$_project" -t:Build -p:Configuration=Release -v:m -nologo \
        $BUILD_ARGS "$@"
}

build_slice_tfm() {
    _tfm="net11.0-openharmony${OpenHarmonyTargetFrameworkVersion:-20.0}"
    restore "$SLICE" -p:OpenHarmonySliceTargetFramework="$_tfm" \
        -p:OpenHarmonyGraphicsAssembly="$GRAPHICS" \
        ${HOSTING:+-p:OpenHarmonyHostingAssembly="$HOSTING"}
    build "$SLICE" "$_tfm slice" \
        -p:OpenHarmonySliceTargetFramework="$_tfm" \
        -p:OpenHarmonyGraphicsAssembly="$GRAPHICS" \
        ${HOSTING:+-p:OpenHarmonyHostingAssembly="$HOSTING"}
}

build_core_controls_tfm() {
    _tfm="net11.0-openharmony${OpenHarmonyTargetFrameworkVersion:-20.0}"
    # Restore the source generators with their own TFM first: the global TargetFramework used to
    # select Core/Controls would otherwise force net11.0-openharmony onto them. Controls' own
    # restore can overwrite them, so restore them again afterwards.
    restore "$BINDING_SOURCEGEN" -p:TargetFramework=netstandard2.0
    restore "$HYBRID_SOURCEGEN" -p:TargetFramework=netstandard2.0
    restore "$CORE" -p:TargetFramework="$_tfm" -p:MauiPlatforms="$_tfm"
    restore "$CONTROLS" -p:TargetFramework="$_tfm" -p:MauiPlatforms="$_tfm"
    restore "$BINDING_SOURCEGEN" -p:TargetFramework=netstandard2.0
    restore "$HYBRID_SOURCEGEN" -p:TargetFramework=netstandard2.0

    build "$CORE" "$_tfm Microsoft.Maui.Core" \
        -p:TargetFramework="$_tfm" -p:MauiPlatforms="$_tfm"
    build "$CONTROLS" "$_tfm Microsoft.Maui.Controls" \
        -p:TargetFramework="$_tfm" -p:MauiPlatforms="$_tfm"
}

build_tests() {
    printf '\n== build net11.0 slice vehicle (warnings are errors) ==\n'
    restore "$SLICE" -p:OpenHarmonyHostingAssembly="$HOSTING" \
        -p:OpenHarmonyGraphicsAssembly="$GRAPHICS"
    build "$SLICE" "net11.0 slice vehicle" \
        -p:OpenHarmonyHostingAssembly="$HOSTING" \
        -p:OpenHarmonyGraphicsAssembly="$GRAPHICS"

    printf '\n== host unit tests ==\n'
    restore "$TESTS" -p:OpenHarmonyHostingAssembly="$HOSTING" \
        -p:OpenHarmonyGraphicsAssembly="$GRAPHICS"
    build "$TESTS" "net11.0 unit test host" \
        -p:OpenHarmonyHostingAssembly="$HOSTING" \
        -p:OpenHarmonyGraphicsAssembly="$GRAPHICS"
    TEST_DLL="$W/artifacts/bin/Microsoft.Maui.Core.OpenHarmony.UnitTests/Release/net11.0/Microsoft.Maui.Core.OpenHarmony.UnitTests.dll"
    RUNNER=""
    for _root in "${NUGET_PACKAGES:-}" "$HOME/.nuget/packages"; do
        [ -n "$_root" ] || continue
        _candidate="$(find "$_root/xunit.runner.console" -path '*/tools/netcoreapp2.0/xunit.console.dll' 2>/dev/null | sort | tail -1)"
        [ -n "$_candidate" ] && { RUNNER="$_candidate"; break; }
    done
    if [ -n "$RUNNER" ] && [ -f "$TEST_DLL" ]; then
        # The VSTest host resolves DOTNET_ROOT through a home-directory symlink on the
        # OpenHarmony development host; the xunit console runner is host-independent.
        DOTNET_ROLL_FORWARD=LatestMajor "$DOTNET" "$RUNNER" "$TEST_DLL" -nologo
    else
        # shellcheck disable=SC2086
        "$DOTNET" test "$TESTS" -c Release -v:m --nologo --no-build --no-restore \
            $EXTRA_ARGS -p:UseSharedCompilation=false \
            -p:OpenHarmonyHostingAssembly="$HOSTING" \
            -p:OpenHarmonyGraphicsAssembly="$GRAPHICS"
    fi
}

case "$MODE" in
    default) build_tests ;;
    slice-tfm) build_slice_tfm ;;
    tfm) build_core_controls_tfm ;;
    all) build_core_controls_tfm; build_slice_tfm; build_tests ;;
esac

printf '\nverify-slice: OK\n'
