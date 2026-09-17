// MauiAppBuilder registration for the OpenHarmony platform services.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Dispatching;
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
        return builder;
    }
}
