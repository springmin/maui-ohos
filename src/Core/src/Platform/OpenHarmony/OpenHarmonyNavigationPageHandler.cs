// NavigationPage handler for OpenHarmony: draws a navigation bar with the current page and a
// back button that pops the stack.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyNavigationPageHandler : OpenHarmonyViewHandler<NavigationPage>
{
    public static readonly IPropertyMapper<NavigationPage, OpenHarmonyNavigationPageHandler> Mapper =
        new PropertyMapper<NavigationPage, OpenHarmonyNavigationPageHandler>(ViewMapper)
        {
            [nameof(NavigationPage.CurrentPage)] = MapCurrentPage,
            [nameof(NavigationPage.BarBackgroundColor)] = MapBarBackgroundColor,
            [nameof(NavigationPage.BarTextColor)] = MapBarTextColor,
        };

    public OpenHarmonyNavigationPageHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsNavigationPage = true };
        view.BackTapped = OnBackTapped;
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } navigationPage)
        {
            navigationPage.Pushed += OnStackChanged;
            navigationPage.Popped += OnStackChanged;
        }
        UpdateNavigationBar();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } navigationPage)
        {
            navigationPage.Pushed -= OnStackChanged;
            navigationPage.Popped -= OnStackChanged;
        }
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// MAUI's navigation pipeline (MauiNavigationImpl) asks the platform handler to perform a
    /// navigation. There is no transition animation here, so the new stack is reported back
    /// immediately - that call also completes the pending <c>PushAsync</c>/<c>PopAsync</c>.
    /// </summary>
    public override void Invoke(string command, object? args)
    {
        if (command == nameof(IStackNavigation.RequestNavigation) && args is NavigationRequest request)
        {
            if (VirtualView is IStackNavigation navigation)
            {
                navigation.NavigationFinished(request.NavigationStack);
            }
            UpdateNavigationBar();
            OpenHarmonyBridge.RequestRedraw();
            return;
        }
        base.Invoke(command, args);
    }

    private void OnStackChanged(object? sender, NavigationEventArgs args)
    {
        UpdateNavigationBar();
        OpenHarmonyBridge.RequestRedraw();
    }

    private void UpdateNavigationBar()
    {
        PlatformView.NavTitle = VirtualView?.CurrentPage?.Title;
        PlatformView.CanGoBack = (VirtualView?.Navigation?.NavigationStack?.Count ?? 1) > 1;
    }

    private void OnBackTapped()
    {
        if (VirtualView is { } navigationPage)
        {
            _ = navigationPage.PopAsync();
        }
    }

    public static void MapCurrentPage(OpenHarmonyNavigationPageHandler handler, NavigationPage navigationPage)
        => handler.UpdateNavigationBar();

    public static void MapBarBackgroundColor(OpenHarmonyNavigationPageHandler handler, NavigationPage navigationPage)
    {
        if (navigationPage.BarBackgroundColor is not null)
        {
            handler.PlatformView.NavBarColor = navigationPage.BarBackgroundColor;
        }
    }

    public static void MapBarTextColor(OpenHarmonyNavigationPageHandler handler, NavigationPage navigationPage)
    {
        if (navigationPage.BarTextColor is not null)
        {
            handler.PlatformView.NavBarTextColor = navigationPage.BarTextColor;
        }
    }
}
