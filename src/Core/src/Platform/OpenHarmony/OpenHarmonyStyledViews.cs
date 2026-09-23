// Styled platform views for the OpenHarmony compositor.
//
// The base OpenHarmonyView models plain single-line text (left aligned, one size, solid colour)
// and solid-colour fills. The label/button handlers need the rest of MAUI's text contract
// (font weight/slant, character spacing, line height, alignment, decorations, max lines, line
// break mode, padding) and the shape/border handlers need gradient and image paints, which the
// shared canvas backend already supports (OpenHarmonyCanvas.SetFillPaint ->
// SetLinearGradient/SetRadialGradient/SetImagePattern). These subclasses keep that extra state on
// the platform view and draw it with the same DrawString/MeasureText/DrawPath primitives the base
// view and the renderer use, so the compositor's per-frame path is unchanged. Everything the
// canvas cannot render (italic typefaces, gradient strokes, procedural pattern paths) is reported
// once through the status channel instead of being silently dropped.
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

/// <summary>Shared one-time status reporting for the platform views' honest degradations.</summary>
internal static class OpenHarmonyStatus
{
    private static readonly object s_sync = new();
    private static readonly HashSet<string> s_logged = new(StringComparer.Ordinal);

    /// <summary>Writes a status line once per key (the slice's off-device visible log channel).</summary>
    public static void Once(string key, string message)
    {
        lock (s_sync)
        {
            if (!s_logged.Add(key))
            {
                return;
            }
        }
        Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus("[maui] " + message);
    }

    /// <summary>
    /// Reports an exception that was caught at a native -> managed callback boundary. An
    /// exception escaping such a callback unwinds into the host's native frame, where CoreCLR
    /// treats it as fatal (the a11y action callback's contract), so every boundary that can run
    /// application code catches and reports instead. Exception text can carry page- or
    /// native-controlled characters and is often attacker-influenced (a URL, a JSON snippet),
    /// so the line is flattened to one status line and capped; it is logged once per
    /// boundary/exception-type so a page that keeps triggering an app failure cannot flood
    /// dotnet-status.txt.
    /// </summary>
    public static void NativeCallbackFailed(string boundary, Exception error)
    {
        Once(
            "native-callback:" + boundary + ":" + error.GetType().Name,
            $"{boundary} callback failed: {error.GetType().Name}: {Flatten(error.Message)}");
    }

    /// <summary>One log-safe line: control characters become spaces and the text is capped (B7-style).</summary>
    private static string Flatten(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        const int MaxMessageLength = 600;
        int length = Math.Min(value.Length, MaxMessageLength);
        char[]? flattened = null;
        for (int i = 0; i < length; i++)
        {
            if (char.IsControl(value[i]))
            {
                flattened ??= value.ToCharArray();
                flattened[i] = ' ';
            }
        }
        string result = flattened is null ? value.Substring(0, length) : new string(flattened, 0, length);
        return value.Length > MaxMessageLength ? result + "..." : result;
    }
}

/// <summary>
/// Paint-aware fills and strokes. The canvas renders solid, linear/radial gradient and image
/// paints through <c>SetFillPaint</c>; a stroke has no paint API at all, so non-solid strokes are
/// approximated with their first stop colour and reported once.
/// </summary>
internal static class OpenHarmonyPaintRenderer
{
    /// <summary>
    /// Fills a path (or a rounded/plain rectangle when <paramref name="path"/> is null) with the
    /// paint. Returns false when the paint cannot be rendered (already reported).
    /// </summary>
    public static bool Fill(MauiCanvas canvas, Paint? paint, RectF bounds, PathF? path, float cornerRadius)
    {
        switch (paint)
        {
            case null:
                return false;
            case SolidPaint { Color: { } color }:
                canvas.FillColor = color;
                FillShape(canvas, path, bounds, cornerRadius);
                return true;
            case LinearGradientPaint or RadialGradientPaint or ImagePaint:
                canvas.SetFillPaint(paint, bounds);
                FillShape(canvas, path, bounds, cornerRadius);
                return true;
            case PatternPaint { Pattern: PaintPattern wrapper }:
                // A paint wrapped in a pattern is the same paint for our purposes.
                return Fill(canvas, wrapper.Paint, bounds, path, cornerRadius);
            case PatternPaint:
                if (path is null && cornerRadius <= 0)
                {
                    // The backend tiles procedural patterns on plain rectangles.
                    canvas.SetFillPaint(paint, bounds);
                    canvas.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    return true;
                }
                OpenHarmonyStatus.Once("paint.pattern.path",
                    "a procedural pattern paint fills a path or rounded rectangle: the OpenHarmony canvas " +
                    "tiles patterns on plain rectangles only, so this fill was skipped");
                return false;
            default:
                OpenHarmonyStatus.Once("paint.fill." + paint.GetType().Name,
                    $"paint {paint.GetType().Name} cannot be filled by the OpenHarmony canvas; the fill was skipped");
                return false;
        }
    }

