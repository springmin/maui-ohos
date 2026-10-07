// In-app subwindow (MULTIWINDOW-M) for the OpenHarmony MAUI slice.
//
// The ArkTS shell creates an application subwindow under the main window with
// window.createSubWindowWithOptions (no ACL; window type TYPE_FLOAT would need the
// system-only SYSTEM_FLOAT_WINDOW permission and is deliberately out of reach). Commands
// travel managed -> shell through host.registerSubWindowSink, reported by the shell through
// host.notifySubWindowEvent -> ohos_host_sub_window_event_listener, which this class registers
// in a module initializer. The payloads are small JSON documents, opaque to the native host,
// so the shell and the slice can extend the command/event set without a host rebuild.
//
// Honest degradation (matches the platform-limits doc E2 and the multiwindow prestudy A1):
// without a shell sink the subwindow hosts shell-drawn ArkUI content; with the L/L2 shell each
// managed child owns its surface/renderer (window-id routing in the app host). MULTIWINDOW-L3
// M1 carries up to two managed children; the shell's session registry (SUB_WINDOW_MAX) and this
// class's command wire are keyed by the managed surface id. Every native call is guarded:
// off-device (desktop harness) or with an older host library the feature reports unavailable
// and no call throws.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

/// <summary>Kind of an in-app subwindow event reported by the ArkTS shell.</summary>
public enum OpenHarmonySubWindowEventKind
{
    /// <summary>The shell created and showed the subwindow; the payload carries its rect + id.</summary>
    Created = 0,
    /// <summary>The subwindow became visible (window event WINDOW_SHOWN).</summary>
    Shown = 1,
    /// <summary>The subwindow was hidden (window event WINDOW_HIDDEN).</summary>
    Hidden = 2,
    /// <summary>The subwindow moved; the payload carries x/y.</summary>
    Moved = 3,
    /// <summary>The subwindow resized; the payload carries w/h.</summary>
    Resized = 4,
    /// <summary>The subwindow was destroyed.</summary>
    Closed = 5,
    /// <summary>A touch landed inside the subwindow content; see <see cref="OpenHarmonySubWindowTouchEventArgs"/>.</summary>
    Touch = 6,
    /// <summary>The shell could not create or drive the subwindow; the payload carries a reason.</summary>
    Failed = 7,
    /// <summary>The shell-drawn subwindow page finished loading; the payload carries its window id.</summary>
    PageReady = 8,
    /// <summary>The main window went hidden; the child is suspended with it.</summary>
    Suspended = 9,
    /// <summary>The main window came back and the child was restored.</summary>
    Resumed = 10,
    /// <summary>MULTIWINDOW-L M4: the child window gained focus (WINDOW_ACTIVE).</summary>
    Active = 11,
    /// <summary>MULTIWINDOW-L M4: the child window lost focus (WINDOW_INACTIVE).</summary>
    Inactive = 12,
    /// <summary>MULTIWINDOW-L M4: text typed into the child's input control.</summary>
    TextInput = 13,
    /// <summary>MULTIWINDOW-L M4: IME preview text from the child's input control.</summary>
    TextComposition = 14,
    /// <summary>MULTIWINDOW-L M4: the child input's return key.</summary>
    TextSubmitted = 15,
    /// <summary>MULTIWINDOW-L M4: a hardware key pressed while the child XComponent had focus.</summary>
    Key = 16,
    /// <summary>MULTIWINDOW-L M4: the system Back key on the child window.</summary>
    Back = 17,
}

/// <summary>One subwindow state report (kind, geometry, window id, failure detail).</summary>
public sealed class OpenHarmonySubWindowEventArgs : EventArgs
{
    internal OpenHarmonySubWindowEventArgs(
        OpenHarmonySubWindowEventKind kind, int windowId, Rect bounds, int code, string? message,
        string? surfaceId = null, string? text = null, int compositionOffset = 0,
        int keyCode = 0, int keyEventType = 0)
    {
        Kind = kind;
        WindowId = windowId;
        Bounds = bounds;
        Code = code;
        Message = message;
        SurfaceId = surfaceId ?? string.Empty;
        Text = text;
        CompositionOffset = compositionOffset;
        KeyCode = keyCode;
        KeyEventType = keyEventType;
    }

    /// <summary>The event kind.</summary>
    public OpenHarmonySubWindowEventKind Kind { get; }

    /// <summary>The shell window id (0 when no subwindow exists).</summary>
    public int WindowId { get; }

