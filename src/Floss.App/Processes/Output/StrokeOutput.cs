using System;
using System.Linq;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Strokes a polygon or path onto the active layer.
public sealed class StrokeOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = true;
    public float StrokeWidth { get; set; } = 4;
    public bool ClosePath { get; set; }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not PolygonInput poly) return;
        if (poly.SmoothedPoints.Count < 2) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var points = poly.SmoothedPoints;
        var color = ctx.PaintColor;

        using var bitmap = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A),
            StrokeWidth = StrokeWidth,
            IsAntialias = Antialiasing,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var path = new SKPath();
        path.MoveTo((float)(points[0].X - layer.OffsetX), (float)(points[0].Y - layer.OffsetY));
        for (int i = 1; i < points.Count; i++)
            path.LineTo((float)(points[i].X - layer.OffsetX), (float)(points[i].Y - layer.OffsetY));
        if (ClosePath) path.Close();

        canvas.DrawPath(path, paint);

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        bool changed = false;

        for (int ly = 0; ly < layer.Height; ly++)
        {
            for (int lx = 0; lx < layer.Width; lx++)
            {
                int docX = lx + layer.OffsetX;
                int docY = ly + layer.OffsetY;
                if (!ctx.Selection.IsSelected(docX, docY)) continue;

                var skColor = bitmap.GetPixel(lx, ly);
                if (skColor.Alpha == 0) continue;

                if (layer.IsAlphaLocked)
                {
                    layer.Pixels.GetPixel(lx, ly, out _, out _, out _, out byte ea);
                    if (ea == 0) continue;
                }

                layer.Pixels.SetPixel(lx, ly, skColor.Blue, skColor.Green, skColor.Red, skColor.Alpha);
                changed = true;
            }
        }

        if (!changed) return;

        var dirty = new PixelRegion(layer.OffsetX, layer.OffsetY, layer.Width, layer.Height);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }
}
