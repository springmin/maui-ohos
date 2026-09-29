// FormattedText (Span) support for the OpenHarmony label text view: the span list is desugared
// against its label into styled runs (span values override the label-level style, font attributes
// combine, the text transform inherits), and the run set is laid out with the same
// wrapping/truncation/max-lines contract the plain-text path implements. The layout keeps one
// entry per drawn line; every entry is a list of (run, text) segments so a line can mix fonts,
// colours, spacing, decorations and backgrounds. Everything the canvas cannot render per run
// (a span font family against the process-wide typeface, span gestures) is reported once through
// the status channel instead of being silently dropped.
using Microsoft.Maui;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One styled run of platform text: a MAUI <c>Span</c> resolved against its label (size, weight,
/// slant, colour, character spacing, line height, decorations and background).
/// </summary>
internal sealed class OpenHarmonyTextRun
{
    public OpenHarmonyTextRun(string text, Color? textColor, float fontSize, bool bold, bool italic,
        double characterSpacing, TextDecorations textDecorations, double lineHeight, Color? backgroundColor)
    {
        Text = text;
        TextColor = textColor;
        FontSize = fontSize;
        IsBold = bold;
        IsItalic = italic;
        CharacterSpacing = characterSpacing;
        TextDecorations = textDecorations;
        LineHeight = lineHeight;
        BackgroundColor = backgroundColor;
    }

    public string Text { get; }

    public Color? TextColor { get; }

    public float FontSize { get; }

    public bool IsBold { get; }

    public bool IsItalic { get; }

    public double CharacterSpacing { get; }

    public TextDecorations TextDecorations { get; }

    /// <summary>Line-height multiplier over the run's natural height (-1 keeps the natural one).</summary>
    public double LineHeight { get; }

    public Color? BackgroundColor { get; }
}

/// <summary>Builds the platform runs from a label's <c>FormattedText</c>.</summary>
internal static class OpenHarmonyFormattedText
{
    /// <summary>
    /// Returns the label's runs, or null when the label is in plain-text mode (FormattedText is
    /// null). An empty array means "formatted mode with nothing to draw" (every span is empty),
    /// which must not fall back to the plain-text path.
    /// </summary>
    public static OpenHarmonyTextRun[]? Create(ILabel label, Microsoft.Maui.Controls.Label? controls)
    {
        if (controls?.FormattedText is not { } formatted)
        {
            return null;
        }
        Microsoft.Maui.Font labelFont = label.Font;
        float labelFontSize = labelFont.Size is > 0 and < float.MaxValue ? (float)labelFont.Size : 14f;
        var runs = new List<OpenHarmonyTextRun>(formatted.Spans.Count);
        bool familyReported = false;
        bool gestureReported = false;
        foreach (Microsoft.Maui.Controls.Span span in formatted.Spans)
        {
            TextTransform transform = span.TextTransform != TextTransform.Default
                ? span.TextTransform
                : controls.TextTransform;
            string text = ApplyTextTransform(span.Text ?? string.Empty, transform);
            if (text.Length == 0)
            {
                continue;
            }
            if (!gestureReported && span.GestureRecognizers.Count > 0)
            {
                gestureReported = true;
                OpenHarmonyStatus.Once("text.span-gesture",
                    "a span gesture recognizer has no hit-testable glyph range: span gestures are not wired");
            }
            if (!familyReported && !string.IsNullOrEmpty(span.FontFamily) &&
                !string.Equals(span.FontFamily, labelFont.Family, StringComparison.Ordinal))
            {
                familyReported = true;
                OpenHarmonyStatus.Once("text.span-family",
                    "a span font family is drawn with the label's typeface: the OpenHarmony text bridge " +
                    "selects one process-wide typeface");
            }
            double size = double.IsNaN(span.FontSize) || span.FontSize <= 0 ? labelFontSize : span.FontSize;
            bool bold = labelFont.Weight >= FontWeight.Semibold ||
                (span.FontAttributes & FontAttributes.Bold) != 0;
            bool italic = labelFont.Slant != FontSlant.Default ||
                (span.FontAttributes & FontAttributes.Italic) != 0;
            double spacing = span.CharacterSpacing != 0 ? span.CharacterSpacing : label.CharacterSpacing;
            double lineHeight = span.LineHeight > 0 ? span.LineHeight : label.LineHeight;
            TextDecorations decorations = span.TextDecorations | label.TextDecorations;
            Color? color = span.TextColor ?? label.TextColor;
            runs.Add(new OpenHarmonyTextRun(text, color, (float)size, bold, italic, spacing, decorations,
                lineHeight, span.BackgroundColor));
        }
        return runs.ToArray();
    }

    /// <summary>Applies the MAUI text transform (the Controls layer leaves span text untransformed).</summary>
    public static string ApplyTextTransform(string text, TextTransform transform) => transform switch
    {
        TextTransform.Uppercase => text.ToUpper(),
        TextTransform.Lowercase => text.ToLower(),
        _ => text,
    };
}

/// <summary>One laid-out line: the styled segments that make it up.</summary>
internal sealed class OpenHarmonyFormattedLine
{
    private readonly List<OpenHarmonyFormattedSegment> _segments = new();

    public IReadOnlyList<OpenHarmonyFormattedSegment> Segments => _segments;

    public float Width { get; private set; }

    public float Height { get; private set; }

    /// <summary>Concatenated segment text (also what the max-lines truncation shortens).</summary>
    public string Text { get; private set; } = string.Empty;

    internal void Add(OpenHarmonyTextRun run, string text, float width, float height)
    {
        if (text.Length == 0)
        {
            return;
        }
        _segments.Add(new OpenHarmonyFormattedSegment(run, text, width));
        Width += width;
        Text += text;
        Height = Math.Max(Height, height);
    }

    /// <summary>Height of a line with no segments (an empty paragraph or an all-empty line).</summary>
    internal void Empty(float height) => Height = Math.Max(Height, height);
}

/// <summary>One run's slice of a line (a line can start/end in the middle of a run).</summary>
internal readonly struct OpenHarmonyFormattedSegment
{
    public OpenHarmonyFormattedSegment(OpenHarmonyTextRun run, string text, float width)
    {
        Run = run;
        Text = text;
        Width = width;
    }

    public OpenHarmonyTextRun Run { get; }

    public string Text { get; }

    public float Width { get; }
}

/// <summary>
/// Run-aware line breaking for the formatted view: paragraphs split on hard newlines, then the
/// mapped line-break mode wraps, truncates or measures the runs. Measurement always walks the
/// maximal single-run slices of a character range, so a word split across spans still wraps as
/// one word and each run keeps its own advance (font size plus intra-run character spacing).
/// </summary>
internal sealed class OpenHarmonyFormattedTextLayout
{
    private const string Ellipsis = "…";

    private readonly OpenHarmonyTextRun[] _runs;
    private readonly string _text;
    private readonly int[] _runOf;
    private readonly int[] _runEnd;
    private readonly LineBreakMode _mode;
    private readonly int _maxLines;
    private readonly float _maxWidth;
    private readonly float _defaultFontSize;
    private readonly Dictionary<float, float> _naturalHeights = new();

    public OpenHarmonyFormattedTextLayout(OpenHarmonyTextRun[] runs, LineBreakMode mode, int maxLines,
        float maxWidth, float defaultFontSize)
    {
        _runs = runs;
        _mode = mode;
        _maxLines = maxLines;
        _maxWidth = maxWidth;
        _defaultFontSize = defaultFontSize;
        var text = new System.Text.StringBuilder();
        var runOf = new List<int>();
        var runEnd = new int[runs.Length];
        for (int i = 0; i < runs.Length; i++)
        {
            text.Append(runs[i].Text);
            for (int c = 0; c < runs[i].Text.Length; c++)
            {
                runOf.Add(i);
            }
            runEnd[i] = text.Length;
        }
        _text = text.ToString();
        _runOf = runOf.ToArray();
        _runEnd = runEnd;
        var lines = Build();
        float width = 0f;
        float height = 0f;
        foreach (OpenHarmonyFormattedLine line in lines)
        {
            width = Math.Max(width, line.Width);
            height += line.Height;
        }
        Lines = lines;
        Width = width;
        Height = height;
    }

    public IReadOnlyList<OpenHarmonyFormattedLine> Lines { get; }

    public float Width { get; }

    public float Height { get; }

    /// <summary>Width of the character range [start, start + length) with per-run metrics.</summary>
    public float MeasureRange(int start, int length)
    {
        if (length <= 0)
        {
            return 0f;
        }
        float width = 0f;
        int end = start + length;
        int i = start;
        while (i < end)
        {
            int runIndex = _runOf[i];
            int segmentEnd = Math.Min(end, _runEnd[runIndex]);
            int segmentLength = segmentEnd - i;
            OpenHarmonyTextRun run = _runs[runIndex];
            width += OpenHarmonyLabelHandler.MeasureText(_text.Substring(i, segmentLength), run.FontSize).Width;
            width += (float)(run.CharacterSpacing * Math.Max(0, segmentLength - 1));
            i = segmentEnd;
        }
        return width;
    }

    private List<OpenHarmonyFormattedLine> Build()
    {
        var lines = new List<OpenHarmonyFormattedLine>();
        if (_text.Length == 0)
        {
            AddEmptyLine(lines);
            return lines;
        }
        int start = 0;
        while (true)
        {
            int newline = _text.IndexOf('\n', start);
            int end = newline < 0 ? _text.Length : newline;
            AddParagraph(lines, start, end);
            if (newline < 0)
            {
                break;
            }
            start = newline + 1;
        }
        if (_maxLines > 0 && lines.Count > _maxLines)
        {
            lines.RemoveRange(_maxLines, lines.Count - _maxLines);
            lines[^1] = TruncateTail(lines[^1]);
        }
        return lines;
    }

    private void AddParagraph(List<OpenHarmonyFormattedLine> lines, int start, int end)
    {
        switch (_mode)
        {
            case LineBreakMode.NoWrap:
                AddLine(lines, start, end);
                return;
            case LineBreakMode.HeadTruncation:
            case LineBreakMode.MiddleTruncation:
            case LineBreakMode.TailTruncation:
                if (MeasureRange(start, end - start) <= _maxWidth)
                {
                    AddLine(lines, start, end);
                }
                else
                {
                    AddTruncatedLine(lines, start, end);
                }
                return;
            case LineBreakMode.CharacterWrap:
                WrapCharacters(lines, start, end);
                return;
            default:
                WrapWords(lines, start, end);
                return;
        }
    }

    private void AddLine(List<OpenHarmonyFormattedLine> lines, int start, int end)
    {
        var line = new OpenHarmonyFormattedLine();
        AddSegments(line, start, end);
        if (line.Segments.Count == 0)
        {
            line.Empty(EmptyHeight());
        }
        lines.Add(line);
    }

    private void AddEmptyLine(List<OpenHarmonyFormattedLine> lines)
    {
        var line = new OpenHarmonyFormattedLine();
        line.Empty(EmptyHeight());
        lines.Add(line);
    }

    private float EmptyHeight()
        => _runs.Length > 0 ? EffectiveHeight(_runs[0]) : NaturalHeight(_defaultFontSize);

    /// <summary>Appends the maximal single-run segments of [start, end) to the line.</summary>
    private void AddSegments(OpenHarmonyFormattedLine line, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            int runIndex = _runOf[i];
            int segmentEnd = Math.Min(end, _runEnd[runIndex]);
            OpenHarmonyTextRun run = _runs[runIndex];
            string text = _text.Substring(i, segmentEnd - i);
            float width = OpenHarmonyLabelHandler.MeasureText(text, run.FontSize).Width +
                (float)(run.CharacterSpacing * Math.Max(0, text.Length - 1));
            line.Add(run, text, width, EffectiveHeight(run));
            i = segmentEnd;
        }
    }

    private void WrapWords(List<OpenHarmonyFormattedLine> lines, int start, int end)
    {
        if (start >= end)
        {
            AddEmptyLine(lines);
            return;
        }
        bool hasWord = false;
        int lineStart = start;
        int lineEnd = start;
        int cursor = start;
        while (cursor < end)
        {
            int wordStart = cursor;
            while (wordStart < end && _text[wordStart] == ' ')
            {
                wordStart++;
            }
            if (wordStart >= end)
            {
                break;
            }
            int wordEnd = wordStart;
            while (wordEnd < end && _text[wordEnd] != ' ')
            {
                wordEnd++;
            }
            if (!hasWord)
            {
                if (MeasureRange(wordStart, wordEnd - wordStart) <= _maxWidth)
                {
                    lineStart = wordStart;
                    lineEnd = wordEnd;
                    hasWord = true;
                }
                else
                {
                    // A single word wider than the line: split it by characters.
                    WrapCharacters(lines, wordStart, wordEnd);
                }
            }
            else if (MeasureRange(lineStart, wordEnd - lineStart) <= _maxWidth)
            {
                lineEnd = wordEnd;
            }
            else
            {
                AddLine(lines, lineStart, lineEnd);
                if (MeasureRange(wordStart, wordEnd - wordStart) <= _maxWidth)
                {
                    lineStart = wordStart;
                    lineEnd = wordEnd;
                    hasWord = true;
                }
                else
                {
                    WrapCharacters(lines, wordStart, wordEnd);
                    hasWord = false;
                }
            }
            cursor = wordEnd;
        }
        if (hasWord)
        {
            AddLine(lines, lineStart, lineEnd);
        }
    }

    private void WrapCharacters(List<OpenHarmonyFormattedLine> lines, int start, int end)
    {
        if (start >= end)
        {
            AddEmptyLine(lines);
            return;
        }
        int cursor = start;
        while (cursor < end)
        {
            int count = LongestPrefix(cursor, end);
            if (count <= 0)
            {
                count = 1;
            }
            AddLine(lines, cursor, cursor + count);
            cursor += count;
        }
    }

    /// <summary>Longest character count from <paramref name="start"/> that fits the line width.</summary>
    private int LongestPrefix(int start, int end)
    {
        if (float.IsInfinity(_maxWidth) || _maxWidth <= 0)
        {
            return end - start;
        }
        int low = 1;
        int high = end - start;
        int best = 0;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (MeasureRange(start, middle) <= _maxWidth)
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

    private void AddTruncatedLine(List<OpenHarmonyFormattedLine> lines, int start, int end)
    {
        var line = new OpenHarmonyFormattedLine();
        int length = end - start;
        switch (_mode)
        {
            case LineBreakMode.HeadTruncation:
            {
                int drop = 0;
                while (drop < length - 1 &&
                    EllipsisWidth(RunAt(start + drop + 1)) + MeasureRange(start + drop + 1, length - drop - 1) > _maxWidth)
                {
                    drop++;
                }
                line.Add(RunAt(start + drop), Ellipsis,
                    OpenHarmonyLabelHandler.MeasureText(Ellipsis, RunAt(start + drop).FontSize).Width,
                    EffectiveHeight(RunAt(start + drop)));
                AddSegments(line, start + drop, end);
                break;
            }
            case LineBreakMode.MiddleTruncation:
            {
                int head = 0;
                int tail = 0;
                while (head + tail < length - 1)
                {
                    bool growHead = head <= tail;
                    int nextHead = head + (growHead ? 1 : 0);
                    int nextTail = tail + (growHead ? 0 : 1);
                    if (nextHead + nextTail > length - 1)
                    {
                        break;
                    }
                    float width = MeasureRange(start, nextHead) +
                        EllipsisWidth(RunAt(start + nextHead)) +
                        MeasureRange(end - nextTail, nextTail);
                    if (width > _maxWidth)
                    {
                        break;
                    }
                    head = nextHead;
                    tail = nextTail;
                }
                AddSegments(line, start, start + head);
                OpenHarmonyTextRun boundary = RunAt(start + head);
                line.Add(boundary, Ellipsis,
                    OpenHarmonyLabelHandler.MeasureText(Ellipsis, boundary.FontSize).Width,
                    EffectiveHeight(boundary));
                AddSegments(line, end - tail, end);
                break;
            }
            default:
            {
                int count = length;
                while (count > 0 &&
                    MeasureRange(start, count) + EllipsisWidth(RunAt(start + count - 1)) > _maxWidth)
                {
                    count--;
                }
                if (count > 0)
                {
                    AddSegments(line, start, start + count);
                }
                OpenHarmonyTextRun last = RunAt(start + Math.Max(0, count - 1));
                line.Add(last, Ellipsis,
                    OpenHarmonyLabelHandler.MeasureText(Ellipsis, last.FontSize).Width,
                    EffectiveHeight(last));
                break;
            }
        }
        if (line.Segments.Count == 0)
        {
            line.Empty(EmptyHeight());
        }
        lines.Add(line);
    }

    /// <summary>Appends the ellipsis to the last line's last run and shortens it until it fits.</summary>
    private OpenHarmonyFormattedLine TruncateTail(OpenHarmonyFormattedLine line)
    {
        if (line.Segments.Count == 0)
        {
            return line;
        }
        var result = new OpenHarmonyFormattedLine();
        float used = 0f;
        for (int i = 0; i < line.Segments.Count; i++)
        {
            OpenHarmonyFormattedSegment segment = line.Segments[i];
            string text = i == line.Segments.Count - 1 ? segment.Text + Ellipsis : segment.Text;
            float width = MeasureSegment(segment.Run, text);
            if (i == line.Segments.Count - 1)
            {
                while (text.Length > 1 && used + width > _maxWidth)
                {
                    text = text.Substring(0, text.Length - 1);
                    width = MeasureSegment(segment.Run, text);
                }
            }
            result.Add(segment.Run, text, width, EffectiveHeight(segment.Run));
            used += width;
        }
        return result;
    }

    private float EllipsisWidth(OpenHarmonyTextRun run)
        => OpenHarmonyLabelHandler.MeasureText(Ellipsis, run.FontSize).Width;

    private static float MeasureSegment(OpenHarmonyTextRun run, string text)
        => OpenHarmonyLabelHandler.MeasureText(text, run.FontSize).Width +
            (float)(run.CharacterSpacing * Math.Max(0, text.Length - 1));

    private OpenHarmonyTextRun RunAt(int index)
        => _runs[index >= _text.Length ? _runs.Length - 1 : _runOf[index]];

    private float NaturalHeight(float fontSize)
    {
        if (_naturalHeights.TryGetValue(fontSize, out float cached))
        {
            return cached;
        }
        (float _, float height) = OpenHarmonyLabelHandler.MeasureText("M", fontSize);
        if (height <= 0)
        {
            height = fontSize * 1.35f;
        }
        _naturalHeights[fontSize] = height;
        return height;
    }

    private float EffectiveHeight(OpenHarmonyTextRun run)
        => (float)(run.LineHeight > 0 ? run.LineHeight * NaturalHeight(run.FontSize) : NaturalHeight(run.FontSize));
}
