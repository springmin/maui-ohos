// Rich shell flyout panel (T14): the compositor's drawer (OpenHarmonyView.FlyoutItems) is a flat
// list of text rows with fixed geometry. This materializes the rows a Shell can carry:
//   * FlyoutHeader/FlyoutFooter as a View (any non-Label view) or through FlyoutHeaderTemplate /
//     FlyoutFooterTemplate. Shell itself creates the template content (IShellController.FlyoutHeader/
//     FlyoutFooter return the resolved view), so its BindingContext inherits from the Shell exactly
//     like MAUI's own flyout renderers; a directly assigned Label keeps the flat text row (and the
//     ArkTS section label the shell publishes).
//   * FlyoutContent/FlyoutContentTemplate: the resolved IShellController.FlyoutContent view replaces
//     the item rows and fills the panel between the header and the footer, like MAUI's platform
//     renderers (a template content binds against the Shell).
//   * The item rows are MAUI's canonical flyout model, IShellController.GenerateFlyoutGrouping():
//     it flattens implicit items, expands an item/section whose FlyoutDisplayOptions is
//     AsMultipleItems into one row per visible child, honours FlyoutItemIsVisible and appends the
//     visible MenuItems of the current ShellContent. An item row resolves Shell.ItemTemplate
//     (the element's own attached value first, then the shell's), a menu row resolves
//     Shell.MenuItemTemplate the same way; a row without a matching template keeps its text.
//   * Activating a row: a menu row runs IMenuItemController.Activate() (Clicked/Command and the
//     IsEnabled gate live there), an item row selects its element - a ShellContent selects its
//     section and item, so an AsMultipleItems row lands exactly on its child.
// The rows are real views: they are measured/arranged into the panel and handed to the view's
// FlyoutRows list, which the renderer draws (after the panel background) and routes touches into
// (a row's own content handles the touch first; the row selection is the fallback).
// The panel only exists on screen while the drawer is open, so the sync runs on that window's
// draws; a closed drawer keeps the last materialized rows and the next open re-measures them.
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One row of the shell flyout panel. A text row carries <see cref="Text"/> and is drawn by
/// <see cref="OpenHarmonyView.DrawFlyoutPanel"/>; a rich row carries a materialized
/// <see cref="View"/> that the renderer draws and hit-tests. <see cref="Frame"/> is the physical
/// (canvas space) box computed from the panel's resolved flow direction, and
/// <see cref="PanelIndex"/> is the row's position in the panel - the index space
/// <see cref="OpenHarmonyShellHandler.SelectFlyoutRow"/> maps back to shell items.
/// </summary>
internal sealed class OpenHarmonyFlyoutRow
{
    /// <summary>Materialized row content; null for a text row.</summary>
    public View? View { get; init; }

    /// <summary>Row label; null for a rich row.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// The element a tap on this row selects: a ShellItem/ShellSection/ShellContent, or a menu
    /// item (activated through <see cref="IMenuItemController"/>). Null for header/footer/content
    /// rows, which are not selectable.
    /// </summary>
    public object? Target { get; init; }

    /// <summary>Shell item the row belongs to (diagnostics and the flat fallback), -1 otherwise.</summary>
    public int ItemIndex { get; init; } = -1;

    /// <summary>Body row: the FlyoutContent view fills the panel between the header and footer.</summary>
    public bool IsBody { get; init; }

    /// <summary>Position in the panel (leading header first, then the items, then the footer).</summary>
    public int PanelIndex { get; init; }

    /// <summary>Physical canvas-space box (filled by the materializer's arrange pass).</summary>
    public RectF Frame { get; set; }
}

/// <summary>
/// Materializes and arranges the rich rows of the shell flyout panel (see the file header).
/// </summary>
internal sealed class OpenHarmonyShellFlyout
{
    private readonly OpenHarmonyView _view;

    /// <summary>Views this materializer created (item/menu template content); disposed on rebuild.</summary>
    private readonly List<View> _owned = new();

    /// <summary>Reused measure scratch for the arrange pass (no per-arrange allocation).</summary>
    private readonly List<float> _heights = new();

    // A change in any of these rebuilds the row views.
    private (object? Header, object? Footer, object? Content, DataTemplate? ItemTemplate,
        DataTemplate? MenuItemTemplate, int Rows) _built;

    // A change in any of these re-measures/re-frames the rows (panel geometry, flow direction,
    // system font scale, a new open - a row's content may have settled while closed).
    private (float X, float Y, float Width, bool RightToLeft, int FontScale, int OpenEpoch, int Rows, int Build) _arranged;

