// CheckBox handler for OpenHarmony: a drawn box that toggles on tap.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyCheckBoxHandler : OpenHarmonyViewHandler<ICheckBox>
{
    public static readonly IPropertyMapper<ICheckBox, OpenHarmonyCheckBoxHandler> Mapper =
        new PropertyMapper<ICheckBox, OpenHarmonyCheckBoxHandler>(ViewMapper)
        {
            [nameof(ICheckBox.IsChecked)] = MapIsChecked,
            [nameof(ICheckBox.Foreground)] = MapForeground,
        };

    public OpenHarmonyCheckBoxHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsCheckBox = true };
        view.Tap = () =>
        {
            if (VirtualView is { } checkBox)
            {
                checkBox.IsChecked = !checkBox.IsChecked;
            }
        };
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(28, widthConstraint), Math.Min(28, heightConstraint));

    public static void MapIsChecked(OpenHarmonyCheckBoxHandler handler, ICheckBox checkBox)
        => handler.PlatformView.IsChecked = checkBox.IsChecked;

    public static void MapForeground(OpenHarmonyCheckBoxHandler handler, ICheckBox checkBox)
        => handler.PlatformView.CheckBoxColor = checkBox.Foreground is SolidPaint paint ? paint.Color : Colors.White;
}
