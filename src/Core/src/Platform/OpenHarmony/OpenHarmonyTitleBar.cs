// Window.TitleBar row for the compositor.
//
// Window.TitleBar (Microsoft.Maui.Controls.TitleBar, ITitleBar in rc.1) is the window-level title
// bar: a logical child of the window, not part of the page tree the renderer walks. The row here
// owns that control for the slice: it measures and arranges the TitleBar (whose default
// ControlTemplate carries Icon/Title/Subtitle/LeadingContent/Content/TrailingContent), lets the
// compositor draw the arranged subtree, and routes touches into it (a tap on a template child
// reaches that child's handler exactly like a touch anywhere else in the tree).
//
// Back affordance ("返回位"): when the window's page tree can consume a back press, the row
// reserves the leading slot (the shell chrome's InBackButton geometry) and draws the same
// chevron there. A tap on the slot goes through IWindow.BackButtonClicked, so the app's own
// OnBackButtonPressed / modal / navigation handling decides; the leading slot wins over the
// TitleBar's own leading content, like the navigation bar's back region wins over its children.
//
// Visibility follows the TitleBar control: TitleBar.IsVisible = false hides the row and the
// window content fills the surface (the documented TitleBar contract).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

internal sealed class OpenHarmonyTitleBarRow
{
    /// <summary>Row height when the TitleBar does not ask for one (the slice's chrome height).</summary>
    internal const float DefaultHeight = 56f;

    /// <summary>Width of the leading back slot, matching the shell/navigation back region.</summary>
    internal const float BackButtonWidth = 56f;

    /// <summary>Width of one caption button at the trailing edge (system minimize/maximize/close).</summary>
    internal const float SystemButtonWidth = 46f;

    /// <summary>The caption buttons in trailing-to-leading draw/hit order (close outermost).</summary>
    private static readonly OpenHarmonyWindowDecorCommand[] s_systemButtons =
    {
        OpenHarmonyWindowDecorCommand.Close,
        OpenHarmonyWindowDecorCommand.ToggleMaximize,
        OpenHarmonyWindowDecorCommand.Minimize,
    };

    private OpenHarmonyWindowDecorCommand? _pressedDecorButton;

    private readonly IWindow _window;
    private readonly ITitleBar _titleBar;
    private readonly IView _view;

    internal OpenHarmonyTitleBarRow(IWindow window, ITitleBar titleBar)
    {
        _window = window;
        _titleBar = titleBar;
        _view = (IView)titleBar;
        // The template root (and with it title/subtitle/leading/content/trailing) is a logical
        // child of the TitleBar; the connector descends through IContentView.PresentedContent, so
        // every template view gets its slice handler before the row is arranged.
        OpenHarmonyHandlerConnector.ConnectTree((IElement)titleBar);
        BackTapped = () => window.BackButtonClicked();
    }

    /// <summary>The TitleBar control this row presents.</summary>
    internal ITitleBar VirtualView => _titleBar;

    /// <summary>The TitleBar view the compositor draws and hit-tests.</summary>
    internal IView View => _view;

    /// <summary>Row height: the TitleBar's HeightRequest when set, else the chrome height.</summary>
    internal float Height
        => _view is Microsoft.Maui.Controls.VisualElement element && element.HeightRequest > 0
            ? (float)element.HeightRequest
            : DefaultHeight;

    /// <summary>False when the app hid the TitleBar: the window content then fills the surface.</summary>
    internal bool IsVisible => _view.Visibility == Visibility.Visible;

    /// <summary>The row's arranged rectangle in window coordinates (empty until arranged).</summary>
    internal Rect Frame { get; private set; }

    /// <summary>
    /// True when the window's page tree can consume a back press; drives the chevron. Mirrors
    /// Microsoft.Maui.Controls.Window.CanConsumeBackNavigation, which is internal in rc.1.
    /// </summary>
    internal bool ShowsBack => OpenHarmonyBackNavigation.CanConsume(_window);

    /// <summary>
    /// A tap on the leading slot. The window handler seeds this with IWindow.BackButtonClicked;
    /// returning false (the window did not consume the press) lets the tap fall through to the
    /// TitleBar's own leading content.
    /// </summary>
    internal Func<bool> BackTapped { get; set; }

    /// <summary>Hit test for the leading back slot (the shell chrome's InBackButton geometry).</summary>
    internal bool InBackButton(float x, float y)
    {
        // The slot is the row's leading edge: physical left in LTR, right in RTL.
        bool rightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(_view);
        bool inSlot = rightToLeft
            ? x >= Frame.Right - BackButtonWidth && x <= Frame.Right
            : x >= Frame.X && x <= Frame.X + BackButtonWidth;
        return Frame.Width > 0 && inSlot &&
               y >= Frame.Y && y <= Frame.Y + Frame.Height;
    }

    /// <summary>True when the point falls inside the arranged row.</summary>
    internal bool Contains(float x, float y) => Frame.Width > 0 && Frame.Contains(x, y);

