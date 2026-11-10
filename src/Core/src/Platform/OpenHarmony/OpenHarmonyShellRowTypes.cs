using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Materialized rich Shell.TitleView row (T15): the resolved non-Label view and the canvas-space
/// band the chrome arranged it into. The renderer draws the row over the title bar and routes
/// touches in the band into the view. Null while the bar keeps the text title path.
/// </summary>
internal sealed class OpenHarmonyShellTitleViewRow
{
    /// <summary>The app's TitleView (handlers are connected by the chrome's sync).</summary>
    public required IView View { get; init; }

    /// <summary>Canvas-space band the row was arranged into (empty before the first sync).</summary>
    public RectF Frame { get; set; }
}

/// <summary>
/// Materialized rich row of the Shell flyout panel (T14): either a text row or a real view that
/// the renderer draws and hit-tests. <see cref="Frame"/> is the physical (canvas space) box
/// computed from the panel's resolved flow direction, and <see cref="PanelIndex"/> is the row's
/// position in the panel - the index space <c>OpenHarmonyShellHandler.SelectFlyoutRow</c>
/// maps back to shell items.
/// </summary>
internal sealed class OpenHarmonyFlyoutRow
{
    /// <summary>Materialized row content; null for a text row.</summary>
    public IView? View { get; init; }

    /// <summary>Row label; null for a rich row.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// The element a tap on this row selects: a ShellItem/ShellSection/ShellContent, or a menu
    /// item (activated through <c>IMenuItemController</c>). Null for header/footer/content
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
