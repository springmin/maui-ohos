// MODAL-OVERLAY: the shell's web-overlay suspension is one global pair of commands (`suspend`
// hides every main-window ArkWeb overlay, `resume` restores the visibility vector the suspend
// remembered). FIX-WVP uses it for a compositor-drawn drawer: the drawer renders in the managed
// surface under the overlays, so every overlay hides while it is presented. A modal pushed over
// such a drawer replaces the whole window content - the drawer is not rendered any more - but the
// drawer's IsPresented does not change across that transition, so nothing lifted the suspension:
// every frame/load/data/show the modal page's own overlay issued was suppressed (the shell keeps
// webVisible false while suspended) and only the managed placeholder was on screen. The device
// form of the defect: a 2in1 / landscape tablet where a Default FlyoutPage is pinned presented by
// MAUI's split-mode rule, so the drawer is open at startup and every modal (WebView, media
// element) painted its placeholder only.
//
// This state machine owns the one command pair. It suspends while a presented primary-window
// drawer is on screen, lifts the suspension while a modal is on top (the drawer is off screen),
// and re-arms it when the modal pops. Commands are emitted only on a state change, so a modal
// transition without a presented drawer sends nothing at all (the historical behavior).
using Microsoft.Maui.Controls;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyOverlaySuspension
{
    private static readonly object s_sync = new();
    // The presented primary-window drawers. Weak, so a torn-down page cannot root itself through
    // this table; dead entries are pruned on every change.
    private static readonly List<WeakReference<FlyoutPage>> s_presentedDrawers = new();
    private static bool s_modalUp;
    // What the shell was last told; the shell's own webSuspended bit mirrors this.
    private static bool s_suspended;

    /// <summary>
    /// One drawer's presented state changed (open/close, or its handler connected/disconnected).
    /// A drawer outside the primary window keeps the historical direct command: the shell's
    /// suspend/resume protocol addresses the main window's overlay set only (a subwindow owns its
    /// own child web pool, which has no suspend wire).
    /// </summary>
    internal static void DrawerChanged(FlyoutPage drawer, bool presented)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        if (OpenHarmonyMauiAppHost.ResolveWindowId(drawer) != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            OpenHarmonyBridge.WebCommand(presented ? "suspend" : "resume");
            return;
        }
        lock (s_sync)
        {
            SetPresented(drawer, presented);
            Apply();
        }
    }

    /// <summary>
    /// The primary window's modal stack gained or dropped its top page. Only that window's modals
    /// matter: the untagged suspend/resume commands reach the main window's overlays.
    /// </summary>
    internal static void ModalChanged(bool presented)
    {
        lock (s_sync)
        {
            if (s_modalUp == presented)
            {
                return;
            }
            s_modalUp = presented;
            Apply();
        }
    }

    private static void SetPresented(FlyoutPage drawer, bool presented)
    {
        int index = -1;
        int i = 0;
        while (i < s_presentedDrawers.Count)
        {
            if (!s_presentedDrawers[i].TryGetTarget(out FlyoutPage? target))
            {
                s_presentedDrawers.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(target, drawer))
            {
                index = i;
                break;
            }
            i++;
        }
        if (presented && index < 0)
        {
            s_presentedDrawers.Add(new WeakReference<FlyoutPage>(drawer));
        }
        else if (!presented && index >= 0)
        {
            s_presentedDrawers.RemoveAt(index);
        }
    }

    private static void Apply()
    {
        bool want = s_presentedDrawers.Count > 0 && !s_modalUp;
        if (want == s_suspended)
        {
            return;
        }
        s_suspended = want;
        if (want)
        {
            // Re-arming: hide every overlay first, so the visibility snapshot the shell remembers
            // cannot resurface the page that was on top of the drawer (the modal that just
            // popped); the next drawer close restores an all-hidden vector and the drawer's own
            // arrange cycle re-shows the controls that belong to the page behind it.
            OpenHarmonyBridge.WebCommand("hide");
            OpenHarmonyBridge.WebCommand("suspend");
        }
        else
        {
            OpenHarmonyBridge.WebCommand("resume");
        }
    }
}
