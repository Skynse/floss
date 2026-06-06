using System;
using Floss.App.Canvas.FloodFill;
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
    public bool ContiguousFill { get; set; } = true;
    public double AreaScaling { get; set; }
    public double Opacity { get; set; } = 1.0;
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;

    private readonly VisitEpoch _visit = new();
    private byte[]? _fillMask;

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

        byte[]? refBuf = FillReference != FillReferenceMode.CurrentLayer
            ? BuildReferenceBuffer(ctx, docW, docH)
            : null;

        byte refB, refG, refR, refA;
        if (refBuf != null)
        {
            int off = (cy * docW + cx) * 4;
            refB = refBuf[off];
            refG = refBuf[off + 1];
            refR = refBuf[off + 2];
            refA = refBuf[off + 3];
        }
        else
        {
            layer.Pixels.GetPixel(cx - layer.OffsetX, cy - layer.OffsetY,
                out refB, out refG, out refR, out refA);
        }

        int threshold = ColorDifference.Tolerance01ToThreshold(Tolerance);
        var fillColor = ctx.PaintColor;
        var blendMode = BlendMode;
        var effectiveA = (byte)Math.Round(fillColor.A * Math.Clamp(Opacity, 0.0, 1.0));

        int pixelCount = docW * docH;
        if (_fillMask == null || _fillMask.Length < pixelCount)
            _fillMask = new byte[pixelCount];
        else
            Array.Clear(_fillMask, 0, pixelCount);

        bool Similar(int x, int y)
        {
            if (refBuf != null)
            {
                int off = (y * docW + x) * 4;
                return ColorDifference.IsSimilarBgra(refBuf.AsSpan(off, 4), refB, refG, refR, refA, threshold);
            }

            layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY,
                out byte b, out byte g, out byte r, out byte a);
            return ColorDifference.IsSimilarBgra(b, g, r, a, refB, refG, refR, refA, threshold);
        }

        void MarkPixel(int x, int y)
        {
            if (!ctx.Selection.IsSelected(x, y)) return;
            _fillMask![y * docW + x] = 255;
        }

        if (ContiguousFill)
        {
            _visit.BeginPass(pixelCount);
            FloodFillScanline.FillContiguous(docW, docH, cx, cy, Similar,
                _visit.Stamp, _visit.Epoch, MarkPixel);
        }
        else
        {
            FloodFillNonContiguous.FillInBounds(0, 0, docW - 1, docH - 1,
                (x, y) => Similar(x, y) && ctx.Selection.IsSelected(x, y), MarkPixel);
        }

        int scaling = (int)Math.Round(Math.Clamp(AreaScaling, -20, 20));
        if (scaling != 0)
            MaskMorphology.ApplyAreaScaling(_fillMask, docW, docH, scaling);

        var paint = layer.ActivePixels;
        var beforeTiles = paint.CaptureTiles(paint.Bounds);

        bool changed = false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (int y = 0; y < docH; y++)
        {
            int row = y * docW;
            for (int x = 0; x < docW; x++)
            {
                if (_fillMask[row + x] == 0) continue;

                var lx = x - layer.OffsetX;
                var ly = y - layer.OffsetY;
                if (AlphaLockPixelOps.TryWriteColor(paint, lx, ly,
                        fillColor.B, fillColor.G, fillColor.R, effectiveA, layer.IsAlphaLocked, blendMode))
                {
                    changed = true;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
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
}