    private int _buildSerial;
    private int _openEpoch;
    private bool _wasOpen;
    private bool _isRich;

    public OpenHarmonyShellFlyout(OpenHarmonyView view) => _view = view;

    /// <summary>True when the panel is driven by rich rows instead of the flat text layout.</summary>
    public bool IsRich => _isRich;

    /// <summary>
    /// Syncs the panel with the shell. Does nothing while the drawer is closed; the next open
    /// re-enters with a new open epoch, so rows settle their measurement before they are shown.
    /// </summary>
    public void Sync(Shell shell, string? headerText, string? footerText)
    {
        if (!_view.FlyoutOpen)
        {
            _wasOpen = false;
            return;
        }
        if (!_wasOpen)
        {
            _wasOpen = true;
            _openEpoch++;
        }
        View? headerView = RichSectionView(shell, header: true);
        View? footerView = RichSectionView(shell, header: false);
        View? contentView = FlyoutContentView(shell);
        // The canonical flyout model: grouping flattens implicit items, expands AsMultipleItems
        // children and appends the current content's menu items.
        List<List<Element>> grouping = ((IShellController)shell).GenerateFlyoutGrouping();
        int rowCount = 0;
        bool hasMenuRow = false;
        int rowsHash = grouping.Count;
        foreach (List<Element> group in grouping)
        {
            foreach (Element element in group)
            {
                rowCount++;
                hasMenuRow |= element is IMenuItemController;
                rowsHash = rowsHash * 31 + RowHash(element);
            }
        }
        _isRich = headerView is not null || footerView is not null || contentView is not null
            || shell.ItemTemplate is not null || shell.MenuItemTemplate is not null
            || hasMenuRow || HasMultipleItems(shell);
        if (!_isRich)
        {
            if (_view.FlyoutRows.Count > 0)
            {
                DisconnectOwned();
                _view.FlyoutRows.Clear();
            }
            _built = default;
            _arranged = default;
            return;
        }
        var build = (Header: (object?)headerView, Footer: (object?)footerView, Content: (object?)contentView,
            ItemTemplate: shell.ItemTemplate, MenuItemTemplate: shell.MenuItemTemplate, Rows: rowsHash);
        if (build != _built)
        {
            Rebuild(shell, headerView, footerView, contentView, grouping, headerText, footerText);
            _built = build;
            _buildSerial++;
        }
        RectF panel = _view.FlyoutPanelRect();
        var arrange = (X: panel.X, Y: panel.Y, Width: panel.Width, RightToLeft: _view.FlowRightToLeft,
            FontScale: OpenHarmonyFontManager.FontScaleGeneration, OpenEpoch: _openEpoch,
            Rows: _view.FlyoutRows.Count, Build: _buildSerial);
        if (arrange == _arranged)
        {
            return;
        }
        Arrange(panel);
        _arranged = arrange;
    }

    /// <summary>Releases the materialized rows (handler detach). Shell-owned views stay.</summary>
    public void Detach()
    {
        DisconnectOwned();
        _view.FlyoutRows.Clear();
        _built = default;
        _arranged = default;
        _isRich = false;
    }

    /// <summary>
    /// The resolved rich view of a flyout section, or null when the section is a plain text row.
    /// Shell creates <c>FlyoutHeaderView</c>/<c>FlyoutFooterView</c> for both a directly assigned
    /// View and a template, so the view is reused as-is (its BindingContext inherits from the
    /// Shell through the logical parent MAUI wired).
    /// </summary>
    private static View? RichSectionView(Shell shell, bool header)
    {
        IShellController controller = shell;
        View? view = header ? controller.FlyoutHeader : controller.FlyoutFooter;
        if (view is null)
        {
            return null;
        }
        DataTemplate? template = header ? shell.FlyoutHeaderTemplate : shell.FlyoutFooterTemplate;
        // A directly assigned Label keeps the flat text row (and the ArkTS section label);
        // template-produced content is always a rich row, even when the template is a Label.
        return template is null && view is Label ? null : view;
    }

    /// <summary>
    /// The resolved FlyoutContent view (a directly assigned view or the template content Shell
    /// created); null when the shell has neither. The view replaces the item rows.
    /// </summary>
    private static View? FlyoutContentView(Shell shell) => ((IShellController)shell).FlyoutContent;

