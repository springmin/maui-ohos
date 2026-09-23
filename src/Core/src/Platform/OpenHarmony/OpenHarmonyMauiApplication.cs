// Platform application object for the OpenHarmony slice (MAUI's IPlatformApplication), named
// like Tizen's MauiApplication so it does not collide with OpenHarmonyPlatformApplication (the
// IApplication handler's platform element). UseOpenHarmony registers it plus an
// IMauiInitializeService shim, so MauiAppBuilder.Build() publishes the singleton - and
// IPlatformApplication.Current, set through the interface type - before OpenHarmonyMauiAppHost.Run;
// Application resolves the registered IApplication lazily because it may not exist during Build.
// The zero-reference interfaces and their documented gaps - ITitleBar (the ArkTS shell owns the
// title bar), IKeyboardAccelerator (rc.1 has no per-element collection; see
// OpenHarmonyKeyboardAcceleratorManager) and IAdorner (nothing initializes the diagnostics
// overlay the slice's overlay host could draw) - are in docs/openharmony-slice-notes.md.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The OpenHarmony platform application: the object behind <see cref="IPlatformApplication"/>
/// while a MauiApp built with <see cref="MauiOpenHarmonyExtensions.UseOpenHarmony"/> is running.
/// </summary>
public sealed class OpenHarmonyMauiApplication : IPlatformApplication
{
    private readonly IServiceProvider _services;
    private IApplication? _application;

    /// <summary>
    /// Creates the platform application over the MauiApp's service provider and publishes it as
    /// <see cref="IPlatformApplication.Current"/> (the platform-entry-point contract). The
    /// interface's Current is a static virtual property with its own backing storage, so the
    /// assignment must go through the interface type - a class-level Current property would
    /// shadow it and leave the interface value null.
    /// </summary>
    public OpenHarmonyMauiApplication(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        IPlatformApplication.Current = this;
        Instance = this;
    }

    /// <summary>Typed access to the current instance (IPlatformApplication.Current is authoritative).</summary>
    public static OpenHarmonyMauiApplication? Instance { get; private set; }

    /// <inheritdoc />
    public IServiceProvider Services => _services;

    /// <inheritdoc />
    public IApplication Application => _application ??= _services.GetRequiredService<IApplication>();
}

/// <summary>
/// Runs inside <see cref="MauiAppBuilder.Build"/> and installs the platform-application object
/// and the window-overlay host. Registered by <see cref="MauiOpenHarmonyExtensions.UseOpenHarmony"/>.
/// </summary>
internal sealed class OpenHarmonyMauiApplicationInitializer : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
    {
        // Resolving the singleton publishes it (the constructor sets Current); the overlay host
        // needs its renderer hook installed before the first frame, and Build is the earliest
        // point with the final service provider.
        _ = services.GetRequiredService<OpenHarmonyMauiApplication>();
        OpenHarmonyWindowOverlayHost.Install();
    }
}
