// IFontManager for OpenHarmony: the platform draws with one selected typeface, so the manager
// resolves MAUI font families to a font file (the conventional Assets/Fonts/<family>.ttf) and
// hands it to the native drawing layer through OpenHarmonyBridge.SetFontFile.
using Microsoft.Maui;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFontManager : IFontManager
{
    private static readonly Dictionary<string, string> s_registered = new(StringComparer.OrdinalIgnoreCase);
    private string? _current;

    public IFontRegistrar Registrar { get; }

    /// <summary>Default text size when a font does not specify one.</summary>
    public double DefaultFontSize => 14;

    public OpenHarmonyFontManager(IFontRegistrar registrar)
    {
        Registrar = registrar;
    }

    /// <summary>Registers a font file for a family (used by the app or by ConfigureFonts).</summary>
    public static void RegisterFont(string family, string path)
    {
        if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(path))
        {
            s_registered[family] = path;
        }
    }

    public Font GetFont(Font? font, double? defaultSize = null)
    {
        Font resolved = font ?? Font.Default;
        if (defaultSize is > 0)
        {
            resolved = resolved.WithSize(defaultSize.Value);
        }
        ApplyFamily(resolved.Family);
        return resolved;
    }

    private void ApplyFamily(string? family)
    {
        if (string.Equals(_current, family, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _current = family;
        if (string.IsNullOrEmpty(family))
        {
            return;
        }
        if (!s_registered.TryGetValue(family, out string? path))
        {
            // Conventional location for AddFont-style registrations.
            string candidate = Path.Combine(OpenHarmonyPaths.DataDirectory, "Fonts", family + ".ttf");
            if (!File.Exists(candidate))
            {
                return;
            }
            path = candidate;
        }
        OpenHarmony.Hosting.OpenHarmonyBridge.SetFontFile(path);
    }
}
