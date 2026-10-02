// Platform view for the OpenHarmony compositor: a lightweight drawing element. Handlers set
// its properties from the virtual view; OpenHarmonyWindowRenderer draws and hit-tests it.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One row of the compositor's navigation/shell toolbar. The platform mirror builds these from
/// the current page's <c>ToolbarItem</c> collection (text and/or icon, order, enabled state,
/// activation) and <see cref="OpenHarmonyView"/> draws and hit-tests them: primary items dock in
/// the bar, secondary items live in the overflow dropdown behind the "more" affordance.
/// </summary>
public sealed class OpenHarmonyToolbarItem
{
    /// <summary>Item label (empty for an icon-only item, which still keeps its bar slot).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Decoded icon bytes (file source); null for text/glyph-only items.</summary>
    public byte[]? IconBytes { get; init; }

    /// <summary>Glyph text of a FontImageSource icon; null for text/bytes-only items.</summary>
    public string? Glyph { get; init; }

    /// <summary>Glyph size in surface pixels (already density-scaled); 0 means the bar default.</summary>
    public float GlyphFontSize { get; init; }

    /// <summary>Glyph colour (a FontImageSource's own colour).</summary>
    public Color GlyphColor { get; init; } = Colors.White;

    /// <summary>False mirrors an item whose IsEnabled is false: drawn dimmed, taps ignored.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>True for ToolbarItemOrder.Secondary: the row lives in the overflow dropdown.</summary>
    public bool IsSecondary { get; init; }

    /// <summary>The activation contract (MenuItem.Clicked/Command); never null.</summary>
    public Action Activate { get; init; } = static () => { };
}

public class OpenHarmonyView
{
    public IView? VirtualView { get; set; }

    private string? _text;

