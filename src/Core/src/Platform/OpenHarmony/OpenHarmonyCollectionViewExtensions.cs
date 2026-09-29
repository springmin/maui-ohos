// Application-facing list extensions for the OpenHarmony compositor.
//
// CollectionView has no built-in group collapse/expand API, and the slice implements it without
// touching ItemsSource: the materializer keeps every row and only hides the rows of collapsed
// groups from the viewport projection (see OpenHarmonyItemListMaterializer.SetGroupCollapsed).
// These helpers are the app surface for that projection: collapse/expand a group object, read
// the current state, and opt in to header-tap toggling.
//
// A group object is the group element of the grouped source (the element CollectionView uses as
// the group; the same object the GroupHeaderTemplate binds to). Calls before the handler is
// connected or with a group that is not in the current source are safe no-ops that return false.
using Microsoft.Maui.Controls;

namespace Microsoft.Maui.Platform;

public static class OpenHarmonyCollectionViewExtensions
{
    /// <summary>
    /// Collapses or expands <paramref name="group"/> without changing ItemsSource. The rows stay
    /// in the source; collapsed groups hide their items (and group footer) from the viewport and
    /// the offset is adjusted so the content below the group does not jump.
    /// Returns false when the control has no OpenHarmony handler or the group is not present.
    /// </summary>
    public static bool SetGroupCollapsed(this CollectionView collectionView, object group, bool collapsed)
        => (collectionView?.Handler as OpenHarmonyCollectionViewHandler)?.SetGroupCollapsed(group, collapsed) == true;

    /// <summary>True when <paramref name="group"/> is collapsed in the current viewport projection.</summary>
    public static bool IsGroupCollapsed(this CollectionView collectionView, object group)
        => (collectionView?.Handler as OpenHarmonyCollectionViewHandler)?.IsGroupCollapsed(group) == true;

    /// <summary>Toggles the collapsed state of <paramref name="group"/>; false when it is absent.</summary>
    public static bool ToggleGroupCollapsed(this CollectionView collectionView, object group)
    {
        if (collectionView?.Handler is not OpenHarmonyCollectionViewHandler handler)
        {
            return false;
        }
        return handler.SetGroupCollapsed(group, !handler.IsGroupCollapsed(group));
    }

    /// <summary>
    /// Opt-in: while enabled, tapping a group header row toggles that group's collapsed state
    /// (the header row keeps its normal rendering; only its tap target changes).
    /// </summary>
    public static void SetGroupHeaderTogglesCollapse(this CollectionView collectionView, bool enabled)
        => (collectionView?.Handler as OpenHarmonyCollectionViewHandler)?.SetGroupHeaderTogglesCollapse(enabled);
}
