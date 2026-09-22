// LabelHandler for OpenHarmony: maps ILabel onto the compositor's platform view.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using HostCanvas = Microsoft.OpenHarmony.Hosting.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyLabelHandler : OpenHarmonyViewHandler<ILabel>
{
    public static readonly IPropertyMapper<ILabel, OpenHarmonyLabelHandler> Mapper =
        new PropertyMapper<ILabel, OpenHarmonyLabelHandler>(ViewMapper)
        {
            [nameof(ILabel.Text)] = MapText,
            [nameof(ILabel.TextColor)] = MapTextColor,
            [nameof(ITextStyle.Font)] = MapFont,
            [nameof(ITextStyle.CharacterSpacing)] = MapCharacterSpacing,
            [nameof(ITextAlignment.HorizontalTextAlignment)] = MapHorizontalTextAlignment,
            [nameof(ITextAlignment.VerticalTextAlignment)] = MapVerticalTextAlignment,
            [nameof(ILabel.LineHeight)] = MapLineHeight,
            [nameof(ILabel.TextDecorations)] = MapTextDecorations,
            [nameof(Microsoft.Maui.Controls.Label.MaxLines)] = MapMaxLines,
            [nameof(Microsoft.Maui.Controls.Label.LineBreakMode)] = MapLineBreakMode,
            [nameof(Microsoft.Maui.Controls.Label.Padding)] = MapPadding,
        };

    public OpenHarmonyLabelHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        // The styled view carries the text attributes the base view does not model (the mappers
        // below fill them in through the Label's Font/ITextStyle/ITextAlignment/ILabel surface).
        return new OpenHarmonyTextView();
    }

    private new OpenHarmonyTextView PlatformView => (OpenHarmonyTextView)base.PlatformView!;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        string text = (VirtualView as IText)?.Text ?? string.Empty;
        OpenHarmonyTextView view = PlatformView;
        Thickness padding = OpenHarmonyTextMapping.NormalizePadding(view.Padding, Thickness.Zero);
        float fontSize = FontSizeOf(view);
        float available = (float)Math.Max(0, widthConstraint - padding.HorizontalThickness);
        (float width, float height) = view.MeasureTextBlock(text, fontSize, available);
        return new Size(
            Math.Min(width + padding.HorizontalThickness, widthConstraint),
            height + padding.VerticalThickness);
    }

    private static float FontSizeOf(OpenHarmonyTextView view) => view.FontSize > 0 ? view.FontSize : 14f;

    /// <summary>Platform text metrics with an estimate fallback (device text APIs need a surface).</summary>
    internal static (float Width, float Height) MeasureText(string text, float fontSize)
    {
        if (!string.IsNullOrEmpty(text) &&
            HostCanvas.MeasureText(text, fontSize, out int measuredWidth, out int measuredHeight) &&
            measuredWidth > 0)
        {
            return (measuredWidth, measuredHeight);
        }
        return (text.Length * fontSize * 0.55f, fontSize * 1.35f);
    }

    public static void MapText(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.Text = label.Text;

    public static void MapTextColor(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.TextColor = label.TextColor ?? Colors.White;

    /// <summary>
    /// Maps the whole font surface (size, family, weight, slant). MAUI raises the Font change for
    /// FontSize/FontFamily/FontAttributes changes, and <see cref="Microsoft.Maui.Font"/> carries
    /// all of them, so one mapper covers the three.
    /// </summary>
    public static void MapFont(OpenHarmonyLabelHandler handler, ILabel label)
    {
        Microsoft.Maui.Font font = label.Font;
        OpenHarmonyTextView view = handler.PlatformView;
        view.FontSize = font.Size > 0 ? (float)font.Size : 14f;
        view.TextFont = OpenHarmonyTextMapping.Resolve(handler, font);
    }

    public static void MapCharacterSpacing(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.CharacterSpacing = label.CharacterSpacing;

    public static void MapHorizontalTextAlignment(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.HorizontalTextAlignment = label.HorizontalTextAlignment;

    public static void MapVerticalTextAlignment(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.VerticalTextAlignment = label.VerticalTextAlignment;

    public static void MapLineHeight(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.LineHeight = label.LineHeight;

    public static void MapTextDecorations(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.TextDecorations = label.TextDecorations;

    public static void MapMaxLines(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.MaxLines = (label as Microsoft.Maui.Controls.Label)?.MaxLines ?? -1;

    public static void MapLineBreakMode(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.LineBreakMode = (label as Microsoft.Maui.Controls.Label)?.LineBreakMode
            ?? Microsoft.Maui.LineBreakMode.WordWrap;

    public static void MapPadding(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.Padding =
            OpenHarmonyTextMapping.NormalizePadding((label as IPadding)?.Padding ?? Thickness.Zero, Thickness.Zero);
}
