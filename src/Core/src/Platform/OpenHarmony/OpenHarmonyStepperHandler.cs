// Stepper handler for OpenHarmony: a drawn -/+ control (tap a half to step the value).
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyStepperHandler : OpenHarmonyViewHandler<IStepper>
{
    public static readonly IPropertyMapper<IStepper, OpenHarmonyStepperHandler> Mapper =
        new PropertyMapper<IStepper, OpenHarmonyStepperHandler>(ViewMapper)
        {
            [nameof(IRange.Value)] = MapValue,
            [nameof(IRange.Minimum)] = MapRange,
            [nameof(IRange.Maximum)] = MapRange,
            [nameof(IStepper.Interval)] = MapValue,
        };

    public OpenHarmonyStepperHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsStepper = true, Background = Colors.DimGray, TextColor = Colors.White };
        view.StepperStep = OnStep;
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(120, widthConstraint), Math.Min(32, heightConstraint));

    private void OnStep(bool increment)
    {
        if (VirtualView is not { } stepper)
        {
            return;
        }
        double interval = stepper.Interval > 0 ? Math.Max(stepper.Interval, 0.1) : 1;
        double value = Math.Clamp(stepper.Value + (increment ? interval : -interval), stepper.Minimum, stepper.Maximum);
        switch (stepper)
        {
            case Microsoft.Maui.Controls.Stepper control:
                control.Value = value;
                break;
        }
        PlatformView.StepperValue = value;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapValue(OpenHarmonyStepperHandler handler, IStepper stepper)
    {
        handler.PlatformView.StepperValue = stepper.Value;
        handler.PlatformView.StepperInterval = stepper.Interval > 0 ? stepper.Interval : 1;
    }

    public static void MapRange(OpenHarmonyStepperHandler handler, IStepper stepper)
    {
        handler.PlatformView.StepperMinimum = stepper.Minimum;
        handler.PlatformView.StepperMaximum = stepper.Maximum;
    }
}
