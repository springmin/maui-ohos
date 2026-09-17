// ProgressBar handler for OpenHarmony: a drawn track + progress fill.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyProgressBarHandler : OpenHarmonyViewHandler<IProgress>
{
    public static readonly IPropertyMapper<IProgress, OpenHarmonyProgressBarHandler> Mapper =
        new PropertyMapper<IProgress, OpenHarmonyProgressBarHandler>(ViewMapper)
        {
            [nameof(IProgress.Progress)] = MapProgress,
            [nameof(IProgress.ProgressColor)] = MapProgressColor,
        };

    public OpenHarmonyProgressBarHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsProgressBar = true };

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(220, widthConstraint), Math.Min(8, heightConstraint));

    public static void MapProgress(OpenHarmonyProgressBarHandler handler, IProgress progress)
        => handler.PlatformView.Progress = progress.Progress;

    public static void MapProgressColor(OpenHarmonyProgressBarHandler handler, IProgress progress)
    {
        if (progress.ProgressColor is not null)
        {
            handler.PlatformView.ProgressColor = progress.ProgressColor;
        }
    }
}
