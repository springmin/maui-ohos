// Process-wide layout invalidation version for the compositor's layout gate.
//
// The slice has no platform layout pipeline: MAUI invalidations surface as handler invokes
// (IView.InvalidateMeasure for a virtual measure invalidation, ILayoutHandler.Add/Remove/
// Clear/Insert/Update for structural edits), and the compositor used to re-run Measure/Arrange
// unconditionally on every Render to pick them up. OpenHarmonyViewHandler.Invoke bumps this
// version on exactly those commands, so OpenHarmonyWindowRenderer can skip the per-frame layout
// pass while still re-arranging on the next frame after any change. A plain counter (not a
// flag) matters: a mark raised while a pass is running must survive that pass.
using System.Threading;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyLayoutInvalidation
{
    private static int s_version;

    /// <summary>Current layout version; changes on every invalidation command.</summary>
    internal static int Version => Volatile.Read(ref s_version);

    /// <summary>Records one layout-affecting command.</summary>
    internal static void Mark() => Interlocked.Increment(ref s_version);
}
