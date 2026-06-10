using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;
using Floss.App.ImageFiles;
using Floss.App.Windows;
using SkiaSharp;

namespace Floss.App.Features.Overview;

/// <summary>
/// Builds a single small overview cache and resizes it for the navigator panel.
/// No mipmap pyramid — resize from cache is cheap; rebuild is debounced and stroke-aware.
/// </summary>
internal static class DocumentOverviewCompositor
{
    /// <summary>Fixed cache resolution (longest edge). Kept small to avoid UI stalls.</summary>
    public const int CacheLongEdge = 384;

    /// <summary>Direct full flatten when source pixels are below this.</summary>
    public const long MaxDirectFlattenPixels = 4_194_304;

    public static (int Width, int Height) ComputeFit(int docWidth, int docHeight, int maxWidth, int maxHeight)
    {
        if (docWidth <= 0 || docHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
            return (Math.Max(1, docWidth), Math.Max(1, docHeight));

        var scale = Math.Min(maxWidth / (double)docWidth, maxHeight / (double)docHeight);
        if (scale > 1.0)
            scale = 1.0;

        return (
            Math.Max(1, (int)Math.Round(docWidth * scale)),
            Math.Max(1, (int)Math.Round(docHeight * scale)));
    }

    public static SKBitmap? BuildCache(DrawingDocument document)
    {
        var docW = document.Width;
        var docH = document.Height;
        if (docW <= 0 || docH <= 0)
            return null;

        var useCheckerboard = !document.IsPaperBackgroundVisible || document.PaperColor.A < 255;
        var paper = useCheckerboard ? 0u : PaperToBgra(document.PaperColor);
        var (cacheW, cacheH) = ComputeFit(docW, docH, CacheLongEdge, CacheLongEdge);

        if ((long)docW * docH <= MaxDirectFlattenPixels)
        {
            var settings = new ExportSettings(
                Quality: 100,
                ScaleMode: ExportScaleMode.Percent,
                ScalePercent: 100,
                TargetWidth: docW,
                TargetHeight: docH,
                TargetDpi: 72,
                Background: useCheckerboard ? ExportBackgroundMode.Transparent : ExportBackgroundMode.Document,
                Resample: ExportResampleMode.Lanczos);

            using var flattened = DocumentRasterizer.RenderFlattenedBitmap(document, settings);
            if (flattened.Width == cacheW && flattened.Height == cacheH)
                return flattened.Copy();

            return flattened.Resize(
                       new SKImageInfo(cacheW, cacheH, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                       MitchellSampling())
                   ?? flattened.Copy();
        }

        return CompositeAndDownsample(document, cacheW, cacheH, paper, useCheckerboard);
    }

    public static DocumentOverviewSnapshot? CreateSnapshot(
        SKBitmap cache,
        int documentWidth,
        int documentHeight,
        int panelMaxWidth,
        int panelMaxHeight)
    {
        var (targetW, targetH) = ComputeFit(documentWidth, documentHeight, panelMaxWidth, panelMaxHeight);
        using var scaled = ResizeToExact(cache, targetW, targetH);
        var bitmap = ToWriteableBitmap(scaled);
        return new DocumentOverviewSnapshot(bitmap, documentWidth, documentHeight);
    }

    private static SKBitmap CompositeAndDownsample(
        DrawingDocument document,
        int outW,
        int outH,
        uint paper,
        bool useCheckerboard)
    {
        using var compositor = new LayerCompositor();
        var docW = document.Width;
        var docH = document.Height;
        var fullVp = new PixelRegion(0, 0, docW, docH);
        compositor.Invalidate(null);

        var guard = 0;
        while (compositor.Composite(document.Layers, docW, docH, paper, fullVp))
        {
            if (++guard > 65536)
                throw new InvalidOperationException("Document overview composite did not finish.");
        }

        var bmp = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        unsafe
        {
            var pixels = (byte*)bmp.GetPixels();
            for (var y = 0; y < outH; y++)
            {
                var docY = Math.Clamp((int)((y + 0.5) * docH / (double)outH), 0, docH - 1);
                for (var x = 0; x < outW; x++)
                {
                    var docX = Math.Clamp((int)((x + 0.5) * docW / (double)outW), 0, docW - 1);
                    var px = pixels + y * bmp.RowBytes + x * 4;

                    if (compositor.TryReadDisplayPixel(docX, docY, out var b, out var g, out var r, out var a) && a > 0)
                    {
                        px[0] = b;
                        px[1] = g;
                        px[2] = r;
                        px[3] = a;
                        if (useCheckerboard && a < 255)
                            BlendCheckerboardPixel(px, x, y, a);
                        continue;
                    }

                    if (useCheckerboard)
                        WriteCheckerboardPixel(px, x, y);
                    else if (paper != 0)
                        WriteBgra(px, paper);
                    else
                        px[3] = 0;
                }
            }
        }

        return bmp;
    }

    private static SKBitmap ResizeToExact(SKBitmap source, int targetW, int targetH)
    {
        if (source.Width == targetW && source.Height == targetH)
            return source.Copy();

        return source.Resize(
                   new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                   MitchellSampling())
               ?? source.Copy();
    }

    private static SKSamplingOptions MitchellSampling()
        => new(SKCubicResampler.Mitchell);

    private static uint PaperToBgra(Avalonia.Media.Color c)
        => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));

    private static unsafe void WriteCheckerboardPixel(byte* px, int x, int y)
    {
        const int checkSize = 4;
        const byte cbDark = 0x88;
        const byte cbLight = 0xBB;
        var onDark = ((x / checkSize) + (y / checkSize)) % 2 == 0;
        var cb = onDark ? cbDark : cbLight;
        px[0] = cb;
        px[1] = cb;
        px[2] = cb;
        px[3] = 255;
    }

    private static unsafe void BlendCheckerboardPixel(byte* px, int x, int y, byte a)
    {
        const int checkSize = 4;
        const byte cbDark = 0x88;
        const byte cbLight = 0xBB;
        var onDark = ((x / checkSize) + (y / checkSize)) % 2 == 0;
        var cb = onDark ? cbDark : cbLight;
        var inv = a / 255.0;
        px[0] = (byte)(px[0] * inv + cb * (1.0 - inv));
        px[1] = (byte)(px[1] * inv + cb * (1.0 - inv));
        px[2] = (byte)(px[2] * inv + cb * (1.0 - inv));
        px[3] = 255;
    }

    private static unsafe void WriteBgra(byte* px, uint bgra)
    {
        px[0] = (byte)(bgra & 0xFF);
        px[1] = (byte)((bgra >> 8) & 0xFF);
        px[2] = (byte)((bgra >> 16) & 0xFF);
        px[3] = (byte)((bgra >> 24) & 0xFF);
    }

    private static unsafe WriteableBitmap ToWriteableBitmap(SKBitmap source)
    {
        var wb = new WriteableBitmap(
            new PixelSize(source.Width, source.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var frame = wb.Lock();
        var src = (byte*)source.GetPixels();
        var dst = (byte*)frame.Address;
        var rowBytes = Math.Min(frame.RowBytes, source.RowBytes);
        for (var y = 0; y < source.Height; y++)
            Buffer.MemoryCopy(src + y * source.RowBytes, dst + y * frame.RowBytes, rowBytes, rowBytes);

        return wb;
    }
}
