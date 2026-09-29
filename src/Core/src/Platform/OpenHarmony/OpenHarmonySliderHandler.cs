// Slider handler for OpenHarmony: drag updates IRange.Value; the track/thumb are drawn.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySliderHandler : OpenHarmonyViewHandler<ISlider>
{
    private bool _dragging;

    public static readonly IPropertyMapper<ISlider, OpenHarmonySliderHandler> Mapper =
        new PropertyMapper<ISlider, OpenHarmonySliderHandler>(ViewMapper)
        {
            [nameof(IRange.Value)] = MapValue,
            [nameof(IRange.Minimum)] = MapRange,
            [nameof(IRange.Maximum)] = MapRange,
            [nameof(ISlider.MinimumTrackColor)] = MapMinimumTrackColor,
            [nameof(ISlider.MaximumTrackColor)] = MapMaximumTrackColor,
            [nameof(ISlider.ThumbColor)] = MapThumbColor,
        };

    public OpenHarmonySliderHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsSlider = true };
        view.SliderDrag = OnSliderDrag;
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(220, widthConstraint), Math.Min(40, heightConstraint));

    private void OnSliderDrag(float fraction, bool completed)
    {
        if (VirtualView is not { } slider)
        {
            return;
        }
        if (!_dragging)
        {
            _dragging = true;
            slider.DragStarted();
        }
        double value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
        switch (slider)
        {
            case Microsoft.Maui.Controls.Slider control:
                control.Value = value;
                break;
        }
        PlatformView.SliderValue = value;
        if (completed)
        {
            _dragging = false;
            slider.DragCompleted();
        }
    }

    public static void MapValue(OpenHarmonySliderHandler handler, ISlider slider)
        => handler.PlatformView.SliderValue = slider.Value;

    public static void MapRange(OpenHarmonySliderHandler handler, ISlider slider)
    {
        handler.PlatformView.SliderMinimum = slider.Minimum;
        handler.PlatformView.SliderMaximum = slider.Maximum;
    }

    public static void MapMinimumTrackColor(OpenHarmonySliderHandler handler, ISlider slider)
        => handler.PlatformView.SliderMinimumTrackColor = slider.MinimumTrackColor ?? Colors.DodgerBlue;

    public static void MapMaximumTrackColor(OpenHarmonySliderHandler handler, ISlider slider)
        => handler.PlatformView.SliderMaximumTrackColor = slider.MaximumTrackColor ?? Colors.DimGray;

    public static void MapThumbColor(OpenHarmonySliderHandler handler, ISlider slider)
        => handler.PlatformView.SliderThumbColor = slider.ThumbColor ?? Colors.White;
}
