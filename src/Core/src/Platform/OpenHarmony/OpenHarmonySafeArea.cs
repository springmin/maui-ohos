// Safe-area handling for the slice.
//
// The ArkTS shell owns the window and reports its avoid area (status bar, navigation bar,
// display cutout) through ohos_host_get_avoid_area, which OpenHarmonyBridge.TryGetAvoidArea
// wraps. Those insets describe the window, not the app surface: the app host arranges the
// surface at its full bounds and each page/content view decides per edge, through MAUI's
// cross-platform SafeAreaEdges model, whether its content stays out of the avoid area
// (SafeAreaRegions.None means edge to edge, Container/All/Default keep out of the bars and the
// cutout, SoftInput only pads for the keyboard - which this slice does not track yet, so it
// applies nothing). ISafeAreaView is documented as iOS/Mac Catalyst-only, so a view that does
// not implement ISafeAreaElement falls back to the platform default (Container); that keeps
// the historical look for container pages such as NavigationPage/Shell/FlyoutPage, whose
// chrome (the navigation bar) must stay out of the status bar. When the shell reports no avoid
// area, or the host library is absent (the off-device verification harness), every inset is
// zero and the arrangement is exactly the historical one.
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonySafeArea
{
    /// <summary>Window avoid area in pixels; zero when the shell has no answer.</summary>
    internal static Thickness GetWindowInsets()
    {
        if (OpenHarmonyBridge.TryGetAvoidArea(out int top, out int bottom, out int left, out int right))
        {
            return new Thickness(Math.Max(0, left), Math.Max(0, top), Math.Max(0, right), Math.Max(0, bottom));
        }
        return Thickness.Zero;
    }

    internal static bool IsEmpty(Thickness insets) =>
        insets.Left <= 0 && insets.Top <= 0 && insets.Right <= 0 && insets.Bottom <= 0;

    /// <summary>The safe-area regions a view asks to obey on each edge.</summary>
    internal static SafeAreaEdges GetEdges(IView view)
    {
        if (view is ISafeAreaElement element)
        {
            try
            {
                return SafeAreaElementExtensions.GetEffectiveSafeAreaEdges(element);
            }
            catch
            {
                // A custom element may throw while resolving its configuration; the platform
                // default is a safer answer than dropping the whole arrange pass.
            }
        }
        // Container: out of the bars/cutout, under the keyboard. Matches the platform default
        // for views without an explicit safe-area configuration.
        return SafeAreaEdges.Container;
    }

    /// <summary>
    /// Shrinks <paramref name="frame"/> by the part of the window insets it actually overlaps,
    /// for the edges the view asks to obey. Only the overlap is consumed, so a view that is
    /// already inside the avoid area (a nested layout, for example) is not padded twice.
    /// </summary>
    internal static Rect Pad(IView view, Rect frame, Rect windowBounds, Thickness insets)
    {
        if (IsEmpty(insets))
        {
            return frame;
        }
        SafeAreaEdges edges = GetEdges(view);
        double left = EdgeAmount(edges.Left, insets.Left, OverlapLeft(frame, insets.Left), isBottom: false);
        double top = EdgeAmount(edges.Top, insets.Top, OverlapTop(frame, insets.Top), isBottom: false);
        double right = EdgeAmount(edges.Right, insets.Right, OverlapRight(frame, windowBounds, insets.Right), isBottom: false);
        double bottom = EdgeAmount(edges.Bottom, insets.Bottom, OverlapBottom(frame, windowBounds, insets.Bottom), isBottom: true);
        if (left == 0 && top == 0 && right == 0 && bottom == 0)
        {
            return frame;
        }
        return new Rect(frame.X + left, frame.Y + top,
            Math.Max(0, frame.Width - left - right), Math.Max(0, frame.Height - top - bottom));
    }

    private static double EdgeAmount(SafeAreaRegions region, double inset, double overlap, bool isBottom)
    {
        if (inset <= 0 || overlap <= 0 || region == SafeAreaRegions.None)
        {
            return 0;
        }
        // SoftInput pads for the keyboard only; this slice has no keyboard insets, so the
        // avoid area is not applied on that edge.
        if (isBottom && region == SafeAreaRegions.SoftInput)
        {
            return 0;
        }
        return Math.Min(inset, overlap);
    }

    private static double OverlapLeft(Rect frame, double inset) =>
        Math.Clamp(inset - frame.X, 0, inset);

    private static double OverlapTop(Rect frame, double inset) =>
        frame.Y < 0 ? 0 : Math.Clamp(inset - frame.Y, 0, inset);

    private static double OverlapRight(Rect frame, Rect windowBounds, double inset)
    {
        double overflow = frame.X + frame.Width - (windowBounds.Width - inset);
        return overflow <= 0 ? 0 : Math.Min(overflow, inset);
    }

    private static double OverlapBottom(Rect frame, Rect windowBounds, double inset)
    {
        double overflow = frame.Y + frame.Height - (windowBounds.Height - inset);
        return overflow <= 0 ? 0 : Math.Min(overflow, inset);
    }
}
