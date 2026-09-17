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
        });
        return builder;
    }
}
