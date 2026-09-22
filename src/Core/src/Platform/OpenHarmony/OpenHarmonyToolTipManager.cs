// Desktop-style tooltips for the OpenHarmony compositor.
//
// MAUI rc.1 surface (verified against Microsoft.Maui.Controls 11.0.0-rc.1.26451.6):
//   * Microsoft.Maui.IToolTipElement.ToolTip (Microsoft.Maui.ToolTip, Content object) - Element
//     implements it over the attached property below;
//   * Microsoft.Maui.Controls.ToolTipProperties.TextProperty (object) + GetText/SetText;
//   * Microsoft.Maui.Handlers.ViewHandler.MapToolTip(IViewHandler, IView) - the shared
//     ViewHandler.ViewMapper has a "ToolTip" entry, and IElementHandler.UpdateValue("ToolTip")
//     routes through it (verified with the live handler: ToolTipProperties.SetText on a
//     connected element reaches the entry), so replacing that entry observes every set/clear.
// The slice has no native ArkUI tooltip node per view (all views are composited into the one
// XComponent), so this manager draws the popup itself: it chains the renderer's public
// SurfacePresent hook (the documented "tests report a virtual surface" seam) and paints the
// popup on the renderer's canvas right before the frame is presented. No renderer or host edit.
//
// Hover source: the renderer routes Entered/Exited/Moved/Pressed to the deepest view that owns a
// PointerGestureRecognizer (OpenHarmonyPointer.HasPointer/FindPointerTarget). Tooltips are not
// gesture recognizers in rc.1, so the manager attaches one plain PointerGestureRecognizer to
// each element that has a tooltip (and removes it when the tooltip is cleared). The recognizer
// never consumes taps/scrolls and is not part of the accessibility tree, so focus and a11y
// behaviour are unchanged. Documented trade-off (the rc.1 renderer has no tooltip hook): a
// tooltip element becomes the deepest pointer/gesture candidate for its own rectangle, so an
// ancestor's PointerGestureRecognizer or Pan/SwipeGestureRecognizer no longer receives events
// that land on the tooltip element (the gesture-target search picks the deepest view with any
// recognizer and checks its pan/swipe). Taps (platform OnTouch / TapGestureRecognizer), a11y and
// clicks are unaffected; the harness's pointer, pan, swipe and a11y checks stay green.
//
// Behaviour: after ShowDelayMs of hover the popup appears near the pointer; it hides on pointer
// exit, on a press on the element, and on any platform touch-down (the global press hook is
// subscribed only while a tooltip is registered). While the popup is visible it redraws with the
// normal frames; when nothing is registered there is no recognizer, no timer, no frame hook and
// no present hook (inert). `A tooltip set after the handler is connected is picked up because
// UpdateValue("ToolTip") reaches the replaced mapper entry (verified); the content is re-read on
// hover, so a changed tooltip text is reflected without a re-registration.
//
// Documented limitations (rc.1 / slice surface):
//   * ToolTip.Content is rendered as text (ToString); a Content that is a View/ImageSource is not
//     rendered as rich content (the slice has no native tooltip container to host it).
//   * hover requires the element to be a Microsoft.Maui.Controls.View (the renderer's pointer
//     hit-test only names Views with recognizers); a pure IView with a tooltip is counted in
//     UnsupportedElements and gets no popup.
//   * the renderer dispatches no Exited when the pointer leaves the window, so a popup hides on
//     the next dispatch/touch rather than on focus loss.
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Desktop-style tooltips for elements whose rc.1 ToolTip/ToolTipProperties.Text is set. The
/// manager replaces the shared ViewMapper "ToolTip" entry, attaches one private pointer
/// recognizer per tooltip element for hover, and paints the popup on the compositor canvas
/// through the renderer's SurfacePresent hook. Inert while no tooltip is registered.
/// </summary>
internal static class OpenHarmonyToolTipManager
{
    /// <summary>The shared ViewMapper key (nameof(IToolTipElement.ToolTip)).</summary>
    internal const string MapperKey = "ToolTip";

    /// <summary>Hover time before the popup appears (ms).</summary>
    internal static int ShowDelayMs { get; set; } = 650;

