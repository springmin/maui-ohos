// Compositor for OpenHarmony: measure/arrange a MAUI visual tree, draw it through the
// Microsoft.Maui.Graphics canvas and route taps to the handlers' platform views.
using System.Text;
using Microsoft.Maui.Graphics;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWindowRenderer
{
    private readonly MauiCanvas _canvas = new();

    public Color BackgroundColor { get; set; } = Colors.DarkSlateBlue;

    /// <summary>Measures and arranges the tree, then draws it when a surface is available.</summary>
    public bool Render(IView content, int width, int height)
    {
        content.Measure(width, height);
        content.Arrange(new Rect(0, 0, width, height));

        if (!HostCanvas.Begin(width, height))
        {
            // No surface yet (or the host refuses); the tree is still arranged.
            return false;
        }
        _canvas.FillColor = BackgroundColor;
        _canvas.FillRectangle(0, 0, width, height);
        DrawView(content);
        HostCanvas.Present();
        return true;
    }

    /// <summary>Child views of a view: layout children and content-view content (pages).</summary>
    private static IEnumerable<IView> ChildrenOf(IView view)
    {
        if (view is ILayout layout)
        {
            foreach (IView child in layout)
            {
                yield return child;
            }
        }
        if (view is IContentView contentView && contentView.PresentedContent is IView presented)
        {
            yield return presented;
        }
    }

    private void DrawView(IView view)
    {
        if (view.Visibility != Visibility.Visible)
        {
            return;
        }
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            platform.Draw(_canvas);
            if (platform.IsScrollView)
            {
                // Clip to the viewport and translate the content by the scroll offsets.
                _canvas.SaveState();
                _canvas.ClipRectangle(platform.Frame.X, platform.Frame.Y, platform.Frame.Width, platform.Frame.Height);
                _canvas.Translate(-platform.ScrollOffsetX, -platform.ScrollOffsetY);
                foreach (IView child in ChildrenOf(view))
                {
                    DrawView(child);
                }
                _canvas.RestoreState();
                return;
            }
        }
        foreach (IView child in ChildrenOf(view))
        {
            DrawView(child);
        }
    }

    /// <summary>
    /// Routes a touch through the MAUI tree (deepest interactive views first), so hit-testing
    /// does not depend on the platform handlers maintaining child lists.
    /// </summary>
    private OpenHarmonyView? _dragScrollTarget;
    private float _dragLastY;

    /// <summary>Handles a touch/mouse move: scrolls the scroll view captured on the last down.</summary>
    public bool HandleMove(float x, float y)
    {
        if (_dragScrollTarget is not { IsScrollView: true } scroll)
        {
            return false;
        }
        float delta = _dragLastY - y;
        _dragLastY = y;
        float maxOffset = Math.Max(0f, scroll.ScrollContentHeight - scroll.Frame.Height);
        scroll.ScrollOffsetY = Math.Clamp(scroll.ScrollOffsetY + delta, 0f, maxOffset);
        // Keep the virtual view in sync so apps can observe the scroll position.
        if (scroll.VirtualView is IScrollView virtualScroll)
        {
            virtualScroll.VerticalOffset = scroll.ScrollOffsetY;
        }
        return true;
    }

    public bool HandleTouch(IView root, bool down, bool up, float x, float y)
    {
        // The drag target is resolved once at the top level; the recursive walk must not
        // overwrite it as it descends into leaves.
        if (down)
        {
            _dragScrollTarget = FindScrollView(root, x, y);
            _dragLastY = y;
        }
        else if (up)
        {
            _dragScrollTarget = null;
        }
        return HandleTouchCore(root, down, up, x, y);
    }

    private bool HandleTouchCore(IView view, bool down, bool up, float x, float y)
    {
        bool handled = false;
        foreach (IView child in ChildrenOf(view))
        {
            handled |= HandleTouchCore(child, down, up, x, y);
        }
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            if (platform.Tap is not null)
            {
                handled |= platform.OnTouch(down, up, x, y);
            }
            // Scroll views consume touches inside them (drag scrolling).
            handled |= platform.IsScrollView && platform.Frame.Contains(x, y);
        }
        return handled;
    }

    /// <summary>Deepest scroll view containing the point (coordinates adjusted for offsets).</summary>
    private OpenHarmonyView? FindScrollView(IView view, float x, float y)
    {
        OpenHarmonyView? found = null;
        float localX = x;
        float localY = y;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            if (!platform.Frame.Contains(x, y))
            {
                return null;
            }
            if (platform.IsScrollView)
            {
                found = platform;
                localX += platform.ScrollOffsetX;
                localY += platform.ScrollOffsetY;
            }
        }
        foreach (IView child in ChildrenOf(view))
        {
            found = FindScrollView(child, localX, localY) ?? found;
        }
        return found;
    }

    /// <summary>Human-readable tree for logs/tests: type, text and arranged frame.</summary>
    public string Describe(IView view, int depth = 0)
    {
        var sb = new StringBuilder();
        string indent = new string(' ', depth * 2);
        Rect frame = view.Frame;
        OpenHarmonyView? platform = view.Handler?.PlatformView as OpenHarmonyView;
        sb.Append(indent)
          .Append(view.GetType().Name)
          .Append(" frame=").Append($"{frame.X:0},{frame.Y:0},{frame.Width:0}x{frame.Height:0}");
        if (platform?.ImageBytes is { Length: > 0 } bytes)
        {
            sb.Append($" image={bytes.Length}B");
        }
        if (platform is { IsScrollView: true })
        {
            sb.Append($" scroll={platform.ScrollOffsetX:0},{platform.ScrollOffsetY:0} content={platform.ScrollContentWidth:0}x{platform.ScrollContentHeight:0}");
        }
        if (platform is { IsTextEntry: true, IsFocused: true })
        {
            sb.Append(" focused");
        }
        if (view is ILabel label && !string.IsNullOrEmpty(label.Text))
        {
            sb.Append(" text='").Append(label.Text).Append('\'');
        }
        if (view is IText text && !string.IsNullOrEmpty(text.Text))
        {
            sb.Append(" text='").Append(text.Text).Append('\'');
        }
        sb.AppendLine();
        foreach (IView child in ChildrenOf(view))
        {
            sb.Append(Describe(child, depth + 1));
        }
        return sb.ToString();
    }
}
