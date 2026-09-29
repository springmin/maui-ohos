// Visual diagnostics for OpenHarmony: draws every view's frame with its type name so layout issues
// can be inspected on a device (the runtime has no IDE overlay). Enabled by the app or by the
// OpenHarmonyDiagnostics.Enabled flag; the same pass counts the outlines for tests.
namespace Microsoft.Maui.Platform;

public static class OpenHarmonyDiagnostics
{
    /// <summary>When true the compositor outlines every view after drawing it.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Number of outlines drawn by the last frame (0 while disabled).</summary>
    public static int OutlinesDrawn { get; internal set; }

    internal static void Reset() => OutlinesDrawn = 0;

    internal static void Count() => OutlinesDrawn++;
}
