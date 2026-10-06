// Accessibility for OpenHarmony. The compositor draws custom pixels, so there is no ArkUI node
// tree for a screen reader to read: this builds a shadow tree from the MAUI view tree (bounds, text,
// description, role, state) which the host publishes through the ArkUI NDK accessibility provider
// (OH_ArkUI_NativeModule_GetNativeAccessibilityProvider / OH_ArkUI_AddAndGetAccessibilityElementInfo)
// and which tests can assert as a snapshot without a device.
using System.Runtime.InteropServices;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One node of the shadow tree. Range values are only meaningful for slider/progress roles;
/// every other role marks the range absent with <see cref="double.NaN"/> bounds, which can
/// never satisfy "RangeMin &lt;= RangeMax" (the host's validity test). Checked is 0/1 for
/// switch/checkBox and -1 when the role has no check state.
/// </summary>
public sealed record OpenHarmonyAccessibilityNode(
    int Id,
    int ParentId,
    string Role,
    string? Text,
    string? Description,
    string? Hint,
    RectF Bounds,
    bool IsEnabled,
    bool IsFocusable,
    double RangeMin,
    double RangeMax,
    double RangeCurrent,
    int Checked);

/// <summary>Action codes from the ArkUI NDK (ArkUI_Accessibility_ActionType).</summary>
public enum OpenHarmonyAccessibilityAction
{
    Invalid = 0,
    Click = 0x00000010,
    LongClick = 0x00000020,
    GainFocus = 0x00000040,
    ClearFocus = 0x00000080,
    ScrollForward = 0x00000100,
    ScrollBackward = 0x00000200,
    Copy = 0x00000400,
    Paste = 0x00000800,
    Cut = 0x00001000,
    SelectText = 0x00002000,
    SetText = 0x00004000,
    SetCursorPosition = 0x00100000,
}

public static partial class OpenHarmonyAccessibility
{
    /// <summary>Actions a node with the given role can perform (published to the provider).</summary>
    /// <remarks>
    /// Only actions the host really executes are advertised. The provider's action listener is
    /// <c>(nodeId, action)</c> with no value payload, so SET_TEXT and SET_CURSOR_POSITION can never
    /// be honoured and are not published. This slice has no platform long-press path either, so
    /// LONG_CLICK is not published. CLICK, the scroll actions (IScrollView/ISlider) and the text
    /// input actions (copy/paste/cut/select, all executed on the node's view) are. The modal
    /// prompt field is the one narrowing: it publishes CLICK and COPY only, because its edits
    /// ride the keyboard bridge and it has no view or caret/selection model.
    /// </remarks>
    public static IReadOnlyList<OpenHarmonyAccessibilityAction> ActionsFor(string role)
        => role switch
        {
            "button" => new[] { OpenHarmonyAccessibilityAction.Click },
            "text" => new[] { OpenHarmonyAccessibilityAction.Click },
            "textInput" => new[]
            {
                OpenHarmonyAccessibilityAction.Click,
                OpenHarmonyAccessibilityAction.Copy,
                OpenHarmonyAccessibilityAction.Paste,
                OpenHarmonyAccessibilityAction.Cut,
                OpenHarmonyAccessibilityAction.SelectText,
            },
            "checkBox" or "switch" => new[] { OpenHarmonyAccessibilityAction.Click },
            "slider" => new[]
            {
                OpenHarmonyAccessibilityAction.ScrollForward,
                OpenHarmonyAccessibilityAction.ScrollBackward,
            },
            "scroll" => new[]
            {
                OpenHarmonyAccessibilityAction.ScrollForward,
                OpenHarmonyAccessibilityAction.ScrollBackward,
            },
            _ => Array.Empty<OpenHarmonyAccessibilityAction>(),
        };

    /// <summary>
    /// The same action set as <see cref="ActionsFor"/> as a bit mask, without the array (and its
    /// interface enumerator) the publish loop would otherwise allocate per node per republish.
    /// </summary>
    private static int ActionMask(string role) => role switch
    {
        "button" or "text" or "checkBox" or "switch" => (int)OpenHarmonyAccessibilityAction.Click,
        "textInput" => (int)(OpenHarmonyAccessibilityAction.Click
            | OpenHarmonyAccessibilityAction.Copy
            | OpenHarmonyAccessibilityAction.Paste
            | OpenHarmonyAccessibilityAction.Cut
            | OpenHarmonyAccessibilityAction.SelectText),
        "slider" or "scroll" => (int)(OpenHarmonyAccessibilityAction.ScrollForward
            | OpenHarmonyAccessibilityAction.ScrollBackward),
        _ => 0,
    };

    /// <summary>
    /// Action mask for one published node, with the modal prompt field narrowed to the actions
    /// the alert really executes: it is a <c>textInput</c> by role, but its edits ride the
    /// keyboard bridge (no view, no caret/selection model), so only CLICK (focus it) and COPY
    /// (its published text) can be honoured; PASTE/CUT/SELECT_TEXT would be advertised but
    /// no-op and are withheld for modal nodes.
    /// </summary>
    private static int ActionMask(string role, bool modal)
        => modal && role == "textInput"
            ? (int)(OpenHarmonyAccessibilityAction.Click | OpenHarmonyAccessibilityAction.Copy)
            : ActionMask(role);

