# OpenHarmony platform slice: implementation notes

Long-form rationale for the OpenHarmony platform slice sources
(`src/Core/src/Platform/OpenHarmony/**`). Each source file keeps a short header and points
here; the moved notes are grouped by surface below and are otherwise unchanged.

## Platform application object (`OpenHarmonyMauiApplication`)

Platform application object for the OpenHarmony slice (MAUI's IPlatformApplication).

Named like Tizen's MauiApplication: the existing OpenHarmonyPlatformApplication type in
OpenHarmonyApplicationHandler.cs is the IApplication handler's platform element (a different
role), so this entry point must not reuse that name.

MAUI's platform entry point publishes the application's root service provider and the current
IApplication through the static IPlatformApplication.Current, so class libraries can reach
platform services without a platform reference (Tizen sets it from MauiApplication's
constructor, Windows/iOS from their platform application classes). The slice's host
(OpenHarmonyMauiAppHost) owns window creation and never had that companion object; this type is
it, wired without touching the host:

  * UseOpenHarmony registers OpenHarmonyMauiApplication plus an IMauiInitializeService shim.
    MauiAppBuilder.Build() runs every IMauiInitializeService (the documented contract, verified
    against Microsoft.Maui.Core 11.0.0-rc.1.26451.6), so the singleton is created and
    IPlatformApplication.Current is set before an app calls OpenHarmonyMauiAppHost.Run.
  * Services is the provider MauiAppBuilder.Build() handed the initializer. In rc.1 Build runs
    initializers inside a service scope (verified: ServiceProviderEngineScope), so this is that
    scope, not MauiApp.Services' root ServiceProvider; every singleton is shared with the root
    (IApplication, the app host and this object are the same instances through both, verified
    off-device), which is what "services resolve from the MauiApp builder" requires.
  * Application resolves the IApplication singleton that UseMauiApp<TApp> registered and that
    OpenHarmonyMauiAppHost.Run consumes. It is resolved lazily: during Build the app object may
    not exist yet, and MAUI's own platforms also leave the application unset until startup.

