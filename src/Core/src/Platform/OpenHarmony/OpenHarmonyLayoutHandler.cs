// Layout handler for OpenHarmony: keeps the compositor's child list in sync with ILayout.
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyLayoutHandler : OpenHarmonyViewHandler<ILayout>, ILayoutHandler
{
    public static readonly IPropertyMapper<ILayout, OpenHarmonyLayoutHandler> Mapper =
        new PropertyMapper<ILayout, OpenHarmonyLayoutHandler>(ViewMapper);

    public OpenHarmonyLayoutHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    // Drive MAUI's cross-platform layout pipeline: Measure/Arrange on the virtual view are
    // routed through the layout's own measure/arrange logic (as the platform handlers do).
    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => VirtualView is Microsoft.Maui.Controls.Layout layout
            ? layout.CrossPlatformMeasure(widthConstraint, heightConstraint)
            : base.GetDesiredSize(widthConstraint, heightConstraint);

    public override void PlatformArrange(Rect frame)
    {
        if (VirtualView is Microsoft.Maui.Controls.Layout layout)
        {
            layout.CrossPlatformArrange(frame);
        }
        else
        {
            base.PlatformArrange(frame);
        }
    }

    public void Add(IView child)
    {
        if (child.Handler?.PlatformView is OpenHarmonyView platformChild && !PlatformView.Children.Contains(platformChild))
        {
            PlatformView.Children.Add(platformChild);
        }
    }

    public void Remove(IView child)
    {
        if (child.Handler?.PlatformView is OpenHarmonyView platformChild)
        {
            PlatformView.Children.Remove(platformChild);
        }
    }

    public void Clear() => PlatformView.Children.Clear();

    public void Insert(int index, IView child)
    {
        if (child.Handler?.PlatformView is not OpenHarmonyView platformChild)
        {
            return;
        }
        PlatformView.Children.Remove(platformChild);
        if (index < 0 || index > PlatformView.Children.Count)
        {
            PlatformView.Children.Add(platformChild);
        }
        else
        {
            PlatformView.Children.Insert(index, platformChild);
        }
    }

    public void Update(int index, IView child) => Insert(index, child);

    public void UpdateZIndex(IView child)
    {
        // Children keep insertion order; z-order is not modelled by the compositor yet.
    }
}
