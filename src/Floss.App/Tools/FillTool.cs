using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

// Flood-fills connected pixels matching the clicked color, constrained to the selection mask.
public sealed class FillTool : ITool
{
    public double Tolerance { get; set; } = 0.05;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsLocked) return;

        int docX = (int)s.X;
        int docY = (int)s.Y;
        int lx = docX - layer.OffsetX;
        int ly = docY - layer.OffsetY;

        if ((uint)lx >= (uint)layer.Width || (uint)ly >= (uint)layer.Height) return;

        layer.Pixels.GetPixel(lx, ly, out byte refB, out byte refG, out byte refR, out byte refA);
        int tolInt = (int)(ctx.Document.Width > 0 ? Tolerance * 255 * 4 : 0);

        var c = ctx.PaintColor;
        byte fillB = c.B, fillG = c.G, fillR = c.R, fillA = c.A;

        // Bail if we'd fill with the same color.
        if (refB == fillB && refG == fillG && refR == fillR && refA == fillA) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        var dirty = new PixelRegion(int.MaxValue, int.MaxValue, 0, 0);

        var visited = new bool[layer.Width * layer.Height];
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((lx, ly));

        int minX = lx, minY = ly, maxX = lx, maxY = ly;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            if ((uint)cx >= (uint)layer.Width || (uint)cy >= (uint)layer.Height) continue;
            int idx = cy * layer.Width + cx;
            if (visited[idx]) continue;
            visited[idx] = true;

            // Respect selection mask.
            if (!ctx.Selection.IsSelected(cx + layer.OffsetX, cy + layer.OffsetY)) continue;

            layer.Pixels.GetPixel(cx, cy, out byte b, out byte g, out byte r, out byte a);
            if (Math.Abs(b - refB) + Math.Abs(g - refG) + Math.Abs(r - refR) + Math.Abs(a - refA) > tolInt) continue;

            layer.Pixels.SetPixel(cx, cy, fillB, fillG, fillR, fillA);

            minX = Math.Min(minX, cx); minY = Math.Min(minY, cy);
            maxX = Math.Max(maxX, cx); maxY = Math.Max(maxY, cy);

            queue.Enqueue((cx + 1, cy)); queue.Enqueue((cx - 1, cy));
            queue.Enqueue((cx, cy + 1)); queue.Enqueue((cx, cy - 1));
        }

        var dirtyRegion = new PixelRegion(
            minX + layer.OffsetX, minY + layer.OffsetY,
            maxX - minX + 1, maxY - minY + 1);

        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirtyRegion);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s) { }
    public void PointerUp(ToolContext ctx, CanvasInputSample s) { }
    public void Cancel(ToolContext ctx) { }
    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }
}
