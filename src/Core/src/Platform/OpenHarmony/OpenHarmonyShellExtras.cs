// Shell handler extras that have no ArkUI/ArkTS surface in the compositor: SearchHandler state and
// the flyout header/footer.
//
// The Shell handler draws its own chrome with the compositor (OpenHarmonyView): title bar, bottom
// tab bar and a flyout panel built from OpenHarmonyView.FlyoutItems. The ArkTS half of the extras
// is the shell panel above the XComponent (flyout header/footer labels plus a search field):
//   * SearchHandler state (query/placeholder/visible/enabled) is published through
//     ohos_host_shell_search_set; the shell applies it via host.registerShellSearchChangedSink
//     and reports the user's interactions back through host.notifyShellSearch, which invokes the
//     listener registered here with ohos_host_shell_search_set_listener. op 0 (query changed)
//     writes SearchHandler.Query, op 1 (submit) calls ISearchHandlerController.QueryConfirmed and
//     op 2 (cancel/clear) empties Query again.
//   * FlyoutHeader/Footer plain text (a string or Label, see SectionText) is published through
//     ohos_host_shell_flyout_header/footer; the shell applies it via
//     host.registerShellFlyoutChangedSink, where NULL or "" clears the label. The compositor keeps
//     drawing the same text as the first/last flyout row (see FlyoutItems in the handler).
//
// What still has no bridge: a rich View/DataTemplate flyout section (SectionText returns null for
// anything but a string/Label) cannot cross either channel.
//
// Every native call is probed once with NativeLibrary.TryGetExport (the screen reader's announce
// probe pattern). Without libopenharmonyhost.so (desktop tests) the managed snapshot below stays
// the live state, no call is attempted after the first miss and one status line records it.
using System.Runtime.InteropServices;
using Microsoft.Maui.Dispatching;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The search state published to the shell search bridge: visible when the current page (or the
/// shell) carries a <see cref="SearchHandler"/>, plus its query text.
/// </summary>
public sealed record OpenHarmonyShellSearchState(
    bool IsVisible,
    bool IsEnabled,
    string? Query,
    string? Placeholder,
    string? PageTitle);

/// <summary>Managed snapshot of the shell extras and the host bridge that publishes them.</summary>
public static partial class OpenHarmonyShellExtras
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const string SearchSetEntryPoint = "ohos_host_shell_search_set";
    private const string SearchListenerEntryPoint = "ohos_host_shell_search_set_listener";
    private const string FlyoutHeaderEntryPoint = "ohos_host_shell_flyout_header";
    private const string FlyoutFooterEntryPoint = "ohos_host_shell_flyout_footer";

    [LibraryImport(HostLibrary, EntryPoint = SearchSetEntryPoint, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ShellSearchSetNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? query,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? placeholder,
        int visible, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SearchInteractionCallback(int op, IntPtr text);

    [LibraryImport(HostLibrary, EntryPoint = SearchListenerEntryPoint)]
    private static partial void ShellSearchSetListenerNative(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = FlyoutHeaderEntryPoint, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ShellFlyoutHeaderNative([MarshalAs(UnmanagedType.LPUTF8Str)] string? text);

    [LibraryImport(HostLibrary, EntryPoint = FlyoutFooterEntryPoint, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ShellFlyoutFooterNative([MarshalAs(UnmanagedType.LPUTF8Str)] string? text);

    // 0 unknown, 1 exported, -1 missing (cached probes; see the header).
    private static int s_searchSetExport;
    private static int s_searchListenerExport;
    private static int s_flyoutHeaderExport;
    private static int s_flyoutFooterExport;
    private static bool s_searchMissingLogged;
    private static bool s_flyoutHeaderMissingLogged;
    private static bool s_flyoutFooterMissingLogged;

    private static unsafe IntPtr s_searchInteractionThunk = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr, void>)&OnSearchInteraction;
    private static bool s_searchListenerRegistered;
    private static SearchHandler? s_searchHandler;
    private static (string? Query, string? Placeholder, bool Visible, bool Enabled) s_lastSearchPayload;

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
    /// Text a flyout section contributes to the panel: plain strings and Labels have one; every
    /// other View/DataTemplate needs a rich-sections bridge that does not exist (returns null).
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
        // Native callbacks route to the handler of the current page; a page switch can carry the
        // same search state, so track it even when the state below is unchanged.
        s_searchHandler = next.IsVisible ? handler : null;
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
                "enabled=" + next.IsEnabled);
        }
        else if (wasVisible && !next.IsVisible)
        {
            SearchDetaches++;
            LogOnce("search detached");
        }
        PublishSearch(next);
    }

    internal static void SetFlyoutSections(string? header, string? footer)
    {
        if (string.Equals(header, FlyoutHeaderText, StringComparison.Ordinal)
            && string.Equals(footer, FlyoutFooterText, StringComparison.Ordinal))
        {
            return;
        }
        bool hadSection = FlyoutHeaderText is not null || FlyoutFooterText is not null;
        bool headerChanged = !string.Equals(header, FlyoutHeaderText, StringComparison.Ordinal);
        bool footerChanged = !string.Equals(footer, FlyoutFooterText, StringComparison.Ordinal);
        FlyoutHeaderText = header;
        FlyoutFooterText = footer;
        if (!hadSection && (header is not null || footer is not null))
        {
            LogOnce($"flyout header='{header}' footer='{footer}'");
        }
        if (headerChanged)
        {
            PublishFlyoutSection(0, header);
        }
        if (footerChanged)
        {
            PublishFlyoutSection(1, footer);
        }
    }

    /// <summary>
    /// Publishes the search state to the shell sink. The payload is compared separately from the
    /// observable state (the page title is managed-only), so a plain page switch does not re-send
    /// an unchanged payload.
    /// </summary>
    private static void PublishSearch(OpenHarmonyShellSearchState state)
    {
        var payload = (state.Query, state.Placeholder, state.IsVisible, state.IsEnabled);
        if (payload == s_lastSearchPayload)
        {
            return;
        }
        s_lastSearchPayload = payload;
        EnsureSearchListener();
        if (!ExportAvailable(SearchSetEntryPoint, ref s_searchSetExport))
        {
            LogMissingOnce(ref s_searchMissingLogged, SearchSetEntryPoint);
            return;
        }
        try
        {
            // Hidden/disabled state clears the shell field; the stored getters back the shell's
            // startup read either way.
            int rc = ShellSearchSetNative(
                state.IsVisible ? state.Query : null,
                state.IsVisible ? state.Placeholder : null,
                state.IsVisible ? 1 : 0,
                state.IsEnabled ? 1 : 0);
            if (rc != 0)
            {
                LogOnce($"search state not queued (host rc={rc})");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref s_searchSetExport, -1);
            LogMissingOnce(ref s_searchMissingLogged, SearchSetEntryPoint);
        }
        catch (Exception ex)
        {
            LogOnce($"search state publish failed: {ex.GetType().Name}");
        }
    }

    private static void EnsureSearchListener()
    {
        if (s_searchListenerRegistered)
        {
            return;
        }
        s_searchListenerRegistered = true;
        if (!ExportAvailable(SearchListenerEntryPoint, ref s_searchListenerExport))
        {
            LogMissingOnce(ref s_searchMissingLogged, SearchListenerEntryPoint);
            return;
        }
        try
        {
            ShellSearchSetListenerNative(s_searchInteractionThunk);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref s_searchListenerExport, -1);
            LogMissingOnce(ref s_searchMissingLogged, SearchListenerEntryPoint);
        }
        catch (Exception ex)
        {
            LogOnce($"search listener registration failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The host's listener: op 0 query changed, 1 submit, 2 cancel. The host calls this from the
    /// ArkTS/NAPI thread, so the handler is only touched on the MAUI dispatcher (a reverse
    /// P/Invoke boundary: nothing may escape into native code).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSearchInteraction(int op, IntPtr text)
    {
        try
        {
            SearchHandler? handler = s_searchHandler;
            if (handler is null)
            {
                return;
            }
            string value = text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text) ?? string.Empty;
            RunOnDispatcher(() => ApplySearchInteraction(handler, op, value));
        }
        catch (Exception ex)
        {
            try
            {
                OpenHarmonyBridge.WriteStatus(
                    $"[maui] shell search callback failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }
    }

    private static void ApplySearchInteraction(SearchHandler handler, int op, string text)
    {
        switch (op)
        {
            case 0:
                handler.Query = text;
                break;
            case 1:
                if (handler is ISearchHandlerController controller)
                {
                    controller.QueryConfirmed();
                }
                break;
            case 2:
                handler.Query = string.Empty;
                break;
        }
    }

    private static void RunOnDispatcher(Action action)
    {
        IDispatcher? dispatcher = null;
        try
        {
            dispatcher = OpenHarmonyHandlerConnector.Context?.Services.GetService(typeof(IDispatcher)) as IDispatcher;
        }
        catch (Exception)
        {
            // No service provider (off-device): the action runs inline below.
        }
        if (dispatcher is not null && dispatcher.IsDispatchRequired)
        {
            dispatcher.Dispatch(action);
            return;
        }
        action();
    }

    private static void PublishFlyoutSection(int op, string? text)
    {
        bool header = op == 0;
        string entryPoint = header ? FlyoutHeaderEntryPoint : FlyoutFooterEntryPoint;
        ref int export = ref (header ? ref s_flyoutHeaderExport : ref s_flyoutFooterExport);
        ref bool missingLogged = ref (header ? ref s_flyoutHeaderMissingLogged : ref s_flyoutFooterMissingLogged);
        if (!ExportAvailable(entryPoint, ref export))
        {
            LogMissingOnce(ref missingLogged, entryPoint);
            return;
        }
        try
        {
            int rc = header ? ShellFlyoutHeaderNative(text) : ShellFlyoutFooterNative(text);
            if (rc != 0)
            {
                LogOnce($"flyout text not queued (host rc={rc})");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref export, -1);
            LogMissingOnce(ref missingLogged, entryPoint);
        }
        catch (Exception ex)
        {
            LogOnce($"flyout text publish failed: {ex.GetType().Name}");
        }
    }

    /// <summary>True when the loaded host library exports <paramref name="entryPoint"/>.</summary>
    private static bool ExportAvailable(string entryPoint, ref int cache)
    {
        int known = Volatile.Read(ref cache);
        if (known != 0)
        {
            return known > 0;
        }
        bool available;
        try
        {
            available = NativeLibrary.TryLoad(HostLibrary, out IntPtr handle) &&
                NativeLibrary.TryGetExport(handle, entryPoint, out _);
        }
        catch (Exception)
        {
            available = false;
        }
        Volatile.Write(ref cache, available ? 1 : -1);
        return available;
    }

    private static void LogMissingOnce(ref bool logged, string entryPoint)
    {
        if (logged)
        {
            return;
        }
        logged = true;
        LogOnce($"state kept managed-only (no {entryPoint} export; the snapshot is observable meanwhile)");
    }

    private static void LogOnce(string what)
    {
        try
        {
            OpenHarmonyBridge.WriteStatus($"[maui] shell {what}");
        }
        catch (Exception)
        {
            // Status logging is diagnostics; it must never surface into the handler.
        }
    }
}
