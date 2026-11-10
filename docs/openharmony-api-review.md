# OpenHarmony slice — public API review

Status: baseline moved to the slice's real assembly and validated; the remaining item is the
upstream API review of the public feature/handler surface.

## Current baseline

- `src/Controls/src/Core/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt`
  - 8,859 lines total (the full `Microsoft.Maui.Controls` OpenHarmony API).
  - 782 lines / 90 types belong to the slice (`Microsoft.Maui.Platform.OpenHarmony*`).
- `src/Core/src/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt`
  - 2,824 lines: Core's platform-neutral API plus `Microsoft.Maui.IPlatformViewHandler`.

Before this phase, the slice baseline was 1,734 lines / 156 types under Core, where the slice no
longer compiles. The baseline now follows the assembly that actually contains the types.

## Internalized in this phase

Phase 2/3 implementation types were changed to `internal`:

- Essentials implementations: app info, battery/display, sensors, connectivity, contacts,
  clipboard, file system, pickers, permissions, launchers, browser/share, notifications,
  preferences, geocoding/haptics/flashlight, calendar/email/SMS/phone, printing, diagnostics,
  dispatcher, screenshot, ticker, alert/menu/window/shell helpers.
- Compositor/windowing internals: window surface/renderer/input router/decoration, overlay and
  animation helpers, handler connector, JSON context, font manager, text/styled view helpers.

This reduced the slice surface from 1,734 lines / 156 types to 782 lines / 90 types (66 types
internalized). The `PublicApiAnalyzers` validation (Release, `PublicApiType=Validate`) passes for
both Core and Controls.

## Kept public

- `MauiOpenHarmonyExtensions.UseOpenHarmony` and `OpenHarmonyMauiAppHost` (platform entry points).
- `OpenHarmonyView`, `OpenHarmonyViewHandler<TVirtualView>` and the `OpenHarmony*Handler` classes
  (MAUI handler extension/customization pattern).
- Accessibility API (`OpenHarmonyAccessibility`, action/node types).
- Feature APIs an app may call directly: Map/Push/Account/LiveView/TextToSpeech/SecureStorage,
  media surface/handler and their event/result types, `OpenHarmonyCollectionViewExtensions`.
- These feature APIs were kept public because the workload harness and apps may legitimately use
  them; internalizing them would require updating those callers and an API decision.

## Remaining before an upstream PR

The concrete proposal is in [`openharmony-api-proposal.md`](openharmony-api-proposal.md)
(90 types, grouped with member counts and disposition).

1. Submit the public feature/handler surface (90 slice types) to the MAUI API review, with
   signatures, platform semantics and compatibility notes.
2. After that review, internalize any feature types the API board classifies as implementation
   detail, and regenerate the baseline.
3. Keep the two baselines (`Core` and `Controls`) in sync with their assemblies; the
   `PublicApiAnalyzers` gate runs in the `--tfm` validation.

The API baseline and analyzer gate are now part of the real-TFM build; no compile-time API drift
can land without updating the baseline.
