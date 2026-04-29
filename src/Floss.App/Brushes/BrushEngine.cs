using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Brushes;

public sealed class BrushEngine
{
    public void RasterizeSegment(
        WriteableBitmap bitmap,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample from,
        CanvasInputSample to)
    {
        if (from.Pressure <= 0 && to.Pressure <= 0) return;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var spacing = Math.Max(1, brush.Size * brush.Spacing);
        var steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));

        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var time = (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t);
            var pressure = from.Pressure + (to.Pressure - from.Pressure) * t;
            var sample = from.WithPosition(from.X + dx * t, from.Y + dy * t, pressure, time);
            var velocity = sample.DistanceTo(from) * 1_000_000 / Math.Max(1, sample.TimeMicros - from.TimeMicros);
            RasterizeDab(bitmap, brush, eraser, sample, velocity);
        }
    }

    public void RasterizeDab(
        WriteableBitmap bitmap,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample sample,
        double velocity)
    {
        var pressure = Math.Pow(Math.Clamp(sample.Pressure, 0, 1), brush.PressureCurve);
        if (pressure <= 0.001) return;

        var velocity01 = Math.Clamp(velocity / 5000, 0, 1);
        var sizeFactor = Math.Clamp(1 - velocity01 * brush.VelocitySize, 0.1, 1);
        var opacityFactor = Math.Clamp(1 - velocity01 * brush.VelocityOpacity, 0.1, 1);
        var radius = Math.Max(0.5, brush.Size * pressure * sizeFactor * 0.5);
        var alpha = (eraser ? 1.0 : brush.Opacity) * opacityFactor * pressure;

        using var frame = bitmap.Lock();
        unsafe
        {
            var pixels = (byte*)frame.Address;
            var minX = Math.Max(0, (int)Math.Floor(sample.X - radius));
            var minY = Math.Max(0, (int)Math.Floor(sample.Y - radius));
            var maxX = Math.Min(DrawingDocument.CanvasWidth - 1, (int)Math.Ceiling(sample.X + radius));
            var maxY = Math.Min(DrawingDocument.CanvasHeight - 1, (int)Math.Ceiling(sample.Y + radius));

            for (var y = minY; y <= maxY; y++)
            {
                var row = pixels + y * frame.RowBytes;
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5 - sample.X;
                    var py = y + 0.5 - sample.Y;
                    var dist = Math.Sqrt(px * px + py * py);
                    if (dist > radius) continue;

                    var falloff = 1 - dist / radius;
                    var localAlpha = alpha * (brush.Hardness + (1 - brush.Hardness) * falloff);
                    if (eraser)
                    {
                        Erase(row + x * 4, localAlpha);
                    }
                    else
                    {
                        Blend(row + x * 4, brush.Color, localAlpha);
                    }
                }
            }
        }
    }

    private static unsafe void Blend(byte* dst, Color color, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        var srcAlpha = alpha * color.A / 255.0;
        var inv = 1 - srcAlpha;
        var dstAlpha = dst[3] / 255.0;
        var outAlpha = srcAlpha + dstAlpha * inv;

        dst[0] = (byte)Math.Clamp(color.B * srcAlpha + dst[0] * inv, 0, 255);
        dst[1] = (byte)Math.Clamp(color.G * srcAlpha + dst[1] * inv, 0, 255);
        dst[2] = (byte)Math.Clamp(color.R * srcAlpha + dst[2] * inv, 0, 255);
        dst[3] = (byte)Math.Clamp(outAlpha * 255, 0, 255);
    }

    private static unsafe void Erase(byte* dst, double alpha)
    {
        var keep = 1 - Math.Clamp(alpha, 0, 1);
        dst[0] = (byte)Math.Clamp(dst[0] * keep, 0, 255);
        dst[1] = (byte)Math.Clamp(dst[1] * keep, 0, 255);
        dst[2] = (byte)Math.Clamp(dst[2] * keep, 0, 255);
        dst[3] = (byte)Math.Clamp(dst[3] * keep, 0, 255);
    }
}
