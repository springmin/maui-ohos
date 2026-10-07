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
    /// <summary>MULTIWINDOW-L3 M2: the identity handshake ack (payload surfaceId/id/gen). Sent
    /// when the child page's PageReady claim matches the session the shell created; the shell
    /// validates it against its own session and only then tells the page to mount the managed
    /// surface, so an out-of-order, duplicate or unknown claim can never bind a surface.</summary>
    public const int IdentityCommand = 7;
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
    // MULTIWINDOW-L3 M2: the managed side of the identity handshake. Every managed child is a
    // session keyed by its surface id; the shell stamps a generation into the child window name
    // on every (re)creation, so a late PageReady/Closed from a retired generation can never be
    // mistaken for the live session. s_currentSurfaceId is the last created live session, whose
    // state the legacy single-child properties (IsOpen/ContentReady/SurfaceId/...) mirror.
    private static readonly Dictionary<string, SurfaceSession> s_sessions = new(StringComparer.Ordinal);
    // Surface ids this side asked the shell to bring up. A PageReady naming one of them may
    // legitimately arrive before the shell's Created (loadContent runs before showWindow), so it
    // is buffered; a PageReady naming anything else is an unknown claim and is closed.
    private static readonly HashSet<string> s_requestedSurfaces = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PendingClaim> s_pendingClaims = new(StringComparer.Ordinal);
    private static string s_currentSurfaceId = string.Empty;
    private static int s_syntheticGeneration;

    /// <summary>MULTIWINDOW-L3 M2: one live shell subwindow session.</summary>
    private sealed class SurfaceSession
    {
        public SurfaceSession(string surfaceId, int shellWindowId, int generation)
        {
            SurfaceId = surfaceId;
            ShellWindowId = shellWindowId;
            Generation = generation;
        }

        public string SurfaceId { get; }
        public int ShellWindowId { get; set; }
        public int Generation { get; set; }
        public bool Ready { get; set; }
        public string ChildName { get; set; } = string.Empty;
    }

    /// <summary>MULTIWINDOW-L3 M2: a child page's ready claim that preceded its Created report.</summary>
    private sealed class PendingClaim
    {
        public PendingClaim(int windowId, int generation, string name)
        {
            WindowId = windowId;
            Generation = generation;
            Name = name;
        }

        public int WindowId { get; }
        public int Generation { get; }
        public string Name { get; }
    }

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

    // MULTIWINDOW-L3 M2 introspection (the headless suite drives the handshake and the
    // per-session lifecycle through HandleNativeEvent and reads these back).

    /// <summary>Number of live managed surface sessions.</summary>
    internal static int SessionCount
    {
        get { lock (s_sync) { return s_sessions.Count; } }
    }

    /// <summary>The last created live session's surface id (the legacy property mirror), empty
    /// when none.</summary>
    internal static string CurrentSurfaceId
    {
        get { lock (s_sync) { return s_currentSurfaceId; } }
    }

    /// <summary>True once the named session's child page reported ready and was confirmed.</summary>
    internal static bool IsSessionReady(string surfaceId)
    {
        lock (s_sync)
        {
            return s_sessions.TryGetValue(surfaceId, out SurfaceSession? session) && session.Ready;
        }
    }

    /// <summary>The generation the shell stamped into the named live session (0 when none).</summary>
    internal static int GenerationOf(string surfaceId)
    {
        lock (s_sync)
        {
            return s_sessions.TryGetValue(surfaceId, out SurfaceSession? session) ? session.Generation : 0;
        }
    }

    /// <summary>The shell window id recorded for the named live session (-1 when none).</summary>
    internal static int ShellWindowIdOf(string surfaceId)
    {
        lock (s_sync)
        {
            return s_sessions.TryGetValue(surfaceId, out SurfaceSession? session) ? session.ShellWindowId : -1;
        }
    }

    /// <summary>Number of buffered ready claims (a PageReady that preceded its Created).</summary>
    internal static int PendingClaimCount
    {
        get { lock (s_sync) { return s_pendingClaims.Count; } }
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
    {
        if (!string.IsNullOrEmpty(windowId))
        {
            lock (s_sync)
            {
                // M2: remember the request so an out-of-order PageReady for it is buffered and
                // validated by the Created report instead of being treated as an unknown claim.
                s_requestedSurfaces.Add(windowId);
            }
        }
        return Send(CreateCommand, BuildCreatePayload(name, x, y, width, height, title, windowId));
    }

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
    /// the closed MAUI window. M2: the local session record is retired eagerly, so the shell's
    /// later Closed report is an idempotent no-op and a late PageReady for the id is closed
    /// fail-closed instead of binding a surface.
    /// </summary>
    public static bool Close(string windowId)
    {
        if (!string.IsNullOrEmpty(windowId))
        {
            lock (s_sync)
            {
                CloseSessionLocked(windowId);
            }
        }
        return Send(CloseCommand, BuildSurfaceIdPayload(windowId));
    }

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

    // MULTIWINDOW-L3 M2: the per-session helpers below are called under s_sync.

    // Drops every trace of a closed/closed-by-us session; the legacy properties either move to
    // the next live session (the last one still mirrors the child state) or reset.
    private static void CloseSessionLocked(string surfaceId)
    {
        if (surfaceId.Length == 0)
        {
            return;
        }
        s_sessions.Remove(surfaceId);
        s_pendingClaims.Remove(surfaceId);
        s_requestedSurfaces.Remove(surfaceId);
        if (s_currentSurfaceId == surfaceId)
        {
            s_currentSurfaceId = string.Empty;
            foreach (string id in s_sessions.Keys)
            {
                s_currentSurfaceId = id;
            }
            if (s_currentSurfaceId.Length > 0 && s_sessions.TryGetValue(s_currentSurfaceId, out SurfaceSession? next))
            {
                ApplySessionLegacyLocked(next);
            }
            else
            {
                s_open = false;
                s_visible = false;
                s_contentReady = false;
                s_focused = false;
                s_windowId = 0;
                s_surfaceId = string.Empty;
                s_bounds = Rect.Zero;
            }
        }
    }

    // Mirrors one session into the legacy single-child properties (the last created session is
    // the one IsOpen/ContentReady/SurfaceId/... describe, preserving the M1 single-child wire).
    private static void ApplySessionLegacyLocked(SurfaceSession session)
    {
        s_open = true;
        s_visible = true;
        s_surfaceId = session.SurfaceId;
        s_contentReady = session.Ready;
        if (session.ShellWindowId > 0)
        {
            s_windowId = session.ShellWindowId;
        }
    }

    // Creates or refreshes the session a Created report describes. A repeated Created for the
    // same shell window is idempotent; a same-id create with a different shell window is a
    // reopen and retires the previous generation. A ready claim buffered before the Created is
    // validated here and answered with the identity ack.
    private static void UpsertSessionLocked(string surfaceId, int shellWindowId, int generation, int childId, string childName, out SurfaceSession? ackSession, out int ackWindowId)
    {
        ackSession = null;
        ackWindowId = childId;
        if (s_sessions.TryGetValue(surfaceId, out SurfaceSession? existing) &&
            existing.ShellWindowId > 0 && existing.ShellWindowId == shellWindowId)
        {
            // Duplicate Created for the live session: idempotent, and a duplicate ready claim
            // already answered is not answered again.
            s_currentSurfaceId = surfaceId;
            return;
        }
        int liveGeneration = generation > 0 ? generation : ++s_syntheticGeneration;
        var session = new SurfaceSession(surfaceId, shellWindowId, liveGeneration);
        s_sessions[surfaceId] = session;
        s_currentSurfaceId = surfaceId;
        ApplySessionLegacyLocked(session);
        if (s_pendingClaims.Remove(surfaceId, out PendingClaim? claim))
        {
            // The claim may be stale (a retired generation): only an agreeing generation claims
            // the session and gets the ack; a mismatch is dropped fail-closed.
            if (claim.Generation == liveGeneration || claim.Generation == 0 || liveGeneration == 0)
            {
                session.Ready = true;
                session.ChildName = claim.Name;
                ApplySessionLegacyLocked(session);
                ackSession = session;
                // The ack carries the shell's authoritative child window id (the page's own id
                // lookup may have resolved the main window); the page filters by surface id and
                // generation, which are deterministic.
                ackWindowId = shellWindowId > 0 ? shellWindowId : claim.WindowId;
            }
        }
    }

    private static string BuildIdentityPayload(SurfaceSession session, int windowId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("surfaceId", session.SurfaceId);
            writer.WriteNumber("id", windowId);
            writer.WriteNumber("gen", session.Generation);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool SendIdentityAck(SurfaceSession session, int windowId)
        => Send(IdentityCommand, BuildIdentityPayload(session, windowId));

    private static bool SendSessionClose(string surfaceId)
        => Send(CloseCommand, BuildSurfaceIdPayload(surfaceId));

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
        string childName = string.Empty;
        int generation = 0;
        int identityStatus = 0;
        SurfaceSession? ackSession = null;
        int ackWindowId = 0;
        string closeSurfaceId = string.Empty;
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
                // M2: the identity handshake fields. The shell stamps the generation into the
                // child window name and reports it in the Created payload; the page returns it
                // in its PageReady claim (with status != 0 when it rejected its own identity).
                childName = ReadString(root, "name");
                generation = ReadInt(root, "gen", 0);
                identityStatus = ReadInt(root, "status", 0);
                bool scoped = surfaceId.Length > 0;
                // The legacy single-child properties mirror the addressed window when it is the
                // current session - and, when no managed session is registered at all (the
                // shell-drawn path or an embedding that only drives window-level events), for
                // any addressed window, exactly like the pre-M2 single-session wire.
                bool isCurrent = !scoped || s_sessions.Count == 0 || surfaceId == s_currentSurfaceId;
                if (!scoped && kind != OpenHarmonySubWindowEventKind.Suspended && kind != OpenHarmonySubWindowEventKind.Resumed)
                {
                    surfaceId = s_surfaceId;
                }
                switch (kind)
                {
                    case OpenHarmonySubWindowEventKind.Created:
                        if (scoped)
                        {
                            UpsertSessionLocked(surfaceId, windowId, generation, windowId, childName, out ackSession, out ackWindowId);
                            if (s_currentSurfaceId == surfaceId)
                            {
                                s_bounds = bounds;
                            }
                            bounds = s_bounds;
                        }
                        else
                        {
                            // The shell-drawn M child (keyed ''): the historical M1 state only.
                            s_open = true;
                            s_visible = true;
                            s_windowId = windowId;
                            s_bounds = bounds;
                        }
                        break;
                    case OpenHarmonySubWindowEventKind.Shown:
                        if (isCurrent)
                        {
                            s_visible = true;
                        }
                        break;
                    case OpenHarmonySubWindowEventKind.Hidden:
                        if (isCurrent)
                        {
                            s_visible = false;
                        }
                        break;
                    case OpenHarmonySubWindowEventKind.Moved:
                    case OpenHarmonySubWindowEventKind.Resized:
                        if (isCurrent)
                        {
                            s_bounds = bounds;
                        }
                        break;
                    case OpenHarmonySubWindowEventKind.Closed:
                        if (scoped)
                        {
                            // A late Closed from a retired generation (the payload's shell window
                            // id differs from the live session's) must not retire the live one.
                            bool lateClose = s_sessions.TryGetValue(surfaceId, out SurfaceSession? closedSession)
                                && windowId > 0 && closedSession.ShellWindowId > 0
                                && closedSession.ShellWindowId != windowId;
                            if (!lateClose)
                            {
                                CloseSessionLocked(surfaceId);
                            }
                            // An unknown id is a no-op: the other sessions' state is untouched.
                            bounds = Rect.Zero;
                        }
                        else
                        {
                            s_open = false;
                            s_visible = false;
                            s_contentReady = false;
                            s_focused = false;
                            s_windowId = 0;
                            s_surfaceId = string.Empty;
                            s_bounds = Rect.Zero;
                            bounds = Rect.Zero;
                            windowId = 0;
                        }
                        break;
                    case OpenHarmonySubWindowEventKind.PageReady:
                        if (!scoped)
                        {
                            // The shell-drawn M path: the historical state only.
                            s_windowId = windowId;
                            s_contentReady = true;
                            bounds = s_bounds;
                            break;
                        }
                        if (identityStatus != 0)
                        {
                            // The page rejected its identity (window name/LocalStorage conflict
                            // or no matching shell claim): close the claimed session if it is
                            // one of ours and never bind it (fail-closed).
                            if (s_sessions.ContainsKey(surfaceId) || s_requestedSurfaces.Contains(surfaceId)
                                || s_pendingClaims.ContainsKey(surfaceId))
                            {
                                closeSurfaceId = surfaceId;
                                CloseSessionLocked(surfaceId);
                            }
                            kind = OpenHarmonySubWindowEventKind.Failed;
                            code = 801;
                            message = childName.Length > 0
                                ? $"subwindow identity rejected: {childName}"
                                : "subwindow identity rejected";
                            bounds = s_bounds;
                            break;
                        }
                        if (s_sessions.TryGetValue(surfaceId, out SurfaceSession? pageSession))
                        {
                            if (pageSession.Generation != 0 && generation != 0
                                && pageSession.Generation != generation)
                            {
                                // A ready claim from a retired generation: ignore it; the live
                                // session's state and the ack channel stay untouched.
                                bounds = s_bounds;
                                break;
                            }
                            if (!pageSession.Ready)
                            {
                                pageSession.Ready = true;
                                pageSession.ChildName = childName.Length > 0 ? childName : pageSession.ChildName;
                                ackSession = pageSession;
                                // The shell's child window id is authoritative; see the buffered
                                // claim path above for why the page-reported id is not used.
                                ackWindowId = pageSession.ShellWindowId > 0 ? pageSession.ShellWindowId : windowId;
                                ApplySessionLegacyLocked(pageSession);
                            }
                            bounds = s_bounds;
                        }
                        else if (s_requestedSurfaces.Contains(surfaceId))
                        {
                            // Out-of-order: the page finished before the shell's Created report
                            // (loadContent runs before showWindow). Buffer the claim; the Created
                            // path validates its generation and answers the ack.
                            s_pendingClaims[surfaceId] = new PendingClaim(windowId, generation, childName);
                            bounds = s_bounds;
                        }
                        else
                        {
                            // An id this side never requested: an unknown claim. Close it
                            // fail-closed; the live sessions are untouched.
                            closeSurfaceId = surfaceId;
                            bounds = s_bounds;
                        }
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
                        if (isCurrent)
                        {
                            s_focused = true;
                            if (scoped)
                            {
                                s_surfaceId = surfaceId;
                            }
                        }
                        bounds = s_bounds;
                        break;
                    case OpenHarmonySubWindowEventKind.Inactive:
                        if (isCurrent)
                        {
                            s_focused = false;
                        }
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

        // M2: the handshake answers are sent outside the state lock: a close for an unknown or
        // rejected claim, and the identity ack for a validated claim (op 7; the shell validates
        // it against its own session before the page may mount the managed surface).
        if (closeSurfaceId.Length > 0)
        {
            SendSessionClose(closeSurfaceId);
        }
        if (ackSession is not null)
        {
            SendIdentityAck(ackSession, ackWindowId);
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
