using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class FillToolOperation : IToolOperation
{
    private readonly ToolContext _context;
    private readonly double _tolerance;

    public FillToolOperation(ToolContext context, double tolerance)
    {
        _context = context;
        _tolerance = tolerance;
    }

    public int SampleCount { get; private set; }
    public void Update(CanvasInputSample sample) { }
    public void Cancel() => SampleCount = 0;

    public void Commit(CanvasInputSample sample)
    {
        SampleCount = 1;
        var layer = _context.ActiveLayer;
        if (layer == null || !_context.Document.CanPaintActiveLayer) return;

        int docX = (int)sample.X;
        int docY = (int)sample.Y;
        int lx = docX - layer.OffsetX;
        int ly = docY - layer.OffsetY;

        if ((uint)lx >= (uint)layer.Width || (uint)ly >= (uint)layer.Height) return;

        layer.Pixels.GetPixel(lx, ly, out byte refB, out byte refG, out byte refR, out byte refA);
        int tolInt = (int)(_context.Document.Width > 0 ? _tolerance * 255 * 4 : 0);

        var c = _context.PaintColor;
        byte fillB = c.B, fillG = c.G, fillR = c.R, fillA = c.A;

        if (refB == fillB && refG == fillG && refR == fillR && refA == fillA) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        var visited = new bool[layer.Width * layer.Height];
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((lx, ly));

        int minX = lx, minY = ly, maxX = lx, maxY = ly;
        bool changed = false;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            if ((uint)cx >= (uint)layer.Width || (uint)cy >= (uint)layer.Height) continue;
            int idx = cy * layer.Width + cx;
            if (visited[idx]) continue;
            visited[idx] = true;

            if (!_context.Selection.IsSelected(cx + layer.OffsetX, cy + layer.OffsetY)) continue;

            layer.Pixels.GetPixel(cx, cy, out byte b, out byte g, out byte r, out byte a);
            if (Math.Abs(b - refB) + Math.Abs(g - refG) + Math.Abs(r - refR) + Math.Abs(a - refA) > tolInt) continue;

            layer.Pixels.SetPixel(cx, cy, fillB, fillG, fillR, fillA);
            changed = true;

            minX = Math.Min(minX, cx); minY = Math.Min(minY, cy);
            maxX = Math.Max(maxX, cx); maxY = Math.Max(maxY, cy);

            queue.Enqueue((cx + 1, cy)); queue.Enqueue((cx - 1, cy));
            queue.Enqueue((cx, cy + 1)); queue.Enqueue((cx, cy - 1));
        }

        if (!changed) return;

        var dirtyRegion = new PixelRegion(
            minX + layer.OffsetX, minY + layer.OffsetY,
            maxX - minX + 1, maxY - minY + 1);

        layer.MarkThumbnailDirty();
        _context.CommitMutation(_context.ActiveLayerIndex, beforeTiles, dirtyRegion);
        SampleCount = 0;
    }
}

public sealed class ColorSampleOperation : IToolOperation
{
    private readonly ToolContext _context;

    public ColorSampleOperation(ToolContext context)
    {
        _context = context;
    }

    public int SampleCount { get; private set; }
    public void Update(CanvasInputSample sample) => Sample(sample);
    public void Commit(CanvasInputSample sample) => Sample(sample);
    public void Cancel() => SampleCount = 0;

    private void Sample(CanvasInputSample sample)
    {
        SampleCount++;
        var docX = (int)sample.X;
        var docY = (int)sample.Y;
        if (_context.SampleDocumentColor(docX, docY) is { } sampled)
        {
            _context.OnColorSampled(sampled);
            return;
        }

        var layer = _context.ActiveLayer;
        if (layer == null || layer.IsGroup) return;
        int x = docX - layer.OffsetX;
        int y = docY - layer.OffsetY;
        if ((uint)x >= (uint)layer.Width || (uint)y >= (uint)layer.Height) return;
        layer.Pixels.GetPixel(x, y, out byte b, out byte g, out byte r, out byte a);
        if (a == 0) return;
        _context.OnColorSampled(Color.FromArgb(a, r, g, b));
    }
}

public sealed class MagicWandOperation : IToolOperation
{
    private readonly ToolContext _context;
    private readonly double _tolerance;
    private readonly SelectOp _op;

    public MagicWandOperation(ToolContext context, double tolerance, SelectOp op)
    {
        _context = context;
        _tolerance = tolerance;
        _op = op;
    }

    public int SampleCount { get; private set; }
    public void Update(CanvasInputSample sample) { }
    public void Cancel() => SampleCount = 0;

    public void Commit(CanvasInputSample sample)
    {
        var layer = _context.ActiveLayer;
        if (layer == null) return;
        int x = (int)sample.X - layer.OffsetX;
        int y = (int)sample.Y - layer.OffsetY;
        _context.Selection.SetFromFloodFill(layer.Pixels, x, y, layer.OffsetX, layer.OffsetY, _tolerance, _op);
        SampleCount = 0;
        _context.InvalidateRender();
    }
}
