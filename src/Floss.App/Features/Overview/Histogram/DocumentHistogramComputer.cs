using System;
using SkiaSharp;

namespace Floss.App.Features.Overview.Histogram;

internal static class DocumentHistogramComputer
{
    public static DocumentHistogram Compute(SKBitmap bitmap, int documentWidth, int documentHeight)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.ColorType != SKColorType.Bgra8888)
            throw new NotSupportedException($"Unsupported overview color type: {bitmap.ColorType}");

        var red = new int[256];
        var green = new int[256];
        var blue = new int[256];
        var luminance = new int[256];
        var total = 0;

        var info = bitmap.Info;
        var rowBytes = bitmap.RowBytes;
        var pixels = bitmap.GetPixels();

        if (pixels == IntPtr.Zero || info.Width <= 0 || info.Height <= 0)
            return new DocumentHistogram(red, green, blue, luminance, 0, documentWidth, documentHeight);

        unsafe
        {
            var basePtr = (byte*)pixels;
            for (var y = 0; y < info.Height; y++)
            {
                var row = basePtr + y * rowBytes;
                for (var x = 0; x < info.Width; x++)
                {
                    var px = row + x * 4;
                    var b = px[0];
                    var g = px[1];
                    var r = px[2];
                    var a = px[3];
                    if (a == 0)
                        continue;

                    red[r]++;
                    green[g]++;
                    blue[b]++;
                    var lum = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
                    luminance[Math.Clamp(lum, 0, 255)]++;
                    total++;
                }
            }
        }

        return new DocumentHistogram(red, green, blue, luminance, total, documentWidth, documentHeight);
    }
}
