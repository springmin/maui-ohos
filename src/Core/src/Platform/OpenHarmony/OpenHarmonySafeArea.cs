// Safe-area handling for the slice.
//
// The ArkTS shell owns the window and reports its avoid area (status bar, navigation bar,
// display cutout) through ohos_host_get_avoid_area, which OpenHarmonyBridge.TryGetAvoidArea
// wraps. Those insets describe the window, not the app surface: the app host arranges the
// surface at its full bounds and each page/content view decides per edge, through MAUI's
// cross-platform SafeAreaEdges model, whether its content stays out of the avoid area
// (SafeAreaRegions.None means edge to edge, Container/All/Default keep out of the bars and the
// cutout, SoftInput pads for the keyboard). ISafeAreaView is documented as iOS/Mac
// Catalyst-only, so a view that does not implement ISafeAreaElement falls back to the platform
// default (Container); that keeps the historical look for container pages such as
// NavigationPage/Shell/FlyoutPage, whose chrome (the navigation bar) must stay out of the
// status bar. When the shell reports no avoid area, or the host library is absent (the
// off-device verification harness), every inset is zero and the arrangement is exactly the
// historical one.
//
// Soft keyboard (SoftInput edges): the shell subscribes to the window's avoid-area change for
// the keyboard (window.on('avoidAreaChange') for AvoidAreaType.TYPE_KEYBOARD) and pushes its
// height through host.notifySoftInputArea -> ohos_host_set_soft_input_area, which this file
// reads back through ohos_host_get_soft_input_area (the SoftInputInset getter below) and also
// receives as a change callback (ohos_host_register_soft_input_change): the callback asks the
// app host for a redraw, so the keyboard's avoid-area change re-runs the layout by itself. The
// keyboard overlaps from the bottom only, so the value pads the bottom edge: SoftInput consumes
// the keyboard inset instead of the system bottom bar, and All consumes whichever of the two
// reaches deeper (no double padding). Container/Default keep the system-bar behaviour only. The
// system avoid area itself (GetWindowInsets) is unchanged.
using System.Runtime.InteropServices;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonySafeArea
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Keyboard height in pixels; the host clamps negatives, and a missing export (an
    /// older host library or the off-device harness) reports 0 after the first probe.</summary>
    [DllImport(HostLibrary, EntryPoint = "ohos_host_get_soft_input_area")]
    private static extern int GetSoftInputAreaNative(out int bottom);

    private static bool s_softInputAvailable = true;
    private static bool s_softInputProbed;
    private static bool s_softInputCallbackRegistered;
    private static SoftInputChanged? s_softInputChangedThunk;

    /// <summary>Managed form of the host's soft-input change callback: void (*)(int bottom).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SoftInputChanged(int bottom);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_register_soft_input_change")]
    private static extern void RegisterSoftInputChangeNative(IntPtr callback);

    /// <summary>Window avoid area in pixels; zero when the shell has no answer.</summary>
    internal static Thickness GetWindowInsets()
    {
        if (OpenHarmonyBridge.TryGetAvoidArea(out int top, out int bottom, out int left, out int right))
        {
            return new Thickness(Math.Max(0, left), Math.Max(0, top), Math.Max(0, right), Math.Max(0, bottom));
        }
        return Thickness.Zero;
    }

    /// <summary>
    /// Registers the host's soft-input change callback once (best effort; off-device the host
    /// library is absent). The callback asks the app host for a redraw, which re-measures and
    /// re-arranges the tree, so the keyboard's avoid-area change re-applies the safe-area
    /// padding without waiting for another input event.
    /// </summary>
    private static void EnsureSoftInputCallback()
    {
        if (s_softInputCallbackRegistered)
        {
            return;
        }
        s_softInputCallbackRegistered = true;
        s_softInputChangedThunk = OnSoftInputChangedNative;
        try
        {
            RegisterSoftInputChangeNative(Marshal.GetFunctionPointerForDelegate(s_softInputChangedThunk));
        }
        catch (Exception)
        {
            // No native host (the off-device harness): only the pull path is available.
        }
    }

    private static void OnSoftInputChangedNative(int bottom)
    {
        // The keyboard height changed: a redraw re-runs Measure/Arrange, which reads the new
        // inset through GetSoftInputInset.
        OpenHarmonyBridge.RequestRedraw();
    }

    /// <summary>
    /// Keyboard inset in pixels (0 when the shell reports no keyboard, the host library is
    /// absent or the export is missing). Read per arrange like the avoid area; the failed
    /// binding is only probed once.
    /// </summary>
    internal static int GetSoftInputInset()
    {
        EnsureSoftInputCallback();
        if (s_softInputProbed && !s_softInputAvailable)
        {
            return 0;
        }
        try
        {
            s_softInputProbed = true;
            return GetSoftInputAreaNative(out int bottom) == 1 ? Math.Max(0, bottom) : 0;
        }
        catch (DllNotFoundException)
        {
            // No native host (tests): the soft-input inset stays zero.
            s_softInputAvailable = false;
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            // An older host library without the soft-input export.
            s_softInputAvailable = false;
            return 0;
        }
    }

    internal static bool HasSoftInput() => GetSoftInputInset() > 0;

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
    /// already inside the avoid area (a nested layout, for example) is not padded twice. The
    /// keyboard inset (see GetSoftInputInset) pads the bottom edge for SoftInput/All.
    /// </summary>
    internal static Rect Pad(IView view, Rect frame, Rect windowBounds, Thickness insets)
    {
        int softInput = GetSoftInputInset();
        if (IsEmpty(insets) && softInput <= 0)
        {
            return frame;
        }
        SafeAreaEdges edges = GetEdges(view);
        double left = EdgeAmount(edges.Left, insets.Left, OverlapLeft(frame, insets.Left), isBottom: false, softInput, 0);
        double top = EdgeAmount(edges.Top, insets.Top, OverlapTop(frame, insets.Top), isBottom: false, softInput, 0);
        double right = EdgeAmount(edges.Right, insets.Right, OverlapRight(frame, windowBounds, insets.Right), isBottom: false, softInput, 0);
        // The keyboard is a second, independent bottom inset: it overlaps the frame on its own,
        // so the soft-input amount is clamped by its own overlap (the system overlap may be 0).
        double softOverlap = softInput > 0 ? OverlapBottom(frame, windowBounds, softInput) : 0;
        double bottom = EdgeAmount(edges.Bottom, insets.Bottom, OverlapBottom(frame, windowBounds, insets.Bottom), isBottom: true, softInput, softOverlap);
        if (left == 0 && top == 0 && right == 0 && bottom == 0)
        {
            return frame;
        }
        return new Rect(frame.X + left, frame.Y + top,
            Math.Max(0, frame.Width - left - right), Math.Max(0, frame.Height - top - bottom));
    }

    private static double EdgeAmount(SafeAreaRegions region, double inset, double overlap, bool isBottom, int softInput, double softOverlap)
    {
        if (region == SafeAreaRegions.None || (overlap <= 0 && softOverlap <= 0))
        {
            return 0;
        }
        // SoftInput pads for the keyboard only: it never consumes the system bars, and a hidden
        // keyboard (no reported height) applies nothing.
        if (isBottom && region == SafeAreaRegions.SoftInput)
        {
            return softOverlap > 0 ? Math.Min(softInput, softOverlap) : 0;
        }
        double amount = inset > 0 && overlap > 0 ? Math.Min(inset, overlap) : 0;
        if (isBottom && region == SafeAreaRegions.All && softOverlap > 0 && softInput > amount)
        {
            // All covers the keyboard as well; the deeper of the two bottom insets wins so the
            // content clears both without being padded twice.
            amount = Math.Min(softInput, softOverlap);
        }
        return amount;
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