    /// <summary>Strokes a path with the paint (solid exactly, gradients by their first colour).</summary>
    public static bool Stroke(MauiCanvas canvas, Paint? paint, PathF path, float thickness)
    {
        if (paint is null || thickness <= 0)
        {
            return false;
        }
        if (paint is SolidPaint { Color: { } solid })
        {
            if (solid.Alpha <= 0)
            {
                return false;
            }
            canvas.StrokeColor = solid;
            canvas.StrokeSize = thickness;
            canvas.DrawPath(path);
            return true;
        }
        Color? approximation = ApproximateColor(paint);
        if (approximation is null)
        {
            OpenHarmonyStatus.Once("paint.stroke." + paint.GetType().Name,
                $"paint {paint.GetType().Name} cannot be stroked by the OpenHarmony canvas (solid strokes only); " +
                "the stroke was skipped");
            return false;
        }
        OpenHarmonyStatus.Once("paint.stroke." + paint.GetType().Name,
            $"{paint.GetType().Name} strokes are drawn with the first stop colour: the OpenHarmony canvas " +
            "strokes solid colours only");
        canvas.StrokeColor = approximation;
        canvas.StrokeSize = thickness;
        canvas.DrawPath(path);
        return true;
    }

    /// <summary>First stop colour of a gradient (used as the documented stroke approximation).</summary>
    public static Color? ApproximateColor(Paint paint)
    {
        if (paint is GradientPaint gradient)
        {
            if (gradient.GradientStops is { Length: > 0 } stops)
            {
                Color? first = null;
                float offset = float.MaxValue;
                foreach (PaintGradientStop stop in stops)
                {
                    if (stop.Offset < offset)
                    {
                        offset = stop.Offset;
                        first = stop.Color;
                    }
                }
                if (first is not null)
                {
                    return first;
                }
            }
            return gradient.StartColor ?? gradient.EndColor;
        }
        if (paint is SolidPaint { Color: { } color })
        {
            return color;
        }
        return null;
    }

    private static void FillShape(MauiCanvas canvas, PathF? path, RectF bounds, float cornerRadius)
    {
        if (path is not null)
        {
            canvas.FillPath(path);
        }
        else if (cornerRadius > 0)
        {
            canvas.FillRoundedRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, cornerRadius);
        }
        else
        {
            canvas.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }
}

/// <summary>Shared mapping helpers for the Label/Button text handlers.</summary>
internal static class OpenHarmonyTextMapping
{
    /// <summary>
    /// Resolves the font family through the platform font manager, which applies the typeface file
    /// to the native text layer (the same facility <see cref="OpenHarmonyFontManager"/> uses).
    /// </summary>
    public static Font Resolve(IElementHandler handler, Font font)
    {
        try
        {
            if (handler.MauiContext?.Services.GetService(typeof(IFontManager)) is OpenHarmonyFontManager fonts)
            {
                return fonts.GetFont(font);
            }
        }
        catch (Exception)
        {
            // Font resolution must never take a frame down; the requested font is kept as-is.
        }
        return font;
    }

    /// <summary>
    /// Clamps a MAUI padding to real numbers: a NaN component means "platform default" (Button's
    /// Padding default value creator returns NaN) and must never reach the layout arithmetic.
    /// </summary>
    public static Thickness NormalizePadding(Thickness padding, Thickness fallback)
    {
        static double Value(double value, double fallbackValue) => double.IsNaN(value) ? fallbackValue : value;
        return new Thickness(
            Value(padding.Left, fallback.Left),
            Value(padding.Top, fallback.Top),
            Value(padding.Right, fallback.Right),
            Value(padding.Bottom, fallback.Bottom));
    }
}

/// <summary>
/// Platform view for Label/Button: carries the text attributes the base view does not model and
/// lays the text out itself (hard breaks, optional wrapping/truncation, alignment, decorations,
/// spacing, line height, max lines, padding). A mapped background paint replaces the solid
/// background colour. The layout is cached per text/width/font, so steady-state frames only draw.
/// </summary>
public class OpenHarmonyTextView : OpenHarmonyView
{
    private readonly Dictionary<TextLayoutKey, string[]> _layouts = new();

