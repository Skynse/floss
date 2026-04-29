using System;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Brushes;

public sealed class BrushEngine
{
    public PixelRegion RasterizeSegment(
        DrawingLayer layer,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample from,
        CanvasInputSample to)
    {
        if (from.Pressure <= 0 && to.Pressure <= 0) return PixelRegion.Empty;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var spacing = Math.Max(0.5, brush.Size * brush.Spacing);
        var steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
        var dirty = PixelRegion.Empty;

        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var time = (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t);
            var pressure = from.Pressure + (to.Pressure - from.Pressure) * t;
            var sample = from.WithPosition(from.X + dx * t, from.Y + dy * t, pressure, time);
            var velocity = sample.DistanceTo(from) * 1_000_000 / Math.Max(1, sample.TimeMicros - from.TimeMicros);
            dirty = dirty.Union(RasterizeDab(layer, brush, eraser, sample, velocity));
        }

        return dirty;
    }

    public PixelRegion RasterizeDab(
        DrawingLayer layer,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample sample,
        double velocity)
    {
        var pressure = Math.Pow(Math.Clamp(sample.Pressure, 0, 1), brush.PressureCurve);
        if (pressure <= 0.001) return PixelRegion.Empty;

        var velocity01 = Math.Clamp(velocity / 5000, 0, 1);
        var sizeFactor = brush.VelocityToSize
            ? Math.Clamp(1 - velocity01 * brush.VelocitySize, 0.1, 1)
            : 1.0;
        var opacityFactor = brush.VelocityToOpacity
            ? Math.Clamp(1 - velocity01 * brush.VelocityOpacity, 0.1, 1)
            : 1.0;
        var pressureSize = brush.PressureToSize ? pressure : 1.0;
        var pressureOpacity = brush.PressureToOpacity ? pressure : 1.0;
        var radius = Math.Max(0.5, brush.Size * pressureSize * sizeFactor * 0.5);
        var alpha = (eraser ? 1.0 : brush.Opacity) * opacityFactor * pressureOpacity;
        var grain = brush.Grain;
        var hasGrain = grain > 0;

        var bmpWidth = layer.Width;
        var bmpHeight = layer.Height;
        var minX = Math.Max(0, (int)Math.Floor(sample.X - radius));
        var minY = Math.Max(0, (int)Math.Floor(sample.Y - radius));
        var maxX = Math.Min(bmpWidth - 1, (int)Math.Ceiling(sample.X + radius));
        var maxY = Math.Min(bmpHeight - 1, (int)Math.Ceiling(sample.Y + radius));
        if (minX > maxX || minY > maxY) return PixelRegion.Empty;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5 - sample.X;
                var py = y + 0.5 - sample.Y;
                var dist = Math.Sqrt(px * px + py * py);
                if (dist > radius) continue;

                // Hard core (inner fraction = hardness) at full alpha,
                // smooth S-curve falloff across the soft outer ring.
                var t = dist / radius;
                double localAlpha;
                var h = Math.Max(0.001, brush.Hardness);
                if (t < h)
                {
                    localAlpha = alpha * brush.Flow;
                }
                else
                {
                    var s = (t - h) / (1.0 - h);               // 0..1 across soft zone
                    var fade = 1.0 - s * s * (3.0 - 2.0 * s);  // smoothstep
                    localAlpha = alpha * fade * brush.Flow;
                }

                if (hasGrain)
                {
                    // Noise value [0,1]; high grain = more texture, lower average opacity
                    var noise = GrainNoise(x, y);
                    localAlpha *= 1.0 - grain * (1.0 - noise);
                }

                layer.Pixels.GetPixel(x, y, out var b, out var g, out var r, out var a);
                if (eraser)
                    Erase(ref b, ref g, ref r, ref a, localAlpha);
                else
                    Blend(ref b, ref g, ref r, ref a, brush.Color, localAlpha);
                layer.Pixels.SetPixel(x, y, b, g, r, a);
            }
        }

        return new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public PixelRegion EstimateSegmentRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample from, CanvasInputSample to)
    {
        var maxPressure = Math.Max(Math.Clamp(from.Pressure, 0, 1), Math.Clamp(to.Pressure, 0, 1));
        var pressure = Math.Pow(maxPressure, brush.PressureCurve);
        var pressureSize = brush.PressureToSize ? pressure : 1.0;
        var radius = Math.Max(0.5, brush.Size * pressureSize * 0.5);
        var minX = (int)Math.Floor(Math.Min(from.X, to.X) - radius - 1);
        var minY = (int)Math.Floor(Math.Min(from.Y, to.Y) - radius - 1);
        var maxX = (int)Math.Ceiling(Math.Max(from.X, to.X) + radius + 1);
        var maxY = (int)Math.Ceiling(Math.Max(from.Y, to.Y) + radius + 1);
        return new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1)
            .ClipTo(layer.Width, layer.Height);
    }

    public PixelRegion EstimateDabRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample sample)
        => EstimateSegmentRegion(layer, brush, sample, sample);

    // Hash-based white noise returning [0, 1]
    private static double GrainNoise(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17;
            h *= 0xbf324c81u;
            h ^= h >> 13;
            h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0;
        }
    }

    private static void Blend(ref byte b, ref byte g, ref byte r, ref byte a, Color color, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        var srcAlpha = alpha * color.A / 255.0;
        var inv = 1 - srcAlpha;
        var dstAlpha = a / 255.0;
        var outAlpha = srcAlpha + dstAlpha * inv;

        b = (byte)Math.Clamp(color.B * srcAlpha + b * inv, 0, 255);
        g = (byte)Math.Clamp(color.G * srcAlpha + g * inv, 0, 255);
        r = (byte)Math.Clamp(color.R * srcAlpha + r * inv, 0, 255);
        a = (byte)Math.Clamp(outAlpha * 255, 0, 255);
    }

    private static void Erase(ref byte b, ref byte g, ref byte r, ref byte a, double alpha)
    {
        var keep = 1 - Math.Clamp(alpha, 0, 1);
        b = (byte)Math.Clamp(b * keep, 0, 255);
        g = (byte)Math.Clamp(g * keep, 0, 255);
        r = (byte)Math.Clamp(r * keep, 0, 255);
        a = (byte)Math.Clamp(a * keep, 0, 255);
    }
}
