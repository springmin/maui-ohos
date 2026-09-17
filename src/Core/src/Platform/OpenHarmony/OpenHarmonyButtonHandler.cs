// ButtonHandler for OpenHarmony: draws a rounded button and routes taps to the virtual view.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyButtonHandler : OpenHarmonyViewHandler<IButton>
{
    public static readonly IPropertyMapper<IButton, OpenHarmonyButtonHandler> Mapper =
        new PropertyMapper<IButton, OpenHarmonyButtonHandler>(ViewMapper)
        {
            [nameof(IText.Text)] = MapText,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
            [nameof(IButton.Background)] = MapBackground,
            [nameof(IButtonStroke.CornerRadius)] = MapCornerRadius,
        };

    public OpenHarmonyButtonHandler() : base(Mapper) { }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        string text = (VirtualView as IText)?.Text ?? string.Empty;
        (float width, float height) = OpenHarmonyLabelHandler.MeasureText(text, PlatformView?.FontSize > 0 ? PlatformView.FontSize : 14f);
        return new Size(Math.Min(width + 48, widthConstraint), height + 32);
    }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { CornerRadius = 24f };
        view.Tap = () => VirtualView?.Clicked();
        return view;
    }

    public static void MapText(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.Text = (button as IText)?.Text;

    public static void MapTextColor(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.TextColor = (button as ITextStyle)?.TextColor ?? Colors.White;

    public static void MapBackground(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.Background = (button as IView)?.Background as SolidPaint is { Color: { } color }
            ? color
            : Colors.MediumSeaGreen;

    public static void MapCornerRadius(OpenHarmonyButtonHandler handler, IButton button)
        => handler.PlatformView.CornerRadius = (float)((button as IButtonStroke)?.CornerRadius ?? 24);
}
