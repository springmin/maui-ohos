// Shell chrome the compositor can express beyond the title string: the TitleView and the
// current page's ToolbarItems.
//
// TitleView (the Shell.TitleView attached property, a View): a visible Label publishes its
// Text as the bar title (the compositor bar's fast text path). Any other visible view (T15:
// Image, SearchBar, Button, a layout of runs) is materialized as a real row: the chrome
// connects the view's handlers, measures it into the band between the leading back slot and
// the trailing toolbar slot, arranges it there and hands it to OpenHarmonyView.ShellTitleViewRow;
// the renderer draws it over the bar (its own content replaces the title text) and hit-tests it,
// so a button/entry/gesture inside the title view works. An absent or hidden title view is
// treated as absent: the page (then shell item) title stays in the bar. The value is re-read on
// every chrome sync, and the shell handler calls that sync from the renderer's per-draw
// ChromeRefresh, so a change is picked up on the next frame.
//
// ToolbarItems: the current page's collection is mirrored into OpenHarmonyView.ToolbarItems the
// same way OpenHarmonyNavigationPageHandler mirrors it for NavigationPage - text, icon
// (file bytes or FontImageSource glyph), Order/Priority and IsEnabled (OpenHarmonyToolbarMirror),
// with a tap activating the item (IMenuItemController.Activate, else the item's Command with its
// CommandParameter). The rebuild is event-driven (page swap, collection/item changes), so the
// per-draw chrome sync only compares the page reference.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Materialized rich Shell.TitleView row (T15): the resolved non-Label view and the canvas-space
/// band the chrome arranged it into. The renderer draws the row over the title bar and routes
/// touches in the band into the view. Null while the bar keeps the text title path.
/// </summary>
internal sealed class OpenHarmonyShellTitleViewRow
{
    /// <summary>The app's TitleView (handlers are connected by the chrome's sync).</summary>
    public required View View { get; init; }

    /// <summary>Canvas-space band the row was arranged into (empty before the first sync).</summary>
    public RectF Frame { get; set; }
}

internal sealed class OpenHarmonyShellChrome
{
    /// <summary>
    /// Leading/trailing slots the bar reserves for the back chevron (or hamburger) and the
    /// toolbar items; the rich title view is arranged between them, like the title text.
    /// </summary>
    internal const float TitleViewInset = 56f;

    private readonly OpenHarmonyView _view;
    private readonly Action _refresh;
    private Page? _toolbarPage;
    private OpenHarmonyShellTitleViewRow? _titleViewRow;

    public OpenHarmonyShellChrome(OpenHarmonyView view, Action refresh)
    {
        _view = view;
        _refresh = refresh;
    }

    /// <summary>Re-reads the title view and the current page's toolbar items from the shell.</summary>
    public void Apply(Shell shell)
    {
        ApplyTitleView(shell);
        UpdateToolbar(shell.CurrentPage);
    }

    /// <summary>Clears the mirrored toolbar and releases the title view row.</summary>
    public void Detach()
    {
        UnsubscribeToolbar(_toolbarPage);
        _toolbarPage = null;
        _view.ToolbarItems.Clear();
        _view.ToolbarOverflowOpen = false;
        _titleViewRow = null;
        _view.ShellTitleViewRow = null;
    }

    /// <summary>
    /// Publishes the bar title. A visible Label keeps the text path (its Text becomes the bar
    /// title, else the page title); any other visible view is materialized as the rich row
    /// (T15). An absent or hidden title view falls back to the page (then shell item) title.
    /// </summary>
    private void ApplyTitleView(Shell shell)
    {
        View? titleView = shell.CurrentPage is { } page ? Shell.GetTitleView(page) : null;
        titleView ??= Shell.GetTitleView(shell);
        // Visibility is an explicit IView implementation on the Controls base in rc.1.
        if (titleView is null || ((IView)titleView).Visibility != Visibility.Visible)
        {
            _view.TitleText = PageTitle(shell);
            ClearTitleViewRow();
            return;
        }
        if (titleView is Label label)
        {
            _view.TitleText = string.IsNullOrEmpty(label.Text) ? PageTitle(shell) : label.Text;
            ClearTitleViewRow();
            return;
        }
        // A rich title view replaces the title text with real content; the bar background, the
        // back affordance and the tab bar stay.
        _view.TitleText = string.Empty;
        ArrangeTitleView(titleView);
    }

