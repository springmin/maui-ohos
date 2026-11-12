# Controls 41 public-type API review

Status: technical review complete; the visibility decisions still need the MAUI API board.
The PublicAPI baselines are the source of truth.

## 1. Count reconciliation

| Scope | Baseline | Types | Lines | Notes |
|---|---|---:|---:|---|
| Controls | `src/Controls/src/Core/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt` | **42** | 8,082 | 41 Controls-bound types + `MauiOpenHarmonyExtensions` (entry point, 0 members) |
| Core | `src/Core/src/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt` | **57** | 3,601 | 48 OpenHarmony types + 9 stock MAUI platform-neutral types |
| Proposal | `docs/openharmony-api-proposal.md` | **90** | — | 48 Core + 41 Controls + 1 entry point; every proposal type exists in a baseline |

The documented "41 Controls" is correct for the Controls-bound surface; the baseline's 42nd type
is the `MauiOpenHarmonyExtensions` entry point. The documented "48 Core" excludes nine stock MAUI
types that the Core baseline also contains:

`ElementExtensions`, `IImageSourcePartSetter`, `ImageSourcePartLoader`, `StrokeExtensions`,
`SwipeViewExtensions`, `TimeExtensions`, `ViewExtensions`, `WindowExtensions`, `WrapperView`.

The proposal table has no type that is absent from the baselines, so this is a counting/clarity
issue, not missing API. Recommend spelling the three buckets out in both review docs.

## 2. Findings

### 2.1 Media surface is the only large Controls implementation surface

`OpenHarmonyMediaElementSurface` has 29 members (15 methods, 14 properties/events). Several
methods are handler lifecycle plumbing rather than app API:

`Attach`, `Detach`, `AttachOverlay`, `DetachOverlay`, `Arrange`, `SetAspect`, `SetSource`, ...

The handler already exposes `PlayAsync`/`PauseAsync`/`SeekAsync`/`StopAsync` and the event
surface. **Recommendation:** internalize the handler-only lifecycle methods and keep the
app-facing playback/event members public. This is the largest safe reduction in the Controls
surface; it needs an API-board decision because a media app may use the surface directly today.

### 2.2 Accessibility contract is coherent

`OpenHarmonyAccessibilityNode` (30 members), `OpenHarmonyAccessibilityAction` (13 enum values)
and `OpenHarmonyAccessibility` (0 members) form a read-only (get/init, no public setters)
accessibility contract. Keep public for assistive tooling/diagnostics, but add XML docs for the
enums and node properties.

### 2.3 Handler set follows the MAUI pattern

29 `OpenHarmony*Handler` classes expose one public constructor each (Application/Layout/Media/
WebView/Window have additional mapper members). This matches the MAUI platform-handler pattern
and `UseOpenHarmony` registration. Keep public; internalizing them as a group remains a valid
alternative for the API board, but it would also internalize `OpenHarmonyView` and the mapper
extension points.

### 2.4 No mutable public setters

The Controls baseline has get/init accessors only; no `set ->` entries. Good for API review and
thread-safety.

### 2.5 XML documentation gaps

These public handler files contain no `///` XML docs (roughly the generated one-constructor
handlers): `OpenHarmonyBoxViewHandler.cs`, `OpenHarmonyContentViewHandler.cs`,
`OpenHarmonyFrameHandler.cs`, `OpenHarmonyPageHandler.cs`, `OpenHarmonyRefreshViewHandler.cs`,
`OpenHarmonySearchBarHandler.cs`, `OpenHarmonyLayoutHandler.cs`. Add at least a type summary and
constructor summary before an upstream PR. The media/accessibility/host files already carry docs.

### 2.6 Proposal count drift

`OpenHarmonyCollectionViewExtensions` is listed with 0 members but has four public extension
methods (`SetGroupCollapsed`, `IsGroupCollapsed`, `ToggleGroupCollapsed`,
`SetGroupHeaderTogglesCollapse`). Update the proposal's member count. The extension methods are
app-facing and should stay public.

### 2.7 Enums are API too

`OpenHarmonyAccessibilityAction` (13 values), `OpenHarmonyOpenWindowResult` (4 values) and
`OpenHarmonyMediaAspect` (2 values) are public enums; their values are part of the contract and
need docs/stability review.

## 3. Recommended disposition

| Group | Types | Recommendation |
|---|---|---|
| Entry point | `MauiOpenHarmonyExtensions` | public (counted separately from the 41) |
| Host/window/layout | `OpenHarmonyMauiAppHost`, `OpenHarmonyWindowHandler`, `OpenHarmonyLayoutHandler`, `OpenHarmonyPlatformApplication` | public |
| Handlers (29) | `OpenHarmony*Handler` | public (MAUI pattern) |
| Accessibility | `OpenHarmonyAccessibility`, `OpenHarmonyAccessibilityAction`, `OpenHarmonyAccessibilityNode` | public + docs |
| Media | `OpenHarmonyMediaElementHandler`, event/result types, `IOpenHarmonyMediaElementView` | public |
| Media surface | `OpenHarmonyMediaElementSurface` | keep app-facing playback/events public; internalize handler-only lifecycle methods |
| CollectionView helpers | `OpenHarmonyCollectionViewExtensions` | public |
| Result/enums | `OpenHarmonyOpenWindowResult`, `OpenHarmonyMediaAspect` | public + docs |

## 4. Requested decisions

1. Approve the handler set as public (current proposal) or internalize the handler + view base as
   one group.
2. Approve the media-surface reduction: internalize `Attach`/`Detach`/`AttachOverlay`/
   `DetachOverlay`/`Arrange`/`SetAspect`/`SetSource` while keeping playback/events public.
3. Confirm that the feature APIs (Core: Map/Push/Account/LiveView/TTS/SecureStorage/SubWindow)
   stay public; that is the separate 48-type review.
4. Require XML docs for the public types/enums before the upstream PR.

## 5. Reproduction

```sh
# type counts (strip accessor lines; count type declarations)
python3 - <<'PY'
import re
for name,path in {
  'Core': 'src/Core/src/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt',
  'Controls': 'src/Controls/src/Core/PublicAPI/net-openharmony/PublicAPI.Unshipped.txt',
}.items():
    types=set()
    for l in open(path, encoding='utf-8'):
        l=re.sub(r'^(static|virtual|abstract|override|sealed|readonly|const|extern|unsafe|partial)\s+','',l.strip())
        m=re.match(r'^Microsoft\.Maui\.Platform\.([A-Za-z0-9_]+)',l)
        if m: types.add(m.group(1))
    print(name, len(types))
PY
```

The analyzer gate remains `eng/openharmony/verify-slice.sh --tfm` (PublicApiAnalyzers Validate).
