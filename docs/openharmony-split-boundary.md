# OpenHarmony slice split boundary

This document records the dependency boundary behind
`src/Core/src/Platform/OpenHarmony/OpenHarmonyCoreSlice.props`, the single source of truth for
which platform files compile into `Microsoft.Maui.Core` and which stay in
`Microsoft.Maui.Controls`. It exists so the split can be audited without re-running the probes.

## Criterion

A slice file belongs to Core only when it compiles against Core/workload contracts alone.
If it consumes a `Microsoft.Maui.Controls` type (directly or through a Controls-bound slice
helper), it stays in Controls: moving it would make Core reference Controls, which the MAUI
architecture forbids. Public API entries live with the assembly that contains the type, so the
move also moves the corresponding `PublicAPI.Unshipped.txt` lines.

## Current split

- Slice `.cs` files: **149** (the split extracted shared helpers into new files, so
  this is larger than the original 142).
- `Microsoft.Maui.Core`: **76** files (`OpenHarmonyCoreSlice.props`).
- `Microsoft.Maui.Controls`: **73** files:
  - **62** consume Controls types directly (the table below);
  - **10** are blocked by a Controls-bound slice helper;
  - **1** is the slice-wide `GlobalUsings.OpenHarmony.cs` (System namespaces only,
    shared by every slice file).

## Direct Controls consumers

| File | Controls types it uses |
| --- | --- |
| `MauiOpenHarmonyExtensions.cs` | BoxView, CarouselView, CollectionView, Editor, FlyoutPage, Frame, IAlertManager, ImageButton, ListView, NavigationPage, Page, RefreshView, Shell, SwipeView, TabbedPage, TableView, TemplatedView |
| `OpenHarmonyAccessibility.cs` | Controls, FlyoutPage, NavigationPage, Shell, TabbedPage, VisualElement |
| `OpenHarmonyAlertManager.cs` | Controls, Internals, Platform |
| `OpenHarmonyAppLinks.cs` | Controls, Window |
| `OpenHarmonyApplicationHandler.cs` | Quit, Window |
| `OpenHarmonyBlazorWebViewHandler.cs` | VisualElement |
| `OpenHarmonyBoxViewHandler.cs` | Controls |
| `OpenHarmonyButtonHandler.cs` | Button, FileImageSource, LineBreakMode |
| `OpenHarmonyCarouselView.cs` | Controls |
| `OpenHarmonyCarouselViewHandler.cs` | Controls |
| `OpenHarmonyCollectionViewExtensions.cs` | Controls |
| `OpenHarmonyCollectionViewHandler.cs` | Controls |
| `OpenHarmonyContentArrange.cs` | Controls |
| `OpenHarmonyContentViewHandler.cs` | Controls |
| `OpenHarmonyDragAndDrop.cs` | Controls |
| `OpenHarmonyEditorHandler.cs` | Editor, InputView, IsFocusedPropertyKey, VisualElement |
| `OpenHarmonyEntryHandler.cs` | Editor, Entry, InputView, IsFocusedPropertyKey, VisualElement |
| `OpenHarmonyEssentialsExtras.cs` | Controls |
| `OpenHarmonyFlowDirection.cs` | Controls |
| `OpenHarmonyFlyoutPageHandler.cs` | Controls, VisualElement |
| `OpenHarmonyFocusManager.cs` | Controls |
| `OpenHarmonyFormattedText.cs` | Controls, Label, Span |
| `OpenHarmonyFrameHandler.cs` | Controls |
| `OpenHarmonyGestures.cs` | Controls |
| `OpenHarmonyHandlerConnector.cs` | NavigationPage |
| `OpenHarmonyImageButtonHandler.cs` | BorderColor, BorderWidth, Controls, CornerRadius, ImageButton |
| `OpenHarmonyImageHandler.cs` | Controls, VisualElement |
| `OpenHarmonyItemListMaterializer.cs` | Controls |
| `OpenHarmonyKeyboardAcceleratorManager.cs` | Activate, Controls, KeyboardAccelerator |
| `OpenHarmonyLabelHandler.cs` | FormattedText, Label, LineBreakMode, MaxLines, Padding |
| `OpenHarmonyLayoutHandler.cs` | Element, Layout |
| `OpenHarmonyListViewHandler.cs` | Controls |
| `OpenHarmonyMauiAppHost.cs` | Application, Editor, Entry, VisualElement, Window |
| `OpenHarmonyMenus.cs` | Controls, Window |
| `OpenHarmonyNavigationPageHandler.cs` | Controls |
| `OpenHarmonyOverlaySlotWatch.cs` | Controls |
| `OpenHarmonyOverlaySuspension.cs` | Controls |
| `OpenHarmonyPageHandler.cs` | Controls |
| `OpenHarmonyPageTransitions.cs` | Controls |
| `OpenHarmonyPinch.cs` | Controls, Internals |
| `OpenHarmonyPointer.cs` | Controls |
| `OpenHarmonyRadioButtonHandler.cs` | RadioButton |
| `OpenHarmonyRefreshViewHandler.cs` | Controls |
| `OpenHarmonySafeAreaArrange.cs` | Controls |
| `OpenHarmonySearchBarHandler.cs` | IsFocusedPropertyKey, SearchBar, VisualElement |
| `OpenHarmonySharedTransition.cs` | Controls |
| `OpenHarmonyShellChrome.cs` | Controls |
| `OpenHarmonyShellExtras.cs` | Controls, Label, SearchHandler |
| `OpenHarmonyShellFlyout.cs` | Controls |
| `OpenHarmonyShellHandler.cs` | Controls, FileImageSource, ImageSource, NavBarIsVisibleProperty, Page, VisualElement |
| `OpenHarmonySwipeViewHandler.cs` | Controls |
| `OpenHarmonyTabbedPageHandler.cs` | FileImageSource, ImageSource, MultiPage, Page, TabbedPage, VisualElement |
| `OpenHarmonyTableViewHandler.cs` | Controls |
| `OpenHarmonyTheme.cs` | Controls |
| `OpenHarmonyTitleBar.cs` | CanConsumeBackNavigation, Controls, SolidColorBrush, TitleBar, VisualElement, Window |
| `OpenHarmonyToolTipManager.cs` | Controls, TextProperty, View |
| `OpenHarmonyToolbarMirror.cs` | Controls |
| `OpenHarmonyWebViewHandler.cs` | Controls |
| `OpenHarmonyWindowHandler.cs` | Window |
| `OpenHarmonyWindowHost.cs` | Window |
| `OpenHarmonyWindowOverlay.cs` | Controls |
| `OpenHarmonyWindowRenderer.cs` | CarouselView, Controls, FlyoutPage, Image, InputView, Layout, NavigationPage, Shell, TabbedPage, VisualElement |

