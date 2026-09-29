// Switch handler for OpenHarmony: a drawn track + thumb that toggles on tap.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySwitchHandler : OpenHarmonyViewHandler<ISwitch>
{
    public static readonly IPropertyMapper<ISwitch, OpenHarmonySwitchHandler> Mapper =
        new PropertyMapper<ISwitch, OpenHarmonySwitchHandler>(ViewMapper)
        {
            [nameof(ISwitch.IsOn)] = MapIsOn,
            [nameof(ISwitch.TrackColor)] = MapTrackColor,
            [nameof(ISwitch.ThumbColor)] = MapThumbColor,
        };

    public OpenHarmonySwitchHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsSwitch = true };
        view.Tap = () =>
        {
            if (VirtualView is { } toggle)
            {
                toggle.IsOn = !toggle.IsOn;
            }
        };
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(51, widthConstraint), Math.Min(31, heightConstraint));

    public static void MapIsOn(OpenHarmonySwitchHandler handler, ISwitch toggle)
        => handler.PlatformView.IsOn = toggle.IsOn;

    public static void MapTrackColor(OpenHarmonySwitchHandler handler, ISwitch toggle)
    {
        if (toggle.TrackColor is not null)
        {
            handler.PlatformView.SwitchTrackColor = toggle.TrackColor;
            handler.PlatformView.SliderMinimumTrackColor = toggle.TrackColor;
        }
    }

    public static void MapThumbColor(OpenHarmonySwitchHandler handler, ISwitch toggle)
        => handler.PlatformView.SwitchThumbColor = toggle.ThumbColor ?? Colors.White;
}
