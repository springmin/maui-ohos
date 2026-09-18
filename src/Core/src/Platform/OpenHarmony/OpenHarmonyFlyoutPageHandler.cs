// FlyoutPage handler for OpenHarmony: the detail fills the window, the flyout slides in as a
// left panel with a scrim; the renderer draws/nit-tests the panel and the hamburger.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFlyoutPageHandler : OpenHarmonyViewHandler<FlyoutPage>
{
    public static readonly IPropertyMapper<FlyoutPage, OpenHarmonyFlyoutPageHandler> Mapper =
        new PropertyMapper<FlyoutPage, OpenHarmonyFlyoutPageHandler>(ViewMapper)
        {
            [nameof(FlyoutPage.IsPresented)] = MapIsPresented,
            [nameof(FlyoutPage.Flyout)] = MapContent,
            [nameof(FlyoutPage.Detail)] = MapContent,
        };

    public OpenHarmonyFlyoutPageHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsFlyoutPage = true, Background = Colors.Black };
        view.OpenFlyout = () =>
        {
            if (VirtualView is { } flyoutPage)
            {
                flyoutPage.IsPresented = true;
            }
        };
        view.FlyoutDismiss = () =>
        {
            if (VirtualView is { } flyoutPage)
            {
                flyoutPage.IsPresented = false;
            }
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } flyoutPage)
        {
            flyoutPage.IsPresentedChanged += OnIsPresentedChanged;
            OpenHarmonyHandlerConnector.ConnectTree(flyoutPage.Flyout);
            OpenHarmonyHandlerConnector.ConnectTree(flyoutPage.Detail);
        }
        MapIsPresented(this, VirtualView!);
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } flyoutPage)
        {
            flyoutPage.IsPresentedChanged -= OnIsPresentedChanged;
        }
        base.DisconnectHandler(platformView);
    }

    private void OnIsPresentedChanged(object? sender, EventArgs args)
    {
        MapIsPresented(this, VirtualView!);
        ArrangeContent();
        OpenHarmonyBridge.RequestRedraw();
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double height = element?.HeightRequest > 0
            ? element.HeightRequest
            : (double.IsFinite(heightConstraint) ? heightConstraint : 600);
        return new Size(widthConstraint, height);
    }

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        ArrangeContent();
    }

    private void ArrangeContent()
    {
        if (VirtualView is not { } flyoutPage)
        {
            return;
        }
        Rect frame = PlatformView.Frame;
        PlatformView.FlyoutWidth = (float)Math.Min(360, Math.Max(240, frame.Width * 0.4));
        if (flyoutPage.Detail is IView detail)
        {
            OpenHarmonyHandlerConnector.ConnectTree(detail);
            detail.Measure(frame.Width, frame.Height);
            detail.Arrange(new Rect(frame.X, frame.Y, frame.Width, frame.Height));
        }
        if (flyoutPage.Flyout is IView flyout)
        {
            OpenHarmonyHandlerConnector.ConnectTree(flyout);
            flyout.Measure(PlatformView.FlyoutWidth, frame.Height);
            flyout.Arrange(new Rect(frame.X, frame.Y, PlatformView.FlyoutWidth, frame.Height));
        }
    }

    public static void MapIsPresented(OpenHarmonyFlyoutPageHandler handler, FlyoutPage flyoutPage)
        => handler.PlatformView.FlyoutPresented = flyoutPage.IsPresented;

    public static void MapContent(OpenHarmonyFlyoutPageHandler handler, FlyoutPage flyoutPage)
    {
        OpenHarmonyHandlerConnector.ConnectTree(flyoutPage.Flyout);
        OpenHarmonyHandlerConnector.ConnectTree(flyoutPage.Detail);
        handler.ArrangeContent();
    }
}
