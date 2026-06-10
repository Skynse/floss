using System;

namespace Floss.App.Features;

/// <summary>
/// Pan math for navigator input — mirrors <see cref="Canvas.DrawingCanvas"/> viewport inverse
/// (flip → zoom → rotate → pan around document center).
/// </summary>
internal static class CanvasViewTransformMath
{
    public static (double PanX, double PanY) PanToCenterDocumentPoint(
        double docX,
        double docY,
        int docW,
        int docH,
        double zoom,
        double rotationDeg,
        int flipX,
        int flipY)
    {
        NormalizeFlip(ref flipX, ref flipY);
        var angle = rotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var relX = (docX - docW * 0.5) * flipX * zoom;
        var relY = (docY - docH * 0.5) * flipY * zoom;
        return (-(relX * cos + relY * sin), -(-relX * sin + relY * cos));
    }

    public static (double PanDx, double PanDy) DocumentDeltaToViewportPan(
        double docDx,
        double docDy,
        double zoom,
        double rotationDeg,
        int flipX,
        int flipY)
    {
        NormalizeFlip(ref flipX, ref flipY);
        var angle = rotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var relX = docDx * flipX * zoom;
        var relY = docDy * flipY * zoom;
        return (-(relX * cos + relY * sin), -(-relX * sin + relY * cos));
    }

    private static void NormalizeFlip(ref int flipX, ref int flipY)
    {
        if (flipX == 0)
            flipX = 1;
        if (flipY == 0)
            flipY = 1;
    }
}
