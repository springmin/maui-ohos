// Shared base for OpenHarmony view handlers: keeps the platform view pointed at its virtual
// view so the compositor can read arranged frames and route taps.
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public abstract class OpenHarmonyViewHandler<TVirtualView> : ViewHandler<TVirtualView, OpenHarmonyView>
    where TVirtualView : class, IView
{
    protected OpenHarmonyViewHandler(IPropertyMapper mapper) : base(mapper) { }

    protected OpenHarmonyViewHandler(IPropertyMapper mapper, CommandMapper commandMapper)
        : base(mapper, commandMapper) { }

    public override void SetVirtualView(IView view)
    {
        base.SetVirtualView(view);
        if (PlatformView is not null && view is TVirtualView typed)
        {
            PlatformView.VirtualView = typed;
        }
    }

    /// <summary>
    /// Marks the layout version on every layout-affecting command before MAUI dispatches it.
    /// The slice routes virtual measure invalidations (<c>IView.InvalidateMeasure</c>) and
    /// structural layout edits (<c>ILayoutHandler.Add/Remove/Clear/Insert/Update</c>) through the
    /// handler, so this is the complete change signal the compositor's Render gate samples; the
    /// mapped commands themselves still run through the normal dispatch below.
    /// </summary>
    public override void Invoke(string command, object? args)
    {
        if (command is nameof(IView.InvalidateMeasure)
            or nameof(ILayoutHandler.Add)
            or nameof(ILayoutHandler.Remove)
            or nameof(ILayoutHandler.Clear)
            or nameof(ILayoutHandler.Insert)
            or nameof(ILayoutHandler.Update))
        {
            OpenHarmonyLayoutInvalidation.Mark();
        }
        base.Invoke(command, args);
    }
}