    /// <summary>Time source (ms); tests replace it to advance the show delay deterministically.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>Overlay canvas factory; tests substitute a recording rasterizer. Null = the
    /// real MauiCanvas bound to the active host frame (a no-op without a native host).</summary>
    internal static Func<ICanvas>? OverlayCanvasFactory { get; set; }

    // Popup style (device pixels).
    private const float PopupFontSize = 22f;
    private const float PopupPadding = 14f;
    private const float PopupMaxWidth = 420f;
    private const float PopupPointerOffsetX = 18f;
    private const float PopupPointerOffsetY = 26f;
    private static readonly Color PopupBackground = Color.FromArgb("#F01B1B1B");
    private static readonly Color PopupBorder = Color.FromArgb("#60FFFFFF");
    private static readonly Color PopupTextColor = Colors.White;

    /// <summary>One tooltip element: weak (the manager must not root a removed page) plus the
    /// private recognizer that the renderer hit-tests. rc.1's PointerGestureRecognizer is sealed,
    /// so the manager uses a plain instance (identified by this registration only).</summary>
    private sealed class Registration
    {
        public required WeakReference<View> Element { get; init; }
        public required PointerGestureRecognizer Recognizer { get; init; }
        public string Text { get; set; } = string.Empty;
        public float PointerX { get; set; }
        public float PointerY { get; set; }
        public bool Visible { get; set; }
        public RectF Rect { get; set; }
    }

    private static readonly object s_sync = new();
    private static readonly List<Registration> s_registrations = new();
    private static bool s_installed;
    private static Action<IViewHandler, IView>? s_previousMapper;
    private static bool s_pressHook;
    private static Registration? s_pending;
    private static Registration? s_visible;
    private static Timer? s_timer;
    private static long s_dueAtMs;
    private static bool s_presentHook;
    private static Action? s_presentPrevious;
    private static readonly Action s_presentDelegate = OnSurfacePresent;

    /// <summary>Tooltip elements tracked since load.</summary>
    internal static int Count
    {
        get { lock (s_sync) { PruneLocked(); return s_registrations.Count; } }
    }

    /// <summary>The popup currently shown (null when none).</summary>
    internal static bool IsVisible
    {
        get { lock (s_sync) { return s_visible is not null; } }
    }

    /// <summary>Text of the visible popup (null when none).</summary>
    internal static string? VisibleText
    {
        get { lock (s_sync) { return s_visible?.Text; } }
    }

    /// <summary>Popup rectangle of the visible tooltip (empty when none).</summary>
    internal static RectF VisibleRect
    {
        get { lock (s_sync) { return s_visible?.Rect ?? default; } }
    }

    /// <summary>A show is waiting for the delay.</summary>
    internal static bool IsPending
    {
        get { lock (s_sync) { return s_pending is not null; } }
    }

    /// <summary>The global press-hide hook is subscribed (only while a tooltip is registered).</summary>
    internal static bool IsHooked => s_pressHook;

    /// <summary>The SurfacePresent overlay hook is installed (only while pending/visible).</summary>
    internal static bool IsPresentHooked => s_presentHook;

    /// <summary>A delay timer exists (only while a show is pending/visible).</summary>
    internal static bool HasTimer
    {
        get { lock (s_sync) { return s_timer is not null; } }
    }

    /// <summary>Hovers that scheduled a show.</summary>
    internal static int Scheduled { get; private set; }

    /// <summary>Popups shown.</summary>
    internal static int Shown { get; private set; }

    /// <summary>Popups hidden.</summary>
    internal static int Hidden { get; private set; }

    /// <summary>Overlay paint passes that drew the visible popup.</summary>
    internal static int Draws { get; private set; }

    /// <summary>Dead registrations pruned.</summary>
    internal static int Pruned { get; private set; }

    /// <summary>Elements with a tooltip that the pointer path cannot hit-test (non-Controls IView).</summary>
    internal static int UnsupportedElements { get; private set; }

