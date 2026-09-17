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
        };

    public OpenHarmonyLabelHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        string text = (VirtualView as IText)?.Text ?? string.Empty;
        (float width, float height) = MeasureText(text, PlatformViewTextSize);
        return new Size(Math.Min(width, widthConstraint), height);
    }

    private float PlatformViewTextSize => PlatformView?.FontSize > 0 ? PlatformView.FontSize : 14f;

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

    public static void MapFont(OpenHarmonyLabelHandler handler, ILabel label)
        => handler.PlatformView.FontSize = (float)((double)label.Font.Size);
}
