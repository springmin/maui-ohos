// Desktop menus (MenuBarItem / MenuFlyout) for OpenHarmony.
//
// The compositor draws custom pixels, so there is no ArkUI component tree that could own a
// MenuBarItem: the current page's menu bars are published to the native host as a flat table
// (ohos_host_menu_begin/item/commit) which the ArkTS shell reads back and renders with an
// ArkUI bindMenu. A tap in the shell calls host.notifyMenuAction(index), the host invokes the
// callback registered here, and the matching MenuFlyoutItem is activated through
// IMenuItemController.Activate() - the same activation path as the navigation bar's toolbar
// items, so Clicked and Command both fire.
//
// Shape of the table (index space shared with the shell):
//   - every MenuBarItem's items are published in declaration order, bar by bar, so the shell
//     shows one flat menu (the bar titles are not transmitted: the host table has no column
//     for them).
//   - MenuFlyoutSeparators are skipped (they are not actionable).
//   - a MenuFlyoutSubItem is flattened: its children are published right after it, keeping
//     declaration order, and the subitem header itself is not published (tapping it could not
//     open a real submenu). Depth is tracked in the managed snapshot for diagnostics/tests,
//     but the host table itself carries text/enabled only.
//   - an item is enabled only when both its MenuBarItem and the item itself are enabled.
//
// The refresh is self-wired: a module initializer listens to the platform's redraw requests
// (raised by the navigation/tab/flyout/shell handlers on page changes) and to frame ticks, then
// resolves the window's current page through the app host and republishes when the table
// changed. Every native call is guarded: with no libopenharmonyhost.so (desktop tests) the
// managed snapshot still updates and the native path degrades silently.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>One row of the flat menu table published for the current page.</summary>
/// <param name="Index">Position in the table (the value the shell sends back on a tap).</param>
/// <param name="Text">Item text shown by the shell.</param>
/// <param name="IsEnabled">False for disabled items (the shell still shows them, greyed out).</param>
/// <param name="Depth">0 for an item of a MenuBarItem, 1+ for a flattened MenuFlyoutSubItem child.</param>
/// <param name="Item">The MAUI item activated when the row is tapped.</param>
public sealed record OpenHarmonyMenuEntry(int Index, string Text, bool IsEnabled, int Depth, MenuItem Item);

public static partial class OpenHarmonyMenus
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Rows of the last published table (index order), observable off-device.</summary>
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

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_commit")]
    private static partial int MenuCommit();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_menu_set_listener")]
    private static partial void MenuSetListener(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MenuActionCallback(int index);

    private static readonly List<OpenHarmonyMenuEntry> s_items = new();
    private static readonly List<OpenHarmonyMenuEntry> s_next = new();
    private static unsafe IntPtr s_actionThunk = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnMenuActionNative;
    private static bool s_available = true;
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
        if (index < 0 || index >= s_items.Count)
        {
            return false;
        }
        OpenHarmonyMenuEntry entry = s_items[index];
        if (!entry.IsEnabled || !entry.Item.IsEnabled)
        {
            return false;
        }
        if (entry.Item is IMenuItemController controller)
        {
            controller.Activate();
            return true;
        }
        if (entry.Item.Command is ICommand command && command.CanExecute(entry.Item.CommandParameter))
        {
            command.Execute(entry.Item.CommandParameter);
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
    /// Builds the flat table for a page and hands it to the host when it differs from the
    /// last one. Native failures degrade to the managed snapshot only.
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
        WouldPublish = s_available;
        LastPublishedCount = 0;
        if (!s_available)
        {
            return false;
        }
        try
        {
            EnsureListener();
            MenuBegin(s_items.Count);
            foreach (OpenHarmonyMenuEntry entry in s_items)
            {
                MenuItemNative(entry.Index, entry.Text, entry.IsEnabled ? 1 : 0);
            }
            MenuCommit();
            LastPublishedCount = s_items.Count;
            return true;
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
            foreach (IMenuElement element in bar)
            {
                AddElement(table, element, 0, bar.IsEnabled);
            }
        }
    }

    private static void AddElement(List<OpenHarmonyMenuEntry> table, IMenuElement element, int depth, bool barEnabled)
    {
        // Order matters: MenuFlyoutSubItem and MenuFlyoutSeparator derive from MenuFlyoutItem.
        if (element is MenuFlyoutSubItem subItem)
        {
            if (!subItem.IsEnabled)
            {
                // A disabled submenu cannot be opened, so its children are not reachable either.
                return;
            }
            foreach (IMenuElement child in subItem)
            {
                AddElement(table, child, depth + 1, barEnabled);
            }
            return;
        }
        if (element is MenuFlyoutSeparator)
        {
            return;
        }
        if (element is MenuFlyoutItem item)
        {
            table.Add(new OpenHarmonyMenuEntry(
                table.Count, item.Text ?? string.Empty, barEnabled && item.IsEnabled, depth, item));
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
                || left[i].Depth != right[i].Depth
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
