// Pages and navigation pages have no platform layout in this slice, so arranging them directly
// is a no-op. This helper descends through a page's presented content (subtracting the
// navigation bar where applicable) until it reaches a real view, measures it and arranges it.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyContentArrange
{
    /// <summary>
    /// The view this thread's walk is arranging. A page with no presented content is placed
    /// through IView.Arrange, which calls the page handler's PlatformArrange; without the marker
    /// that call starts a second walk for the same page. That page has nothing to descend into,
    /// so the second walk arranges it again and recurses until the stack overflows (the depth
    /// guard cannot see the nested call: every PlatformArrange re-enters with depth 0).
    /// OpenHarmonyPageHandler consults the marker and lets the nested call just place the frame.
    /// </summary>
    [ThreadStatic]
    private static IView? s_arrangingView;

    /// <summary>True while this thread's walk is arranging <paramref name="view"/>.</summary>
    internal static bool IsArranging(IView view) => ReferenceEquals(s_arrangingView, view);

    /// <summary>Marks this thread's walk as arranging <paramref name="view"/>; returns the previous marker.</summary>
    internal static IView? BeginArrange(IView view)
    {
        IView? previous = s_arrangingView;
        s_arrangingView = view;
        return previous;
    }

    /// <summary>Restores the marker <see cref="BeginArrange"/> returned.</summary>
    internal static void EndArrange(IView? previous) => s_arrangingView = previous;

    public static void Arrange(IView view, Rect frame, int depth = 0)
    {
        if (depth > 4)
        {
            return;
        }
        IView? previous = BeginArrange(view);
        try
        {
            // Pages can appear after the tree was connected (tab switches, navigation): make sure
            // every view in the chain has its handler before it is measured.
            OpenHarmonyHandlerConnector.ConnectTree(view);
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
        finally
        {
            EndArrange(previous);
        }
    }
}
