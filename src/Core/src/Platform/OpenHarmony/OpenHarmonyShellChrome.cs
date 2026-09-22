// Shell chrome the compositor can express beyond the title string: a text-producing TitleView
// and the current page's ToolbarItems.
//
// TitleView (the Shell.TitleView / NavigationPage.TitleView attached property, a View): the
// compositor title bar draws text, so a visible Label publishes its Text as the bar title. A
// rich view (Image/SearchBar/layout) has no representation in the text compositor; the page
// title stays in the bar and the gap is noted once (OpenHarmonyShellChrome.LogRichTitleViewOnce)
// instead of silently showing the wrong thing. The value is re-read on every chrome sync, and
// the shell handler calls that sync from the renderer's per-draw ChromeRefresh, so a Label text
// change is picked up on the next frame.
//
// ToolbarItems: the current page's collection is mirrored into OpenHarmonyView.ToolbarItems the
// same way OpenHarmonyNavigationPageHandler mirrors it for NavigationPage: the bar draws one
// text item per ToolbarItem and a tap activates it (IMenuItemController.Activate, else the
// item's Command with its CommandParameter). The mirror compares the page, the item count and
// the item texts before rebuilding, because the shell chrome sync runs on every draw.
using Microsoft.Maui.Controls;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal sealed class OpenHarmonyShellChrome
{
    private static bool s_richTitleViewLogged;

    private readonly OpenHarmonyView _view;
    private readonly Action _refresh;
    private Page? _toolbarPage;

    public OpenHarmonyShellChrome(OpenHarmonyView view, Action refresh)
    {
        _view = view;
        _refresh = refresh;
    }

    /// <summary>Re-reads the title and the current page's toolbar items from the shell.</summary>
    public void Apply(Shell shell)
    {
        _view.TitleText = ResolveTitleText(shell);
        UpdateToolbar(shell.CurrentPage);
    }

    /// <summary>Clears the mirrored toolbar and releases the page reference.</summary>
    public void Detach()
    {
        _toolbarPage = null;
        _view.ToolbarItems.Clear();
    }

    /// <summary>
    /// The compositor title: a visible Label title view's Text when there is one, else the page
    /// title (then the shell item title). A non-text title view is noted once.
    /// </summary>
    private static string ResolveTitleText(Shell shell)
    {
        View? titleView = shell.CurrentPage is { } page ? Shell.GetTitleView(page) : null;
        titleView ??= Shell.GetTitleView(shell);
        if (titleView is Label { IsVisible: true, Text: { Length: > 0 } text })
        {
            return text;
        }
        if (titleView is not null)
        {
            LogRichTitleViewOnce();
        }
        return shell.CurrentPage?.Title ?? shell.CurrentItem?.Title ?? string.Empty;
    }

    /// <summary>
    /// One status note per process: the title bar draws a single text run, so a TitleView that
    /// is not a Label cannot be rendered (the page title remains the bar's text).
    /// </summary>
    private static void LogRichTitleViewOnce()
    {
        if (s_richTitleViewLogged)
        {
            return;
        }
        s_richTitleViewLogged = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] shell title view: the compositor title bar draws text only, so a rich TitleView is not rendered; the page title stays in the bar (a visible Label title view publishes its text)");
    }

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
