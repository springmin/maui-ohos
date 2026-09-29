// IFontManager for OpenHarmony: the platform draws with one selected typeface, so the manager
// resolves MAUI font families to a font file (the conventional Assets/Fonts/<family>.ttf) and
// hands it to the native drawing layer through OpenHarmonyBridge.SetFontFile.
//
// The manager also owns the system font scale (T21): OpenHarmony reports the user's font size
// setting as a scale factor, and because this slice draws all text itself the scale has to be
// applied to every text size the canvas measures or draws. MAUI font values stay logical (a
// Label.FontSize of 20 is still 20); the text pipeline multiplies by the scale at the platform
// boundary, exactly like an sp-sized TextView does on Android.
using Microsoft.Maui;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFontManager : IFontManager
{
    private static readonly Dictionary<string, string> s_registered = new(StringComparer.OrdinalIgnoreCase);
    private static int s_typefaceGeneration;
    private string? _current;

    /// <summary>Lowest system font scale the platform honours (everything below clamps here).</summary>
    private const float MinimumSystemFontScale = 0.5f;

    /// <summary>Highest system font scale the platform honours (everything above clamps here).</summary>
    private const float MaximumSystemFontScale = 3f;

    private static float s_systemFontScale = 1f;
    private static int s_fontScaleGeneration;

    /// <summary>
    /// Bumped whenever the process-wide typeface changes (a font family resolving to a font file).
    /// Text measurements depend on the selected typeface, so caches keyed by (text, size) include
    /// this generation and can never serve a width measured with a different font file.
    /// </summary>
    internal static int TypefaceGeneration => Volatile.Read(ref s_typefaceGeneration);

    public IFontRegistrar Registrar { get; }

    /// <summary>Default text size when a font does not specify one.</summary>
    public double DefaultFontSize => 14;

    /// <summary>
    /// The platform's system font scale (1 is the default size). Text is measured and drawn at
    /// its logical size multiplied by this factor; 0.5..3 are the honoured bounds.
    /// </summary>
    public static float SystemFontScale => Volatile.Read(ref s_systemFontScale);

    /// <summary>
    /// Publishes the system font scale (the user's font size setting) to the text pipeline. A
    /// non-finite or non-positive value resets the scale to 1; anything else is clamped to
    /// 0.5..3. Once the value changes, callers should re-arrange and redraw so the new metrics
    /// take effect (the host does that when the configuration changes).
    /// </summary>
    public static void SetSystemFontScale(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0)
        {
            scale = 1f;
        }
        scale = Math.Clamp(scale, MinimumSystemFontScale, MaximumSystemFontScale);
        if (Math.Abs(Volatile.Read(ref s_systemFontScale) - scale) < 0.0001f)
        {
            return;
        }
        Volatile.Write(ref s_systemFontScale, scale);
        Interlocked.Increment(ref s_fontScaleGeneration);
    }

    /// <summary>Bumped whenever the system font scale changes: caches keyed by text metrics include it.</summary>
    internal static int FontScaleGeneration => Volatile.Read(ref s_fontScaleGeneration);

    /// <summary>A logical text size in canvas units (the platform boundary this slice owns).</summary>
    internal static float ScaleFontSize(float fontSize) => fontSize * Volatile.Read(ref s_systemFontScale);

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
        // Any measurement cached before this point was taken with the previous typeface.
        Interlocked.Increment(ref s_typefaceGeneration);
    }
}
