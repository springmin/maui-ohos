// Pages and navigation pages have no platform layout in this slice, so arranging them directly
// is a no-op. This helper descends through a page's presented content (subtracting the
// navigation bar where applicable) until it reaches a real view, measures it and arranges it.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyContentArrange
{
    public static void Arrange(IView view, Rect frame, int depth = 0)
    {
        if (depth > 4)
        {
            return;
        }
        if (view is NavigationPage navigation)
        {
            // Containers keep a frame of their own: hit-testing walks the parent chain.
            view.Frame = frame;
            // The current page lives below the navigation bar.
            double barHeight = (view.Handler?.PlatformView as OpenHarmonyView)?.NavBarHeight ?? 48f;
            var contentFrame = new Rect(frame.X, frame.Y + barHeight, frame.Width, Math.Max(0, frame.Height - barHeight));
            if (navigation.CurrentPage is IView currentPage)
            {
                Arrange(currentPage, contentFrame, depth + 1);
                return;
            }
        }
        if (view is Page && (view as IContentView)?.PresentedContent is IView pageContent)
        {
            view.Frame = frame;
            Arrange(pageContent, frame, depth + 1);
            return;
        }
        view.Measure(frame.Width, frame.Height);
        view.Arrange(frame);
    }
}
