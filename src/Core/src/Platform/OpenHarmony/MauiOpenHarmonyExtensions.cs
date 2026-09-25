// MauiAppBuilder registration for the OpenHarmony platform services.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Hosting;

namespace Microsoft.Maui.Platform;

public static class MauiOpenHarmonyExtensions
{
    /// <summary>
    /// Handler registry for the platform slice. It is explicit because MAUI registers its own
    /// platform-partial handlers for several of the same types (pages in particular), and the
    /// slice's platform-less handlers must win in <see cref="OpenHarmonyMauiAppHost"/>.
    /// Values are <see cref="SliceHandlerRegistration"/> rather than a bare Type so the trim/AOT
    /// analyzers can follow the constructor contract through AddHandler and the connector's
    /// factory (FIX-INTEROP #5/#6; a Dictionary&lt;Type, Type&gt; erased it and produced
    /// IL2072/IL2067).
    /// </summary>
    internal static readonly Dictionary<Type, SliceHandlerRegistration> SliceHandlers = new()
    {
        // The application handler is registered here as well as attached directly by
        // OpenHarmonyMauiAppHost.Run (which must set Application.Handler before the first window
        // exists); the registration keeps it resolvable through the standard MAUI handler
        // collection and through OpenHarmonyHandlerConnector's interface walk.
        [typeof(IApplication)] = new(typeof(OpenHarmonyApplicationHandler)),
        [typeof(ILabel)] = new(typeof(OpenHarmonyLabelHandler)),
        [typeof(IButton)] = new(typeof(OpenHarmonyButtonHandler)),
        [typeof(ILayout)] = new(typeof(OpenHarmonyLayoutHandler)),
        [typeof(IWindow)] = new(typeof(OpenHarmonyWindowHandler)),
        [typeof(IEntry)] = new(typeof(OpenHarmonyEntryHandler)),
        [typeof(Microsoft.Maui.IImage)] = new(typeof(OpenHarmonyImageHandler)),
        [typeof(IScrollView)] = new(typeof(OpenHarmonyScrollViewHandler)),
        [typeof(ICheckBox)] = new(typeof(OpenHarmonyCheckBoxHandler)),
        [typeof(ISwitch)] = new(typeof(OpenHarmonySwitchHandler)),
        [typeof(ISlider)] = new(typeof(OpenHarmonySliderHandler)),
        [typeof(IProgress)] = new(typeof(OpenHarmonyProgressBarHandler)),
        [typeof(IActivityIndicator)] = new(typeof(OpenHarmonyActivityIndicatorHandler)),
        [typeof(Microsoft.Maui.Controls.NavigationPage)] = new(typeof(OpenHarmonyNavigationPageHandler)),
        [typeof(Microsoft.Maui.Controls.Page)] = new(typeof(OpenHarmonyPageHandler)),
        [typeof(Microsoft.Maui.Controls.CollectionView)] = new(typeof(OpenHarmonyCollectionViewHandler)),
        [typeof(IShapeView)] = new(typeof(OpenHarmonyShapeHandler)),
        [typeof(IBorderView)] = new(typeof(OpenHarmonyBorderHandler)),
        [typeof(IStepper)] = new(typeof(OpenHarmonyStepperHandler)),
        [typeof(IRadioButton)] = new(typeof(OpenHarmonyRadioButtonHandler)),
        [typeof(ISearchBar)] = new(typeof(OpenHarmonySearchBarHandler)),
        [typeof(IPicker)] = new(typeof(OpenHarmonyPickerHandler)),
        [typeof(IDatePicker)] = new(typeof(OpenHarmonyDatePickerHandler)),
        [typeof(ITimePicker)] = new(typeof(OpenHarmonyTimePickerHandler)),
        [typeof(Microsoft.Maui.Controls.TabbedPage)] = new(typeof(OpenHarmonyTabbedPageHandler)),
        [typeof(Microsoft.Maui.Controls.FlyoutPage)] = new(typeof(OpenHarmonyFlyoutPageHandler)),
        [typeof(Microsoft.Maui.Controls.Shell)] = new(typeof(OpenHarmonyShellHandler)),
        [typeof(Microsoft.Maui.Controls.ListView)] = new(typeof(OpenHarmonyListViewHandler)),
        [typeof(Microsoft.Maui.Controls.SwipeView)] = new(typeof(OpenHarmonySwipeViewHandler)),
        [typeof(Microsoft.Maui.Controls.RefreshView)] = new(typeof(OpenHarmonyRefreshViewHandler)),
        [typeof(Microsoft.Maui.Controls.CarouselView)] = new(typeof(OpenHarmonyCarouselViewHandler)),
        [typeof(Microsoft.Maui.Controls.BoxView)] = new(typeof(OpenHarmonyBoxViewHandler)),
        [typeof(Microsoft.Maui.Controls.ImageButton)] = new(typeof(OpenHarmonyImageButtonHandler)),
        [typeof(IIndicatorView)] = new(typeof(OpenHarmonyIndicatorViewHandler)),
        [typeof(Microsoft.Maui.Controls.Frame)] = new(typeof(OpenHarmonyFrameHandler)),
        [typeof(Microsoft.Maui.Controls.Editor)] = new(typeof(OpenHarmonyEditorHandler)),
        [typeof(IGraphicsView)] = new(typeof(OpenHarmonyGraphicsViewHandler)),
        [typeof(Microsoft.Maui.Controls.TemplatedView)] = new(typeof(OpenHarmonyContentViewHandler)),
        [typeof(IWebView)] = new(typeof(OpenHarmonyWebViewHandler)),
        [typeof(IHybridWebView)] = new(typeof(OpenHarmonyHybridWebViewHandler)),
#if OPENHARMONY_BLAZOR_WEBVIEW
        [typeof(Microsoft.AspNetCore.Components.WebView.Maui.IBlazorWebView)] = new(typeof(OpenHarmonyBlazorWebViewHandler)),
#endif
    };

