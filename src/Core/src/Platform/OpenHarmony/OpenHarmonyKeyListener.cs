// Hardware key events for the slice: the second half of the seam NB13 documented in
// OpenHarmonyEntryHandler.cs ("the callback registered with ohos_host_register_key_event
// (void (*)(int keyCode, int eventType); 0 = down, 1 = up, the ArkUI KeyType encoding). MAUI rc.1
// exposes no key surface (no IKeyListener/KeyDown/KeyUp), so the hosting bridge keeps that
// callback as its documented internal surface and no slice handler consumes keys yet").
//
// The shell's XComponent onKeyEvent (id 'ohos_dotnet_surface', .focusable(true)) forwards the raw
// ArkUI key code and type through host.keyEvent -> ohos_host_key_event -> the callback this file
// registers with ohos_host_register_key_event. The callback arrives on the ArkUI UI thread, the
// same thread the dispatcher drains, so no marshalling is needed; keys only arrive while the
// surface holds ArkUI focus, which OpenHarmonyFocusManager (non-text Focus()) and the text-input
// sink (unfocus) arrange between them.
//
// MAUI rc.1 has no key contract to surface: Microsoft.Maui and Microsoft.Maui.Controls
// 11.0.0-rc.1.26451.6 expose no IKeyListener and no KeyDown/KeyUp members at all. The slice
// therefore keeps the surface internal - KeyEvent (raw), KeyDown/KeyUp (split), the
// EventsReceived/LastKeyCode/LastEventType observables and Dispatch() as the testable entry
// point, plus a one-time status line - and documents what a real public surface would need, in
// Microsoft.Maui itself:
//   1. an IView-level key contract (for example IKeyListener with OnKeyDown/OnKeyUp taking the
//      key and modifiers and returning whether the event was consumed);
//   2. VisualElement.KeyDown/KeyUp routed events and a key enum/mapping (ArkUI KeyCode -> Key),
//      with the accelerator plumbing other platforms already have;
//   3. a handler command-mapper entry that forwards a platform key event to the virtual view and
//      honors the consumed flag so the platform does not also process the key;
//   4. focus interop: the event source is the focused XComponent, so the focus routing above must
//      name 'ohos_dotnet_surface' (the shell has no other key-carrying node) and the text input's
//      focus/unfocus must return focus to it.
// Until those exist this file is the documented internal hook: it registers the callback and lets
// a future surface (or a slice handler) subscribe without native code changes.
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

internal static partial class OpenHarmonyKeyListener
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>ArkUI KeyType.Down (the host forwards the raw ArkUI encoding).</summary>
    internal const int KeyTypeDown = 0;

    /// <summary>ArkUI KeyType.Up.</summary>
    internal const int KeyTypeUp = 1;

    /// <summary>Managed form of the host's key callback: void (*)(int keyCode, int eventType).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void KeyEventCallback(int keyCode, int eventType);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_register_key_event")]
    private static partial void RegisterKeyEventNative(IntPtr callback);

    private static bool s_installed;
    private static bool s_registered;
    private static bool s_gapLogged;
    private static string s_lastEventTypeName = "none";
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, void>)&OnKeyEventNative;

    /// <summary>Raw hardware key events (keyCode, eventType; KeyTypeDown/KeyTypeUp).</summary>
    internal static event Action<int, int>? KeyEvent;

    /// <summary>Key-down events (raw ArkUI key code).</summary>
    internal static event Action<int>? KeyDown;

    /// <summary>Key-up events (raw ArkUI key code).</summary>
    internal static event Action<int>? KeyUp;

    /// <summary>True when the host accepted the callback registration.</summary>
    internal static bool IsRegistered => s_registered;

    /// <summary>True when registration ran and the host library/export is missing
    /// (the off-device harness and an older host library both answer true).</summary>
    internal static bool IsUnavailable => s_installed && !s_registered;

    /// <summary>Key events delivered to Dispatch() since Install().</summary>
    internal static int EventsReceived { get; private set; }

    /// <summary>Key code of the last event (-1 before the first one).</summary>
    internal static int LastKeyCode { get; private set; } = -1;

    /// <summary>ArkUI event type of the last event (-1 before the first one).</summary>
    internal static int LastEventType { get; private set; } = -1;

    /// <summary>Name of the last event type ("down"/"up"/"type N"; "none" before the first).</summary>
    internal static string LastEventTypeName => s_lastEventTypeName;

    /// <summary>
    /// Registers the host's key callback once. Best effort: off-device (no host library) and with
    /// an older host without the export the surface stays unavailable and logs the gap once; the
    /// off-device verification harness therefore exercises only the fallback, and the registered
    /// path needs a device (or a host whose loader accepts a stand-in library).
    /// </summary>
    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        try
        {
            RegisterKeyEventNative(s_callback);
            s_registered = true;
            LogInternalSurfaceOnce();
        }
        catch (DllNotFoundException)
        {
            // No native host (tests/desktop): hardware keys have no source.
            LogGapOnce();
        }
        catch (EntryPointNotFoundException)
        {
            // An older host library without the key export.
            LogGapOnce();
        }
    }

    /// <summary>
    /// Delivers one hardware key event to the managed surface. The native callback enters here
    /// (through OnKeyEventNative) and the scratch driver calls it directly to exercise the
    /// dispatch without a device. Subscribers must not throw into the native callback frame.
    /// </summary>
    internal static void Dispatch(int keyCode, int eventType)
    {
        EventsReceived++;
        LastKeyCode = keyCode;
        LastEventType = eventType;
        s_lastEventTypeName = eventType == KeyTypeDown ? "down" : eventType == KeyTypeUp ? "up" : $"type {eventType}";
        try
        {
            KeyEvent?.Invoke(keyCode, eventType);
            if (eventType == KeyTypeDown)
            {
                KeyDown?.Invoke(keyCode);
            }
            else if (eventType == KeyTypeUp)
            {
                KeyUp?.Invoke(keyCode);
            }
        }
        catch (Exception)
        {
            // A subscriber must not take down the ArkUI callback frame; the counters above
            // already recorded that the event arrived.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnKeyEventNative(int keyCode, int eventType) => Dispatch(keyCode, eventType);

    private static void LogInternalSurfaceOnce()
    {
        OpenHarmonyBridge.WriteStatus(
            "[maui] hardware key events registered (internal surface: MAUI rc.1 has no " +
            "IKeyListener/KeyDown/KeyUp; OpenHarmonyKeyListener.KeyEvent/KeyDown/KeyUp + Dispatch)");
    }

    private static void LogGapOnce()
    {
        if (s_gapLogged)
        {
            return;
        }
        s_gapLogged = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] hardware key events unavailable (no host library or no " +
            "ohos_host_register_key_event export); the managed key surface stays internal");
    }
}
