// ButtonHandler for OpenHarmony: draws a rounded button and routes taps to the virtual view.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyButtonHandler : OpenHarmonyViewHandler<IButton>
{
    /// <summary>Platform padding used when the button does not set one (Button's default is NaN).</summary>
    private static readonly Thickness DefaultPadding = new(24, 16, 24, 16);

    public static readonly IPropertyMapper<IButton, OpenHarmonyButtonHandler> Mapper =
        new PropertyMapper<IButton, OpenHarmonyButtonHandler>(ViewMapper)
        {
            [nameof(IText.Text)] = MapText,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
            // Font carries FontSize + FontFamily + FontAttributes (Controls raises this one key).
            [nameof(ITextStyle.Font)] = MapFont,
            [nameof(ITextStyle.CharacterSpacing)] = MapCharacterSpacing,
            [nameof(IPadding.Padding)] = MapPadding,
            [nameof(IButton.Background)] = MapBackground,
            [nameof(IButtonStroke.CornerRadius)] = MapCornerRadius,
            [nameof(IButtonStroke.StrokeColor)] = MapBorderColor,
            [nameof(IButtonStroke.StrokeThickness)] = MapBorderWidth,
            [nameof(Microsoft.Maui.Controls.Button.LineBreakMode)] = MapLineBreakMode,
            [nameof(IImageSourcePart.Source)] = MapSource,
        };

    public OpenHarmonyButtonHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        // Buttons centre their content (MAUI semantics); the base text view defaults to leading.
        var view = new OpenHarmonyTextView { HorizontalTextAlignment = TextAlignment.Center, CornerRadius = 24f };
        view.Tap = () => VirtualView?.Clicked();
        return view;
    }

    private new OpenHarmonyTextView PlatformView => (OpenHarmonyTextView)base.PlatformView!;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        string text = (VirtualView as IText)?.Text ?? string.Empty;
        OpenHarmonyTextView view = PlatformView;
        Thickness padding = OpenHarmonyTextMapping.NormalizePadding(view.Padding, DefaultPadding);
        float fontSize = view.FontSize > 0 ? view.FontSize : 14f;
        float available = (float)Math.Max(0, widthConstraint - padding.HorizontalThickness);
        (float width, float height) = view.MeasureTextBlock(text, fontSize, available);
        if (height <= 0)
        {
            height = fontSize * 1.35f;
        }
        return new Size(
            Math.Min(width + padding.HorizontalThickness, widthConstraint),
            height + padding.VerticalThickness);
    }

    public static void MapText(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.Text = (button as IText)?.Text;

    public static void MapTextColor(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.TextColor = (button as ITextStyle)?.TextColor ?? Colors.White;

    /// <summary>Maps size, family, weight and slant from the button's resolved font.</summary>
    public static void MapFont(OpenHarmonyButtonHandler handler, IButton button)
    {
        if (button is not ITextStyle textStyle)
        {
            return;
        }
        Microsoft.Maui.Font font = textStyle.Font;
        OpenHarmonyTextView view = handler.PlatformView;
        view.FontSize = font.Size > 0 ? (float)font.Size : 14f;
        view.TextFont = OpenHarmonyTextMapping.Resolve(handler, font);
    }

    public static void MapCharacterSpacing(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.CharacterSpacing = (button as ITextStyle)?.CharacterSpacing ?? 0;

    public static void MapPadding(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.Padding =
            OpenHarmonyTextMapping.NormalizePadding((button as IPadding)?.Padding ?? DefaultPadding, DefaultPadding);

    public static void MapLineBreakMode(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.LineBreakMode = (button as Microsoft.Maui.Controls.Button)?.LineBreakMode
            ?? Microsoft.Maui.LineBreakMode.NoWrap;

    public static void MapBackground(OpenHarmonyButtonHandler handler, IButton button)
    {
        // A gradient/image background renders through the canvas paint facilities; only a solid
        // paint goes through the base colour path, and no background keeps the slice's green.
        Paint? background = (button as IView)?.Background;
        OpenHarmonyTextView view = handler.PlatformView;
        view.BackgroundPaint = background is SolidPaint or null ? null : background;
        view.Background = background switch
        {
            SolidPaint { Color: { } color } => color,
            null => Colors.MediumSeaGreen,
            _ => null,
        };
    }

    public static void MapSource(OpenHarmonyButtonHandler handler, IButton button)
    {
        handler.PlatformView.ImageBytes = null;
        if ((button as IImageSourcePart)?.Source is Microsoft.Maui.Controls.FileImageSource file &&
            !string.IsNullOrEmpty(file.File) && File.Exists(file.File))
        {
            handler.PlatformView.ImageBytes = File.ReadAllBytes(file.File);
        }
    }

    public static void MapCornerRadius(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.CornerRadius = (float)((button as IButtonStroke)?.CornerRadius ?? 24);

    public static void MapBorderColor(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.StrokeColor = (button as IButtonStroke)?.StrokeColor;

    public static void MapBorderWidth(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.StrokeThickness = (float)((button as IButtonStroke)?.StrokeThickness ?? 0);
}