    /// <summary>
    /// Text drawn by this view. A change restarts the caret blink so typing never lands on the
    /// hidden phase of the blink (the caret is the only text state that animates).
    /// </summary>
    public string? Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }
            _text = value;
            ResetCaretBlink();
        }
    }

    public float FontSize { get; set; } = 14f;

    private Color _textColor = Colors.White;
    private Color? _dimmedTextColor;

    /// <summary>
    /// Text colour. Assigning a different colour drops the cached disabled variant, so the
    /// derived colour the draw path uses is built once per source colour instead of once per
    /// frame (which allocated a Color per dimmed view per frame).
    /// </summary>
    public Color TextColor
    {
        get => _textColor;
        set
        {
            if (ReferenceEquals(_textColor, value))
            {
                return;
            }
            _textColor = value;
            _dimmedTextColor = null;
        }
    }

    private Color? _background;
    private Color? _dimmedBackground;

    /// <summary>Optional background fill; same cache invalidation as <see cref="TextColor"/>.</summary>
    public Color? Background
    {
        get => _background;
        set
        {
            if (ReferenceEquals(_background, value))
            {
                return;
            }
            _background = value;
            _dimmedBackground = null;
        }
    }

    public float CornerRadius { get; set; }

    /// <summary>Alpha applied to a disabled view's colours.</summary>
    private const float DimAlpha = 0.5f;

    /// <summary>Text colour for this draw pass; the disabled variant is reused, not re-derived.</summary>
    internal Color TextColorForDraw => Dimmed ? _dimmedTextColor ??= _textColor.WithAlpha(DimAlpha) : _textColor;

    /// <summary>Background colour for this draw pass; null when none is set.</summary>
    internal Color? BackgroundForDraw =>
        !Dimmed || _background is null ? _background : _dimmedBackground ??= _background.WithAlpha(DimAlpha);

    /// <summary>Invoked when a tap lands inside this view (buttons wire it to SendClicked).</summary>
    public Action? Tap { get; set; }

    private bool _pressed;
    private bool _toolbarPressed;

    /// <summary>
    /// Touch-down state. The setter drives the press-progress transition (see
    /// <see cref="PressProgress"/>), so the drawn pressed tint eases in/out instead of snapping.
    /// </summary>
    public bool Pressed
    {
        get => _pressed;
        set
        {
            if (_pressed == value)
            {
                return;
            }
            _pressed = value;
            (_pressChannel ??= new ProgressChannel(_pressed ? 0f : 1f))
                .TransitionTo(_pressed ? 1f : 0f, PressDurationMs);
        }
    }

    /// <summary>Press feedback progress, 0 (rest) to 1 (fully pressed).</summary>
    internal float PressProgress => _pressChannel?.Value ?? (_pressed ? 1f : 0f);

    // Entry support
    private bool _isTextEntry;

    /// <summary>Marks this platform view as a text entry (Entry/Editor/SearchBar).</summary>
    public bool IsTextEntry
    {
        get => _isTextEntry;
        set
        {
            if (_isTextEntry == value)
            {
                return;
            }
            _isTextEntry = value;
            UpdateAnimationRegistration();
        }
    }

    public string? Placeholder { get; set; }

    private bool _isFocused;

    /// <summary>
    /// Focus state of the entry. Focus drives the caret blink: a focused entry keeps asking for
    /// frames (see <see cref="NeedsAnimation"/>) so the caret can toggle, an unfocused one is
    /// static and never draws a caret.
    /// </summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value)
            {
                return;
            }
            _isFocused = value;
            if (!value)
            {
                CompositionText = null;
            }
            ResetCaretBlink();
            UpdateAnimationRegistration();
        }
    }

    private int _cursorPosition = -1;

    /// <summary>Caret index (-1 draws at the end of the text).</summary>
    public int CursorPosition
    {
        get => _cursorPosition;
        set
        {
            if (_cursorPosition == value)
            {
                return;
            }
            _cursorPosition = value;
            ResetCaretBlink();
        }
    }

    private int _selectionLength;

    /// <summary>Selection length; the selected range ends at <see cref="CursorPosition"/>.</summary>
    public int SelectionLength
    {
        get => _selectionLength;
        set
        {
            if (_selectionLength == value)
            {
                return;
            }
            _selectionLength = value;
            ResetCaretBlink();
        }
    }

    /// <summary>Anchor of an in-progress selection drag (-1 when none).</summary>
    public int TextAnchor { get; set; } = -1;

    // InputView mapping (T1): the state the Entry/Editor/SearchBar mapper methods write. The
    // self-drawn editor honours it while drawing, while hit-testing the caret/selection and
    // while deciding what the shell's text input may change.

    /// <summary>Maximum input length; 0 or <see cref="int.MaxValue"/> means unlimited.</summary>
    public int MaxLength { get; set; } = int.MaxValue;

    /// <summary>
    /// Read-only text entry: focus and selection keep working, the soft keyboard is never
    /// requested and the shell's text never changes the content.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Password entry: every typed character is drawn as a bullet.</summary>
    public bool IsPassword { get; set; }

    /// <summary>When the entry shows its clear button (never / while editing / always).</summary>
    public ClearButtonVisibility ClearButtonVisibility { get; set; } = ClearButtonVisibility.Never;

    /// <summary>
    /// The return-key kind (Default/Done/Go/Next/Search/Send). The shell's keyboard owns the
    /// key label; the slice records the value so the mapper contract is complete.
    /// </summary>
    public ReturnType ReturnType { get; set; } = ReturnType.Default;

    /// <summary>
    /// The keyboard kind. Numeric and Telephone additionally restrict what the shell's input
    /// may insert (the closest the self-drawn editor gets to Android's inputType filtering);
    /// every other kind only records the value.
    /// </summary>
    public Keyboard? Keyboard { get; set; }

    /// <summary>Placeholder colour; null falls back to the slice's Gray.</summary>
    public Color? PlaceholderColor { get; set; }

    /// <summary>Horizontal alignment of the text, placeholder and caret inside the frame.</summary>
    public TextAlignment HorizontalTextAlignment { get; set; } = TextAlignment.Start;

    /// <summary>Clears the entry's text (wired by the Entry handler to the virtual view).</summary>
    public Action? ClearText { get; set; }

    private bool _clearPressed;

    /// <summary>Text as drawn: password entries replace every character with a bullet.</summary>
    internal string DisplayText
    {
        get
        {
            string text = Text ?? string.Empty;
            return IsPassword ? MaskPassword(text) : text;
        }
    }

    private static string MaskPassword(string text)
        => text.Length == 0 ? text : new string('\u2022', text.Length);

    /// <summary>Clamps input to <paramref name="maxLength"/> (0/MaxValue = unlimited).</summary>
    internal static string ClampToMaxLength(string text, int maxLength)
        => maxLength > 0 && maxLength != int.MaxValue && text.Length > maxLength
            ? text[..maxLength]
            : text;

    /// <summary>
    /// Applies the Keyboard kind's input restriction. Numeric keeps digits and the decimal
    /// signs, Telephone keeps digits and the dialing punctuation; a keyboard created through
    /// <see cref="Keyboard.Create(KeyboardFlags)"/> is not one of the static kinds and keeps
    /// the text unchanged.
    /// </summary>
    internal static string ApplyKeyboardFilter(Keyboard? keyboard, string text)
    {
        if (ReferenceEquals(keyboard, Keyboard.Numeric))
        {
            return FilterChars(text, static c => char.IsAsciiDigit(c) || c is '.' or ',' or '-' or '+');
        }
        if (ReferenceEquals(keyboard, Keyboard.Telephone))
        {
            return FilterChars(text, static c => char.IsAsciiDigit(c) || c is '+' or '-' or ' ' or '(' or ')' or '.');
        }
        return text;
    }

    private static string FilterChars(string text, Func<char, bool> keep)
    {
        int firstDropped = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (!keep(text[i]))
            {
                firstDropped = i;
                break;
            }
        }
        if (firstDropped < 0)
        {
            return text;
        }
        var builder = new System.Text.StringBuilder(text.Length);
        builder.Append(text, 0, firstDropped);
        for (int i = firstDropped + 1; i < text.Length; i++)
        {
            if (keep(text[i]))
            {
                builder.Append(text[i]);
            }
        }
        return builder.ToString();
    }

    /// <summary>Clear-button geometry: radius 9, 12 px from the trailing edge.</summary>
    internal const float ClearButtonRadius = 9f;

    /// <summary>Fingertip slop around the clear-button circle.</summary>
    internal const float ClearButtonTouchRadius = 16f;

    /// <summary>
    /// True when the clear button is shown for the current state: the rc.1 enum only carries
    /// Never/WhileEditing, so the button shows while the entry is focused and has text.
    /// </summary>
    public bool ClearButtonVisible =>
        IsTextEntry && !string.IsNullOrEmpty(Text) &&
        ClearButtonVisibility == ClearButtonVisibility.WhileEditing && IsFocused;

    /// <summary>Centre of the clear button (drawing and hit testing share it).</summary>
    internal PointF ClearButtonCenter
    {
        get
        {
            RectF frame = CanvasFrame;
            // The clear button sits at the entry's trailing edge: physical right in LTR, left in RTL.
            float x = FlowRightToLeft
                ? frame.X + ClearButtonRadius + 12f
                : frame.X + frame.Width - ClearButtonRadius - 12f;
            return new PointF(x, frame.Y + frame.Height / 2f);
        }
    }

    /// <summary>True when (x, y) hits the visible clear button (with fingertip slop).</summary>
    public bool InClearButton(float x, float y)
    {
        if (!ClearButtonVisible)
        {
            return false;
        }
        PointF center = ClearButtonCenter;
        float dx = x - center.X;
        float dy = y - center.Y;
        return dx * dx + dy * dy <= ClearButtonTouchRadius * ClearButtonTouchRadius;
    }

    // IME preedit (composition): the shell's input delivers the input method's preview text
    // (PreviewText.value/offset) while a composition is in flight; it is drawn at the caret with
    // an underline/highlight and replaced by the committed text the shell sends through TextInput.
    private string? _compositionText;

    /// <summary>Composing (preedit) string, null/empty when no composition is in flight.</summary>
    internal string? CompositionText
    {
        get => _compositionText;
        set
        {
            // Normalize "no composition" to null so IsComposing and the commit path agree.
            string? normalized = string.IsNullOrEmpty(value) ? null : value;
            if (string.Equals(_compositionText, normalized, StringComparison.Ordinal))
            {
                return;
            }
            _compositionText = normalized;
            ResetCaretBlink();
        }
    }

    /// <summary>Insertion offset of the composing string in the text (UTF-16 units).</summary>
    internal int CompositionOffset { get; set; }

    internal bool IsComposing => !string.IsNullOrEmpty(_compositionText);

    // Caret blink: 500 ms visible / 500 ms hidden, the platform text-caret cadence. The phase is
    // anchored at the last caret-affecting change (focus, cursor, selection, text, composition),
    // so the caret is visible for the first half period after every interaction.
    internal const int CaretBlinkHalfPeriodMs = 500;

    /// <summary>Test/diagnostic clock override (null = <see cref="Environment.TickCount64"/>).</summary>
    internal static Func<long>? CaretClock { get; set; }

    private long _caretBlinkEpochMs = long.MinValue;

    private static long CaretNowMs => CaretClock?.Invoke() ?? Environment.TickCount64;

    private void ResetCaretBlink() => _caretBlinkEpochMs = CaretNowMs;

    /// <summary>True when the caret is in its visible half of the blink period.</summary>
    internal bool CaretVisible
    {
        get
        {
            if (!IsFocused)
            {
                return false;
            }
            long now = CaretNowMs;
            if (_caretBlinkEpochMs == long.MinValue)
            {
                _caretBlinkEpochMs = now;
                return true;
            }
            return (now - _caretBlinkEpochMs) % (2L * CaretBlinkHalfPeriodMs) < CaretBlinkHalfPeriodMs;
        }
    }

    // Image support
    private byte[]? _imageBytes;
    private int _imageGeneration;
    private int _imageStageGeneration = -1;   // generation whose preview/final pass already ran
    private int _imageFailedGeneration = -1;  // generation whose decode failed (placeholder shown)

    /// <summary>
    /// Encoded image bytes drawn by this view. A different array starts a new image generation:
    /// the progressive decode state (preview pass, placeholder) is reset for it, and the host
    /// cache keys on content + requested size, so the generation stays a managed-side concern.
    /// </summary>
    public byte[]? ImageBytes
    {
        get => _imageBytes;
        set
        {
            if (ReferenceEquals(_imageBytes, value))
            {
                return;
            }
            _imageBytes = value;
            _imageGeneration++;
            ImageDecodePasses = 0;
            _imageStageGeneration = -1;
            _imageFailedGeneration = -1;
        }
    }

    public Aspect ImageAspect { get; set; } = Aspect.AspectFit;

    // Progressive decode (P2b-IMG): a destination at least ImagePreviewMinEdgePx long decodes a
    // coarse preview first (fast, bounded), then requests a redraw that decodes at the display
    // size; a smaller destination decodes once at the display size. The host never decodes the
    // full source for the draw path, so a large image cannot OOM the frame.
    internal const int ImagePreviewMinEdgePx = 128;
    internal const int ImagePreviewDivisor = 8;

    /// <summary>Decode passes completed for the current bytes: 0 none, 1 preview, 2 display size.</summary>
    internal int ImageDecodePasses { get; private set; }

    /// <summary>Last decode size requested from the host (tests/diagnostics).</summary>
    internal int LastImageDecodeWidth { get; private set; }
    internal int LastImageDecodeHeight { get; private set; }

    /// <summary>
    /// Test seam: receives the requesting view plus every image draw request, stage 1 = preview,
    /// 2 = display size. The view is part of the callback so a test can pin one view's contract
    /// while other image views in the tree keep drawing.
    /// </summary>
    internal static Action<OpenHarmonyView, RectF, int, int, int>? ImageDrawRequested { get; set; }

    /// <summary>Test seam: receives the view whose failed decode fell back to the placeholder.</summary>
    internal static Action<OpenHarmonyView, RectF>? ImagePlaceholderDrawn { get; set; }

    /// <summary>Test seam: replaces the host blit (data, x, y, w, h, decodeW, decodeH).</summary>
    internal static Func<byte[], int, int, int, int, int, int, bool?>? ImageDrawOverride { get; set; }

    // CheckBox support
    public bool IsCheckBox { get; set; }

    private bool _isChecked;

    /// <summary>Checked state; the setter eases the drawn check mark in/out.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }
            _isChecked = value;
            (_checkChannel ??= new ProgressChannel(_isChecked ? 0f : 1f))
                .TransitionTo(_isChecked ? 1f : 0f, CheckDurationMs);
        }
    }

    /// <summary>Check-mark draw-on progress, 0 (unchecked) to 1 (checked).</summary>
    internal float CheckProgress => _checkChannel?.Value ?? (_isChecked ? 1f : 0f);

    public Color CheckBoxColor { get; set; } = Colors.White;

    // Switch support
    public bool IsSwitch { get; set; }

    private bool _isOn;

    /// <summary>Switch state; the setter eases the knob/track between the two poses.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }
            _isOn = value;
            (_switchChannel ??= new ProgressChannel(_isOn ? 0f : 1f))
                .TransitionTo(_isOn ? 1f : 0f, SwitchDurationMs);
        }
    }

    /// <summary>Knob/track progress, 0 (off) to 1 (on).</summary>
    internal float SwitchProgress => _switchChannel?.Value ?? (_isOn ? 1f : 0f);

    public Color SwitchTrackColor { get; set; } = Colors.DimGray;
    public Color SwitchThumbColor { get; set; } = Colors.White;

    // Slider support
    public bool IsSlider { get; set; }
    public double SliderValue { get; set; }
    public double SliderMinimum { get; set; }
    public double SliderMaximum { get; set; } = 1;
    public Color SliderMinimumTrackColor { get; set; } = Colors.DodgerBlue;
    public Color SliderMaximumTrackColor { get; set; } = Colors.DimGray;
    public Color SliderThumbColor { get; set; } = Colors.White;

    /// <summary>(fraction, completed) invoked while a drag updates the slider value.</summary>
    public Action<float, bool>? SliderDrag { get; set; }

    /// <summary>Current drag fraction, used when the drag completes.</summary>
    public float SliderFraction { get; set; }

    // Progress bar support
    public bool IsProgressBar { get; set; }
    public double Progress { get; set; }
    public Color ProgressColor { get; set; } = Colors.DodgerBlue;

    // Activity indicator support. IsActivityIndicator/IsRunning feed a global registration
    // count so the frame loop can ask "is any spinner running?" in O(1) instead of walking the
    // whole tree every frame (the tree walk stays as the exact answer once some view is
    // registered, so hidden spinners behave exactly like before).
    private bool _isActivityIndicator;
    private bool _isRunning;
    private bool _animationRegistered;

    public bool IsActivityIndicator
    {
        get => _isActivityIndicator;
        set
        {
            if (_isActivityIndicator == value)
            {
                return;
            }
            _isActivityIndicator = value;
            UpdateAnimationRegistration();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }
            _isRunning = value;
            UpdateAnimationRegistration();
        }
    }

    public Color IndicatorColor { get; set; } = Colors.White;

    /// <summary>Shared rotation (degrees) advanced by the renderer for animated views.</summary>
    public static float AnimationAngle { get; set; }

    /// <summary>
    /// True when this view keeps redrawing on its own: a running activity indicator (spinner
    /// angle) or a focused text entry (caret blink).
    /// </summary>
    public bool NeedsAnimation => _isActivityIndicator && _isRunning || _isTextEntry && _isFocused;

    private static int s_animationCount;

    /// <summary>
    /// Platform views that currently want continuous redraws (their <see cref="NeedsAnimation"/>
    /// is true). The frame loop reads this to skip the per-frame tree walk when nothing animates;
    /// the walk still decides whether a running indicator or a focused entry is actually visible.
    /// </summary>
    internal static int AnimationCount => Volatile.Read(ref s_animationCount);

    private void UpdateAnimationRegistration()
    {
        bool active = _isActivityIndicator && _isRunning || _isTextEntry && _isFocused;
        if (active == _animationRegistered)
        {
            return;
        }
        _animationRegistered = active;
        if (active)
        {
            Interlocked.Increment(ref s_animationCount);
        }
        else
        {
            Interlocked.Decrement(ref s_animationCount);
        }
    }

    // Control-state transitions (P1a-ANIM) ------------------------------------------------
    // Press feedback, the switch knob and the check mark are the slice's common state changes.
    // Each channel is one cached <see cref="ProgressChannel"/> on the shared frame loop: the
    // first transition lazily creates it, later transitions reuse it, no per-frame allocation.
    private const long PressDurationMs = 90;
    private const long SwitchDurationMs = 160;
    private const long CheckDurationMs = 140;

    private ProgressChannel? _pressChannel;
    private ProgressChannel? _switchChannel;
    private ProgressChannel? _checkChannel;

    /// <summary>
    /// One animated 0..1 progress value on the shared frame loop. The owning view reads
    /// <see cref="Value"/> while drawing; the channel re-registers itself on the first
    /// transition and retires when it reaches its target, so idle views never tick.
    /// </summary>
    private sealed class ProgressChannel : IOpenHarmonyAnimation
    {
        private float _value;
        private float _from;
        private float _target;
        private long _startMs;
        private long _durationMs = 1;
        private bool _active;

        internal ProgressChannel(float value)
        {
            _value = value;
            _target = value;
        }

        internal float Value => _value;

        public bool NeedsRedraw => true;

        /// <summary>Eases to <paramref name="target"/>; snaps under reduce-motion.</summary>
        internal void TransitionTo(float target, long durationMs)
        {
            if (OpenHarmonyMotion.ReduceMotion)
            {
                Snap(target);
                return;
            }
            if (_active && Math.Abs(target - _target) < 0.0001f)
            {
                return;
            }
            _from = _value;
            _target = target;
            _startMs = OpenHarmonyAnimationLoop.NowMs;
            _durationMs = Math.Max(1, durationMs);
            if (!_active)
            {
                _active = true;
                OpenHarmonyAnimationLoop.Register(this);
            }
        }

        internal void Snap(float value)
        {
            _value = value;
            _target = value;
            if (_active)
            {
                _active = false;
                OpenHarmonyAnimationLoop.Unregister(this);
            }
            RequestRepaint();
        }

        public bool Step(long nowMs, float dtSeconds)
        {
            if (!_active)
            {
                return false;
            }
            float t = (nowMs - _startMs) / (float)_durationMs;
            if (t >= 1f)
            {
                _value = _target;
                // Retire explicitly: the loop removes the finished entry, but a later
                // TransitionTo must see the channel idle so it re-registers.
                _active = false;
                // The final frame must be painted even though this step retires the channel.
                RequestRepaint();
                return false;
            }
            if (t < 0f)
            {
                t = 0f;
            }
            // Cubic ease-out: fast response with a soft settle.
            float inverse = 1f - t;
            _value = _from + (_target - _from) * (1f - inverse * inverse * inverse);
            return true;
        }

        private static void RequestRepaint()
        {
            try
            {
                Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
            }
            catch (Exception)
            {
                // No host: the next input/frame repaint shows the settled value.
            }
        }
    }

    /// <summary>Raised when image bytes are blitted (tests observe the destination rect).</summary>
    public static Action<RectF>? ImageDrawn;

    // WebView support (the shell owns the ArkWeb component; this view is a placeholder)
    public bool IsWebView { get; set; }

    // GraphicsView support. The renderer owns press capture and feeds these with every tracked
    // pointer (window coordinates, the same space as the drawable's dirty rect): a single finger
    // is a one-entry array and a multi-touch gesture passes the whole set. They are the
    // IGraphicsView interaction contract the Controls GraphicsView raises its events from.
    public bool IsGraphicsView { get; set; }
    /// <summary>IGraphicsView.StartInteraction: a press inside the frame (or a new pointer).</summary>
    public Action<PointF[]>? GraphicsStartInteraction { get; set; }
    /// <summary>IGraphicsView.DragInteraction: pointer movement while a press is held.</summary>
    public Action<PointF[]>? GraphicsDragInteraction { get; set; }
    /// <summary>IGraphicsView.EndInteraction: release; the flag is whether it happened inside.</summary>
    public Action<PointF[], bool>? GraphicsEndInteraction { get; set; }
    /// <summary>IGraphicsView.CancelInteraction: the platform canceled the press.</summary>
    public Action? GraphicsCancelInteraction { get; set; }
    /// <summary>IGraphicsView.StartHoverInteraction: a hovering pointer entered the frame.</summary>
    public Action<PointF[]>? GraphicsHoverStart { get; set; }
    /// <summary>IGraphicsView.MoveHoverInteraction: a hovering pointer moved inside the frame.</summary>
    public Action<PointF[]>? GraphicsHoverMove { get; set; }
    /// <summary>IGraphicsView.EndHoverInteraction: the hovering pointer left the frame.</summary>
    public Action? GraphicsHoverEnd { get; set; }

    // Indicator view support
    public bool IsIndicatorView { get; set; }
    public int IndicatorCount { get; set; }
    public int IndicatorPosition { get; set; }
    public Color DotsColor { get; set; } = Colors.Gray;
    public Color SelectedDotsColor { get; set; } = Colors.White;
    public float DotsSize { get; set; } = 6f;

    // Shape support (Rectangle/Ellipse/Line/Path/Polygon/Polyline/RoundRectangle)
    public bool IsShape { get; set; }
    public Microsoft.Maui.Graphics.IShape? Shape { get; set; }
    public Paint? ShapeFill { get; set; }
    public Color ShapeStroke { get; set; } = Colors.White;
    public float ShapeStrokeThickness { get; set; } = 1f;

    // Border support
    public bool IsBorder { get; set; }
    public Color BorderStroke { get; set; } = Colors.Gray;
    public float BorderStrokeThickness { get; set; } = 1f;

    /// <summary>Optional stroke drawn around the view's frame (buttons, frames).</summary>
    public Color? StrokeColor { get; set; }
    public float StrokeThickness { get; set; }

    /// <summary>Disabled views paint their colours at half alpha (set by the renderer).</summary>
    public bool Dimmed { get; set; }

    // Stepper support
    public bool IsStepper { get; set; }
    public double StepperValue { get; set; }
    public double StepperMinimum { get; set; }
    public double StepperMaximum { get; set; } = 100;
    public double StepperInterval { get; set; } = 1;
    /// <summary>Invoked with true (increment) or false (decrement) when a stepper half is tapped.</summary>
    public Action<bool>? StepperStep { get; set; }

    // Radio button support
    public bool IsRadioButton { get; set; }
    public bool RadioChecked { get; set; }
    public Color RadioColor { get; set; } = Colors.DodgerBlue;

    /// <summary>(deltaX, deltaY) when a pan ends on this view (carousel paging).</summary>
    public Action<float, float>? Swipe { get; set; }

    // Shell chrome (title bar + back button)
    public bool ShowsTitleBar { get; set; }
    /// <summary>Re-syncs the chrome (title/back) with the virtual view before drawing.</summary>
    public Action? ChromeRefresh { get; set; }
    public string? TitleText { get; set; }
    public bool ShowsBack { get; set; }
    public const float TitleBarHeight = 56f;

    /// <summary>
    /// Materialized rich Shell.TitleView row (T15): the shell chrome measures/arranges the
    /// resolved non-Label view into the title band and the renderer draws and hit-tests it.
    /// Null while the bar keeps the text title path (see OpenHarmonyShellChrome).
    /// </summary>
    internal OpenHarmonyShellTitleViewRow? ShellTitleViewRow { get; set; }

    public bool InBackButton(float x, float y)
    {
        RectF frame = CanvasFrame;
        // The back slot is the leading edge of the bar: physical left in LTR, right in RTL.
        bool inSlot = FlowRightToLeft
            ? x >= frame.Right - 56 && x <= frame.Right
            : x >= frame.X && x <= frame.X + 56;
        return ShowsTitleBar && ShowsBack &&
               inSlot &&
               y >= frame.Y && y <= frame.Y + TitleBarHeight;
    }

    // Shell flyout support
    public bool ShowsHamburger { get; set; }
    public bool FlyoutOpen { get; set; }
    public List<string> FlyoutItems { get; } = new();
    public Action<int>? FlyoutSelect { get; set; }
    public Action? FlyoutRequested { get; set; }

    // Shell flyout panel geometry (the drawer): one width/inset/gap source shared by the text
    // layout, the rich-row arrangement (OpenHarmonyShellFlyout) and hit-testing.
    internal const float FlyoutPanelMaxWidth = 320f;
    internal const float FlyoutPanelTopInset = 16f;
    internal const float FlyoutPanelRowInset = 20f;
    internal const float FlyoutRowHeight = 44f;
    internal const float FlyoutRowGap = 8f;

    /// <summary>
    /// Materialized rich rows of the drawer (T14: rich FlyoutHeader/Footer sections, the canonical
    /// flyout rows - Shell.ItemTemplate item rows, Shell.MenuItemTemplate menu rows and
    /// AsMultipleItems children - and a FlyoutContent body). Empty for a text-only panel, which
    /// then keeps the flat FlyoutItems geometry below; with rows, the row frames drive drawing
    /// and hit-testing so header sections can be real views and item rows can be full templates.
    /// </summary>
    internal List<OpenHarmonyFlyoutRow> FlyoutRows { get; } = new();

    /// <summary>Physical (canvas space) panel rectangle: docked to the flyout's start edge.</summary>
    internal RectF FlyoutPanelRect()
    {
        RectF frame = CanvasFrame;
        float width = Math.Min(FlyoutPanelMaxWidth, frame.Width);
        return new RectF(FlowRightToLeft ? frame.Right - width : frame.X, frame.Y, width, frame.Height);
    }

    /// <summary>Dimming scrim over the covered content: one shared colour, not per-frame.</summary>
    private static readonly Color s_flyoutScrim = Colors.Black.WithAlpha(0.5f);

    public void DrawFlyoutPanel(MauiCanvas canvas)
    {
        RectF frame = CanvasFrame;
        float width = Math.Min(FlyoutPanelMaxWidth, frame.Width);
        // The panel is the flyout's start edge: physical left in LTR, right in RTL; the menu
        // rows keep their vertical geometry and align their text to the panel's start edge.
        float panelX = FlowRightToLeft ? frame.Right - width : frame.X;
        canvas.FillColor = s_flyoutScrim;
        canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
        canvas.FillColor = Colors.DimGray;
        canvas.FillRectangle(panelX, frame.Y, width, frame.Height);
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(26);
        canvas.FontColor = Colors.White;
        if (FlyoutRows.Count > 0)
        {
            // Rich panel: every row has its physical frame; text rows draw their label (rich
            // rows are drawn by the renderer as real views, after this background pass).
            foreach (OpenHarmonyFlyoutRow row in FlyoutRows)
            {
                if (row.Text is { } text)
                {
                    canvas.DrawString(text, row.Frame.X, row.Frame.Y, row.Frame.Width, row.Frame.Height,
                        FlowRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment.Center);
                }
            }
            return;
        }
        for (int i = 0; i < FlyoutItems.Count; i++)
        {
            float rowY = frame.Y + FlyoutPanelTopInset + i * (FlyoutRowHeight + FlyoutRowGap);
            canvas.DrawString(FlyoutItems[i], panelX + FlyoutPanelRowInset, rowY, width - 2 * FlyoutPanelRowInset,
                FlyoutRowHeight, FlowRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment.Center);
        }
    }

    /// <summary>Index of the flyout row at the point (-1 outside the panel, -2 dismiss).</summary>
    public int FlyoutItemAt(float x, float y)
    {
        RectF frame = CanvasFrame;
        float width = Math.Min(FlyoutPanelMaxWidth, frame.Width);
        // Outside the panel (toward the covered detail) dismisses the drawer; the covered side
        // is the physical right of an RTL panel.
        bool outside = FlowRightToLeft ? x < frame.Right - width : x > frame.X + width;
        if (outside)
        {
            return -2;
        }
        if (FlyoutRows.Count > 0)
        {
            foreach (OpenHarmonyFlyoutRow row in FlyoutRows)
            {
                if (row.Frame.Contains(x, y))
                {
                    return row.PanelIndex;
                }
            }
            return -1;
        }
        float relative = y - frame.Y - FlyoutPanelTopInset;
        if (relative < 0)
        {
            return -1;
        }
        int index = (int)(relative / (FlyoutRowHeight + FlyoutRowGap));
        return index >= 0 && index < FlyoutItems.Count ? index : -1;
    }

    // Calendar (DatePicker) support
    public bool IsCalendar { get; set; }
    public int CalendarYear { get; set; }
    public int CalendarMonth { get; set; } = 1;
    public int CalendarSelectedDay { get; set; }
    public Action? CalendarPreviousMonth { get; set; }
    public Action? CalendarNextMonth { get; set; }
    public Action<DateTime>? CalendarSelectDay { get; set; }

    /// <summary>First selectable day (date only); earlier days draw disabled and do not hit-test.</summary>
    public DateTime CalendarMinimum { get; set; } = DateTime.MinValue;

    /// <summary>Last selectable day (date only); later days draw disabled and do not hit-test.</summary>
    public DateTime CalendarMaximum { get; set; } = DateTime.MaxValue;

    public const float CalendarWidth = 380f;
    public const float CalendarHeaderHeight = 52f;
    public const float CalendarRowHeight = 46f;
    public const int CalendarWeeks = 6;

    public static float CalendarHeight => CalendarHeaderHeight + CalendarRowHeight * (CalendarWeeks + 1);

    private static readonly string[] s_calendarWeekdays = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };

    private static readonly string[] s_calendarDayLabels = BuildCalendarDayLabels();

    private static readonly Color s_calendarSelected = Colors.DodgerBlue;
    private static readonly Color s_calendarDisabled = Colors.Gray;

    private int _calendarTitleYear;
    private int _calendarTitleMonth;
    private string _calendarTitle = string.Empty;

    private static string[] BuildCalendarDayLabels()
    {
        var labels = new string[31];
        for (int day = 1; day <= labels.Length; day++)
        {
            labels[day - 1] = day.ToString();
        }
        return labels;
    }

    /// <summary>The header title; the string is rebuilt only when the shown month changes.</summary>
    private string CalendarTitle()
    {
        if (_calendarTitleYear != CalendarYear || _calendarTitleMonth != CalendarMonth)
        {
            _calendarTitleYear = CalendarYear;
            _calendarTitleMonth = CalendarMonth;
            _calendarTitle = $"{CalendarYear:0000}-{CalendarMonth:00}";
        }
        return _calendarTitle;
    }

    /// <summary>
    /// Draws the open month calendar below the field: the header (month navigation clamps at the
    /// min/max month), the Monday-first weekday row and the 6x7 day grid. Days outside
    /// <see cref="CalendarMinimum"/>..<see cref="CalendarMaximum"/> draw disabled.
    /// </summary>
    public void DrawCalendar(MauiCanvas canvas)
    {
        RectF frame = CanvasFrame;
        float x = frame.X;
        float y = frame.Y + frame.Height;
        float width = CalendarWidth;
        // The day/weekday columns run from the start edge: physical left in LTR, right in RTL
        // (a mirrored calendar reads Monday-first from the right, like the platform pickers).
        float ColumnX(int column)
            => x + (FlowRightToLeft ? 6 - column : column) * width / 7f;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(x, y, width, CalendarHeight);
        canvas.StrokeColor = s_calendarDisabled;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(x, y, width, CalendarHeight);

        // Header: < month year >; an edge arrow is greyed when its month is out of range.
        // Previous is the start edge, next the end edge (swapped by an RTL direction).
        float previousX = FlowRightToLeft ? x + width - 48 : x + 8;
        float nextX = FlowRightToLeft ? x + 8 : x + width - 48;
        DateTime firstMonth = new(CalendarMinimum.Year, CalendarMinimum.Month, 1);
        DateTime lastMonth = new(CalendarMaximum.Year, CalendarMaximum.Month, 1);
        DateTime shownMonth = new(CalendarYear, CalendarMonth, 1);
        canvas.FontColor = shownMonth > firstMonth ? Colors.White : s_calendarDisabled;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(26);
        canvas.DrawString("<", previousX, y, 40, CalendarHeaderHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.FontColor = shownMonth < lastMonth ? Colors.White : s_calendarDisabled;
        canvas.DrawString(">", nextX, y, 40, CalendarHeaderHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.FontColor = Colors.White;
        canvas.DrawString(CalendarTitle(), x + 48, y, width - 96, CalendarHeaderHeight,
            HorizontalAlignment.Center, VerticalAlignment.Center);

        // Weekday row (Monday first), matching CalendarHit's row geometry.
        canvas.FontColor = s_calendarDisabled;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(20);
        for (int column = 0; column < 7; column++)
        {
            canvas.DrawString(s_calendarWeekdays[column], ColumnX(column), y + CalendarHeaderHeight,
                width / 7f, CalendarRowHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        // Day grid: selected day highlighted, today accented, out-of-range days disabled.
        DateTime first = new(CalendarYear, CalendarMonth, 1);
        int leading = ((int)first.DayOfWeek + 6) % 7;
        int days = DateTime.DaysInMonth(CalendarYear, CalendarMonth);
        DateTime today = DateTime.Today;
        DateTime min = CalendarMinimum.Date;
        DateTime max = CalendarMaximum.Date;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(22);
        for (int day = 1; day <= days; day++)
        {
            int cell = leading + day - 1;
            int row = cell / 7;
            if (row >= CalendarWeeks)
            {
                break;
            }
            int column = cell % 7;
            float cellX = ColumnX(column);
            float cellY = y + CalendarHeaderHeight + (row + 1) * CalendarRowHeight;
            DateTime date = new(CalendarYear, CalendarMonth, day);
            bool disabled = date < min || date > max;
            bool selected = day == CalendarSelectedDay && !disabled;
            if (selected)
            {
                canvas.FillColor = s_calendarSelected;
                canvas.FillCircle(cellX + width / 14f, cellY + CalendarRowHeight / 2f, CalendarRowHeight / 2f - 2);
            }
            bool isToday = !disabled && CalendarYear == today.Year && CalendarMonth == today.Month && day == today.Day;
            canvas.FontColor = disabled ? s_calendarDisabled
                : selected ? Colors.White
                : isToday ? s_calendarSelected
                : Colors.White;
            canvas.DrawString(s_calendarDayLabels[day - 1], cellX, cellY, width / 7f, CalendarRowHeight,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    /// <summary>Hit test for the calendar: -1 outside, -2 previous, -3 next, 1..31 selectable day.</summary>
    public int CalendarHit(float x, float y)
    {
        RectF frame = CanvasFrame;
        float left = frame.X;
        float top = frame.Y + frame.Height;
        float width = CalendarWidth;
        if (x < left || x > left + width || y < top || y > top + CalendarHeight)
        {
            return -1;
        }
        if (y < top + CalendarHeaderHeight)
        {
            // Previous is the start edge, next the end edge (swapped by an RTL direction).
            if (FlowRightToLeft ? x > left + width - 48 : x < left + 48)
            {
                return -2;
            }
            if (FlowRightToLeft ? x < left + 48 : x > left + width - 48)
            {
                return -3;
            }
            return 0;
        }
        float rowY = y - top - CalendarHeaderHeight;
        int row = (int)(rowY / CalendarRowHeight);
        int column = (int)((x - left) / (width / 7f));
        if (FlowRightToLeft)
        {
            column = 6 - column;
        }
        if (row < 1 || column < 0 || column > 6)
        {
            return 0;
        }
        DateTime first = new(CalendarYear, CalendarMonth, 1);
        int leading = ((int)first.DayOfWeek + 6) % 7;
        int day = (row - 1) * 7 + column - leading + 1;
        int days = DateTime.DaysInMonth(CalendarYear, CalendarMonth);
        if (day < 1 || day > days)
        {
            return 0;
        }
        DateTime date = new(CalendarYear, CalendarMonth, day);
        return date >= CalendarMinimum.Date && date <= CalendarMaximum.Date ? day : 0;
    }

    // Flyout page support
    public bool IsFlyoutPage { get; set; }
    public bool FlyoutPresented { get; set; }
    public float FlyoutWidth { get; set; } = 320f;
    public Action? OpenFlyout { get; set; }
    public Action? FlyoutDismiss { get; set; }
    public const float HamburgerSize = 40f;

    public bool InFlyoutPanel(float x, float y)
    {
        RectF frame = CanvasFrame;
        // The panel is the flyout's start edge: physical left in LTR, right in RTL.
        return FlyoutPresented &&
               (FlowRightToLeft
                   ? x >= frame.Right - FlyoutWidth && x <= frame.Right
                   : x >= frame.X && x <= frame.X + FlyoutWidth);
    }

    public bool InHamburger(float x, float y)
    {
        RectF frame = CanvasFrame;
        // The hamburger sits in the start edge's top corner: physical left in LTR, right in RTL.
        bool inCorner = FlowRightToLeft
            ? x >= frame.Right - HamburgerSize && x <= frame.Right
            : x >= frame.X && x <= frame.X + HamburgerSize;
        return (IsFlyoutPage && !FlyoutPresented || ShowsHamburger) &&
               inCorner &&
               y >= frame.Y && y <= frame.Y + HamburgerSize;
    }

    // Picker support (inline dropdown rendered as an overlay)
    public bool IsPicker { get; set; }
    public List<string> PopupItems { get; } = new();
    public bool PopupVisible { get; set; }
    public int PopupSelectedIndex { get; set; } = -1;
    public string? PopupTitle { get; set; }
    public Action<int>? PopupSelect { get; set; }
    public Action? PopupClosed { get; set; }

    /// <summary>Height of one dropdown row.</summary>
    public const float PopupRowHeight = 46f;

    // Tabbed page support
    public bool IsTabbedPage { get; set; }
    public List<string> TabTitles { get; } = new();

    /// <summary>False hides the bottom bar (Shell.TabBarIsVisible).</summary>
    public bool TabTitlesVisible { get; set; } = true;

    /// <summary>Optional tab icons (encoded bytes) aligned with TabTitles.</summary>
    public List<byte[]?> TabIcons { get; } = new();
    public int SelectedTab { get; set; }
    public Action<int>? TabSelected { get; set; }
    public const float TabBarHeight = 56f;

    // Navigation page support
    public bool IsNavigationPage { get; set; }
    public float NavBarHeight { get; set; } = 48f;
    public string? NavTitle { get; set; }
    public bool CanGoBack { get; set; }
    public Color NavBarColor { get; set; } = Colors.Black;
    public Color NavBarTextColor { get; set; } = Colors.White;
    public Action? BackTapped { get; set; }

    /// <summary>Width of the tappable back area in the navigation bar.</summary>
    public const float NavBackWidth = 88f;

    // RefreshView support
    public bool IsRefreshView { get; set; }
    public bool IsRefreshing { get; set; }
    public Color RefreshColor { get; set; } = Colors.DodgerBlue;
    public Action<bool>? RefreshChanged { get; set; }

    /// <summary>Called by the renderer when a drag over this view completes.</summary>
    public void RefreshDrag(float dx, float dy)
    {
        if (!IsRefreshView || dy < 60 || IsRefreshing)
        {
            return;
        }
        // A pull past the threshold starts a refresh; MAUI runs the command when IsRefreshing flips.
        IsRefreshing = true;
        RefreshChanged?.Invoke(true);
    }

    // SwipeView support
    public bool IsSwipeView { get; set; }
    public bool IsSwipeOpen { get; private set; }
    public bool SwipeOpenToRight { get; private set; }
    public List<(string Text, Color Background, Action Activate)> SwipeItems { get; } = new();
    public float SwipeItemWidth { get; set; } = 140f;
    public Action<bool>? SwipeOpenChanged { get; set; }

    /// <summary>Revealed swipe item rectangle (drawing and hit testing share it).</summary>
    public RectF SwipeItemRect(int index)
    {
        // Swipe reveal geometry stays in physical (gesture) space: the drag direction picks the
        // side, so the flow direction does not move the revealed panel.
        RectF frame = CanvasFrame;
        float height = frame.Height / Math.Max(1, SwipeItems.Count);
        float x = SwipeOpenToRight ? frame.Right - SwipeItemWidth : frame.X;
        return new RectF(x, frame.Y + index * height, SwipeItemWidth, height);
    }

    public int SwipeItemAt(float x, float y)
    {
        if (!IsSwipeOpen)
        {
            return -1;
        }
        for (int i = 0; i < SwipeItems.Count; i++)
        {
            if (SwipeItemRect(i).Contains(x, y))
            {
                return i;
            }
        }
        return -1;
    }

    public void SetSwipeOpen(bool open, bool notify = true)
    {
        if (open && SwipeItems.Count == 0)
        {
            open = false;
        }
        if (IsSwipeOpen == open)
        {
            return;
        }
        IsSwipeOpen = open;
        if (notify)
        {
            SwipeOpenChanged?.Invoke(open);
        }
    }

    /// <summary>Called by the renderer when a drag over this view completes.</summary>
    public void SwipeDrag(float dx, float dy)
    {
        if (!IsSwipeView || Math.Abs(dx) < 40)
        {
            return;
        }
        // Dragging left reveals the trailing (right) items, dragging right the leading ones.
        SwipeOpenToRight = dx < 0;
        SetSwipeOpen(true);
    }

    /// <summary>
    /// Toolbar rows of the current page: primary rows first, then the secondary rows behind the
    /// overflow affordance (OpenHarmonyToolbarMirror fills the list). Drawing, hit-testing and
    /// activation all read this list.
    /// </summary>
    public List<OpenHarmonyToolbarItem> ToolbarItems { get; } = new();
    public float ToolbarItemWidth { get; set; } = 140f;

    /// <summary>Width of the overflow ("more") affordance at the bar's trailing edge.</summary>
    public const float ToolbarMoreWidth = 56f;

    /// <summary>Height of one overflow dropdown row.</summary>
    public const float ToolbarOverflowRowHeight = 46f;

    /// <summary>Height of the bar owning the toolbar (nav bar or shell title bar).</summary>
    public float ToolbarBarHeight => IsNavigationPage ? NavBarHeight : TitleBarHeight;

    /// <summary>True when this platform view owns a toolbar (nav page bar or shell title bar).</summary>
    public bool ShowsToolbar => IsNavigationPage || ShowsTitleBar;

    /// <summary>True while the overflow dropdown is open.</summary>
    public bool ToolbarOverflowOpen { get; set; }

    /// <summary>True when the page has secondary items (the overflow affordance is drawn).</summary>
    public bool HasToolbarOverflow
    {
        get
        {
            foreach (OpenHarmonyToolbarItem item in ToolbarItems)
            {
                if (item.IsSecondary)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Rectangle of a primary toolbar item (drawing and hit testing share it).</summary>
    public RectF ToolbarItemRect(int index)
    {
        RectF frame = CanvasFrame;
        // Primary items are trailing actions: docked to the physical right in LTR, left in RTL,
        // inside the more affordance when one exists.
        float inset = FlowRightToLeft ? frame.X : frame.Right;
        if (HasToolbarOverflow)
        {
            inset += FlowRightToLeft ? ToolbarMoreWidth : -ToolbarMoreWidth;
        }
        float x = FlowRightToLeft ? inset + index * ToolbarItemWidth : inset - (index + 1) * ToolbarItemWidth;
        return new RectF(x, frame.Y, ToolbarItemWidth, ToolbarBarHeight);
    }

    /// <summary>Rectangle of the overflow affordance (empty when no secondary item exists).</summary>
    public RectF ToolbarMoreRect()
    {
        RectF frame = CanvasFrame;
        float x = FlowRightToLeft ? frame.X : frame.Right - ToolbarMoreWidth;
        return new RectF(x, frame.Y, ToolbarMoreWidth, ToolbarBarHeight);
    }

    /// <summary>Rectangle of an overflow row (below the bar, trailing-aligned).</summary>
    public RectF ToolbarOverflowRect(int row)
    {
        RectF frame = CanvasFrame;
        float width = Math.Min(220f, frame.Width);
        float x = FlowRightToLeft ? frame.X : frame.Right - width;
        return new RectF(x, frame.Y + ToolbarBarHeight + row * ToolbarOverflowRowHeight, width, ToolbarOverflowRowHeight);
    }

    /// <summary>Ordinal of the primary toolbar item at the point (-1 when outside every item).</summary>
    public int ToolbarItemAt(float x, float y)
    {
        int index = 0;
        foreach (OpenHarmonyToolbarItem item in ToolbarItems)
        {
            if (item.IsSecondary)
            {
                continue;
            }
            if (ToolbarItemRect(index).Contains(x, y))
            {
                return index;
            }
            index++;
        }
        return -1;
    }

    /// <summary>True when the point is inside the overflow affordance.</summary>
    public bool InToolbarMore(float x, float y)
        => HasToolbarOverflow && ToolbarMoreRect().Contains(x, y);

    /// <summary>Row index of the open overflow dropdown (-1 when closed or outside it).</summary>
    public int ToolbarOverflowIndexAt(float x, float y)
    {
        if (!ToolbarOverflowOpen)
        {
            return -1;
        }
        int row = 0;
        foreach (OpenHarmonyToolbarItem item in ToolbarItems)
        {
            if (!item.IsSecondary)
            {
                continue;
            }
            if (ToolbarOverflowRect(row).Contains(x, y))
            {
                return row;
            }
            row++;
        }
        return -1;
    }

    /// <summary>Activates a primary item by ordinal (false for a disabled or missing item).</summary>
    public bool ActivateToolbarItem(int index)
    {
        OpenHarmonyToolbarItem? item = ToolbarItemAtOrdinal(index, secondary: false);
        if (item is null || !item.IsEnabled)
        {
            return false;
        }
        item.Activate();
        return true;
    }

    /// <summary>Opens/closes the overflow dropdown and requests a frame.</summary>
    public void SetToolbarOverflow(bool open)
    {
        if (ToolbarOverflowOpen == open)
        {
            return;
        }
        ToolbarOverflowOpen = open && HasToolbarOverflow;
        RequestToolbarRedraw();
    }

    /// <summary>Activates the secondary item behind an overflow row (and closes the dropdown).</summary>
    public bool ActivateToolbarOverflow(int row)
    {
        OpenHarmonyToolbarItem? item = ToolbarItemAtOrdinal(row, secondary: true);
        if (item is null || !item.IsEnabled)
        {
            return false;
        }
        SetToolbarOverflow(false);
        item.Activate();
        return true;
    }

    /// <summary>The ordinal-th item of the primary or secondary partition.</summary>
    private OpenHarmonyToolbarItem? ToolbarItemAtOrdinal(int ordinal, bool secondary)
    {
        int index = 0;
        foreach (OpenHarmonyToolbarItem item in ToolbarItems)
        {
            if (item.IsSecondary != secondary)
            {
                continue;
            }
            if (index++ == ordinal)
            {
                return item;
            }
        }
        return null;
    }

    private static void RequestToolbarRedraw()
    {
        try
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
        }
        catch (Exception)
        {
            // No host: the next input/frame event repaints anyway.
        }
    }

    public bool InBackRegion(float x, float y)
    {
        RectF frame = CanvasFrame;
        // The back region is the navigation bar's leading edge: physical left in LTR, right in RTL.
        bool inSlot = FlowRightToLeft
            ? x >= frame.Right - NavBackWidth && x <= frame.Right
            : x >= frame.X && x <= frame.X + NavBackWidth;
        return IsNavigationPage && CanGoBack &&
               y >= frame.Y && y <= frame.Y + NavBarHeight &&
               inSlot;
    }

    // Scroll support
    public bool IsScrollView { get; set; }

    private float _scrollOffsetX;
    private float _scrollOffsetY;

    /// <summary>
    /// Horizontal scroll offset. Writes feed the shared scroll physics so a fling can pick up
    /// the velocity of a drag; with the physics disabled the property is a plain assignment.
    /// </summary>
    public float ScrollOffsetX
    {
        get => _scrollOffsetX;
        set
        {
            if (value == _scrollOffsetX)
            {
                return;
            }
            float previous = _scrollOffsetX;
            _scrollOffsetX = value;
            OpenHarmonyScrollPhysics.OnOffsetChanged(this, horizontal: true, previous, value);
        }
    }

    /// <summary>Vertical scroll offset (see <see cref="ScrollOffsetX"/>).</summary>
    public float ScrollOffsetY
    {
        get => _scrollOffsetY;
        set
        {
            if (value == _scrollOffsetY)
            {
                return;
            }
            float previous = _scrollOffsetY;
            _scrollOffsetY = value;
            OpenHarmonyScrollPhysics.OnOffsetChanged(this, horizontal: false, previous, value);
        }
    }

    public float ScrollContentWidth { get; set; }
    public float ScrollContentHeight { get; set; }
    /// <summary>Invoked when the scroll offset changes (virtualized lists slide their window).</summary>
    public Action? ScrollOffsetChanged { get; set; }

    public List<OpenHarmonyView> Children { get; } = new();

    /// <summary>
    /// Views owned by the platform side (collection view items materialized from a template):
    /// the renderer draws and hit-tests them like ordinary children.
    /// </summary>
    public List<IView> ViewChildren { get; } = new();

    /// <summary>
    /// The view's logical MAUI frame (window coordinates, before flow-direction mirroring).
    /// Arrange-time readers (the page/tab/flyout/shell handlers that compute child frames) use
    /// this, exactly like <c>IView.Frame</c> on the native platforms; drawing and hit-testing use
    /// <see cref="CanvasFrame"/>.
    /// </summary>
    public RectF Frame => VirtualView?.Frame is Rect frame
        ? new RectF((float)frame.X, (float)frame.Y, (float)frame.Width, (float)frame.Height)
        : default;

    private OpenHarmonyFlowMap _flowMap = OpenHarmonyFlowMap.Identity;

    /// <summary>
    /// Effective flow direction resolved by the compositor walk (false without a walk: LTR).
    /// Drives every start/end surface: text alignment, the clear button, navigation/menu sides,
    /// the picker dropdown, the calendar arrows and the like.
    /// </summary>
    internal bool FlowRightToLeft { get; private set; }

    /// <summary>
    /// Canvas-space frame: the logical frame mapped through the walk's flow map. Drawing,
    /// clipping and hit-testing share this one; an unmirrored (LTR) view maps to itself.
    /// </summary>
    internal RectF CanvasFrame => _flowMap.Map(Frame);

    /// <summary>Hands the compositor walk's flow map and resolved direction to the view.</summary>
    internal void SetFlowContext(in OpenHarmonyFlowMap map, bool rightToLeft)
    {
        _flowMap = map;
        FlowRightToLeft = rightToLeft;
    }

    /// <summary>
    /// Physical alignment for a MAUI Start/End alignment: right-to-left swaps the two edges
    /// (Center/Justify are unchanged).
    /// </summary>
    internal TextAlignment ResolveHorizontalTextAlignment(TextAlignment alignment) => (alignment, FlowRightToLeft) switch
    {
        (TextAlignment.Start, true) => TextAlignment.End,
        (TextAlignment.End, true) => TextAlignment.Start,
        _ => alignment,
    };

    /// <summary>
    /// Draws the view's MAUI shadow (<see cref="IView.Shadow"/>) behind its content. The host
    /// canvas shadow layer takes an offset, a blur and one colour, so the view's frame is filled
    /// with a fully transparent brush (the geometry casts the shadow, the brush itself stays
    /// invisible); the effect is cleared before the view draws. A shadow the drawing cannot
    /// express (non-solid paint) is reported once instead of being dropped.
    /// </summary>
    public void DrawShadow(MauiCanvas canvas)
    {
        if (VirtualView?.Shadow is not { } shadow || shadow.Opacity <= 0 || shadow.Radius <= 0)
        {
            return;
        }
        Paint? paint = shadow.Paint;
        if (paint is PatternPaint { Pattern: PaintPattern wrapper })
        {
            paint = wrapper.Paint;
        }
        if (paint is not SolidPaint { Color: { } color })
        {
            OpenHarmonyStatus.Once("shadow.paint." + (paint?.GetType().Name ?? "null"),
                $"IShadow.Paint '{paint?.GetType().Name ?? "<null>"}' is not a solid colour: the " +
                "OpenHarmony shadow layer carries a single colour, so this shadow was not drawn");
            return;
        }
        RectF frame = CanvasFrame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        float alpha = Math.Clamp(shadow.Opacity, 0f, 1f) * color.Alpha *
            (float)Math.Clamp(VirtualView.Opacity, 0d, 1d);
        if (alpha <= 0)
        {
            return;
        }
        Color previousFill = canvas.FillColor;
        // FillColor resets the host effects; the shadow layer then applies to the next fill.
        canvas.FillColor = Colors.Transparent;
        canvas.SetShadow(new SizeF((float)shadow.Offset.X, (float)shadow.Offset.Y), shadow.Radius,
            color.WithAlpha(alpha));
        if (CornerRadius > 0)
        {
            canvas.FillRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
        }
        else
        {
            canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
        }
        // Never leak the shadow layer into the view's own fills/text.
        canvas.FillColor = previousFill;
        Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.ClearEffects();
    }

    /// <summary>Fills this view's background rect (rounded when a corner radius is set).</summary>
    internal void FillBackgroundRect(MauiCanvas canvas, RectF frame)
    {
        if (CornerRadius > 0)
        {
            canvas.FillRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
        }
        else
        {
            canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
        }
    }

    public virtual void Draw(MauiCanvas canvas)
    {
        RectF frame = CanvasFrame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        if (Background is not null)
        {
            canvas.FillColor = BackgroundForDraw!;
            FillBackgroundRect(canvas, frame);
            // Press feedback: the previous static pressed look was a full OrangeRed fill. Drawing
            // it as an overlay whose alpha follows the press progress keeps that exact end state,
            // eases in/out, and allocates nothing per frame.
            float press = PressProgress;
            if (press > 0.001f)
            {
                float savedAlpha = canvas.Alpha;
                canvas.Alpha = savedAlpha * press;
                canvas.FillColor = Colors.OrangeRed;
                FillBackgroundRect(canvas, frame);
                canvas.Alpha = savedAlpha;
            }
        }
        // Focus affordance for the self-drawn route: the PE2/OpenHarmonyFocusManager path marks
        // this platform view's IsFocused, so the outline is painted here (opt-out via
        // OpenHarmonyFocusRing.Enabled).
        OpenHarmonyFocusRing.Draw(canvas, this);
        if (IsNavigationPage)
        {
            DrawNavigationBar(canvas, frame);
            return;
        }
        if (IsShape || IsBorder)
        {
            DrawShape(canvas, frame);
            return;
        }
        if (IsFlyoutPage)
        {
            DrawFlyoutChrome(canvas, frame);
            return;
        }
        if (IsSwipeView && IsSwipeOpen)
        {
            DrawSwipePanel(canvas, frame);
        }
        if (IsRefreshView && IsRefreshing)
        {
            DrawRefreshIndicator(canvas, frame);
        }
        if (IsGraphicsView)
        {
            if (VirtualView is IGraphicsView graphics && graphics.Drawable is { } drawable)
            {
                drawable.Draw(canvas, new RectF(frame.X, frame.Y, frame.Width, frame.Height));
            }
            return;
        }
        if (IsIndicatorView)
        {
            DrawIndicatorView(canvas, frame);
            return;
        }
        if (IsPicker)
        {
            DrawPicker(canvas, frame);
            return;
        }
        if (IsTabbedPage)
        {
            if (ShowsTitleBar)
            {
                DrawTitleBar(canvas, frame);
            }
            DrawTabBar(canvas, frame);
            if (ShowsHamburger && !ShowsBack && !FlyoutOpen)
            {
                DrawHamburger(canvas, frame);
            }
            return;
        }
        if (IsStepper)
        {
            DrawStepper(canvas, frame);
            return;
        }
        if (IsRadioButton)
        {
            DrawRadioButton(canvas, frame);
            return;
        }
        if (IsCheckBox)
        {
            DrawCheckBox(canvas, frame);
            return;
        }
        if (IsSwitch)
        {
            DrawSwitch(canvas, frame);
            return;
        }
        if (IsSlider)
        {
            DrawSlider(canvas, frame);
            return;
        }
        if (IsProgressBar)
        {
            DrawProgress(canvas, frame);
            return;
        }
        if (IsActivityIndicator)
        {
            DrawActivityIndicator(canvas, frame);
            return;
        }
        if (StrokeColor is { } outline && StrokeThickness > 0)
        {
            canvas.StrokeColor = outline;
            canvas.StrokeSize = StrokeThickness;
            if (CornerRadius > 0)
            {
                canvas.DrawRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
            }
            else
            {
                canvas.DrawRectangle(frame.X, frame.Y, frame.Width, frame.Height);
            }
        }
        if (ImageBytes is { Length: > 0 })
        {
            DrawImage(canvas, frame);
            return;
        }
        string? text = Text;
        if (IsTextEntry && string.IsNullOrEmpty(text))
        {
            canvas.FontColor = PlaceholderColor ?? Colors.Gray;
            canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
            float origin = TextOriginX(Placeholder ?? string.Empty);
            float available = Math.Max(8f, frame.X + frame.Width - 12 - origin);
            canvas.DrawString(Placeholder ?? string.Empty, origin, frame.Y, available, frame.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center);
            if (IsFocused)
            {
                DrawComposition(canvas, frame, string.Empty);
                DrawCaret(canvas, frame, string.Empty);
            }
            return;
        }
        if (!string.IsNullOrEmpty(text))
        {
            canvas.FontColor = TextColorForDraw;
            canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
            if (IsTextEntry)
            {
                if (IsFocused)
                {
                    DrawSelection(canvas, frame, text);
                }
                // The masked (password) form is what is drawn; the caret/selection metrics use
                // the same form so the glyphs and the caret stay aligned.
                string display = DisplayText;
                float origin = TextOriginX(display);
                float available = Math.Max(8f, frame.X + frame.Width - 12 - origin);
                canvas.DrawString(display, origin, frame.Y, available, frame.Height,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
                if (IsFocused)
                {
                    // Composition and caret sit on top of the text (the preedit overlays the range it
                    // will replace); the selection handles are the topmost layer so a drag never
                    // hides them under a glyph.
                    DrawComposition(canvas, frame, text);
                    DrawCaret(canvas, frame, text);
                    DrawSelectionHandles(canvas, frame, text);
                }
                if (ClearButtonVisible)
                {
                    DrawClearButton(canvas);
                }
            }
            else
            {
                float padding = CornerRadius > 0 ? 24f : 0f;
                canvas.DrawString(Text, frame.X + padding, frame.Y, frame.Width - padding * 2, frame.Height,
                    FlowRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment.Center);
            }
        }
        if (IsScrollView)
        {
            // Auto-hiding vertical thumb, painted from the view's own pass (opt-out via
            // OpenHarmonyScrollbars.Enabled).
            OpenHarmonyScrollbars.Draw(canvas, this);
        }
        // Children are drawn by OpenHarmonyWindowRenderer walking the MAUI tree; the list here
        // is used for hit-testing.
    }

    private float[]? _charWidths;
    private string? _charWidthsText;
    private float _charWidthsFontSize;

    /// <summary>
    /// Per-character widths (cached per text and drawn font size) for caret hit testing. The
    /// cache key is the scaled size, so a system font scale change recomputes instead of serving
    /// old widths.
    /// </summary>
    private float[] CharWidths(string text)
    {
        float drawFontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
        if (_charWidths is not null && _charWidthsText == text && Math.Abs(_charWidthsFontSize - drawFontSize) < 0.01f)
        {
            return _charWidths;
        }
        var widths = new float[text.Length];
        float previous = 0;
        float accumulated = 0;
        for (int i = 0; i < text.Length; i++)
        {
            string prefix = text[..(i + 1)];
            float prefixWidth;
            if (Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.MeasureText(prefix, drawFontSize, out int measured, out int _) && measured > 0)
            {
                prefixWidth = measured;
            }
            else
            {
                prefixWidth = prefix.Length * drawFontSize * 0.55f;
            }
            widths[i] = Math.Max(0f, prefixWidth - previous);
            previous = prefixWidth;
            accumulated += widths[i];
        }
        _charWidths = widths;
        _charWidthsText = text;
        _charWidthsFontSize = drawFontSize;
        return widths;
    }

    /// <summary>Caret index for an x coordinate inside a text entry (midpoint hit testing).</summary>
    public int CursorIndexFromX(float x)
    {
        string text = Text ?? string.Empty;
        if (text.Length == 0)
        {
            return 0;
        }
        string metrics = MetricsText(text);
        float left = TextOriginX(metrics);
        float relative = x - left;
        if (relative <= 0)
        {
            return 0;
        }
        float[] widths = CharWidths(metrics);
        float accumulated = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            if (relative < accumulated + widths[i] / 2f)
            {
                return i;
            }
            accumulated += widths[i];
        }
        return text.Length;
    }

    /// <summary>
    /// X origin of the (possibly password-masked) text at the current alignment: the left
    /// padding for Start, centred in the padded box for Center, right-aligned for End (Justify
    /// is a single line here and is drawn like Start). The drawing, the caret, the selection and
    /// the hit tests all derive from this one origin, so they cannot drift apart.
    /// </summary>
    /// <param name="measure">
    /// The string whose width positions the box (the drawn text, or the placeholder when there
    /// is none yet); null measures <see cref="DisplayText"/>.
    /// </param>
    internal float TextOriginX(string? measure = null)
    {
        RectF frame = CanvasFrame;
        const float padding = 12f;
        float available = Math.Max(0f, frame.Width - padding * 2);
        float measured = Math.Min(MeasureTextWidth(measure ?? DisplayText), available);
        // Start/End follow the entry's resolved direction: Start is the physical left in LTR
        // (the right edge in RTL), End the opposite edge; Justify is a single line here and is
        // drawn like Start.
        return HorizontalTextAlignment switch
        {
            TextAlignment.Center => frame.X + padding + (available - measured) / 2f,
            TextAlignment.End => FlowRightToLeft ? frame.X + padding : frame.X + frame.Width - padding - measured,
            _ => FlowRightToLeft ? frame.X + frame.Width - padding - measured : frame.X + padding,
        };
    }

    /// <summary>The string the caret metrics measure: the drawn (masked) form of the text.</summary>
    private string MetricsText(string text) => IsPassword ? MaskPassword(text) : text;

    /// <summary>
    /// X coordinate of the caret slot at <paramref name="index"/> (clamped to the text bounds).
    /// The same cached per-character prefix the hit test uses, so the drawn caret, the selection
    /// highlight, the handles and <see cref="CursorIndexFromX"/> always agree.
    /// </summary>
    internal float TextPositionX(string text, int index)
    {
        string metrics = MetricsText(text);
        float left = TextOriginX(metrics);
        if (index <= 0)
        {
            return left;
        }
        if (index > metrics.Length)
        {
            index = metrics.Length;
        }
        float[] widths = CharWidths(metrics);
        float width = 0;
        for (int i = 0; i < index && i < widths.Length; i++)
        {
            width += widths[i];
        }
        return left + width;
    }

    /// <summary>Draws the caret at the cursor (or the composition end), when the blink is on.</summary>
    private void DrawCaret(MauiCanvas canvas, RectF frame, string text)
    {
        if (!CaretVisible)
        {
            return;
        }
        int caretIndex = IsComposing
            ? CompositionOffset + (_compositionText?.Length ?? 0)
            : (CursorPosition >= 0 ? CursorPosition : text.Length);
        float caretX = TextPositionX(text, caretIndex);
        canvas.FillColor = Colors.White;
        canvas.FillRectangle(caretX, frame.Y + 8, 2, frame.Height - 16);
    }

    private void DrawSelection(MauiCanvas canvas, RectF frame, string text)
    {
        if (SelectionLength <= 0)
        {
            return;
        }
        int caret = SelectionEnd;
        int start = SelectionStart;
        float startX = TextPositionX(text, start);
        float endX = TextPositionX(text, caret);
        if (endX > startX)
        {
            canvas.FillColor = s_selectionFill;
            canvas.FillRectangle(startX, frame.Y + 8, endX - startX, frame.Height - 16);
        }
    }

    /// <summary>Selection highlight: one shared colour, not a new Color per drawn frame.</summary>
    private static readonly Color s_selectionFill = Colors.DodgerBlue.WithAlpha(0.4f);

    // Selection handles: the platform's round handles below the text line. The visual circle is
    // SelectionHandleRadius (18 px diameter), the touch target adds a 24 px slop so a fingertip
    // grabs one without pixel precision.
    internal const float SelectionHandleRadius = 9f;
    internal const float SelectionHandleTouchRadius = 24f;

    /// <summary>Selection start index (selection ends at <see cref="CursorPosition"/>).</summary>
    internal int SelectionStart
    {
        get
        {
            string text = Text ?? string.Empty;
            int caret = SelectionEnd;
            return Math.Clamp(caret - Math.Max(0, SelectionLength), 0, text.Length);
        }
    }

    /// <summary>Selection end index (= caret), clamped to the text.</summary>
    internal int SelectionEnd
    {
        get
        {
            string text = Text ?? string.Empty;
            return Math.Clamp(CursorPosition < 0 ? text.Length : CursorPosition, 0, text.Length);
        }
    }

    internal bool HasSelectionHandles => IsFocused && SelectionLength > 0;

    private static float SelectionHandleY(RectF frame) => frame.Y + frame.Height - SelectionHandleRadius - 2f;

    /// <summary>0 = none, 1 = selection start handle, 2 = selection end handle.</summary>
    internal int SelectionHandleHit(float x, float y)
    {
        if (!HasSelectionHandles)
        {
            return 0;
        }
        string text = Text ?? string.Empty;
        float handleY = SelectionHandleY(CanvasFrame);
        float hitSquared = SelectionHandleTouchRadius * SelectionHandleTouchRadius;
        float dx = x - TextPositionX(text, SelectionStart);
        if (dx * dx + (y - handleY) * (y - handleY) <= hitSquared)
        {
            return 1;
        }
        dx = x - TextPositionX(text, SelectionEnd);
        return dx * dx + (y - handleY) * (y - handleY) <= hitSquared ? 2 : 0;
    }

    private void DrawSelectionHandles(MauiCanvas canvas, RectF frame, string text)
    {
        if (!HasSelectionHandles)
        {
            return;
        }
        float y = SelectionHandleY(frame);
        DrawSelectionHandle(canvas, TextPositionX(text, SelectionStart), y);
        DrawSelectionHandle(canvas, TextPositionX(text, SelectionEnd), y);
    }

    private static void DrawSelectionHandle(MauiCanvas canvas, float x, float y)
    {
        canvas.FillColor = s_selectionHandleFill;
        canvas.FillCircle(x, y, SelectionHandleRadius);
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 2;
        canvas.DrawCircle(x, y, SelectionHandleRadius);
    }

    /// <summary>Handle fill: one shared colour, not a new Color per drawn frame.</summary>
    private static readonly Color s_selectionHandleFill = Colors.DodgerBlue;

    /// <summary>Clear-button fill: one shared colour, not a new Color per drawn frame.</summary>
    private static readonly Color s_clearButtonFill = Color.FromArgb("#FF9E9E9E");

    /// <summary>
    /// Draws the clear button (a filled circle with an x) at the trailing edge. Drawing and
    /// <see cref="InClearButton"/> share <see cref="ClearButtonCenter"/>, so the pixels and the
    /// touch target cannot drift apart.
    /// </summary>
    private void DrawClearButton(MauiCanvas canvas)
    {
        PointF center = ClearButtonCenter;
        canvas.FillColor = s_clearButtonFill;
        canvas.FillCircle(center.X, center.Y, ClearButtonRadius);
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 2;
        float arm = ClearButtonRadius * 0.42f;
        canvas.DrawLine(center.X - arm, center.Y - arm, center.X + arm, center.Y + arm);
        canvas.DrawLine(center.X + arm, center.Y - arm, center.X - arm, center.Y + arm);
    }

    /// <summary>Composition highlight behind the preedit (inverse-video style).</summary>
    private static readonly Color s_compositionFill = Colors.DodgerBlue.WithAlpha(0.35f);

    /// <summary>
    /// Draws the IME preedit (composition) over the range it will replace: a highlight, the
    /// composing string and the platform's underline, with the caret sitting after it. The
    /// committed text arrives through <see cref="Text"/> (the shell's TextInput event), which
    /// clears <see cref="CompositionText"/> and replaces the range.
    /// </summary>
    private void DrawComposition(MauiCanvas canvas, RectF frame, string text)
    {
        if (!IsComposing)
        {
            return;
        }
        string composition = _compositionText!;
        int start = Math.Clamp(CompositionOffset, 0, text.Length);
        int end = Math.Clamp(CompositionOffset + composition.Length, 0, text.Length);
        float startX = TextPositionX(text, start);
        float endX = end > start ? TextPositionX(text, end) : startX;
        if (endX > startX)
        {
            canvas.FillColor = s_compositionFill;
            canvas.FillRectangle(startX, frame.Y + 6, endX - startX, frame.Height - 12);
        }
        float available = Math.Max(8f, frame.X + frame.Width - 12 - startX);
        float drawFontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
        canvas.FontColor = TextColorForDraw;
        canvas.FontSize = drawFontSize;
        canvas.DrawString(composition, startX, frame.Y, available, frame.Height,
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.StrokeColor = Colors.DodgerBlue;
        canvas.StrokeSize = 2;
        float underlineWidth = Math.Min(available, Math.Max(8f, MeasureTextWidth(composition)));
        float underlineY = frame.Y + frame.Height / 2f + drawFontSize * 0.55f;
        canvas.DrawLine(startX, underlineY, startX + underlineWidth, underlineY);
    }

    private float MeasureTextWidth(string value)
    {
        float drawFontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
        if (Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.MeasureText(value, drawFontSize, out int measured, out int _) && measured > 0)
        {
            return measured;
        }
        return value.Length * drawFontSize * 0.55f;
    }

    private static readonly Color s_imagePlaceholderFill = Color.FromArgb("#FFE8E8E8");
    private static readonly Color s_imagePlaceholderStroke = Color.FromArgb("#FFBDBDBD");

    /// <summary>
    /// Draws the image progressively: one coarse preview decode for a large destination (fast,
    /// bounded), then the display-size decode on the frame the redraw requests. A small
    /// destination skips the preview; a failed decode falls back to a neutral placeholder.
    /// </summary>
    private void DrawImage(MauiCanvas canvas, RectF frame)
    {
        float imageWidth = frame.Width;
        float imageHeight = frame.Height;
        float x = frame.X;
        float y = frame.Y;
        // Aspect fitting is limited to what the platform view knows (intrinsic size is not
        // tracked yet): AspectFill/Center keep the frame, Fit preserves the square case.
        if (ImageAspect == Aspect.AspectFit && Math.Abs(imageWidth - imageHeight) > 0.5f)
        {
            float side = Math.Min(imageWidth, imageHeight);
            x += (imageWidth - side) / 2f;
            y += (imageHeight - side) / 2f;
            imageWidth = imageHeight = side;
        }
        var destination = new RectF(x, y, imageWidth, imageHeight);
        ImageDrawn?.Invoke(destination);
        if (destination.Width <= 0.5f || destination.Height <= 0.5f)
        {
            return;
        }
        if (_imageFailedGeneration == _imageGeneration)
        {
            DrawImagePlaceholder(canvas, destination);
            return;
        }
        byte[] bytes = ImageBytes!;
        int targetWidth = Math.Max(1, (int)MathF.Round(destination.Width));
        int targetHeight = Math.Max(1, (int)MathF.Round(destination.Height));
        bool progressive = Math.Max(targetWidth, targetHeight) >= ImagePreviewMinEdgePx;
        if (progressive && _imageStageGeneration != _imageGeneration)
        {
            // First sight of this image: decode the coarse preview, then ask for the frame that
            // replaces it with the display-size decode. The host sizes the decode, so the
            // preview never pays for the full source.
            int previewWidth = Math.Max(1, targetWidth / ImagePreviewDivisor);
            int previewHeight = Math.Max(1, targetHeight / ImagePreviewDivisor);
            LastImageDecodeWidth = previewWidth;
            LastImageDecodeHeight = previewHeight;
            ImageDrawRequested?.Invoke(this, destination, previewWidth, previewHeight, 1);
            bool? preview = DrawImageBytes(bytes, destination, previewWidth, previewHeight);
            if (preview is true)
            {
                ImageDecodePasses = 1;
                _imageStageGeneration = _imageGeneration;
                Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
                return;
            }
            if (preview is null)
            {
                // No sized host path (older host library, or tests): keep the existing behavior.
                LegacyDrawImageBytes(bytes, destination);
                _imageStageGeneration = _imageGeneration;
                return;
            }
            ImageDecodePasses = 0;
            _imageFailedGeneration = _imageGeneration;
            DrawImagePlaceholder(canvas, destination);
            return;
        }
        LastImageDecodeWidth = targetWidth;
        LastImageDecodeHeight = targetHeight;
        ImageDrawRequested?.Invoke(this, destination, targetWidth, targetHeight, 2);
        bool? final = DrawImageBytes(bytes, destination, targetWidth, targetHeight);
        if (final is true)
        {
            ImageDecodePasses = 2;
            _imageStageGeneration = _imageGeneration;
        }
        else if (final is null)
        {
            LegacyDrawImageBytes(bytes, destination);
            _imageStageGeneration = _imageGeneration;
        }
        else if (ImageDecodePasses <= 0)
        {
            // The decode failed and no preview is on screen: degrade to the placeholder.
            _imageFailedGeneration = _imageGeneration;
            DrawImagePlaceholder(canvas, destination);
        }
        // A failed display-size decode keeps the already-drawn preview visible.
    }

    /// <summary>The host-sized blit; the test seam can replace it (null = host unavailable).</summary>
    private static bool? DrawImageBytes(byte[] bytes, RectF destination, int decodeWidth, int decodeHeight)
    {
        if (ImageDrawOverride is { } over)
        {
            return over(bytes, (int)destination.X, (int)destination.Y, (int)destination.Width,
                        (int)destination.Height, decodeWidth, decodeHeight);
        }
        return Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytesSized(
            bytes, (int)destination.X, (int)destination.Y, (int)destination.Width,
            (int)destination.Height, decodeWidth, decodeHeight);
    }

    /// <summary>The pre-P2b blit: full-resolution decode, used when the host lacks the sized path.</summary>
    private static void LegacyDrawImageBytes(byte[] bytes, RectF destination)
        => Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytes(
            bytes, (int)destination.X, (int)destination.Y, (int)destination.Width,
            (int)destination.Height);

    /// <summary>Neutral fallback for an undecodable image (no exception, no blank frame).</summary>
    private void DrawImagePlaceholder(MauiCanvas canvas, RectF destination)
    {
        canvas.FillColor = s_imagePlaceholderFill;
        canvas.FillRectangle(destination.X, destination.Y, destination.Width, destination.Height);
        canvas.StrokeColor = s_imagePlaceholderStroke;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(destination.X, destination.Y, destination.Width, destination.Height);
        ImagePlaceholderDrawn?.Invoke(this, destination);
    }

    private void DrawFlyoutChrome(MauiCanvas canvas, RectF frame)
    {
        if (ShowsHamburger)
        {
            DrawHamburger(canvas, frame);
            return;
        }
        // The hamburger and the panel live at the flyout's start edge: physical left in LTR,
        // right in RTL.
        float iconX = FlowRightToLeft ? frame.Right - 36f : frame.X + 8f;
        if (!FlyoutPresented)
        {
            canvas.FillColor = Colors.Black;
            canvas.FillRoundedRectangle(iconX, frame.Y + 8, 28, 28, 4);
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 2;
            for (int line = 0; line < 3; line++)
            {
                float lineY = frame.Y + 15 + line * 7;
                canvas.DrawLine(iconX + 5, lineY, iconX + 23, lineY);
            }
            return;
        }
        float width = Math.Min(FlyoutWidth, frame.Width);
        float panelX = FlowRightToLeft ? frame.Right - width : frame.X;
        canvas.FillColor = Colors.DimGray;
        canvas.FillRectangle(panelX, frame.Y, width, frame.Height);
        canvas.StrokeColor = Colors.Gray;
        canvas.StrokeSize = 1;
        canvas.DrawLine(panelX + width, frame.Y, panelX + width, frame.Y + frame.Height);
    }

    private void DrawHamburger(MauiCanvas canvas, RectF frame)
    {
        float iconX = FlowRightToLeft ? frame.Right - 36f : frame.X + 8f;
        canvas.FillColor = Colors.Black;
        canvas.FillRoundedRectangle(iconX, frame.Y + 8, 28, 28, 4);
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 2;
        for (int line = 0; line < 3; line++)
        {
            float lineY = frame.Y + 15 + line * 7;
            canvas.DrawLine(iconX + 5, lineY, iconX + 23, lineY);
        }
    }

    private void DrawIndicatorView(MauiCanvas canvas, RectF frame)
    {
        if (IndicatorCount <= 1)
        {
            return;
        }
        float spacing = Math.Max(DotsSize * 2.5f, 12f);
        float startX = frame.X + (frame.Width - IndicatorCount * spacing) / 2f + spacing / 2f;
        float y = frame.Y + frame.Height / 2f;
        for (int i = 0; i < IndicatorCount; i++)
        {
            bool active = i == IndicatorPosition;
            canvas.FillColor = active ? SelectedDotsColor : DotsColor;
            canvas.FillCircle(startX + i * spacing, y, active ? DotsSize * 1.2f : DotsSize);
        }
    }

    private void DrawPicker(MauiCanvas canvas, RectF frame)
    {
        canvas.FontColor = TextColor;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
        string text = Text ?? string.Empty;
        // The value starts at the field's start edge; the chevron sits at the trailing edge.
        canvas.DrawString(text, frame.X + 12, frame.Y, frame.Width - 44, frame.Height,
            FlowRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.StrokeColor = TextColor;
        canvas.StrokeSize = 2;
        float cx = FlowRightToLeft ? frame.X + 20 : frame.X + frame.Width - 20;
        float cy = frame.Y + frame.Height / 2f;
        canvas.DrawLine(cx - 8, cy - 4, cx, cy + 5);
        canvas.DrawLine(cx, cy + 5, cx + 8, cy - 4);
    }

    /// <summary>Draws the open dropdown on top of everything else.</summary>
    public void DrawPopup(MauiCanvas canvas)
    {
        if (!PopupVisible)
        {
            return;
        }
        if (IsCalendar)
        {
            DrawCalendar(canvas);
            return;
        }
        RectF frame = CanvasFrame;
        if (PopupItems.Count == 0)
        {
            return;
        }
        float width = Math.Max(frame.Width, 220f);
        float height = PopupItems.Count * PopupRowHeight;
        // The dropdown spans from the field's start edge: physical left in LTR (growing right),
        // the right edge in RTL (growing left). Rows align to the same start edge.
        float x = FlowRightToLeft ? frame.Right - width : frame.X;
        float y = frame.Y + frame.Height;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(x, y, width, height);
        canvas.StrokeColor = Colors.Gray;
        canvas.StrokeSize = 1;
        canvas.DrawRectangle(x, y, width, height);
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
        // Only rows the surface can show are drawn. A long dropdown (a picker with hundreds of
        // items) otherwise pays one native text draw per item on every frame, and the canvas
        // clips every row below the surface anyway, so the pixels are unchanged.
        int lastRow = PopupItems.Count;
        if (SurfaceViewportHeight > 0)
        {
            lastRow = (int)Math.Ceiling((SurfaceViewportHeight - y) / PopupRowHeight);
            lastRow = Math.Clamp(lastRow, 0, PopupItems.Count);
        }
        for (int i = 0; i < lastRow; i++)
        {
            float rowY = y + i * PopupRowHeight;
            canvas.FontColor = i == PopupSelectedIndex ? Colors.DodgerBlue : Colors.White;
            canvas.DrawString(PopupItems[i], x + 16, rowY, width - 32, PopupRowHeight,
                FlowRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    /// <summary>
    /// Size of the surface the compositor last drew into. The popup and carousel indicator draws
    /// use it to skip rows/dots the canvas clips; 0 means unknown and draws everything.
    /// </summary>
    internal static int SurfaceViewportWidth { get; private set; }

    internal static int SurfaceViewportHeight { get; private set; }

    internal static void SetSurfaceViewport(int width, int height)
    {
        SurfaceViewportWidth = width;
        SurfaceViewportHeight = height;
    }

    /// <summary>Index of the dropdown row at the point (-1 when outside the popup).</summary>
    public int PopupIndexAt(float x, float y)
    {
        RectF frame = CanvasFrame;
        if (!PopupVisible)
        {
            return -1;
        }
        float width = Math.Max(frame.Width, 220f);
        float top = frame.Y + frame.Height;
        // The dropdown spans from the field's start edge (see DrawPopup).
        bool outside = FlowRightToLeft
            ? x < frame.Right - width || x > frame.Right
            : x < frame.X || x > frame.X + width;
        if (outside || y < top)
        {
            return -1;
        }
        int index = (int)((y - top) / PopupRowHeight);
        return index >= 0 && index < PopupItems.Count ? index : -1;
    }

    private void DrawTitleBar(MauiCanvas canvas, RectF frame)
    {
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(frame.X, frame.Y, frame.Width, TitleBarHeight);
        if (ShowsBack)
        {
            // The back chevron is the bar's leading affordance: physical left in LTR, right in RTL.
            float cx = FlowRightToLeft ? frame.Right - 26f : frame.X + 26f;
            float cy = frame.Y + TitleBarHeight / 2f;
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 3;
            if (FlowRightToLeft)
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
        canvas.FontColor = Colors.White;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(28);
        canvas.DrawString(TitleText ?? string.Empty, frame.X + 56, frame.Y, frame.Width - 112, TitleBarHeight,
            HorizontalAlignment.Center, VerticalAlignment.Center);
        // The current page's toolbar rows (ShellChrome mirror): the same primary/secondary
        // surface the navigation bar draws.
        DrawToolbarItems(canvas, frame, Colors.White);
    }

    private void DrawTabBar(MauiCanvas canvas, RectF frame)
    {
        float barY = frame.Y + frame.Height - TabBarHeight;
        canvas.FillColor = Colors.Black;
        canvas.FillRectangle(frame.X, barY, frame.Width, TabBarHeight);
        if (!TabTitlesVisible || TabTitles.Count == 0)
        {
            return;
        }
        float tabWidth = frame.Width / TabTitles.Count;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
        for (int i = 0; i < TabTitles.Count; i++)
        {
            // Tabs run from the start edge: index 0 is the physical left tab in LTR, the right
            // tab in RTL (matching TabIndexAt).
            float tabX = frame.X + (FlowRightToLeft ? TabTitles.Count - 1 - i : i) * tabWidth;
            bool active = i == SelectedTab;
            if (i < TabIcons.Count && TabIcons[i] is { Length: > 0 } icon)
            {
                // Icon above a smaller caption, like the platform tab bars.
                const float iconSize = 24f;
                int width = (int)Math.Min(iconSize, tabWidth - 8);
                int height = width;
                Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytes(
                    icon, (int)(tabX + (tabWidth - width) / 2f), (int)(barY + 6), width, height);
                canvas.FontColor = active ? Colors.DodgerBlue : Colors.Gray;
                canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(18);
                canvas.DrawString(TabTitles[i], tabX, barY + 6 + iconSize, tabWidth, TabBarHeight - iconSize - 8,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
                canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
                continue;
            }
            canvas.FontColor = active ? Colors.DodgerBlue : Colors.Gray;
            canvas.DrawString(TabTitles[i], tabX, barY, tabWidth, TabBarHeight,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    /// <summary>Tab index at the point (-1 when outside the tab bar).</summary>
    public int TabIndexAt(float x, float y)
    {
        RectF frame = CanvasFrame;
        if (!IsTabbedPage || TabTitles.Count == 0)
        {
            return -1;
        }
        if (y < frame.Y + frame.Height - TabBarHeight || x < frame.X || x > frame.X + frame.Width)
        {
            return -1;
        }
        float tabWidth = frame.Width / TabTitles.Count;
        int index = (int)((x - frame.X) / tabWidth);
        if (FlowRightToLeft)
        {
            // Tabs run from the start edge (see DrawTabBar): the physical rightmost slot is index 0.
            index = TabTitles.Count - 1 - index;
        }
        return index >= 0 && index < TabTitles.Count ? index : -1;
    }

    /// <summary>Fills/strokes a shape (IShape.PathForBounds) inside the frame.</summary>
    private void DrawShape(MauiCanvas canvas, RectF frame)
    {
        // MAUI's Shape.PathForBounds returns geometry in the shape's own coordinate space
        // (starting near 0,0), so the canvas is translated to the view's frame instead.
        Microsoft.Maui.Graphics.PathF? path = Shape?.PathForBounds(new RectF(0, 0, frame.Width, frame.Height));
        if (path is null)
        {
            return;
        }
        canvas.SaveState();
        canvas.Translate(frame.X, frame.Y);
        Color? fill = (ShapeFill as SolidPaint)?.Color;
        if (fill is not null)
        {
            canvas.FillColor = fill;
            canvas.FillPath(path);
        }
        Color stroke = IsBorder ? BorderStroke : ShapeStroke;
        float thickness = IsBorder ? BorderStrokeThickness : ShapeStrokeThickness;
        if (thickness > 0 && stroke.Alpha > 0)
        {
            canvas.StrokeColor = stroke;
            canvas.StrokeSize = thickness;
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    private void DrawStepper(MauiCanvas canvas, RectF frame)
    {
        float height = Math.Min(frame.Height, 32f);
        float y = frame.Y + (frame.Height - height) / 2f;
        canvas.FillColor = Background ?? Colors.DimGray;
        canvas.FillRoundedRectangle(frame.X, y, frame.Width, height, 6);
        canvas.FontColor = TextColor;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(26);
        // Decrement is the start half, increment the end half: physical left/right in LTR,
        // swapped in RTL (matching OnTouch's half split).
        canvas.DrawString(FlowRightToLeft ? "+" : "-", frame.X, y, frame.Width / 2, height, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.DrawString(FlowRightToLeft ? "-" : "+", frame.X + frame.Width / 2, y, frame.Width / 2, height, HorizontalAlignment.Center, VerticalAlignment.Center);
        canvas.DrawString($"{StepperValue:0.##}", frame.X, frame.Y - 22, frame.Width, 20, HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private void DrawRadioButton(MauiCanvas canvas, RectF frame)
    {
        float side = Math.Min(Math.Min(frame.Width, frame.Height), 26f);
        // The circle is the control's leading affordance: physical left in LTR, right in RTL.
        float cx = FlowRightToLeft ? frame.Right - side / 2 - 2 : frame.X + side / 2 + 2;
        float cy = frame.Y + frame.Height / 2f;
        canvas.StrokeColor = RadioColor;
        canvas.StrokeSize = 2;
        canvas.DrawCircle(cx, cy, side / 2);
        if (RadioChecked)
        {
            canvas.FillColor = RadioColor;
            canvas.FillCircle(cx, cy, side / 2 - 4);
        }
        if (!string.IsNullOrEmpty(Text))
        {
            canvas.FontColor = TextColor;
            canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(FontSize);
            if (FlowRightToLeft)
            {
                canvas.DrawString(Text, frame.X, frame.Y, Math.Max(1f, cx - side / 2 - frame.X), frame.Height,
                    HorizontalAlignment.Right, VerticalAlignment.Center);
            }
            else
            {
                canvas.DrawString(Text, cx + side, frame.Y, frame.Width - side - 4, frame.Height,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
            }
        }
    }

    private void DrawRefreshIndicator(MauiCanvas canvas, RectF frame)
    {
        float cx = frame.Center.X;
        float cy = frame.Y + 20;
        canvas.StrokeColor = RefreshColor;
        canvas.StrokeSize = 4;
        canvas.DrawArc(cx - 14, cy - 14, 28, 28, 20, 300, false, false);
    }

    private void DrawSwipePanel(MauiCanvas canvas, RectF frame)
    {
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(24);
        for (int i = 0; i < SwipeItems.Count; i++)
        {
            RectF item = SwipeItemRect(i);
            canvas.FillColor = SwipeItems[i].Background;
            canvas.FillRectangle(item.X, item.Y, item.Width, item.Height);
            canvas.FontColor = Colors.White;
            canvas.DrawString(SwipeItems[i].Text, item.X, item.Y, item.Width, item.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    private void DrawNavigationBar(MauiCanvas canvas, RectF frame)
    {
        canvas.FillColor = NavBarColor;
        canvas.FillRectangle(frame.X, frame.Y, frame.Width, NavBarHeight);
        if (CanGoBack)
        {
            // The back chevron is the navigation bar's leading affordance: physical left in LTR,
            // right in RTL (matching InBackRegion).
            float cx = FlowRightToLeft ? frame.Right - 26f : frame.X + 26f;
            float cy = frame.Y + NavBarHeight / 2f;
            canvas.StrokeColor = NavBarTextColor;
            canvas.StrokeSize = 3;
            if (FlowRightToLeft)
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
        canvas.FontColor = NavBarTextColor;
        canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(30);
        canvas.DrawString(NavTitle ?? string.Empty, frame.X + NavBackWidth, frame.Y,
            frame.Width - NavBackWidth * 2, NavBarHeight, HorizontalAlignment.Center, VerticalAlignment.Center);
        DrawToolbarItems(canvas, frame, NavBarTextColor);
        // The current page is drawn by the renderer, translated below the bar.
    }

    /// <summary>
    /// Draws the primary toolbar rows (icon and/or label, dimmed when disabled) and the overflow
    /// affordance when the page has secondary items. Shared by the navigation bar and the shell
    /// title bar so both bars express the same ToolbarItem contract.
    /// </summary>
    private void DrawToolbarItems(MauiCanvas canvas, RectF frame, Color textColor)
    {
        int index = 0;
        foreach (OpenHarmonyToolbarItem item in ToolbarItems)
        {
            if (item.IsSecondary)
            {
                continue;
            }
            DrawToolbarItem(canvas, item, ToolbarItemRect(index), textColor);
            index++;
        }
        if (!HasToolbarOverflow)
        {
            return;
        }
        // "More": three stacked dots, drawn vectorially so no glyph/font is required.
        RectF more = ToolbarMoreRect();
        canvas.FillColor = textColor;
        float cx = more.Center.X;
        float cy = more.Center.Y;
        for (int i = -1; i <= 1; i++)
        {
            canvas.FillCircle(cx, cy + i * 9f, 3f);
        }
    }

    /// <summary>Draws one primary item: icon and/or label, dimmed when disabled.</summary>
    private void DrawToolbarItem(MauiCanvas canvas, OpenHarmonyToolbarItem item, RectF rect, Color textColor)
    {
        bool hasText = !string.IsNullOrEmpty(item.Text);
        bool hasGlyph = !string.IsNullOrEmpty(item.Glyph);
        bool hasImage = item.IconBytes is { Length: > 0 };
        if (!hasText && !hasGlyph && !hasImage)
        {
            return;
        }
        float savedAlpha = canvas.Alpha;
        if (!item.IsEnabled)
        {
            canvas.Alpha = savedAlpha * 0.5f;
        }
        bool iconThenText = hasText && (hasGlyph || hasImage);
        float iconWidth = iconThenText ? Math.Min(32f, rect.Width) : 0f;
        if (hasImage)
        {
            float side = Math.Min(28f, Math.Max(8f, rect.Height - 16f));
            float iconX = iconThenText
                ? (FlowRightToLeft ? rect.Right - iconWidth : rect.X) + (iconWidth - side) / 2f
                : rect.X + (rect.Width - side) / 2f;
            Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas.DrawImageBytes(
                item.IconBytes!, (int)iconX, (int)(rect.Y + (rect.Height - side) / 2f), (int)side, (int)side);
        }
        else if (hasGlyph)
        {
            canvas.FontColor = item.GlyphColor;
            canvas.FontSize = item.GlyphFontSize > 0 ? item.GlyphFontSize : OpenHarmonyFontManager.ScaleFontSize(26);
            float glyphX = iconThenText
                ? (FlowRightToLeft ? rect.Right - iconWidth : rect.X)
                : rect.X;
            canvas.DrawString(item.Glyph!, glyphX, rect.Y, iconThenText ? iconWidth : rect.Width, rect.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        if (hasText)
        {
            float textX = iconThenText && !FlowRightToLeft ? rect.X + iconWidth : rect.X;
            float textWidth = iconThenText ? rect.Width - iconWidth : rect.Width;
            canvas.FontColor = textColor;
            canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(26);
            canvas.DrawString(item.Text, textX, rect.Y, Math.Max(8f, textWidth), rect.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        canvas.Alpha = savedAlpha;
    }

    /// <summary>
    /// Draws the open overflow dropdown (the secondary rows). The renderer calls this after the
    /// tree walk so the rows float above the page content.
    /// </summary>
    public void DrawToolbarOverflow(MauiCanvas canvas)
    {
        if (!ToolbarOverflowOpen)
        {
            return;
        }
        int rows = 0;
        foreach (OpenHarmonyToolbarItem item in ToolbarItems)
        {
            if (!item.IsSecondary)
            {
                continue;
            }
            RectF row = ToolbarOverflowRect(rows++);
            canvas.FillColor = Colors.Black;
            canvas.FillRectangle(row.X, row.Y, row.Width, row.Height);
            canvas.StrokeColor = Colors.Gray;
            canvas.StrokeSize = 1;
            canvas.DrawRectangle(row.X, row.Y, row.Width, row.Height);
            canvas.FontColor = item.IsEnabled ? Colors.White : Colors.Gray;
            canvas.FontSize = OpenHarmonyFontManager.ScaleFontSize(26);
            canvas.DrawString(item.Text ?? string.Empty, row.X + 16, row.Y, row.Width - 32, row.Height,
                FlowRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        if (rows == 0)
        {
            // The items went away while the dropdown was open.
            ToolbarOverflowOpen = false;
        }
    }

    private void DrawCheckBox(MauiCanvas canvas, RectF frame)
    {
        float side = Math.Min(frame.Width, frame.Height) * 0.7f;
        float x = frame.X + (frame.Width - side) / 2f;
        float y = frame.Y + (frame.Height - side) / 2f;
        canvas.StrokeColor = CheckBoxColor;
        canvas.StrokeSize = 2;
        canvas.DrawRoundedRectangle(x, y, side, side, 4);
        // The check mark draws on with the animated progress: the first leg fills 0..0.5, the
        // second 0.5..1, and progress 1 reproduces the previous static two lines exactly.
        float progress = CheckProgress;
        if (progress > 0.01f)
        {
            float p1x = x + side * 0.25f;
            float p1y = y + side * 0.55f;
            float mx = x + side * 0.45f;
            float my = y + side * 0.75f;
            float p2x = x + side * 0.78f;
            float p2y = y + side * 0.28f;
            float first = Math.Min(1f, progress * 2f);
            float second = Math.Max(0f, progress * 2f - 1f);
            canvas.StrokeColor = CheckBoxColor;
            canvas.StrokeSize = 3;
            if (first > 0f)
            {
                canvas.DrawLine(p1x, p1y, p1x + (mx - p1x) * first, p1y + (my - p1y) * first);
            }
            if (second > 0f)
            {
                canvas.DrawLine(mx, my, mx + (p2x - mx) * second, my + (p2y - my) * second);
            }
        }
    }

    private void DrawSwitch(MauiCanvas canvas, RectF frame)
    {
        float height = Math.Min(frame.Height, 28f);
        float width = Math.Min(frame.Width, height * 1.9f);
        float x = frame.X + (frame.Width - width) / 2f;
        float y = frame.Y + (frame.Height - height) / 2f;
        float radius = height / 2f;
        float progress = SwitchProgress;
        // Track colour: fill the off colour, then the on colour as an alpha overlay following
        // the progress. No per-frame allocation, and progress 1 reproduces the on colour exactly.
        canvas.FillColor = SwitchTrackColor;
        canvas.FillRoundedRectangle(x, y, width, height, radius);
        if (progress > 0.001f)
        {
            float savedAlpha = canvas.Alpha;
            canvas.Alpha = savedAlpha * progress;
            canvas.FillColor = SliderMinimumTrackColor;
            canvas.FillRoundedRectangle(x, y, width, height, radius);
            canvas.Alpha = savedAlpha;
        }
        float thumbRadius = radius - 3f;
        // Off is the start edge of the track: physical left in LTR, right in RTL.
        float thumbX = FlowRightToLeft
            ? x + width - radius - (width - radius * 2f) * progress
            : x + radius + (width - radius * 2f) * progress;
        canvas.FillColor = SwitchThumbColor;
        canvas.FillCircle(thumbX, y + radius, thumbRadius);
    }

    private void DrawSlider(MauiCanvas canvas, RectF frame)
    {
        float thickness = 6f;
        float cy = frame.Y + frame.Height / 2f;
        float left = frame.X + 10f;
        float right = frame.X + frame.Width - 10f;
        double span = SliderMaximum - SliderMinimum;
        float fraction = span > 0 ? (float)Math.Clamp((SliderValue - SliderMinimum) / span, 0, 1) : 0f;
        SliderFraction = fraction;
        // The value grows from the start edge: left in LTR, right in RTL (matching SliderValueFromX).
        float thumbX = FlowRightToLeft
            ? right - (right - left) * fraction
            : left + (right - left) * fraction;
        canvas.FillColor = SliderMaximumTrackColor;
        canvas.FillRoundedRectangle(left, cy - thickness / 2f, right - left, thickness, thickness / 2f);
        canvas.FillColor = SliderMinimumTrackColor;
        float completed = Math.Max(0f, FlowRightToLeft ? right - thumbX : thumbX - left);
        canvas.FillRoundedRectangle(FlowRightToLeft ? thumbX : left, cy - thickness / 2f, completed, thickness, thickness / 2f);
        canvas.FillColor = SliderThumbColor;
        canvas.FillCircle(thumbX, cy, 11f);
    }

    private void DrawProgress(MauiCanvas canvas, RectF frame)
    {
        float thickness = Math.Min(frame.Height, 8f);
        float y = frame.Y + (frame.Height - thickness) / 2f;
        canvas.FillColor = Colors.DimGray;
        canvas.FillRoundedRectangle(frame.X, y, frame.Width, thickness, thickness / 2f);
        float filled = (float)(frame.Width * Math.Clamp(Progress, 0, 1));
        if (filled > 0)
        {
            // The filled portion grows from the start edge: left in LTR, right in RTL.
            canvas.FillColor = ProgressColor;
            canvas.FillRoundedRectangle(FlowRightToLeft ? frame.Right - filled : frame.X, y, filled, thickness, thickness / 2f);
        }
    }

    private void DrawActivityIndicator(MauiCanvas canvas, RectF frame)
    {
        float radius = Math.Min(frame.Width, frame.Height) / 2f - 2f;
        float cx = frame.X + frame.Width / 2f;
        float cy = frame.Y + frame.Height / 2f;
        canvas.StrokeColor = IndicatorColor;
        canvas.StrokeSize = 3;
        // A 90-degree arc rotating with the renderer's shared angle: motion without a timer.
        canvas.DrawArc(cx - radius, cy - radius, radius * 2, radius * 2, AnimationAngle, AnimationAngle + 90, false, false);
    }

    public bool HitTest(float x, float y)
    {
        RectF frame = CanvasFrame;
        return x >= frame.X && x <= frame.X + frame.Width && y >= frame.Y && y <= frame.Y + frame.Height;
    }

    /// <summary>Updates the slider value from a touch x inside the view (returns the fraction).</summary>
    public float SliderValueFromX(float x)
    {
        RectF frame = CanvasFrame;
        float left = frame.X + 10f;
        float right = frame.X + frame.Width - 10f;
        float physical = right > left ? Math.Clamp((x - left) / (right - left), 0f, 1f) : 0f;
        // The physical fraction runs left-to-right; an RTL slider's value grows right-to-left.
        float fraction = FlowRightToLeft ? 1f - physical : physical;
        SliderFraction = fraction;
        return fraction;
    }

    /// <summary>Routes a tap through this view and its children (children first).</summary>
    public bool OnTouch(bool down, bool up, float x, float y)
    {
        bool handled = false;
        foreach (OpenHarmonyView child in Children)
        {
            handled |= child.OnTouch(down, up, x, y);
        }
        if (IsTabbedPage)
        {
            int tab = TabIndexAt(x, y);
            if (down && tab >= 0)
            {
                Pressed = true;
                return true;
            }
            // A shell's title-bar press shares Pressed but belongs to the toolbar block below.
            if (up && Pressed && !_toolbarPressed)
            {
                Pressed = false;
                if (tab >= 0)
                {
                    TabSelected?.Invoke(tab);
                }
                return true;
            }
        }
        if (IsPicker)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    Tap?.Invoke();
                }
                return true;
            }
        }
        if (IsStepper)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    // Decrement is the start half: left in LTR, right in RTL (matching DrawStepper).
                    bool increment = FlowRightToLeft
                        ? x < Frame.X + Frame.Width / 2
                        : x >= Frame.X + Frame.Width / 2;
                    StepperStep?.Invoke(increment);
                }
                return true;
            }
        }
        if (IsRadioButton)
        {
            if (down && HitTest(x, y))
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                if (HitTest(x, y))
                {
                    Tap?.Invoke();
                }
                return true;
            }
        }
        if (ShowsToolbar)
        {
            // The bar's own affordances (back, primary items, the overflow button) win over the
            // page content, like a native toolbar. The shell bar shares the item handling; its
            // back/hamburger slots are the renderer's chrome block.
            bool inBar = y >= CanvasFrame.Y && y <= CanvasFrame.Y + ToolbarBarHeight;
            int toolbarIndex = inBar ? ToolbarItemAt(x, y) : -1;
            bool inMore = inBar && InToolbarMore(x, y);
            bool inBack = IsNavigationPage && CanGoBack && InBackRegion(x, y);
            if (down && (inBack || toolbarIndex >= 0 || inMore))
            {
                if (inMore)
                {
                    // The affordance toggles the dropdown (the renderer consumes an open
                    // dropdown's taps, so closing here also works when no frame has run yet).
                    SetToolbarOverflow(!ToolbarOverflowOpen);
                    return true;
                }
                _toolbarPressed = true;
                Pressed = true;
                return true;
            }
            if (up && _toolbarPressed)
            {
                _toolbarPressed = false;
                Pressed = false;
                if (toolbarIndex >= 0)
                {
                    ActivateToolbarItem(toolbarIndex);
                }
                else if (inBack)
                {
                    BackTapped?.Invoke();
                }
                return true;
            }
        }
        if (IsSwipeView && IsSwipeOpen)
        {
            if (down && SwipeItemAt(x, y) >= 0)
            {
                Pressed = true;
                return true;
            }
            if (up && Pressed)
            {
                Pressed = false;
                int swipeIndex = SwipeItemAt(x, y);
                if (swipeIndex >= 0)
                {
                    (string Text, Color Background, Action Activate) item = SwipeItems[swipeIndex];
                    SetSwipeOpen(false);
                    item.Activate();
                }
                return true;
            }
        }
        if (IsTextEntry && ClearButtonVisible)
        {
            // The clear button owns its tap: down arms it, up on the same spot clears the text
            // (through the Entry handler's ClearText binding), up anywhere else cancels. This
            // runs before the entry's own Tap (focus) so a clear never re-requests the keyboard.
            if (down && InClearButton(x, y))
            {
                _clearPressed = true;
                return true;
            }
            if (up && _clearPressed)
            {
                _clearPressed = false;
                if (InClearButton(x, y))
                {
                    ClearText?.Invoke();
                }
                return true;
            }
        }
        if (Tap is null)
        {
            return handled || Pressed;
        }
        if (down && HitTest(x, y))
        {
            Pressed = true;
            return true;
        }
        if (up && Pressed)
        {
            Pressed = false;
            if (HitTest(x, y))
            {
                Tap();
            }
            return true;
        }
        return handled || Pressed;
    }
}

/// <summary>
/// MAUI maps <see cref="IView.Shadow"/> through the shared ViewMapper; the slice has no other
/// sink for it. This replaces the entry once so a shadow change requests a frame. The drawing
/// itself happens in <see cref="OpenHarmonyWindowRenderer"/> through
/// <see cref="OpenHarmonyView.DrawShadow"/>, which reads the virtual view's shadow directly.
/// </summary>
internal static class OpenHarmonyShadow
{
    private static bool s_installed;

    /// <summary>Installs the redraw hook (idempotent; called by the renderer's constructor).</summary>
    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        if (ViewHandler.ViewMapper is PropertyMapper<IView, IViewHandler> mapper)
        {
            Action<IViewHandler, IView>? previous = mapper[nameof(IView.Shadow)];
            mapper[nameof(IView.Shadow)] = (handler, view) =>
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
                    Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
                }
                catch (Exception)
                {
                    // No host: the next input/frame event repaints anyway.
                }
            };
        }
    }
}
