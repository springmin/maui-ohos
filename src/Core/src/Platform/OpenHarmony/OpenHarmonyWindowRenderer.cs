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
        // A changed IView.Shadow must repaint; the drawing below reads the shadow directly.
        OpenHarmonyShadow.Install();
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
        OpenHarmonyView.SetSurfaceViewport(width, height);
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

    private void DrawDiagnosticsFor(IView root)
    {
        var stack = new Stack<IView>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            IView view = stack.Pop();
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
                stack.Push(child);
            }
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

    /// <summary>
    /// Child views of a view: layout children and content-view content (pages).
    /// </summary>
    /// <remarks>
    /// The sequence is produced by the allocation-free <see cref="ChildEnumerator"/> struct instead
    /// of an iterator method: the frame path walks every node of the tree (drawing, hit-testing,
    /// animation probing), and a C# iterator allocated a state machine plus the layout's interface
    /// enumerator per node per walk, which the Debug build measured at ~180 B per node per frame.
    /// The order, the reference-identity comparisons (presented content vs. navigation page) and
    /// the laziness (later branches are only read once the earlier ones are exhausted, so a
    /// hit-test that stops at a view outside the point never touches its page/flyout state) are
    /// exactly those of the iterator this replaces.
    /// </remarks>
    private struct ChildEnumerator
    {
        private static readonly List<IView> s_noChildren = new();

        private readonly IView _view;
        private IView? _current;
        private IView? _presentedContent;
        private List<IView>? _viewChildren;
        private int _phase;
        private int _index;

        public ChildEnumerator(IView view)
        {
            _view = view;
            _current = null;
            _presentedContent = null;
            _viewChildren = null;
            _phase = 0;
            _index = 0;
        }

        /// <summary>Pattern-based foreach entry point: copied per loop, still allocation-free.</summary>
        public readonly ChildEnumerator GetEnumerator() => this;

        public readonly IView Current => _current!;

        public bool MoveNext()
        {
            switch (_phase)
            {
                case 0: // layout children, in list order
                    if (_view is ILayout layout)
                    {
                        while (_index < layout.Count)
                        {
                            _current = layout[_index++];
                            return true;
                        }
                    }
                    _phase = 1;
                    _index = 0;
                    goto case 1;
                case 1: // the content view's presented content
                    _phase = 2;
                    _presentedContent = (_view as IContentView)?.PresentedContent as IView;
                    if (_presentedContent is not null)
                    {
                        _current = _presentedContent;
                        return true;
                    }
                    goto case 2;
                case 2: // the visible page of a navigation page (unless already presented above)
                    _phase = 3;
                    if (_view is Microsoft.Maui.Controls.NavigationPage navigation &&
                        navigation.CurrentPage is IView currentPage &&
                        !ReferenceEquals(currentPage, _presentedContent))
                    {
                        _current = currentPage;
                        return true;
                    }
                    goto case 3;
                case 3: // flyout page detail (always visible)
                    _phase = 4;
                    if (_view is Microsoft.Maui.Controls.FlyoutPage flyoutPage && flyoutPage.Detail is IView detail)
                    {
                        _current = detail;
                        return true;
                    }
                    goto case 4;
                case 4: // flyout page panel (only while presented)
                    _phase = 5;
                    if (_view is Microsoft.Maui.Controls.FlyoutPage presented &&
                        presented.IsPresented &&
                        presented.Flyout is IView flyoutContent)
                    {
                        _current = flyoutContent;
                        return true;
                    }
                    goto case 5;
                case 5: // platform-owned children (collection view items)
                    _viewChildren ??= _view.Handler?.PlatformView is OpenHarmonyView { ViewChildren.Count: > 0 } platform
                        ? platform.ViewChildren
                        : s_noChildren;
                    while (_index < _viewChildren.Count)
                    {
                        _current = _viewChildren[_index++];
                        return true;
                    }
                    _phase = 6;
                    return false;
                default:
                    return false;
            }
        }
    }

    private static ChildEnumerator ChildrenOf(IView view) => new(view);

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
            platform.DrawShadow(_canvas);
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
                // Detail fills the window; the flyout is an overlay clipped to its panel. Only the
                // first two children are drawn, so the walk stops there instead of materialising
                // the list the iterator version needed.
                IView? flyoutDetail = null;
                IView? flyoutContent = null;
                int childIndex = 0;
                foreach (IView child in ChildrenOf(view))
                {
                    if (childIndex == 0)
                    {
                        flyoutDetail = child;
                    }
                    else if (childIndex == 1)
                    {
                        flyoutContent = child;
                        break;
                    }
                    childIndex++;
                }
                if (flyoutDetail is not null)
                {
                    DrawView(flyoutDetail);
                }
                if (platform.FlyoutPresented && flyoutContent is not null)
                {
                    RectF flyoutFrame = platform.Frame;
                    _canvas.FillColor = Colors.Black.WithAlpha(0.5f);
                    _canvas.FillRectangle(flyoutFrame.X, flyoutFrame.Y, flyoutFrame.Width, flyoutFrame.Height);
                    _canvas.SaveState();
                    _canvas.ClipRectangle(flyoutFrame.X, flyoutFrame.Y, platform.FlyoutWidth, flyoutFrame.Height);
                    DrawView(flyoutContent);
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
    private OpenHarmonyDragAndDrop.Session? _dragSession;
    private IView? _dragCandidate;
    private IView? _dragRoot;
    private float _dragPressX;
    private float _dragPressY;
    private long _dragPressTicks;
    private bool _dragRejected;
    /// <summary>Press-time fact: does any view in the tree own a usable drop recognizer?</summary>
    private bool _dropCapableSeen;

    /// <summary>True while any view wants continuous redraws (activity indicators).</summary>
    public bool HasAnimations(IView? root)
    {
        // Idle frames are the common case: with no spinner registered anywhere the tree cannot
        // contain a NeedsAnimation view, so skip the recursive walk entirely (it stays the exact
        // answer - including visibility - whenever something is registered).
        if (OpenHarmonyView.AnimationCount <= 0)
        {
            return false;
        }
        return TreeHasAnimations(root);
    }

    /// <summary>Page indicator dots for a carousel.</summary>
    private void DrawCarouselIndicator(OpenHarmonyView carousel)
    {
        if (carousel.VirtualView is not Microsoft.Maui.Controls.CarouselView view)
        {
            return;
        }
        int count = OpenHarmonyCarouselViewHandler.MaterializeItems(view).Count;
        if (count <= 1)
        {
            return;
        }
        RectF frame = carousel.Frame;
        float spacing = 16f;
        float startX = frame.X + (frame.Width - count * spacing) / 2f + spacing / 2f;
        float y = frame.Y + frame.Height - 16f;
        int position = Math.Clamp(view.Position, 0, count - 1);
        // Only dots the surface can show are drawn: a carousel with hundreds of items lays them
        // out past both screen edges, and a dot outside the surface is clipped, so its colour and
        // circle are invisible work. The index range is derived from the same startX/spacing the
        // drawing loop uses, so the visible pixels are identical.
        int first = 0;
        int last = count;
        if (OpenHarmonyView.SurfaceViewportWidth > 0)
        {
            first = Math.Clamp((int)Math.Floor((-startX) / spacing), 0, count);
            last = Math.Clamp((int)Math.Ceiling((OpenHarmonyView.SurfaceViewportWidth - startX) / spacing), 0, count);
        }
        for (int i = first; i < last; i++)
        {
            _canvas.FillColor = i == position ? Colors.White : Colors.Gray;
            _canvas.FillCircle(startX + i * spacing, y, i == position ? 6f : 4f);
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
        // A promoted drag owns the pointer before any pan/scroll/selection tracking.
        if (HandleDragMove(x, y))
        {
            return true;
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

    /// <summary>
    /// Long-press promotion and drag tracking for moves. Returns true while a drag is in flight
    /// (the move is consumed) and false otherwise.
    /// </summary>
    private bool HandleDragMove(float x, float y)
    {
        if (_dragSession is { } session)
        {
            if (_dragRoot is { } sessionRoot)
            {
                // The press-time structure decides whether a drop target can exist at all; only
                // then does the move pay for the positional walk (which cannot be answered from a
                // flat candidate list: the ancestor frames that prune a subtree, and the scroll
                // offsets that shift it, depend on the path to each view).
                OpenHarmonyDragAndDrop.Update(session,
                    _dropCapableSeen ? FindDropTarget(sessionRoot, x, y) : null);
            }
            return true;
        }
        if (_dragCandidate is null || _dragRejected)
        {
            return false;
        }
        if (Math.Abs(x - _dragPressX) <= OpenHarmonyDragAndDrop.Slop &&
            Math.Abs(y - _dragPressY) <= OpenHarmonyDragAndDrop.Slop)
        {
            return false;
        }
        // The pointer left the slop: only a press held long enough becomes a drag.
        _dragRejected = true;
        if (Environment.TickCount64 - _dragPressTicks < OpenHarmonyDragAndDrop.LongPressMilliseconds)
        {
            return false;
        }
        _dragSession = OpenHarmonyDragAndDrop.Start(_dragCandidate, x, y);
        if (_dragSession is null)
        {
            return false;
        }
        AbortDragCompetitors();
        if (_dragRoot is { } dragRoot)
        {
            OpenHarmonyDragAndDrop.Update(_dragSession,
                _dropCapableSeen ? FindDropTarget(dragRoot, x, y) : null);
        }
        return true;
    }

    /// <summary>A promoted drag owns the pointer: the pan/scroll/selection tracking from down ends.</summary>
    private void AbortDragCompetitors()
    {
        if (_panTarget is { } panTarget)
        {
            if (_panGestureId >= 0)
            {
                OpenHarmonyGestures.CompletePan(panTarget, _panGestureId);
            }
            _panTarget = null;
            _panGestureId = -1;
        }
        _swipeTarget = null;
        _dragSliderTarget = null;
        _dragScrollTarget = null;
        _textDragTarget = null;
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
            TouchWalk pointerWalk = default;
            CollectTouchTargets(root, x, y, true, TouchTargets.Pointer, ref pointerWalk);
            if (pointerWalk.Pointer is { } pointerView)
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

        // Every remaining hit-test the touch path needs is answered by one walk instead of one
        // walk per query. The queries the walk replaces - popup, flyout, scroll view, slider,
        // drag target, gesture target - all share the same traversal rules (a platform view whose
        // frame excludes the point hides its whole subtree; scroll offsets shift the children),
        // and the popup/flyout searches walk the whole tree whenever no dropdown is open, i.e. on
        // every ordinary touch. The walk runs after the pointer dispatch exactly like the queries
        // it replaces, so a handler that mutates the tree while handling the press is observed by
        // the remaining queries just as before.
        TouchTargets wanted = TouchTargets.None;
        if (_popupView is not { PopupVisible: true })
        {
            wanted |= TouchTargets.Popup;
        }
        if (_flyoutPanelView is not { FlyoutOpen: true })
        {
            wanted |= TouchTargets.Flyout;
        }
        if (down)
        {
            wanted |= TouchTargets.Scroll | TouchTargets.Slider | TouchTargets.Drag
                | TouchTargets.Gesture | TouchTargets.Drop;
        }
        TouchWalk walk = default;
        if (wanted != TouchTargets.None)
        {
            CollectTouchTargets(root, x, y, true, wanted, ref walk);
            if ((wanted & TouchTargets.Popup) != 0)
            {
                _popupView = walk.Popup;
            }
            if ((wanted & TouchTargets.Flyout) != 0)
            {
                _flyoutPanelView = walk.Flyout;
            }
            if ((wanted & TouchTargets.Drop) != 0)
            {
                // Structural hint for drag moves: with no drop recognizer anywhere no move can
                // resolve a drop target, so the per-move full-tree search is skipped.
                _dropCapableSeen = walk.SawDropCapable;
            }
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
            _dragScrollTarget = walk.Scroll;
            _dragSliderTarget = walk.Slider;
            _dragLastY = y;
            _panTarget = null;
            _swipeTarget = null;
            _textDragTarget = null;
            _panGestureId = -1;
            // A press may become a drag when it is held long enough and then moved.
            if (_dragSession is { } unexpectedDrag)
            {
                OpenHarmonyDragAndDrop.Cancel(unexpectedDrag);
            }
            _dragSession = null;
            _dragCandidate = walk.Drag;
            _dragRoot = root;
            _dragPressX = x;
            _dragPressY = y;
            _dragPressTicks = Environment.TickCount64;
            _dragRejected = false;
            if (walk.Gesture is { } panCandidate)
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
            if (_dragSession is { } dragSession)
            {
                // The release completes the drag over the view under the pointer (if any).
                OpenHarmonyDragAndDrop.Drop(dragSession, _dropCapableSeen ? FindDropTarget(root, x, y) : null);
                _dragSession = null;
                _dragCandidate = null;
                _dragRoot = null;
                _dragRejected = true;
                return true;
            }
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

    /// <summary>Which hit-tests a touch walk should answer.</summary>
    [Flags]
    private enum TouchTargets
    {
        None = 0,
        Pointer = 1 << 0,
        Scroll = 1 << 1,
        Slider = 1 << 2,
        Drag = 1 << 3,
        Gesture = 1 << 4,
        Popup = 1 << 5,
        Flyout = 1 << 6,
        Drop = 1 << 7,
    }

    /// <summary>Results of one touch walk; null means "nothing of that kind under the point".</summary>
    private struct TouchWalk
    {
        public IView? Pointer;
        public OpenHarmonyView? Scroll;
        public OpenHarmonyView? Slider;
        public IView? Drag;
        public IView? Gesture;
        public OpenHarmonyView? Popup;
        public OpenHarmonyView? Flyout;

        /// <summary>True when the walk saw a usable drop recognizer anywhere (structure, not position).</summary>
        public bool SawDropCapable;
    }

    /// <summary>
    /// One walk that answers every touch hit-test the press path needs. Each query keeps its
    /// original traversal rule: the five position queries prune a subtree whose platform view
    /// does not contain the point and shift child coordinates by scroll offsets, while the popup
    /// and flyout searches are pre-order first matches and are not pruned. Deepest-match queries
    /// resolve like the recursive originals: the last child that produced a match wins, and the
    /// view itself only matches when no child did.
    /// </summary>
    private void CollectTouchTargets(IView view, float x, float y, bool positioned, TouchTargets wanted, ref TouchWalk walk)
    {
        OpenHarmonyView? platform = view.Handler?.PlatformView as OpenHarmonyView;
        if (platform is not null)
        {
            if ((wanted & TouchTargets.Popup) != 0 && walk.Popup is null && platform.PopupVisible)
            {
                walk.Popup = platform;
            }
            if ((wanted & TouchTargets.Flyout) != 0 && walk.Flyout is null && platform.FlyoutOpen)
            {
                walk.Flyout = platform;
            }
            if (positioned && !platform.Frame.Contains(x, y))
            {
                positioned = false;
            }
        }
        float localX = x;
        float localY = y;
        if (positioned && platform is { IsScrollView: true })
        {
            localX += platform.ScrollOffsetX;
            localY += platform.ScrollOffsetY;
        }
        if ((wanted & TouchTargets.Drop) != 0 && OpenHarmonyDragAndDrop.HasDrop(view))
        {
            walk.SawDropCapable = true;
        }
        IView? pointerBefore = walk.Pointer;
        OpenHarmonyView? scrollBefore = walk.Scroll;
        OpenHarmonyView? sliderBefore = walk.Slider;
        IView? dragBefore = walk.Drag;
        IView? gestureBefore = walk.Gesture;
        foreach (IView child in ChildrenOf(view))
        {
            CollectTouchTargets(child, localX, localY, positioned, wanted, ref walk);
        }
        if (!positioned)
        {
            return;
        }
        if ((wanted & TouchTargets.Pointer) != 0 && ReferenceEquals(walk.Pointer, pointerBefore)
            && OpenHarmonyPointer.HasPointer(view))
        {
            walk.Pointer = view;
        }
        if ((wanted & TouchTargets.Scroll) != 0 && ReferenceEquals(walk.Scroll, scrollBefore)
            && platform is { IsScrollView: true })
        {
            walk.Scroll = platform;
        }
        if ((wanted & TouchTargets.Slider) != 0 && ReferenceEquals(walk.Slider, sliderBefore)
            && platform is { IsSlider: true })
        {
            walk.Slider = platform;
        }
        if ((wanted & TouchTargets.Drag) != 0 && ReferenceEquals(walk.Drag, dragBefore)
            && OpenHarmonyDragAndDrop.HasDrag(view))
        {
            walk.Drag = view;
        }
        if ((wanted & TouchTargets.Gesture) != 0 && ReferenceEquals(walk.Gesture, gestureBefore)
            && (OpenHarmonyGestures.HasGestures(view) || platform is { Swipe: not null }))
        {
            walk.Gesture = view;
        }
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

    /// <summary>Deepest view containing the point that owns a usable drop recognizer.</summary>
    private IView? FindDropTarget(IView view, float x, float y)
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
            found = FindDropTarget(child, localX, localY) ?? found;
        }
        return found ?? (OpenHarmonyDragAndDrop.HasDrop(view) ? view : null);
    }

    /// <summary>Human-readable tree for logs/tests: type, text and arranged frame.</summary>
    public string Describe(IView view, int depth = 0)
    {
        var sb = new StringBuilder();
        DescribeInto(view, depth, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Appends the description to one builder. The recursion used to return a string per subtree
    /// and append it to the parent's builder, which copied every node once per ancestor (O(n*depth),
    /// measured at 150 levels); writing into a single builder is O(n).
    /// </summary>
    private static void DescribeInto(IView view, int depth, StringBuilder sb)
    {
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
            DescribeInto(child, depth + 1, sb);
        }
    }
}
