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
internal class OpenHarmonyTextView : OpenHarmonyView
{
    private readonly Dictionary<TextLayoutKey, string[]> _layouts = new();

    /// <summary>Resolved MAUI font: family is applied by the font manager, weight/slant drive the text path.</summary>
    public Font TextFont { get; set; } = Font.Default;

    /// <summary>Spacing between characters, in device-independent units.</summary>
    public double CharacterSpacing { get; set; }

    /// <summary>Line height multiplier (-1 keeps the platform default line height).</summary>
    public double LineHeight { get; set; } = -1;

    // The base view now models the entry alignment (T1); the text view keeps its own property
    // because its layout pipeline owns the value (mapped by the Label/Button handlers).
    public new TextAlignment HorizontalTextAlignment { get; set; } = TextAlignment.Start;

    public TextAlignment VerticalTextAlignment { get; set; } = TextAlignment.Center;

    public TextDecorations TextDecorations { get; set; }

    public LineBreakMode LineBreakMode { get; set; } = LineBreakMode.WordWrap;

    /// <summary>Maximum number of laid-out lines (-1/0 mean unlimited).</summary>
    public int MaxLines { get; set; } = -1;

    public Thickness Padding { get; set; }

    /// <summary>Background paint mapped by a handler (gradient/image); overrides the solid Background.</summary>
    public Paint? BackgroundPaint { get; set; }

    // ---- FormattedText (Span) state --------------------------------------------------------
    private OpenHarmonyTextRun[]? _formattedRuns;
    private int _formattedGeneration;
    private (int Generation, float MaxWidth, LineBreakMode Mode, int MaxLines, float DefaultFontSize,
        int FontScaleGeneration, OpenHarmonyFormattedTextLayout Layout)? _formattedLayout;

    /// <summary>True when the mapper handed this view a non-empty run set (formatted mode).</summary>
    internal bool HasFormattedRuns => _formattedRuns is { Length: > 0 };

    /// <summary>Installs the label's runs (null returns to the plain-text path) and drops the layout cache.</summary>
    internal void SetFormattedRuns(OpenHarmonyTextRun[]? runs)
    {
        _formattedRuns = runs is { Length: > 0 } ? runs : null;
        _formattedGeneration++;
        _formattedLayout = null;
    }

    /// <summary>Run-aware layout at the given width (cached per run set, break contract and font scale).</summary>
    internal OpenHarmonyFormattedTextLayout FormattedLayout(float maxWidth)
    {
        float defaultFontSize = TextFont.Size is > 0 and < float.MaxValue ? (float)TextFont.Size : FontSize;
        int fontScaleGeneration = OpenHarmonyFontManager.FontScaleGeneration;
        if (_formattedLayout is { } cached &&
            cached.Generation == _formattedGeneration &&
            cached.MaxWidth.Equals(maxWidth) &&
            cached.Mode == LineBreakMode &&
            cached.MaxLines == MaxLines &&
            cached.DefaultFontSize.Equals(defaultFontSize) &&
            cached.FontScaleGeneration == fontScaleGeneration)
        {
            return cached.Layout;
        }
        var layout = new OpenHarmonyFormattedTextLayout(_formattedRuns!, LineBreakMode, MaxLines, maxWidth,
            defaultFontSize);
        _formattedLayout = (_formattedGeneration, maxWidth, LineBreakMode, MaxLines, defaultFontSize,
            fontScaleGeneration, layout);
        return layout;
    }

    /// <summary>Mirrors <c>OpenHarmonyView.DimAlpha</c> for explicit run colours (the base dims only its own).</summary>
    private const float DisabledTextAlpha = 0.5f;

