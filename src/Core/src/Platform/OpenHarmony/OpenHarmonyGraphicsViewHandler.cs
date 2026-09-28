// GraphicsView handler for OpenHarmony: the IDrawable paints through the compositor's canvas and
// the compositor routes press/drag/hover/cancel into the IGraphicsView interaction contract
// (Controls' GraphicsView raises StartInteraction / DragInteraction / EndInteraction /
// CancelInteraction / *HoverInteraction from these calls).
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyGraphicsViewHandler : OpenHarmonyViewHandler<IGraphicsView>
{
    public static readonly IPropertyMapper<IGraphicsView, OpenHarmonyGraphicsViewHandler> Mapper =
        new PropertyMapper<IGraphicsView, OpenHarmonyGraphicsViewHandler>(ViewMapper);

    /// <summary>Commands the Controls GraphicsView invokes on its handler.</summary>
    public static readonly CommandMapper<IGraphicsView, OpenHarmonyGraphicsViewHandler> CommandMapper =
        new(ViewCommandMapper)
        {
            [nameof(IGraphicsView.Invalidate)] = MapInvalidate,
        };

    public OpenHarmonyGraphicsViewHandler() : base(Mapper, CommandMapper) { }

    /// <summary>IGraphicsView.Invalidate: the drawable asked the compositor for a repaint.</summary>
    public static void MapInvalidate(OpenHarmonyGraphicsViewHandler handler, IGraphicsView graphicsView, object? arg)
    {
        OpenHarmonyBridge.RequestRedraw();
    }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsGraphicsView = true };
        view.GraphicsStartInteraction = points => VirtualView?.StartInteraction(points);
        view.GraphicsDragInteraction = points => VirtualView?.DragInteraction(points);
        view.GraphicsEndInteraction = (points, isInsideBounds) => VirtualView?.EndInteraction(points, isInsideBounds);
        view.GraphicsCancelInteraction = () => VirtualView?.CancelInteraction();
        view.GraphicsHoverStart = points => VirtualView?.StartHoverInteraction(points);
        view.GraphicsHoverMove = points => VirtualView?.MoveHoverInteraction(points);
        view.GraphicsHoverEnd = () => VirtualView?.EndHoverInteraction();
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var element = VirtualView as Microsoft.Maui.Controls.VisualElement;
        double width = element?.WidthRequest > 0 ? element.WidthRequest : Math.Min(200, widthConstraint);
        double height = element?.HeightRequest > 0 ? element.HeightRequest : Math.Min(200, heightConstraint);
        return new Size(Math.Min(width, widthConstraint), Math.Min(height, heightConstraint));
    }
}
