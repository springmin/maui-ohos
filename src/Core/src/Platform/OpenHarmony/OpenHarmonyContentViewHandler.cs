// ContentView/TemplatedView handler for OpenHarmony: the content is arranged inside the view's
// frame (MAUI has no platform layout for them without a handler, so their children would stay
// unarranged otherwise).
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyContentViewHandler : OpenHarmonyViewHandler<TemplatedView>
{
    public static readonly IPropertyMapper<TemplatedView, OpenHarmonyContentViewHandler> Mapper =
        new PropertyMapper<TemplatedView, OpenHarmonyContentViewHandler>(ViewMapper);

    public OpenHarmonyContentViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    private IView? Content => (VirtualView as IContentView)?.PresentedContent as IView;

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => Content is { } content
            ? content.Measure(widthConstraint, heightConstraint)
            : base.GetDesiredSize(widthConstraint, heightConstraint);

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        if (Content is { } content)
        {
            OpenHarmonyHandlerConnector.ConnectTree(content);
            content.Measure(frame.Width, frame.Height);
            content.Arrange(new Rect(frame.X, frame.Y, frame.Width, frame.Height));
        }
    }
}