    public override void Draw(MauiCanvas canvas)
    {
        RectF frame = CanvasFrame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        Paint? backgroundPaint = BackgroundPaint;
        if (backgroundPaint is not null)
        {
            float alpha = canvas.Alpha;
            // Press feedback over a gradient/image fill: ease the same 50% dim in/out with the
            // press progress instead of snapping.
            float fade = Dimmed ? 0.5f : 0f;
            fade = Math.Min(1f, fade + 0.5f * PressProgress);
            canvas.Alpha = alpha * (1f - fade);
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
        if (!IsTextEntry)
        {
            if (_formattedRuns is { Length: > 0 })
            {
                DrawFormattedBlock(canvas);
            }
            else if (!string.IsNullOrEmpty(text))
            {
                DrawTextBlock(canvas, text);
            }
        }
    }

    /// <summary>Desired size of the text block at the given width (padding included).</summary>
    public (float Width, float Height) MeasureTextBlock(string? text, float fontSize, float maxWidth)
    {
        if (_formattedRuns is { Length: > 0 })
        {
            OpenHarmonyFormattedTextLayout formatted = FormattedLayout(maxWidth);
            return (formatted.Width, formatted.Height);
        }
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
        RectF frame = CanvasFrame;
        float left = frame.X + (float)Padding.Left;
        float top = frame.Y + (float)Padding.Top;
        float width = Math.Max(0f, frame.Width - (float)(Padding.Left + Padding.Right));
        float height = Math.Max(0f, frame.Height - (float)(Padding.Top + Padding.Bottom));
        if (width <= 0 || height <= 0)
        {
            return;
        }
        float fontSize = TextFont.Size is > 0 and < float.MaxValue ? (float)TextFont.Size : FontSize;
        float drawFontSize = OpenHarmonyFontManager.ScaleFontSize(fontSize);
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
        canvas.FontColor = TextColorForDraw;
        canvas.FontSize = drawFontSize;
        bool bold = IsBold(TextFont);
        if (IsItalic(TextFont))
        {
            OpenHarmonyStatus.Once("text.italic",
                "italic text is drawn upright: the OpenHarmony text bridge exposes no typeface slant");
        }
        foreach (string line in lines)
        {
            float lineWidth = MeasureLine(line, fontSize);
            float x = ResolveHorizontalTextAlignment(HorizontalTextAlignment) switch
            {
                TextAlignment.Center => left + (width - lineWidth) / 2f,
                TextAlignment.End => left + width - lineWidth,
                _ => left,
            };
            DrawLine(canvas, line, x, y, lineHeight, fontSize, drawFontSize, bold);
            DrawDecorations(canvas, TextDecorations, TextColorForDraw, lineWidth, x, y, lineHeight, drawFontSize);
            y += lineHeight;
        }
    }

    /// <summary>
    /// Draws the run-aware layout: each line places its styled segments at the alignment origin,
    /// and a segment carries its own colour, font size, weight, spacing, decorations and
    /// background (the layout already resolved the metrics).
    /// </summary>
    private void DrawFormattedBlock(MauiCanvas canvas)
    {
        RectF frame = CanvasFrame;
        float left = frame.X + (float)Padding.Left;
        float top = frame.Y + (float)Padding.Top;
        float width = Math.Max(0f, frame.Width - (float)(Padding.Left + Padding.Right));
        float height = Math.Max(0f, frame.Height - (float)(Padding.Top + Padding.Bottom));
        if (width <= 0 || height <= 0)
        {
            return;
        }
        float maxWidth = LineBreakMode == LineBreakMode.NoWrap ? float.PositiveInfinity : width;
        OpenHarmonyFormattedTextLayout layout = FormattedLayout(maxWidth);
        float y = VerticalTextAlignment switch
        {
            TextAlignment.Center => top + (height - layout.Height) / 2f,
            TextAlignment.End => top + height - layout.Height,
            _ => top,
        };
        foreach (OpenHarmonyFormattedLine line in layout.Lines)
        {
            float x = ResolveHorizontalTextAlignment(HorizontalTextAlignment) switch
            {
                TextAlignment.Center => left + (width - line.Width) / 2f,
                TextAlignment.End => left + width - line.Width,
                _ => left,
            };
            foreach (OpenHarmonyFormattedSegment segment in line.Segments)
            {
                DrawFormattedSegment(canvas, segment, x, y, line.Height);
                x += segment.Width;
            }
            y += line.Height;
        }
    }

    private void DrawFormattedSegment(MauiCanvas canvas, OpenHarmonyFormattedSegment segment, float x, float y,
        float lineHeight)
    {
        OpenHarmonyTextRun run = segment.Run;
        float fontSize = run.FontSize;
        float drawFontSize = OpenHarmonyFontManager.ScaleFontSize(fontSize);
        Color color = run.TextColor is { } runColor
            ? (Dimmed ? runColor.WithAlpha(DisabledTextAlpha) : runColor)
            : TextColorForDraw;
        if (run.BackgroundColor is { } background)
        {
            Color fill = canvas.FillColor;
            canvas.FillColor = background;
            canvas.FillRectangle(x, y, segment.Width, lineHeight);
            canvas.FillColor = fill;
        }
        if (run.IsItalic)
        {
            OpenHarmonyStatus.Once("text.italic",
                "italic text is drawn upright: the OpenHarmony text bridge exposes no typeface slant");
        }
        Color savedColor = canvas.FontColor;
        float savedSize = canvas.FontSize;
        canvas.FontColor = color;
        canvas.FontSize = drawFontSize;
        float segmentWidth = Math.Max(1f, segment.Width);
        if (run.CharacterSpacing == 0)
        {
            canvas.DrawString(segment.Text, x, y, segmentWidth, lineHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);
            if (run.IsBold)
            {
                canvas.DrawString(segment.Text, x + BoldOffset(drawFontSize), y, segmentWidth, lineHeight,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
            }
        }
        else
        {
            // Same per-glyph emulation as the plain path (each glyph needs its own advance).
            float cx = x;
            foreach (char character in segment.Text)
            {
                string glyph = Glyph(character);
                float glyphWidth = MeasureLine(glyph, fontSize);
                canvas.DrawString(glyph, cx, y, Math.Max(1f, glyphWidth), lineHeight,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
                if (run.IsBold)
                {
                    canvas.DrawString(glyph, cx + BoldOffset(drawFontSize), y, Math.Max(1f, glyphWidth), lineHeight,
                        HorizontalAlignment.Left, VerticalAlignment.Center);
                }
                cx += glyphWidth + (float)run.CharacterSpacing;
            }
        }
        DrawDecorations(canvas, run.TextDecorations, color, segment.Width, x, y, lineHeight, drawFontSize);
        canvas.FontColor = savedColor;
        canvas.FontSize = savedSize;
    }

    private void DrawLine(MauiCanvas canvas, string line, float x, float y, float lineHeight, float fontSize,
        float drawFontSize, bool bold)
    {
        if (CharacterSpacing == 0)
        {
            canvas.DrawString(line, x, y, Math.Max(1f, MeasureLine(line, fontSize)), lineHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);
            if (bold)
            {
                canvas.DrawString(line, x + BoldOffset(drawFontSize), y, Math.Max(1f, MeasureLine(line, fontSize)), lineHeight,
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
                canvas.DrawString(glyph, cx + BoldOffset(drawFontSize), y, Math.Max(1f, glyphWidth), lineHeight,
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

    private static void DrawDecorations(MauiCanvas canvas, TextDecorations decorations, Color color, float lineWidth,
        float x, float y, float lineHeight, float fontSize)
    {
        if (decorations == TextDecorations.None)
        {
            return;
        }
        Color stroke = canvas.StrokeColor;
        float strokeSize = canvas.StrokeSize;
        canvas.StrokeColor = color;
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
    private int _naturalLineHeightFontScaleGeneration = -1;

    private float EffectiveLineHeight(float fontSize)
    {
        int fontScaleGeneration = OpenHarmonyFontManager.FontScaleGeneration;
        if (Math.Abs(_naturalLineHeightFontSize - fontSize) > 0.001f ||
            _naturalLineHeightFontScaleGeneration != fontScaleGeneration)
        {
            float natural = OpenHarmonyLabelHandler.MeasureText("M", fontSize).Height;
            if (natural <= 0)
            {
                natural = OpenHarmonyFontManager.ScaleFontSize(fontSize) * 1.35f;
            }
            _naturalLineHeight = natural;
            _naturalLineHeightFontSize = fontSize;
            _naturalLineHeightFontScaleGeneration = fontScaleGeneration;
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
        var key = new TextLayoutKey(text, fontSize, maxWidth, LineBreakMode, MaxLines, CharacterSpacing, LineHeight,
            OpenHarmonyFontManager.FontScaleGeneration);
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
                while (start < text.Length && !Fits(string.Concat(Ellipsis.AsSpan(), text.AsSpan(start + 1)), fontSize, maxWidth))
                {
                    start++;
                }
                return string.Concat(Ellipsis.AsSpan(), text.AsSpan(Math.Min(start + 1, text.Length)));
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
                        || !Fits(string.Concat(text.AsSpan(0, nextHead), Ellipsis.AsSpan(), text.AsSpan(text.Length - nextTail)),
                            fontSize, maxWidth))
                    {
                        break;
                    }
                    head = nextHead;
                    tail = nextTail;
                }
                return string.Concat(text.AsSpan(0, head), Ellipsis.AsSpan(), text.AsSpan(text.Length - tail));
            }
            default:
            {
                int count = text.Length;
                while (count > 0 && !Fits(string.Concat(text.AsSpan(0, count), Ellipsis.AsSpan()), fontSize, maxWidth))
                {
                    count--;
                }
                return count <= 0 ? Ellipsis : string.Concat(text.AsSpan(0, count), Ellipsis.AsSpan());
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
        private readonly int _fontScaleGeneration;

        public TextLayoutKey(string text, float fontSize, float maxWidth, LineBreakMode mode, int maxLines,
            double characterSpacing, double lineHeight, int fontScaleGeneration)
        {
            _text = text;
            _fontSize = fontSize;
            _maxWidth = maxWidth;
            _mode = mode;
            _maxLines = maxLines;
            _characterSpacing = characterSpacing;
            _lineHeight = lineHeight;
            _fontScaleGeneration = fontScaleGeneration;
        }

        public bool Equals(TextLayoutKey other)
            => string.Equals(_text, other._text, StringComparison.Ordinal)
                && _fontSize.Equals(other._fontSize)
                && _maxWidth.Equals(other._maxWidth)
                && _mode == other._mode
                && _maxLines == other._maxLines
                && _characterSpacing.Equals(other._characterSpacing)
                && _lineHeight.Equals(other._lineHeight)
                && _fontScaleGeneration == other._fontScaleGeneration;

        public override bool Equals(object? obj) => obj is TextLayoutKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_text, _fontSize, _maxWidth, _mode, _maxLines, _characterSpacing, _lineHeight,
                _fontScaleGeneration);
    }
}
