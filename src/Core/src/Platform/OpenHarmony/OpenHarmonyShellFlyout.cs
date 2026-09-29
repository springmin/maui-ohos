// Rich shell flyout panel (T14): the compositor's drawer (OpenHarmonyView.FlyoutItems) is a flat
// list of text rows with fixed geometry. This materializes the rich sections a Shell can carry:
//   * FlyoutHeader/FlyoutFooter as a View (any non-Label view) or through FlyoutHeaderTemplate /
//     FlyoutFooterTemplate. Shell itself creates the template content (IShellController.FlyoutHeader/
//     FlyoutFooter return the resolved view), so its BindingContext inherits from the Shell exactly
//     like MAUI's own flyout renderers; a directly assigned Label keeps the flat text row (and the
//     ArkTS section label the shell publishes).
//   * Shell.ItemTemplate item rows: the template content is created here and bound to its
//     ShellItem, one row per shell item, so a flyout item can be a full view (icon + title +
//     accessory) instead of a title string.
// The rows are real views: they are measured/arranged into the panel and handed to the view's
// FlyoutRows list, which the renderer draws (after the panel background) and routes touches into
// (a row's own content handles the touch first; the flat row -> item selection is the fallback).
// The panel only exists on screen while the drawer is open, so the sync runs on that window's
// draws; a closed drawer keeps the last materialized rows and the next open re-measures them.
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

    /// <summary>Shell item the row selects, -1 for headers/footers.</summary>
    public int ItemIndex { get; init; } = -1;

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

    /// <summary>Views this materializer created (item template content); disposed on rebuild.</summary>
    private readonly List<View> _owned = new();

    // A change in any of these rebuilds the row views.
    private (object? Header, object? Footer, DataTemplate? ItemTemplate, int ItemCount, int TitleHash) _built;

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
        DataTemplate? itemTemplate = shell.ItemTemplate;
        _isRich = headerView is not null || footerView is not null || itemTemplate is not null;
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
        var build = (Header: (object?)headerView, Footer: (object?)footerView, ItemTemplate: itemTemplate,
            ItemCount: shell.Items.Count, TitleHash: TitleHash(shell));
        if (build != _built)
        {
            Rebuild(shell, headerView, footerView, itemTemplate, headerText, footerText);
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

    /// <summary>Releases the materialized rows (handler detach). Shell-owned section views stay.</summary>
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

    private static int TitleHash(Shell shell)
    {
        int hash = shell.Items.Count;
        for (int i = 0; i < shell.Items.Count; i++)
        {
            hash = hash * 31 + (shell.Items[i].Title?.GetHashCode() ?? 0);
        }
        return hash;
    }

    private void Rebuild(Shell shell, View? headerView, View? footerView, DataTemplate? itemTemplate,
        string? headerText, string? footerText)
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
            Add(new OpenHarmonyFlyoutRow { View = headerView, ItemIndex = -1, PanelIndex = panelIndex++ });
        }
        else if (!string.IsNullOrEmpty(headerText))
        {
            Add(new OpenHarmonyFlyoutRow { Text = headerText, ItemIndex = -1, PanelIndex = panelIndex++ });
        }
        for (int i = 0; i < shell.Items.Count; i++)
        {
            View? itemView = CreateItemView(itemTemplate, shell.Items[i]);
            if (itemView is not null)
            {
                Add(new OpenHarmonyFlyoutRow { View = itemView, ItemIndex = i, PanelIndex = panelIndex++ });
            }
            else
            {
                Add(new OpenHarmonyFlyoutRow
                {
                    Text = shell.Items[i].Title ?? string.Empty,
                    ItemIndex = i,
                    PanelIndex = panelIndex++,
                });
            }
        }
        if (footerView is not null)
        {
            if (shell.FlyoutFooterTemplate is not null)
            {
                footerView.BindingContext = shell;
            }
            Add(new OpenHarmonyFlyoutRow { View = footerView, ItemIndex = -1, PanelIndex = panelIndex });
        }
        else if (!string.IsNullOrEmpty(footerText))
        {
            Add(new OpenHarmonyFlyoutRow { Text = footerText, ItemIndex = -1, PanelIndex = panelIndex });
        }
    }

    private void Add(OpenHarmonyFlyoutRow row) => _view.FlyoutRows.Add(row);

    /// <summary>
    /// Creates one item row from Shell.ItemTemplate. The content is bound to its ShellItem so
    /// the template's bindings resolve like MAUI's own flyout rows; null falls back to the
    /// item title row (a template that does not produce a view).
    /// </summary>
    private View? CreateItemView(DataTemplate? itemTemplate, object item)
    {
        if (itemTemplate is null)
        {
            return null;
        }
        if (itemTemplate.CreateContent() is not View view)
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
        float y = panel.Y + OpenHarmonyView.FlyoutPanelTopInset;
        float rowWidth = Math.Max(1f, panel.Width - 2 * OpenHarmonyView.FlyoutPanelRowInset);
        foreach (OpenHarmonyFlyoutRow row in _view.FlyoutRows)
        {
            float height = OpenHarmonyView.FlyoutRowHeight;
            if (row.View is { } view)
            {
                view.Measure(rowWidth, double.PositiveInfinity);
                // The row is the section's parent here, so the view's explicit request is this
                // parent's to honor (MAUI layouts apply HeightRequest at arrange time).
                double desired = Math.Max(view.DesiredSize.Height, view.HeightRequest);
                if (desired > height)
                {
                    height = (float)desired;
                }
                row.Frame = new RectF(panel.X + OpenHarmonyView.FlyoutPanelRowInset, y, rowWidth, height);
                view.Arrange(new Rect(row.Frame.X, row.Frame.Y, row.Frame.Width, row.Frame.Height));
            }
            else
            {
                row.Frame = new RectF(panel.X + OpenHarmonyView.FlyoutPanelRowInset, y, rowWidth, height);
            }
            y += height + OpenHarmonyView.FlyoutRowGap;
        }
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
