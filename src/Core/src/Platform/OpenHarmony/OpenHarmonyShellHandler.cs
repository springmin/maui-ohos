// Shell handler for OpenHarmony: renders Shell.CurrentPage with a bottom bar for the shell
// items. Shell's own navigation manager (GoToAsync/routes) runs in managed code; this handler
// only has to show the current page and let the bar switch items.
using System.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyShellHandler : OpenHarmonyViewHandler<Shell>
{
    public static readonly IPropertyMapper<Shell, OpenHarmonyShellHandler> Mapper =
        new PropertyMapper<Shell, OpenHarmonyShellHandler>(ViewMapper)
        {
            [nameof(Shell.CurrentItem)] = MapShell,
            [nameof(Shell.CurrentPage)] = MapShell,
        };

    public OpenHarmonyShellHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        // The bottom bar reuses the tabbed page chrome (titles + selection).
        var view = new OpenHarmonyView { IsTabbedPage = true, Background = Colors.Black, TextColor = Colors.White, FontSize = 24 };
        view.TabSelected = index =>
        {
            if (VirtualView is { } shell && index >= 0 && index < shell.Items.Count)
            {
                shell.CurrentItem = shell.Items[index];
            }
        };
        view.ChromeRefresh = () => MapShell(this, VirtualView!);
        view.BackTapped = () =>
        {
            if (VirtualView is { } shell)
            {
                // The shell section owns the navigation stack.
                var navigation = shell.CurrentItem?.CurrentItem?.Navigation ?? shell.Navigation;
                _ = navigation.PopAsync();
            }
        };
        view.FlyoutRequested = () =>
        {
            view.FlyoutOpen = true;
            OpenHarmonyBridge.RequestRedraw();
        };
        view.FlyoutSelect = index =>
        {
            view.FlyoutOpen = false;
            if (VirtualView is { } shell && index >= 0 && index < shell.Items.Count)
            {
                shell.CurrentItem = shell.Items[index];
            }
            OpenHarmonyBridge.RequestRedraw();
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } shell)
        {
            shell.PropertyChanged += OnShellPropertyChanged;
            shell.Navigated += OnShellNavigated;
        }
        MapShell(this, VirtualView!);
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } shell)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            shell.Navigated -= OnShellNavigated;
        }
        base.DisconnectHandler(platformView);
    }

    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs args)
    {
        MapShell(this, VirtualView!);
        ArrangeContent();
        OpenHarmonyBridge.RequestRedraw();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Shell.CurrentItem) or nameof(Shell.CurrentPage))
        {
            MapShell(this, VirtualView!);
            ArrangeContent();
            OpenHarmonyBridge.RequestRedraw();
        }
    }

    public static void MapShell(OpenHarmonyShellHandler handler, Shell shell)
    {
        OpenHarmonyView view = handler.PlatformView;
        view.TabTitles.Clear();
        foreach (ShellItem item in shell.Items)
        {
            view.TabTitles.Add(item.Title ?? string.Empty);
        }
        int index = shell.CurrentItem is { } current ? shell.Items.IndexOf(current) : -1;
        view.SelectedTab = Math.Max(0, index);
        // Chrome: title bar with the current page's title and a back button when the shell can
        // navigate back.
        view.ShowsTitleBar = shell.GetValue(Microsoft.Maui.Controls.Shell.NavBarIsVisibleProperty) is not bool navBar || navBar;
        view.TitleText = shell.CurrentPage?.Title ?? shell.CurrentItem?.Title ?? string.Empty;
        view.ShowsBack = CurrentStackDepth(shell) > 1;
        if (view.ShowsBack)
        {
            view.ShowsHamburger = false;
        }
        // Flyout (hamburger + drawer) shows the shell items.
        view.ShowsHamburger = shell.FlyoutBehavior != FlyoutBehavior.Disabled;
        view.FlyoutItems.Clear();
        view.TabIcons.Clear();
        foreach (ShellItem item in shell.Items)
        {
            view.FlyoutItems.Add(item.Title ?? string.Empty);
            view.TabIcons.Add(ResolveIcon(item.Icon));
        }
        // Shell contents are created lazily: realise the current one so it can be rendered.
        if (shell.CurrentPage is null &&
            shell.CurrentItem?.CurrentItem is ShellSection { CurrentItem: ShellContent content } &&
            content is IShellContentController controller)
        {
            controller.GetOrCreateContent();
        }
        if (shell.CurrentPage is IView page)
        {
            OpenHarmonyHandlerConnector.ConnectTree(page);
        }
    }

    /// <summary>Loads a file-based tab icon; other source kinds need the image service.</summary>
    private static byte[]? ResolveIcon(Microsoft.Maui.Controls.ImageSource? icon)
    {
        try
        {
            if (icon is Microsoft.Maui.Controls.FileImageSource file && !string.IsNullOrEmpty(file.File) && File.Exists(file.File))
            {
                return File.ReadAllBytes(file.File);
            }
        }
        catch
        {
            // Icons are optional.
        }
        return null;
    }

    private static int CurrentStackDepth(Shell shell)
        => shell.CurrentItem?.CurrentItem?.Navigation?.NavigationStack?.Count
           ?? shell.CurrentPage?.Navigation?.NavigationStack?.Count
           ?? 1;

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
        if (VirtualView?.CurrentPage is not IView content)
        {
            return;
        }
        Rect frame = PlatformView.Frame;
        OpenHarmonyHandlerConnector.ConnectTree(content);
        bool tabBar = VirtualView?.GetValue(Microsoft.Maui.Controls.Shell.TabBarIsVisibleProperty) is not bool visible || visible;
        PlatformView.TabTitlesVisible = tabBar;
        double top = PlatformView.ShowsTitleBar ? OpenHarmonyView.TitleBarHeight : 0;
        double bottom = tabBar ? OpenHarmonyView.TabBarHeight : 0;
        var contentFrame = new Rect(frame.X, frame.Y + top, frame.Width,
            Math.Max(0, frame.Height - bottom - top));
        content.Measure(contentFrame.Width, contentFrame.Height);
        content.Arrange(contentFrame);
    }
}
