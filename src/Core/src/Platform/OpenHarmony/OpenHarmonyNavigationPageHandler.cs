// NavigationPage handler for OpenHarmony: draws a navigation bar with the current page and a
// back button that pops the stack.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
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
            // MAUI's navigation pipeline asks the platform to perform the transition and to
            // complete the pending navigation: there is no animation here, so completion is
            // immediate.
            var controller = (INavigationPageController)navigationPage;
            controller.PushRequested += OnPushRequested;
            controller.PopRequested += OnPopRequested;
            controller.PopToRootRequested += OnPopToRootRequested;
        }
        UpdateNavigationBar();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } navigationPage)
        {
            navigationPage.Pushed -= OnStackChanged;
            navigationPage.Popped -= OnStackChanged;
            var controller = (INavigationPageController)navigationPage;
            controller.PushRequested -= OnPushRequested;
            controller.PopRequested -= OnPopRequested;
            controller.PopToRootRequested -= OnPopToRootRequested;
        }
        base.DisconnectHandler(platformView);
    }

    private void OnPushRequested(object? sender, NavigationRequestedEventArgs args)
    {
        var stack = new List<IView>(CurrentStack());
        if (args.Page is IView pushed)
        {
            stack.Add(pushed);
        }
        Finish(stack, args);
    }

    private void OnPopRequested(object? sender, NavigationRequestedEventArgs args)
    {
        var stack = new List<IView>(CurrentStack());
        if (stack.Count > 1)
        {
            stack.RemoveAt(stack.Count - 1);
        }
        Finish(stack, args);
    }

    private void OnPopToRootRequested(object? sender, NavigationRequestedEventArgs args)
    {
        var stack = new List<IView>(CurrentStack());
        if (stack.Count > 1)
        {
            stack.RemoveRange(1, stack.Count - 1);
        }
        Finish(stack, args);
    }

    private IEnumerable<IView> CurrentStack()
        => VirtualView?.Navigation?.NavigationStack?.Cast<IView>() ?? Enumerable.Empty<IView>();

    private void Finish(IReadOnlyList<IView> stack, NavigationRequestedEventArgs args)
    {
        // There is no transition animation, so the platform side is finished immediately. NOTE:
        // MAUI's stock navigation pipeline still waits for a platform NavigationView handler to
        // report its own completion, so `await PushAsync(...)`/`await PopAsync(...)` do not
        // complete on this slice yet even though the navigation itself happens (the stack, the
        // bar and the rendered page are updated). Tracked for the navigation phase.
        if (VirtualView is IStackNavigation navigation)
        {
            navigation.NavigationFinished(stack);
        }
        args.Task = Task.FromResult(true);
        UpdateNavigationBar();
        OpenHarmonyBridge.RequestRedraw();
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if (VirtualView?.CurrentPage is IView content)
        {
            double contentHeight = Math.Max(0, heightConstraint - PlatformView.NavBarHeight);
            Size size = content.Measure(widthConstraint, contentHeight);
            return new Size(Math.Min(size.Width, widthConstraint),
                            Math.Min(size.Height + PlatformView.NavBarHeight, heightConstraint));
        }
        return base.GetDesiredSize(widthConstraint, heightConstraint);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (VirtualView?.CurrentPage is IView content)
        {
            // The current page lives below the navigation bar, so no draw-time offset is needed.
            double contentHeight = Math.Max(0, frame.Height - PlatformView.NavBarHeight);
            content.Measure(frame.Width, contentHeight);
            content.Arrange(new Rect(frame.X, frame.Y + PlatformView.NavBarHeight, frame.Width, contentHeight));
        }
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
