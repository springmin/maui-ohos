// AUTODISCONNECT: release an overlay slot when its web control leaves the visual tree.
//
// MAUI does not disconnect an element's handler on a layout removal: Layout.Clear/Remove only
// drop the child from the layout (and its platform child list); the Element keeps its handler so
// page/JS state can survive a remove + re-add. On OpenHarmony the ArkWeb overlay was therefore
// never released - the shell never received "slot destroy", a removed third hybrid kept its
// engine and document alive, and a re-added control showed the stale overlay instead of
// reloading (kit #44 self-check: removing web C only flipped the label).
//
// The slice watches the element tree's own descendant add/remove events instead of mirroring
// child lists or walking the tree: a removed element whose handler implements the slice-local
// IOpenHarmonyOverlaySlotLifetime is told to drop its claim (OnOverlaySlotDetached), and a
// (re)added one to re-claim and replay (OnOverlaySlotAttached). A page/content-view/layout
// handler watches its subtree, so a Clear, a Remove, an indexer update and a whole-subtree
// removal all land here; the handlers stay connected while removed (MAUI semantics) and the
// overlay is rebuilt when the control is added back. The calls are idempotent, so the several
// watchers along the parent chain cannot double-release.
//
// The lifetime contract is deliberately slice-local (not a member of the hosting
// IOpenHarmonyOverlaySlotOwner): the workload's reference pack is built from the hosting sources
// and the device build resolves the interface from that pack, so an added member there would
// only compile after the packs are rebuilt. The slice compiles its own contract; the pool side
// (Acquire/Release/Touch) is unchanged.
using Microsoft.Maui.Controls;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Visual-tree lifetime contract of an overlay-slot owner (AUTODISCONNECT). Implemented by the
/// WebView/HybridWebView/BlazorWebView handlers; called by
/// <see cref="OpenHarmonyOverlaySlotWatch"/> from the page/content-view/layout handlers that
/// watch their element subtree.
/// </summary>
internal interface IOpenHarmonyOverlaySlotLifetime
{
    /// <summary>
    /// The control left the visual tree while its handler stayed connected. Drop the claim (a
    /// dynamic slot is destroyed in the shell) and mark the control for a replay, so a later
    /// re-attach rebuilds the overlay. Idempotent.
    /// </summary>
    void OnOverlaySlotDetached();

    /// <summary>
    /// The control (re)entered the visual tree. An owner left without a claim (detached or
    /// preempted) re-claims a slot and replays its page/registration; a live claim is only
    /// touched (LRU bookkeeping).
    /// </summary>
    void OnOverlaySlotAttached();
}

/// <summary>
/// Subtree watcher for the overlay-slot lifetime (AUTODISCONNECT). Used by the page, content-view
/// and layout handlers, which are the element ancestors a web control can be removed from.
/// </summary>
internal static class OpenHarmonyOverlaySlotWatch
{
    /// <summary>Starts watching <paramref name="root"/>'s descendants (null-safe).</summary>
    public static void Watch(Element? root)
    {
        if (root is null)
        {
            return;
        }
        root.DescendantRemoved += OnDescendantRemoved;
        root.DescendantAdded += OnDescendantAdded;
    }

    /// <summary>Stops watching <paramref name="root"/>'s descendants (null-safe).</summary>
    public static void Unwatch(Element? root)
    {
        if (root is null)
        {
            return;
        }
        root.DescendantRemoved -= OnDescendantRemoved;
        root.DescendantAdded -= OnDescendantAdded;
    }

    private static void OnDescendantRemoved(object? sender, ElementEventArgs e)
        => (e.Element?.Handler as IOpenHarmonyOverlaySlotLifetime)?.OnOverlaySlotDetached();

    private static void OnDescendantAdded(object? sender, ElementEventArgs e)
        => (e.Element?.Handler as IOpenHarmonyOverlaySlotLifetime)?.OnOverlaySlotAttached();
}