Zero-reference interfaces, status documented once (no silent gaps):

  * ITitleBar / Microsoft.Maui.Controls.Window.TitleBar (rc.1: ITitleBar is IContentView +
    IPadding + ICrossPlatformLayout with Title/Subtitle/PassthroughElements; Window.TitleBar is
    a bindable property). Wired: OpenHarmonyWindowHandler maps TitleBar onto the compositor-owned
    OpenHarmonyTitleBarRow. The row takes the top of the surface (the TitleBar's HeightRequest,
    else the 56px chrome height), measures/arranges and draws the TitleBar's template subtree
    inside it (Title/Subtitle/Leading/Content/Trailing all draw through the normal walk), and
    arranges the window content below; touches route into the template's views, and
    TitleBar.IsVisible = false gives the surface back to the content. The window's back
    affordance occupies the leading slot: while the window's page tree can consume a back press
    (modal stack, NavigationPage stack, presented flyout, Shell stack - the slice's mirror of
    Controls' internal Window.CanConsumeBackNavigation), the row draws the shell chrome's
    chevron there and a tap invokes IWindow.BackButtonClicked (a custom OnBackButtonPressed
    override still wins). Still open: the ArkUI shell keeps its own window decorations, so
    system minimize/maximize/close buttons and drag regions are not mapped onto the row, and
    the row is not yet part of the accessibility shadow tree.

  * IKeyboardAccelerator (the per-element collection). rc.1 exposes the interface
    (Modifiers/Key) and the Microsoft.Maui.Controls.KeyboardAccelerator object, but the only
    collection is MenuFlyoutItem.KeyboardAccelerators - there is no VisualElement-level
    collection or mapper. The slice's OpenHarmonyKeyboardAcceleratorManager keeps its internal
    registry fed by the hardware-key listener (PE2's OpenHarmonyKeyListener). A real
    per-element surface needs MAUI to add VisualElement.KeyboardAccelerators (or an attached
    property) plus a ViewMapper entry so the platform can observe the collection, and a
    consuming key contract (bool OnKeyDown) so a match can stop the key from propagating; the
    host callback is void, so the slice only records and invokes.

  * IAdorner (rc.1: IAdorner is IWindowOverlayElement + Density + VisualView; Controls'
    VisualDiagnosticsOverlay.AddAdorner builds one to frame a view). Wired: the window handler
    initializes Window.VisualDiagnosticsOverlay when it attaches to a window
    (OpenHarmonyWindowHandler.MapContent, the Tizen hook), the overlay host draws it next to
    IWindow.Overlays, and a frame-tick signature check requests a redraw when an adorner was
    added or removed (Controls' Invalidate is a no-op on the platform-less build, so the host
    cannot be asked directly; one frame of latency). Remaining gap: the element selector's
    tap-to-select never fires for the package overlay because its Tapped raiser is internal to
    Microsoft.Maui, and the diagnostics overlay does not suppress touch passthrough (recorded
    flags only, like OpenHarmonyWindowOverlay's documented limitation).

## Bluetooth GATT (`OpenHarmonyBluetoothGatt`)

Bluetooth GATT client for OpenHarmony - a platform extension, NOT a MAUI core interface.

MAUI has no BLE/GATT API (there is no IBle/IBluetooth interface to implement), so this type
is an OpenHarmony-specific extra compiled into the platform slice. Apps that want BLE link it
explicitly; nothing in MAUI references it.

It rides the established host/ArkTS request-response bridge (the same shape as
OpenHarmonyBluetooth/OpenHarmonyPrinting):

  managed call -> P/Invoke ohos_host_bluetooth_gatt_request(requestId, op, payload) ->
    ArkTS shell sink (host.registerBluetoothGattSink) requests ohos.permission.ACCESS_BLUETOOTH
    when needed, lazily imports @kit.ConnectivityKit and runs the kotlin-free GATT calls on
    ble.createGattClientDevice(address) (connect/disconnect, getServices, read/write
    characteristic and descriptor, setCharacteristicChangeNotification, setBLEMtuSize) ->
    host.notifyBluetoothGattResult(requestId, code, payload) ->
    managed OnResultNative completes the awaiting Task.

Unsolicited device events (characteristic value changed, connection state changed, MTU
changed) are pushed by the same shell sink through host.notifyBluetoothGattEvent(payload) and
arrive on the events below: ValueChanged, ConnectionStateChanged, MtuChanged. The push is a
separate export (ohos_host_bluetooth_gatt_register_event), so an older host library that only
serves the request/response half still works - the events then never fire.

Result codes: 0 = the request completed (an empty success payload is a valid answer),
-1 = the platform path is unavailable (no host library, no shell sink, the Connectivity Kit is
missing or the ACCESS_BLUETOOTH permission was denied), -2 = a transient kit failure (the
adapter is off, the device is not connected, the SDK rejected the argument). -1 flips
IsSupported false and keeps it there; -2 only fails that one call. A failed request may carry
a diagnostic message that is logged, never thrown.

Permissions: ohos.permission.ACCESS_BLUETOOTH (user_grant; requested at call time by the
shell). Without the manifest declaration the shell answers -1 instead of guessing.

Wire format. Requests carry tab-separated fields (the managed side rejects any field that
contains a tab/LF/CR, so no escaping is needed in that direction). op and fields:
  0 connect                address
  1 disconnect             address
  2 services               address
  3 read characteristic    address, serviceUuid, characteristicUuid
  4 write characteristic   address, serviceUuid, characteristicUuid, writeType (1 with
                           response / 2 without), valueBase64
  5 notifications          address, serviceUuid, characteristicUuid, enable (0/1)
  6 read descriptor        address, serviceUuid, characteristicUuid, descriptorUuid
  7 write descriptor       address, serviceUuid, characteristicUuid, descriptorUuid, valueBase64
  8 request MTU            address, mtu
  9 release                address
Success payloads: op 2 answers one "serviceUuid\tsPrimary\tcharacteristicUuid\tproperties"
record per characteristic (4 escaped fields per line, the same OpenHarmonyKitRecords shape
the other extras use; a service without characteristics carries empty uuid/properties), op
3/6 answer the raw value as base64, op 8 the negotiated MTU in decimal (empty when the
platform did not report one), and the remaining ops answer empty.

Event payloads (host.notifyBluetoothGattEvent), tab-separated:
  value  address, serviceUuid, characteristicUuid, valueBase64
  state  address, state (ProfileConnectionState), reason (decimal, empty when absent)
  mtu    address, mtu (decimal)
Malformed event payloads are ignored; a value event whose base64 does not decode is ignored.

## Keyboard accelerators (`OpenHarmonyKeyboardAcceleratorManager`)

Keyboard accelerators for the OpenHarmony slice's hardware-key surface.

MAUI rc.1 surface (verified against Microsoft.Maui.Controls 11.0.0-rc.1.26451.6):
  * Microsoft.Maui.Controls.KeyboardAccelerator (BindableObject: Key string, Modifiers
    KeyboardAcceleratorModifiers; IKeyboardAccelerator) and the public flag enum
    Microsoft.Maui.KeyboardAcceleratorModifiers (None/Shift/Ctrl/Alt/Cmd/Windows);
  * the only collection on the public surface is MenuFlyoutItem.KeyboardAccelerators
    (IList<KeyboardAccelerator>) - there is no VisualElement/Element.KeyboardAccelerators in
    rc.1. On this slice MenuFlyoutItem has no platform view/handler at all, so there is no
    public per-element accelerator surface to consume automatically.
The manager therefore keeps the documented internal registry:
  * Register(element, accelerator|key, modifiers, invoked?) records an accelerator for a
    BindableObject (a Button/ImageButton/MenuFlyoutItem or any element with a Command) plus an
    optional callback; RegisterElementAccelerators reads the one rc.1 collection
    (MenuFlyoutItem.KeyboardAccelerators) when a caller hands the item in.
  * matching runs from PE2's OpenHarmonyKeyListener.KeyEvent surface (the raw ArkUI
    (keyCode, KeyType) callback), so the host needs no change: the manager subscribes on the
    first registration and unsubscribes with the last one (inert when unused).
  * on a match the element is invoked with the same semantics as a click: IButton.Clicked()
    (runs Command and raises Clicked), MenuFlyoutItem's IMenuItemController.Activate()
    (reflection: the rc.1 interface is internal), a registered callback, or a generic public
    Command property, in that order.

Host key codes: ArkUI forwards the numeric @ohos.multimodalInput.keyCode value and KeyType
(0 down / 1 up). The modifier letters are NOT part of the callback signature
(void (*)(int keyCode, int eventType) in the native host), so Ctrl/Shift/Alt/Meta are tracked
from the modifier keys' own down/up events (2045-2048 alt/shift, 2072-2073 ctrl,
2076-2077 meta). Matching requires the tracked modifier bits to match exactly
((event & Ctrl|Shift|Alt|Cmd|Windows) == (accelerator.Modifiers & ...)); the host Meta key
maps to Cmd|Windows so either flag matches, and Cmd never matches anything else.

Modifier semantics mapping (documented):
  ArkUI CTRL_LEFT/RIGHT (2072/2073) -> KeyboardAcceleratorModifiers.Ctrl
  ArkUI ALT_LEFT/RIGHT  (2045/2046) -> KeyboardAcceleratorModifiers.Alt
  ArkUI SHIFT_LEFT/RIGHT(2047/2048) -> KeyboardAcceleratorModifiers.Shift
  ArkUI META_LEFT/RIGHT (2076/2077) -> KeyboardAcceleratorModifiers.Cmd | Windows

Key names (documented subset; the listener comment is the source of the numeric values):
letters A-Z 2017-2042, digits 0-9 2000-2009 (also D0..D9/Number0..9), F1-F12 2090-2101,
arrows 2012-2015 (Up/ArrowUp, ...), Enter/Return 2054, Escape/Esc 2070, Tab 2049,
Space/Spacebar 2050, Backspace/Back 2055, Delete/Del/ForwardDelete 2071, Home 2081, End 2082,
PageUp 2068, PageDown 2069, Insert 2083, CapsLock 2074, NumLock 2102, Menu 2067,
OemComma/Comma 2043, OemPeriod/Period 2044, OemPlus/Equals/Plus 2058, OemMinus/Minus 2057,
OemQuestion/Slash 2064, Numpad0-9 2103-2112, NumpadEnter 2119. A Key that is all digits
matches the raw ArkUI code (an escape hatch for keys outside the subset). Names are matched
case-insensitively.

What a public rc.1 surface would need (documented once):
  1. VisualElement.KeyboardAccelerators (IList<KeyboardAccelerator>) or an attached property,
     with a property-changed push into the handler mapper (like ToolTipProperties.Text does);
  2. a ViewMapper entry (e.g. MapKeyboardAccelerators) so the platform can observe the
     collection per element without a slice-side registry;
  3. consumed-by-handler semantics on the key contract (an IKeyListener whose OnKeyDown
     returns bool / a VisualElement.KeyDown routed event) so a matched accelerator can stop
     ArkUI from also processing the key; the current host callback is void, so consumption
     cannot be reported back and the slice only records/invokes.

## Image decoding and drawing (`OpenHarmonyImageHandler`, `OpenHarmonyView`)

The slice has no native image widget: every image-like control maps onto
`OpenHarmonyView.ImageBytes` (encoded File/Stream/Uri bytes; a `FontImageSource` instead goes
through the compositor's text path, documented in the handler). The view draws the bytes
through the host canvas, and since P2b-IMG the decode is sized to the destination instead of
decoding the source at full resolution:

* **Generation.** Replacing `ImageBytes` (new source, reload, edited file) starts a new
  generation and resets the progressive state; the host cache is content-keyed, so a
  generation bump re-decodes once and repeated frames hit the cache.
* **Progressive pass.** A destination whose long edge is at least `ImagePreviewMinEdgePx`
  (128) first decodes a coarse preview at `destination / ImagePreviewDivisor` (8) through
  `OpenHarmonyCanvas.DrawImageBytesSized`, then calls `OpenHarmonyBridge.RequestRedraw`; the
  next frame decodes at the display size and replaces the preview. A smaller destination
  decodes once at its own size (no preview frame).
* **Failure.** A decode failure (preview or single pass) draws a neutral placeholder and is
  not retried for that generation (no per-frame decode loop); a failed display-size decode
  after a successful preview keeps the preview visible. Drawing never throws.
* **Older host.** `DrawImageBytesSized` returns null when the host library has no
  `ohos_host_draw_image_bytes_sized` (or no native host in tests); the view then uses the
  pre-P2b full-resolution `DrawImageBytes`, so behavior degrades to before the feature.
* **Test seams.** `ImageDrawRequested` / `ImagePlaceholderDrawn` receive the requesting view
  (other images in the tree keep drawing while one view's contract is pinned) and
  `ImageDrawOverride` replaces the host blit. The interaction suite pins preview -> final,
  the small single-pass path, the placeholder-once rule and the host contract.

Host side (`ohos_host_draw_image_bytes_sized`, `openharmony_host.c`): the decoder is asked
for the requested size through `OH_DecodingOptions_SetDesiredSize` (API 12+, resolved softly
so an image library without it keeps the full-resolution decode), each edge is clamped to
`OHOS_IMAGE_DECODE_MAX_EDGE` (4096), and the pixelmap cache key carries content hash, length
and the requested decode size, so a window resize misses once and the previous entry ages
out of the same LRU/byte budget. A local bench of a 3.27 MB JPEG (3400x2550, target
1032x200) measured 86.3 ms full decode vs 63.4 ms downsample and a retained bitmap of
34.7 MB vs 0.8 MB; the 4.77 MB 4000x3000 case was 117.3 ms vs 88.2 ms (48.0 MB vs 3.5 MB
retained). See the packaging doc's P2b-IMG section for the on-device commands.

Limits: `AspectFit` centers a square only (the platform view does not track intrinsic size
yet), the placeholder is the documented undecodable fallback, and animated formats are not
sampled - a still frame is drawn.

## Flow direction / RTL (`OpenHarmonyFlowDirection`, `OpenHarmonyView.CanvasFrame`)

Upstream MAUI leaves RTL layout mirroring to the platform: dotnet/maui#9558 removed the
cross-platform arrangement mirroring, so Android and iOS flip the platform placement of every
child whose parent layout is right-to-left, resolve Start/End text alignment against the view's
own direction and put trailing affordances at the physical leading edge. The slice reproduces
that contract on the managed canvas:

* **Flow map.** Every compositor walk (drawing, hit-testing, hover/pointer/pinch/drop
  resolution) carries an `OpenHarmonyFlowMap`, the affine map `x' = Scale * x + Offset` from a
  view's logical MAUI x coordinate to canvas space. A view whose effective direction is
  RightToLeft is a mirror boundary: its children's logical offsets are reflected within its
  physical frame (the native platforms' arrange flip). MatchParent inherits the parent's
  resolved direction; a walk root whose logical parents are outside the walk resolves from the
  nearest explicit ancestor and stays left-to-right without one. An LTR tree maps to itself, so
  the existing output is unchanged.
* **`Frame` stays logical.** Arrange-time readers (the page/tab/flyout/shell handlers that
  compute child frames, the list materializer, the item-list viewport math) keep reading the
  MAUI frame, which is what `IView.Frame` carries on the native platforms. Drawing, clipping,
  transforms, hit-testing, the focus ring, the accessibility bounds and the scrollbar geometry
  use `OpenHarmonyView.CanvasFrame`, the walk-mapped rectangle. The walk hands the map and the
  resolved direction to each platform view once per visit (`SetFlowContext`), so no per-frame
  parent walk is needed.
* **Direction-aware surfaces.** Text alignment (Start/End swap; Center/Justify unchanged), the
  entry's trailing clear button, the navigation/shell back slot and chevron, the toolbar items
  (trailing actions), the shell flyout hamburger/panel/menu rows, the flyout page panel, the
  picker dropdown (opens from the field's start edge), the calendar header arrows and the
  day/weekday columns, tabs, sliders, progress fills, switches, steppers and radio buttons all
  resolve their start/end edge from the view's own direction in both drawing and hit-testing. A
  `FlowDirection` change requests a repaint through the shared view-mapper hook
  (`OpenHarmonyLayoutRedraw`).
* **Limits (recorded).** The canvas text bridge does no bidi reordering, so RTL text keeps its
  logical glyph order and only the block's start edge moves. Horizontal scroll offsets stay
  physical (the slice's scroll physics are vertical-only). SwipeView reveal geometry stays in
  gesture space (the drag direction picks the side). The alert overlay keeps its fixed
  accept/cancel layout.

## Tabbed page content in the compositor walk (`OpenHarmonyWindowRenderer.ChildEnumerator`)

A device verification round (report conclusion 2 / finding 5.3-3: a TabbedPage demo drew its
bottom tab bar but a black body) traced the root cause to the compositor's allocation-free
`ChildEnumerator`: it yielded layout children, the content-view's presented content, the
`NavigationPage`'s current page and the flyout detail/panel, but had no case for
`TabbedPage.CurrentPage`, so the selected page's subtree never reached the frame (only the tab
bar did, because the platform view paints it itself). rc.1's `TabbedPage` is not an
`IContentView`, so the presented-content phase cannot cover it.

The enumerator now yields `TabbedPage.CurrentPage` exactly like `NavigationPage.CurrentPage` -
same `!ReferenceEquals(..., _presentedContent)` guard, chained right after the navigation-page
phase and before the flyout phases - so drawing, hit-testing, animation probing and the ordered
(ZIndex) walk all see the selected page.

Pinned by two interaction checks in `ohos-workload/test/maui-platform-verify` (452 -> 454,
floor 432 -> 434): `tabbed draw current` renders a two-page TabbedPage through a
text-recording canvas and requires the selected page's label in the frame with the unselected
page's label absent (the pre-fix slice drew neither, which is also the negative control: the
check fails with the case removed), and `tabbed draw after switch` taps the second tab and
requires the frame to follow `CurrentPage`. The pixel suite is unchanged and green.

The accessibility walk has its own children builder (`OpenHarmonyAccessibility.PushChildren`) with
the same omission, so the shadow tree of a TabbedPage did not include the selected page either.
It now pushes `TabbedPage.CurrentPage` with the same presented-content reference guard, mirroring
the compositor walk: the accessibility tree contains exactly the selected page's subtree and
follows a `CurrentPage` switch.

Pinned by two more interaction checks in `ohos-workload/test/maui-platform-verify` (468 -> 470,
floor 448 -> 450): `tabbed a11y current` publishes the shadow tree for the two-page TabbedPage and
requires the selected page's label node in it with the unselected page's label absent (the
pre-fix slice publishes neither, so the check fails with the branch removed), and `tabbed a11y
after switch` taps the second tab and requires the tree to follow `CurrentPage`.

## System font scale (`OpenHarmonyFontManager.SystemFontScale`)

OpenHarmony reports the user's font size setting as a scale factor; the native platforms apply
it at the text layer (Android sp-sized `TextView`s, iOS `UIFontMetrics`). This slice draws all
text itself, so the scale is applied in managed code at the one boundary every text path shares
(T21):

* **Publishing the value.** `OpenHarmonyFontManager.SetSystemFontScale(float)` stores the
  scale; `SystemFontScale` reads it. A non-finite or non-positive value resets the scale to 1,
  everything else clamps to 0.5..3. MAUI font values stay logical (a `Label.FontSize` of 20 is
  still 20; `GetFont` returns the requested font unchanged), and the pipeline multiplies by the
  scale when it measures or draws - the same split Android makes between sp text and the
  logical value the app set. The host is expected to re-arrange and redraw after publishing a
  changed value (the configuration-change path); the value alone cannot relayout the tree.
* **One scaled boundary.** Every logical size reaches the canvas through
  `OpenHarmonyFontManager.ScaleFontSize`: `OpenHarmonyLabelHandler.MeasureText` scales the
  native measurement and its arithmetic fallback, the plain and `FormattedText` run drawing in
  `OpenHarmonyTextView` scales `canvas.FontSize` (and the synthetic bold offset and decoration
  metrics), the entry/editor/searchbar text, placeholder, IME composition and per-character
  caret metrics scale in `OpenHarmonyView`, and the slice's self-drawn chrome (picker value and
  dropdown rows, calendar header/weekday/day labels, tab captions, title bar, navigation bar
  toolbar, stepper, swipe panel, flyout rows, alerts, tooltips) scales its fixed sizes through
  the same helper. At the default scale of 1 the multiplier is identity, so the existing output
  is unchanged.
* **Caches follow the scale.** Text measurements are cached per (text, logical size, typeface
  generation) in `OpenHarmonyLabelHandler`; the key now also carries the font-scale generation,
  and the view-level line-break, formatted-layout, natural-line-height and per-character caches
  carry the same generation (or key on the scaled size), so a scale change can never serve
  metrics computed for the previous size.
* **Limits (recorded).** The scale is a single process-wide value (the platform's own font size
  setting is process-wide); the slice does not subscribe to the system configuration change
  itself - the app host / shell half that reads `Configuration.fontSizeScale` and calls
  `SetSystemFontScale` plus a re-arrange is still to be wired (the public setter is the seam).
  Character spacing, paddings, row heights and other pixel geometry stay physical; only text
  metrics scale. The diagnostics overlay (a debug tool that draws type names) keeps its fixed
  18 px labels.