    /// <summary>
    /// Replaces the shared ViewMapper "ToolTip" entry once. Idempotent; called from the module
    /// initializer so a plain slice build/load gets tooltips without a registrar edit. The
    /// consolidation pass can call this from UseOpenHarmony instead.
    /// </summary>
    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        if (ViewHandler.ViewMapper is PropertyMapper<IView, IViewHandler> mapper)
        {
            s_previousMapper = mapper[MapperKey];
            mapper[MapperKey] = OnToolTipMapped;
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => Install();

    /// <summary>Mapper entry: records/removes the element's tooltip, then runs the previous
    /// mapping (the rc.1 default is platform-specific and kept for behaviour parity).</summary>
    private static void OnToolTipMapped(IViewHandler handler, IView view)
    {
        try
        {
            if (view is View controlsView)
            {
                string? text = ReadToolTipText(view);
                if (string.IsNullOrEmpty(text))
                {
                    Unregister(controlsView);
                }
                else
                {
                    Register(controlsView, text);
                }
            }
            else if (ReadToolTipText(view) is { Length: > 0 })
            {
                UnsupportedElements++;
            }
        }
        catch (Exception)
        {
            // Bookkeeping must never break a handler mapping.
        }
        try
        {
            s_previousMapper?.Invoke(handler, view);
        }
        catch (Exception)
        {
            // The default rc.1 mapping has no OpenHarmony platform view contract; ignore.
        }
    }

    /// <summary>The rc.1 tooltip text: the attached property first (that is what XAML sets),
    /// then the IToolTipElement.ToolTip content.</summary>
    private static string? ReadToolTipText(IView view)
    {
        if (view is BindableObject bindable && ToolTipProperties.GetText(bindable) is { } text)
        {
            string result = text.ToString() ?? string.Empty;
            if (result.Length > 0)
            {
                return result;
            }
        }
        if (view is IToolTipElement toolTipElement && toolTipElement.ToolTip?.Content is { } content)
        {
            string result = content.ToString() ?? string.Empty;
            return result.Length > 0 ? result : null;
        }
        return null;
    }

    /// <summary>Registers (or refreshes) the tooltip of an element with an already-known text.</summary>
    private static void Register(View element, string text)
    {
        lock (s_sync)
        {
            Registration? existing = FindLocked(element);
            if (existing is not null)
            {
                existing.Text = text;
                return;
            }
            var recognizer = new PointerGestureRecognizer();
            var registration = new Registration
            {
                Element = new WeakReference<View>(element),
                Recognizer = recognizer,
                Text = text,
            };
            recognizer.PointerEntered += (_, e) => OnPointerEntered(registration, e);
            recognizer.PointerExited += (_, _) => OnPointerExited(registration);
            recognizer.PointerMoved += (_, e) => OnPointerMoved(registration, e);
            recognizer.PointerPressed += (_, _) => OnPointerPressed(registration);
            s_registrations.Add(registration);
            element.GestureRecognizers.Add(recognizer);
        }
        EnsurePressHook();
    }

    /// <summary>Removes the tooltip of an element (cleared property / disposed element).</summary>
    private static void Unregister(View element)
    {
        Registration? removed = null;
        bool changed = false;
        lock (s_sync)
        {
            Registration? found = FindLocked(element);
            if (found is not null)
            {
                s_registrations.Remove(found);
                removed = found;
                changed = found.Visible;
                CancelPendingLocked(found, hideVisible: true);
            }
        }
        if (removed is not null)
        {
            element.GestureRecognizers.Remove(removed.Recognizer);
            if (changed)
            {
                RequestRedraw();
            }
            ReleaseIfIdle();
        }
    }

    /// <summary>Unregisters every element (tests/teardown).</summary>
    internal static void Clear()
    {
        var removed = new List<KeyValuePair<View, Registration>>();
        lock (s_sync)
        {
            foreach (Registration registration in s_registrations)
            {
                if (registration.Element.TryGetTarget(out View? element))
                {
                    removed.Add(new KeyValuePair<View, Registration>(element, registration));
                }
            }
            s_registrations.Clear();
            CancelPendingLocked(s_pending, hideVisible: true);
            s_pending = null;
            s_visible = null;
            s_timer?.Dispose();
            s_timer = null;
        }
        foreach (KeyValuePair<View, Registration> pair in removed)
        {
            pair.Key.GestureRecognizers.Remove(pair.Value.Recognizer);
        }
        if (removed.Count > 0)
        {
            RequestRedraw();
        }
        ReleaseIfIdle();
    }

    private static Registration? FindLocked(View element)
    {
        for (int i = s_registrations.Count - 1; i >= 0; i--)
        {
            if (!s_registrations[i].Element.TryGetTarget(out View? target))
            {
                s_registrations.RemoveAt(i);
                Pruned++;
                continue;
            }
            if (ReferenceEquals(target, element))
            {
                return s_registrations[i];
            }
        }
        return null;
    }

    private static void PruneLocked()
    {
        for (int i = s_registrations.Count - 1; i >= 0; i--)
        {
            if (!s_registrations[i].Element.TryGetTarget(out _))
            {
                s_registrations.RemoveAt(i);
                Pruned++;
            }
        }
    }

    // ------------------------------------------------------------------ hover state machine

    private static void OnPointerEntered(Registration registration, PointerEventArgs args)
    {
        if (!TryGetElement(registration, out _))
        {
            return;
        }
        if (!RefreshText(registration))
        {
            Hide(registration);
            return;
        }
        Point? position = args.GetPosition(null);
        if (position is { } point)
        {
            registration.PointerX = (float)point.X;
            registration.PointerY = (float)point.Y;
        }
        Schedule(registration);
    }

    private static void OnPointerMoved(Registration registration, PointerEventArgs args)
    {
        if (!TryGetElement(registration, out _))
        {
            return;
        }
        if (!RefreshText(registration))
        {
            return;
        }
        // The popup is anchored where it appeared; a move only updates the pending position.
        if (!registration.Visible)
        {
            Point? position = args.GetPosition(null);
            if (position is { } point)
            {
                registration.PointerX = (float)point.X;
                registration.PointerY = (float)point.Y;
            }
        }
    }

    private static void OnPointerExited(Registration registration)
    {
        if (!TryGetElement(registration, out _))
        {
            return;
        }
        Hide(registration);
    }

    private static void OnPointerPressed(Registration registration) => Hide(registration);

    /// <summary>Re-reads the tooltip text on hover so a changed text shows without re-mapping.
    /// Returns false when the element no longer has a tooltip.</summary>
    private static bool RefreshText(Registration registration)
    {
        if (!TryGetElement(registration, out View? element))
        {
            return false;
        }
        string? text = ReadToolTipText(element);
        if (string.IsNullOrEmpty(text))
        {
            Unregister(element);
            return false;
        }
        registration.Text = text;
        return true;
    }

    private static bool TryGetElement(Registration registration, out View? element)
    {
        if (!registration.Element.TryGetTarget(out element))
        {
            Prune(registration);
            return false;
        }
        return true;
    }

    private static void Prune(Registration registration)
    {
        lock (s_sync)
        {
            s_registrations.Remove(registration);
            Pruned++;
            CancelPendingLocked(registration, hideVisible: true);
        }
        ReleaseIfIdle();
    }

    private static void Schedule(Registration registration)
    {
        int delay = Math.Max(0, ShowDelayMs);
        lock (s_sync)
        {
            if (registration.Visible)
            {
                return;
            }
            s_pending = registration;
            s_dueAtMs = Clock() + delay;
            Scheduled++;
            s_timer ??= new Timer(static _ => Pump());
            s_timer.Change(delay, Timeout.Infinite);
        }
        EnsurePresentHook();
    }

    /// <summary>Drives a due pending show. The delay timer calls this; tests call it after
    /// advancing <see cref="Clock"/>.</summary>
    internal static void Pump()
    {
        Registration? show = null;
        lock (s_sync)
        {
            if (s_pending is { } pending && Clock() >= s_dueAtMs)
            {
                show = pending;
                s_pending = null;
            }
        }
        if (show is not null)
        {
            Show(show);
        }
    }

    private static void Show(Registration registration)
    {
        bool ok;
        lock (s_sync)
        {
            ok = registration.Element.TryGetTarget(out View? element) && !string.IsNullOrEmpty(registration.Text);
            if (ok)
            {
                if (s_visible is { } previous && !ReferenceEquals(previous, registration))
                {
                    previous.Visible = false;
                    Hidden++;
                }
                string text = registration.Text;
                (float width, float height) = Measure(text);
                float x = registration.PointerX + PopupPointerOffsetX;
                float y = registration.PointerY + PopupPointerOffsetY;
                float windowWidth = (float)OpenHarmonyAlertHost.Width;
                float windowHeight = (float)OpenHarmonyAlertHost.Height;
                if (windowWidth > 0)
                {
                    x = Math.Clamp(x, 4f, Math.Max(4f, windowWidth - width - 4f));
                }
                if (windowHeight > 0)
                {
                    y = Math.Clamp(y, 4f, Math.Max(4f, windowHeight - height - 4f));
                }
                registration.Rect = new RectF(x, y, width, height);
                registration.Visible = true;
                s_visible = registration;
                Shown++;
            }
            if (s_pending is not null && ReferenceEquals(s_pending, registration))
            {
                s_pending = null;
            }
        }
        if (ok)
        {
            EnsurePresentHook();
            RequestRedraw();
        }
        else
        {
            ReleaseIfIdle();
        }
    }

    private static void Hide(Registration registration)
    {
        bool changed;
        lock (s_sync)
        {
            changed = registration.Visible || ReferenceEquals(s_pending, registration);
            CancelPendingLocked(registration, hideVisible: true);
        }
        if (changed)
        {
            RequestRedraw();
        }
        ReleaseIfIdle();
    }

    private static void HideAll()
    {
        bool changed;
        lock (s_sync)
        {
            changed = s_visible is not null || s_pending is not null;
            CancelPendingLocked(s_pending, hideVisible: true);
            s_pending = null;
        }
        if (changed)
        {
            RequestRedraw();
        }
        ReleaseIfIdle();
    }

    /// <summary>Cancels a pending show and (optionally) hides the visible popup. Caller holds
    /// <see cref="s_sync"/>; the caller requests the redraw after releasing it.</summary>
    private static void CancelPendingLocked(Registration? registration, bool hideVisible)
    {
        if (registration is null)
        {
            return;
        }
        if (ReferenceEquals(s_pending, registration))
        {
            s_pending = null;
        }
        if (hideVisible && registration.Visible)
        {
            registration.Visible = false;
            if (ReferenceEquals(s_visible, registration))
            {
                s_visible = null;
            }
            Hidden++;
        }
    }

    /// <summary>Releases the timer and the global hooks when nothing is pending or visible.</summary>
    private static void ReleaseIfIdle()
    {
        bool releasePress;
        bool releasePresent;
        lock (s_sync)
        {
            if (s_pending is not null || s_visible is not null)
            {
                return;
            }
            s_timer?.Dispose();
            s_timer = null;
            releasePress = s_pressHook && s_registrations.Count == 0;
            if (releasePress)
            {
                s_pressHook = false;
            }
            releasePresent = s_presentHook;
            if (releasePresent)
            {
                s_presentHook = false;
            }
        }
        if (releasePress)
        {
            OpenHarmonyBridge.Touch -= OnTouchDown;
        }
        if (releasePresent)
        {
            RestorePresentHook();
        }
    }

    private static void EnsurePressHook()
    {
        lock (s_sync)
        {
            if (s_pressHook)
            {
                return;
            }
            s_pressHook = true;
        }
        OpenHarmonyBridge.Touch += OnTouchDown;
    }

    private static void OnTouchDown(OpenHarmonyTouchEventArgs args)
    {
        if (args.Action == OpenHarmonyTouchAction.Down)
        {
            HideAll();
        }
    }

    // ------------------------------------------------------------------ overlay drawing

    /// <summary>Chains the renderer's present hook so the popup is painted after the tree and
    /// before the frame is presented. Installed only while a popup is pending/visible.</summary>
    private static void EnsurePresentHook()
    {
        lock (s_sync)
        {
            if (s_presentHook && ReferenceEquals(OpenHarmonyWindowRenderer.SurfacePresent, s_presentDelegate))
            {
                return;
            }
            s_presentHook = true;
            s_presentPrevious = OpenHarmonyWindowRenderer.SurfacePresent;
            OpenHarmonyWindowRenderer.SurfacePresent = s_presentDelegate;
        }
    }

    private static void RestorePresentHook()
    {
        if (ReferenceEquals(OpenHarmonyWindowRenderer.SurfacePresent, s_presentDelegate))
        {
            OpenHarmonyWindowRenderer.SurfacePresent = s_presentPrevious;
        }
    }

    private static void OnSurfacePresent()
    {
        try
        {
            DrawOverlay();
        }
        catch (Exception)
        {
            // A paint failure must not break the frame.
        }
        if (s_presentPrevious is { } previous)
        {
            previous();
        }
        else
        {
            HostCanvas.Present();
        }
    }

    private static MauiCanvas? s_defaultCanvas;

    /// <summary>Paints the visible popup on the frame canvas (testable entry point).</summary>
    internal static void DrawOverlay()
    {
        string text;
        RectF rect;
        lock (s_sync)
        {
            if (s_visible is not { } visible || string.IsNullOrEmpty(visible.Text) || visible.Rect.Width <= 0)
            {
                return;
            }
            text = visible.Text;
            rect = visible.Rect;
        }
        ICanvas canvas = OverlayCanvasFactory?.Invoke() ?? (s_defaultCanvas ??= new MauiCanvas());
        canvas.FillColor = PopupBackground;
        canvas.FillRoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, 8);
        canvas.StrokeColor = PopupBorder;
        canvas.StrokeSize = 1;
        canvas.DrawRoundedRectangle(rect.X, rect.Y, rect.Width, rect.Height, 8);
        canvas.FontColor = PopupTextColor;
        canvas.FontSize = PopupFontSize;
        canvas.DrawString(text, rect.X + PopupPadding, rect.Y + PopupPadding, rect.Width - PopupPadding * 2,
            rect.Height - PopupPadding * 2, HorizontalAlignment.Left, VerticalAlignment.Center);
        Draws++;
    }