    /// <summary>The last known subwindow rectangle (in window pixels).</summary>
    public Rect Bounds { get; }

    /// <summary>A failure code for <see cref="OpenHarmonySubWindowEventKind.Failed"/>; 0 otherwise.</summary>
    public int Code { get; }

    /// <summary>A failure message for <see cref="OpenHarmonySubWindowEventKind.Failed"/>.</summary>
    public string? Message { get; }

    /// <summary>The managed surface id (the shell XComponent id) when the event carries one
    /// (MULTIWINDOW-L M3); empty for the shell-drawn M path.</summary>
    public string SurfaceId { get; }

    /// <summary>MULTIWINDOW-L M4: text for <see cref="OpenHarmonySubWindowEventKind.TextInput"/>,
    /// <see cref="OpenHarmonySubWindowEventKind.TextComposition"/> and
    /// <see cref="OpenHarmonySubWindowEventKind.TextSubmitted"/>; null otherwise.</summary>
    public string? Text { get; }

    /// <summary>M4: the IME preview offset of a composition event (0 otherwise).</summary>
    public int CompositionOffset { get; }

    /// <summary>M4: the raw ArkUI key code of a <see cref="OpenHarmonySubWindowEventKind.Key"/> event.</summary>
    public int KeyCode { get; }

    /// <summary>M4: the ArkUI key type of a key event (0 down, 1 up).</summary>
    public int KeyEventType { get; }
}

/// <summary>One touch reported from the subwindow's shell-drawn content.</summary>
public sealed class OpenHarmonySubWindowTouchEventArgs : EventArgs
{
    internal OpenHarmonySubWindowTouchEventArgs(int action, float x, float y, int pointerCount)
    {
        Action = action;
        X = x;
        Y = y;
        PointerCount = pointerCount;
    }

    /// <summary>Touch type (0 down, 1 up, 2 move, 3 cancel - the ArkUI TouchType order).</summary>
    public int Action { get; }

    /// <summary>X in subwindow coordinates.</summary>
    public float X { get; }

    /// <summary>Y in subwindow coordinates.</summary>
    public float Y { get; }

    /// <summary>The number of active pointers reported with the touch.</summary>
    public int PointerCount { get; }
}

