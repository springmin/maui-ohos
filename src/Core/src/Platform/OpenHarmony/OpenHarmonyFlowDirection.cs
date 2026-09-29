// Flow direction (RTL) support for the self-drawn compositor.
//
// Upstream MAUI defers RTL to the platform: dotnet/maui#9558 removed the cross-platform
// arrangement mirroring and the native platforms flip the placement of every child whose parent
// layout is right-to-left (Android's PlatformArrangeHandler rewrites left/right about the parent
// width, iOS adjusts the centre point), resolve Start/End text alignment against the view's own
// direction, and put trailing affordances at the physical leading edge. This slice reproduces
// that contract on the managed canvas:
//
//   * Every compositor walk (drawing, hit-testing, hover/pointer/pinch/drop resolution) carries
//     an OpenHarmonyFlowMap: the affine map (x' = Scale * x + Offset) from a view's logical MAUI
//     x coordinate to canvas space. A view whose effective direction is RightToLeft is a "mirror
//     boundary": its children's logical offsets are reflected within its physical frame, exactly
//     like the native platforms' arrange flip. MatchParent inherits the parent's resolved value;
//     a MatchParent view whose logical parents are outside the walk stays left-to-right (the
//     documented default for an element without a parent).
//   * OpenHarmonyView.Frame stays the logical (MAUI) frame. CanvasFrame is the walk-mapped,
//     canvas-space rectangle that drawing, clipping and hit-testing use; arrange-time readers
//     (page/tab/flyout/shell handlers that compute child frames) keep reading the logical frame,
//     which is what IView.Frame carries on the native platforms.
//   * Surfaces with a start/end edge resolve it from the view's own resolved direction (text
//     alignment, the entry clear button, navigation/chrome slots, flyout panel and its menu rows,
//     the picker dropdown, calendar arrows/columns, sliders, progress, switches, steppers, tab
//     order). Logical glyph order is kept: the canvas text bridge exposes no bidi reordering, so
//     only the block's start edge and the trailing affordances move, never the glyph sequence
//     (recorded limitation, same class as the italic-typeface degradation).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

/// <summary>Effective flow direction resolution for the compositor walks.</summary>
internal static class OpenHarmonyFlowDirection
{
    /// <summary>
    /// Resolves one view against its parent's already-resolved direction: an explicit
    /// LeftToRight/RightToLeft wins, MatchParent inherits.
    /// </summary>
    internal static bool IsRightToLeft(IView view, bool parentRightToLeft) => view.FlowDirection switch
    {
        FlowDirection.RightToLeft => true,
        FlowDirection.LeftToRight => false,
        _ => parentRightToLeft,
    };

    /// <summary>
    /// Resolves the direction of a walk root (a render/hit subtree is entered at its root, whose
    /// logical parents are outside the walk): the nearest explicit direction up the logical
    /// parent chain wins, a MatchParent-only chain stays left-to-right.
    /// </summary>
    internal static bool IsRightToLeftRoot(IView view)
    {
        if (view.FlowDirection != FlowDirection.MatchParent)
        {
            return view.FlowDirection == FlowDirection.RightToLeft;
        }
        for (Element? parent = (view as Element)?.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IView ancestor && ancestor.FlowDirection != FlowDirection.MatchParent)
            {
                return ancestor.FlowDirection == FlowDirection.RightToLeft;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the view itself is right-to-left, for chrome resolved outside a walk (the
    /// window title-bar row). Inside a walk the resolved value travels on the platform view
    /// (<c>OpenHarmonyView.FlowRightToLeft</c>), so no parent walk is needed per frame.
    /// </summary>
    internal static bool IsRightToLeft(IView view) => IsRightToLeft(view, IsRightToLeftRoot(view));
}

/// <summary>
/// Affine map from a view's logical (MAUI) x coordinate to compositor canvas space:
/// <c>x' = Scale * x + Offset</c>. <see cref="Scale"/> is +1 for an unmirrored space and -1
/// inside a mirrored (right-to-left) boundary; y never changes (the flow direction mirrors the
/// horizontal axis only). The drawing and hit-test walks build the map as they descend and hand
/// it to every platform view, so <c>OpenHarmonyView.CanvasFrame</c> and the walk's hit tests agree.
/// </summary>
internal readonly struct OpenHarmonyFlowMap
{
    internal readonly float Scale;
    internal readonly float Offset;

    internal OpenHarmonyFlowMap(float scale, float offset)
    {
        Scale = scale;
        Offset = offset;
    }

    /// <summary>The unmirrored map: logical coordinates are canvas coordinates.</summary>
    internal static OpenHarmonyFlowMap Identity => new(1f, 0f);

    /// <summary>Maps one logical x coordinate to canvas space.</summary>
    internal float MapX(float x) => Scale * x + Offset;

    /// <summary>
    /// Maps a logical rectangle to canvas space. A mirrored map decreases, so the two mapped
    /// edges are sorted; the size is preserved (a mirror never changes a rect's extent).
    /// </summary>
    internal RectF Map(in RectF logical)
    {
        float first = MapX(logical.X);
        float second = MapX(logical.Right);
        return new RectF(Math.Min(first, second), logical.Y, logical.Width, logical.Height);
    }

    /// <summary>
    /// The map for a child of the mapped view. A right-to-left parent mirrors its children's
    /// logical offsets within its own physical frame (the native platforms' arrange flip); any
    /// other parent lays its children out unmirrored inside its physical frame, so the child's
    /// map is a pure translation from the parent's physical origin (which is the identity in an
    /// unmirrored tree and a shifted space inside a mirrored ancestor).
    /// </summary>
    internal OpenHarmonyFlowMap ForChild(in RectF parentLogical, bool parentRightToLeft)
    {
        RectF parentPhysical = Map(parentLogical);
        return parentRightToLeft
            ? new OpenHarmonyFlowMap(-1f, parentPhysical.Left + parentLogical.Right)
            : new OpenHarmonyFlowMap(1f, parentPhysical.Left - parentLogical.X);
    }
}
