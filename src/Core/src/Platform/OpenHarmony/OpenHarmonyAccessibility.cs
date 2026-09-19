// Accessibility for OpenHarmony. The compositor draws custom pixels, so there is no ArkUI node
// tree for a screen reader to read: this builds a shadow tree from the MAUI view tree (bounds, text,
// description, role, state) which the host publishes through the ArkUI NDK accessibility provider
// (OH_ArkUI_NativeModule_GetNativeAccessibilityProvider / OH_ArkUI_AddAndGetAccessibilityElementInfo)
// and which tests can assert as a snapshot without a device.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

public sealed record OpenHarmonyAccessibilityNode(
    int Id,
    int ParentId,
    string Role,
    string? Text,
    string? Description,
    RectF Bounds,
    bool IsEnabled,
    bool IsFocusable);

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

    /// <summary>Nodes of the last frame (root first, parents before children).</summary>
    public static IReadOnlyList<OpenHarmonyAccessibilityNode> Nodes => s_nodes;

    /// <summary>Rebuilds the shadow tree for a rendered frame.</summary>
    public static void Refresh(IView root)
    {
        s_nodes.Clear();
        Visit(root, 0);
    }

    private static void Visit(IView view, int parentId)
    {
        int id = s_nodes.Count + 1;
        RectF bounds = default;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            bounds = platform.Frame;
        }
        string role = RoleOf(view);
        string? text = view is IText textPart && !string.IsNullOrEmpty(textPart.Text) ? textPart.Text
            : view is ILabel label && !string.IsNullOrEmpty(label.Text) ? label.Text
            : null;
        string? description = view is VisualElement element ? SemanticProperties.GetDescription(element) : null;
        bool enabled = view is not VisualElement visual || visual.IsEnabled;
        bool focusable = view is VisualElement focusableElement && focusableElement.IsEnabled && role != "group";
        s_nodes.Add(new OpenHarmonyAccessibilityNode(id, parentId, role, text, description, bounds, enabled, focusable));
        foreach (IView child in ChildrenOf(view))
        {
            Visit(child, id);
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
        Microsoft.Maui.IImage => "image",
        ILabel => "text",
        _ => "group",
    };
}
