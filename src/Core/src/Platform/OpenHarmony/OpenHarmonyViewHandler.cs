// Shared base for OpenHarmony view handlers: keeps the platform view pointed at its virtual
// view so the compositor can read arranged frames and route taps.
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public abstract class OpenHarmonyViewHandler<TVirtualView> : ViewHandler<TVirtualView, OpenHarmonyView>
    where TVirtualView : class, IView
{
    protected OpenHarmonyViewHandler(IPropertyMapper mapper) : base(mapper) { }

    public override void SetVirtualView(IView view)
    {
        base.SetVirtualView(view);
        if (PlatformView is not null && view is TVirtualView typed)
        {
            PlatformView.VirtualView = typed;
        }
    }
}
