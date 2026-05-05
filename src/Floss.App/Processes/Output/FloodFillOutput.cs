using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Flood-fills connected pixels starting from a click point.
public sealed class FloodFillOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public double Tolerance { get; set; } = 0.05;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var cx = (int)click.Point.X;
        var cy = (int)click.Point.Y;

        // Sample target color
        layer.Pixels.GetPixel(cx - layer.OffsetX, cy - layer.OffsetY,
            out byte sb, out byte sg, out byte sr, out byte sa);

        var targetColor = new SKColor(sr, sg, sb, sa);
        var fillColor = ctx.PaintColor;
        var toleranceSq = (int)(Tolerance * 255 * Tolerance * 255 * 3);

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        var visited = new System.Collections.Generic.HashSet<(int, int)>();
        var queue = new System.Collections.Generic.Queue<(int, int)>();
        queue.Enqueue((cx, cy));
        visited.Add((cx, cy));

        bool changed = false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            if (!ctx.Selection.IsSelected(x, y)) continue;

            int lx = x - layer.OffsetX;
            int ly = y - layer.OffsetY;

            layer.Pixels.GetPixel(lx, ly, out byte cb, out byte cg, out byte cr, out byte ca);
            var pixelColor = new SKColor(cr, cg, cb, ca);
            if (!ColorMatch(pixelColor, targetColor, toleranceSq)) continue;

            if (layer.IsAlphaLocked && ca == 0) continue;

            layer.Pixels.SetPixel(lx, ly, fillColor.B, fillColor.G, fillColor.R, fillColor.A);
            changed = true;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);

            EnqueueIfValid(x - 1, y);
            EnqueueIfValid(x + 1, y);
            EnqueueIfValid(x, y - 1);
            EnqueueIfValid(x, y + 1);
        }

        if (!changed) return;

        var dirty = new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);

        void EnqueueIfValid(int x, int y)
        {
            if (x < 0 || y < 0 || x >= ctx.Document.Width || y >= ctx.Document.Height) return;
            if (!visited.Add((x, y))) return;
            queue.Enqueue((x, y));
        }
    }

    private static bool ColorMatch(SKColor a, SKColor b, int toleranceSq)
    {
        int dr = a.Red - b.Red;
        int dg = a.Green - b.Green;
        int db = a.Blue - b.Blue;
        return dr * dr + dg * dg + db * db <= toleranceSq;
    }
}
