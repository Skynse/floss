using System;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Fills a closed polygon area with the current paint color.
// Supports antialiased fill when Antialiasing is true.
public sealed class ClosedAreaFillOutput : IOutputProcess
{
    public bool IsPaintOutput => true;
    public bool Antialiasing { get; set; } = true;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not PolygonInput poly) return;
        if (poly.SmoothedPoints.Count < 3) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var points = poly.SmoothedPoints;
        var color = ctx.PaintColor;

        using var skPath = new SKPath();
        skPath.MoveTo((float)points[0].X, (float)points[0].Y);
        for (int i = 1; i < points.Count; i++)
            skPath.LineTo((float)points[i].X, (float)points[i].Y);
        skPath.Close();

        var bounds = skPath.Bounds;
        int x1 = Math.Clamp((int)bounds.Left, 0, ctx.Document.Width - 1);
        int y1 = Math.Clamp((int)bounds.Top, 0, ctx.Document.Height - 1);
        int x2 = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, ctx.Document.Width - 1);
        int y2 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, ctx.Document.Height - 1);

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        bool changed = false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        if (Antialiasing)
        {
            int bw = x2 - x1 + 1;
            int bh = y2 - y1 + 1;
            if (bw > 0 && bh > 0)
            {
                using var bitmap = new SKBitmap(bw, bh, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);

                using var paint = new SKPaint
                {
                    Color = new SKColor(color.R, color.G, color.B, color.A),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };

                using var localPath = new SKPath(skPath);
                localPath.Offset(-x1, -y1);
                canvas.DrawPath(localPath, paint);

                for (int docY = y1; docY <= y2; docY++)
                {
                    for (int docX = x1; docX <= x2; docX++)
                    {
                        if (!ctx.Selection.IsSelected(docX, docY)) continue;

                        int lx = docX - layer.OffsetX;
                        int ly = docY - layer.OffsetY;

                        var skColor = bitmap.GetPixel(docX - x1, docY - y1);
                        if (skColor.Alpha == 0) continue;

                        if (AlphaLockPixelOps.TryWriteColor(layer.Pixels, lx, ly,
                                skColor.Blue, skColor.Green, skColor.Red, skColor.Alpha, layer.IsAlphaLocked))
                        {
                            changed = true;
                            minX = Math.Min(minX, docX);
                            minY = Math.Min(minY, docY);
                            maxX = Math.Max(maxX, docX);
                            maxY = Math.Max(maxY, docY);
                        }
                    }
                }
            }
        }
        else
        {
            using var region = new SKRegion();
            region.SetPath(skPath, new SKRegion(new SKRectI(0, 0, ctx.Document.Width, ctx.Document.Height)));

            for (int docY = y1; docY <= y2; docY++)
            {
                for (int docX = x1; docX <= x2; docX++)
                {
                    if (!region.Contains(docX, docY)) continue;
                    if (!ctx.Selection.IsSelected(docX, docY)) continue;

                    int lx = docX - layer.OffsetX;
                    int ly = docY - layer.OffsetY;

                    if (AlphaLockPixelOps.TryWriteColor(layer.Pixels, lx, ly,
                            color.B, color.G, color.R, color.A, layer.IsAlphaLocked))
                    {
                        changed = true;
                        minX = Math.Min(minX, docX);
                        minY = Math.Min(minY, docY);
                        maxX = Math.Max(maxX, docX);
                        maxY = Math.Max(maxY, docY);
                    }
                }
            }
        }

        if (!changed) return;

        var dirty = new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }
}
