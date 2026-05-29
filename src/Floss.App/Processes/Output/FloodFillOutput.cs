using System;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Flood-fills connected pixels starting from a click point.
public sealed class FloodFillOutput : IOutputProcess
{
    public bool IsPaintOutput => true;
    public bool Antialiasing { get; set; } = false;
    public double Tolerance { get; set; } = 0.05;
    public FillReferenceMode FillReference { get; set; } = FillReferenceMode.CurrentLayer;
    public double Opacity { get; set; } = 1.0;
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var cx = (int)click.Point.X;
        var cy = (int)click.Point.Y;

        int docW = ctx.Document.Width;
        int docH = ctx.Document.Height;
        if ((uint)cx >= (uint)docW || (uint)cy >= (uint)docH) return;

        // Build flat reference composite when sampling from other layers.
        // The BFS uses this for both seed color and boundary detection.
        byte[]? refBuf = FillReference != FillReferenceMode.CurrentLayer
            ? BuildReferenceBuffer(ctx, docW, docH)
            : null;

        // Sample seed color from reference composite or active layer.
        SKColor targetColor;
        if (refBuf != null)
        {
            int off = (cy * docW + cx) * 4;
            targetColor = new SKColor(refBuf[off + 2], refBuf[off + 1], refBuf[off + 0], refBuf[off + 3]);
        }
        else
        {
            layer.Pixels.GetPixel(cx - layer.OffsetX, cy - layer.OffsetY,
                out byte sb, out byte sg, out byte sr, out byte sa);
            targetColor = new SKColor(sr, sg, sb, sa);
        }

        var fillColor = ctx.PaintColor;
        var blendMode = BlendMode;
        var effectiveA = (byte)Math.Round(fillColor.A * Math.Clamp(Opacity, 0.0, 1.0));
        var toleranceSq = (int)(Tolerance * 255 * Tolerance * 255 * 4);

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);

        // Cap total flood-fill pixels to avoid OOM on huge canvases.
        // 64M pixels (8k×8k) is ~256MB for visited array + queue.
        const int maxPixels = 64_000_000;
        var totalPixels = (long)docW * docH;
        if (totalPixels > maxPixels)
            return;

        var pixelCount = docW * docH;
        var visited = new bool[pixelCount];
        var queue = new System.Collections.Generic.Queue<int>(pixelCount / 4);
        int startIdx = cy * docW + cx;
        visited[startIdx] = true;
        queue.Enqueue(startIdx);

        bool changed = false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int y = idx / docW;
            int x = idx % docW;

            if (!ctx.Selection.IsSelected(x, y)) continue;

            // Sample from reference composite or active layer for boundary test.
            SKColor pixelColor;
            if (refBuf != null)
            {
                int off = idx * 4;
                pixelColor = new SKColor(refBuf[off + 2], refBuf[off + 1], refBuf[off + 0], refBuf[off + 3]);
            }
            else
            {
                layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY,
                    out byte cb, out byte cg, out byte cr, out byte ca);
                pixelColor = new SKColor(cr, cg, cb, ca);
            }

            if (!ColorMatch(pixelColor, targetColor, toleranceSq)) continue;

            var lx = x - layer.OffsetX;
            var ly = y - layer.OffsetY;
            if (AlphaLockPixelOps.TryWriteColor(layer.Pixels, lx, ly,
                    fillColor.B, fillColor.G, fillColor.R, effectiveA, layer.IsAlphaLocked, blendMode))
            {
                changed = true;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }

            if (x + 1 < docW) { int ni = idx + 1; if (!visited[ni]) { visited[ni] = true; queue.Enqueue(ni); } }
            if (x - 1 >= 0) { int ni = idx - 1; if (!visited[ni]) { visited[ni] = true; queue.Enqueue(ni); } }
            if (y + 1 < docH) { int ni = idx + docW; if (!visited[ni]) { visited[ni] = true; queue.Enqueue(ni); } }
            if (y - 1 >= 0) { int ni = idx - docW; if (!visited[ni]) { visited[ni] = true; queue.Enqueue(ni); } }
        }

        if (!changed) return;

        var dirty = new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }

    private byte[] BuildReferenceBuffer(ToolContext ctx, int w, int h)
    {
        var buf = new byte[w * h * 4];
        foreach (var l in ctx.Document.Layers)
        {
            if (!l.IsVisible || l.IsGroup) continue;
            if (FillReference == FillReferenceMode.ReferenceLayers && !l.IsReference) continue;
            l.Pixels.BlendOnto(buf, w, h, l.Opacity);
        }
        return buf;
    }

    private static bool ColorMatch(SKColor a, SKColor b, int toleranceSq)
    {
        int dr = a.Red - b.Red;
        int dg = a.Green - b.Green;
        int db = a.Blue - b.Blue;
        int da = a.Alpha - b.Alpha;
        return dr * dr + dg * dg + db * db + da * da <= toleranceSq;
    }
}
