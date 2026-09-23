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
    a bindable property). The slice draws no window chrome of its own: the ArkTS shell owns the
    title bar and OpenHarmonyWindowHandler.MapTitle only publishes the title through
    ohos_host_set_window_title. Window.TitleBar is stored by Controls but never measured,
    drawn or routed. A real implementation would add a title-bar row to the compositor
    (measure/arrange/draw Title/Subtitle/PassthroughElements and route their touches like the
    navigation bar does), map the window's navigation affordances (back button) onto it, and
    confirm the ArkUI shell lets the app own the title area.

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
    VisualDiagnosticsOverlay.AddAdorner builds one to frame a view). The slice's overlay host
    (OpenHarmonyWindowOverlay.cs) draws any IWindowOverlayElement, so an adorner on an overlay
    that is drawn/initialized renders. The remaining gap is that Controls creates
    Window.VisualDiagnosticsOverlay with IsPlatformViewInitialized false and nothing in the
    slice calls its Initialize() (Tizen does it from WindowHandler.MapContent, which the
    slice's OpenHarmonyWindowHandler does not replace). Real wiring needs the window lifecycle
    to initialize the diagnostics overlay; then the same overlay host draws it and its
    adorners.

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
