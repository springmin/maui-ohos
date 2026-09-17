# Microsoft.Maui.Platform.OpenHarmony (platform slice, start)

Platform services for MAUI on OpenHarmony. The project builds standalone (`net11.0`) against
`Microsoft.Maui.Core`/`Microsoft.Maui.Graphics` (11.0.0-rc.1.26451.6) and the platform bridge
that ships in the `microsoft.net.sdk.openharmony` workload
(`Microsoft.OpenHarmony.Hosting`).

| Type | Purpose |
|---|---|
| `OpenHarmonyDispatcher` (`IDispatcher`) | queues work and drains it on the XComponent frame ticks (with a safety-net timer); `CreateTimer()` returns a frame-driven `IDispatcherTimer` |
| `OpenHarmonyWindowSurface` | tracks `OpenHarmonyBridge.SurfaceChanged` and hands out `ICanvas` instances (`Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas`) plus `Present()` |
| `OpenHarmonyView` | the compositor's platform view: arranged frame, text/background, tap routing |
| `OpenHarmonyWindowRenderer` | measures/arranges the MAUI tree, draws it through the MauiGraphics canvas and routes touches (deepest interactive view first) |
| `OpenHarmonyLabelHandler` / `OpenHarmonyButtonHandler` / `OpenHarmonyLayoutHandler` | handlers for `ILabel`/`IButton`/`ILayout` (also an `ILayoutHandler`), including `GetDesiredSize` from platform text metrics and cross-platform measure/arrange |
| `MauiOpenHarmonyExtensions.UseOpenHarmony()` | registers the services and the handlers with `MauiAppBuilder` |

Build:

```sh
dotnet build src/Core/src/Platform/OpenHarmony/Microsoft.Maui.Platform.OpenHarmony.csproj \
  -p:OpenHarmonyHostingAssembly=<path to Microsoft.OpenHarmony.Hosting.dll>
```

## Verification

`Microsoft.Maui.Platform.OpenHarmony` compiles standalone and a harness driven with real
MAUI controls (the `Microsoft.Maui.Controls` package) exercises the pipeline:

```
[verify] VerticalStackLayout frame=0,0,1080x1920
           Label  frame=24,24,1032x54  text='MAUI on OpenHarmony'
           Button frame=24,94,1032x51  text='Tap me'
           Label  frame=24,161,1032x32 text='Ready'
[verify] tap at 540,119 handled(down=True, up=True) clicks=1
```

i.e. handling, measure/arrange, drawing and touch all go through our platform code.

Next steps for the slice:
1. `WindowHandler` mapping (`Microsoft.Maui.Handlers.WindowHandler`): window content rendered
   through `OpenHarmonyWindowSurface` (canvas or ArkUI nodes via
   `OpenHarmonyBridge.NodeContent`).
2. First view handlers (Label/Button/Layout) over the same canvas calls the workload's demo
   app uses; touch comes from `OpenHarmonyBridge.Touch`.
3. Wire the handlers into the MAUI build once the `net11.0-openharmony*` TFM participates in
   the repo's platform matrix (the workload already enables it via
   `IncludeOpenHarmonyTargetFrameworks`).
