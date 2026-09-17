// TabbedPage handler for OpenHarmony: draws a bottom tab bar and shows the selected page.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyTabbedPageHandler : OpenHarmonyViewHandler<Microsoft.Maui.Controls.TabbedPage>
{
    public static readonly IPropertyMapper<Microsoft.Maui.Controls.TabbedPage, OpenHarmonyTabbedPageHandler> Mapper =
        new PropertyMapper<Microsoft.Maui.Controls.TabbedPage, OpenHarmonyTabbedPageHandler>(ViewMapper)
        {
            [nameof(Microsoft.Maui.Controls.MultiPage<Microsoft.Maui.Controls.Page>.CurrentPage)] = MapPages,
        };

    public OpenHarmonyTabbedPageHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsTabbedPage = true, Background = Colors.Black, TextColor = Colors.White, FontSize = 26 };
        view.TabSelected = index => SelectTab(index);
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is { } tabbedPage)
        {
            tabbedPage.CurrentPageChanged += OnCurrentPageChanged;
        }
        MapPages(this, VirtualView!);
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        if (VirtualView is { } tabbedPage)
        {
            tabbedPage.CurrentPageChanged -= OnCurrentPageChanged;
        }
        base.DisconnectHandler(platformView);
    }

    private void OnCurrentPageChanged(object? sender, EventArgs args)
    {
        MapPages(this, VirtualView!);
        ArrangeContent();
        OpenHarmonyBridge.RequestRedraw();
    }

    private void SelectTab(int index)
    {
        if (VirtualView is { } tabbedPage && index >= 0 && index < tabbedPage.Children.Count)
        {
            tabbedPage.CurrentPage = (Microsoft.Maui.Controls.Page)tabbedPage.Children[index];
            // The new page has not been arranged yet.
            ArrangeContent();
            OpenHarmonyBridge.RequestRedraw();
        }
    }

    private void ArrangeContent()
    {
        if (VirtualView?.CurrentPage is IView content)
        {
            OpenHarmonyHandlerConnector.ConnectTree(content);
            Rect frame = PlatformView.Frame;
            var contentFrame = new Rect(frame.X, frame.Y, frame.Width, Math.Max(0, frame.Height - OpenHarmonyView.TabBarHeight));
            content.Measure(contentFrame.Width, contentFrame.Height);
            content.Arrange(contentFrame);
        }
    }

    public static void MapPages(OpenHarmonyTabbedPageHandler handler, Microsoft.Maui.Controls.TabbedPage tabbedPage)
    {
        OpenHarmonyView view = handler.PlatformView;
        view.TabTitles.Clear();
        foreach (Microsoft.Maui.Controls.Page page in tabbedPage.Children)
        {
            view.TabTitles.Add(page.Title ?? string.Empty);
        }
        view.SelectedTab = Math.Max(0, tabbedPage.Children.IndexOf(tabbedPage.CurrentPage));
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(widthConstraint, heightConstraint);

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        ArrangeContent();
    }
}