    /// <summary>Resolved MAUI font: family is applied by the font manager, weight/slant drive the text path.</summary>
    public Font TextFont { get; set; } = Font.Default;

    /// <summary>Spacing between characters, in device-independent units.</summary>
    public double CharacterSpacing { get; set; }

    /// <summary>Line height multiplier (-1 keeps the platform default line height).</summary>
    public double LineHeight { get; set; } = -1;

    public TextAlignment HorizontalTextAlignment { get; set; } = TextAlignment.Start;

    public TextAlignment VerticalTextAlignment { get; set; } = TextAlignment.Center;

    public TextDecorations TextDecorations { get; set; }

    public LineBreakMode LineBreakMode { get; set; } = LineBreakMode.WordWrap;

    /// <summary>Maximum number of laid-out lines (-1/0 mean unlimited).</summary>
    public int MaxLines { get; set; } = -1;

    public Thickness Padding { get; set; }

    /// <summary>Background paint mapped by a handler (gradient/image); overrides the solid Background.</summary>
    public Paint? BackgroundPaint { get; set; }

    public override void Draw(MauiCanvas canvas)
    {
        RectF frame = Frame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        Paint? backgroundPaint = BackgroundPaint;
        if (backgroundPaint is not null)
        {
            float alpha = canvas.Alpha;
            if (Pressed || Dimmed)
            {
                canvas.Alpha = alpha * 0.5f;
            }
            OpenHarmonyPaintRenderer.Fill(canvas, backgroundPaint, frame, null, CornerRadius);
            canvas.Alpha = alpha;
        }
        // The base handles the solid background, the outline and the image; the text is drawn here
        // so the attributes above apply. Suppress the base's own text draw for this one call.
        string? text = Text;
        Color? background = Background;
        bool painted = backgroundPaint is not null;
        if (painted)
        {
            Background = null;
        }
        Text = null;
        try
        {
            base.Draw(canvas);
        }
        finally
        {
            Text = text;
            if (painted)
            {
                Background = background;
            }
        }
        if (!IsTextEntry && !string.IsNullOrEmpty(text))
        {
            DrawTextBlock(canvas, text);
        }
    }

