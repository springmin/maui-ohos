// Builds the compositor toolbar rows from the current page's ToolbarItems (shared by the
// NavigationPage handler and the Shell chrome). MAUI's platform contract (the Tizen/Windows
// reference implementations): Default and Primary items dock in the bar, Secondary items live in
// the overflow dropdown, and both partitions order by Priority then declaration order. An icon
// is materialized here - a file source as bytes (the tab-bar path), a FontImageSource as the
// cached glyph (the compositor's text path) - with the label kept alongside; the async
// stream/URI sources keep the label fallback rather than blocking a draw.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyToolbarMirror
{
    /// <summary>
    /// Rebuilds <paramref name="target"/> in mirror order: the primary rows first (by Priority),
    /// then the secondary rows behind the overflow affordance.
    /// </summary>
    public static void Fill(IList<OpenHarmonyToolbarItem> target, IEnumerable<ToolbarItem>? items)
    {
        target.Clear();
        if (items is null)
        {
            return;
        }
        // Stable two-key ordering: the primary/secondary partition stays ahead of Priority, and
        // equal priorities keep declaration order (OrderBy is a stable sort).
        foreach (ToolbarItem item in items
            .OrderBy(static item => item.Order == ToolbarItemOrder.Secondary ? 1 : 0)
            .ThenBy(static item => item.Priority))
        {
            (byte[]? bytes, string? glyph, float glyphSize, Color glyphColor) =
                ResolveIcon(item.IconImageSource);
            target.Add(new OpenHarmonyToolbarItem
            {
                Text = item.Text ?? string.Empty,
                IconBytes = bytes,
                Glyph = glyph,
                GlyphFontSize = glyphSize,
                GlyphColor = glyphColor,
                IsEnabled = item.IsEnabled,
                IsSecondary = item.Order == ToolbarItemOrder.Secondary,
                Activate = () => Activate(item),
            });
        }
    }

    /// <summary>The shared activation contract (Clicked/Command), guarded by IsEnabled.</summary>
    public static void Activate(ToolbarItem item)
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

    /// <summary>Materializes a ToolbarItem icon; unsupported source kinds fall back to the label.</summary>
    private static (byte[]? Bytes, string? Glyph, float Size, Color Color) ResolveIcon(ImageSource? source)
    {
        switch (source)
        {
            case FileImageSource file when !string.IsNullOrEmpty(file.File) && File.Exists(file.File):
                try
                {
                    return (File.ReadAllBytes(file.File), null, 0f, Colors.White);
                }
                catch
                {
                    // An unreadable icon must not take the item's label down.
                    return (null, null, 0f, Colors.White);
                }
            case FontImageSource font when !string.IsNullOrEmpty(font.Glyph):
                OpenHarmonyFontImageSource.Glyph glyph = OpenHarmonyFontImageSource.Resolve(font);
                return (null, glyph.Text, glyph.FontSize, glyph.Color);
            default:
                // Uri/stream sources need the async image service: keep the label instead of an
                // empty slot, matching the tab bar's file-only icon policy.
                return (null, null, 0f, Colors.White);
        }
    }
}
