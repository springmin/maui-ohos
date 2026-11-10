using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

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