    /// <summary>
    /// Explicit no-op. The Essentials implementations are installed through the DI registrations
    /// in <see cref="UseOpenHarmony"/> and each feature's [ModuleInitializer] (app-launching,
    /// haptics, menus, TextToSpeech, theme); the static Current/Default entry points resolve from
    /// the service provider. The previous reflection loop assigned get-only properties, so the
    /// first assignment threw and no property was ever set.
    /// </summary>
    private static void InstallEssentials()
    {
    }

    /// <summary>
    /// Registers the OpenHarmony platform services (dispatcher + window surface) and the
    /// handler set with the MAUI app builder.
    /// </summary>
    public static MauiAppBuilder UseOpenHarmony(this MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<IDispatcher, OpenHarmonyDispatcher>();
        builder.Services.AddSingleton<Microsoft.Maui.Dispatching.IDispatcherProvider, OpenHarmonyDispatcherProvider>();
        builder.Services.AddSingleton<Microsoft.Maui.IFontManager, OpenHarmonyFontManager>();
        OpenHarmonySensors.Install();
        builder.Services.AddSingleton<Microsoft.Maui.Controls.Platform.IAlertManager, OpenHarmonyAlertManager>();
        // Essentials: file-backed preferences/filesystem (the Essentials assembly ships with the
        // MAUI controls package, so apps can use Preferences/FileSystem directly).
        var preferences = new OpenHarmonyPreferences();
        var fileSystem = new OpenHarmonyFileSystem();
        builder.Services.AddSingleton<Microsoft.Maui.Storage.IPreferences>(preferences);
        builder.Services.AddSingleton<Microsoft.Maui.Storage.IFileSystem>(fileSystem);
        var secureStorage = new OpenHarmonySecureStorage();
        var appInfo = new OpenHarmonyAppInfo();
        var deviceInfo = new OpenHarmonyDeviceInfo();
        var versionTracking = new OpenHarmonyVersionTracking(preferences);
        var appActions = new OpenHarmonyAppActions();
        var clipboard = new OpenHarmonyClipboard();
        var connectivity = new OpenHarmonyConnectivity();
        var launcher = new OpenHarmonyLauncher();
        var browser = new OpenHarmonyBrowser();
        var share = new OpenHarmonyShare();
        var vibration = new OpenHarmonyVibration();
        var permissions = new OpenHarmonyPermissions();
        var geolocation = new OpenHarmonyGeolocation();
        var filePicker = new OpenHarmonyFilePicker();
        var mediaPicker = new OpenHarmonyMediaPicker();
        builder.Services.AddSingleton<Microsoft.Maui.Storage.ISecureStorage>(secureStorage);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IAppInfo>(appInfo);
        builder.Services.AddSingleton<Microsoft.Maui.Devices.IDeviceInfo>(deviceInfo);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IVersionTracking>(versionTracking);
        // IAppActions has no runtime shortcut setter on OpenHarmony; registering the degrading
        // implementation keeps AppActions.Current (and apps iterating shortcuts) from hitting
        // the reference-assembly not-implemented exception.
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IAppActions>(appActions);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard>(clipboard);
        builder.Services.AddSingleton<Microsoft.Maui.Networking.IConnectivity>(connectivity);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.ILauncher>(launcher);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IBrowser>(browser);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.DataTransfer.IShare>(share);
        builder.Services.AddSingleton<Microsoft.Maui.Devices.IVibration>(vibration);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IPermissions>(permissions);
        builder.Services.AddSingleton<Microsoft.Maui.Devices.Sensors.IGeolocation>(geolocation);
        builder.Services.AddSingleton<Microsoft.Maui.Storage.IFilePicker>(filePicker);
        builder.Services.AddSingleton<Microsoft.Maui.Media.IMediaPicker>(mediaPicker);
        // WebAuthenticator: the browser redirect cannot come back to the app in this slice (no
        // callback ability skill in the HAP and no want->host forwarding in the shell), so the
        // honest implementation answers FeatureNotSupportedException; registering it here keeps
        // both DI resolution and WebAuthenticator.Default (ModuleInitializer field install) off
        // the Essentials reference-assembly exception (see OpenHarmonyWebAuthenticator.cs).
        builder.Services.AddSingleton<Microsoft.Maui.Authentication.IWebAuthenticator>(OpenHarmonyWebAuthenticator.Instance);
        // Communication / capture / geocoding: the statics (Email.Default, Sms.Default,
        // PhoneDialer.Default, Screenshot.Default, Geocoding.Default) resolve from these
        // registrations when the app is built, exactly like Clipboard/Connectivity above.
        var email = OpenHarmonyEmail.Instance;
        var sms = OpenHarmonySms.Instance;
        var phoneDialer = OpenHarmonyPhoneDialer.Instance;
        var screenshot = OpenHarmonyScreenshot.Instance;
        var geocoding = new OpenHarmonyGeocoding();
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.Communication.IEmail>(email);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.Communication.ISms>(sms);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.Communication.IPhoneDialer>(phoneDialer);
        builder.Services.AddSingleton<Microsoft.Maui.Media.IScreenshot>(screenshot);
        builder.Services.AddSingleton<Microsoft.Maui.Devices.Sensors.IGeocoding>(geocoding);
        InstallEssentials();
        // Generic focus hook for non-text views (the shared ViewHandler Focus/Unfocus commands)
        // and the internal hardware-key surface; MAUI rc.1 has no key contract, so the key
        // listener stays internal (see OpenHarmonyKeyListener for what a public surface needs).
        OpenHarmonyFocusManager.Install();
        OpenHarmonyKeyListener.Install();
        // MAUI animations (FadeTo/TranslateTo/...): the ticker drives the animation manager.
        builder.Services.AddSingleton<Microsoft.Maui.Animations.ITicker, OpenHarmonyTicker>();
        builder.Services.AddSingleton<Microsoft.Maui.Animations.IAnimationManager, Microsoft.Maui.Animations.AnimationManager>();
        builder.Services.AddSingleton<OpenHarmonyWindowSurface>();
        builder.Services.AddSingleton<OpenHarmonyWindowRenderer>();
        builder.Services.AddSingleton<OpenHarmonyMauiAppHost>();
        // Missing MAUI platform interfaces (audit): IPlatformApplication.Current is published by
        // MauiAppBuilder.Build() through the initializer (which receives the app's service
        // provider), and the overlay host installs the IWindowOverlay frame hook/registry. See
        // OpenHarmonyMauiApplication.cs and OpenHarmonyWindowOverlay.cs.
        builder.Services.AddSingleton<OpenHarmonyMauiApplication>();
        builder.Services.AddSingleton<Microsoft.Maui.Hosting.IMauiInitializeService, OpenHarmonyMauiApplicationInitializer>();
        builder.ConfigureMauiHandlers(handlers =>
        {
            foreach (KeyValuePair<Type, SliceHandlerRegistration> entry in SliceHandlers)
            {
                // entry.Value.HandlerType carries the DynamicallyAccessedMembers contract, so
                // AddHandler's own requirement is satisfied without a warning.
                handlers.AddHandler(entry.Key, entry.Value.HandlerType);
            }
        });
        return builder;
    }
}
