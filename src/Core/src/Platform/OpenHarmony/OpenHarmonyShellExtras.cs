// Shell handler extras that have no ArkUI/ArkTS surface in the slice yet: SearchHandler state and
// the flyout header/footer.
//
// The Shell handler draws its own chrome with the compositor (OpenHarmonyView): title bar, bottom
// tab bar and a flyout panel built from OpenHarmonyView.FlyoutItems. The one ArkTS-side panel
// bridge in the slice is the menu table (OpenHarmonyMenus -> ohos_host_menu_begin/item/commit ->
// host.registerMenuChangedSink -> ArkUI bindMenu); it carries menu rows only, so it has no column
// for a search field or for flyout section content. This type is the managed snapshot of that
// state - the payload a shell bridge would publish.
//
// What the handler expresses through the existing compositor surface today:
//   TabBarIsVisible       -> OpenHarmonyView.TabTitlesVisible (effective value: current page and
//                            its ancestors, then the shell; see the Shell handler)
//   FlyoutBehavior        -> OpenHarmonyView.ShowsHamburger (Disabled hides it) and FlyoutOpen
//                            (Locked keeps the panel open across redraws)
//   FlyoutHeader/Footer   -> a plain-text header/footer is published as the first/last row of the
//                            compositor flyout panel (non-selectable, see FlyoutItems in the
//                            handler); a View/DataTemplate section cannot be drawn there
//   SearchHandler         -> attach/detach, query, placeholder and visibility are tracked here
//
// Bridge needed for the ArkTS half (mirrors the menu table contract; no host source is edited by
// the slice, this is the exact contract a host+shell increment has to add):
//   int  ohos_host_shell_search_set(const char* query, const char* placeholder, int visible, int enabled);
//   void ohos_host_shell_search_set_listener(void* callback);   // callback(int op, const char* text)
//   napi host.registerShellSearchChangedSink(fn) + host.shellSearchQuery()/shellSearchPlaceholder()
//   napi host.notifyShellSearch(op, text)                       // op 0 = query, 1 = submit, 2 = clear
//   int  ohos_host_shell_flyout_header(const char* text);       // NULL/"" clears
//   int  ohos_host_shell_flyout_footer(const char* text);
//   napi host.registerShellFlyoutChangedSink(fn) + host.shellFlyoutHeader()/host.shellFlyoutFooter()
// The shell renders the search field with an ArkUI Search/TextInput (and the flyout sections in
// the panel it opens); the managed callbacks map back onto SearchHandler.Query /
// QueryConfirmed() / ClearPlaceholderClicked().
// Once per attach the handler logs this contract through OpenHarmonyBridge.WriteStatus, so a
// device run shows the managed half is live while the ArkTS half is absent.
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The search state a shell search bridge would publish: visible when the current page (or the
/// shell) carries a <see cref="Microsoft.Maui.Controls.SearchHandler"/>, plus its query text.
/// </summary>
public sealed record OpenHarmonyShellSearchState(
    bool IsVisible,
    bool IsEnabled,
    string? Query,
    string? Placeholder,
    string? PageTitle);

/// <summary>Managed snapshot of the shell extras the compositor cannot draw yet.</summary>
public static class OpenHarmonyShellExtras
{
    /// <summary>Last search state (invisible before a handler is attached).</summary>
    public static OpenHarmonyShellSearchState Search { get; private set; } = new(false, false, null, null, null);

    /// <summary>Text of the current shell's flyout header, when it is a string/Label.</summary>
    public static string? FlyoutHeaderText { get; private set; }

    /// <summary>Text of the current shell's flyout footer, when it is a string/Label.</summary>
    public static string? FlyoutFooterText { get; private set; }

    /// <summary>Search state transitions observed (attach, detach, query/placeholder/enabled change).</summary>
    public static int SearchStateChanges { get; private set; }

    /// <summary>Search handlers attached to a shell (visible transitions).</summary>
    public static int SearchAttaches { get; private set; }

    /// <summary>Search handlers detached from a shell (visible -> invisible transitions).</summary>
    public static int SearchDetaches { get; private set; }

    /// <summary>
    /// Text a flyout section contributes to the compositor panel: plain strings and Labels have
    /// one; every other View/DataTemplate needs the shell bridge (returns null).
    /// </summary>
    public static string? SectionText(object? section) => section switch
    {
        string text when !string.IsNullOrEmpty(text) => text,
        Microsoft.Maui.Controls.Label label when !string.IsNullOrEmpty(label.Text) => label.Text,
        _ => null,
    };

    internal static void UpdateSearch(Microsoft.Maui.Controls.SearchHandler? handler, string? pageTitle)
    {
        var next = new OpenHarmonyShellSearchState(
            handler is not null,
            handler?.IsSearchEnabled ?? false,
            handler?.Query,
            handler?.Placeholder,
            pageTitle);
        if (next == Search)
        {
            return;
        }
        bool wasVisible = Search.IsVisible;
        Search = next;
        SearchStateChanges++;
        if (!wasVisible && next.IsVisible)
        {
            SearchAttaches++;
            LogOnce(
                $"search attached page='{next.PageTitle}' query='{next.Query}' placeholder='{next.Placeholder}' " +
                "enabled=" + next.IsEnabled,
                "needs ohos_host_shell_search_set/listener (ohos_host_shell_search_set + registerShellSearchChangedSink)");
        }
        else if (wasVisible && !next.IsVisible)
        {
            SearchDetaches++;
            LogOnce("search detached", null);
        }
    }

    internal static void SetFlyoutSections(string? header, string? footer)
    {
        if (string.Equals(header, FlyoutHeaderText, StringComparison.Ordinal)
            && string.Equals(footer, FlyoutFooterText, StringComparison.Ordinal))
        {
            return;
        }
        bool hadSection = FlyoutHeaderText is not null || FlyoutFooterText is not null;
        FlyoutHeaderText = header;
        FlyoutFooterText = footer;
        if (!hadSection && (header is not null || footer is not null))
        {
            LogOnce(
                $"flyout header='{header}' footer='{footer}'",
                "rich sections need ohos_host_shell_flyout_header/footer");
        }
    }

    private static void LogOnce(string what, string? missing)
    {
        try
        {
            string suffix = missing is null
                ? string.Empty
                : $" (no ArkTS bridge: {missing}; the managed state is observable meanwhile)";
            OpenHarmonyBridge.WriteStatus($"[maui] shell {what}{suffix}");
        }
        catch (Exception)
        {
            // Status logging is diagnostics; it must never surface into the handler.
        }
    }
}
