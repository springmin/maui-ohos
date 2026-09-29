// Generic focus hook for the slice's non-text views: the seam NB13 documented in
// OpenHarmonyEntryHandler.cs ("A shared Invoke(\"Focus\"/\"Unfocus\") override keyed on the
// platform view (requesting focus for 'ohos_dotnet_surface') is the missing piece; until then a
// non-text focus is not expressible").
//
// The compositor draws every view into the one ArkUI XComponent ('ohos_dotnet_surface',
// .focusable(true) in the shell), so there is no per-view ArkUI node to name: the platform
// component key of any non-text view is that shared surface. Focus() asks the shell's
// registerFocusSink handler (through ohos_host_request_focus, the same export the text handlers
// use) to hand ArkUI focus to the surface, which is what makes the XComponent's onKeyEvent fire
// and route hardware keys through OpenHarmonyKeyListener. The text handlers (Entry/Editor/
// SearchBar) intercept the command in their own Invoke override before the shared command mapper
// is consulted, so the keyboard path and the 'ohos_dotnet_input' target are untouched.
//
// VisualElement.Focus() calls handler.Invoke("Focus", FocusRequest) and Unfocus() calls
// handler.Invoke("Unfocus", null) while IsFocused is true. MAUI's base ViewHandler resolves both
// through the shared ViewCommandMapper, whose default entries never set the FocusRequest result
// (off-device Focus() throws InvalidOperationException "No result value was set." today), so
// Install() replaces those two entries with this manager: that is the shared Invoke override,
// registered for every slice handler at once from UseOpenHarmony (nothing per-handler).
//
// State: Focus() is answered from the platform report (ohos_host_request_focus returns 0 only
// when the shell's focus sink actually handed ArkUI focus to the target). When the report is
// negative (no host library, an older host without the export, no shell sink, or a platform view
// that is not a compositor view) the call is a documented no-op: IsFocused stays false and
// Focus() returns false instead of throwing. On success the element's read-only IsFocused
// bindable key is set (exactly like the text handlers do) and the platform view records it;
// focusing another element clears the previous one, because ArkUI focus is exclusive. Unfocus()
// clears the managed state and names the surface again (the sink has no clear-focus op, and the
// surface is where hardware keys are delivered when no view is focused). The manager cannot
// observe a text handler taking ArkUI focus for the input ('ohos_dotnet_input'), so a stale
// non-text IsFocused is possible until the app calls Focus()/Unfocus() again; routing the text
// handlers through this manager would need them to change (outside this file).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyFocusManager
{
    /// <summary>The XComponent the managed content is rendered into (the shell's Surface id).</summary>
    private const string SurfaceId = "ohos_dotnet_surface";

    private static bool s_installed;

    // Weak: the focused element can be a whole page in the visual tree; tracking it must not root
    // a page the app has removed from the window.
    private static WeakReference<IView>? s_focused;

    /// <summary>Focus commands routed through the shared command mapper since Install().</summary>
    internal static int FocusRequests { get; private set; }

    /// <summary>Focus commands the shell answered (the surface took ArkUI focus).</summary>
    internal static int FocusHandled { get; private set; }

    /// <summary>Unfocus commands routed through the shared command mapper since Install().</summary>
    internal static int UnfocusRequests { get; private set; }

    /// <summary>The ArkUI target the last request named (null before the first one).</summary>
    internal static string? LastRequestedTarget { get; private set; }

    /// <summary>True when the shell answered the last request (false off-device/older host).</summary>
    internal static bool LastRequestHandled { get; private set; }

    /// <summary>The element the manager currently reports focused (null when none).</summary>
    internal static IView? FocusedView =>
        s_focused is { } reference && reference.TryGetTarget(out IView? view) ? view : null;

    /// <summary>
    /// Replaces the shared ViewCommandMapper Focus/Unfocus entries once. Idempotent; called from
    /// MauiOpenHarmonyExtensions.UseOpenHarmony so every handler that does not intercept the
    /// command itself resolves it through this manager instead of the result-less default.
    /// </summary>
    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        ViewHandler.ViewCommandMapper["Focus"] = OnFocusCommand;
        ViewHandler.ViewCommandMapper["Unfocus"] = OnUnfocusCommand;
    }

    private static void OnFocusCommand(IViewHandler handler, IView view, object? args)
    {
        FocusRequests++;
        bool handled = TryRouteFocus(view);
        if (handled)
        {
            FocusHandled++;
        }
        if (args is RetrievePlatformValueRequest<bool> request)
        {
            request.SetResult(handled);
        }
    }

    private static void OnUnfocusCommand(IViewHandler handler, IView view, object? args)
    {
        UnfocusRequests++;
        ClearFocus(view);
        if (args is RetrievePlatformValueRequest<bool> request)
        {
            request.SetResult(true);
        }
    }

    /// <summary>
    /// Routes this view's Focus() to the shell's focus sink. The target key comes from the
    /// platform view, the same element-to-platform mapping the accessibility shadow tree uses
    /// (node id -> view -> platform view): every compositor view is drawn into the one XComponent,
    /// so its platform component key is 'ohos_dotnet_surface'. A platform view that is not an
    /// OpenHarmonyView cannot be named by the managed side (the shell owns it), which is the
    /// documented no-op fallback.
    /// </summary>
    private static bool TryRouteFocus(IView view)
    {
        if (!TryGetTargetKey(view, out string targetKey))
        {
            LastRequestedTarget = null;
            LastRequestHandled = false;
            return false;
        }
        LastRequestedTarget = targetKey;
        bool handled = OpenHarmonyFocusBridge.RequestSurfaceFocus();
        LastRequestHandled = handled;
        if (!handled)
        {
            // No native host / no shell sink / older host: documented no-op, IsFocused unchanged.
            return false;
        }
        SetFocused(view);
        return true;
    }

    private static void SetFocused(IView view)
    {
        IView? previous = FocusedView;
        if (ReferenceEquals(previous, view))
        {
            ApplyFocusState(view, true);
            return;
        }
        if (previous is not null)
        {
            // ArkUI focus is exclusive: the element focused before this request is no longer it.
            ApplyFocusState(previous, false);
        }
        s_focused = new WeakReference<IView>(view);
        ApplyFocusState(view, true);
    }

    private static void ClearFocus(IView view)
    {
        if (!ReferenceEquals(FocusedView, view))
        {
            // MAUI only calls Unfocus() while IsFocused is true, so this is normally a stale
            // platform state; the call still succeeds as a no-op.
            return;
        }
        s_focused = null;
        ApplyFocusState(view, false);
        // The sink has no clear-focus op: naming the surface keeps ArkUI focus where the
        // hardware key events are delivered. Its answer is not part of the managed state.
        OpenHarmonyFocusBridge.RequestSurfaceFocus();
    }

    /// <summary>Marks the virtual and platform view focused/unfocused (the platform view is the
    /// source of truth for the compositor, the read-only IsFocused bindable key for app code,
    /// exactly like the text handlers' SetFocus).</summary>
    private static void ApplyFocusState(IView view, bool focused)
    {
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            platform.IsFocused = focused;
        }
        if (view is VisualElement element && element.IsFocused != focused)
        {
            element.SetValue(VisualElement.IsFocusedPropertyKey, focused);
        }
    }

    /// <summary>
    /// The platform component key this view's focus request names. Compositor views are all drawn
    /// into the shell's XComponent, so the key is the surface id; when a future host exposes
    /// per-view ArkUI nodes, OpenHarmonyView gains the id and this is the one place to return it.
    /// False (with an empty key) when the view has no compositor platform view.
    /// </summary>
    internal static bool TryGetTargetKey(IView view, out string targetKey)
    {
        if (view.Handler?.PlatformView is OpenHarmonyView)
        {
            targetKey = SurfaceId;
            return true;
        }
        targetKey = string.Empty;
        return false;
    }
}
