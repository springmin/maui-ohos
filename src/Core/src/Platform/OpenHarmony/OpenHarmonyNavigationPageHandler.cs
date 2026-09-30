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

    /// <summary>
    /// Arranges the current page below the navigation bar. A parent container handler
    /// (FlyoutPage/TabbedPage ArrangeContent, a tab switch) arranges the navigation page
    /// through IView.Arrange, which lands here; without this descent the current page's
    /// subtree never gets a frame and draws nothing (the Home tab black-screen defect).
    /// The page-aware walks arrange the current page themselves and call back through
    /// IView.Arrange for the container, which the arrange marker suppresses.
    /// </summary>
    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (VirtualView is not IView navigationPage)
        {
            return;
        }
        if (OpenHarmonyContentArrange.IsArranging(navigationPage))
        {
            return;
        }
        Thickness insets = OpenHarmonySafeArea.GetWindowInsets();
        if (!OpenHarmonySafeArea.IsEmpty(insets) &&
            OpenHarmonyBridge.Surface is { Width: > 0, Height: > 0 } surface)
        {
            OpenHarmonySafeAreaArrange.Arrange(navigationPage, frame,
                new Rect(0, 0, surface.Width, surface.Height), insets);
            return;
        }
        OpenHarmonyContentArrange.Arrange(navigationPage, frame);
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } navigationPage)
        {
            navigationPage.Pushed += OnPushed;
            navigationPage.Popped += OnPopped;
        }
        UpdateNavigationBar();
        _previousPage = VirtualView?.CurrentPage;
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } navigationPage)
        {
            navigationPage.Pushed -= OnPushed;
            navigationPage.Popped -= OnPopped;
        }
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// MAUI's navigation pipeline (MauiNavigationImpl) asks the platform handler to perform a
    /// navigation. The stack is reported back immediately (that call also completes the pending
    /// PushAsync/PopAsync); the visible page then enters through the shared frame loop (see
    /// OpenHarmonyPageTransitions) while the outgoing page's "shared:" elements are captured in
    /// OnStackChanged for the minimal shared transition.
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

    /// <summary>The page that was current at the last stack change (the outgoing page).</summary>
    private Page? _previousPage;

    private void OnPushed(object? sender, NavigationEventArgs args) => OnStackChanged(forward: true);

    private void OnPopped(object? sender, NavigationEventArgs args) => OnStackChanged(forward: false);

    private void OnStackChanged(bool forward)
    {
        UpdateNavigationBar();
        OpenHarmonyBridge.RequestRedraw();
        // The outgoing page is the one that was current before this event: CurrentPage already
        // points at the incoming page here (MAUI commits the stack before raising Pushed/Popped).
        Page? outgoing = _previousPage;
        Page? current = VirtualView?.CurrentPage;
        _previousPage = current;
        OpenHarmonySharedTransition.Capture(outgoing);
        OpenHarmonySharedTransition.Run(current);
        OpenHarmonyPageTransitions.Enter(current, forward);
    }

    private Page? _toolbarPage;

    private void UpdateNavigationBar()
    {
        PlatformView.NavTitle = VirtualView?.CurrentPage?.Title;
        PlatformView.CanGoBack = (VirtualView?.Navigation?.NavigationStack?.Count ?? 1) > 1;
        UpdateToolbar();
    }

    /// <summary>
    /// Mirrors the current page's ToolbarItems into the platform bar and keeps them in sync
    /// (collection changes and item property changes rebuild the item list).
    /// </summary>
    private void UpdateToolbar()
    {
        Page? current = VirtualView?.CurrentPage;
        if (!ReferenceEquals(_toolbarPage, current))
        {
            if (_toolbarPage is { } previous)
            {
                if (previous.ToolbarItems is System.Collections.Specialized.INotifyCollectionChanged previousCollection)
                {
                    previousCollection.CollectionChanged -= OnToolbarCollectionChanged;
                }
                foreach (ToolbarItem item in previous.ToolbarItems)
                {
                    item.PropertyChanged -= OnToolbarItemChanged;
                }
            }
            _toolbarPage = current;
            if (current is not null)
            {
                if (current.ToolbarItems is System.Collections.Specialized.INotifyCollectionChanged collection)
                {
                    collection.CollectionChanged += OnToolbarCollectionChanged;
                }
                foreach (ToolbarItem item in current.ToolbarItems)
                {
                    item.PropertyChanged += OnToolbarItemChanged;
                }
            }
        }
        PlatformView.ToolbarItems.Clear();
        if (current is not null)
        {
            foreach (ToolbarItem item in current.ToolbarItems)
            {
                ToolbarItem captured = item;
                PlatformView.ToolbarItems.Add((captured.Text ?? string.Empty, () => ActivateToolbarItem(captured)));
            }
        }
    }

    private void OnToolbarCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        // Re-read the page so the per-item subscriptions follow the collection.
        if (VirtualView?.CurrentPage is { } current && ReferenceEquals(_toolbarPage, current))
        {
            foreach (ToolbarItem item in current.ToolbarItems)
            {
                item.PropertyChanged -= OnToolbarItemChanged;
                item.PropertyChanged += OnToolbarItemChanged;
            }
        }
        UpdateToolbar();
        OpenHarmonyBridge.RequestRedraw();
    }

    private void OnToolbarItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // Text/enabled changes are mirrored by rebuilding the platform item list.
        UpdateToolbar();
        OpenHarmonyBridge.RequestRedraw();
    }

    private static void ActivateToolbarItem(ToolbarItem item)
    {
        if (!item.IsEnabled)
        {
            return;
        }
        if (item is IMenuItemController controller)
        {
            controller.Activate();
        }
        else
        {
            item.Command?.Execute(item.CommandParameter);
        }
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