    // The shadow tree is published as immutable snapshots: Refresh walks the live tree into
    // reusable build buffers and only swaps the published snapshot in when something actually
    // moved. An accessibility callback on another thread (Nodes, TryFindNode, the action
    // listener) can therefore enumerate a frame without ever observing a list being cleared or
    // appended to, and a node's bounds always belong to one coherent frame. Old snapshots stay
    // valid for any in-flight enumeration, and a frame whose tree is unchanged publishes no new
    // objects at all: every node position reuses the previous frame's immutable node record.
    private sealed class Frame
    {
        public static readonly Frame Empty = new(Array.Empty<OpenHarmonyAccessibilityNode>(), Array.Empty<IView?>(), 0);

        public Frame(OpenHarmonyAccessibilityNode[] nodes, IView?[] views, int modalRootId)
        {
            Nodes = nodes;
            Views = views;
            ModalRootId = modalRootId;
        }

        /// <summary>Nodes of the frame, root first, parent before child (id = index + 1).</summary>
        public OpenHarmonyAccessibilityNode[] Nodes { get; }

        /// <summary>
        /// Positional view table: <c>Views[i]</c> is the view <c>Nodes[i]</c> was built from (null
        /// for the modal alert nodes, which have no view). The array is swapped together with
        /// <see cref="Nodes"/>, so an action callback always routes against a single complete
        /// frame, never a partially rebuilt one.
        /// </summary>
        public IView?[] Views { get; }

        /// <summary>
        /// Id of the modal alert subtree root (an open Alert/ActionSheet/Prompt), 0 when no alert
        /// is open. The modal nodes are appended after the view tree, so ids
        /// <c>&gt;= ModalRootId</c> are exactly the alert subtree (the focus trap's inside).
        /// </summary>
        public int ModalRootId { get; }
    }

    private static Frame s_frame = Frame.Empty;
    // MULTIWINDOW-L M4: one shadow frame per window. The primary window's frame lives in
    // s_frame (the historical Volatile.Read path, zero change); a secondary window's frame is
    // kept in this table so its render pass can no longer overwrite the primary tree (the M3
    // regression: both windows called Refresh/Publish, last renderer won).
    private static readonly object s_framesLock = new();
    private static readonly Dictionary<string, Frame> s_windowFrames = new(StringComparer.Ordinal);

    /// <summary>Nodes of the last published primary frame (root first, parents before children).</summary>
    public static IReadOnlyList<OpenHarmonyAccessibilityNode> Nodes => Volatile.Read(ref s_frame).Nodes;

    /// <summary>Nodes of a window's last shadow frame (the primary id returns the published
    /// frame; a secondary window's frame is built and kept locally in M4 - see
    /// <see cref="Publish(string)"/>).</summary>
    internal static IReadOnlyList<OpenHarmonyAccessibilityNode> NodesForWindow(string windowId)
        => FrameOf(windowId).Nodes;

    /// <summary>Number of windows with a shadow frame (primary included when published).</summary>
    internal static int FrameWindowCount
    {
        get
        {
            if (Volatile.Read(ref s_frame) == Frame.Empty)
            {
                lock (s_framesLock)
                {
                    return s_windowFrames.Count;
                }
            }
            lock (s_framesLock)
            {
                return s_windowFrames.Count + 1;
            }
        }
    }

    /// <summary>Secondary-window publish passes that were kept local (no per-window provider yet).</summary>
    internal static int SecondaryPublishes { get; private set; }

    /// <summary>Nodes of the last kept secondary frame (diagnostics).</summary>
    internal static int LastSecondaryPublishedCount { get; private set; }

