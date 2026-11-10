using System;
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Platform view for Shape/Border: keeps the MAUI <see cref="Paint"/> objects from the handlers and
/// fills/strokes them through the canvas paint facilities, so gradient/image fills render instead of
/// being dropped by the solid-only base path.
/// </summary>
internal sealed class OpenHarmonyShapeView : OpenHarmonyView
{
    /// <summary>Stroke paint (solid, gradient or image) mapped by the shape/border handlers.</summary>
    public Paint? ShapeStrokePaint { get; set; }

    /// <summary>Background paint mapped from IView.Background when it is not a solid colour.</summary>
    public Paint? BackgroundPaint { get; set; }

    public override void Draw(MauiCanvas canvas)
    {
        RectF frame = CanvasFrame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }
        if (BackgroundPaint is not null)
        {
            OpenHarmonyPaintRenderer.Fill(canvas, BackgroundPaint, frame, null, CornerRadius);
        }
        else if (Background is not null)
        {
            canvas.FillColor = BackgroundForDraw!;
            FillBackgroundRect(canvas, frame);
            float press = PressProgress;
            if (press > 0.001f)
            {
                float savedAlpha = canvas.Alpha;
                canvas.Alpha = savedAlpha * press;
                canvas.FillColor = Colors.OrangeRed;
                FillBackgroundRect(canvas, frame);
                canvas.Alpha = savedAlpha;
            }
        }
        // MAUI's Shape.PathForBounds returns geometry in the shape's own coordinate space (starting
        // near 0,0), so the canvas is translated to the view's frame - same as the base view.
        PathF? path = Shape?.PathForBounds(new RectF(0, 0, frame.Width, frame.Height));
        if (path is null)
        {
            return;
        }
        canvas.SaveState();
        canvas.Translate(frame.X, frame.Y);
        OpenHarmonyPaintRenderer.Fill(canvas, ShapeFill,
            new RectF(0, 0, frame.Width, frame.Height), path, 0);
        float thickness = IsBorder ? BorderStrokeThickness : ShapeStrokeThickness;
        Paint? stroke = ShapeStrokePaint ?? new SolidPaint(IsBorder ? BorderStroke : ShapeStroke);
        OpenHarmonyPaintRenderer.Stroke(canvas, stroke, path, thickness);
        canvas.RestoreState();
    }
}
