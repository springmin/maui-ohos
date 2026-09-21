// Page-aware arrangement for the safe-area model (see OpenHarmonySafeArea): walk the page
// chain the same way OpenHarmonyContentArrange does, but pad each page/content view by the part
// of the window's avoid area it asks to obey instead of insetting the whole surface. Only the
// first content view of a page consumes the insets; the views below are arranged inside the
// padded frame, so they are not padded again. With no reported avoid area the arrangement is
// delegated to OpenHarmonyContentArrange unchanged.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonySafeAreaArrange
{
    private const int MaxDepth = 4;

    /// <summary>
    /// Arranges <paramref name="view"/> into <paramref name="frame"/> (window coordinates) with
    /// the window insets applied per view. <paramref name="windowBounds"/> is the full surface,
    /// used for the right/bottom overlap checks.
    /// </summary>
    internal static void Arrange(IView view, Rect frame, Rect windowBounds, Thickness insets, int depth = 0)
    {
        if (OpenHarmonySafeArea.IsEmpty(insets))
        {
            OpenHarmonyContentArrange.Arrange(view, frame, depth);
            return;
        }
        if (depth > MaxDepth)
        {
            return;
        }
        OpenHarmonyHandlerConnector.ConnectTree(view);

        if (view is NavigationPage navigation)
        {
            // Container pages have no SafeAreaEdges in the MAUI model; keep the historical look
            // (navigation bar and content out of the avoid area) by padding the whole page.
            Rect pageFrame = OpenHarmonySafeArea.Pad(view, frame, windowBounds, insets);
            view.Frame = pageFrame;
            double barHeight = (view.Handler?.PlatformView as OpenHarmonyView)?.NavBarHeight ?? 48f;
            var contentFrame = new Rect(pageFrame.X, pageFrame.Y + barHeight,
                pageFrame.Width, Math.Max(0, pageFrame.Height - barHeight));
            if (navigation.CurrentPage is IView currentPage)
            {
                Arrange(currentPage, contentFrame, windowBounds, insets, depth + 1);
            }
            return;
        }

        if (view is Page && (view as IContentView)?.PresentedContent is IView pageContent)
        {
            // A page fills the surface; its edges decide whether the content is padded, so a
            // page that ignores the safe area still paints edge to edge.
            view.Frame = frame;
            Rect contentFrame = OpenHarmonySafeArea.Pad(view, frame, windowBounds, insets);
            Arrange(pageContent, contentFrame, windowBounds, insets, depth + 1);
            return;
        }

        if (view is ILayout || view is IContentView)
        {
            // The content view that overlaps the avoid area consumes it; MAUI then arranges its
            // children inside the padded frame.
            Rect padded = OpenHarmonySafeArea.Pad(view, frame, windowBounds, insets);
            view.Measure(padded.Width, padded.Height);
            view.Arrange(padded);
            return;
        }

        view.Measure(frame.Width, frame.Height);
        view.Arrange(frame);
    }
}
