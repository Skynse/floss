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

        var paint = layer.ActivePixels;
        int threshold = ColorDifference.Tolerance01ToThreshold(Tolerance);
        var fillColor = ctx.PaintColor;
        var blendMode = BlendMode;
        var effectiveA = (byte)Math.Round(fillColor.A * Math.Clamp(Opacity, 0.0, 1.0));

        int pixelCount = docW * docH;
        if (_fillMask == null || _fillMask.Length < pixelCount)
            _fillMask = new byte[pixelCount];
        else
            Array.Clear(_fillMask, 0, pixelCount);

        if (FillReference != FillReferenceMode.CurrentLayer)
        {
            var refBuf = FloodFillReference.BuildCompositeBuffer(ctx, FillReference, docW, docH);
            int off = (cy * docW + cx) * 4;
            byte refB = refBuf[off], refG = refBuf[off + 1], refR = refBuf[off + 2], refA = refBuf[off + 3];

            bool SimilarDoc(int x, int y)
            {
                int pixelOff = (y * docW + x) * 4;
                return ColorDifference.IsSimilarBgra(refBuf.AsSpan(pixelOff, 4), refB, refG, refR, refA, threshold);
            }

            void MarkDocPixel(int x, int y)
            {
                if (!ctx.Selection.IsSelected(x, y)) return;
                _fillMask![y * docW + x] = 255;
            }

            if (ContiguousFill)
            {
                _visit.BeginPass(pixelCount);
                FloodFillScanline.FillContiguous(docW, docH, cx, cy, SimilarDoc,
                    _visit.Stamp, _visit.Epoch, MarkDocPixel);
            }
            else
            {
                FloodFillNonContiguous.FillInBounds(0, 0, docW - 1, docH - 1,
                    (x, y) => SimilarDoc(x, y) && ctx.Selection.IsSelected(x, y), MarkDocPixel);
            }
        }
        else
        {
            int localX = cx - layer.OffsetX;
            int localY = cy - layer.OffsetY;
            if ((uint)localX >= (uint)layer.Width || (uint)localY >= (uint)layer.Height)
                return;

            paint.GetPixel(localX, localY, out byte refB, out byte refG, out byte refR, out byte refA);

            bool SimilarLocal(int lx, int ly)
            {
                if ((uint)lx >= (uint)layer.Width || (uint)ly >= (uint)layer.Height)
                    return false;
                paint.GetPixel(lx, ly, out byte b, out byte g, out byte r, out byte a);
                return ColorDifference.IsSimilarBgra(b, g, r, a, refB, refG, refR, refA, threshold);
            }

            void MarkLocalPixel(int lx, int ly)
            {
                int docX = lx + layer.OffsetX;
                int docY = ly + layer.OffsetY;
                if (!ctx.Selection.IsSelected(docX, docY)) return;
                _fillMask![docY * docW + docX] = 255;
            }

            if (ContiguousFill)
            {
                _visit.BeginPass(layer.Width * layer.Height);
                FloodFillScanline.FillContiguous(layer.Width, layer.Height, localX, localY, SimilarLocal,
                    _visit.Stamp, _visit.Epoch, MarkLocalPixel);
            }
            else
            {
                FloodFillNonContiguous.FillInBounds(0, 0, layer.Width - 1, layer.Height - 1,
                    (lx, ly) =>
                    {
                        if (!SimilarLocal(lx, ly))
                            return false;
                        int docX = lx + layer.OffsetX;
                        int docY = ly + layer.OffsetY;
                        return ctx.Selection.IsSelected(docX, docY);
                    },
                    MarkLocalPixel);
            }
        }

        int scaling = (int)Math.Round(Math.Clamp(AreaScaling, -20, 20));
        if (scaling != 0)
            MaskMorphology.ApplyAreaScaling(_fillMask, docW, docH, scaling);

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

}