    /// <summary>
    /// True when the shell reports app-managed decorations (its decor sink is registered), so the
    /// row draws the system caption buttons; off-device and on a fullscreen phone window this is
    /// false and the app title bar keeps the whole band for its own content.
    /// </summary>
    internal bool ShowsSystemButtons => OpenHarmonyWindowDecoration.Available;

    /// <summary>
    /// The caption button at the point, or null outside them. The buttons claim the trailing edge
    /// (physical right in LTR, left in RTL, mirroring the platform caption corner): close is
    /// outermost, then the maximize/restore toggle, then minimize.
    /// </summary>
    internal OpenHarmonyWindowDecorCommand? HitSystemButton(float x, float y)
    {
        if (!ShowsSystemButtons || Frame.Width <= 0)
        {
            return null;
        }
        foreach (OpenHarmonyWindowDecorCommand command in s_systemButtons)
        {
            if (SystemButtonRect(command).Contains(x, y))
            {
                return command;
            }
        }
        return null;
    }

    /// <summary>Remembers the caption button the current press started on (dispatch happens on release).</summary>
    internal void PressDecorButton(OpenHarmonyWindowDecorCommand command) => _pressedDecorButton = command;

    /// <summary>
    /// Dispatches the pressed caption command when the release is still inside the same button
    /// (a release elsewhere cancels, like a native caption button).
    /// </summary>
    internal bool ReleaseDecorButton(float x, float y)
    {
        OpenHarmonyWindowDecorCommand? pressed = _pressedDecorButton;
        _pressedDecorButton = null;
        if (pressed is { } command && HitSystemButton(x, y) == command)
        {
            return OpenHarmonyWindowDecoration.Execute(command);
        }
        return false;
    }

    /// <summary>Drops a press without dispatching (a canceled gesture).</summary>
    internal void CancelDecorPress() => _pressedDecorButton = null;

    /// <summary>
    /// True when a press at the point should start the window move: inside the row and not on the
    /// leading back slot or a caption button. The renderer only asks after the TitleBar's own
    /// subtree declined the press, so interactive template content keeps its input.
    /// </summary>
    internal bool InDragRegion(float x, float y)
        => Contains(x, y) && !(ShowsBack && InBackButton(x, y)) && HitSystemButton(x, y) is null;

    /// <summary>Starts the window move through the shell (op 3; drag continues until the release).</summary>
    internal bool StartWindowDrag()
        => OpenHarmonyWindowDecoration.Execute(OpenHarmonyWindowDecorCommand.StartMoving);

    /// <summary>The caption button rectangle in canvas space (trailing edge; see HitSystemButton).</summary>
    private RectF SystemButtonRect(OpenHarmonyWindowDecorCommand command)
    {
        bool rightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(_view);
        int index = command switch
        {
            OpenHarmonyWindowDecorCommand.Close => 0,
            OpenHarmonyWindowDecorCommand.ToggleMaximize => 1,
            _ => 2,
        };
        float x = rightToLeft
            ? (float)Frame.X + index * SystemButtonWidth
            : (float)Frame.Right - (index + 1) * SystemButtonWidth;
        return new RectF(x, (float)Frame.Y, SystemButtonWidth, (float)Frame.Height);
    }

    /// <summary>Measures the TitleBar for the given width; the row height is fixed (see Height).</summary>
    internal void Measure(double width)
    {
        // The TitleBar's Content/LeadingContent/TrailingContent can change after the row was
        // built, and the template materializes them through the dispatcher; re-connect so every
        // newly presented view has its slice handler before the walk sees it. The bar's solid
        // background is mirrored onto the platform view as well: the compositor draws a view's
        // own Background, and a TitleBar's BackgroundColor/Brush has no generic mapper.
        OpenHarmonyHandlerConnector.ConnectTree((IElement)_titleBar);
        SyncBackground();
        _view.Measure(width, Height);
    }

    /// <summary>Mirrors the TitleBar's solid background so the row paints it (see Measure).</summary>
    private void SyncBackground()
    {
        if (_view.Handler?.PlatformView is not OpenHarmonyView platform ||
            _view is not Microsoft.Maui.Controls.VisualElement element)
        {
            return;
        }
        platform.Background = element.BackgroundColor ??
            (element.Background as Microsoft.Maui.Controls.SolidColorBrush)?.Color;
    }

    /// <summary>
    /// Arranges the TitleBar (and with it the template subtree) into the row frame. The row owns
    /// the frame: MAUI's arrange pipeline recomputes a view's frame from its current size and
    /// layout options (<c>ComputeFrame</c>), which would keep the TitleBar at its measured height
    /// centred in the row, so the frame is forced and the handler's platform arrange lays the
    /// template subtree out inside it.
    /// </summary>
    internal void Arrange(Rect frame)
    {
        Frame = frame;
        _view.Frame = frame;
        if (_view.Handler is IViewHandler handler)
        {
            handler.PlatformArrange(frame);
        }
        // The template root fills the row too: MAUI's arrange chain can shrink a layout to its
        // measured width (ComputeFrame honours the current frame), which would leave the
        // TitleBar's background short of the row; the root is forced and re-arranged by its
        // handler so the template columns lay out across the full row.
        if ((_titleBar as IContentView)?.PresentedContent is { } root &&
            root.Handler is IViewHandler rootHandler)
        {
            root.Frame = frame;
            rootHandler.PlatformArrange(frame);
        }
    }

