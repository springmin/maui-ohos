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
// same way OpenHarmonyNavigationPageHandler mirrors it for NavigationPage: the bar draws one
// text item per ToolbarItem and a tap activates it (IMenuItemController.Activate, else the
// item's Command with its CommandParameter). The mirror compares the page, the item count and
// the item texts before rebuilding, because the shell chrome sync runs on every draw.
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
        _toolbarPage = null;
        _view.ToolbarItems.Clear();
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
    /// Mirrors <paramref name="page"/>'s ToolbarItems into the platform bar. Rebuilds only when
    /// the page, the item count or an item text changed; the tap delegate re-checks IsEnabled.
    /// </summary>
    private void UpdateToolbar(Page? page)
    {
        bool pageChanged = !ReferenceEquals(_toolbarPage, page);
        _toolbarPage = page;
        if (!pageChanged && !ToolbarDiffers(page))
        {
            return;
        }
        _view.ToolbarItems.Clear();
        if (page is null)
        {
            _refresh();
            return;
        }
        foreach (ToolbarItem item in page.ToolbarItems)
        {
            ToolbarItem captured = item;
            _view.ToolbarItems.Add((captured.Text ?? string.Empty, () => ActivateToolbarItem(captured)));
        }
        _refresh();
    }

    /// <summary>True when the mirrored item texts no longer match the page's collection.</summary>
    private bool ToolbarDiffers(Page? page)
    {
        if (page is null)
        {
            return _view.ToolbarItems.Count > 0;
        }
        if (_view.ToolbarItems.Count != page.ToolbarItems.Count)
        {
            return true;
        }
        for (int i = 0; i < page.ToolbarItems.Count; i++)
        {
            if (!string.Equals(_view.ToolbarItems[i].Text, page.ToolbarItems[i].Text ?? string.Empty, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Same activation contract as OpenHarmonyNavigationPageHandler.ActivateToolbarItem.</summary>
    private static void ActivateToolbarItem(ToolbarItem item)
    {
        if (!item.IsEnabled)
        {
            return;
        }
        if (item is IMenuItemController controller)
        {
            controller.Activate();
        }
        else
        {
            item.Command?.Execute(item.CommandParameter);
        }
    }
}
