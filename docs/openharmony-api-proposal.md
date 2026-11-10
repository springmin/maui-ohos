# OpenHarmony slice — public API proposal
Status: proposal for MAUI API review. This document covers the public slice surface after the
Phase 2/3 internalization. The authoritative signatures are in
`src/Controls/src/Core/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt`.

## Surface summary

- Slice API in Controls baseline: **782 lines / 90 public types**.
- Core baseline (`src/Core/src/PublicAPI/net-openharmony/`) is separate and platform-neutral.
- `PublicApiAnalyzers` Validate passes for both assemblies.
- The baseline contains no shipped API: all entries are in `PublicAPI.Unshipped.txt`.

## Internalized before this proposal

Implementation-only types were moved to `internal` before proposing the public surface:

- Essentials implementations (app info, battery/display, sensors, connectivity, contacts,
  clipboard, file system, pickers, permissions, launchers, browser/share, notifications,
  preferences, geocoding/haptics/flashlight, calendar/email/SMS/phone, printing, diagnostics,
  dispatcher, screenshot, ticker, alert/menu/window/shell helpers).
- Compositor/windowing internals (window surface/renderer/input router/decoration, overlay and
  animation helpers, handler connector, JSON context, font manager, text/styled view helpers,
  menu entry types, toolbar item).
- This removed 66 public types from the slice surface; the workload harness and app
  compile the slice sources directly, so they are unaffected by the visibility change.

## Proposed public types

### Entry points

| Type | Public members | Disposition |
|---|---:|---|
| `MauiOpenHarmonyExtensions` | 0 | public |
| `OpenHarmonyMauiAppHost` | 12 | public |

### View / handler base

| Type | Public members | Disposition |
|---|---:|---|
| `OpenHarmonyView` | 322 | public |
| `OpenHarmonyViewHandler<TVirtualView>` | 0 | public |

### Handlers

| Type | Public members | Disposition |
|---|---:|---|
| `OpenHarmonyActivityIndicatorHandler` | 1 | public |
| `OpenHarmonyApplicationHandler` | 3 | public |
| `OpenHarmonyBorderHandler` | 1 | public |
| `OpenHarmonyBoxViewHandler` | 1 | public |
| `OpenHarmonyButtonHandler` | 1 | public |
| `OpenHarmonyCarouselViewHandler` | 1 | public |
| `OpenHarmonyCheckBoxHandler` | 1 | public |
| `OpenHarmonyCollectionViewHandler` | 1 | public |
| `OpenHarmonyContentViewHandler` | 1 | public |
| `OpenHarmonyDatePickerHandler` | 1 | public |
| `OpenHarmonyEditorHandler` | 1 | public |
| `OpenHarmonyEntryHandler` | 1 | public |
| `OpenHarmonyFlyoutPageHandler` | 1 | public |
| `OpenHarmonyFrameHandler` | 1 | public |
| `OpenHarmonyGraphicsViewHandler` | 1 | public |
| `OpenHarmonyHybridWebViewHandler` | 3 | public |
| `OpenHarmonyImageButtonHandler` | 1 | public |
| `OpenHarmonyImageHandler` | 1 | public |
| `OpenHarmonyIndicatorViewHandler` | 1 | public |
| `OpenHarmonyLabelHandler` | 1 | public |
| `OpenHarmonyLayoutHandler` | 7 | public |
| `OpenHarmonyListViewHandler` | 1 | public |
| `OpenHarmonyMediaElementHandler` | 8 | public |
| `OpenHarmonyNavigationPageHandler` | 1 | public |
| `OpenHarmonyPageHandler` | 1 | public |
| `OpenHarmonyPickerHandler` | 1 | public |
| `OpenHarmonyProgressBarHandler` | 1 | public |
| `OpenHarmonyRadioButtonHandler` | 1 | public |
| `OpenHarmonyRefreshViewHandler` | 1 | public |
| `OpenHarmonyScrollViewHandler` | 1 | public |
| `OpenHarmonySearchBarHandler` | 1 | public |
| `OpenHarmonyShapeHandler` | 1 | public |
| `OpenHarmonyShellHandler` | 1 | public |
| `OpenHarmonySliderHandler` | 1 | public |
| `OpenHarmonyStepperHandler` | 1 | public |
| `OpenHarmonySwipeViewHandler` | 1 | public |
| `OpenHarmonySwitchHandler` | 1 | public |
| `OpenHarmonyTabbedPageHandler` | 1 | public |
| `OpenHarmonyTableViewHandler` | 1 | public |
| `OpenHarmonyTimePickerHandler` | 1 | public |
| `OpenHarmonyWebViewHandler` | 2 | public |
| `OpenHarmonyWindowHandler` | 6 | public |

### Accessibility

| Type | Public members | Disposition |
|---|---:|---|
| `OpenHarmonyAccessibility` | 0 | public |
| `OpenHarmonyAccessibilityAction` | 13 | public |
| `OpenHarmonyAccessibilityNode` | 30 | public |

### Media

