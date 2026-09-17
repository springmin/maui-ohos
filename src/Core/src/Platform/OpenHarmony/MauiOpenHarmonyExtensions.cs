// MauiAppBuilder registration for the OpenHarmony platform services.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Hosting;

namespace Microsoft.Maui.Platform;

public static class MauiOpenHarmonyExtensions
{
    /// <summary>
    /// Registers the OpenHarmony platform services (dispatcher + window surface) with the
    /// MAUI app builder.
    /// </summary>
    public static MauiAppBuilder UseOpenHarmony(this MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<IDispatcher, OpenHarmonyDispatcher>();
        builder.Services.AddSingleton<OpenHarmonyWindowSurface>();
        builder.Services.AddSingleton<OpenHarmonyWindowRenderer>();
        builder.Services.AddSingleton<OpenHarmonyMauiAppHost>();
        builder.ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<ILabel, OpenHarmonyLabelHandler>();
            handlers.AddHandler<IButton, OpenHarmonyButtonHandler>();
            handlers.AddHandler<ILayout, OpenHarmonyLayoutHandler>();
            handlers.AddHandler<IWindow, OpenHarmonyWindowHandler>();
            handlers.AddHandler<IEntry, OpenHarmonyEntryHandler>();
            handlers.AddHandler<Microsoft.Maui.IImage, OpenHarmonyImageHandler>();
            handlers.AddHandler<IScrollView, OpenHarmonyScrollViewHandler>();
            handlers.AddHandler<ICheckBox, OpenHarmonyCheckBoxHandler>();
            handlers.AddHandler<ISwitch, OpenHarmonySwitchHandler>();
            handlers.AddHandler<ISlider, OpenHarmonySliderHandler>();
            handlers.AddHandler<IProgress, OpenHarmonyProgressBarHandler>();
            handlers.AddHandler<IActivityIndicator, OpenHarmonyActivityIndicatorHandler>();
        });
        return builder;
    }
}