    /// <summary>
    /// Draws the window back affordance over the arranged row: the shell chrome's chevron in the
    /// leading slot, in the TitleBar's foreground colour when it has one. Drawn after the
    /// TitleBar subtree so the window affordance stays visible over app leading content.
    /// </summary>
    internal void DrawBackAffordance(MauiCanvas canvas)
    {
        if (!ShowsBack)
        {
            return;
        }
        // The chevron sits in the row's leading slot: physical left in LTR, right in RTL.
        bool rightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(_view);
        float cx = rightToLeft ? (float)Frame.Right - 26f : (float)Frame.X + 26f;
        float cy = (float)(Frame.Y + Frame.Height / 2);
        canvas.StrokeColor = (_titleBar as Microsoft.Maui.Controls.TitleBar)?.ForegroundColor ?? Colors.White;
        canvas.StrokeSize = 3;
        if (rightToLeft)
        {
            canvas.DrawLine(cx - 9, cy - 11, cx + 4, cy);
            canvas.DrawLine(cx + 4, cy, cx - 9, cy + 11);
        }
        else
        {
            canvas.DrawLine(cx + 9, cy - 11, cx - 4, cy);
            canvas.DrawLine(cx - 4, cy, cx + 9, cy + 11);
        }
    }

    /// <summary>
    /// Draws the system caption buttons over the arranged row when the shell reports app-managed
    /// decorations: minimize (a bar), maximize/restore (the shell decides the toggle; the glyph is
    /// the maximize square) and close (an X), at the trailing edge. Drawn after the TitleBar
    /// subtree, like the back affordance.
    /// </summary>
    internal void DrawSystemButtons(MauiCanvas canvas)
    {
        if (!ShowsSystemButtons)
        {
            return;
        }
        canvas.StrokeColor = (_titleBar as Microsoft.Maui.Controls.TitleBar)?.ForegroundColor ?? Colors.White;
        canvas.StrokeSize = 1.6f;
        foreach (OpenHarmonyWindowDecorCommand command in s_systemButtons)
        {
            RectF rect = SystemButtonRect(command);
            float cx = rect.X + rect.Width / 2;
            float cy = rect.Y + rect.Height / 2;
            switch (command)
            {
                case OpenHarmonyWindowDecorCommand.Minimize:
                    canvas.DrawLine(cx - 6, cy, cx + 6, cy);
                    break;
                case OpenHarmonyWindowDecorCommand.ToggleMaximize:
                    // A stroked square (four lines): the headless rasterizer's DrawRectangle
                    // approximation fills with FillColor, the stroke paths stay colour-true.
                    canvas.DrawLine(cx - 6, cy - 6, cx + 6, cy - 6);
                    canvas.DrawLine(cx + 6, cy - 6, cx + 6, cy + 6);
                    canvas.DrawLine(cx + 6, cy + 6, cx - 6, cy + 6);
                    canvas.DrawLine(cx - 6, cy + 6, cx - 6, cy - 6);
                    break;
                default:
                    canvas.DrawLine(cx - 6, cy - 6, cx + 6, cy + 6);
                    canvas.DrawLine(cx + 6, cy - 6, cx - 6, cy + 6);
                    break;
            }
        }
    }
}

/// <summary>
/// Back-navigation state for the window row: the slice's mirror of
/// Microsoft.Maui.Controls.Window.CanConsumeBackNavigation (internal in rc.1), which decides
/// only whether the affordance is shown and active. The press itself always goes through
/// IWindow.BackButtonClicked, so a custom OnBackButtonPressed override still wins.
/// </summary>
internal static class OpenHarmonyBackNavigation
{
    internal static bool CanConsume(IWindow window)
    {
        if (window is not Microsoft.Maui.Controls.Window controlsWindow)
        {
            return false;
        }
        if (controlsWindow.Navigation.ModalStack.Count > 0)
        {
            return true;
        }
        return CanConsume(controlsWindow.Page);
    }

    private static bool CanConsume(Page? page) => page switch
    {
        Shell shell => CanConsume(shell.CurrentPage) ||
            (shell.FlyoutIsPresented && shell.FlyoutBehavior == FlyoutBehavior.Flyout) ||
            shell.CurrentItem?.CurrentItem?.Stack.Count > 1,
        NavigationPage navigation => CanConsume(navigation.CurrentPage) ||
            navigation.Navigation.NavigationStack.Count > 1,
        FlyoutPage flyout => flyout.IsPresented && CanConsume(flyout.Detail),
        MultiPage<Page> multiPage => CanConsume(multiPage.CurrentPage),
        _ => false,
    };
}