    /// <summary>Desired size of the text block at the given width (padding included).</summary>
    public (float Width, float Height) MeasureTextBlock(string? text, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0f, 0f);
        }
        string[] lines = GetLayout(text, fontSize, maxWidth);
        float width = 0f;
        foreach (string line in lines)
        {
            width = Math.Max(width, MeasureLine(line, fontSize));
        }
        return (width, lines.Length * EffectiveLineHeight(fontSize));
    }

    private void DrawTextBlock(MauiCanvas canvas, string text)
    {
        RectF frame = Frame;
        float left = frame.X + (float)Padding.Left;
        float top = frame.Y + (float)Padding.Top;
        float width = Math.Max(0f, frame.Width - (float)(Padding.Left + Padding.Right));
        float height = Math.Max(0f, frame.Height - (float)(Padding.Top + Padding.Bottom));
        if (width <= 0 || height <= 0)
        {
            return;
        }
        float fontSize = TextFont.Size is > 0 and < float.MaxValue ? (float)TextFont.Size : FontSize;
        float maxWidth = LineBreakMode == LineBreakMode.NoWrap ? float.PositiveInfinity : width;
        string[] lines = GetLayout(text, fontSize, maxWidth);
        float lineHeight = EffectiveLineHeight(fontSize);
        float blockHeight = lines.Length * lineHeight;
        float y = VerticalTextAlignment switch
        {
            TextAlignment.Center => top + (height - blockHeight) / 2f,
            TextAlignment.End => top + height - blockHeight,
            _ => top,
        };
        canvas.FontColor = Dimmed ? TextColor.WithAlpha(0.5f) : TextColor;
        canvas.FontSize = fontSize;
        bool bold = IsBold(TextFont);
        if (IsItalic(TextFont))
        {
            OpenHarmonyStatus.Once("text.italic",
                "italic text is drawn upright: the OpenHarmony text bridge exposes no typeface slant");
        }
        foreach (string line in lines)
        {
            float lineWidth = MeasureLine(line, fontSize);
            float x = HorizontalTextAlignment switch
            {
                TextAlignment.Center => left + (width - lineWidth) / 2f,
                TextAlignment.End => left + width - lineWidth,
                _ => left,
            };
            DrawLine(canvas, line, x, y, lineHeight, fontSize, bold);
            DrawDecorations(canvas, lineWidth, x, y, lineHeight, fontSize);
            y += lineHeight;
        }
    }

    private void DrawLine(MauiCanvas canvas, string line, float x, float y, float lineHeight, float fontSize, bool bold)
    {
        if (CharacterSpacing == 0)
        {
            canvas.DrawString(line, x, y, Math.Max(1f, MeasureLine(line, fontSize)), lineHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);
            if (bold)
            {
                canvas.DrawString(line, x + BoldOffset(fontSize), y, Math.Max(1f, MeasureLine(line, fontSize)), lineHeight,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            return;
        }
        // Character spacing is emulated by drawing one glyph at a time, so each glyph needs its
        // own advance: MeasureLine(glyph) adds the glyph's own width, and the per-glyph widths
        // are served by the shared (text, size) measurement cache after the first frame. The
        // glyph strings themselves are cached per character so a per-frame line does not allocate
        // one string per character.
        float cx = x;
        foreach (char character in line)
        {
            string glyph = Glyph(character);
            float glyphWidth = MeasureLine(glyph, fontSize);
            canvas.DrawString(glyph, cx, y, Math.Max(1f, glyphWidth), lineHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);
            if (bold)
            {
                canvas.DrawString(glyph, cx + BoldOffset(fontSize), y, Math.Max(1f, glyphWidth), lineHeight,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            cx += glyphWidth + (float)CharacterSpacing;
        }
    }

    private Dictionary<char, string>? _glyphs;

    /// <summary>The one-character string for a glyph, reused across frames (bounded cache).</summary>
    private string Glyph(char character)
    {
        Dictionary<char, string> glyphs = _glyphs ??= new Dictionary<char, string>();
        if (glyphs.TryGetValue(character, out string? glyph))
        {
            return glyph;
        }
        if (glyphs.Count >= 256)
        {
            glyphs.Clear();
        }
        glyph = character.ToString();
        glyphs[character] = glyph;
        return glyph;
    }

    private void DrawDecorations(MauiCanvas canvas, float lineWidth, float x, float y, float lineHeight, float fontSize)
    {
        TextDecorations decorations = TextDecorations;
        if (decorations == TextDecorations.None)
        {
            return;
        }
        Color stroke = canvas.StrokeColor;
        float strokeSize = canvas.StrokeSize;
        canvas.StrokeColor = Dimmed ? TextColor.WithAlpha(0.5f) : TextColor;
        canvas.StrokeSize = Math.Max(1f, fontSize / 14f);
        float extent = Math.Max(1f, lineWidth);
        if ((decorations & TextDecorations.Underline) != TextDecorations.None)
        {
            float underline = y + lineHeight / 2f + fontSize * 0.42f;
            canvas.DrawLine(x, underline, x + extent, underline);
        }
        if ((decorations & TextDecorations.Strikethrough) != TextDecorations.None)
        {
            float middle = y + lineHeight / 2f;
            canvas.DrawLine(x, middle, x + extent, middle);
        }
        canvas.StrokeColor = stroke;
        canvas.StrokeSize = strokeSize;
    }

    private float _naturalLineHeight;
    private float _naturalLineHeightFontSize = -1;

    private float EffectiveLineHeight(float fontSize)
    {
        if (Math.Abs(_naturalLineHeightFontSize - fontSize) > 0.001f)
        {
            float natural = OpenHarmonyLabelHandler.MeasureText("M", fontSize).Height;
            if (natural <= 0)
            {
                natural = fontSize * 1.35f;
            }
            _naturalLineHeight = natural;
            _naturalLineHeightFontSize = fontSize;
        }
        return LineHeight > 0 ? (float)LineHeight * _naturalLineHeight : _naturalLineHeight;
    }

    private float MeasureLine(string line, float fontSize)
        => OpenHarmonyLabelHandler.MeasureText(line, fontSize).Width
            + (float)(Math.Max(0, line.Length - 1) * CharacterSpacing);

    private static float BoldOffset(float fontSize) => Math.Max(0.6f, fontSize / 18f);

    private static bool IsBold(Font font) => (int)font.Weight >= (int)FontWeight.Semibold;

    private static bool IsItalic(Font font) => font.Slant != FontSlant.Default;

    // ---------------------------------------------------------------- layout

    private string[] GetLayout(string text, float fontSize, float maxWidth)
    {
        var key = new TextLayoutKey(text, fontSize, maxWidth, LineBreakMode, MaxLines, CharacterSpacing, LineHeight);
        if (_layouts.TryGetValue(key, out string[]? cached))
        {
            return cached;
        }
        // Measure and arrange pass different widths; keep a handful of variants per view (a label
        // is laid out with its measure constraint and its arranged width, and both repeat).
        if (_layouts.Count >= 8)
        {
            _layouts.Clear();
        }
        string[] lines = BuildLines(text, fontSize, maxWidth);
        _layouts[key] = lines;
        return lines;
    }

    private string[] BuildLines(string text, float fontSize, float maxWidth)
    {
        var lines = new List<string>();
        foreach (string rawParagraph in text.Split('\n'))
        {
            string paragraph = rawParagraph.TrimEnd('\r');
            switch (LineBreakMode)
            {
                case LineBreakMode.NoWrap:
                    lines.Add(paragraph);
                    break;
                case LineBreakMode.HeadTruncation:
                case LineBreakMode.MiddleTruncation:
                case LineBreakMode.TailTruncation:
                    lines.Add(Truncate(paragraph, fontSize, maxWidth));
                    break;
                case LineBreakMode.CharacterWrap:
                    WrapCharacters(paragraph, fontSize, maxWidth, lines);
                    break;
                default:
                    WrapWords(paragraph, fontSize, maxWidth, lines);
                    break;
            }
        }
        if (MaxLines > 0 && lines.Count > MaxLines)
        {
            lines.RemoveRange(MaxLines, lines.Count - MaxLines);
            lines[^1] = Truncate(lines[^1] + "…", fontSize, maxWidth);
        }
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }
        return lines.ToArray();
    }

    private void WrapWords(string paragraph, float fontSize, float maxWidth, List<string> lines)
    {
        if (paragraph.Length == 0)
        {
            lines.Add(string.Empty);
            return;
        }
        string line = string.Empty;
        foreach (string word in paragraph.Split(' '))
        {
            if (line.Length == 0)
            {
                if (Fits(word, fontSize, maxWidth))
                {
                    line = word;
                }
                else
                {
                    // A single word wider than the line: split it by characters.
                    WrapCharacters(word, fontSize, maxWidth, lines);
                }
                continue;
            }
            string candidate = line + " " + word;
            if (Fits(candidate, fontSize, maxWidth))
            {
                line = candidate;
                continue;
            }
            lines.Add(line);
            if (Fits(word, fontSize, maxWidth))
            {
                line = word;
            }
            else
            {
                WrapCharacters(word, fontSize, maxWidth, lines);
                line = string.Empty;
            }
        }
        if (line.Length > 0 || lines.Count == 0)
        {
            lines.Add(line);
        }
    }

    private void WrapCharacters(string text, float fontSize, float maxWidth, List<string> lines)
    {
        if (text.Length == 0)
        {
            lines.Add(string.Empty);
            return;
        }
        int start = 0;
        while (start < text.Length)
        {
            int count = LongestPrefix(text, start, fontSize, maxWidth);
            if (count <= 0)
            {
                count = 1;
            }
            lines.Add(text.Substring(start, count));
            start += count;
        }
    }

    private int LongestPrefix(string text, int start, float fontSize, float maxWidth)
    {
        int low = 1;
        int high = text.Length - start;
        int best = 0;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (Fits(text.Substring(start, middle), fontSize, maxWidth))
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return best;
    }

    /// <summary>Trims a paragraph to the line width, honouring the head/middle/tail break mode.</summary>
    private string Truncate(string text, float fontSize, float maxWidth)
    {
        if (float.IsInfinity(maxWidth) || maxWidth <= 0 || Fits(text, fontSize, maxWidth))
        {
            return text;
        }
        const string Ellipsis = "…";
        switch (LineBreakMode)
        {
            case LineBreakMode.HeadTruncation:
            {
                int start = 0;
                while (start < text.Length && !Fits(Ellipsis + text.Substring(start + 1), fontSize, maxWidth))
                {
                    start++;
                }
                return Ellipsis + text.Substring(Math.Min(start + 1, text.Length));
            }
            case LineBreakMode.MiddleTruncation:
            {
                int head = 0;
                int tail = 0;
                while (head + tail < text.Length - 1)
                {
                    bool growHead = head <= tail;
                    int nextHead = head + (growHead ? 1 : 0);
                    int nextTail = tail + (growHead ? 0 : 1);
                    if (nextHead + nextTail > text.Length - 1
                        || !Fits(text.Substring(0, nextHead) + Ellipsis + text.Substring(text.Length - nextTail),
                            fontSize, maxWidth))
                    {
                        break;
                    }
                    head = nextHead;
                    tail = nextTail;
                }
                return text.Substring(0, head) + Ellipsis + text.Substring(text.Length - tail);
            }
            default:
            {
                int count = text.Length;
                while (count > 0 && !Fits(text.Substring(0, count) + Ellipsis, fontSize, maxWidth))
                {
                    count--;
                }
                return count <= 0 ? Ellipsis : text.Substring(0, count) + Ellipsis;
            }
        }
    }

    private bool Fits(string text, float fontSize, float maxWidth)
        => float.IsInfinity(maxWidth) || maxWidth <= 0 || MeasureLine(text, fontSize) <= maxWidth;

    private readonly struct TextLayoutKey : IEquatable<TextLayoutKey>
    {
        private readonly string _text;
        private readonly float _fontSize;
        private readonly float _maxWidth;
        private readonly LineBreakMode _mode;
        private readonly int _maxLines;
        private readonly double _characterSpacing;
        private readonly double _lineHeight;

        public TextLayoutKey(string text, float fontSize, float maxWidth, LineBreakMode mode, int maxLines,
            double characterSpacing, double lineHeight)
        {
            _text = text;
            _fontSize = fontSize;
            _maxWidth = maxWidth;
            _mode = mode;
            _maxLines = maxLines;
            _characterSpacing = characterSpacing;
            _lineHeight = lineHeight;
        }

        public bool Equals(TextLayoutKey other)
            => string.Equals(_text, other._text, StringComparison.Ordinal)
                && _fontSize.Equals(other._fontSize)
                && _maxWidth.Equals(other._maxWidth)
                && _mode == other._mode
                && _maxLines == other._maxLines
                && _characterSpacing.Equals(other._characterSpacing)
                && _lineHeight.Equals(other._lineHeight);

        public override bool Equals(object? obj) => obj is TextLayoutKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_text, _fontSize, _maxWidth, _mode, _maxLines, _characterSpacing, _lineHeight);
    }
}

/// <summary>
/// Platform view for Shape/Border: keeps the MAUI <see cref="Paint"/> objects from the handlers and
/// fills/strokes them through the canvas paint facilities, so gradient/image fills render instead of
/// being dropped by the solid-only base path.
/// </summary>
public sealed class OpenHarmonyShapeView : OpenHarmonyView
{
    /// <summary>Stroke paint (solid, gradient or image) mapped by the shape/border handlers.</summary>
    public Paint? ShapeStrokePaint { get; set; }

    /// <summary>Background paint mapped from IView.Background when it is not a solid colour.</summary>
    public Paint? BackgroundPaint { get; set; }

    public override void Draw(MauiCanvas canvas)
    {
        RectF frame = Frame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        if (BackgroundPaint is not null)
        {
            OpenHarmonyPaintRenderer.Fill(canvas, BackgroundPaint, frame, null, CornerRadius);
        }
        else if (Background is not null)
        {
            canvas.FillColor = Pressed ? Colors.OrangeRed : (Dimmed ? Background.WithAlpha(0.5f) : Background);
            if (CornerRadius > 0)
            {
                canvas.FillRoundedRectangle(frame.X, frame.Y, frame.Width, frame.Height, CornerRadius);
            }
            else
            {
                canvas.FillRectangle(frame.X, frame.Y, frame.Width, frame.Height);
            }
        }
        // MAUI's Shape.PathForBounds returns geometry in the shape's own coordinate space (starting
        // near 0,0), so the canvas is translated to the view's frame - same as the base view.
        PathF? path = Shape?.PathForBounds(new RectF(0, 0, frame.Width, frame.Height));
        if (path is null)
        {
            return;
        }
        canvas.SaveState();
        canvas.Translate(frame.X, frame.Y);
        OpenHarmonyPaintRenderer.Fill(canvas, ShapeFill,
            new RectF(0, 0, frame.Width, frame.Height), path, 0);
        float thickness = IsBorder ? BorderStrokeThickness : ShapeStrokeThickness;
        Paint? stroke = ShapeStrokePaint ?? new SolidPaint(IsBorder ? BorderStroke : ShapeStroke);
        OpenHarmonyPaintRenderer.Stroke(canvas, stroke, path, thickness);
        canvas.RestoreState();
    }
}
