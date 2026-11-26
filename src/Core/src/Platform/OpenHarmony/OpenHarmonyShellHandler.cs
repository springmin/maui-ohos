// Shell handler for OpenHarmony: renders Shell.CurrentPage with a bottom bar for the shell
// items. Shell's own navigation manager (GoToAsync/routes) runs in managed code; this handler
// only has to show the current page and let the bar switch items.
//
// Shell extras (audit: absent) and where they land:
//   * TabBarIsVisible maps onto the existing OpenHarmonyView.TabTitlesVisible; the effective
//     value is resolved from the current page and its ancestors, then the shell (the attached
//     property is usually set on a page, not on the shell).
//   * FlyoutBehavior maps onto OpenHarmonyView.ShowsHamburger / FlyoutOpen: Disabled hides and
//     closes the drawer, Locked keeps it open (the renderer's chrome refresh re-asserts it on
//     every draw, so a dismiss tap cannot leave a locked shell closed).
//   * FlyoutHeader/FlyoutFooter: a string or Label header/footer is published as the first/last
//     row of the compositor flyout panel (FlyoutItems, non-selectable - SelectFlyoutRow offsets
//     the selection so item taps still land on the right shell item) and as text through the
//     ArkTS section bridge (OpenHarmonyShellExtras). A rich View or a DataTemplate section is
//     materialized as a real row of the panel (OpenHarmonyShellFlyout): Shell's own resolved
//     FlyoutHeaderView/FlyoutFooterView is measured/arranged into the drawer and the renderer
//     draws and hit-tests it. The item rows come from MAUI's canonical flyout model
//     (IShellController.GenerateFlyoutGrouping): implicit items flatten, AsMultipleItems expands
//     into one row per child, the current content's MenuItems append after it, and a row selects
//     its own element when tapped. Shell.ItemTemplate materializes item rows, Shell.MenuItemTemplate
//     menu rows, and FlyoutContent/FlyoutContentTemplate replaces the item rows with the resolved
//     content view between the header and the footer; a row's own content handles a touch first,
//     the row selection is the fallback.
//   * SearchHandler: attach/detach, query, placeholder and visibility are tracked per current
//     page (and per shell) in OpenHarmonyShellExtras and published through
//     ohos_host_shell_search_set; the shell's notifyShellSearch edits return through the
//     listener (op 0 query / 1 submit / 2 cancel, registered with
//     ohos_host_shell_search_set_listener).
using System.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyShellHandler : OpenHarmonyViewHandler<Shell>
{
    public static readonly IPropertyMapper<Shell, OpenHarmonyShellHandler> Mapper =
        new PropertyMapper<Shell, OpenHarmonyShellHandler>(ViewMapper)
        {
            [nameof(Shell.CurrentItem)] = MapShell,
            [nameof(Shell.CurrentPage)] = MapShell,
            // The shell extras the compositor can express. Attached-property names are the same
            // strings BindableObject raises in PropertyChanged, so the mapper stays in sync with
            // the subscriptions below. Each extra maps only its own surface: MAUI calls
            // UpdateValue("FlyoutBehavior") from Shell.NotifyFlyoutBehaviorObservers during
            // navigation, and a full chrome sync there would move the back/title state ahead of
            // the renderer's own ChromeRefresh.
            [nameof(Shell.FlyoutBehavior)] = MapFlyoutBehavior,
            [nameof(Shell.FlyoutHeader)] = MapFlyoutSections,
            [nameof(Shell.FlyoutFooter)] = MapFlyoutSections,
            [nameof(Shell.FlyoutHeaderTemplate)] = MapFlyoutSections,
            [nameof(Shell.FlyoutFooterTemplate)] = MapFlyoutSections,
            // T14 extras: a menu template, a FlyoutContent body and an ItemTemplate swap all
            // re-materialize the drawer rows (as do the content/template pairs above).
            [nameof(Shell.ItemTemplate)] = MapFlyoutSections,
            [nameof(Shell.MenuItemTemplate)] = MapFlyoutSections,
            [nameof(Shell.FlyoutContent)] = MapFlyoutSections,
            [nameof(Shell.FlyoutContentTemplate)] = MapFlyoutSections,
            ["TabBarIsVisible"] = MapTabBar,
            ["SearchHandler"] = MapSearch,
        };

    public OpenHarmonyShellHandler() : base(Mapper) { }

    private SearchHandler? _searchHandler;
    private Microsoft.Maui.Controls.Page? _observedPage;
    private OpenHarmonyShellChrome? _chrome;
    private OpenHarmonyShellFlyout? _flyout;

    /// <summary>
    /// The chrome pieces the compositor can express beyond the title string (a text-producing
    /// TitleView and the current page's toolbar items); created on first use because it needs
    /// the platform view.
    /// </summary>
    private OpenHarmonyShellChrome Chrome =>
        _chrome ??= new OpenHarmonyShellChrome(PlatformView, () => OpenHarmonyBridge.RequestRedraw());

    /// <summary>Rich drawer sections/rows; created on first use (needs the platform view).</summary>
    private OpenHarmonyShellFlyout Flyout => _flyout ??= new OpenHarmonyShellFlyout(PlatformView);

    /// <summary>Header text rows in FlyoutItems; the flyout selection subtracts them.</summary>
    private int _flyoutLeadingRows;

    /// <summary>Section stack depth at the last Navigated, so a push/pop is told from a tab switch.</summary>
    private int _lastStackDepth = 1;

    protected override OpenHarmonyView CreatePlatformView()
    {
        // The bottom bar reuses the tabbed page chrome (titles + selection).
        var view = new OpenHarmonyView { IsTabbedPage = true, Background = Colors.Black, TextColor = Colors.White, FontSize = 24 };
        view.TabSelected = index =>
        {
            if (VirtualView is { } shell && index >= 0 && index < shell.Items.Count)
            {
                shell.CurrentItem = shell.Items[index];
            }
        };
        view.ChromeRefresh = () => MapShell(this, VirtualView!);
        view.BackTapped = () =>
        {
            if (VirtualView is { } shell)
            {
                // The shell section owns the navigation stack.
                var navigation = shell.CurrentItem?.CurrentItem?.Navigation ?? shell.Navigation;
                _ = navigation.PopAsync();
            }
        };
        view.FlyoutRequested = () =>
        {
            view.FlyoutOpen = true;
            OpenHarmonyBridge.RequestRedraw();
        };
        view.FlyoutSelect = SelectFlyoutRow;
        return view;
    }

    /// <summary>
    /// A tap on a compositor flyout row. A rich row selects the element it materialized (a menu
    /// row activates, a shell element switches the item/section/content chain); the flat text
    /// layout keeps the index -> shell item mapping (header rows offset the index). Out-of-range
    /// rows just close the drawer.
    /// </summary>
    private void SelectFlyoutRow(int row)
    {
        OpenHarmonyView view = PlatformView;
        view.FlyoutOpen = false;
        OpenHarmonyFlyoutRow? rich = null;
        if (view.FlyoutRows.Count > 0)
        {
            foreach (OpenHarmonyFlyoutRow candidate in view.FlyoutRows)
            {
                if (candidate.PanelIndex == row)
                {
                    rich = candidate;
                    break;
                }
            }
        }
        if (rich?.Target is { } target)
        {
            SelectFlyoutElement(target);
        }
        else if (rich is null)
        {
            int index = row - _flyoutLeadingRows;
            if (VirtualView is { } shell && index >= 0 && index < shell.Items.Count)
            {
                shell.CurrentItem = shell.Items[index];
            }
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>
    /// Selects a materialized flyout element: a menu item runs its own activation (Clicked/
    /// Command, gated by IsEnabled), a ShellContent selects its section and item, a ShellSection
    /// selects its item, a ShellItem selects itself. The ShellContent case is what makes an
    /// AsMultipleItems row land exactly on the child the row stands for.
    /// </summary>
    private static void SelectFlyoutElement(object target)
    {
        switch (target)
        {
            case IMenuItemController menu:
                menu.Activate();
                break;
            case ShellContent content:
                if (content.Parent is ShellSection contentSection)
                {
                    contentSection.CurrentItem = content;
                    SelectFlyoutElement(contentSection);
                }
                break;
            case ShellSection section:
                if (section.Parent is ShellItem owner)
                {
                    owner.CurrentItem = section;
                    SelectFlyoutElement(owner);
                }
                break;
            case ShellItem item:
                if (item.Parent is Shell shell)
                {
                    shell.CurrentItem = item;
                }
                break;
        }
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } shell)
        {
            shell.PropertyChanged += OnShellPropertyChanged;
            shell.Navigating += OnShellNavigating;
            shell.Navigated += OnShellNavigated;
            _lastStackDepth = CurrentStackDepth(shell);
        }
        // FIX-BACKSIZE: the system Back press closes an open shell drawer (see OnBackPressed).
        OpenHarmonyBridge.BackPressed += OnBackPressed;
        MapShell(this, VirtualView!);
        if (VirtualView is { } connectedShell)
        {
            UpdateSearchHandler(connectedShell);
        }
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyBridge.BackPressed -= OnBackPressed;
        if (VirtualView is { } shell)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            shell.Navigating -= OnShellNavigating;
            shell.Navigated -= OnShellNavigated;
        }
        DetachSearchHandler();
        _chrome?.Detach();
        _flyout?.Detach();
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// System Back (hosting bridge event): closes an open shell drawer and writes the managed
    /// <see cref="Shell.FlyoutIsPresented"/> back (the compositor's FlyoutOpen is platform state
    /// and both move together), consuming the press. A Locked drawer is re-pinned by every chrome
    /// refresh, so it is left to the system default; a closed drawer reports false.
    /// </summary>
    private bool OnBackPressed()
    {
        if (VirtualView is { } shell && PlatformView.FlyoutOpen && shell.FlyoutBehavior != FlyoutBehavior.Locked)
        {
            PlatformView.FlyoutOpen = false;
            shell.FlyoutIsPresented = false;
            OpenHarmonyBridge.RequestRedraw();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Captures "shared:" elements of the page that is about to leave the draw tree, before the
    /// navigation commit replaces the shell's CurrentPage.
    /// </summary>
    private void OnShellNavigating(object? sender, ShellNavigatingEventArgs args)
        => OpenHarmonySharedTransition.Capture(VirtualView?.CurrentPage);

    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs args)
    {
        MapShell(this, VirtualView!);
        UpdateSearchHandler(VirtualView!);
        ArrangeContent();
        OpenHarmonyBridge.RequestRedraw();
        // Push/pop (the stack depth moved) gets the enter pass; shell item/section/content
        // switches keep the instant commit. The depth is read before the trackers update.
        if (VirtualView is { } shell)
        {
            int depth = CurrentStackDepth(shell);
            bool? forward = depth > _lastStackDepth ? true : depth < _lastStackDepth ? false : (bool?)null;
            _lastStackDepth = depth;
            if (forward is bool direction)
            {
                Page? current = shell.CurrentPage;
                OpenHarmonySharedTransition.Run(current);
                OpenHarmonyPageTransitions.Enter(current, direction);
            }
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (VirtualView is not { } shell)
        {
            return;
        }
        switch (args.PropertyName)
        {
            case nameof(Shell.CurrentItem):
            case nameof(Shell.CurrentPage):
                MapShell(this, shell);
                UpdateSearchHandler(shell);
                ArrangeContent();
                OpenHarmonyBridge.RequestRedraw();
                break;
            case nameof(Shell.FlyoutBehavior):
            case nameof(Shell.FlyoutHeader):
            case nameof(Shell.FlyoutFooter):
            case nameof(Shell.FlyoutHeaderTemplate):
            case nameof(Shell.FlyoutFooterTemplate):
            case nameof(Shell.ItemTemplate):
            case nameof(Shell.MenuItemTemplate):
            case nameof(Shell.FlyoutContent):
            case nameof(Shell.FlyoutContentTemplate):
            case "TabBarIsVisible":
            case "SearchHandler":
            case "TitleView":
                // Chrome-only changes: the title/back state did not move, but MapShell rebuilds
                // the flyout rows, re-asserts Disabled/Locked and re-reads the title view.
                MapShell(this, shell);
                UpdateSearchHandler(shell);
                if (args.PropertyName is "TabBarIsVisible")
                {
                    ArrangeContent();
                }
                OpenHarmonyBridge.RequestRedraw();
                break;
        }
    }

    /// <summary>
    /// Property changes on the visible page: the tab bar and the search handler are usually set
    /// through the attached properties on a page, so the shell's own PropertyChanged never fires
    /// for them.
    /// </summary>
    private void OnCurrentPagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (VirtualView is not { } shell)
        {
            return;
        }
        switch (args.PropertyName)
        {
            case "SearchHandler":
                UpdateSearchHandler(shell);
                break;
            case "TabBarIsVisible":
                MapShell(this, shell);
                ArrangeContent();
                OpenHarmonyBridge.RequestRedraw();
                break;
            case "TitleView":
                // The attached property is usually set on the page; MapShell re-reads it.
                MapShell(this, shell);
                OpenHarmonyBridge.RequestRedraw();
                break;
        }
    }

    /// <summary>Re-resolves the search handler of the current page (falling back to the shell).</summary>
    private void UpdateSearchHandler(Shell shell)
    {
        Microsoft.Maui.Controls.Page? page = shell.CurrentPage;
        if (!ReferenceEquals(page, _observedPage))
        {
            if (_observedPage is not null)
            {
                _observedPage.PropertyChanged -= OnCurrentPagePropertyChanged;
            }
            _observedPage = page;
            if (page is not null)
            {
                page.PropertyChanged += OnCurrentPagePropertyChanged;
            }
        }
        // The attached property is set on the page that owns the search box; the shell itself is
        // the second place MAUI accepts it.
        SearchHandler? handler = page is not null ? Shell.GetSearchHandler(page) : null;
        handler ??= Shell.GetSearchHandler(shell);
        AttachSearchHandler(handler, page);
    }

    private void AttachSearchHandler(SearchHandler? handler, Microsoft.Maui.Controls.Page? page)
    {
        if (!ReferenceEquals(handler, _searchHandler))
        {
            if (_searchHandler is not null)
            {
                _searchHandler.PropertyChanged -= OnSearchHandlerPropertyChanged;
            }
            _searchHandler = handler;
            if (handler is not null)
            {
                handler.PropertyChanged += OnSearchHandlerPropertyChanged;
            }
        }
        OpenHarmonyShellExtras.UpdateSearch(handler, page?.Title);
    }

    private void OnSearchHandlerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (VirtualView is { } shell)
        {
            // Query/Placeholder/IsSearchEnabled changes: refresh the state a shell bridge reads.
            OpenHarmonyShellExtras.UpdateSearch(_searchHandler, shell.CurrentPage?.Title);
        }
    }

    private void DetachSearchHandler()
    {
        if (_searchHandler is not null)
        {
            _searchHandler.PropertyChanged -= OnSearchHandlerPropertyChanged;
            _searchHandler = null;
        }
        if (_observedPage is not null)
        {
            _observedPage.PropertyChanged -= OnCurrentPagePropertyChanged;
            _observedPage = null;
        }
        OpenHarmonyShellExtras.UpdateSearch(null, null);
    }

    public static void MapShell(OpenHarmonyShellHandler handler, Shell shell)
    {
        OpenHarmonyView view = handler.PlatformView;
        view.TabTitles.Clear();
        foreach (ShellItem item in shell.Items)
        {
            view.TabTitles.Add(item.Title ?? string.Empty);
        }
        int index = shell.CurrentItem is { } current ? shell.Items.IndexOf(current) : -1;
        view.SelectedTab = Math.Max(0, index);
        // Chrome: title bar with the current page's title (or its Label TitleView) and a back
        // button when the shell can navigate back.
        view.ShowsTitleBar = shell.GetValue(Microsoft.Maui.Controls.Shell.NavBarIsVisibleProperty) is not bool navBar || navBar;
        handler.Chrome.Apply(shell);
        view.ShowsBack = CurrentStackDepth(shell) > 1;
        if (view.ShowsBack)
        {
            view.ShowsHamburger = false;
        }
        // Flyout (hamburger + drawer) shows the shell items.
        ApplyFlyoutBehavior(view, shell);
        // Flyout rows: an optional plain-text header first, then one row per shell item, then an
        // optional plain-text footer. Rich header/footer views and Shell.ItemTemplate item rows
        // are materialized into the panel by SyncFlyoutPanel (see OpenHarmonyShellFlyout).
        BuildFlyoutRows(handler, view, shell);
        handler.SyncFlyoutPanel(shell);
        view.TabIcons.Clear();
        foreach (ShellItem item in shell.Items)
        {
            view.TabIcons.Add(ResolveIcon(item.Icon));
        }
        // Shell contents are created lazily: realise the current one so it can be rendered.
        if (shell.CurrentPage is null &&
            shell.CurrentItem?.CurrentItem is ShellSection { CurrentItem: ShellContent content } &&
            content is IShellContentController controller)
        {
            controller.GetOrCreateContent();
        }
        if (shell.CurrentPage is IView page)
        {
            OpenHarmonyHandlerConnector.ConnectTree(page);
        }
    }

    /// <summary>
    /// FlyoutBehavior without a full chrome sync: Disabled hides the hamburger and closes the
    /// drawer; Locked pins the drawer open (the chrome refresh re-asserts it on every draw, so a
    /// dismiss tap cannot leave a locked shell closed). MAUI calls UpdateValue("FlyoutBehavior")
    /// from Shell.NotifyFlyoutBehaviorObservers during navigation, so this must not move the
    /// title/back state the renderer's ChromeRefresh owns.
    /// </summary>
    public static void MapFlyoutBehavior(OpenHarmonyShellHandler handler, Shell shell)
        => ApplyFlyoutBehavior(handler.PlatformView, shell);

    private static void ApplyFlyoutBehavior(OpenHarmonyView view, Shell shell)
    {
        view.ShowsHamburger = shell.FlyoutBehavior != FlyoutBehavior.Disabled;
        if (shell.FlyoutBehavior == FlyoutBehavior.Disabled)
        {
            view.FlyoutOpen = false;
        }
        else if (shell.FlyoutBehavior == FlyoutBehavior.Locked)
        {
            view.FlyoutOpen = true;
        }
    }

    /// <summary>Header/footer rows of the compositor flyout panel (titles/rows only).</summary>
    public static void MapFlyoutSections(OpenHarmonyShellHandler handler, Shell shell)
    {
        BuildFlyoutRows(handler, handler.PlatformView, shell);
        handler.SyncFlyoutPanel(shell);
    }

    /// <summary>
    /// Re-materializes/re-arranges the rich drawer rows. Runs on the drawer's draws (the panel
    /// only exists while open) and after layout: a closed drawer keeps its last rows.
    /// </summary>
    private void SyncFlyoutPanel(Shell shell)
    {
        // The leading non-selectable rows are the flyout section rows: the handler's row -> item
        // mapping (SelectFlyoutRow) and the panel's hit-testing share the same row list.
        Flyout.Sync(shell,
            OpenHarmonyShellExtras.SectionText(shell.FlyoutHeader),
            OpenHarmonyShellExtras.SectionText(shell.FlyoutFooter));
        if (Flyout.IsRich)
        {
            _flyoutLeadingRows = PlatformView.FlyoutRows.Count > 0 && PlatformView.FlyoutRows[0].ItemIndex < 0 ? 1 : 0;
        }
    }

    private static void BuildFlyoutRows(OpenHarmonyShellHandler handler, OpenHarmonyView view, Shell shell)
    {
        string? header = OpenHarmonyShellExtras.SectionText(shell.FlyoutHeader);
        string? footer = OpenHarmonyShellExtras.SectionText(shell.FlyoutFooter);
        OpenHarmonyShellExtras.SetFlyoutSections(header, footer);
        view.FlyoutItems.Clear();
        handler._flyoutLeadingRows = 0;
        if (!string.IsNullOrEmpty(header))
        {
            view.FlyoutItems.Add(header);
            handler._flyoutLeadingRows = 1;
        }
        foreach (ShellItem item in shell.Items)
        {
            view.FlyoutItems.Add(item.Title ?? string.Empty);
        }
        if (!string.IsNullOrEmpty(footer))
        {
            view.FlyoutItems.Add(footer);
        }
    }

    /// <summary>Effective Shell.TabBarIsVisible plus the content re-arrange it implies.</summary>
    public static void MapTabBar(OpenHarmonyShellHandler handler, Shell shell)
    {
        handler.PlatformView.TabTitlesVisible = ResolveTabBarVisible(shell);
        handler.ArrangeContent();
    }

    /// <summary>Attached Shell.SearchHandler changes (the shell's own attached property).</summary>
    public static void MapSearch(OpenHarmonyShellHandler handler, Shell shell)
        => handler.UpdateSearchHandler(shell);

    /// <summary>Loads a file-based tab icon; other source kinds need the image service.</summary>
    private static byte[]? ResolveIcon(Microsoft.Maui.Controls.ImageSource? icon)
    {
        try
        {
            if (icon is Microsoft.Maui.Controls.FileImageSource file && !string.IsNullOrEmpty(file.File) && File.Exists(file.File))
            {
                return File.ReadAllBytes(file.File);
            }
        }
        catch
        {
            // Icons are optional.
        }
        return null;
    }

    private static int CurrentStackDepth(Shell shell)
        => shell.CurrentItem?.CurrentItem?.Navigation?.NavigationStack?.Count
           ?? shell.CurrentPage?.Navigation?.NavigationStack?.Count
           ?? 1;

    /// <summary>
    /// Effective Shell.TabBarIsVisible: the nearest explicit value on the current page or one of
    /// its ancestors, then the shell; visible when nobody set it.
    /// </summary>
    private static bool ResolveTabBarVisible(Shell shell)
    {
        for (Element? element = shell.CurrentPage; element is not null; element = element.Parent)
        {
            if (element.IsSet(Shell.TabBarIsVisibleProperty))
            {
                return (bool)element.GetValue(Shell.TabBarIsVisibleProperty);
            }
        }
        if (shell.IsSet(Shell.TabBarIsVisibleProperty))
        {
            return (bool)shell.GetValue(Shell.TabBarIsVisibleProperty);
        }
        return true;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double height = element?.HeightRequest > 0
            ? element.HeightRequest
            : (double.IsFinite(heightConstraint) ? heightConstraint : 600);
        return new Size(widthConstraint, height);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        ArrangeContent();
    }

    private void ArrangeContent()
    {
        if (VirtualView?.CurrentPage is not IView content)
        {
            return;
        }
        Rect frame = PlatformView.Frame;
        OpenHarmonyHandlerConnector.ConnectTree(content);
        bool tabBar = ResolveTabBarVisible(VirtualView!);
        PlatformView.TabTitlesVisible = tabBar;
        double top = PlatformView.ShowsTitleBar ? OpenHarmonyView.TitleBarHeight : 0;
        double bottom = tabBar ? OpenHarmonyView.TabBarHeight : 0;
        var contentFrame = new Rect(frame.X, frame.Y + top, frame.Width,
            Math.Max(0, frame.Height - bottom - top));
        content.Measure(contentFrame.Width, contentFrame.Height);
        content.Arrange(contentFrame);
        // The drawer follows the window: re-arrange its rich rows with the new panel geometry
        // (no-op while the drawer is closed or the panel is text-only).
        SyncFlyoutPanel(VirtualView!);
    }
}