| Type | Public members | Disposition |
|---|---:|---|
| `OpenHarmonyMediaAspect` | 2 | public |
| `OpenHarmonyMediaDurationChangedEventArgs` | 1 | public |
| `OpenHarmonyMediaElementSurface` | 29 | public |
| `OpenHarmonyMediaEndedEventArgs` | 2 | public |
| `OpenHarmonyMediaFailedEventArgs` | 2 | public |
| `OpenHarmonyMediaOpenedEventArgs` | 3 | public |
| `OpenHarmonyMediaPlaybackState` | 10 | public |
| `OpenHarmonyMediaPositionChangedEventArgs` | 1 | public |
| `OpenHarmonyMediaResult` | 9 | public |
| `OpenHarmonyMediaSizeChangedEventArgs` | 2 | public |
| `OpenHarmonyMediaSource` | 4 | public |
| `OpenHarmonyMediaSourceKind` | 3 | public |
| `OpenHarmonyMediaStateChangedEventArgs` | 2 | public |
| `OpenHarmonyMediaStatus` | 3 | public |
| `OpenHarmonyMediaStatusResult` | 15 | public |

### Feature APIs

| Type | Public members | Disposition |
|---|---:|---|
| `OpenHarmonyAccount` | 1 | public |
| `OpenHarmonyAccountResult` | 5 | public |
| `OpenHarmonyAccountStatus` | 13 | public |
| `OpenHarmonyLiveView` | 1 | public |
| `OpenHarmonyLiveViewStatus` | 24 | public |
| `OpenHarmonyLiveViewUpdate` | 14 | public |
| `OpenHarmonyMap` | 1 | public |
| `OpenHarmonyMapCapability` | 3 | public |
| `OpenHarmonyMapLauncher` | 5 | public |
| `OpenHarmonyMapMarker` | 12 | public |
| `OpenHarmonyMapMarkerClickEventArgs` | 1 | public |
| `OpenHarmonyMapRegion` | 10 | public |
| `OpenHarmonyMapRegionEventArgs` | 1 | public |
| `OpenHarmonyOpenWindowResult` | 4 | public |
| `OpenHarmonyPlatformApplication` | 2 | public |
| `OpenHarmonyPush` | 1 | public |
| `OpenHarmonyPushStatus` | 12 | public |
| `OpenHarmonyPushToken` | 5 | public |
| `OpenHarmonySecureStorage` | 5 | public |
| `OpenHarmonySubWindow` | 0 | public |
| `OpenHarmonySubWindowEventArgs` | 10 | public |
| `OpenHarmonySubWindowEventKind` | 18 | public |
| `OpenHarmonySubWindowTouchEventArgs` | 4 | public |
| `OpenHarmonyTextToSpeech` | 4 | public |

### Extensions

| Type | Public members | Disposition |
|---|---:|---|
| `OpenHarmonyCollectionViewExtensions` | 0 | public |

### Other

| Type | Public members | Disposition |
|---|---:|---|
| `IOpenHarmonyMediaElementView` | 0 | public |

## Rationale by group

**Entry points.** `MauiOpenHarmonyExtensions.UseOpenHarmony` and `OpenHarmonyMauiAppHost` are
the only application-facing setup/entry types. They must be public for `MauiProgram` and the
host bootstrap.

**Handlers and view base.** The `OpenHarmony*Handler` classes follow the MAUI platform-handler
pattern (public, mapper-based, customizable). `OpenHarmonyView` and
`OpenHarmonyViewHandler<TVirtualView>` are the platform view/base that public handlers expose; they
stay public with the handlers. If the API board prefers internal handlers, all of these become
internal together (the handlers are registered by `UseOpenHarmony`, not called by apps).

**Accessibility.** `OpenHarmonyAccessibility` + action/node records are the public accessibility
contract used by the shadow accessibility tree; they are kept public for assistive-tooling
integration and diagnostics.

**Media.** The media surface/handler and event/result types are kept public because a media app
may interact with the platform surface and observe playback events directly.

**Feature APIs.** Map/Push/Account/LiveView/TextToSpeech/SecureStorage and the sub-window API are
app-facing OpenHarmony capabilities that have no cross-platform MAUI equivalent. They are the
main API-review question: keep public (current proposal) or internalize and expose only through
the MAUI interfaces. Keeping them public preserves the documented device capabilities.

**Extensions.** `OpenHarmonyCollectionViewExtensions` is a small public helper over the
CollectionView platform behavior.

## API review checklist

- Namespace: all types under `Microsoft.Maui.Platform`; no new root namespace.
- Naming follows the MAUI `*Handler` / platform-view conventions.
- Nullability annotations are generated in the baseline (`#nullable enable`).
- No shipped API; no compatibility burden until the proposal is approved.
- Trimming/AOT: the slice is AOT-analyzed; `NativeAotSupported=false` for OpenHarmony.
- Handlers are mapper-based and customizable like the other MAUI platform handlers.

## Remaining decisions

1. Public vs internal for the feature APIs (Map/Push/Account/LiveView/TTS/SecureStorage/SubWindow).
2. Public vs internal for the handler set as a whole (all-or-nothing with the view base).
3. Whether the 322-member `OpenHarmonyView` should expose only the members handlers/apps need.

After the review, regenerate the Controls baseline and re-run
`eng/openharmony/verify-slice.sh --tfm` (PublicApiAnalyzers Validate).
