// Platform application object for the OpenHarmony slice (MAUI's IPlatformApplication).
//
// Named like Tizen's MauiApplication: the existing OpenHarmonyPlatformApplication type in
// OpenHarmonyApplicationHandler.cs is the IApplication handler's platform element (a different
// role), so this entry point must not reuse that name.
//
// MAUI's platform entry point publishes the application's root service provider and the current
// IApplication through the static IPlatformApplication.Current, so class libraries can reach
// platform services without a platform reference (Tizen sets it from MauiApplication's
// constructor, Windows/iOS from their platform application classes). The slice's host
// (OpenHarmonyMauiAppHost) owns window creation and never had that companion object; this type is
// it, wired without touching the host:
//
//   * UseOpenHarmony registers OpenHarmonyMauiApplication plus an IMauiInitializeService shim.
//     MauiAppBuilder.Build() runs every IMauiInitializeService (the documented contract, verified
//     against Microsoft.Maui.Core 11.0.0-rc.1.26451.6), so the singleton is created and
//     IPlatformApplication.Current is set before an app calls OpenHarmonyMauiAppHost.Run.
//   * Services is the provider MauiAppBuilder.Build() handed the initializer. In rc.1 Build runs
//     initializers inside a service scope (verified: ServiceProviderEngineScope), so this is that
//     scope, not MauiApp.Services' root ServiceProvider; every singleton is shared with the root
//     (IApplication, the app host and this object are the same instances through both, verified
//     off-device), which is what "services resolve from the MauiApp builder" requires.
//   * Application resolves the IApplication singleton that UseMauiApp<TApp> registered and that
//     OpenHarmonyMauiAppHost.Run consumes. It is resolved lazily: during Build the app object may
//     not exist yet, and MAUI's own platforms also leave the application unset until startup.
//
// Zero-reference interfaces, status documented once (no silent gaps):
//
//   * ITitleBar / Microsoft.Maui.Controls.Window.TitleBar (rc.1: ITitleBar is IContentView +
//     IPadding + ICrossPlatformLayout with Title/Subtitle/PassthroughElements; Window.TitleBar is
//     a bindable property). The slice draws no window chrome of its own: the ArkTS shell owns the
//     title bar and OpenHarmonyWindowHandler.MapTitle only publishes the title through
//     ohos_host_set_window_title. Window.TitleBar is stored by Controls but never measured,
//     drawn or routed. A real implementation would add a title-bar row to the compositor
//     (measure/arrange/draw Title/Subtitle/PassthroughElements and route their touches like the
//     navigation bar does), map the window's navigation affordances (back button) onto it, and
//     confirm the ArkUI shell lets the app own the title area.
//
//   * IKeyboardAccelerator (the per-element collection). rc.1 exposes the interface
//     (Modifiers/Key) and the Microsoft.Maui.Controls.KeyboardAccelerator object, but the only
//     collection is MenuFlyoutItem.KeyboardAccelerators - there is no VisualElement-level
//     collection or mapper. The slice's OpenHarmonyKeyboardAcceleratorManager keeps its internal
//     registry fed by the hardware-key listener (PE2's OpenHarmonyKeyListener). A real
//     per-element surface needs MAUI to add VisualElement.KeyboardAccelerators (or an attached
//     property) plus a ViewMapper entry so the platform can observe the collection, and a
//     consuming key contract (bool OnKeyDown) so a match can stop the key from propagating; the
//     host callback is void, so the slice only records and invokes.
//
//   * IAdorner (rc.1: IAdorner is IWindowOverlayElement + Density + VisualView; Controls'
//     VisualDiagnosticsOverlay.AddAdorner builds one to frame a view). The slice's overlay host
//     (OpenHarmonyWindowOverlay.cs) draws any IWindowOverlayElement, so an adorner on an overlay
//     that is drawn/initialized renders. The remaining gap is that Controls creates
//     Window.VisualDiagnosticsOverlay with IsPlatformViewInitialized false and nothing in the
//     slice calls its Initialize() (Tizen does it from WindowHandler.MapContent, which the
//     slice's OpenHarmonyWindowHandler does not replace). Real wiring needs the window lifecycle
//     to initialize the diagnostics overlay; then the same overlay host draws it and its
//     adorners.
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