    /// <summary>Popup size for the text: the platform font metrics when the host answers, else a
    /// deterministic estimate (average advance 0.55em, line height 1.35em).</summary>
    private static (float Width, float Height) Measure(string text)
    {
        float maxTextWidth = PopupMaxWidth - PopupPadding * 2;
        float textWidth;
        float textHeight;
        if (HostCanvas.MeasureText(text, PopupFontSize, out int measuredWidth, out int measuredHeight) && measuredWidth > 0)
        {
            int lines = Math.Max(1, (int)Math.Ceiling(measuredWidth / maxTextWidth));
            textWidth = Math.Min(maxTextWidth, measuredWidth);
            textHeight = Math.Max(1, measuredHeight) * lines;
        }
        else
        {
            float advance = PopupFontSize * 0.55f;
            int charsPerLine = Math.Max(1, (int)(maxTextWidth / advance));
            int lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)charsPerLine));
            textWidth = Math.Min(maxTextWidth, Math.Max(1, text.Length) * advance);
            textHeight = lines * PopupFontSize * 1.35f;
        }
        return (textWidth + PopupPadding * 2, textHeight + PopupPadding * 2);
    }

    private static void RequestRedraw()
    {
        try
        {
            OpenHarmonyBridge.RequestRedraw();
        }
        catch (Exception)
        {
            // No host: the popup state is still consistent.
        }
    }

    private static void RequestRedrawIfVisible()
    {
        lock (s_sync)
        {
            if (s_visible is null)
            {
                return;
            }
        }
        RequestRedraw();
    }

    /// <summary>Resets every counter/state and releases the hooks (tests).</summary>
    internal static void ResetForTests()
    {
        Clear();
        lock (s_sync)
        {
            s_timer?.Dispose();
            s_timer = null;
            s_pending = null;
            s_visible = null;
            Scheduled = 0;
            Shown = 0;
            Hidden = 0;
            Draws = 0;
            Pruned = 0;
            UnsupportedElements = 0;
        }
    }
}
