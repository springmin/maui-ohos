using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Paint-aware fills and strokes. The canvas renders solid, linear/radial gradient and image
/// paints through <c>SetFillPaint</c>; a stroke has no paint API at all, so non-solid strokes are
/// approximated with their first stop colour and reported once.
/// </summary>
internal static class OpenHarmonyPaintRenderer
{
    /// <summary>
    /// Fills a path (or a rounded/plain rectangle when <paramref name="path"/> is null) with the
    /// paint. Returns false when the paint cannot be rendered (already reported).
    /// </summary>
    public static bool Fill(MauiCanvas canvas, Paint? paint, RectF bounds, PathF? path, float cornerRadius)
    {
        switch (paint)
        {
            case null:
                return false;
            case SolidPaint { Color: { } color }:
                canvas.FillColor = color;
                FillShape(canvas, path, bounds, cornerRadius);
                return true;
            case LinearGradientPaint or RadialGradientPaint or ImagePaint:
                canvas.SetFillPaint(paint, bounds);
                FillShape(canvas, path, bounds, cornerRadius);
                return true;
            case PatternPaint { Pattern: PaintPattern wrapper }:
                // A paint wrapped in a pattern is the same paint for our purposes.
                return Fill(canvas, wrapper.Paint, bounds, path, cornerRadius);
            case PatternPaint:
                if (path is null && cornerRadius <= 0)
                {
                    // The backend tiles procedural patterns on plain rectangles.
                    canvas.SetFillPaint(paint, bounds);
                    canvas.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    return true;
                }
                OpenHarmonyStatus.Once("paint.pattern.path",
                    "a procedural pattern paint fills a path or rounded rectangle: the OpenHarmony canvas " +
                    "tiles patterns on plain rectangles only, so this fill was skipped");
                return false;
            default:
                OpenHarmonyStatus.Once("paint.fill." + paint.GetType().Name,
                    $"paint {paint.GetType().Name} cannot be filled by the OpenHarmony canvas; the fill was skipped");
                return false;
        }
    }

    /// <summary>Strokes a path with the paint (solid exactly, gradients by their first colour).</summary>
    public static bool Stroke(MauiCanvas canvas, Paint? paint, PathF path, float thickness)
    {
        if (paint is null || thickness <= 0)
        {
            return false;
        }
        if (paint is SolidPaint { Color: { } solid })
        {
            if (solid.Alpha <= 0)
            {
                return false;
            }
            canvas.StrokeColor = solid;
            canvas.StrokeSize = thickness;
            canvas.DrawPath(path);
            return true;
        }
        Color? approximation = ApproximateColor(paint);
        if (approximation is null)
        {
            OpenHarmonyStatus.Once("paint.stroke." + paint.GetType().Name,
                $"paint {paint.GetType().Name} cannot be stroked by the OpenHarmony canvas (solid strokes only); " +
                "the stroke was skipped");
            return false;
        }
        OpenHarmonyStatus.Once("paint.stroke." + paint.GetType().Name,
            $"{paint.GetType().Name} strokes are drawn with the first stop colour: the OpenHarmony canvas " +
            "strokes solid colours only");
        canvas.StrokeColor = approximation;
        canvas.StrokeSize = thickness;
        canvas.DrawPath(path);
        return true;
    }

    /// <summary>First stop colour of a gradient (used as the documented stroke approximation).</summary>
    public static Color? ApproximateColor(Paint paint)
    {
        if (paint is GradientPaint gradient)
        {
            if (gradient.GradientStops is { Length: > 0 } stops)
            {
                Color? first = null;
                float offset = float.MaxValue;
                foreach (PaintGradientStop stop in stops)
                {
                    if (stop.Offset < offset)
                    {
                        offset = stop.Offset;
                        first = stop.Color;
                    }
                }
                if (first is not null)
                {
                    return first;
                }
            }
            return gradient.StartColor ?? gradient.EndColor;
        }
        if (paint is SolidPaint { Color: { } color })
        {
            return color;
        }
        return null;
    }

    private static void FillShape(MauiCanvas canvas, PathF? path, RectF bounds, float cornerRadius)
    {
        if (path is not null)
        {
            canvas.FillPath(path);
        }
        else if (cornerRadius > 0)
        {
            canvas.FillRoundedRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, cornerRadius);
        }
        else
        {
            canvas.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
    }
}

