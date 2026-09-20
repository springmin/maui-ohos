// Accessibility for OpenHarmony. The compositor draws custom pixels, so there is no ArkUI node
// tree for a screen reader to read: this builds a shadow tree from the MAUI view tree (bounds, text,
// description, role, state) which the host publishes through the ArkUI NDK accessibility provider
// (OH_ArkUI_NativeModule_GetNativeAccessibilityProvider / OH_ArkUI_AddAndGetAccessibilityElementInfo)
// and which tests can assert as a snapshot without a device.
using System.Runtime.InteropServices;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

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

public static class OpenHarmonyAccessibility
{
    /// <summary>Actions a node with the given role can perform (published to the provider).</summary>
    public static IReadOnlyList<OpenHarmonyAccessibilityAction> ActionsFor(string role)
        => role switch
        {
            "button" => new[] { OpenHarmonyAccessibilityAction.Click, OpenHarmonyAccessibilityAction.LongClick },
            "text" => new[] { OpenHarmonyAccessibilityAction.Click },
            "textInput" => new[] { OpenHarmonyAccessibilityAction.Click, OpenHarmonyAccessibilityAction.SetText, OpenHarmonyAccessibilityAction.SetCursorPosition, OpenHarmonyAccessibilityAction.SelectText, OpenHarmonyAccessibilityAction.Copy, OpenHarmonyAccessibilityAction.Paste, OpenHarmonyAccessibilityAction.Cut },
            "checkBox" or "switch" => new[] { OpenHarmonyAccessibilityAction.Click },
            "slider" => new[] { OpenHarmonyAccessibilityAction.ScrollForward, OpenHarmonyAccessibilityAction.ScrollBackward, OpenHarmonyAccessibilityAction.SetText },
            _ => Array.Empty<OpenHarmonyAccessibilityAction>(),
        };

    private static readonly List<OpenHarmonyAccessibilityNode> s_nodes = new();
    private static readonly Dictionary<int, OpenHarmonyAccessibilityNode> s_index = new();

    /// <summary>Nodes of the last frame (root first, parents before children).</summary>
    public static IReadOnlyList<OpenHarmonyAccessibilityNode> Nodes => s_nodes;

    private const string HostLibrary = "libopenharmonyhost.so";
    private static bool _available = true;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_begin")]
    private static extern int AccessibilityBegin(int count);

    // The argument order is the publish contract shared with openharmony_host.h /
    // openharmony_host.c; the interaction harness reflects this method and compares the
    // parameter names/types against the C definition, so an arity or order change fails
    // off-device instead of shifting arguments on device (see the audit doc, section 30).
    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_node")]
    private static extern int AccessibilityNode(int id, int parentId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string role,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? description,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? hint,
        float x, float y, float width, float height, int flags, int actions,
        double rangeMin, double rangeMax, double rangeCurrent, int @checked);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_commit")]
    private static extern int AccessibilityCommit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ActionListener(int nodeId, int action);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_set_action_listener")]
    private static extern void SetActionListener(IntPtr callback);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_send_event")]
    private static extern int SendEvent(int eventType);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_provider_status")]
    private static extern int ProviderStatus();

    private static ActionListener? _actionThunk;
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
            _actionThunk ??= OnAction;
            SetActionListener(Marshal.GetFunctionPointerForDelegate(_actionThunk));
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

    private static void OnAction(int nodeId, int action)
        => _actionHandler?.Invoke(nodeId, action);

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

    /// <summary>Finds a published node by id (used to route actions back to a hit test).</summary>
    public static bool TryFindNode(int id, out OpenHarmonyAccessibilityNode node)
    {
        foreach (OpenHarmonyAccessibilityNode candidate in s_nodes)
        {
            if (candidate.Id == id)
            {
                node = candidate;
                return true;
            }
        }
        node = null!;
        return false;
    }

    /// <summary>Nodes handed to the host by the last publish pass (0 when unavailable).</summary>
    public static int LastPublishedCount { get; private set; }

    /// <summary>Frames whose publish was skipped because nothing changed (avoids per-frame marshalling).</summary>
    public static int FramesSkipped { get; private set; }

    /// <summary>Whether the last publish pass would have talked to the host (observable off-device).</summary>
    public static bool WouldPublish { get; private set; }

    private static readonly List<OpenHarmonyAccessibilityNode> s_previousList = new();

    /// <summary>
    /// Changes detected between the last two published frames, as ArkUI accessibility event type
    /// flags (0x20 page state update, 0x800 page content update, 0x10 text update); the host turns
    /// these into OH_ArkUI_SendAccessibilityAsyncEvent calls.
    /// </summary>
    public static int PendingEventCount { get; private set; }

    public const int EventPageStateUpdate = 0x00000020;
    public const int EventPageContentUpdate = 0x00000800;
    public const int EventTextUpdate = 0x00000010;

