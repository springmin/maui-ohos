// Generic page handler for OpenHarmony: pages draw nothing themselves, but they must exist as
// handlers so MAUI arranges their content (ContentPage/NavigationPage/... all derive from Page).
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyPageHandler : OpenHarmonyViewHandler<Page>
{
    public static readonly IPropertyMapper<Page, OpenHarmonyPageHandler> Mapper =
        new PropertyMapper<Page, OpenHarmonyPageHandler>(ViewMapper);

    public OpenHarmonyPageHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    private IView? PageContent => (VirtualView as IContentView)?.PresentedContent as IView;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => PageContent is { } content
            ? content.Measure(widthConstraint, heightConstraint)
            : base.GetDesiredSize(widthConstraint, heightConstraint);

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (PageContent is { } content)
        {
            content.Measure(frame.Width, frame.Height);
            content.Arrange(new Rect(frame.X, frame.Y, frame.Width, frame.Height));
        }
    }
}
