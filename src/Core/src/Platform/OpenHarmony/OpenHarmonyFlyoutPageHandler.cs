// FlyoutPage handler for OpenHarmony: the detail fills the window, the flyout slides in as a
// left panel over a scrim - the slice's only presentation, so a default layout behavior is
// stated as FlyoutLayoutBehavior.Popover (see ConnectHandler); the renderer draws/hit-tests the
// panel and the hamburger.
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
            // The compositor draws the flyout as a sliding overlay (there is no docked/split
            // layout). MAUI resolves the default layout behavior against the idiom and the
            // display orientation, and on a tablet-sized landscape surface that means split
            // mode: IsPresented is pinned true (UpdateFlyoutLayoutBehavior force-presents it)
            // and a dismiss write-back throws "Can't change IsPresented when setting Default"
            // - the device drawer opened but its outside tap could never close it (the touch
            // callback reports the throw and the panel stays). State the presentation this
            // slice actually renders so MAUI's model agrees with the compositor; an explicit
            // app value (Split/Popover/...) is left alone.
            if (flyoutPage.FlyoutLayoutBehavior == FlyoutLayoutBehavior.Default)
            {
                flyoutPage.FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover;
            }
            flyoutPage.IsPresentedChanged += OnIsPresentedChanged;
            // FIX-BACKSIZE: the shell forwards the system Back press through the hosting bridge
            // (onBackPress -> host.backPressed -> OpenHarmonyBridge.BackPressed). Close the
            // presented drawer by writing the managed property back (the same path the scrim
            // dismiss uses) and answer true so the press is consumed; with the drawer closed the
            // default false lets the system background the app.
            OpenHarmonyBridge.BackPressed += OnBackPressed;
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
            OpenHarmonyBridge.BackPressed -= OnBackPressed;
        }
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// System Back (hosting bridge event): a presented drawer is closed through the managed
    /// property so the platform view and the overlay suspension follow, and the press reports
    /// true (consumed). A closed drawer reports false and the system keeps its default.
    /// </summary>
    private bool OnBackPressed()
    {
        if (VirtualView is { IsPresented: true } flyoutPage)
        {
            flyoutPage.IsPresented = false;
            return true;
        }
        return false;
    }

    private void OnIsPresentedChanged(object? sender, EventArgs args)
    {
        MapIsPresented(this, VirtualView!);
        // FIX-WVP: the single ArkWeb overlay renders above the managed surface, so the drawer
        // the compositor draws would be covered by a visible web page. Suspend the overlay while
        // the drawer is open (the shell then ignores frame/load re-shows) and resume its previous
        // visibility when it closes, so a drawer on a page without a web control cannot make a
        // stale overlay reappear.
        if (VirtualView is { IsPresented: false })
        {
            OpenHarmonyBridge.WebCommand("resume");
        }
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
    {
        handler.PlatformView.FlyoutPresented = flyoutPage.IsPresented;
        if (flyoutPage.IsPresented)
        {
            // FIX-WVP: the ArkWeb overlay sits above the managed surface; suspend it while the
            // compositor-drawn drawer is open (also covers a drawer that starts presented). The
            // flyout handler's own arrange cycle re-emits the detail's web frame, which the
            // shell ignores while suspended.
            OpenHarmonyBridge.WebCommand("suspend");
        }
    }

    public static void MapContent(OpenHarmonyFlyoutPageHandler handler, FlyoutPage flyoutPage)
    {
        OpenHarmonyHandlerConnector.ConnectTree(flyoutPage.Flyout);
        OpenHarmonyHandlerConnector.ConnectTree(flyoutPage.Detail);
        handler.ArrangeContent();
    }
}
