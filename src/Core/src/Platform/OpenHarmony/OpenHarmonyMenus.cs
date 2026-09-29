// Desktop menus (MenuBarItem / MenuFlyout) for OpenHarmony.
//
// The compositor draws custom pixels, so there is no ArkUI component tree that could own a
// MenuBarItem: the current page's menu bars are published to the native host as a structured
// table (ohos_host_menu_begin/item_ex/commit) which the ArkTS shell reads back and renders with
// a Menu custom builder (one MenuItemGroup per bar, MenuItem({builder}) for a submenu). A tap in
// the shell calls host.notifyMenuAction(index), the host invokes the callback registered here,
// and the matching MenuFlyoutItem is activated through IMenuItemController.Activate() - the same
// activation path as the navigation bar's toolbar items, so Clicked and Command both fire.
//
// Shape of the table (index space shared with the shell; every row's Index is its position):
//   - one Bar row per MenuBarItem (Kind=Bar, Level=0, Item=null): the shell renders its Text as
//     the group header; a bar whose elements are all separators/empty submenus is dropped.
//   - every MenuBarItem element is published in declaration order with Level=1, a MenuFlyoutItem
//     as an Item row and an enabled MenuFlyoutSubItem as a Submenu header whose children follow
//     with Level+1 (recursively, so nested submenus keep their depth). A submenu with no
//     reachable children is published as a plain item row (nothing to open).
//   - MenuFlyoutSeparators are skipped (they are not actionable).
//   - a disabled submenu is published as a disabled item row (it cannot be opened, so its
//     children are not reachable either).
//   - an item is enabled only when both its MenuBarItem and the item itself are enabled.
//   - BarTitle carries the owning MenuBarItem's text on every row.
//
// The refresh is self-wired: a module initializer listens to the platform's redraw requests
// (raised by the navigation/tab/flyout/shell handlers on page changes) and to frame ticks, then
// resolves the window's current page through the app host and republishes when the table
// changed. Every native call is guarded: with no libopenharmonyhost.so (desktop tests) the
// managed snapshot still updates and the native path degrades silently. A host library that
// predates ohos_host_menu_item_ex gets the legacy flat table (submenu headers dropped, children
// with their depth), so the structured and legacy publishes share one activation map.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Kind of a published menu row (the shell renders each kind differently).</summary>
public enum OpenHarmonyMenuEntryKind
{
    /// <summary>A leaf row: activating it runs its MenuItem (a disabled submenu stays a leaf).</summary>
    Item = 0,

    /// <summary>A MenuFlyoutSubItem header: the shell opens its nested rows; it activates nothing.</summary>
    Submenu = 1,

    /// <summary>A MenuBarItem title: the shell groups the bar's rows under it; it activates nothing.</summary>
    Bar = 2,
}

/// <summary>One row of the structured menu table published for the current page.</summary>
/// <param name="Index">Position in the table (the value the shell sends back on a tap).</param>
/// <param name="Text">Item/header text shown by the shell.</param>
/// <param name="IsEnabled">False for disabled rows (the shell greys them out and blocks them).</param>
/// <param name="Level">0 for a bar row, 1 for a direct child of a bar, 2+ for a nested submenu row.</param>
/// <param name="Item">The MAUI item activated when a leaf is tapped (null for bar/submenu rows).</param>
/// <param name="Kind">Item/submenu header/bar title (see <see cref="OpenHarmonyMenuEntryKind"/>).</param>
/// <param name="BarTitle">Text of the MenuBarItem the row belongs to.</param>
public sealed record OpenHarmonyMenuEntry(int Index, string Text, bool IsEnabled, int Level, MenuItem? Item,
    OpenHarmonyMenuEntryKind Kind = OpenHarmonyMenuEntryKind.Item, string? BarTitle = null);

