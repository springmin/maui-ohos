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
    /// </summary>
    internal static readonly Dictionary<Type, Type> SliceHandlers = new()
    {
        [typeof(ILabel)] = typeof(OpenHarmonyLabelHandler),
        [typeof(IButton)] = typeof(OpenHarmonyButtonHandler),
        [typeof(ILayout)] = typeof(OpenHarmonyLayoutHandler),
        [typeof(IWindow)] = typeof(OpenHarmonyWindowHandler),
        [typeof(IEntry)] = typeof(OpenHarmonyEntryHandler),
        [typeof(Microsoft.Maui.IImage)] = typeof(OpenHarmonyImageHandler),
        [typeof(IScrollView)] = typeof(OpenHarmonyScrollViewHandler),
        [typeof(ICheckBox)] = typeof(OpenHarmonyCheckBoxHandler),
        [typeof(ISwitch)] = typeof(OpenHarmonySwitchHandler),
        [typeof(ISlider)] = typeof(OpenHarmonySliderHandler),
        [typeof(IProgress)] = typeof(OpenHarmonyProgressBarHandler),
        [typeof(IActivityIndicator)] = typeof(OpenHarmonyActivityIndicatorHandler),
        [typeof(Microsoft.Maui.Controls.NavigationPage)] = typeof(OpenHarmonyNavigationPageHandler),
        [typeof(Microsoft.Maui.Controls.Page)] = typeof(OpenHarmonyPageHandler),
        [typeof(Microsoft.Maui.Controls.CollectionView)] = typeof(OpenHarmonyCollectionViewHandler),
        [typeof(IShapeView)] = typeof(OpenHarmonyShapeHandler),
        [typeof(IBorderView)] = typeof(OpenHarmonyBorderHandler),
        [typeof(IStepper)] = typeof(OpenHarmonyStepperHandler),
        [typeof(IRadioButton)] = typeof(OpenHarmonyRadioButtonHandler),
        [typeof(ISearchBar)] = typeof(OpenHarmonySearchBarHandler),
        [typeof(IPicker)] = typeof(OpenHarmonyPickerHandler),
        [typeof(IDatePicker)] = typeof(OpenHarmonyDatePickerHandler),
        [typeof(ITimePicker)] = typeof(OpenHarmonyTimePickerHandler),
        [typeof(Microsoft.Maui.Controls.TabbedPage)] = typeof(OpenHarmonyTabbedPageHandler),
        [typeof(Microsoft.Maui.Controls.FlyoutPage)] = typeof(OpenHarmonyFlyoutPageHandler),
        [typeof(Microsoft.Maui.Controls.Shell)] = typeof(OpenHarmonyShellHandler),
        [typeof(Microsoft.Maui.Controls.ListView)] = typeof(OpenHarmonyListViewHandler),
        [typeof(Microsoft.Maui.Controls.CarouselView)] = typeof(OpenHarmonyCarouselViewHandler),
    };

    /// <summary>
    /// Wires the Essentials statics to the OpenHarmony implementations. MAUI keeps
    /// Preferences.Current / FileSystem.Current as internal members set by the platform
    /// assembly, which does not exist for this slice, so they are assigned reflectively.
    /// </summary>
    private static void InstallEssentials(Microsoft.Maui.Storage.IPreferences preferences,
                                           Microsoft.Maui.Storage.IFileSystem fileSystem,
                                           Microsoft.Maui.Storage.ISecureStorage secureStorage,
                                           Microsoft.Maui.ApplicationModel.IAppInfo appInfo,
                                           Microsoft.Maui.Devices.IDeviceInfo deviceInfo,
                                           Microsoft.Maui.ApplicationModel.IVersionTracking versionTracking)
    {
        const System.Reflection.BindingFlags Static =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        try
        {
            typeof(Microsoft.Maui.Storage.Preferences).GetProperty("Current", Static)
                ?.SetValue(null, preferences);
            typeof(Microsoft.Maui.Storage.FileSystem).GetProperty("Current", Static)
                ?.SetValue(null, fileSystem);
            typeof(Microsoft.Maui.Storage.SecureStorage).GetProperty("Current", Static)
                ?.SetValue(null, secureStorage);
            typeof(Microsoft.Maui.ApplicationModel.AppInfo).GetProperty("Current", Static)
                ?.SetValue(null, appInfo);
            typeof(Microsoft.Maui.Devices.DeviceInfo).GetProperty("Current", Static)
                ?.SetValue(null, deviceInfo);
            typeof(Microsoft.Maui.ApplicationModel.VersionTracking).GetProperty("Current", Static)
                ?.SetValue(null, versionTracking);
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
                $"[maui] essentials wiring failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Registers the OpenHarmony platform services (dispatcher + window surface) and the
    /// handler set with the MAUI app builder.
    /// </summary>
    public static MauiAppBuilder UseOpenHarmony(this MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<IDispatcher, OpenHarmonyDispatcher>();
        builder.Services.AddSingleton<Microsoft.Maui.Dispatching.IDispatcherProvider, OpenHarmonyDispatcherProvider>();
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
        builder.Services.AddSingleton<Microsoft.Maui.Storage.ISecureStorage>(secureStorage);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IAppInfo>(appInfo);
        builder.Services.AddSingleton<Microsoft.Maui.Devices.IDeviceInfo>(deviceInfo);
        builder.Services.AddSingleton<Microsoft.Maui.ApplicationModel.IVersionTracking>(versionTracking);
        InstallEssentials(preferences, fileSystem, secureStorage, appInfo, deviceInfo, versionTracking);
        // MAUI animations (FadeTo/TranslateTo/...): the ticker drives the animation manager.
        builder.Services.AddSingleton<Microsoft.Maui.Animations.ITicker, OpenHarmonyTicker>();
        builder.Services.AddSingleton<Microsoft.Maui.Animations.IAnimationManager, Microsoft.Maui.Animations.AnimationManager>();
        builder.Services.AddSingleton<OpenHarmonyWindowSurface>();
        builder.Services.AddSingleton<OpenHarmonyWindowRenderer>();
        builder.Services.AddSingleton<OpenHarmonyMauiAppHost>();
        builder.ConfigureMauiHandlers(handlers =>
        {
            foreach (KeyValuePair<Type, Type> entry in SliceHandlers)
            {
                handlers.AddHandler(entry.Key, entry.Value);
            }
        });
        return builder;
    }
}