    private static Frame FrameOf(string windowId)
    {
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            return Volatile.Read(ref s_frame);
        }
        lock (s_framesLock)
        {
            return s_windowFrames.TryGetValue(windowId, out Frame? frame) ? frame : Frame.Empty;
        }
    }

    private static void StoreFrame(string windowId, Frame frame)
    {
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            Volatile.Write(ref s_frame, frame);
            return;
        }
        lock (s_framesLock)
        {
            s_windowFrames[windowId] = frame;
        }
    }

    private const string HostLibrary = "libopenharmonyhost.so";
    private static bool _available = true;
    private static bool _secondaryProviderLogged;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_begin")]
    private static partial int AccessibilityBegin(int count);

    // The argument order is the publish contract shared with openharmony_host.h /
    // openharmony_host.c; the interaction harness reflects this method and compares the
    // parameter names/types against the C definition, so an arity or order change fails
    // off-device instead of shifting arguments on device (see the audit doc, section 30).
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_node", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AccessibilityNode(int id, int parentId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string role,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? description,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? hint,
        float x, float y, float width, float height, int flags, int actions,
        double rangeMin, double rangeMax, double rangeCurrent, int @checked);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_commit")]
    private static partial int AccessibilityCommit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ActionListener(int nodeId, int action);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_set_action_listener")]
    private static partial void SetActionListener(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_send_event")]
    private static partial int SendEvent(int eventType);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_provider_status")]
    private static partial int ProviderStatus();

    // The dedicated text-carrying announcement export (host_napi.cpp): it builds an
    // ANNOUNCE_FOR_ACCESSIBILITY event, sets the announced text on it and sends it through the
    // attached provider. A host library built before this export only has
    // ohos_host_accessibility_send_event, which carries the event kind alone; Announce keeps
    // that as its fallback path.
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_accessibility_announce", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AccessibilityAnnounce([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    private static int s_announceExport;   // 0 unknown, 1 exported, -1 missing (cached probe)

    /// <summary>True when the host library exports <c>ohos_host_accessibility_announce</c>.</summary>
    private static bool AnnounceExportAvailable
    {
        get
        {
            int known = Volatile.Read(ref s_announceExport);
            if (known != 0)
            {
                return known > 0;
            }
            bool available;
            try
            {
                available = NativeLibrary.TryLoad(HostLibrary, out IntPtr handle) &&
                    NativeLibrary.TryGetExport(handle, "ohos_host_accessibility_announce", out _);
            }
            catch (Exception)
            {
                available = false;
            }
            Volatile.Write(ref s_announceExport, available ? 1 : -1);
            return available;
        }
    }

    private static unsafe IntPtr _actionThunk = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, void>)&OnAction;
    private static Action<int, int>? _actionHandler;

    /// <summary>Receives accessibility actions (CLICK, SET_TEXT, SCROLL...) from the framework.</summary>
    public static void SetActionHandler(Action<int, int>? handler)
    {
        _actionHandler = handler;
        if (!_available || handler is null)
        {
            return;
        }
        try
        {
            SetActionListener(_actionThunk);
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    private static bool _statusLogged;

    private static void LogProviderStatusOnce()
    {
        if (_statusLogged)
        {
            return;
        }
        _statusLogged = true;
        int status = -1;
        if (_available)
        {
            try
            {
                status = ProviderStatus();
            }
            catch (Exception)
            {
                _available = false;
            }
        }
        Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
            $"[maui] accessibility provider status={status} (1=attached, 2=frame node, 3=node content)");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAction(int nodeId, int action)
    {
        // The host calls this from the accessibility thread (host_napi.cpp A11yExecuteAction), so
        // this is a reverse P/Invoke boundary: an exception escaping into native code would unwind
        // through the host and abort the process. The handler routes actions back into the normal
        // input path, which runs app event code, so catch everything and report instead.
        try
        {
            _actionHandler?.Invoke(nodeId, action);
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
                $"[maui] accessibility action handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Pushes the pending frame changes as accessibility events.</summary>
    public static void FlushEvents()
    {
        if (!_available || PendingEventCount == 0 || LastPublishedCount == 0)
        {
            return;
        }
        try
        {
            SendEvent(PendingEventCount);
            PendingEventCount = 0;
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    /// <summary>
    /// Announces text through the platform screen reader (the platform half of
    /// <c>Microsoft.Maui.Accessibility.ISemanticScreenReader.Announce</c>).
    ///
    /// Preferred path: the dedicated host export
    /// <c>int ohos_host_accessibility_announce(const char* text)</c>, which builds an
    /// ANNOUNCE_FOR_ACCESSIBILITY event
    /// (ARKUI_ACCESSIBILITY_NATIVE_EVENT_TYPE_ANNOUNCE_FOR_ACCESSIBILITY), sets the announced
    /// text (OH_ArkUI_AccessibilityEventSetTextAnnouncedForAccessibility) and sends it through the
    /// attached provider (host_napi.cpp). The export is probed once (NativeLibrary); a host
    /// library built before it falls back to the older event-kind-only path
    /// <c>ohos_host_accessibility_send_event(EventAnnouncement)</c>, which maps to
    /// OH_ArkUI_AccessibilityEventSetEventType + OH_ArkUI_SendAccessibilityAsyncEvent and cannot
    /// carry the text itself (the text still lands in <see cref="LastAnnouncement"/>). Both paths
    /// degrade silently without the host library.
    /// </summary>
    /// <returns>True when the announcement reached the host provider.</returns>
    public static bool Announce(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        Volatile.Write(ref s_lastAnnouncement, text);
        WouldAnnounce = _available;
        if (!_available)
        {
            return false;
        }
        bool textPath = AnnounceExportAvailable;
        try
        {
            // The host answers 1 when the event was created and sent, 0 when no provider is
            // attached (nothing to announce to) - that is not a failure of availability.
            if ((textPath ? AccessibilityAnnounce(text) : SendEvent(EventAnnouncement)) != 0)
            {
                AnnouncementsSent++;
                if (!textPath)
                {
                    LogAnnounceFallbackOnce();
                }
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // The probe saw the export but this call could not resolve it (a different library
            // won the load): mark the export missing and retry once through the event-kind path.
            Volatile.Write(ref s_announceExport, -1);
            try
            {
                if (SendEvent(EventAnnouncement) != 0)
                {
                    AnnouncementsSent++;
                    LogAnnounceFallbackOnce();
                    return true;
                }
            }
            catch (Exception)
            {
                _available = false;
            }
        }
        catch (Exception)
        {
            // Same visibility rule as Publish: availability flips off, but WouldAnnounce stays
            // true because the call attempted the host boundary ("would have talked to it").
            _available = false;
        }
        return false;
    }

    private static string? s_lastAnnouncement;
    private static bool _announceFallbackLogged;

    /// <summary>Text of the last <see cref="Announce"/> call (null before the first one).</summary>
    public static string? LastAnnouncement => Volatile.Read(ref s_lastAnnouncement);

    /// <summary>Announcements the host provider accepted.</summary>
    public static int AnnouncementsSent { get; private set; }

    /// <summary>True when the last <see cref="Announce"/> call would have talked to the host.</summary>
    public static bool WouldAnnounce { get; private set; }

    private static void LogAnnounceFallbackOnce()
    {
        if (_announceFallbackLogged)
        {
            return;
        }
        _announceFallbackLogged = true;
        Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
            "[maui] screen reader announce fell back to ohos_host_accessibility_send_event " +
            "(event kind only): this host library predates ohos_host_accessibility_announce(const char* text)");
    }

    /// <summary>Finds a published node by id (used to route actions back to a hit test).</summary>
    public static bool TryFindNode(int id, out OpenHarmonyAccessibilityNode node)
    {
        // One immutable index snapshot: an action arriving while Refresh rebuilds the tree routes
        // with the last complete frame (never a half-swapped one and never a partially updated
        // rectangle), so a click cannot be misrouted onto torn bounds. Node ids are the frame's
        // positional slots (id = index + 1), so the lookup is a direct index into the snapshot.
        Frame frame = Volatile.Read(ref s_frame);
        if (id >= 1 && id <= frame.Nodes.Length && frame.Nodes[id - 1].Id == id)
        {
            node = frame.Nodes[id - 1];
            return true;
        }
        node = null!;
        return false;
    }

    /// <summary>
    /// Finds the view a published node was built from, so an action can be executed on it
    /// (scrolling, text edits). False when the node is unknown, is a modal alert node (which has
    /// no view; the alert itself handles the action) or its view has gone away.
    /// </summary>
    public static bool TryFindView(int id, out IView view)
    {
        Frame frame = Volatile.Read(ref s_frame);
        if (id >= 1 && id <= frame.Views.Length && frame.Nodes[id - 1].Id == id
            && frame.Views[id - 1] is { } found)
        {
            view = found;
            return true;
        }
        view = null!;
        return false;
    }

    /// <summary>
    /// True when the id belongs to the modal alert subtree of the published frame (an open
    /// Alert/ActionSheet/Prompt). While a modal is open only these nodes accept actions; the
    /// background tree stays published but non-focusable, so the screen reader's focus order
    /// (the host's focusable-bit scans) cannot leave the dialog.
    /// </summary>
    internal static bool IsModalNode(int id)
    {
        Frame frame = Volatile.Read(ref s_frame);
        return frame.ModalRootId != 0
            && id >= frame.ModalRootId
            && id <= frame.Nodes.Length
            && frame.Nodes[id - 1].Id == id;
    }

    /// <summary>Nodes handed to the host by the last publish pass (0 when unavailable).</summary>
    public static int LastPublishedCount { get; private set; }

    /// <summary>Frames whose publish was skipped because nothing changed (avoids per-frame marshalling).</summary>
    public static int FramesSkipped { get; private set; }

    /// <summary>Whether the last publish pass would have talked to the host (observable off-device).</summary>
    public static bool WouldPublish { get; private set; }

    // One baseline per window (M4 partition): the diff of a secondary frame must not compare
    // against the primary window's previous list.
    private static readonly object s_previousListsLock = new();
    private static readonly Dictionary<string, OpenHarmonyAccessibilityNode[]> s_previousLists = new(StringComparer.Ordinal);

    /// <summary>
    /// Changes detected between the last two published frames, as ArkUI accessibility event type
    /// flags (0x20 page state update, 0x800 page content update, 0x10 text update); the host turns
    /// these into OH_ArkUI_SendAccessibilityAsyncEvent calls.
    /// </summary>
    public static int PendingEventCount { get; private set; }

    public const int EventPageStateUpdate = 0x00000020;
    public const int EventPageContentUpdate = 0x00000800;
    public const int EventTextUpdate = 0x00000010;

    /// <summary>
    /// Proactive announcement event (ArkUI NDK
    /// ARKUI_ACCESSIBILITY_NATIVE_EVENT_TYPE_ANNOUNCE_FOR_ACCESSIBILITY, 0x10000000), the event
    /// kind that asks the screen reader to read text out of turn.
    /// </summary>
    public const int EventAnnouncement = 0x10000000;

    private static int DiffFrames(string windowId, OpenHarmonyAccessibilityNode[] current)
    {
        // Read the baseline snapshot, compare against the frame being published, then make the
        // frame the new baseline with a single reference swap: no Clear/AddRange on a collection
        // another thread could be enumerating.
        OpenHarmonyAccessibilityNode[] previous;
        lock (s_previousListsLock)
        {
            previous = s_previousLists.TryGetValue(windowId, out OpenHarmonyAccessibilityNode[]? list)
                ? list : Array.Empty<OpenHarmonyAccessibilityNode>();
        }
        int events = 0;
        if (previous.Length != current.Length)
        {
            events |= EventPageContentUpdate;
        }
        int shared = Math.Min(previous.Length, current.Length);
        for (int i = 0; i < shared; i++)
        {
            if (!string.Equals(previous[i].Text, current[i].Text, StringComparison.Ordinal)
                || !string.Equals(previous[i].Description, current[i].Description, StringComparison.Ordinal)
                || !string.Equals(previous[i].Hint, current[i].Hint, StringComparison.Ordinal))
            {
                events |= EventTextUpdate;
            }
            // Range/checked changes must force a republish even when the bounds and text did
            // not move (dragging a slider is exactly that case). double.Equals treats NaN as
            // equal to NaN, so an absent range does not look changed on every frame.
            bool rangeSame = previous[i].RangeMin.Equals(current[i].RangeMin)
                && previous[i].RangeMax.Equals(current[i].RangeMax)
                && previous[i].RangeCurrent.Equals(current[i].RangeCurrent);
            if (previous[i].Bounds != current[i].Bounds
                || !rangeSame
                || previous[i].Checked != current[i].Checked)
            {
                events |= EventPageStateUpdate;
            }
        }
        lock (s_previousListsLock)
        {
            s_previousLists[windowId] = current;
        }
        return events;
    }

    /// <summary>Rebuilds the shadow tree for a rendered frame (only when something moved).</summary>
    public static void Refresh(IView root) => Refresh(OpenHarmonyWindowSurface.PrimaryWindowId, root);

    /// <summary>
    /// MULTIWINDOW-L M4: rebuilds one window's shadow tree. Each window keeps its own frame
    /// (so a secondary render can no longer overwrite the primary's published tree) and only the
    /// window that owns an open alert republishes the alert's modal nodes.
    /// </summary>
    public static void Refresh(string windowId, IView root)
    {
        // The build buffers are render-thread state and are reused across frames: an unchanged
        // frame walks the tree, reuses every previous node and view and swaps nothing, so it
        // allocates no nodes, no lists and no arrays. A changed frame allocates only the new
        // snapshot (plus a node record per position that actually moved).
        lock (s_buildLock)
        {
            Frame previous = FrameOf(windowId);
            s_buildNodes.Clear();
            s_buildViews.Clear();
            s_buildPending.Clear();
            // An open alert is modal: the background tree stays in the frame with its content,
            // but every background node is republished non-focusable and the alert's own nodes
            // are appended after it as the only focusable region (the focus trap). Only the
            // window the alert belongs to appends them.
            OpenHarmonyAlertState? alert = OpenHarmonyAlertHost.CurrentFor(windowId);
            bool changed = Visit(root, previous, alert is not null);
            s_walkRoot = null;
            s_titleBarView = null;
            (int modalRootId, bool modalChanged) = AppendAlertNodes(previous, alert);
            changed |= modalChanged || modalRootId != previous.ModalRootId;
            if (!changed)
            {
                return;
            }
            // Publish the completed frame with atomic reference swaps: a callback on the
            // accessibility thread either sees the previous complete frame or this one, never a
            // partial rebuild, and the arrays themselves are never mutated after publication.
            StoreFrame(windowId, new Frame(s_buildNodes.ToArray(), s_buildViews.ToArray(), modalRootId));
        }
    }

    /// <summary>
    /// Hands the shadow tree to the host, which turns it into ArkUI accessibility element
    /// information served by the registered provider callbacks. Degrades to a no-op off-device.
    /// </summary>
    public static void Publish() => Publish(OpenHarmonyWindowSurface.PrimaryWindowId);

    /// <summary>
    /// MULTIWINDOW-L M4: publishes one window's shadow tree. The primary window keeps the
    /// historical provider path (native begin/node/commit, event diff, status probe); a
    /// secondary window's frame is diffed and kept locally, because the host provider is bound
    /// to the main window's NodeContent and there is no per-window provider yet (documented
    /// degrade: the primary tree is never overwritten and the subwindow tree stays addressable
    /// for a later provider).
    /// </summary>
    public static void Publish(string windowId)
    {
        Frame frame = FrameOf(windowId);
        OpenHarmonyAccessibilityNode[] nodes = frame.Nodes;
        // The event source is managed state, so the diff runs even when the host is unavailable.
        int events = DiffFrames(windowId, nodes);
        if (windowId != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            SecondaryPublishes++;
            LastSecondaryPublishedCount = nodes.Length;
            if (nodes.Length > 0 && !_secondaryProviderLogged)
            {
                _secondaryProviderLogged = true;
                Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
                    $"[maui] accessibility: window '{windowId}' keeps its shadow frame locally " +
                    "(no per-window provider yet; the main provider is unchanged)");
            }
            return;
        }
        PendingEventCount = events;
        if (!_available || nodes.Length == 0)
        {
            LastPublishedCount = 0;
            return;
        }
        WouldPublish = _available && nodes.Length > 0 && PendingEventCount != 0;
        if (!WouldPublish)
        {
            // Nothing moved: skip the native traffic (three calls plus UTF-8 marshalling per node).
            FramesSkipped++;
            LastPublishedCount = 0;
            return;
        }
        try
        {
            AccessibilityBegin(nodes.Length);
            foreach (OpenHarmonyAccessibilityNode node in nodes)
            {
                int flags = (node.IsEnabled ? 1 : 0) | (node.IsFocusable ? 2 : 0);
                bool modal = frame.ModalRootId != 0 && node.Id >= frame.ModalRootId;
                AccessibilityNode(node.Id, node.ParentId, node.Role, node.Text, node.Description, node.Hint,
                    node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height, flags,
                    ActionMask(node.Role, modal),
                    node.RangeMin, node.RangeMax, node.RangeCurrent, node.Checked);
            }
            AccessibilityCommit();
            LastPublishedCount = nodes.Length;
            FlushEvents();
            LogProviderStatusOnce();
        }
        catch (DllNotFoundException)
        {
            _available = false;
            LastPublishedCount = 0;
        }
        catch (EntryPointNotFoundException)
        {
            _available = false;
            LastPublishedCount = 0;
        }
    }

    // Reused build buffers, guarded by s_buildLock. Only Refresh touches them, and it is driven
    // from the render thread; the lock keeps a reentrant test/simulated call from corrupting a
    // walk in progress. They grow to the tree size once and are then reused every frame.
    private static readonly object s_buildLock = new();
    private static readonly List<OpenHarmonyAccessibilityNode> s_buildNodes = new();
    // Null slots are the modal alert nodes: they are built from the alert state, not from a view.
    private static readonly List<IView?> s_buildViews = new();
    private static readonly Stack<(IView View, int ParentId)> s_buildPending = new();
    private static readonly List<IView> s_buildChildren = new();
    // The walk in progress (N4): the render root and the window TitleBar row's view, so the
    // title bar can be seeded as the root's first child. Only Refresh/Visit touch them, under
    // s_buildLock, and they are cleared when the walk ends.
    private static IView? s_walkRoot;
    private static IView? s_titleBarView;

    /// <summary>
    /// The visible Window.TitleBar row's view for a render root, or null when the window has no
    /// TitleBar (or it is hidden). The row is a logical child of the window, so the accessibility
    /// walk reaches it through the window handler rather than through the page tree (N4).
    /// </summary>
    private static IView? ResolveTitleBarView(IView root)
    {
        if (root is not Microsoft.Maui.Controls.VisualElement element ||
            element.Window is not { Handler: OpenHarmonyWindowHandler windowHandler } ||
            windowHandler.TitleBar is not { IsVisible: true } titleBar)
        {
            return null;
        }
        return titleBar.View;
    }

    /// <summary>
    /// Builds the node for one position, reusing the previous frame's immutable record when every
    /// published value (and the view it was built from) is unchanged. Reuse is safe for in-flight
    /// snapshots: the reused record still holds the same values it was published with.
    /// </summary>
    private static OpenHarmonyAccessibilityNode BuildNode(IView view, int id, int parentId,
        OpenHarmonyAccessibilityNode? previous, bool modal)
    {
        RectF bounds = default;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            // Accessibility publishes canvas-space bounds (the resolved flow direction included).
            bounds = platform.CanvasFrame;
        }
        string role = RoleOf(view);
        if (view is VisualElement heading && SemanticProperties.GetHeadingLevel(heading) != SemanticHeadingLevel.None && role == "text")
        {
            role = "header";
        }
        string? text = view is IText textPart && !string.IsNullOrEmpty(textPart.Text) ? textPart.Text
            : view is ILabel label && !string.IsNullOrEmpty(label.Text) ? label.Text
            : null;
        // SEC-SCAN-4: a password entry must not publish its plaintext through the shadow tree.
        // The compositor already masks the drawn text (OpenHarmonyView.DisplayText); an enabled
        // screen reader could otherwise read the password verbatim from the published node. The
        // platform flag is authoritative when the handler is connected; the virtual IEntry flag
        // covers a view that has no platform view yet.
        bool passwordEntry = view is IEntry { IsPassword: true } ||
            (view.Handler?.PlatformView is OpenHarmonyView passwordPlatform && passwordPlatform.IsPassword);
        if (text is not null && passwordEntry)
        {
            text = OpenHarmonyView.MaskPassword(text);
        }
        string? description = view is VisualElement element ? SemanticProperties.GetDescription(element) : null;
        string? hint = view is VisualElement hintElement ? SemanticProperties.GetHint(hintElement) : null;
        bool enabled = view is not VisualElement visual || visual.IsEnabled;
        // While an alert is open the whole background tree drops the focusable bit: the host's
        // focus-move scans walk the published table by exactly this flag, so focus cannot leave
        // the dialog (the alert's own nodes are appended focusable after the tree).
        bool focusable = !modal && view is VisualElement focusableElement && focusableElement.IsEnabled && role != "group";
        // Range is published only where the control has one; NaN bounds mark it absent (the
        // host's "RangeMin <= RangeMax" check can never pass for NaN). Sliders use their own
        // Minimum/Maximum/Value; MAUI's IProgress.Progress is already a 0..1 fraction, so the
        // progress range is published as 0/1/Progress (no rescaling to 0..100).
        double rangeMin = double.NaN;
        double rangeMax = double.NaN;
        double rangeCurrent = 0;
        if (view is ISlider slider)
        {
            rangeMin = slider.Minimum;
            rangeMax = slider.Maximum;
            rangeCurrent = slider.Value;
        }
        else if (view is IProgress progress)
        {
            rangeMin = 0;
            rangeMax = 1;
            rangeCurrent = progress.Progress;
        }
        // Checked is 0/1 only for real toggle roles; -1 keeps the host from announcing a state
        // the control does not have (SetChecked is skipped for -1 on the host side).
        int checkedState = view switch
        {
            ISwitch toggle => toggle.IsOn ? 1 : 0,
            ICheckBox checkBox => checkBox.IsChecked ? 1 : 0,
            _ => -1,
        };
        if (previous is not null && NodeMatches(previous, id, parentId, role, text, description, hint, bounds,
            enabled, focusable, rangeMin, rangeMax, rangeCurrent, checkedState))
        {
            return previous;
        }
        return new OpenHarmonyAccessibilityNode(id, parentId, role, text, description, hint, bounds, enabled, focusable,
            rangeMin, rangeMax, rangeCurrent, checkedState);
    }

    /// <summary>Value equality of every published field; <c>double.Equals</c> keeps NaN == NaN.</summary>
    private static bool NodeMatches(OpenHarmonyAccessibilityNode node, int id, int parentId, string role,
        string? text, string? description, string? hint, RectF bounds, bool enabled, bool focusable,
        double rangeMin, double rangeMax, double rangeCurrent, int checkedState)
        => node.Id == id
            && node.ParentId == parentId
            && string.Equals(node.Role, role, StringComparison.Ordinal)
            && string.Equals(node.Text, text, StringComparison.Ordinal)
            && string.Equals(node.Description, description, StringComparison.Ordinal)
            && string.Equals(node.Hint, hint, StringComparison.Ordinal)
            && node.Bounds == bounds
            && node.IsEnabled == enabled
            && node.IsFocusable == focusable
            && node.RangeMin.Equals(rangeMin)
            && node.RangeMax.Equals(rangeMax)
            && node.RangeCurrent.Equals(rangeCurrent)
            && node.Checked == checkedState;

    private static bool Visit(IView root, Frame previous, bool modal)
    {
        // N4: the Window.TitleBar row is a logical child of the window, not of the page the walk
        // starts from, so it is seeded as the render root's first child (it is the topmost row).
        s_walkRoot = root;
        s_titleBarView = ResolveTitleBarView(root);
        // The view walk covers the content nodes only; the previous frame's alert nodes (appended
        // at ModalRootId) are not positions the walk can produce, so the length check compares
        // against the previous content count alone.
        int previousViewCount = previous.ModalRootId == 0 ? previous.Nodes.Length : previous.ModalRootId - 1;
        bool changed = false;
        s_buildPending.Push((root, 0));
        while (s_buildPending.Count > 0)
        {
            (IView view, int parent) = s_buildPending.Pop();
            int index = s_buildNodes.Count;
            int id = index + 1;
            // A position can be reused only when it is the same view: an identical-looking node
            // built from a different view must still re-index the frame so TryFindView routes
            // actions to the view actually being shown.
            OpenHarmonyAccessibilityNode? previousNode =
                index < previous.Nodes.Length && ReferenceEquals(previous.Views[index], view)
                    ? previous.Nodes[index]
                    : null;
            OpenHarmonyAccessibilityNode node = BuildNode(view, id, parent, previousNode, modal);
            s_buildNodes.Add(node);
            s_buildViews.Add(view);
            changed |= !ReferenceEquals(node, previousNode);
            PushChildren(view, id);
        }
        // A shorter frame is a change even when every surviving position reused its node.
        return changed || s_buildNodes.Count != previousViewCount;
    }

    /// <summary>
    /// Pushes a view's children onto the pending stack in traversal order, without the per-node
    /// list and iterator allocations the previous implementation paid on every frame.
    /// </summary>
    private static void PushChildren(IView view, int id)
    {
        s_buildChildren.Clear();
        // N4: the window TitleBar row leads the render root's children, matching the frame order
        // (the bar is the topmost row); its own subtree is pushed by the regular walk below.
        if (s_titleBarView is { } titleBarView && ReferenceEquals(view, s_walkRoot))
        {
            s_buildChildren.Add(titleBarView);
        }
        if (view is ILayout layout)
        {
            // Indexed access over IList<IView>: a foreach would allocate the interface enumerator.
            for (int i = 0; i < layout.Count; i++)
            {
                s_buildChildren.Add(layout[i]);
            }
        }
        if (view is Microsoft.Maui.Controls.NavigationPage navigation && navigation.CurrentPage is IView currentPage)
        {
            s_buildChildren.Add(currentPage);
        }
        // The compositor's ChildEnumerator yields a tabbed page's selected page exactly like a
        // navigation page's current page (with the same presented-content reference guard); the
        // shadow tree mirrors that walk so assistive technology sees what the frame draws. rc.1's
        // TabbedPage is not an IContentView, so the presented-content branch cannot cover it.
        if (view is Microsoft.Maui.Controls.TabbedPage tabbed &&
            tabbed.CurrentPage is IView tabPage &&
            !ReferenceEquals(tabPage, (view as IContentView)?.PresentedContent))
        {
            s_buildChildren.Add(tabPage);
        }
        // The same mirroring for a shell: the compositor's ChildEnumerator yields its CurrentPage,
        // so the shadow tree publishes what the frame draws. rc.1's Shell is not an IContentView,
        // so the presented-content branch below cannot reach the page it presents either.
        if (view is Microsoft.Maui.Controls.Shell shell &&
            shell.CurrentPage is IView shellPage &&
            !ReferenceEquals(shellPage, (view as IContentView)?.PresentedContent))
        {
            s_buildChildren.Add(shellPage);
        }
        // FIX-A11YFLYOUT: the compositor's ChildEnumerator yields a flyout page's detail page
        // (always visible) and its panel while presented, so the shadow tree publishes what the
        // frame draws. The hello-maui-app window is a FlyoutPage: without this branch the walk
        // stopped at the root and the device --a11y-probe reported nodeCount=1. rc.1's FlyoutPage
        // is not an IContentView, so the presented-content branch cannot reach either pane.
        if (view is Microsoft.Maui.Controls.FlyoutPage flyoutPage && flyoutPage.Detail is IView detail)
        {
            s_buildChildren.Add(detail);
        }
        if (view is Microsoft.Maui.Controls.FlyoutPage presentedFlyout &&
            presentedFlyout.IsPresented &&
            presentedFlyout.Flyout is IView flyoutContent)
        {
            s_buildChildren.Add(flyoutContent);
        }
        if (view is IContentView contentView && contentView.PresentedContent is IView presented && !ReferenceEquals(presented, view))
        {
            s_buildChildren.Add(presented);
        }
        for (int i = s_buildChildren.Count - 1; i >= 0; i--)
        {
            s_buildPending.Push((s_buildChildren[i], id));
        }
    }

    /// <summary>
    /// Appends the open alert's nodes after the view tree: a dialog root (title, or the message
    /// when there is no title), the message when both are present, the prompt text field and one
    /// button per accept/cancel label or action sheet row. Returns the dialog root's id (0 when
    /// no alert is open) and whether any appended node had to be rebuilt.
    /// </summary>
    private static (int RootId, bool Changed) AppendAlertNodes(Frame previous, OpenHarmonyAlertState? alert)
    {
        if (alert is null)
        {
            return (0, false);
        }
        bool changed = false;
        int rootId = s_buildNodes.Count + 1;
        // Title and message collapse into the dialog node when only one of them exists, so a
        // dialog with a message but no title does not get an empty leading focus stop.
        string? title = !string.IsNullOrEmpty(alert.Title) ? alert.Title : null;
        string? message = !string.IsNullOrEmpty(alert.Message) ? alert.Message : null;
        AddAlertNode(previous, 0, "dialog", title ?? message, null, null,
            OpenHarmonyAlertHost.BoxRect, focusable: true, ref changed);
        if (title is not null && message is not null)
        {
            AddAlertNode(previous, rootId, "text", message, null, null,
                OpenHarmonyAlertHost.MessageRect, focusable: true, ref changed);
        }
        if (alert.Kind == OpenHarmonyAlertKind.Prompt)
        {
            AddAlertNode(previous, rootId, "textInput", alert.PromptText, null, null,
                OpenHarmonyAlertHost.PromptRect, focusable: true, ref changed);
        }
        if (alert.Kind == OpenHarmonyAlertKind.ActionSheet)
        {
            for (int i = 0; i < alert.Options.Count; i++)
            {
                AddAlertNode(previous, rootId, "button", alert.Options[i], null, null,
                    OpenHarmonyAlertHost.OptionRect(i), focusable: true, ref changed);
            }
        }
        if (!string.IsNullOrEmpty(alert.Accept))
        {
            AddAlertNode(previous, rootId, "button", alert.Accept, null, null,
                OpenHarmonyAlertHost.AcceptRect, focusable: true, ref changed);
        }
        if (!string.IsNullOrEmpty(alert.Cancel))
        {
            AddAlertNode(previous, rootId, "button", alert.Cancel, null, null,
                OpenHarmonyAlertHost.CancelRect, focusable: true, ref changed);
        }
        return (rootId, changed);
    }

    /// <summary>
    /// Appends one alert node, reusing the previous frame's immutable record at the same position
    /// when it was also an alert node with identical published values (the same reuse rule the
    /// view walk applies, minus the view identity, which alert nodes do not have).
    /// </summary>
    private static void AddAlertNode(Frame previous, int parentId, string role, string? text,
        string? description, string? hint, RectF bounds, bool focusable, ref bool changed)
    {
        int index = s_buildNodes.Count;
        int id = index + 1;
        OpenHarmonyAccessibilityNode? previousNode =
            index < previous.Nodes.Length && previous.Views[index] is null ? previous.Nodes[index] : null;
        if (previousNode is not null && NodeMatches(previousNode, id, parentId, role, text, description,
            hint, bounds, true, focusable, double.NaN, double.NaN, 0, -1))
        {
            s_buildNodes.Add(previousNode);
            s_buildViews.Add(null);
            return;
        }
        s_buildNodes.Add(new OpenHarmonyAccessibilityNode(id, parentId, role, text, description, hint, bounds,
            true, focusable, double.NaN, double.NaN, 0, -1));
        s_buildViews.Add(null);
        changed = true;
    }

    private static string RoleOf(IView view) => view switch
    {
        IButton => "button",
        IEntry or IEditor => "textInput",
        ICheckBox => "checkBox",
        ISwitch => "switch",
        ISlider => "slider",
        IScrollView => "scroll",
        IProgress => "progress",
        Microsoft.Maui.IImage => "image",
        ILabel => "text",
        _ => "group",
    };
}