/// <summary>
/// Application subwindow lifecycle/geometry host. <see cref="IsSupported"/> is the shell's
/// availability probe; the other members degrade to false/no-op without a device shell.
/// </summary>
public static partial class OpenHarmonySubWindow
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Command op: create (payload name/x/y/w/h/title).</summary>
    public const int CreateCommand = 0;
    /// <summary>Command op: move (payload x/y).</summary>
    public const int MoveCommand = 1;
    /// <summary>Command op: resize (payload w/h).</summary>
    public const int ResizeCommand = 2;
    /// <summary>Command op: show.</summary>
    public const int ShowCommand = 3;
    /// <summary>Command op: hide.</summary>
    public const int HideCommand = 4;
    /// <summary>Command op: close/destroy.</summary>
    public const int CloseCommand = 5;
    /// <summary>MULTIWINDOW-L M4: route a per-window ArkUI focus/keyboard request into the child
    /// page (payload surfaceId/show/caret/text; the shell forwards it to the child's input).</summary>
    public const int TextFocusCommand = 6;
    // The shell's availability probe (mirrors kSubWindowProbeOp in host_napi.cpp).
    private const int ProbeCommand = 99;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_sub_window_event_listener")]
    private static partial void SubWindowEventListener(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_sub_window_command", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SubWindowCommandNative(int op, string payload);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SubWindowEventCallback(int op, IntPtr payloadUtf8);

    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr, void>)&OnNativeEvent;

    private static readonly object s_sync = new();
    private static bool s_registered;
    private static bool s_available = true;
    private static int s_supported = -1;   // -1 unknown, 0 no shell sink, 1 shell sink registered
    private static bool s_open;
    private static bool s_visible;
    private static bool s_suspended;
    private static bool s_contentReady;
    private static bool s_focused;
    private static int s_windowId;
    private static Rect s_bounds = Rect.Zero;
    // M3: the managed surface id (the shell XComponent id) once the shell reports one.
    private static string s_surfaceId = string.Empty;

    /// <summary>
    /// Test/embedding seam: when set, commands go through this delegate instead of the native
    /// library (the headless suite drives the command payloads with it).
    /// </summary>
    internal static Func<int, string, bool>? CommandSender { get; set; }

    /// <summary>Raised for every subwindow state report (created/shown/hidden/moved/...).</summary>
    public static event EventHandler<OpenHarmonySubWindowEventArgs>? Changed;

    /// <summary>Raised for every touch inside the subwindow's shell-drawn content.</summary>
    public static event EventHandler<OpenHarmonySubWindowTouchEventArgs>? Touched;

    /// <summary>True when the shell registered its subwindow sink (a device with the M shell).</summary>
    public static bool IsSupported
    {
        get
        {
            lock (s_sync)
            {
                if (s_supported >= 0)
                {
                    return s_supported == 1;
                }
            }
            bool supported = false;
            if (s_available)
            {
                try
                {
                    supported = SubWindowCommandNative(ProbeCommand, string.Empty) >= 1;
                }
                catch (DllNotFoundException)
                {
                    s_available = false;
                }
                catch (EntryPointNotFoundException)
                {
                    s_available = false;
                }
            }
            lock (s_sync)
            {
                s_supported = supported ? 1 : 0;
            }
            return supported;
        }
    }

    /// <summary>True between the shell's Created and Closed reports.</summary>
    public static bool IsOpen
    {
        get { lock (s_sync) { return s_open; } }
    }

    /// <summary>True while the shell reports the subwindow shown (and the main window not suspended).</summary>
    public static bool IsVisible
    {
        get { lock (s_sync) { return s_visible && !s_suspended; } }
    }

    /// <summary>True while the main window is hidden and the child is suspended with it.</summary>
    public static bool IsSuspended
    {
        get { lock (s_sync) { return s_suspended; } }
    }

    /// <summary>MULTIWINDOW-L M4: true while the child window holds window focus (the shell's
    /// WINDOW_ACTIVE report; false after WINDOW_INACTIVE or a close).</summary>
    public static bool IsFocused
    {
        get { lock (s_sync) { return s_focused; } }
    }

    /// <summary>True once the shell-drawn subwindow page reported ready.</summary>
    public static bool IsContentReady
    {
        get { lock (s_sync) { return s_contentReady; } }
    }

    /// <summary>The shell window id of the subwindow (0 when none).</summary>
    public static int WindowId
    {
        get { lock (s_sync) { return s_windowId; } }
    }

    /// <summary>The last known subwindow rectangle in window pixels.</summary>
    public static Rect Bounds
    {
        get { lock (s_sync) { return s_bounds; } }
    }

    /// <summary>The managed surface id of the live subwindow (empty for the shell-drawn M path
    /// or before the shell reports one).</summary>
    public static string SurfaceId
    {
        get { lock (s_sync) { return s_surfaceId; } }
    }

    /// <summary>Registers the native event listener; a guarded no-op off-device.</summary>
    public static void Register()
    {
        if (s_registered)
        {
            return;
        }
        s_registered = true;
        try
        {
            SubWindowEventListener(s_callback);
        }
        catch (DllNotFoundException)
        {
            s_available = false;
        }
        catch (EntryPointNotFoundException)
        {
            s_available = false;
        }
    }

    /// <summary>Creates (or reports, when already open) the application subwindow.</summary>
    public static bool Create(string name, int x, int y, int width, int height, string? title = null)
        => Send(CreateCommand, BuildCreatePayload(name, x, y, width, height, title));

    /// <summary>
    /// MULTIWINDOW-L M3: creates the subwindow with a managed XComponent surface. The shell
    /// page embeds an XComponent with <paramref name="windowId"/> as its component id, bound
    /// to libopenharmonyhost, so the surface registers under that id and the host's window-id
    /// bridge feeds the managed second window. The shell falls back to its drawn content when
    /// the surface cannot be registered.
    /// </summary>
    public static bool CreateManagedSurface(string windowId, string name, int x, int y, int width, int height, string? title = null)
        => Send(CreateCommand, BuildCreatePayload(name, x, y, width, height, title, windowId));

    /// <summary>Moves the subwindow to (x, y) in window pixels.</summary>
    public static bool Move(int x, int y) => Send(MoveCommand, BuildRectPayload("x", x, "y", y));

    /// <summary>Resizes the subwindow to width x height (pixels, clamped by the shell).</summary>
    public static bool Resize(int width, int height) => Send(ResizeCommand, BuildRectPayload("w", width, "h", height));

    /// <summary>Shows the subwindow (a no-op when none exists yet; Create shows it).</summary>
    public static bool Show() => Send(ShowCommand, "{}");

    /// <summary>Hides the subwindow without destroying it.</summary>
    public static bool Hide() => Send(HideCommand, "{}");

    /// <summary>Destroys the subwindow and its shell-drawn content.</summary>
    public static bool Close() => Send(CloseCommand, "{}");

    /// <summary>
    /// MULTIWINDOW-L3 M1: destroys the managed subwindow session named by
    /// <paramref name="windowId"/> (the shell's session key, e.g. "sub-1"). With more than one
    /// managed child a bare close is ambiguous, so the app host targets the session that owned
    /// the closed MAUI window.
    /// </summary>
    public static bool Close(string windowId) => Send(CloseCommand, BuildSurfaceIdPayload(windowId));

    /// <summary>
    /// MULTIWINDOW-L M4: asks the child page to give ArkUI focus to its own hidden input
    /// (<paramref name="focused"/> true) or back to its surface (false). The managed text
    /// handlers of a secondary window use this instead of the process-global keyboard/focus
    /// exports, so the system IME follows the window the element belongs to. The payload also
    /// carries the current managed text so the child input continues from the same value.
    /// </summary>
    public static bool RequestTextFocus(string windowId, bool focused, int caret, string? text = null)
        => Send(TextFocusCommand, BuildTextFocusPayload(windowId, focused, caret, text));

    private static string BuildTextFocusPayload(string windowId, bool focused, int caret, string? text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("surfaceId", windowId ?? string.Empty);
            writer.WriteNumber("show", focused ? 1 : 0);
            writer.WriteNumber("caret", caret);
            if (!string.IsNullOrEmpty(text))
            {
                writer.WriteString("text", text);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool Send(int op, string payload)
    {
        Func<int, string, bool>? sender = CommandSender;
        if (sender is not null)
        {
            return sender(op, payload);
        }
        if (!s_available)
        {
            return false;
        }
        try
        {
            return SubWindowCommandNative(op, payload) == 0;
        }
        catch (DllNotFoundException)
        {
            s_available = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            s_available = false;
            return false;
        }
    }

    // One small JSON writer (Utf8JsonWriter is reflection-free, so this stays NativeAOT-safe).
    private static string BuildCreatePayload(string name, int x, int y, int width, int height, string? title, string? surfaceId = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name ?? string.Empty);
            writer.WriteNumber("x", x);
            writer.WriteNumber("y", y);
            writer.WriteNumber("w", width);
            writer.WriteNumber("h", height);
            if (!string.IsNullOrEmpty(title))
            {
                writer.WriteString("title", title);
            }
            if (!string.IsNullOrEmpty(surfaceId))
            {
                writer.WriteString("surfaceId", surfaceId);
                writer.WriteBoolean("managed", true);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildRectPayload(string firstKey, int firstValue, string secondKey, int secondValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber(firstKey, firstValue);
            writer.WriteNumber(secondKey, secondValue);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // MULTIWINDOW-L3 M1: the session key of one managed child (the shell targets its session
    // registry with it). Absent/empty keeps the historical untargeted request.
    private static string BuildSurfaceIdPayload(string windowId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("surfaceId", windowId ?? string.Empty);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The shell reports one event (host.notifySubWindowEvent). Parses the payload, updates the
    /// published state and raises <see cref="Changed"/>/<see cref="Touched"/>. A malformed
    /// payload is turned into a Failed report instead of throwing.
    /// </summary>
    internal static void HandleNativeEvent(int op, string payload)
    {
        var kind = (OpenHarmonySubWindowEventKind)op;
        int windowId;
        Rect bounds;
        int code = 0;
        string? message = null;
        string surfaceId = string.Empty;
        string? text = null;
        int compositionOffset = 0;
        int keyCode = 0;
        int keyEventType = 0;
        OpenHarmonySubWindowTouchEventArgs? touch = null;
        bool raiseChanged = true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrEmpty(payload) ? "{}" : payload);
            JsonElement root = document.RootElement;
            lock (s_sync)
            {
                windowId = ReadInt(root, "id", s_windowId);
                bounds = new Rect(
                    ReadInt(root, "x", (int)s_bounds.X),
                    ReadInt(root, "y", (int)s_bounds.Y),
                    ReadInt(root, "w", (int)s_bounds.Width),
                    ReadInt(root, "h", (int)s_bounds.Height));
                // M4: every event may carry the managed surface id; the per-window routes need
                // it even for text/key/back/closed, and an event without one falls back to the
                // live child's id (the shell-drawn M path carries none and has no managed window).
                surfaceId = ReadString(root, "surfaceId");
                if (surfaceId.Length == 0)
                {
                    surfaceId = s_surfaceId;
                }
                switch (kind)
                {
                    case OpenHarmonySubWindowEventKind.Created:
                        s_open = true;
                        s_visible = true;
                        s_windowId = windowId;
                        s_bounds = bounds;
                        surfaceId = ReadString(root, "surfaceId");
                        if (surfaceId.Length > 0)
                        {
                            s_surfaceId = surfaceId;
                        }
                        break;
                    case OpenHarmonySubWindowEventKind.Shown:
                        s_visible = true;
                        break;
                    case OpenHarmonySubWindowEventKind.Hidden:
                        s_visible = false;
                        break;
                    case OpenHarmonySubWindowEventKind.Moved:
                    case OpenHarmonySubWindowEventKind.Resized:
                        s_bounds = bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Closed:
                        s_open = false;
                        s_visible = false;
                        s_contentReady = false;
                        s_focused = false;
                        s_windowId = 0;
                        s_surfaceId = string.Empty;
                        s_bounds = Rect.Zero;
                        bounds = Rect.Zero;
                        windowId = 0;
                        break;
                    case OpenHarmonySubWindowEventKind.PageReady:
                        s_windowId = windowId;
                        s_contentReady = true;
                        surfaceId = ReadString(root, "surfaceId");
                        if (surfaceId.Length > 0)
                        {
                            s_surfaceId = surfaceId;
                        }
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Suspended:
                        s_suspended = true;
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Resumed:
                        s_suspended = false;
                        bounds = s_bounds;
                        break;
                    // MULTIWINDOW-L M4: window focus + the child page's own input. The shell
                    // reports them under the child's surface id; the app host routes them to the
                    // window that owns that surface (Active/Inactive) or to the window's text
                    // handlers (TextInput/TextComposition/TextSubmitted) and key hook (Key).
                    case OpenHarmonySubWindowEventKind.Active:
                        s_focused = true;
                        surfaceId = ReadString(root, "surfaceId");
                        if (surfaceId.Length > 0)
                        {
                            s_surfaceId = surfaceId;
                        }
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Inactive:
                        s_focused = false;
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.TextInput:
                        text = ReadString(root, "text");
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.TextComposition:
                        text = ReadString(root, "value");
                        compositionOffset = ReadInt(root, "offset", 0);
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.TextSubmitted:
                        text = ReadString(root, "text");
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Key:
                        keyCode = ReadInt(root, "code", 0);
                        keyEventType = ReadInt(root, "type", 0);
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Back:
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Touch:
                        touch = new OpenHarmonySubWindowTouchEventArgs(
                            ReadInt(root, "action", 0),
                            (float)ReadDouble(root, "x", 0),
                            (float)ReadDouble(root, "y", 0),
                            ReadInt(root, "count", 1));
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Failed:
                        code = ReadInt(root, "code", -1);
                        message = root.TryGetProperty("message", out JsonElement messageElement)
                            ? messageElement.GetString()
                            : null;
                        bounds = s_bounds;
                        break;
                    default:
                        // An event kind this slice does not know (a newer shell): ignore the
                        // state and skip the Changed report so a forward-compatible shell cannot
                        // move this side's contract by accident.
                        raiseChanged = false;
                        bounds = s_bounds;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            lock (s_sync)
            {
                windowId = s_windowId;
                bounds = s_bounds;
                surfaceId = s_surfaceId;
            }
            kind = OpenHarmonySubWindowEventKind.Failed;
            code = -1;
            message = "malformed subwindow payload";
        }

        if (touch is not null)
        {
            Touched?.Invoke(null, touch);
        }
        if (raiseChanged)
        {
            Changed?.Invoke(null, new OpenHarmonySubWindowEventArgs(
                kind, windowId, bounds, code, message, surfaceId, text, compositionOffset,
                keyCode, keyEventType));
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out int value) ? value : fallback;

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty : string.Empty;

    private static double ReadDouble(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble() : fallback;

    /// <summary>Native-shaped thunk: the shell reports a subwindow event (op, JSON payload).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeEvent(int op, IntPtr payloadUtf8)
    {
        // A reverse P/Invoke entry: an exception must not unwind into the native frame.
        try
        {
            HandleNativeEvent(op, Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("subwindow event", ex);
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();
}