    /// <summary>
    /// Measures and arranges the rich title view into the bar's band. Runs on every chrome
    /// sync - the band follows the shell's frame and the flow map, and a text/size change in
    /// the view must be picked up on the next frame - and reuses the row object so the
    /// per-draw path allocates nothing.
    /// </summary>
    private void ArrangeTitleView(View titleView)
    {
        RectF frame = _view.CanvasFrame;
        float width = Math.Max(1f, frame.Width - 2 * TitleViewInset);
        OpenHarmonyHandlerConnector.ConnectTree(titleView);
        titleView.Measure(width, OpenHarmonyView.TitleBarHeight);
        if (!ReferenceEquals(_titleViewRow?.View, titleView))
        {
            _titleViewRow = new OpenHarmonyShellTitleViewRow { View = titleView };
            _view.ShellTitleViewRow = _titleViewRow;
        }
        // The band is the bar height: the view's own alignment decides where its content sits
        // inside it (Fill stretches, Center centers), matching how a TitleView fills a bar.
        _titleViewRow.Frame = new RectF(frame.X + TitleViewInset, frame.Y, width, OpenHarmonyView.TitleBarHeight);
        titleView.Arrange(new Rect(_titleViewRow.Frame.X, _titleViewRow.Frame.Y,
            _titleViewRow.Frame.Width, _titleViewRow.Frame.Height));
    }

    private void ClearTitleViewRow()
    {
        if (_titleViewRow is not null)
        {
            _titleViewRow = null;
            _view.ShellTitleViewRow = null;
        }
    }

    /// <summary>The page title, then the shell item title: the bar text without a title view.</summary>
    private static string PageTitle(Shell shell)
        => shell.CurrentPage?.Title ?? shell.CurrentItem?.Title ?? string.Empty;

    /// <summary>
    /// Mirrors <paramref name="page"/>'s ToolbarItems into the platform bar (text, icon, order,
    /// enabled state and activation). The rebuild is event-driven - a page swap or a collection/
    /// item change - so the per-draw Apply only compares the page reference, like MAUI's own
    /// platform toolbars.
    /// </summary>
    private void UpdateToolbar(Page? page)
    {
        if (ReferenceEquals(_toolbarPage, page))
        {
            return;
        }
        UnsubscribeToolbar(_toolbarPage);
        _toolbarPage = page;
        if (page is not null)
        {
            if (page.ToolbarItems is System.Collections.Specialized.INotifyCollectionChanged collection)
            {
                collection.CollectionChanged += OnToolbarCollectionChanged;
            }
            foreach (ToolbarItem item in page.ToolbarItems)
            {
                item.PropertyChanged += OnToolbarItemChanged;
            }
        }
        // A page swap closes a dropdown the previous page had open.
        _view.ToolbarOverflowOpen = false;
        RebuildToolbar();
    }

    private void RebuildToolbar()
    {
        OpenHarmonyToolbarMirror.Fill(_view.ToolbarItems, _toolbarPage?.ToolbarItems);
        _refresh();
    }

    private void UnsubscribeToolbar(Page? page)
    {
        if (page is null)
        {
            return;
        }
        if (page.ToolbarItems is System.Collections.Specialized.INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnToolbarCollectionChanged;
        }
        foreach (ToolbarItem item in page.ToolbarItems)
        {
            item.PropertyChanged -= OnToolbarItemChanged;
        }
    }

    private void OnToolbarCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        // Re-read the page so the per-item subscriptions follow the collection.
        if (_toolbarPage is { } page)
        {
            foreach (ToolbarItem item in page.ToolbarItems)
            {
                item.PropertyChanged -= OnToolbarItemChanged;
                item.PropertyChanged += OnToolbarItemChanged;
            }
        }
        RebuildToolbar();
    }

    private void OnToolbarItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        => RebuildToolbar();
}
