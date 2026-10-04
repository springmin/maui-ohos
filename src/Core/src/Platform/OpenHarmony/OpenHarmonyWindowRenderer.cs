// Compositor for OpenHarmony: measure/arrange a MAUI visual tree, draw it through the
// Microsoft.Maui.Graphics canvas and route taps to the handlers' platform views.
using System.Text;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
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

    /// <summary>
    /// Test seam: reports the pivot (canvas coordinates) of every rotate/scale transform the
    /// drawing pass applies, so off-device tests can pin AnchorX/AnchorY without a rasterizer.
    /// </summary>
    internal static Action<float, float>? TransformPivotObserved { get; set; }

    /// <summary>
    /// Test seam: reports every drawn flyout panel (the panel view and the number of rich rows
    /// drawn into it), so off-device tests can pin that the drawer and its materialized rows
    /// reached the drawing pass without a rasterizer.
    /// </summary>
    internal static Action<OpenHarmonyView, int>? FlyoutPanelDrawn { get; set; }

    /// <summary>
    /// Test seam: reports every drawn rich Shell.TitleView row (T15: the arranged view and its
    /// canvas-space band), so off-device tests can pin that the materialized row reached the
    /// drawing pass without a rasterizer.
    /// </summary>
    internal static Action<IView, RectF>? ShellTitleViewDrawn { get; set; }

    /// <summary>
    /// Test/diagnostic seam: invoked as each <see cref="Render"/> phase completes, with the
    /// phase index (0 = render entry, 1 = measure+arrange, 2 = surface begin + background fill,
    /// 3 = content draw, 4 = chrome and floating overlays, 5 = accessibility publish, i.e.
    /// immediately before the present). Null in production, so every call site costs one null
    /// check and a frame-pacing probe can split a frame without a timer inside the compositor.
    /// </summary>
    internal static Action<int>? RenderPhaseTick { get; set; }

    /// <summary>
    /// Draw-cost probe seam: invoked once per view that has a platform view, immediately before
    /// that view draws itself (shadow and content), with a coarse node kind - see the
    /// <c>DrawKind*</c> constants. A probe substitutes the canvas factory with a timing canvas
    /// and attributes the canvas work measured between two ticks to the kind of the node that
    /// produced it; the walk's own glue (property reads, child enumeration, flow maps) stays
    /// un-attributed and is the residual of the frame's draw total. Null in production, so a
    /// call site costs one null check and the probe never changes drawing.
    /// </summary>
    internal static Action<int>? DrawCostTick { get; set; }

    /// <summary>Node kinds reported through <see cref="DrawCostTick"/>.</summary>
    internal const int DrawKindText = 0;
    internal const int DrawKindImage = 1;
    internal const int DrawKindShape = 2;
    internal const int DrawKindContainer = 3;
    internal const int DrawKindOther = 4;

    /// <summary>
    /// Coarse classification of what a node's own draw spends its time on, by the same branch
    /// order the platform view's Draw uses: an image or shape node is classified by its payload
    /// even when it also carries text, a text node by its text, everything else by whether it
    /// hosts children.
    /// </summary>
    private static int DrawKindOf(IView view, OpenHarmonyView platform)
    {
        if (platform.ImageBytes is { Length: > 0 })
        {
            return DrawKindImage;
        }
        if (platform.IsShape || platform.IsBorder)
        {
            return DrawKindShape;
        }
        if (platform.IsTextEntry || !string.IsNullOrEmpty(platform.Text))
        {
            return DrawKindText;
        }
        return view is ILayout ? DrawKindContainer : DrawKindOther;
    }

    private readonly MauiCanvas _canvas;

    /// <summary>
    /// Surface rectangle of the frame being drawn, used by the off-surface cull
    /// (<see cref="CanSkipOwnDrawing"/>). Set at the start of every draw pass; frames that do
    /// not call into the walk (tests) leave the default, which disables culling by letting every
    /// frame intersect.
    /// </summary>
    private RectF _drawSurface = new(float.NegativeInfinity, float.NegativeInfinity,
        float.PositiveInfinity, float.PositiveInfinity);

    // Layout revalidation gate. IView.Measure does not consult the per-element measure cache
    // (that cache only serves the obsolete GetSizeRequest path), so re-running Measure/Arrange
    // every frame re-walks the whole tree; on the device interpreter build that measured at
    // ~14 ms of a ~49 ms frame while the tree was static between frames. The gate re-runs the
    // layout pass only when the tree invalidated its measure or edited its layout since the last
    // pass (OpenHarmonyLayoutInvalidation's handler-invoke version, plus the root's
    // MeasureInvalidated event for handler-less subtrees), when the render root or surface size
    // changed, or when the safe-area insets/title bar row changed (both alter the content's
    // frame without invalidating the content's own measure).
    private IView? _layoutRoot;
    private Microsoft.Maui.Controls.VisualElement? _layoutWatched;
    private EventHandler? _layoutInvalidatedHandler;
    private volatile bool _layoutDirty = true;
    private int _layoutVersion;
    private int _layoutWidth = -1;
    private int _layoutHeight = -1;
    private Thickness _layoutInsets;
    private int _layoutSoftInput;
    private bool _layoutShowsTitleBar;
    private float _layoutTitleBarHeight;

    public OpenHarmonyWindowRenderer()
    {
        _canvas = CanvasFactory?.Invoke() ?? new MauiCanvas();
        // A changed IView.Shadow must repaint; the drawing below reads the shadow directly.
        OpenHarmonyShadow.Install();
        // An alert opening or closing must repaint (and republish the accessibility shadow tree)
        // even when no input follows it. One process-wide subscription, not one per renderer.
        if (!s_alertRedrawWired)
        {
            s_alertRedrawWired = true;
            OpenHarmonyAlertHost.Changed += OpenHarmonyBridge.RequestRedraw;
        }
        // Clip, anchor, input transparency and z-order are read from the virtual view on every
        // draw as well; their property changes need a repaint for the same reason.
        OpenHarmonyLayoutRedraw.Install();
    }

    private static bool s_alertRedrawWired;

    public Color BackgroundColor { get; set; } = Colors.DarkSlateBlue;

    /// <summary>Alert scrim: one shared colour, not a new Color per open-alert frame.</summary>
    private static readonly Color s_alertScrim = Colors.Black.WithAlpha(0.55f);

    /// <summary>Flyout scrim over the covered detail: derived once instead of per frame.</summary>
    private static readonly Color s_flyoutScrim = Colors.Black.WithAlpha(0.5f);

    /// <summary>
    /// The Window.TitleBar row of the window that owns <paramref name="content"/>, when the app
    /// set one. TitleBar is a logical child of the window, not of the page, so the compositor
    /// resolves it through the content's window handler; a detached tree (no window) has no row.
    /// </summary>
    private static OpenHarmonyTitleBarRow? ResolveTitleBar(IView? content)
        => content is Microsoft.Maui.Controls.VisualElement element &&
           element.Window is { Handler: OpenHarmonyWindowHandler windowHandler }
            ? windowHandler.TitleBar
            : null;

    /// <summary>Measures and arranges the tree, then draws it when a surface is available.</summary>
    /// <remarks>
    /// A Window.TitleBar row (when the app set one and it is visible) takes the top of the
    /// surface: the content is arranged below it through the same page-aware walk the app host
    /// uses, and the row's own subtree is arranged and drawn above the content.
    /// </remarks>
    public bool Render(IView content, int width, int height)
    {
        RenderPhaseTick?.Invoke(0);
        OpenHarmonyTitleBarRow? titleBar = ResolveTitleBar(content);
        bool showsTitleBar = titleBar is { IsVisible: true };
        float titleBarHeight = showsTitleBar ? titleBar!.Height : 0f;
        float contentHeight = Math.Max(0f, height - titleBarHeight);
        // The draw walk culls nodes whose own pixels cannot land on this surface; the rectangle
        // is only consulted by the platform-view branch of DrawView.
        _drawSurface = new RectF(0, 0, width, height);
        // The insets are read once and both gate the layout pass and feed the arrange below.
        Thickness insets = OpenHarmonySafeArea.GetWindowInsets();
        int softInput = OpenHarmonySafeArea.GetSoftInputInset();
        if (NeedsLayout(content, width, height, insets, softInput, showsTitleBar, titleBarHeight))
        {
            // Consumed before the pass: an invalidation raised while measuring/arranging (or a
            // MeasureInvalidated event racing this reset) must stay pending for the next frame
            // instead of being swallowed by the reset.
            _layoutDirty = false;
            _layoutVersion = OpenHarmonyLayoutInvalidation.Version;
            content.Measure(width, contentHeight);
            if (showsTitleBar)
            {
                // Pages and navigation pages have no platform layout in this slice, so their
                // presented content is arranged by the page-aware walk (the app host uses it too)
                // inside the frame left below the row.
                OpenHarmonySafeAreaArrange.Arrange(content, new Rect(0, titleBarHeight, width, contentHeight),
                    new Rect(0, 0, width, height), insets);
            }
            else
            {
                content.Arrange(new Rect(0, 0, width, height));
            }
            _layoutRoot = content;
            _layoutWidth = width;
            _layoutHeight = height;
            _layoutInsets = insets;
            _layoutSoftInput = softInput;
            _layoutShowsTitleBar = showsTitleBar;
            _layoutTitleBarHeight = titleBarHeight;
            WatchLayout(content);
        }

        RenderPhaseTick?.Invoke(1);
        bool surfaceReady = SurfaceBegin is not null ? SurfaceBegin(width, height) : HostCanvas.Begin(width, height);
        if (!surfaceReady)
        {
            // No surface yet (or the host refuses); the tree is still arranged.
            return false;
        }
        // Hover resolution happens on moves, which carry no root; the render root is the fallback
        // and the touch path overwrites it with the host's exact root (modal-aware).
        _lastRoot = content;
        // The flow walk starts at the render root: its logical parents are outside the walk, so
        // the resolved direction comes from the nearest explicit ancestor (LTR without one).
        bool contentRightToLeft = OpenHarmonyFlowDirection.IsRightToLeftRoot(content);
        _canvas.FillColor = BackgroundColor;
        _canvas.FillRectangle(0, 0, width, height);
        _popupView = null;
        _flyoutPanelView = null;
        _toolbarOverflowView = null;
        _carouselViews.Clear();
        RenderPhaseTick?.Invoke(2);
        DrawView(content, OpenHarmonyFlowMap.Identity, contentRightToLeft);
        RenderPhaseTick?.Invoke(3);
        if (showsTitleBar)
        {
            // The window title bar is chrome above the page: arranged into the top row, drawn
            // after the content, with the back affordance over its leading slot and the system
            // caption buttons (when the shell hands them over) over its trailing slot.
            titleBar!.Measure(width);
            titleBar.Arrange(new Rect(0, 0, width, titleBarHeight));
            DrawView(titleBar.View, OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(titleBar.View));
            titleBar.DrawBackAffordance(_canvas);
            titleBar.DrawSystemButtons(_canvas);
        }
        // N6: app-managed window decorations follow a visible TitleBar row - the shell's own decor
        // steps aside and comes back exactly with it. Gated on the shell's decor sink so a
        // fullscreen phone window never asks (and keeps its system decorations).
        OpenHarmonyWindowDecoration.EnsureAppManaged(showsTitleBar && OpenHarmonyWindowDecoration.Available);
        foreach (OpenHarmonyView carousel in _carouselViews)
        {
            DrawCarouselIndicator(carousel);
        }
        if (_flyoutPanelView is { FlyoutOpen: true } flyoutPanel)
        {
            // The shell drawer floats above the content: the panel background/scrim first, then
            // the rich rows (materialized views, arranged in canvas space by their handler).
            DrawFlyoutPanel(flyoutPanel);
        }
        if (_popupView is { PopupVisible: true } popup)
        {
            // Dropdowns float above the rest of the tree.
            popup.DrawPopup(_canvas);
        }
        if (_toolbarOverflowView is { ToolbarOverflowOpen: true } toolbarOverflow)
        {
            // The toolbar's overflow dropdown floats above the content too (the bar itself is
            // drawn with its own view, before the children it would otherwise sit under).
            toolbarOverflow.DrawToolbarOverflow(_canvas);
        }
        RenderPhaseTick?.Invoke(4);
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
        RenderPhaseTick?.Invoke(5);
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

    /// <summary>
    /// True when the upcoming frame must re-run Measure/Arrange: the render root or surface size
    /// changed, the tree invalidated its measure or edited its layout since the last pass (the
    /// handler-invoke version and the root's MeasureInvalidated event), the safe-area insets
    /// moved (keyboard/avoid area), or the window title bar row appeared/disappeared/resized
    /// (the row steals layout space from the content without invalidating the content's own
    /// measure).
    /// </summary>
    private bool NeedsLayout(IView content, int width, int height, Thickness insets, int softInput,
        bool showsTitleBar, float titleBarHeight)
        => !ReferenceEquals(_layoutRoot, content)
           || width != _layoutWidth
           || height != _layoutHeight
           || _layoutDirty
           || _layoutVersion != OpenHarmonyLayoutInvalidation.Version
           || softInput != _layoutSoftInput
           || insets != _layoutInsets
           || showsTitleBar != _layoutShowsTitleBar
           || titleBarHeight != _layoutTitleBarHeight;

    /// <summary>
    /// Subscribes to the render root's <c>MeasureInvalidated</c> so the next frame re-runs the
    /// layout pass; called after each pass. Descendant invalidations reach the root through
    /// <c>OnChildMeasureInvalidated</c> propagation, and a root swap unsubscribes the previous
    /// root (so a modal page cannot keep the old root's tree alive).
    /// </summary>
    private void WatchLayout(IView content)
    {
        if (ReferenceEquals(_layoutWatched, content))
        {
            return;
        }
        if (_layoutWatched is not null && _layoutInvalidatedHandler is not null)
        {
            _layoutWatched.MeasureInvalidated -= _layoutInvalidatedHandler;
        }
        _layoutWatched = content as Microsoft.Maui.Controls.VisualElement;
        if (_layoutWatched is not null)
        {
            _layoutInvalidatedHandler ??= (_, _) => _layoutDirty = true;
            _layoutWatched.MeasureInvalidated += _layoutInvalidatedHandler;
        }
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
                RectF frame = platform.CanvasFrame;
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
        _canvas.FillColor = s_alertScrim;
        _canvas.FillRectangle(0, 0, (float)OpenHarmonyAlertHost.Width, (float)OpenHarmonyAlertHost.Height);
        RectF box = OpenHarmonyAlertHost.BoxRect;
        _canvas.FillColor = Colors.DimGray;
        _canvas.FillRoundedRectangle(box.X, box.Y, box.Width, box.Height, 12);
        _canvas.FontColor = Colors.White;
        _canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(30);
        RectF title = OpenHarmonyAlertHost.TitleRect;
        _canvas.DrawString(alert.Title ?? string.Empty, title.X, title.Y, title.Width, title.Height,
            HorizontalAlignment.Left, VerticalAlignment.Center);
        _canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(24);
        RectF message = OpenHarmonyAlertHost.MessageRect;
        _canvas.DrawString(alert.Message ?? string.Empty, message.X, message.Y, message.Width, message.Height,
            HorizontalAlignment.Left, VerticalAlignment.Top);
        _canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(26);
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
            RectF field = OpenHarmonyAlertHost.PromptRect;
            _canvas.FillColor = Colors.Black;
            _canvas.FillRoundedRectangle(field.X, field.Y, field.Width, field.Height, 8);
            _canvas.FontColor = Colors.White;
            _canvas.DrawString(alert.PromptText, field.X + 12, field.Y, field.Width - 24, field.Height,
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
    /// Child views of a view: layout children, content-view content and the visible page of a
    /// navigation, tabbed, shell or flyout page.
    /// </summary>
    /// <remarks>
    /// The sequence is produced by the allocation-free <see cref="ChildEnumerator"/> struct instead
    /// of an iterator method: the frame path walks every node of the tree (drawing, hit-testing,
    /// animation probing), and a C# iterator allocated a state machine plus the layout's interface
    /// enumerator per node per walk, which the Debug build measured at ~180 B per node per frame.
    /// The order, the reference-identity comparisons (presented content vs. navigation/tabbed/shell
    /// page) and the laziness (later branches are only read once the earlier ones are exhausted, so
    /// a hit-test that stops at a view outside the point never touches its page/flyout state) are
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
                case 3: // the selected page of a tabbed page (unless already presented above)
                    _phase = 4;
                    if (_view is Microsoft.Maui.Controls.TabbedPage tabbed &&
                        tabbed.CurrentPage is IView tabPage &&
                        !ReferenceEquals(tabPage, _presentedContent))
                    {
                        _current = tabPage;
                        return true;
                    }
                    goto case 4;
                case 4: // the current page of a shell (unless already presented above)
                    _phase = 5;
                    if (_view is Microsoft.Maui.Controls.Shell shell &&
                        shell.CurrentPage is IView shellPage &&
                        !ReferenceEquals(shellPage, _presentedContent))
                    {
                        _current = shellPage;
                        return true;
                    }
                    goto case 5;
                case 5: // flyout page detail (always visible)
                    _phase = 6;
                    if (_view is Microsoft.Maui.Controls.FlyoutPage flyoutPage && flyoutPage.Detail is IView detail)
                    {
                        _current = detail;
                        return true;
                    }
                    goto case 6;
                case 6: // flyout page panel (only while presented)
                    _phase = 7;
                    if (_view is Microsoft.Maui.Controls.FlyoutPage presented &&
                        presented.IsPresented &&
                        presented.Flyout is IView flyoutContent)
                    {
                        _current = flyoutContent;
                        return true;
                    }
                    goto case 7;
                case 7: // platform-owned children (collection view items)
                    _viewChildren ??= _view.Handler?.PlatformView is OpenHarmonyView { ViewChildren.Count: > 0 } platform
                        ? platform.ViewChildren
                        : s_noChildren;
                    while (_index < _viewChildren.Count)
                    {
                        _current = _viewChildren[_index++];
                        return true;
                    }
                    _phase = 8;
                    return false;
                default:
                    return false;
            }
        }
    }

    private static ChildEnumerator ChildrenOf(IView view) => new(view);

    /// <summary>
    /// Enumerates a view's children in ascending <see cref="IView.ZIndex"/> order (stable for
    /// equal values), the order both the drawing pass and the hit-test passes need: the platform
    /// stacks siblings by z-order, so the highest value is drawn last and is the topmost.
    /// </summary>
    /// <remarks>
    /// The fast path keeps the allocation-free <see cref="ChildEnumerator"/> when the children
    /// are already ordered (the usual case: every ZIndex is 0); only a node with an out-of-order
    /// child materialises and sorts its children for that walk. Sorting is a stable insertion
    /// sort: child lists are small and a run only happens after a ZIndex change, so the O(n^2)
    /// worst case buys zero allocations on the frame path.
    /// </remarks>
    private struct OrderedChildEnumerator
    {
        private ChildEnumerator _inner;
        private List<IView>? _sorted;
        private int _index;

        public OrderedChildEnumerator(IView view)
        {
            _inner = default;
            _sorted = null;
            _index = -1;
            int previous = int.MinValue;
            bool outOfOrder = false;
            foreach (IView child in ChildrenOf(view))
            {
                if (child.ZIndex < previous)
                {
                    outOfOrder = true;
                    break;
                }
                previous = child.ZIndex;
            }
            if (!outOfOrder)
            {
                _inner = new ChildEnumerator(view);
                return;
            }
            var list = new List<IView>();
            foreach (IView child in ChildrenOf(view))
            {
                InsertByZIndex(list, child);
            }
            _sorted = list;
        }

        private static void InsertByZIndex(List<IView> list, IView child)
        {
            int z = child.ZIndex;
            int index = list.Count;
            while (index > 0 && list[index - 1].ZIndex > z)
            {
                index--;
            }
            list.Insert(index, child);
        }

        public readonly IView Current => _sorted is null ? _inner.Current : _sorted[_index];

        /// <summary>Pattern-based foreach entry point: copied per loop, still allocation-free.</summary>
        public readonly OrderedChildEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_sorted is null)
            {
                return _inner.MoveNext();
            }
            _index++;
            return _index < _sorted.Count;
        }
    }

    private static OrderedChildEnumerator ChildrenInZOrder(IView view) => new(view);

    /// <summary>
    /// True when the view's subtree must not receive input: an input-transparent view blocks its
    /// own input, and a <see cref="Microsoft.Maui.Controls.Layout"/> additionally blocks its
    /// children while <c>CascadeInputTransparent</c> is true (the Layout default). A
    /// non-cascading layout stays transparent itself but its children remain hit-testable.
    /// </summary>
    private static bool BlocksInput(IView view)
        => view.InputTransparent &&
           (view is not Microsoft.Maui.Controls.Layout layout || layout.CascadeInputTransparent);

    /// <summary>
    /// True when the view's clip shape excludes the point. The clip is built in the view's bounds
    /// (the geometry is bounds-relative, like every MAUI platform clip) and translated into the
    /// canvas coordinates the frame uses; the flattened path is tested with an even-odd ray cast.
    /// A view without a clip contains every point; a clip with no geometry contains none.
    /// </summary>
    private static bool ClipContainsPoint(IView view, RectF frame, float x, float y)
    {
        if (view.Clip is not { } clip)
        {
            return true;
        }
        PathF path = ClipPathFor(clip, frame);
        if (path.Points is null || path.Count == 0)
        {
            return false;
        }
        PathF flat = path.Count >= 3 ? path.GetFlattenedPath(0.25f, false) : path;
        int count = flat.Points is null ? 0 : flat.Count;
        if (count < 3)
        {
            return false;
        }
        bool inside = false;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            PointF a = flat[i];
            PointF b = flat[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// The clip path in canvas coordinates: MAUI clip geometry is relative to the element's
    /// bounds (0,0,w,h), so <see cref="IShape.PathForBounds"/> is asked for the origin bounds and
    /// the result is translated to the view's frame position.
    /// </summary>
    private static PathF ClipPathFor(IShape clip, RectF frame)
    {
        PathF path = clip.PathForBounds(new RectF(0, 0, frame.Width, frame.Height));
        path.Transform(System.Numerics.Matrix3x2.CreateTranslation(frame.X, frame.Y));
        return path;
    }

    /// <summary>
    /// Logical (MAUI) frame of a view, in the coordinates the walk's flow map consumes. The
    /// platform view's <see cref="OpenHarmonyView.Frame"/> stays logical too; its CanvasFrame is
    /// the mapped rectangle the compositor draws and hit-tests.
    /// </summary>
    private static RectF LogicalFrameOf(IView view) => view.Frame is Rect frame
        ? new RectF((float)frame.X, (float)frame.Y, (float)frame.Width, (float)frame.Height)
        : default;

    /// <summary>
    /// Draws one view and its subtree. <paramref name="map"/> is the flow map of this view's
    /// logical-to-canvas space; <paramref name="parentRightToLeft"/> is its parent's resolved
    /// direction (MatchParent inheritance; see <see cref="OpenHarmonyFlowDirection"/>).
    /// <paramref name="shifted"/> is true once an ancestor translated the canvas (a view
    /// translation, scale/rotation or a scroll offset), which makes a child's logical frame no
    /// longer the rectangle its pixels land in: the off-surface cull is disabled from there on.
    /// </summary>
    private void DrawView(IView view, in OpenHarmonyFlowMap map, bool parentRightToLeft, bool shifted = false)
    {
        if (view.Visibility != Visibility.Visible)
        {
            return;
        }
        bool flowRightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(view, parentRightToLeft);
        // The transform must cover the view AND its subtree (a page fade has to carry the
        // page's content), and it must not leak into the next sibling. ICanvas.SaveState
        // restores the transform; alpha is a backend field outside that stack, so it is
        // captured and written back with the same discipline. The state lives outside the
        // platform-view branch so every exit (including the early returns below) can pop it.
        // Every transform input is read once: under the device interpreter each IView property
        // read is a virtual call through the bindable store, and the old code read Opacity,
        // TranslationX/Y, Scale and Rotation twice on the common (untransformed) path.
        bool transformed = false;
        float previousAlpha = 1f;
        bool clipped = false;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            platform.SetFlowContext(map, flowRightToLeft);
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
            double opacity = view.Opacity;
            double translationX = view.TranslationX;
            double translationY = view.TranslationY;
            double scale = view.Scale;
            double rotation = view.Rotation;
            transformed = opacity < 1.0 || translationX != 0 || translationY != 0 ||
                          scale != 1.0 || rotation != 0;
            // Only a geometric transform moves pixels: the off-surface cull stays disabled
            // below a translated/scaled/rotated node, and the scroll branch disables it too.
            bool movesPixels = translationX != 0 || translationY != 0 || scale != 1.0 || rotation != 0;
            previousAlpha = _canvas.Alpha;
            if (transformed)
            {
                _canvas.SaveState();
                _canvas.Alpha = (float)Math.Clamp(opacity, 0, 1);
                if (translationX != 0 || translationY != 0)
                {
                    _canvas.Translate((float)translationX, (float)translationY);
                }
                if (rotation != 0 || scale != 1.0)
                {
                    // Rotation and scale pivot at the view's anchor (AnchorX/AnchorY, 0.5 by
                    // default = the frame centre); the anchor is not clamped, matching the
                    // platform semantics where an anchor outside the frame is legal.
                    RectF frame = platform.CanvasFrame;
                    float anchorX = frame.X + (float)view.AnchorX * frame.Width;
                    float anchorY = frame.Y + (float)view.AnchorY * frame.Height;
                    TransformPivotObserved?.Invoke(anchorX, anchorY);
                    _canvas.Rotate((float)rotation, anchorX, anchorY);
                    if (scale != 1.0)
                    {
                        // ICanvas.Scale has no centre overload: translate around the anchor.
                        _canvas.Translate(anchorX, anchorY);
                        _canvas.Scale((float)scale, (float)scale);
                        _canvas.Translate(-anchorX, -anchorY);
                    }
                }
            }
            // The view's clip applies to the view and its subtree: clipping is one saved canvas
            // state, and the path is built in the view's frame (the drawing coordinates), so a
            // transformed view clips in its transformed space.
            if (view.Clip is { } clip)
            {
                RectF clipFrame = platform.CanvasFrame;
                if (clipFrame.Width > 0 && clipFrame.Height > 0)
                {
                    PathF clipPath = ClipPathFor(clip, clipFrame);
                    if (clipPath.Count > 0)
                    {
                        _canvas.SaveState();
                        _canvas.ClipPath(clipPath);
                        clipped = true;
                    }
                }
            }
            if (!CanSkipOwnDrawing(view, platform, opacity, shifted || movesPixels))
            {
                DrawCostTick?.Invoke(DrawKindOf(view, platform));
                platform.DrawShadow(_canvas);
                platform.Draw(_canvas);
                if (platform.ShellTitleViewRow is { } shellTitleView)
                {
                    // T15: the rich Shell.TitleView is arranged in canvas space by the chrome;
                    // draw it over the bar (its own content replaces the title text), clipped to
                    // the band so a descendant cannot spill into the page content.
                    DrawShellTitleView(shellTitleView, platform.FlowRightToLeft);
                }
            }
            if (platform.PopupVisible)
            {
                // Drawn last so the dropdown floats above the rest of the tree.
                _popupView = platform;
            }
            if (platform.ToolbarOverflowOpen)
            {
                // Same deferral for the toolbar overflow dropdown.
                _toolbarOverflowView = platform;
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
                OpenHarmonyFlowMap flyoutChildMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
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
                    DrawView(flyoutDetail, flyoutChildMap, flowRightToLeft, shifted);
                }
                if (platform.FlyoutPresented && flyoutContent is not null)
                {
                    RectF flyoutFrame = platform.CanvasFrame;
                    _canvas.FillColor = s_flyoutScrim;
                    _canvas.FillRectangle(flyoutFrame.X, flyoutFrame.Y, flyoutFrame.Width, flyoutFrame.Height);
                    _canvas.SaveState();
                    // The flyout panel is the start edge: physical left in LTR, right in RTL.
                    float panelWidth = platform.FlyoutWidth;
                    float panelX = flowRightToLeft ? flyoutFrame.Right - panelWidth : flyoutFrame.X;
                    _canvas.ClipRectangle(panelX, flyoutFrame.Y, panelWidth, flyoutFrame.Height);
                    DrawView(flyoutContent, flyoutChildMap, flowRightToLeft, shifted);
                    _canvas.RestoreState();
                }
                RestoreViewState(_canvas, transformed, previousAlpha, clipped);
                return;
            }
            if (platform.IsScrollView)
            {
                // Clip to the viewport and translate the content by the scroll offsets. The
                // translation can pull any child into the surface, so the subtree's cull is off.
                _canvas.SaveState();
                RectF scrollFrame = platform.CanvasFrame;
                _canvas.ClipRectangle(scrollFrame.X, scrollFrame.Y, scrollFrame.Width, scrollFrame.Height);
                _canvas.Translate(-platform.ScrollOffsetX, -platform.ScrollOffsetY);
                OpenHarmonyFlowMap scrollChildMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
                foreach (IView child in ChildrenInZOrder(view))
                {
                    DrawView(child, scrollChildMap, flowRightToLeft, shifted: true);
                }
                _canvas.RestoreState();
                RestoreViewState(_canvas, transformed, previousAlpha, clipped);
                return;
            }
            bool childShifted = shifted || movesPixels;
            // The child map (a view.Frame read plus the flow arithmetic) is only needed when
            // there is a first child: leaf nodes - the majority of a page - skip it entirely.
            var children = ChildrenInZOrder(view);
            if (!children.MoveNext())
            {
                RestoreViewState(_canvas, transformed, previousAlpha, clipped);
                return;
            }
            OpenHarmonyFlowMap childMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
            do
            {
                DrawView(children.Current, childMap, flowRightToLeft, childShifted);
            }
            while (children.MoveNext());
            RestoreViewState(_canvas, transformed, previousAlpha, clipped);
            return;
        }
        OpenHarmonyFlowMap noPlatformChildMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
        foreach (IView child in ChildrenInZOrder(view))
        {
            DrawView(child, noPlatformChildMap, flowRightToLeft, shifted);
        }
        RestoreViewState(_canvas, transformed, previousAlpha, clipped);
    }

    /// <summary>
    /// True when a view's own drawing can be skipped without changing the frame: it is fully
    /// invisible (a zero-opacity subtree) or its canvas rectangle lies outside the surface with
    /// no canvas shift in between, and it owns no deferred overlay that has to keep registering.
    /// The caller still descends into the children: a child is not bound by its parent's frame
    /// unless a clip says so, and each child runs this same test against its own rectangle.
    /// </summary>
    private bool CanSkipOwnDrawing(IView view, OpenHarmonyView platform, double opacity, bool shifted)
    {
        if (opacity <= 0)
        {
            return true;
        }
        if (shifted)
        {
            // An ancestor translation/scale/rotation (or a scroll offset) can move any of this
            // node's pixels into the surface, so its logical frame says nothing.
            return false;
        }
        RectF frame = platform.CanvasFrame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return false;  // the platform view already no-ops; keep the walk boring
        }
        if (frame.Right >= _drawSurface.X && frame.X <= _drawSurface.Right &&
            frame.Bottom >= _drawSurface.Y && frame.Y <= _drawSurface.Bottom)
        {
            return false;  // intersects the surface
        }
        // Deferred overlays (popup, toolbar overflow, flyout panel) and the carousel indicator
        // register while their owner draws, even when the owner itself is off the surface; the
        // rich Shell.TitleView row is arranged into the title bar and follows the same rule. An
        // Image draws through its decode pipeline: skipping an off-surface image would leave its
        // preview/final passes unrequested until it scrolls into view (the suite pins that a
        // render drives both passes), so image views always draw.
        if (platform.PopupVisible || platform.ToolbarOverflowOpen || platform.FlyoutOpen ||
            platform.ShellTitleViewRow is not null || view is Microsoft.Maui.Controls.CarouselView ||
            view is Microsoft.Maui.Controls.Image || platform.ImageBytes is { Length: > 0 })
        {
            return false;
        }
        // A shadow paints outside the frame: only skip when the shadow's own extent also misses
        // the surface. The (bindable) shadow read happens only for a node outside the surface.
        float margin = 0f;
        if (view.Shadow is { Opacity: > 0, Radius: > 0 } shadow)
        {
            margin = shadow.Radius +
                     (float)Math.Max(Math.Abs(shadow.Offset.X), Math.Abs(shadow.Offset.Y)) + 2f;
        }
        return frame.Right + margin < _drawSurface.X ||
               frame.X - margin > _drawSurface.Right ||
               frame.Bottom + margin < _drawSurface.Y ||
               frame.Y - margin > _drawSurface.Bottom;
    }

    /// <summary>
    /// Pops the canvas state pushed for a clipped view (see <see cref="DrawView"/>), then the
    /// transform state. Both use the canvas stack, so the pops stay in reverse order.
    /// </summary>
    private static void RestoreViewState(MauiCanvas canvas, bool transformed, float previousAlpha, bool clipped)
    {
        if (clipped)
        {
            canvas.RestoreState();
        }
        RestoreTransform(canvas, transformed, previousAlpha);
    }

    /// <summary>
    /// Pops the transform state pushed for a transformed view (see <see cref="DrawView"/>).
    /// ICanvas.SaveState restores the transform stack; the backend's alpha field sits outside
    /// that stack, so it is written back explicitly. Called at every exit of the subtree draw.
    /// </summary>
    private static void RestoreTransform(MauiCanvas canvas, bool transformed, float previousAlpha)
    {
        if (!transformed)
        {
            return;
        }
        canvas.Alpha = previousAlpha;
        canvas.RestoreState();
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
    private OpenHarmonyView? _toolbarOverflowView;
    private OpenHarmonyView? _flyoutPanelView;
    private readonly List<OpenHarmonyView> _carouselViews = new();
    private OpenHarmonyView? _textDragTarget;
    /// <summary>Active selection-handle drag: 0 none, 1 start handle, 2 end handle.</summary>
    private int _textHandleSide;
    /// <summary>Content-space minus screen-space X at press (entry inside a scrolled view).</summary>
    private float _textDragOffsetX;

    // GraphicsView interaction capture. A press on a GraphicsView captures the whole gesture
    // stream: every pointer of the stream is tracked (multi-touch) and the release/cancel ends the
    // capture. Hover is the pointer-over-the-tree case with no press behind it (the shell's mouse
    // path); a move that follows a finger down is a drag, never a hover.
    private OpenHarmonyView? _graphicsTarget;
    private OpenHarmonyView? _graphicsHover;
    private bool _graphicsDragStarted;
    private float _graphicsDownX;
    private float _graphicsDownY;
    private readonly List<int> _graphicsPointerIds = new();
    private readonly List<PointF> _graphicsPointerPoints = new();
    /// <summary>The tree the touch walk resolves against; kept for hover moves (no root argument).</summary>
    private IView? _lastRoot;
    /// <summary>True while any press is down: a finger move is a drag, not a hover.</summary>
    private bool _pointerDown;

    /// <summary>Upstream parity: a drag starts once a single pointer passed this many pixels.</summary>
    private const float GraphicsDragSlop = 3f;

    /// <summary>Active selection-handle drag (0 none, 1 start, 2 end) - diagnostics/tests.</summary>
    internal int TextHandleDrag => _textHandleSide;
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
        int count = OpenHarmonyCarouselViewHandler.MaterializeSlides(view).Count;
        if (count <= 1)
        {
            return;
        }
        RectF frame = carousel.CanvasFrame;
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

    /// <summary>
    /// Draws a materialized rich Shell.TitleView row (T15) as a real view. The chrome arranged
    /// it in canvas space, so it draws with the identity map; the band clip keeps the row's
    /// content inside the bar.
    /// </summary>
    private void DrawShellTitleView(OpenHarmonyShellTitleViewRow row, bool parentRightToLeft)
    {
        if (row.Frame.Width <= 0 || row.Frame.Height <= 0)
        {
            return;
        }
        _canvas.SaveState();
        _canvas.ClipRectangle(row.Frame.X, row.Frame.Y, row.Frame.Width, row.Frame.Height);
        DrawView(row.View, OpenHarmonyFlowMap.Identity, parentRightToLeft);
        _canvas.RestoreState();
        ShellTitleViewDrawn?.Invoke(row.View, row.Frame);
    }

    /// <summary>
    /// Draws the shell drawer: the panel background/scrim/text rows, then the rich rows as real
    /// views (T14). The rich rows are arranged in canvas space by their materializer, so they
    /// draw with the identity map (their own children mirror inside the row box) and are clipped
    /// to the panel.
    /// </summary>
    private void DrawFlyoutPanel(OpenHarmonyView panel)
    {
        panel.DrawFlyoutPanel(_canvas);
        int richRows = 0;
        if (panel.FlyoutRows.Count > 0)
        {
            RectF panelRect = panel.FlyoutPanelRect();
            if (panelRect.Width > 0 && panelRect.Height > 0)
            {
                _canvas.SaveState();
                _canvas.ClipRectangle(panelRect.X, panelRect.Y, panelRect.Width, panelRect.Height);
                foreach (OpenHarmonyFlyoutRow row in panel.FlyoutRows)
                {
                    if (row.View is { } rowView)
                    {
                        DrawView(rowView, OpenHarmonyFlowMap.Identity, panel.FlowRightToLeft);
                        richRows++;
                    }
                }
                _canvas.RestoreState();
            }
        }
        FlyoutPanelDrawn?.Invoke(panel, richRows);
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
        if (entry.VirtualView is Microsoft.Maui.Controls.InputView input)
        {
            input.CursorPosition = caret;
            input.SelectionLength = length;
        }
        // Keep the shell's input control caret on the managed caret so a following IME
        // composition uses the same insertion offset (and typing lands where the user tapped).
        OpenHarmonyBridge.SetKeyboardCaret(caret);
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

    /// <summary>The tracked touch points of the captured GraphicsView gesture, in pointer-down order.</summary>
    private PointF[] GraphicsPoints()
    {
        var points = new PointF[_graphicsPointerIds.Count];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = _graphicsPointerPoints[i];
        }
        return points;
    }

    /// <summary>Adds or moves a pointer of the captured GraphicsView gesture.</summary>
    private void TrackGraphicsPointer(int pointerId, float x, float y)
    {
        int index = _graphicsPointerIds.IndexOf(pointerId);
        if (index >= 0)
        {
            _graphicsPointerPoints[index] = new PointF(x, y);
            return;
        }
        _graphicsPointerIds.Add(pointerId);
        _graphicsPointerPoints.Add(new PointF(x, y));
    }

    private void RemoveGraphicsPointer(int pointerId)
    {
        int index = _graphicsPointerIds.IndexOf(pointerId);
        if (index >= 0)
        {
            _graphicsPointerIds.RemoveAt(index);
            _graphicsPointerPoints.RemoveAt(index);
        }
    }

    /// <summary>Ends the captured gesture (release of the last pointer, or a cancel).</summary>
    private void EndGraphicsCapture()
    {
        _graphicsTarget = null;
        _graphicsDragStarted = false;
        _graphicsPointerIds.Clear();
        _graphicsPointerPoints.Clear();
    }

    private void EndGraphicsHover()
    {
        if (_graphicsHover is { } hover)
        {
            _graphicsHover = null;
            hover.GraphicsHoverEnd?.Invoke();
        }
    }

    /// <summary>
    /// Hover for a move with no press behind it: the deepest GraphicsView under the pointer gets
    /// StartHover on enter, MoveHover on every report and EndHover when the pointer leaves it.
    /// Returns true when the move belonged to a GraphicsView (so the frame repaints).
    /// </summary>
    private bool UpdateGraphicsHover(float x, float y)
    {
        if (_lastRoot is null)
        {
            return false;
        }
        TouchWalk walk = default;
        CollectTouchTargets(_lastRoot, x, y, true, TouchTargets.Graphics, ref walk,
            OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(_lastRoot!));
        OpenHarmonyView? target = walk.Graphics;
        if (ReferenceEquals(target, _graphicsHover))
        {
            if (target is null)
            {
                return false;
            }
            target.GraphicsHoverMove?.Invoke(new[] { new PointF(x, y) });
            return true;
        }
        _graphicsHover?.GraphicsHoverEnd?.Invoke();
        _graphicsHover = target;
        target?.GraphicsHoverStart?.Invoke(new[] { new PointF(x, y) });
        // Leaving a view is an interaction too: the old hover state must repaint.
        return true;
    }

    /// <summary>Handles a touch/mouse move: drags the slider or scrolls the scroll view captured on down.</summary>
    public bool HandleMove(float x, float y) => HandleMove(x, y, 0);

    internal bool HandleMove(float x, float y, int pointerId)
    {
        // A move with no press behind it is a hover (the shell's mouse path).
        if (_graphicsTarget is null && !_pointerDown && UpdateGraphicsHover(x, y))
        {
            return true;
        }
        // A captured GraphicsView owns the moves of the whole gesture stream.
        if (_graphicsTarget is { IsGraphicsView: true } graphics)
        {
            TrackGraphicsPointer(pointerId, x, y);
            // Upstream parity: a single pointer must pass the slop before a drag starts; with a
            // second pointer down the gesture is unambiguously a drag.
            if (!_graphicsDragStarted && _graphicsPointerIds.Count == 1 &&
                (Math.Abs(x - _graphicsDownX) > GraphicsDragSlop || Math.Abs(y - _graphicsDownY) > GraphicsDragSlop))
            {
                _graphicsDragStarted = true;
            }
            if (_graphicsDragStarted)
            {
                // A drag never counts as a tap for the recognizers on the same view.
                _moved = true;
                graphics.GraphicsDragInteraction?.Invoke(GraphicsPoints());
            }
            return true;
        }
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
            // The press recorded the scroll-container delta (content space vs. screen space) so a
            // selection drag stays aligned when the entry lives inside a scrolled view. The move
            // always applies: with a handle drag the caret can stay fixed while the selection
            // start moves (the cursor comparison alone would miss that).
            ApplyTextSelection(textEntry, textEntry.CursorIndexFromX(x + _textDragOffsetX));
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
        scroll.ScrollOffsetY = OpenHarmonyScrollPhysics.DragOffset(scroll, scroll.ScrollOffsetY, delta, maxOffset);
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
                    _dropCapableSeen ? FindDropTarget(sessionRoot, x, y,
                        OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(sessionRoot)) : null);
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
                _dropCapableSeen ? FindDropTarget(dragRoot, x, y,
                    OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(dragRoot)) : null);
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
        => HandleTouch(root, down, up, x, y, 0);

    internal bool HandleTouch(IView root, bool down, bool up, float x, float y, int pointerId)
    {
        _lastRoot = root;
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

        // The Window.TitleBar row is window chrome above the page tree: a touch that lands on it
        // resolves against the TitleBar subtree, and the leading slot maps onto the window's back
        // affordance. The slot wins over the app's leading content, like the navigation bar's
        // back region wins over its children. When the shell reports app-managed decorations (N6),
        // the trailing caption buttons dispatch minimize/maximize/close - release-inside semantics
        // like a native caption - and a press no template child consumed starts the window move.
        if (ResolveTitleBar(root) is { IsVisible: true } titleBar && titleBar.Contains(x, y))
        {
            if (down && titleBar.ShowsBack && titleBar.InBackButton(x, y) && titleBar.BackTapped())
            {
                return true;
            }
            if (titleBar.HitSystemButton(x, y) is { } decorButton)
            {
                if (down)
                {
                    titleBar.PressDecorButton(decorButton);
                }
                else if (up)
                {
                    titleBar.ReleaseDecorButton(x, y);
                }
                return true;
            }
            bool rowHandled = HandleTouchCore(titleBar.View, down, up, x, y,
                OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(titleBar.View));
            if (down && !rowHandled && titleBar.InDragRegion(x, y))
            {
                titleBar.StartWindowDrag();
            }
            // A press on the row never leaks into the page underneath.
            return rowHandled || down || up;
        }

        // T15: a rich Shell.TitleView owns the touches that land on its band (its own content
        // first). Outside the band the shell chrome (back affordance, hamburger, toolbar items,
        // tab bar) stays reachable through the normal path.
        if (ResolveShellTitleView(root) is { ShellTitleViewRow: { } shellTitleRow } shellView &&
            shellTitleRow.Frame.Contains(x, y) &&
            RouteShellTitleViewTouch(shellView, shellTitleRow, down, up, x, y))
        {
            return true;
        }

        // Pointer gestures (pointer recognizers receive enter/press on touch down, release/exit on up).
        if (down || up)
        {
            TouchWalk pointerWalk = default;
            CollectTouchTargets(root, x, y, true, TouchTargets.Pointer, ref pointerWalk,
                OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(root));
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
        if (_toolbarOverflowView is not { ToolbarOverflowOpen: true })
        {
            wanted |= TouchTargets.ToolbarOverflow;
        }
        if (_flyoutPanelView is not { FlyoutOpen: true })
        {
            wanted |= TouchTargets.Flyout;
        }
        if (down)
        {
            wanted |= TouchTargets.Scroll | TouchTargets.Slider | TouchTargets.Drag
                | TouchTargets.Gesture | TouchTargets.Drop | TouchTargets.Graphics;
        }
        TouchWalk walk = default;
        if (wanted != TouchTargets.None)
        {
            CollectTouchTargets(root, x, y, true, wanted, ref walk,
                OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(root));
            if ((wanted & TouchTargets.Popup) != 0)
            {
                _popupView = walk.Popup;
            }
            if ((wanted & TouchTargets.ToolbarOverflow) != 0)
            {
                _toolbarOverflowView = walk.ToolbarOverflow;
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
            // The release of the tap that opened the drawer lands in the hamburger corner:
            // consume it (and a press there) before the drawer rows see the touch.
            if (flyoutPanel.InHamburger(x, y))
            {
                return true;
            }
            // Rich rows own their content: a button/entry/gesture inside an item template or a
            // header view handles the touch before the flat row -> shell item selection.
            if (RouteFlyoutRowTouch(flyoutPanel, down, up, x, y))
            {
                return true;
            }
            if (down)
            {
                // The press belongs to the drawer even when no row content handled it; the row
                // selection is decided on the release (the flat fallback below).
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
        if (_toolbarOverflowView is { ToolbarOverflowOpen: true } toolbarOverflow)
        {
            // The open toolbar dropdown owns the touch: a row activates its secondary item, a
            // tap outside (or on the affordance, which toggles it closed) dismisses. The bar
            // itself is below the dropdown, so this runs before the content routing.
            if (down)
            {
                _downX = x;
                _downY = y;
                _moved = false;
                if (toolbarOverflow.InToolbarMore(x, y))
                {
                    toolbarOverflow.SetToolbarOverflow(false);
                }
                return true;
            }
            if (up && !_moved)
            {
                int row = toolbarOverflow.ToolbarOverflowIndexAt(x, y);
                if (row >= 0)
                {
                    toolbarOverflow.ActivateToolbarOverflow(row);
                }
                else if (!toolbarOverflow.InToolbarMore(x, y))
                {
                    toolbarOverflow.SetToolbarOverflow(false);
                }
            }
            return true;
        }
        // The drag target is resolved once at the top level; the recursive walk must not
        // overwrite it as it descends into leaves.
        bool graphicsHandled = false;
        if (down)
        {
            _downX = x;
            _downY = y;
            _moved = false;
            _pointerDown = true;
            // A press on a GraphicsView captures the gesture stream: later pointers join the
            // tracked set and the release of the last pointer (or a cancel) ends the capture.
            if (_graphicsTarget is null)
            {
                _graphicsTarget = walk.Graphics;
                _graphicsDragStarted = false;
                _graphicsPointerIds.Clear();
                _graphicsPointerPoints.Clear();
            }
            if (_graphicsTarget is { } graphicsTarget)
            {
                _graphicsDownX = x;
                _graphicsDownY = y;
                EndGraphicsHover();
                TrackGraphicsPointer(pointerId, x, y);
                graphicsTarget.GraphicsStartInteraction?.Invoke(GraphicsPoints());
                graphicsHandled = true;
            }
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
            _pointerDown = false;
            if (_graphicsTarget is { } graphicsTarget)
            {
                // Upstream parity: the release carries the point and whether it is inside the
                // frame; the pointer is dropped after the callback and the capture ends with the
                // last pointer.
                TrackGraphicsPointer(pointerId, x, y);
                graphicsTarget.GraphicsEndInteraction?.Invoke(GraphicsPoints(), graphicsTarget.HitTest(x, y));
                RemoveGraphicsPointer(pointerId);
                if (_graphicsPointerIds.Count == 0)
                {
                    EndGraphicsCapture();
                }
                graphicsHandled = true;
            }
            if (_dragSession is { } dragSession)
            {
                // The release completes the drag over the view under the pointer (if any).
                OpenHarmonyDragAndDrop.Drop(dragSession, _dropCapableSeen ? FindDropTarget(root, x, y,
                    OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(root)) : null);
                _dragSession = null;
                _dragCandidate = null;
                _dragRoot = null;
                _dragRejected = true;
                return true;
            }
            _textDragTarget = null;
            _textHandleSide = 0;
            OpenHarmonyScrollPhysics.ClearReleaseSuppression();
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
        return HandleTouchCore(root, down, up, x, y,
            OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(root)) | graphicsHandled;
    }

    /// <summary>
    /// Cancels a captured GraphicsView gesture (the shell reporting a canceled touch stream):
    /// IGraphicsView.CancelInteraction fires and the capture is dropped without a release event.
    /// </summary>
    internal bool HandleCancel(float x, float y)
    {
        _pointerDown = false;
        ResolveTitleBar(_lastRoot)?.CancelDecorPress();
        if (_graphicsTarget is not { } graphics)
        {
            return false;
        }
        EndGraphicsCapture();
        graphics.GraphicsCancelInteraction?.Invoke();
        return true;
    }

    /// <summary>
    /// Routes a touch inside the shell drawer into the rich row under the point (T14). The row
    /// views are arranged in canvas space, so they hit-test with the identity map; the panel's
    /// resolved direction rides along so a MatchParent row's own children mirror inside the row.
    /// Returns false when the point is not inside a rich row or its content does not handle the
    /// touch (the caller then falls back to the flat row -&gt; shell item selection).
    /// </summary>
    /// <summary>
    /// The nearest shell platform view on the root's ancestor chain that carries a rich
    /// Shell.TitleView row (T15), or null. The host's touch root can be the current page
    /// (modal-aware), so the walk climbs to the shell that arranged the title view.
    /// </summary>
    private static OpenHarmonyView? ResolveShellTitleView(IView? root)
    {
        for (Element? element = root as Element; element is not null; element = element.Parent)
        {
            if (element is IView view &&
                view.Handler?.PlatformView is OpenHarmonyView { ShellTitleViewRow: not null } platform)
            {
                return platform;
            }
        }
        return null;
    }

    /// <summary>
    /// Routes a touch that landed on the rich Shell.TitleView band into the materialized view
    /// (its own content handles it first, like a flyout row).
    /// </summary>
    private bool RouteShellTitleViewTouch(OpenHarmonyView shell, OpenHarmonyShellTitleViewRow row,
        bool down, bool up, float x, float y)
    {
        if (row.View is not IView view)
        {
            return false;
        }
        if (down)
        {
            // The same press bookkeeping as the content path, so a title-view tap does not
            // inherit the movement of a previous gesture.
            _downX = x;
            _downY = y;
            _moved = false;
        }
        return HandleTouchCore(view, down, up, x, y, OpenHarmonyFlowMap.Identity, shell.FlowRightToLeft);
    }

    private bool RouteFlyoutRowTouch(OpenHarmonyView panel, bool down, bool up, float x, float y)
    {
        if (panel.FlyoutRows.Count == 0)
        {
            return false;
        }
        foreach (OpenHarmonyFlyoutRow row in panel.FlyoutRows)
        {
            if (row.View is not IView rowView || !row.Frame.Contains(x, y))
            {
                continue;
            }
            if (down)
            {
                // The same press bookkeeping as the content path, so a row's tap does not
                // inherit the movement of a previous gesture.
                _downX = x;
                _downY = y;
                _moved = false;
            }
            return HandleTouchCore(rowView, down, up, x, y, OpenHarmonyFlowMap.Identity, panel.FlowRightToLeft);
        }
        return false;
    }

    private bool HandleTouchCore(IView view, bool down, bool up, float x, float y,
        in OpenHarmonyFlowMap map, bool parentRightToLeft)
    {
        if (BlocksInput(view))
        {
            // Input-transparent view (and, for a cascading layout, its whole subtree).
            return false;
        }
        bool flowRightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(view, parentRightToLeft);
        if (view.Handler?.PlatformView is OpenHarmonyView flowPlatform)
        {
            // The frame checks below (clip, text entry, gestures) all run in canvas space.
            flowPlatform.SetFlowContext(map, flowRightToLeft);
        }
        // A non-cascading transparent layout still routes touches to its children; only its own
        // handlers below are skipped.
        bool transparent = view.InputTransparent;
        // A clip excludes the point from the view and its subtree: hit-testing follows the
        // drawing, which does not paint a clipped-away point.
        if (view.Handler?.PlatformView is OpenHarmonyView clipped
            && view.Clip is not null
            && !ClipContainsPoint(view, clipped.CanvasFrame, x, y))
        {
            return false;
        }
        bool handled = false;
        if (!transparent && view.Handler?.PlatformView is OpenHarmonyView { ShowsTitleBar: true } chromeView)
        {
            if (down && chromeView.InBackButton(x, y))
            {
                chromeView.BackTapped?.Invoke();
                return true;
            }
        }
        if (!transparent && view.Handler?.PlatformView is OpenHarmonyView { ShowsHamburger: true } shellView)
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
            OpenHarmonyFlowMap flyoutChildMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
            foreach (IView child in ChildrenOf(view))
            {
                bool isFlyout = ReferenceEquals(child, (view as Microsoft.Maui.Controls.FlyoutPage)?.Flyout);
                bool isDetail = !isFlyout;
                if (flyoutPage.FlyoutPresented ? isFlyout : isDetail)
                {
                    handled |= HandleTouchCore(child, down, up, x, y, flyoutChildMap, flowRightToLeft);
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
        OpenHarmonyFlowMap childMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
        foreach (IView child in ChildrenInZOrder(view))
        {
            handled |= HandleTouchCore(child, down, up, childX, childY, childMap, flowRightToLeft);
        }
        if (!transparent && view.Handler?.PlatformView is OpenHarmonyView { IsTextEntry: true } textEntry && !_moved &&
            textEntry.CanvasFrame.Contains(x, y) && textEntry.IsFocused)
        {
            if (down)
            {
                // Pressing inside a text entry moves the cursor and starts a selection drag;
                // pressing on a selection handle (round handles below the text line) drags that
                // endpoint instead.
                _textDragOffsetX = x - _downX;
                int handle = textEntry.SelectionHandleHit(x, y);
                if (handle != 0)
                {
                    _textHandleSide = handle;
                    textEntry.TextAnchor = handle == 1 ? textEntry.SelectionEnd : textEntry.SelectionStart;
                }
                else
                {
                    _textHandleSide = 0;
                    textEntry.TextAnchor = -1;
                    ApplyTextSelection(textEntry, textEntry.CursorIndexFromX(x));
                }
                // Selection work owns the gesture: stop any in-flight inertia and keep the
                // enclosing scroll view from flinging when the finger lifts.
                OpenHarmonyScrollPhysics.CancelAll();
                OpenHarmonyScrollPhysics.SuppressReleaseFling();
                _textDragTarget = textEntry;
                handled = true;
            }
            else if (up)
            {
                _textDragTarget = null;
                _textHandleSide = 0;
                OpenHarmonyScrollPhysics.ClearReleaseSuppression();
            }
        }
        if (!transparent && view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            // Views handle their own touches (buttons, navigation bars); plain views ignore them.
            // A drag never counts as a tap.
            handled |= platform.OnTouch(down, up && !_moved, x, y);
            // Scroll views consume touches inside them (drag scrolling).
            handled |= platform.IsScrollView && platform.CanvasFrame.Contains(x, y);
            // Gesture recognizers run for the deepest view under the finger.
            if (platform.CanvasFrame.Contains(x, y) && OpenHarmonyGestures.HasGestures(view))
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
        Graphics = 1 << 8,
        ToolbarOverflow = 1 << 9,
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
        public OpenHarmonyView? ToolbarOverflow;
        public OpenHarmonyView? Flyout;
        public OpenHarmonyView? Graphics;

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
    private void CollectTouchTargets(IView view, float x, float y, bool positioned, TouchTargets wanted, ref TouchWalk walk,
        in OpenHarmonyFlowMap map, bool parentRightToLeft)
    {
        if (BlocksInput(view))
        {
            // Input-transparent view (cascading layout: its whole subtree).
            return;
        }
        bool flowRightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(view, parentRightToLeft);
        OpenHarmonyView? platform = view.Handler?.PlatformView as OpenHarmonyView;
        if (platform is not null)
        {
            // The positional checks below run against the canvas-space frame the map produces.
            platform.SetFlowContext(map, flowRightToLeft);
            if ((wanted & TouchTargets.Popup) != 0 && walk.Popup is null && platform.PopupVisible)
            {
                walk.Popup = platform;
            }
            if ((wanted & TouchTargets.ToolbarOverflow) != 0 && walk.ToolbarOverflow is null && platform.ToolbarOverflowOpen)
            {
                walk.ToolbarOverflow = platform;
            }
            if ((wanted & TouchTargets.Flyout) != 0 && walk.Flyout is null && platform.FlyoutOpen)
            {
                walk.Flyout = platform;
            }
            if (positioned && !platform.CanvasFrame.Contains(x, y))
            {
                positioned = false;
            }
            if (positioned && view.Clip is not null && !ClipContainsPoint(view, platform.CanvasFrame, x, y))
            {
                // The clip excludes the point from the view and its subtree (the drawing pass
                // does not paint a clipped-away point either).
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
        OpenHarmonyView? graphicsBefore = walk.Graphics;
        OpenHarmonyFlowMap childMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
        foreach (IView child in ChildrenInZOrder(view))
        {
            CollectTouchTargets(child, localX, localY, positioned, wanted, ref walk, childMap, flowRightToLeft);
        }
        if (!positioned)
        {
            return;
        }
        if (view.InputTransparent)
        {
            // Non-cascading transparent layout: its children were visited above, the view itself
            // never matches an input query.
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
        if ((wanted & TouchTargets.Graphics) != 0 && ReferenceEquals(walk.Graphics, graphicsBefore)
            && platform is { IsGraphicsView: true })
        {
            walk.Graphics = platform;
        }
    }

    private IView? _pointerHover;

    /// <summary>
    /// Hover for mouse moves (the NDK mouse callback arrives as a touch move): pointer recognizers
    /// receive Exited/Entered when the hovered view changes and Moved on every report.
    /// </summary>
    public bool HandlePointerMove(IView root, float x, float y)
    {
        _lastRoot = root;
        IView? target = FindPointerTarget(root, x, y,
            OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(root));
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
        => FindPinchTarget(root, x, y, OpenHarmonyFlowMap.Identity, OpenHarmonyFlowDirection.IsRightToLeftRoot(root)) is { } target
            && OpenHarmonyPinch.Dispatch(target, phase, scale, x, y);

    private IView? FindPinchTarget(IView view, float x, float y,
        in OpenHarmonyFlowMap map, bool parentRightToLeft)
    {
        if (BlocksInput(view))
        {
            return null;
        }
        bool flowRightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(view, parentRightToLeft);
        IView? found = null;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            platform.SetFlowContext(map, flowRightToLeft);
            if (!platform.CanvasFrame.Contains(x, y)
                || (view.Clip is not null && !ClipContainsPoint(view, platform.CanvasFrame, x, y)))
            {
                return null;
            }
        }
        OpenHarmonyFlowMap childMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
        foreach (IView child in ChildrenInZOrder(view))
        {
            found = FindPinchTarget(child, x, y, childMap, flowRightToLeft) ?? found;
        }
        return found ?? (!view.InputTransparent && OpenHarmonyPinch.HasPinch(view) ? view : null);
    }

    /// <summary>Deepest view containing the point that owns a pointer recognizer.</summary>
    private IView? FindPointerTarget(IView view, float x, float y,
        in OpenHarmonyFlowMap map, bool parentRightToLeft)
    {
        if (BlocksInput(view))
        {
            return null;
        }
        bool flowRightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(view, parentRightToLeft);
        IView? found = null;
        float localX = x;
        float localY = y;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            platform.SetFlowContext(map, flowRightToLeft);
            if (!platform.CanvasFrame.Contains(x, y)
                || (view.Clip is not null && !ClipContainsPoint(view, platform.CanvasFrame, x, y)))
            {
                return null;
            }
            if (platform.IsScrollView)
            {
                localX += platform.ScrollOffsetX;
                localY += platform.ScrollOffsetY;
            }
        }
        OpenHarmonyFlowMap childMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
        foreach (IView child in ChildrenInZOrder(view))
        {
            found = FindPointerTarget(child, localX, localY, childMap, flowRightToLeft) ?? found;
        }
        return found ?? (!view.InputTransparent && OpenHarmonyPointer.HasPointer(view) ? view : null);
    }

    /// <summary>Deepest view containing the point that owns a usable drop recognizer.</summary>
    private IView? FindDropTarget(IView view, float x, float y,
        in OpenHarmonyFlowMap map, bool parentRightToLeft)
    {
        if (BlocksInput(view))
        {
            return null;
        }
        bool flowRightToLeft = OpenHarmonyFlowDirection.IsRightToLeft(view, parentRightToLeft);
        IView? found = null;
        float localX = x;
        float localY = y;
        if (view.Handler?.PlatformView is OpenHarmonyView platform)
        {
            platform.SetFlowContext(map, flowRightToLeft);
            if (!platform.CanvasFrame.Contains(x, y)
                || (view.Clip is not null && !ClipContainsPoint(view, platform.CanvasFrame, x, y)))
            {
                return null;
            }
            if (platform.IsScrollView)
            {
                localX += platform.ScrollOffsetX;
                localY += platform.ScrollOffsetY;
            }
        }
        OpenHarmonyFlowMap childMap = map.ForChild(LogicalFrameOf(view), flowRightToLeft);
        foreach (IView child in ChildrenInZOrder(view))
        {
            found = FindDropTarget(child, localX, localY, childMap, flowRightToLeft) ?? found;
        }
        return found ?? (!view.InputTransparent && OpenHarmonyDragAndDrop.HasDrop(view) ? view : null);
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

/// <summary>
/// Layout semantics (T5) the compositor reads from the virtual view every frame instead of
/// storing on the platform view: Clip, AnchorX/AnchorY, InputTransparent, ZIndex and (T6) the
/// flow direction, whose resolved value mirrors a whole subtree's placement. A property
/// change has to request a repaint or the next frame is only drawn when some other input
/// arrives, exactly like <see cref="OpenHarmonyShadow"/>. The default ViewMapper maps Clip,
/// AnchorX/AnchorY, InputTransparent and FlowDirection to no-ops in this platform-less slice and
/// has no ZIndex entry at all, so each key gets one wrapper that runs the previous mapping (when
/// one exists) and then requests a frame. Installed once, from the renderer constructor.
/// </summary>
internal static class OpenHarmonyLayoutRedraw
{
    private static bool s_installed;

    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        if (ViewHandler.ViewMapper is not PropertyMapper<IView, IViewHandler> mapper)
        {
            return;
        }
        Hook(mapper, nameof(IView.Clip));
        Hook(mapper, nameof(IView.ZIndex));
        Hook(mapper, nameof(ITransform.AnchorX));
        Hook(mapper, nameof(ITransform.AnchorY));
        Hook(mapper, nameof(IView.InputTransparent));
        // The flow direction is read per frame by the walk (the resolved direction of every
        // ancestor changes the whole subtree's placement), so a direction change must repaint.
        Hook(mapper, nameof(IView.FlowDirection));
    }

    private static void Hook(PropertyMapper<IView, IViewHandler> mapper, string key)
    {
        // GetProperty returns null for a key the default map does not carry (ZIndex); the indexer
        // would throw on it.
        Action<IViewHandler, IView>? previous = mapper.GetProperty(key);
        mapper[key] = (handler, view) =>
        {
            try
            {
                previous?.Invoke(handler, view);
            }
            catch (Exception)
            {
                // The default rc.1 mapping has no OpenHarmony platform view contract; ignore.
            }
            try
            {
                OpenHarmonyBridge.RequestRedraw();
            }
            catch (Exception)
            {
                // No host: the next input/frame event repaints anyway.
            }
        };
    }
}
