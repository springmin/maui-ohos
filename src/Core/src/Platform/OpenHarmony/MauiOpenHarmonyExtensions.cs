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
    };

    /// <summary>
    /// Registers the OpenHarmony platform services (dispatcher + window surface) and the
    /// handler set with the MAUI app builder.
    /// </summary>
    public static MauiAppBuilder UseOpenHarmony(this MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<IDispatcher, OpenHarmonyDispatcher>();
        builder.Services.AddSingleton<Microsoft.Maui.Dispatching.IDispatcherProvider, OpenHarmonyDispatcherProvider>();
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