    private static int DiffFrames()
    {
        int events = 0;
        if (s_previousList.Count != s_nodes.Count)
        {
            events |= EventPageContentUpdate;
        }
        int shared = Math.Min(s_previousList.Count, s_nodes.Count);
        for (int i = 0; i < shared; i++)
        {
            if (!string.Equals(s_previousList[i].Text, s_nodes[i].Text, StringComparison.Ordinal)
                || !string.Equals(s_previousList[i].Description, s_nodes[i].Description, StringComparison.Ordinal)
                || !string.Equals(s_previousList[i].Hint, s_nodes[i].Hint, StringComparison.Ordinal))
            {
                events |= EventTextUpdate;
            }
            // Range/checked changes must force a republish even when the bounds and text did
            // not move (dragging a slider is exactly that case). double.Equals treats NaN as
            // equal to NaN, so an absent range does not look changed on every frame.
            bool rangeSame = s_previousList[i].RangeMin.Equals(s_nodes[i].RangeMin)
                && s_previousList[i].RangeMax.Equals(s_nodes[i].RangeMax)
                && s_previousList[i].RangeCurrent.Equals(s_nodes[i].RangeCurrent);
            if (s_previousList[i].Bounds != s_nodes[i].Bounds
                || !rangeSame
                || s_previousList[i].Checked != s_nodes[i].Checked)
            {
                events |= EventPageStateUpdate;
            }
        }
        s_previousList.Clear();
        s_previousList.AddRange(s_nodes);
        return events;
    }

    /// <summary>Rebuilds the shadow tree for a rendered frame.</summary>
    public static void Refresh(IView root)
    {
        s_nodes.Clear();
        s_index.Clear();
        Visit(root, 0);
    }

    /// <summary>
    /// Hands the shadow tree to the host, which turns it into ArkUI accessibility element
    /// information served by the registered provider callbacks. Degrades to a no-op off-device.
    /// </summary>
    public static void Publish()
    {
        // The event source is managed state, so the diff runs even when the host is unavailable.
        PendingEventCount = DiffFrames();
        if (!_available || s_nodes.Count == 0)
        {
            LastPublishedCount = 0;
            return;
        }
        WouldPublish = _available && s_nodes.Count > 0 && PendingEventCount != 0;
        if (!WouldPublish)
        {
            // Nothing moved: skip the native traffic (three calls plus UTF-8 marshalling per node).
            FramesSkipped++;
            LastPublishedCount = 0;
            return;
        }
        try
        {
            AccessibilityBegin(s_nodes.Count);
            foreach (OpenHarmonyAccessibilityNode node in s_nodes)
            {
                int flags = (node.IsEnabled ? 1 : 0) | (node.IsFocusable ? 2 : 0);
                int actions = 0;
                foreach (OpenHarmonyAccessibilityAction action in ActionsFor(node.Role))
                {
                    actions |= (int)action;
                }
                AccessibilityNode(node.Id, node.ParentId, node.Role, node.Text, node.Description, node.Hint,
                    node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height, flags, actions,
                    node.RangeMin, node.RangeMax, node.RangeCurrent, node.Checked);
            }
            AccessibilityCommit();
            LastPublishedCount = s_nodes.Count;
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

    private static int BuildNode(IView view, int parentId)
    {
        int id = s_nodes.Count + 1;
        RectF bounds = default;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            bounds = platform.Frame;
        }
        string role = RoleOf(view);
        if (view is VisualElement heading && SemanticProperties.GetHeadingLevel(heading) != SemanticHeadingLevel.None && role == "text")
        {
            role = "header";
        }
        string? text = view is IText textPart && !string.IsNullOrEmpty(textPart.Text) ? textPart.Text
            : view is ILabel label && !string.IsNullOrEmpty(label.Text) ? label.Text
            : null;
        string? description = view is VisualElement element ? SemanticProperties.GetDescription(element) : null;
        string? hint = view is VisualElement hintElement ? SemanticProperties.GetHint(hintElement) : null;
        bool enabled = view is not VisualElement visual || visual.IsEnabled;
        bool focusable = view is VisualElement focusableElement && focusableElement.IsEnabled && role != "group";
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
        var node = new OpenHarmonyAccessibilityNode(id, parentId, role, text, description, hint, bounds, enabled, focusable,
            rangeMin, rangeMax, rangeCurrent, checkedState);
        s_nodes.Add(node);
        s_index[id] = node;
        return id;
    }
    private static void Visit(IView root, int parentId)
    {
        var pending = new Stack<(IView View, int ParentId)>();
        pending.Push((root, parentId));
        while (pending.Count > 0)
        {
            (IView view, int parent) = pending.Pop();
            int id = BuildNode(view, parent);
            var children = new List<IView>();
            foreach (IView child in ChildrenOf(view))
            {
                children.Add(child);
            }
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push((children[i], id));
            }
        }
    }

    /// <summary>Layout children plus a content view's presented content (same shape as the renderer).</summary>
    private static IEnumerable<IView> ChildrenOf(IView view)
    {
        if (view is ILayout layout)
        {
            foreach (IView child in layout)
            {
                yield return child;
            }
        }
        if (view is Microsoft.Maui.Controls.NavigationPage navigation && navigation.CurrentPage is IView currentPage)
        {
            yield return currentPage;
        }
        if (view is IContentView contentView && contentView.PresentedContent is IView presented && !ReferenceEquals(presented, view))
        {
            yield return presented;
        }
    }

    private static string RoleOf(IView view) => view switch
    {
        IButton => "button",
        IEntry or IEditor => "textInput",
        ICheckBox => "checkBox",
        ISwitch => "switch",
        ISlider => "slider",
        IProgress => "progress",
        Microsoft.Maui.IImage => "image",
        ILabel => "text",
        _ => "group",
    };
}