## Blocked by a Controls-bound slice helper

| File | Blocking slice types |
| --- | --- |
| `OpenHarmonyAppInfo.cs` | OpenHarmonyTheme |
| `OpenHarmonyBlazorWebViewHandler.Stub.cs` | OpenHarmonyBlazorWebViewHandler |
| `OpenHarmonyHybridWebViewHandler.cs` | IOpenHarmonyOverlaySlotLifetime, OpenHarmonyMauiAppHost, OpenHarmonySliceJsonContext, OpenHarmonyWebViewHandler |
| `OpenHarmonyMauiApplication.cs` | MauiOpenHarmonyExtensions, OpenHarmonyKeyboardAcceleratorManager, OpenHarmonyMauiAppHost, OpenHarmonyPlatformApplication, OpenHarmonyWindowHandler, OpenHarmonyWindowOverlayHost |
| `OpenHarmonyMediaElement.cs` | OpenHarmonyHybridWebViewHandler, OpenHarmonyMauiAppHost, OpenHarmonyMediaElementHandler, OpenHarmonySliceJsonContext, OpenHarmonyWebViewHandler |
| `OpenHarmonyMediaElementHandler.cs` | IOpenHarmonyMediaElementView, IOpenHarmonyOverlaySlotLifetime, MauiOpenHarmonyExtensions, OpenHarmonyMauiAppHost, OpenHarmonyMediaElementSurface |
| `OpenHarmonySemanticScreenReader.cs` | OpenHarmonyAccessibility |
| `OpenHarmonySliceJsonContext.cs` | OpenHarmonyBlazorWebViewHandler, OpenHarmonyHybridWebViewHandler, OpenHarmonyMediaElementSurface, OpenHarmonyWebViewHandler |
| `OpenHarmonyStyledViews.cs` | OpenHarmonyFormattedLine, OpenHarmonyFormattedSegment, OpenHarmonyFormattedTextLayout, OpenHarmonyLabelHandler, OpenHarmonyTextRun |
| `OpenHarmonyWindowInputRouter.cs` | OpenHarmonyMauiAppHost |

## Seam candidates (future work, not missing from this split)

A few blocked files are one small seam away from Core, but each seam changes how a
Controls-bound helper is consumed, so they are deliberate follow-ups rather than omissions:

- `OpenHarmonyMediaElement{,Handler}` / `OpenHarmonyHybridWebViewHandler` / `OpenHarmonyWindowInputRouter`
  use `OpenHarmonyMauiAppHost.ResolveWindowId`; a Core window-registry seam would unlock them.
- `OpenHarmonySliceJsonContext` roots WebView/Hybrid/Blazor wire types; its WebView entries
  must stay with the Controls handlers unless each wire type gets a Core context.
- `OpenHarmonyStyledViews` uses the formatted-text layout engine, which currently measures
  through `OpenHarmonyLabelHandler` (Controls); injecting a measure delegate would split it.
- `OpenHarmonyAppInfo` uses `OpenHarmonyTheme`, `OpenHarmonySemanticScreenReader` uses
  `OpenHarmonyAccessibility`; both helpers are Controls-bound by their APIs.
- `OpenHarmonyMauiApplication` installs `OpenHarmonyWindowOverlayHost` from the renderer side;
  the application object follows the host/window split.

## Verification

- `verify-slice.sh --tfm`: Core + Controls + `PublicApiAnalyzers` pass.

- The 826-check interaction harness (`ohos-workload/test/maui-platform-verify`) passes, and
  its 826 `[verify]` lines are identical to the pre-split baseline after normalizing paths and
  timings.

- The union of the two `PublicAPI.Unshipped.txt` baselines is identical, as a set of lines, to the
  pre-split baseline (11,682 lines; zero additions or removals).