    /// <summary>
    /// True when any shell item or section opts into <see cref="FlyoutDisplayOptions.AsMultipleItems"/>,
    /// so its children are shown as individual rows (the canonical grouping expands them).
    /// </summary>
    private static bool HasMultipleItems(Shell shell)
    {
        foreach (ShellItem item in shell.Items)
        {
            if (item.FlyoutDisplayOptions == FlyoutDisplayOptions.AsMultipleItems)
            {
                return true;
            }
            foreach (ShellSection section in item.Items)
            {
                if (section.FlyoutDisplayOptions == FlyoutDisplayOptions.AsMultipleItems)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Change detector for one grouping element (identity, text and enabled state).</summary>
    private static int RowHash(Element element)
    {
        int hash = element.GetType().GetHashCode();
        hash = hash * 31 + RuntimeHelpers.GetHashCode(element);
        hash = hash * 31 + (ElementText(element)?.GetHashCode() ?? 0);
        hash = hash * 31 + (element is IMenuItemController { IsEnabled: false } ? 0 : 1);
        return hash;
    }

    private static string? ElementText(Element element) => element switch
    {
        MenuItem menu => menu.Text,
        BaseShellItem item => item.Title,
        _ => null,
    };

    private void Rebuild(Shell shell, View? headerView, View? footerView, View? contentView,
        List<List<Element>> grouping, string? headerText, string? footerText)
    {
        DisconnectOwned();
        _view.FlyoutRows.Clear();
        int panelIndex = 0;
        if (headerView is not null)
        {
            // A template section binds against the Shell object (MAUI's documented flyout
            // template contract; a directly assigned View keeps its own inherited context).
            if (shell.FlyoutHeaderTemplate is not null)
            {
                headerView.BindingContext = shell;
            }
            // Shell owns the resolved section view; connecting its subtree (idempotent) is what
            // gives the section's inner content a platform view to measure, draw and hit-test.
            OpenHarmonyHandlerConnector.ConnectTree(headerView);
            Add(new OpenHarmonyFlyoutRow { View = headerView, ItemIndex = -1, PanelIndex = panelIndex++ });
        }
        else if (!string.IsNullOrEmpty(headerText))
        {
            Add(new OpenHarmonyFlyoutRow { Text = headerText, ItemIndex = -1, PanelIndex = panelIndex++ });
        }
        if (contentView is not null)
        {
            // FlyoutContent replaces the item rows between the sections, exactly like the
            // platform renderers; template content binds against the Shell.
            if (shell.FlyoutContentTemplate is not null)
            {
                contentView.BindingContext = shell;
            }
            OpenHarmonyHandlerConnector.ConnectTree(contentView);
            Add(new OpenHarmonyFlyoutRow { View = contentView, IsBody = true, PanelIndex = panelIndex++ });
        }
        else
        {
            foreach (List<Element> group in grouping)
            {
                foreach (Element element in group)
                {
                    AddMaterialized(shell, element, ref panelIndex);
                }
            }
        }
        if (footerView is not null)
        {
            if (shell.FlyoutFooterTemplate is not null)
            {
                footerView.BindingContext = shell;
            }
            OpenHarmonyHandlerConnector.ConnectTree(footerView);
            Add(new OpenHarmonyFlyoutRow { View = footerView, ItemIndex = -1, PanelIndex = panelIndex });
        }
        else if (!string.IsNullOrEmpty(footerText))
        {
            Add(new OpenHarmonyFlyoutRow { Text = footerText, ItemIndex = -1, PanelIndex = panelIndex });
        }
    }

    /// <summary>
    /// One row for a grouping element: a menu row activates the item, an item row selects its
    /// element. A matching template materializes a view bound to the element; without one the
    /// row keeps the element's text (the slice's label-cell equivalent).
    /// </summary>
    private void AddMaterialized(Shell shell, Element element, ref int panelIndex)
    {
        int itemIndex = element is IMenuItemController ? -1 : TopLevelIndex(shell, element);
        DataTemplate? template = element is IMenuItemController
            ? MenuItemTemplateFor(shell, element)
            : ItemTemplateFor(shell, element);
        View? view = template is null ? null : CreateView(template, element);
        if (view is not null)
        {
            Add(new OpenHarmonyFlyoutRow { View = view, Target = element, ItemIndex = itemIndex, PanelIndex = panelIndex++ });
        }
        else
        {
            Add(new OpenHarmonyFlyoutRow
            {
                Text = ElementText(element) ?? string.Empty,
                Target = element,
                ItemIndex = itemIndex,
                PanelIndex = panelIndex++,
            });
        }
    }

    private void Add(OpenHarmonyFlyoutRow row) => _view.FlyoutRows.Add(row);

    /// <summary>
    /// The item row template: the element's own attached value first, then the shell's. No
    /// explicit template keeps the text row (MAUI's default flyout cell is a Cell, which this
    /// compositor cannot materialize as a View).
    /// </summary>
    private static DataTemplate? ItemTemplateFor(Shell shell, Element element)
        => Shell.GetItemTemplate(element) ?? shell.ItemTemplate;

    /// <summary>
    /// The menu row template, in MAUI's resolution order: the item's parent (the object the
    /// template was set on), the menu item itself, then the shell.
    /// </summary>
    private static DataTemplate? MenuItemTemplateFor(Shell shell, Element element)
    {
        DataTemplate? template = null;
        if (element is MenuItem menuItem && menuItem.Parent is BindableObject parent)
        {
            template = Shell.GetMenuItemTemplate(parent);
        }
        template ??= Shell.GetMenuItemTemplate(element);
        return template ?? shell.MenuItemTemplate;
    }

    /// <summary>The index of the element's top-level ShellItem in <c>shell.Items</c>, or -1.</summary>
    private static int TopLevelIndex(Shell shell, Element element)
    {
        ShellItem? top = element switch
        {
            ShellItem item => item,
            ShellSection section => section.Parent as ShellItem,
            ShellContent content => content.Parent?.Parent as ShellItem,
            _ => null,
        };
        return top is null ? -1 : shell.Items.IndexOf(top);
    }

    /// <summary>
    /// Creates one row from a template. The content is bound to its element so the template's
    /// bindings resolve like MAUI's own flyout rows; null falls back to the text row (a template
    /// that does not produce a view).
    /// </summary>
    private View? CreateView(DataTemplate template, object item)
    {
        if (template.CreateContent() is not View view)
        {
            return null;
        }
        view.BindingContext = item;
        OpenHarmonyHandlerConnector.ConnectTree(view);
        _owned.Add(view);
        return view;
    }

    private void Arrange(RectF panel)
    {
        float rowWidth = Math.Max(1f, panel.Width - 2 * OpenHarmonyView.FlyoutPanelRowInset);
        // Measure the non-body rows first so the body (FlyoutContent) gets the leftover panel
        // height; heights are cached for the frame pass below.
        _heights.Clear();
        float reserved = OpenHarmonyView.FlyoutPanelTopInset;
        bool hasBody = false;
        foreach (OpenHarmonyFlyoutRow row in _view.FlyoutRows)
        {
            if (row.IsBody)
            {
                hasBody = true;
                _heights.Add(0f);
                continue;
            }
            float height = MeasureRow(row, rowWidth, double.PositiveInfinity);
            _heights.Add(height);
            reserved += height + OpenHarmonyView.FlyoutRowGap;
        }
        float bodyHeight = hasBody ? Math.Max(1f, panel.Height - reserved) : 0f;
        float y = panel.Y + OpenHarmonyView.FlyoutPanelTopInset;
        for (int i = 0; i < _view.FlyoutRows.Count; i++)
        {
            OpenHarmonyFlyoutRow row = _view.FlyoutRows[i];
            float height = row.IsBody ? bodyHeight : _heights[i];
            if (row.IsBody)
            {
                MeasureRow(row, rowWidth, bodyHeight);
            }
            row.Frame = new RectF(panel.X + OpenHarmonyView.FlyoutPanelRowInset, y, rowWidth, height);
            if (row.View is { } view)
            {
                // The row is the section's parent here, so the view's explicit request is this
                // parent's to honor (MAUI layouts apply HeightRequest at arrange time).
                view.Arrange(new Rect(row.Frame.X, row.Frame.Y, row.Frame.Width, row.Frame.Height));
            }
            y += height + OpenHarmonyView.FlyoutRowGap;
        }
    }

    /// <summary>Measures one row's view and returns its row height (the flat slot at minimum).</summary>
    private static float MeasureRow(OpenHarmonyFlyoutRow row, float rowWidth, double heightConstraint)
    {
        float height = OpenHarmonyView.FlyoutRowHeight;
        if (row.View is { } view)
        {
            view.Measure(rowWidth, heightConstraint);
            double desired = Math.Max(view.DesiredSize.Height, view.HeightRequest);
            if (desired > height)
            {
                height = (float)desired;
            }
        }
        return height;
    }

    private void DisconnectOwned()
    {
        foreach (View view in _owned)
        {
            view.Handler?.DisconnectHandler();
        }
        _owned.Clear();
    }
}
