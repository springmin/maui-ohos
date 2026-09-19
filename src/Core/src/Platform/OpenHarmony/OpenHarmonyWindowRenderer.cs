// Compositor for OpenHarmony: measure/arrange a MAUI visual tree, draw it through the
// Microsoft.Maui.Graphics canvas and route taps to the handlers' platform views.
using System.Text;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWindowRenderer
{
    /// <summary>Canvas factory: tests substitute a managed rasterizer for pixel assertions.</summary>
    public static Func<MauiCanvas>? CanvasFactory { get; set; }

    /// <summary>Surface hooks: tests report a virtual surface so rendering runs without a device.</summary>
    public static Func<int, int, bool>? SurfaceBegin { get; set; }

    public static Action? SurfacePresent { get; set; }

    private readonly MauiCanvas _canvas;

    public OpenHarmonyWindowRenderer()
    {
        _canvas = CanvasFactory?.Invoke() ?? new MauiCanvas();
    }

    public Color BackgroundColor { get; set; } = Colors.DarkSlateBlue;

    /// <summary>Measures and arranges the tree, then draws it when a surface is available.</summary>
    public bool Render(IView content, int width, int height)
    {
        content.Measure(width, height);
        content.Arrange(new Rect(0, 0, width, height));

        bool surfaceReady = SurfaceBegin is not null ? SurfaceBegin(width, height) : HostCanvas.Begin(width, height);
        if (!surfaceReady)
        {
            // No surface yet (or the host refuses); the tree is still arranged.
            return false;
        }
        _canvas.FillColor = BackgroundColor;
        _canvas.FillRectangle(0, 0, width, height);
        _popupView = null;
        _flyoutPanelView = null;
        _carouselViews.Clear();
        DrawView(content);
        foreach (OpenHarmonyView carousel in _carouselViews)
        {
            DrawCarouselIndicator(carousel);
        }
        if (_popupView is { PopupVisible: true } popup)
        {
            // Dropdowns float above the rest of the tree.
            popup.DrawPopup(_canvas);
        }
        OpenHarmonyAlertHost.SetSurface(width, height);
        DrawAlertOverlay();
        OpenHarmonyDiagnostics.Reset();
        OpenHarmonyAccessibility.Refresh(content);
        OpenHarmonyAccessibility.Publish();
        if (OpenHarmonyDiagnostics.Enabled)
        {
            DrawDiagnosticsOverlay(content);
        }
        if (SurfacePresent is not null)
        {
            SurfacePresent();
        }
        else
        {
            HostCanvas.Present();
        }
        return true;
    }

    /// <summary>Outlines every view of the tree with its type name (visual diagnostics).</summary>
    private void DrawDiagnosticsOverlay(IView content)
    {
        _canvas.StrokeColor = Colors.Magenta;
        _canvas.StrokeSize = 2;
        _canvas.FontColor = Colors.Yellow;
        _canvas.FontSize = 18;
        DrawDiagnosticsFor(content);
    }

    private void DrawDiagnosticsFor(IView view)
    {
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            RectF frame = platform.Frame;
            if (frame.Width > 0 && frame.Height > 0)
            {
                _canvas.DrawRectangle(frame.X, frame.Y, frame.Width, frame.Height);
                _canvas.DrawString(view.GetType().Name, frame.X + 4, frame.Y + 2, Math.Max(40, frame.Width - 8), 20,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
                OpenHarmonyDiagnostics.Count();
            }
        }
        foreach (IView child in ChildrenOf(view))
        {
            DrawDiagnosticsFor(child);
        }
    }

    /// <summary>Draws the alert overlay (scrim, dialog box, buttons) when one is open.</summary>
    private void DrawAlertOverlay()
    {
        if (OpenHarmonyAlertHost.Current is not { } alert)
        {
            return;
        }
        _canvas.FillColor = Colors.Black.WithAlpha(0.55f);
        _canvas.FillRectangle(0, 0, (float)OpenHarmonyAlertHost.Width, (float)OpenHarmonyAlertHost.Height);
        RectF box = OpenHarmonyAlertHost.BoxRect;
        _canvas.FillColor = Colors.DimGray;
        _canvas.FillRoundedRectangle(box.X, box.Y, box.Width, box.Height, 12);
        _canvas.FontColor = Colors.White;
        _canvas.FontSize = 30;
        _canvas.DrawString(alert.Title ?? string.Empty, box.X + 20, box.Y + 8, box.Width - 40, 48,
            HorizontalAlignment.Left, VerticalAlignment.Center);
        _canvas.FontSize = 24;
        _canvas.DrawString(alert.Message ?? string.Empty, box.X + 20, box.Y + 60, box.Width - 40, box.Height - 140,
            HorizontalAlignment.Left, VerticalAlignment.Top);
        _canvas.FontSize = 26;
        if (alert.Kind == OpenHarmonyAlertKind.ActionSheet)
        {
            for (int i = 0; i < alert.Options.Count; i++)
            {
                RectF row = OpenHarmonyAlertHost.OptionRect(i);
                _canvas.FillColor = i == alert.Options.Count - 1 ? Colors.Gray : Colors.DodgerBlue;
                _canvas.FillRoundedRectangle(row.X + 12, row.Y + 4, row.Width - 24, row.Height - 8, 8);
                _canvas.FontColor = Colors.White;
                _canvas.DrawString(alert.Options[i], row.X, row.Y, row.Width, row.Height,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
            }
            return;
        }
        if (alert.Kind == OpenHarmonyAlertKind.Prompt)
        {
            RectF field = OpenHarmonyAlertHost.BoxRect;
            _canvas.FillColor = Colors.Black;
            _canvas.FillRoundedRectangle(field.X + 20, field.Y + 110, field.Width - 40, 56, 8);
            _canvas.FontColor = Colors.White;
            _canvas.DrawString(alert.PromptText, field.X + 32, field.Y + 110, field.Width - 64, 56,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        if (!string.IsNullOrEmpty(alert.Accept))
        {
            RectF accept = OpenHarmonyAlertHost.AcceptRect;
            _canvas.FillColor = Colors.DodgerBlue;
            _canvas.FillRoundedRectangle(accept.X, accept.Y, accept.Width, accept.Height, 8);
            _canvas.FontColor = Colors.White;
            _canvas.DrawString(alert.Accept!, accept.X, accept.Y, accept.Width, accept.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        if (!string.IsNullOrEmpty(alert.Cancel))
        {
            RectF cancel = OpenHarmonyAlertHost.CancelRect;
            _canvas.FillColor = Colors.Gray;
            _canvas.FillRoundedRectangle(cancel.X, cancel.Y, cancel.Width, cancel.Height, 8);
            _canvas.FontColor = Colors.White;
            _canvas.DrawString(alert.Cancel!, cancel.X, cancel.Y, cancel.Width, cancel.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
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
        IView? presentedContent = (view as IContentView)?.PresentedContent as IView;
        if (presentedContent is not null)
        {
            yield return presentedContent;
        }
        // The current page of a navigation page is what is visible; avoid double-yielding when
        // it is also the presented content.
        if (view is Microsoft.Maui.Controls.NavigationPage navigation &&
            navigation.CurrentPage is IView currentPage &&
            !ReferenceEquals(currentPage, presentedContent))
        {
            yield return currentPage;
        }
        // Flyout pages: the detail is always visible, the flyout only while presented.
        if (view is Microsoft.Maui.Controls.FlyoutPage flyoutPage)
        {
            if (flyoutPage.Detail is IView flyoutDetail)
            {
                yield return flyoutDetail;
            }
            if (flyoutPage.IsPresented && flyoutPage.Flyout is IView flyoutContent)
            {
                yield return flyoutContent;
            }
        }
        // Platform-owned children (collection view items) are part of the rendered tree.
        if (view.Handler?.PlatformView is OpenHarmonyView { ViewChildren.Count: > 0 } platform)
        {
            foreach (IView child in platform.ViewChildren)
            {
                yield return child;
            }
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
            // View transforms (animations set these): opacity, translation, scale, rotation.
            if (platform.ShowsTitleBar)
            {
                // Shell navigation happens in managed code without a platform callback, so the
                // chrome is re-synced whenever it is drawn.
                platform.ChromeRefresh?.Invoke();
            }
            bool dimmed = !view.IsEnabled ||
                          (view as Microsoft.Maui.Controls.VisualElement)?.IsEnabled == false;
            platform.Dimmed = dimmed;
            bool transformed = view.Opacity < 1.0 || view.TranslationX != 0 || view.TranslationY != 0 ||
                               view.Scale != 1.0 || view.Rotation != 0;
            if (transformed)
            {
                _canvas.SaveState();
                double effectiveOpacity = view.Opacity;
                if (view is Microsoft.Maui.Controls.Button)
                {
                }
                _canvas.Alpha = (float)Math.Clamp(effectiveOpacity, 0, 1);
                if (view.TranslationX != 0 || view.TranslationY != 0)
                {
                    _canvas.Translate((float)view.TranslationX, (float)view.TranslationY);
                }
                if (view.Rotation != 0 || view.Scale != 1.0)
                {
                    RectF frame = platform.Frame;
                    _canvas.Rotate((float)view.Rotation, frame.Center.X, frame.Center.Y);
                    if (view.Scale != 1.0)
                    {
                        // ICanvas.Scale has no centre overload: translate around the centre.
                        _canvas.Translate(frame.Center.X, frame.Center.Y);
                        _canvas.Scale((float)view.Scale, (float)view.Scale);
                        _canvas.Translate(-frame.Center.X, -frame.Center.Y);
                    }
                }
            }
            platform.Draw(_canvas);
            if (platform.PopupVisible)
            {
                // Drawn last so the dropdown floats above the rest of the tree.
                _popupView = platform;
            }
            if (platform.FlyoutOpen)
            {
                _flyoutPanelView = platform;
            }
            if (view is Microsoft.Maui.Controls.CarouselView)
            {
                _carouselViews.Add(platform);
            }
            if (platform.IsFlyoutPage)
            {
                // Detail fills the window; the flyout is an overlay clipped to its panel.
                IReadOnlyList<IView> flyoutChildren = ChildrenOf(view).ToList();
                if (flyoutChildren.Count > 0)
                {
                    DrawView(flyoutChildren[0]);
                }
                if (platform.FlyoutPresented && flyoutChildren.Count > 1)
                {
                    RectF flyoutFrame = platform.Frame;
                    _canvas.FillColor = Colors.Black.WithAlpha(0.5f);
                    _canvas.FillRectangle(flyoutFrame.X, flyoutFrame.Y, flyoutFrame.Width, flyoutFrame.Height);
                    _canvas.SaveState();
                    _canvas.ClipRectangle(flyoutFrame.X, flyoutFrame.Y, platform.FlyoutWidth, flyoutFrame.Height);
                    DrawView(flyoutChildren[1]);
                    _canvas.RestoreState();
                }
                return;
            }
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
    private OpenHarmonyView? _dragSliderTarget;
    private float _dragLastY;
    private IView? _panTarget;
    private OpenHarmonyView? _swipeTarget;
    private int _panGestureId = -1;
    private float _panStartX;
    private float _panStartY;
    private float _downX;
    private float _downY;
    private bool _moved;
    private OpenHarmonyView? _popupView;
    private OpenHarmonyView? _flyoutPanelView;
    private readonly List<OpenHarmonyView> _carouselViews = new();
    private OpenHarmonyView? _textDragTarget;
    private int _textDragAnchor;

    /// <summary>True while any view wants continuous redraws (activity indicators).</summary>
    public bool HasAnimations(IView? root) => TreeHasAnimations(root);

    /// <summary>Page indicator dots for a carousel.</summary>
    private void DrawCarouselIndicator(OpenHarmonyView carousel)
    {
        if (carousel.VirtualView is not Microsoft.Maui.Controls.CarouselView view || view.ItemsSource is not { } source)
        {
            return;
        }
        int count = source.Cast<object?>().Count();
        if (count <= 1)
        {
            return;
        }
        RectF frame = carousel.Frame;
        float spacing = 16f;
        float startX = frame.X + (frame.Width - count * spacing) / 2f + spacing / 2f;
        float y = frame.Y + frame.Height - 16f;
        for (int i = 0; i < count; i++)
        {
            _canvas.FillColor = i == view.Position ? Colors.White : Colors.Gray;
            _canvas.FillCircle(startX + i * spacing, y, i == view.Position ? 6f : 4f);
        }
    }

    private static void ApplyTextSelection(OpenHarmonyView entry, int index)
    {
        // The drag anchor is the cursor position when the press started.
        int anchor = entry.TextAnchor >= 0 ? entry.TextAnchor : index;
        int length = Math.Abs(index - anchor);
        // MAUI semantics: the caret sits at the end of the selection and SelectionLength spans
        // the range (Entry clamps CursorPosition to be at least SelectionLength).
        int caret = Math.Max(index, anchor);
        entry.TextAnchor = anchor;
        entry.CursorPosition = caret;
        entry.SelectionLength = length;
        if (entry.VirtualView is Microsoft.Maui.Controls.Entry control)
        {
            control.CursorPosition = caret;
            control.SelectionLength = length;
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    private static bool TreeHasAnimations(IView? view)
    {
        if (view is null || view.Visibility != Visibility.Visible)
        {
            return false;
        }
        if (view.Handler?.PlatformView is OpenHarmonyView { NeedsAnimation: true })
        {
            return true;
        }
        foreach (IView child in ChildrenOf(view))
        {
            if (TreeHasAnimations(child))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Handles a touch/mouse move: drags the slider or scrolls the scroll view captured on down.</summary>
    public bool HandleMove(float x, float y)
    {
        // Touch slop: a drag must not end up as a tap/selection.
        if (!_moved && (Math.Abs(x - _downX) > 8 || Math.Abs(y - _downY) > 8))
        {
            _moved = true;
        }
        if (_dragSliderTarget is { IsSlider: true } slider)
        {
            float fraction = slider.SliderValueFromX(x);
            slider.SliderDrag?.Invoke(fraction, false);
            return true;
        }
        if (_textDragTarget is { IsTextEntry: true } textEntry)
        {
            // Selection extension is approximate (proportional text metrics); keep the caret
            // stable when the estimate does not move.
            int index = textEntry.CursorIndexFromX(x);
            if (index != textEntry.CursorPosition)
            {
                ApplyTextSelection(textEntry, index);
            }
            return true;
        }
        if (_panTarget is { } panTarget)
        {
            OpenHarmonyGestures.SendPan(panTarget, x - _panStartX, y - _panStartY, _panGestureId);
            return true;
        }
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
        scroll.ScrollOffsetChanged?.Invoke();
        return true;
    }

    public bool HandleTouch(IView root, bool down, bool up, float x, float y)
    {
        // An open alert owns all touches until a button is chosen.
        if (OpenHarmonyAlertHost.Current is { } alertState)
        {
            if (down)
            {
                return true;
            }
            if (up)
            {
                int optionIndex = OpenHarmonyAlertHost.OptionIndexAt(x, y);
                if (optionIndex >= 0 && alertState.Options.Count > optionIndex)
                {
                    OpenHarmonyAlertHost.Hide();
                    alertState.CompleteOption?.Invoke(alertState.Options[optionIndex]);
                    return true;
                }
                RectF accept = OpenHarmonyAlertHost.AcceptRect;
                RectF cancel = OpenHarmonyAlertHost.CancelRect;
                // Complete first: the prompt reads its text from the (still current) state.
                if (accept.Contains(x, y))
                {
                    alertState.Complete(true);
                    OpenHarmonyAlertHost.Hide();
                }
                else if (cancel.Contains(x, y))
                {
                    alertState.Complete(false);
                    OpenHarmonyAlertHost.Hide();
                }
                return true;
            }
        }

        // Pointer gestures (pointer recognizers receive enter/press on touch down, release/exit on up).
        if (down || up)
        {
            if (FindPointerTarget(root, x, y) is { } pointerView)
            {
                if (up)
                {
                    OpenHarmonyPointer.Dispatch(pointerView, OpenHarmonyPointer.Kind.Released, x, y);
                    OpenHarmonyPointer.Dispatch(pointerView, OpenHarmonyPointer.Kind.Exited, x, y);
                }
                else
                {
                    OpenHarmonyPointer.Dispatch(pointerView, OpenHarmonyPointer.Kind.Entered, x, y);
                    OpenHarmonyPointer.Dispatch(pointerView, OpenHarmonyPointer.Kind.Pressed, x, y);
                }
            }
        }

        // An open dropdown owns all touches until it is used or dismissed. The popup is found
        // in the tree (it must also work when nothing has been drawn yet, e.g. in tests).
        if (_popupView is not { PopupVisible: true })
        {
            _popupView = FindOpenPopup(root);
        }
        // Shell flyout panels behave the same way.
        if (_flyoutPanelView is not { FlyoutOpen: true })
        {
            _flyoutPanelView = FindOpenFlyout(root);
        }
        if (_flyoutPanelView is { FlyoutOpen: true } flyoutPanel)
        {
            if (down || flyoutPanel.InHamburger(x, y))
            {
                // Consume the press (and the release of the tap that opened the drawer).
                return true;
            }
            if (up)
            {
                int index = flyoutPanel.FlyoutItemAt(x, y);
                if (index >= 0)
                {
                    flyoutPanel.FlyoutSelect?.Invoke(index);
                }
                else
                {
                    flyoutPanel.FlyoutOpen = false;
                    OpenHarmonyBridge.RequestRedraw();
                }
            }
            return true;
        }
        if (_popupView is { PopupVisible: true } popup)
        {
            if (down)
            {
                _downX = x;
                _downY = y;
                _moved = false;
                return true;
            }
            if (up && !_moved)
            {
                if (popup.IsCalendar)
                {
                    int hit = popup.CalendarHit(x, y);
                    if (hit == -2)
                    {
                        popup.CalendarPreviousMonth?.Invoke();
                    }
                    else if (hit == -3)
                    {
                        popup.CalendarNextMonth?.Invoke();
                    }
                    else if (hit >= 1)
                    {
                        popup.CalendarSelectDay?.Invoke(new DateTime(popup.CalendarYear, popup.CalendarMonth, hit));
                    }
                    else if (hit == -1)
                    {
                        popup.PopupVisible = false;
                        popup.PopupClosed?.Invoke();
                        OpenHarmonyBridge.RequestRedraw();
                    }
                }
                else
                {
                    int index = popup.PopupIndexAt(x, y);
                    if (index >= 0)
                    {
                        popup.PopupSelect?.Invoke(index);
                    }
                    else
                    {
                        popup.PopupVisible = false;
                        popup.PopupClosed?.Invoke();
                        OpenHarmonyBridge.RequestRedraw();
                    }
                }
            }
            return true;
        }
        // The drag target is resolved once at the top level; the recursive walk must not
        // overwrite it as it descends into leaves.
        if (down)
        {
            _downX = x;
            _downY = y;
            _moved = false;
            _dragScrollTarget = FindScrollView(root, x, y);
            _dragSliderTarget = FindSlider(root, x, y);
            _dragLastY = y;
            _panTarget = null;
            _swipeTarget = null;
            _textDragTarget = null;
            _panGestureId = -1;
            if (FindGestureTarget(root, x, y) is { } panCandidate)
            {
                if (!OpenHarmonyGestures.HasPan(panCandidate) && OpenHarmonyGestures.HasSwipe(panCandidate))
                {
                    // Swipe recognizers use the same drag tracking as pan.
                    _panTarget = panCandidate;
                    _panStartX = x;
                    _panStartY = y;
                    _panGestureId = -2;
                }
                else if (OpenHarmonyGestures.HasPan(panCandidate))
                {
                    _panTarget = panCandidate;
                    _panStartX = x;
                    _panStartY = y;
                    _panGestureId = OpenHarmonyGestures.StartPan(panCandidate, x, y);
                }
                else if (panCandidate.Handler?.PlatformView is OpenHarmonyView { Swipe: not null } swipeView)
                {
                    _swipeTarget = swipeView;
                    _panStartX = x;
                    _panStartY = y;
                }
            }
        }
        else if (up)
        {
            _textDragTarget = null;
            if (_panTarget is { } panTarget)
            {
                if (_panGestureId == -2)
                {
                    OpenHarmonyGestures.SendSwiped(panTarget, x - _panStartX, y - _panStartY);
                }
                else
                {
                    OpenHarmonyGestures.CompletePan(panTarget, _panGestureId);
                }
                _panTarget = null;
                _panGestureId = -1;
            }
            if (_swipeTarget is { } swipeView)
            {
                swipeView.Swipe?.Invoke(x - _panStartX, y - _panStartY);
                _swipeTarget = null;
            }
            if (_dragSliderTarget is { IsSlider: true } slider)
            {
                _dragSliderTarget = null;
                slider.SliderDrag?.Invoke(slider.SliderFraction, true);
            }
            _dragScrollTarget = null;
        }
        return HandleTouchCore(root, down, up, x, y);
    }

    private bool HandleTouchCore(IView view, bool down, bool up, float x, float y)
    {
        bool handled = false;
        if (view.Handler?.PlatformView is OpenHarmonyView { ShowsTitleBar: true } chromeView)
        {
            if (down && chromeView.InBackButton(x, y))
            {
                chromeView.BackTapped?.Invoke();
                return true;
            }
        }
        if (view.Handler?.PlatformView is OpenHarmonyView { ShowsHamburger: true } shellView)
        {
            if (down && shellView.InHamburger(x, y) && !shellView.FlyoutOpen)
            {
                shellView.FlyoutRequested?.Invoke();
                return true;
            }
        }
        if (view.Handler?.PlatformView is OpenHarmonyView { IsFlyoutPage: true } flyoutPage)
        {
            if (down)
            {
                if (flyoutPage.FlyoutPresented && !flyoutPage.InFlyoutPanel(x, y))
                {
                    flyoutPage.FlyoutDismiss?.Invoke();
                    return true;
                }
                if (flyoutPage.InHamburger(x, y))
                {
                    flyoutPage.OpenFlyout?.Invoke();
                    return true;
                }
            }
            // While the panel is open only it receives touches; otherwise only the detail does.
            foreach (IView child in ChildrenOf(view))
            {
                bool isFlyout = ReferenceEquals(child, (view as Microsoft.Maui.Controls.FlyoutPage)?.Flyout);
                bool isDetail = !isFlyout;
                if (flyoutPage.FlyoutPresented ? isFlyout : isDetail)
                {
                    handled |= HandleTouchCore(child, down, up, x, y);
                }
            }
            return handled || (down && flyoutPage.FlyoutPresented);
        }
        // Children of a scrolled view are shifted by the scroll offset; navigation page
        // content is already arranged below its bar.
        float childX = x;
        float childY = y;
        if (view.Handler?.PlatformView is OpenHarmonyView { IsScrollView: true } container)
        {
            childX += container.ScrollOffsetX;
            childY += container.ScrollOffsetY;
        }
        foreach (IView child in ChildrenOf(view))
        {
            handled |= HandleTouchCore(child, down, up, childX, childY);
        }
        if (view.Handler?.PlatformView is OpenHarmonyView { IsTextEntry: true } textEntry && !_moved &&
            textEntry.Frame.Contains(x, y) && textEntry.IsFocused)
        {
            if (down)
            {
                // Pressing inside a text entry moves the cursor and starts a selection drag.
                _textDragTarget = textEntry;
                _textDragAnchor = textEntry.CursorIndexFromX(x);
                ApplyTextSelection(textEntry, _textDragAnchor);
                handled = true;
            }
            else if (up)
            {
                _textDragTarget = null;
            }
        }
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            // Views handle their own touches (buttons, navigation bars); plain views ignore them.
            // A drag never counts as a tap.
            handled |= platform.OnTouch(down, up && !_moved, x, y);
            // Scroll views consume touches inside them (drag scrolling).
            handled |= platform.IsScrollView && platform.Frame.Contains(x, y);
            // Gesture recognizers run for the deepest view under the finger.
            if (platform.Frame.Contains(x, y) && OpenHarmonyGestures.HasGestures(view))
            {
                if (up && !_moved)
                {
                    handled |= OpenHarmonyGestures.SendTap(view, x, y);
                }
                else if (down)
                {
                    handled = true;
                }
            }
        }
        return handled;
    }

    /// <summary>Applies a cursor/selection update to the entry (platform + virtual view).</summary>
    private OpenHarmonyView? FindOpenFlyout(IView view)
    {
        if (view.Handler?.PlatformView is OpenHarmonyView { FlyoutOpen: true } flyout)
        {
            return flyout;
        }
        foreach (IView child in ChildrenOf(view))
        {
            OpenHarmonyView? found = FindOpenFlyout(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>First platform view in the tree with an open dropdown.</summary>
    private OpenHarmonyView? FindOpenPopup(IView view)
    {
        if (view.Handler?.PlatformView is OpenHarmonyView { PopupVisible: true } popup)
        {
            return popup;
        }
        foreach (IView child in ChildrenOf(view))
        {
            OpenHarmonyView? found = FindOpenPopup(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private IView? _pointerHover;

    /// <summary>
    /// Hover for mouse moves (the NDK mouse callback arrives as a touch move): pointer recognizers
    /// receive Exited/Entered when the hovered view changes and Moved on every report.
    /// </summary>
    public bool HandlePointerMove(IView root, float x, float y)
    {
        IView? target = FindPointerTarget(root, x, y);
        if (!ReferenceEquals(target, _pointerHover))
        {
            if (_pointerHover is { } previous)
            {
                OpenHarmonyPointer.Dispatch(previous, OpenHarmonyPointer.Kind.Exited, x, y);
            }
            _pointerHover = target;
            if (target is not null)
            {
                OpenHarmonyPointer.Dispatch(target, OpenHarmonyPointer.Kind.Entered, x, y);
            }
        }
        return target is not null && OpenHarmonyPointer.Dispatch(target, OpenHarmonyPointer.Kind.Moved, x, y);
    }

    /// <summary>Routes a shell pinch report to the deepest view that owns a pinch recognizer.</summary>
    public bool HandlePinch(IView root, int phase, double scale, float x, float y)
        => FindPinchTarget(root, x, y) is { } target && OpenHarmonyPinch.Dispatch(target, phase, scale, x, y);

    private IView? FindPinchTarget(IView view, float x, float y)
    {
        IView? found = null;
        if (view.Handler?.PlatformView is OpenHarmonyView platform && !platform.Frame.Contains(x, y))
        {
            return null;
        }
        foreach (IView child in ChildrenOf(view))
        {
            found = FindPinchTarget(child, x, y) ?? found;
        }
        return found ?? (OpenHarmonyPinch.HasPinch(view) ? view : null);
    }

    /// <summary>Deepest view containing the point that owns a pointer recognizer.</summary>
    private IView? FindPointerTarget(IView view, float x, float y)
    {
        IView? found = null;
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
                localX += platform.ScrollOffsetX;
                localY += platform.ScrollOffsetY;
            }
        }
        foreach (IView child in ChildrenOf(view))
        {
            found = FindPointerTarget(child, localX, localY) ?? found;
        }
        return found ?? (OpenHarmonyPointer.HasPointer(view) ? view : null);
    }

    /// <summary>Deepest view containing the point that owns gesture recognizers.</summary>
    private IView? FindGestureTarget(IView view, float x, float y)
    {
        IView? found = null;
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
                localX += platform.ScrollOffsetX;
                localY += platform.ScrollOffsetY;
            }
        }
        foreach (IView child in ChildrenOf(view))
        {
            found = FindGestureTarget(child, localX, localY) ?? found;
        }
        return found ?? (OpenHarmonyGestures.HasGestures(view) ||
                         view.Handler?.PlatformView is OpenHarmonyView { Swipe: not null } ? view : null);
    }

    /// <summary>Slider containing the point (the nearest ancestor wins).</summary>
    private OpenHarmonyView? FindSlider(IView view, float x, float y)
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
            if (platform.IsSlider)
            {
                found = platform;
            }
            if (platform.IsScrollView)
            {
                localX += platform.ScrollOffsetX;
                localY += platform.ScrollOffsetY;
            }
        }
        foreach (IView child in ChildrenOf(view))
        {
            found = FindSlider(child, localX, localY) ?? found;
        }
        return found;
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
        if (platform is { IsCheckBox: true })
        {
            sb.Append($" checked={platform.IsChecked}");
        }
        if (platform is { IsSwitch: true })
        {
            sb.Append($" on={platform.IsOn}");
        }
        if (platform is { IsSlider: true })
        {
            sb.Append($" slider={platform.SliderValue:0.##}/{platform.SliderMinimum:0.##}-{platform.SliderMaximum:0.##}");
        }
        if (platform is { IsProgressBar: true })
        {
            sb.Append($" progress={platform.Progress:0.##}");
        }
        if (platform is { IsActivityIndicator: true })
        {
            sb.Append($" running={platform.IsRunning}");
        }
        if (platform is { IsShape: true } or { IsBorder: true })
        {
            var path = platform.Shape?.PathForBounds(new RectF(platform.Frame.X, platform.Frame.Y, platform.Frame.Width, platform.Frame.Height));
            sb.Append($" shape={platform.Shape?.GetType().Name ?? "null"} points={(path?.Points?.Count() ?? 0)}");
        }
        if (platform is { IsStepper: true })
        {
            sb.Append($" stepper={platform.StepperValue:0.##}");
        }
        if (platform is { IsRadioButton: true })
        {
            sb.Append($" radio={platform.RadioChecked}");
        }
        if (view.Opacity < 1.0 || view.TranslationX != 0 || view.TranslationY != 0 || view.Scale != 1.0 || view.Rotation != 0)
        {
            sb.Append($" transform=(opacity={view.Opacity:0.##} t=({view.TranslationX:0.#},{view.TranslationY:0.#}) scale={view.Scale:0.##} rot={view.Rotation:0.#})");
        }
        if (platform is { IsNavigationPage: true })
        {
            sb.Append($" nav='{platform.NavTitle}' back={platform.CanGoBack}");
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
