// RadioButton handler for OpenHarmony: a drawn circle + dot with the button's text.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyRadioButtonHandler : OpenHarmonyViewHandler<IRadioButton>
{
    public static readonly IPropertyMapper<IRadioButton, OpenHarmonyRadioButtonHandler> Mapper =
        new PropertyMapper<IRadioButton, OpenHarmonyRadioButtonHandler>(ViewMapper)
        {
            [nameof(IRadioButton.IsChecked)] = MapIsChecked,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
            [nameof(IButtonStroke.StrokeColor)] = MapStrokeColor,
        };

    public OpenHarmonyRadioButtonHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsRadioButton = true, FontSize = 14f };
        view.Tap = () =>
        {
            if (VirtualView is { } radio)
            {
                radio.IsChecked = !radio.IsChecked;
            }
            OpenHarmonyBridge.RequestRedraw();
        };
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        PlatformView.Text = ContentText;
        // Size hint only: use the managed estimate (no platform text metrics during layout).
        double fontSize = (VirtualView as ITextStyle) is { } style ? style.Font.Size : 14.0;
        double width = ContentText.Length * fontSize * 0.55;
        return new Size(Math.Min(width + 40, widthConstraint), Math.Min(32, heightConstraint));
    }

    /// <summary>A RadioButton shows either a string content or a view content (drawn by the renderer).</summary>
    private string ContentText => (VirtualView as Microsoft.Maui.Controls.RadioButton)?.Content?.ToString() ?? string.Empty;

    public static void MapIsChecked(OpenHarmonyRadioButtonHandler handler, IRadioButton radio)
    {
        handler.PlatformView.RadioChecked = radio.IsChecked;
        handler.PlatformView.Text = handler.ContentText;
    }

    public static void MapTextColor(OpenHarmonyRadioButtonHandler handler, IRadioButton radio)
        => handler.PlatformView.TextColor = (radio as ITextStyle)?.TextColor ?? Colors.White;

    public static void MapStrokeColor(OpenHarmonyRadioButtonHandler handler, IRadioButton radio)
    {
        if ((radio as IButtonStroke)?.StrokeColor is { } color)
        {
            handler.PlatformView.RadioColor = color;
        }
    }
}