public static partial class OpenHarmonyMenus
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Rows of the last built table (index order), observable off-device.</summary>
    public static IReadOnlyList<OpenHarmonyMenuEntry> Items => s_items;

    /// <summary>Rows handed to the host by the last publish (0 when the host is unavailable).</summary>
    public static int LastPublishedCount { get; private set; }

    /// <summary>True when the last change would have talked to the host (observable off-device).</summary>
    public static bool WouldPublish { get; private set; }

    /// <summary>False once a native call failed (no libopenharmonyhost.so).</summary>
    public static bool IsAvailable => s_available;

    /// <summary>Refreshes skipped because the table did not change.</summary>
    public static int RefreshesSkipped { get; private set; }

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_begin")]
    private static partial int MenuBegin(int count);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_item", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MenuItemNative(int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int enabled);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_item_ex", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MenuItemExNative(int index, string text, int enabled, int kind, int level,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string barTitle);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_commit")]
    private static partial int MenuCommit();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_set_listener")]
    private static partial void MenuSetListener(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MenuActionCallback(int index);

    private static readonly List<OpenHarmonyMenuEntry> s_items = new();
    private static readonly List<OpenHarmonyMenuEntry> s_next = new();
    // The rows in host index space: the structured table, or the legacy flat table when the host
    // has no ohos_host_menu_item_ex. Activate() indexes this list, so both publishes route taps
    // to the item the host actually showed.
    private static readonly List<OpenHarmonyMenuEntry> s_published = new();
    private static unsafe IntPtr s_actionThunk = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnMenuActionNative;
    private static bool s_available = true;
    private static bool? s_extendedTable;
    private static bool s_listenerRegistered;
    private static Page? s_lastHostPage;

    /// <summary>
    /// Rebuilds the table for the page visible under <paramref name="root"/> and publishes it
    /// to the host when it changed. Pass any view of the tree: navigation containers are
    /// unwrapped to their current page.
    /// </summary>
    public static bool Refresh(IView? root) => Publish(CurrentPage(root));

    /// <summary>
    /// Refreshes from the running app host (the window's current page) when the page changed;
    /// this is the automatic hook the module initializer installs.
    /// </summary>
    public static bool RefreshFromHost()
    {
        try
        {
            Page? page = ResolveHostPage();
            if (page is null || ReferenceEquals(page, s_lastHostPage))
            {
                return false;
            }
            s_lastHostPage = page;
            return Publish(page);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Current page under a view: unwraps navigation, tabbed, flyout and shell pages.</summary>
    public static Page? CurrentPage(IView? root)
    {
        Page? page = root as Page;
        for (int step = 0; step < 16 && page is not null; step++)
        {
            Page? next = page switch
            {
                NavigationPage navigation => navigation.CurrentPage,
                TabbedPage tabbed => tabbed.CurrentPage,
                FlyoutPage flyout => flyout.Detail,
                Shell shell => shell.CurrentPage,
                _ => null,
            };
            if (next is null || ReferenceEquals(next, page))
            {
                break;
            }
            page = next;
        }
        return page;
    }

    /// <summary>Activates the published row (used by the shell's notifyMenuAction route).</summary>
    public static bool Activate(int index)
    {
        if (index < 0 || index >= s_published.Count)
        {
            return false;
        }
        OpenHarmonyMenuEntry entry = s_published[index];
        // Only a leaf item activates: a bar/submenu header opens its rows in the shell instead.
        if (entry.Kind != OpenHarmonyMenuEntryKind.Item || !entry.IsEnabled || entry.Item is not { } item)
        {
            return false;
        }
        if (!item.IsEnabled)
        {
            return false;
        }
        if (item is IMenuItemController controller)
        {
            controller.Activate();
            return true;
        }
        if (item.Command is ICommand command && command.CanExecute(item.CommandParameter))
        {
            command.Execute(item.CommandParameter);
            return true;
        }
        return false;
    }

    /// <summary>Activation route used by the native callback (host.notifyMenuAction).</summary>
    internal static bool OnMenuAction(int index) => Activate(index);

    // A reverse P/Invoke entry (host.notifyMenuAction): Activate runs the app's menu
    // controller/command, so an exception must not unwind into the native frame (MB-2).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnMenuActionNative(int index)
    {
        try
        {
            Activate(index);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("menu action", ex);
        }
    }

    /// <summary>
    /// Builds the structured table for a page and hands it to the host when it differs from the
    /// last one. Native failures degrade to the managed snapshot only; a host without the
    /// extended row export gets the legacy flat table.
    /// </summary>
    public static bool Publish(Page? page)
    {
        BuildTable(page, s_next);
        if (SameTable(s_next, s_items))
        {
            RefreshesSkipped++;
            LastPublishedCount = 0;
            return false;
        }
        s_items.Clear();
        s_items.AddRange(s_next);
        // The activation map is rebuilt even without a host library: it is the index space the
        // shell would have shown (structured, or the legacy flat table once the probe failed),
        // so OnMenuAction routes taps against the same rows off-device.
        RebuildActivationMap();
        WouldPublish = s_available;
        LastPublishedCount = 0;
        if (!s_available)
        {
            return false;
        }
        if (s_extendedTable != false)
        {
            try
            {
                PublishExtended();
                s_extendedTable = true;
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                // The host predates ohos_host_menu_item_ex: republish as the legacy flat table.
                s_extendedTable = false;
                RebuildActivationMap();
            }
            catch (DllNotFoundException)
            {
                s_available = false;
                return false;
            }
        }
        try
        {
            PublishLegacy();
            return LastPublishedCount > 0;
        }
        catch (DllNotFoundException)
        {
            s_available = false;
        }
        catch (EntryPointNotFoundException)
        {
            s_available = false;
        }
        return false;
    }

    /// <summary>
    /// Rebuilds the activation map (the rows in host index space). The structured mode keeps the
    /// full table; the legacy mode filters to the leaf rows the three-argument publish sends,
    /// renumbering them to the legacy index space.
    /// </summary>
    private static void RebuildActivationMap()
    {
        s_published.Clear();
        if (s_extendedTable == false)
        {
            foreach (OpenHarmonyMenuEntry entry in s_items)
            {
                if (entry.Kind == OpenHarmonyMenuEntryKind.Item && entry.Item is not null)
                {
                    s_published.Add(entry with { Index = s_published.Count });
                }
            }
            return;
        }
        s_published.AddRange(s_items);
    }

    /// <summary>Publishes the structured table through ohos_host_menu_item_ex.</summary>
    private static void PublishExtended()
    {
        EnsureListener();
        MenuBegin(s_published.Count);
        foreach (OpenHarmonyMenuEntry entry in s_published)
        {
            MenuItemExNative(entry.Index, entry.Text, entry.IsEnabled ? 1 : 0, (int)entry.Kind, entry.Level,
                entry.BarTitle ?? string.Empty);
        }
        MenuCommit();
        LastPublishedCount = s_published.Count;
    }

    /// <summary>
    /// Publishes the legacy flat table (leaf rows only, no bar/submenu headers): the pre-T16 host
    /// contract, kept so an older host still shows and activates every item.
    /// </summary>
    private static void PublishLegacy()
    {
        EnsureListener();
        MenuBegin(s_published.Count);
        foreach (OpenHarmonyMenuEntry entry in s_published)
        {
            MenuItemNative(entry.Index, entry.Text, entry.IsEnabled ? 1 : 0);
        }
        MenuCommit();
        LastPublishedCount = s_published.Count;
    }

    private static void EnsureListener()
    {
        if (s_listenerRegistered)
        {
            return;
        }
        s_listenerRegistered = true;
        MenuSetListener(s_actionThunk);
    }

    private static Page? ResolveHostPage()
    {
        if (OpenHarmonyHandlerConnector.Context?.Services.GetService(typeof(OpenHarmonyMauiAppHost))
            is not OpenHarmonyMauiAppHost host || host.Window is not Microsoft.Maui.Controls.Window window)
        {
            return null;
        }
        // A pushed modal owns the window while it is up (same rule as the renderer).
        if (window.Navigation.ModalStack.Count > 0)
        {
            return window.Navigation.ModalStack[^1];
        }
        return CurrentPage(window.Page);
    }

    private static void BuildTable(Page? page, List<OpenHarmonyMenuEntry> table)
    {
        table.Clear();
        if (page is null)
        {
            return;
        }
        foreach (MenuBarItem bar in page.MenuBarItems)
        {
            if (bar is null)
            {
                continue;
            }
            // The bar row is added first (the shell's group header) and dropped again when the
            // bar has no reachable row, so an empty group never reaches the shell.
            int barIndex = table.Count;
            string barTitle = bar.Text ?? string.Empty;
            table.Add(new OpenHarmonyMenuEntry(barIndex, barTitle, bar.IsEnabled, 0, null,
                OpenHarmonyMenuEntryKind.Bar, barTitle));
            foreach (IMenuElement element in bar)
            {
                AddElement(table, element, 1, bar.IsEnabled, barTitle);
            }
            if (table.Count == barIndex + 1)
            {
                table.RemoveAt(barIndex);
            }
        }
    }

    private static void AddElement(List<OpenHarmonyMenuEntry> table, IMenuElement element, int level,
        bool barEnabled, string barTitle)
    {
        // Order matters: MenuFlyoutSubItem and MenuFlyoutSeparator derive from MenuFlyoutItem.
        if (element is MenuFlyoutSubItem subItem)
        {
            if (!subItem.IsEnabled)
            {
                // A disabled submenu cannot be opened, so it is published as a disabled leaf and
                // its children are not reachable either.
                table.Add(new OpenHarmonyMenuEntry(table.Count, subItem.Text ?? string.Empty, false, level, null,
                    OpenHarmonyMenuEntryKind.Item, barTitle));
                return;
            }
            int headerIndex = table.Count;
            table.Add(new OpenHarmonyMenuEntry(headerIndex, subItem.Text ?? string.Empty, true, level, null,
                OpenHarmonyMenuEntryKind.Submenu, barTitle));
            foreach (IMenuElement child in subItem)
            {
                AddElement(table, child, level + 1, barEnabled, barTitle);
            }
            if (table.Count == headerIndex + 1)
            {
                // An empty submenu has nothing to open: keep the label as a plain row.
                table[headerIndex] = table[headerIndex] with { Kind = OpenHarmonyMenuEntryKind.Item };
            }
            return;
        }
        if (element is MenuFlyoutSeparator)
        {
            return;
        }
        if (element is MenuFlyoutItem item)
        {
            table.Add(new OpenHarmonyMenuEntry(table.Count, item.Text ?? string.Empty,
                barEnabled && item.IsEnabled, level, item, OpenHarmonyMenuEntryKind.Item, barTitle));
        }
    }

    private static bool SameTable(List<OpenHarmonyMenuEntry> left, List<OpenHarmonyMenuEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Text, right[i].Text, StringComparison.Ordinal)
                || left[i].IsEnabled != right[i].IsEnabled
                || left[i].Level != right[i].Level
                || left[i].Kind != right[i].Kind
                || !string.Equals(left[i].BarTitle, right[i].BarTitle, StringComparison.Ordinal)
                || !ReferenceEquals(left[i].Item, right[i].Item))
            {
                return false;
            }
        }
        return true;
    }

    private static void OnHostFrame(OpenHarmonyFrameEventArgs _)
    {
        // The per-frame path only looks for a page change (cheap); redraws rebuild the table.
        RefreshFromHost();
    }

    private static void OnHostRedraw()
    {
        // A redraw can also follow a MenuBarItems change on the same page, so rebuild always.
        try
        {
            Page? page = ResolveHostPage();
            s_lastHostPage = page;
            Publish(page);
        }
        catch (Exception)
        {
            // A redraw must never surface a menus fault.
        }
    }

    /// <summary>
    /// Self-wiring: every platform redraw (page changes raise one) and every frame tick checks
    /// whether the window's current page changed and republishes its menu table.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            OpenHarmonyBridge.Frame += OnHostFrame;
            OpenHarmonyBridge.RedrawRequested += OnHostRedraw;
        }
        catch (Exception)
        {
            // No platform events available: menus can still be published explicitly.
        }
    }
}
