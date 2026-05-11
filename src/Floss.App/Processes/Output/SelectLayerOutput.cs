using System;
using System.Collections.Generic;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Selects the topmost visible layer at the clicked point, or layers within a drawn area.
public sealed class SelectLayerOutput : IOutputProcess
{
    public bool Antialiasing { get; set; }

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        // No live preview needed — selection happens on execute.
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        switch (input)
        {
            case ClickInput click:
                TryPickLayerAtPoint(ctx, (int)click.Point.X, (int)click.Point.Y);
                break;

            case RectInput rect:
                {
                    var x0 = (int)Math.Min(rect.Start.X, rect.End.X);
                    var y0 = (int)Math.Min(rect.Start.Y, rect.End.Y);
                    var w = (int)Math.Abs(rect.End.X - rect.Start.X) + 1;
                    var h = (int)Math.Abs(rect.End.Y - rect.Start.Y) + 1;
                    var found = FindLayersInRect(ctx, x0, y0, w, h);
                    if (found.Count > 0)
                        ctx.SelectLayers(found);
                    break;
                }

            case StrokeInput stroke when stroke.SmoothedSamples.Count > 0:
                {
                    // Use bounding box of the stroke for simplicity
                    var bounds = GetStrokeBounds(stroke.SmoothedSamples);
                    var found = FindLayersInRect(ctx, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    if (found.Count > 0)
                        ctx.SelectLayers(found);
                    break;
                }

            case PolygonInput poly when poly.SmoothedPoints.Count > 0:
                {
                    var bounds = GetStrokeBounds(poly.SmoothedPoints);
                    var found = FindLayersInRect(ctx, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    if (found.Count > 0)
                        ctx.SelectLayers(found);
                    break;
                }
        }
    }

    public void Cancel() { }

    public bool IsPaintOutput => false;

    private static void TryPickLayerAtPoint(ToolContext ctx, int x, int y)
    {
        var layers = ctx.Document.Layers;
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.IsGroup) continue;
            layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY, out _, out _, out _, out byte a);
            if (a > 0)
            {
                ctx.SelectLayer(i);
                return;
            }
        }
    }

    private static List<int> FindLayersInRect(ToolContext ctx, int x, int y, int w, int h)
    {
        var found = new List<int>();
        var layers = ctx.Document.Layers;
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.IsGroup) continue;

            var layX = x - layer.OffsetX;
            var layY = y - layer.OffsetY;
            var layW = layer.Width;
            var layH = layer.Height;

            // Check overlap
            if (layX >= layW || layY >= layH || layX + w <= 0 || layY + h <= 0)
                continue;

            // Check if any pixel in the overlap has content
            var startX = Math.Max(0, layX);
            var startY = Math.Max(0, layY);
            var endX = Math.Min(layW, layX + w);
            var endY = Math.Min(layH, layY + h);

            bool hasContent = false;
            for (int py = startY; py < endY && !hasContent; py++)
            {
                for (int px = startX; px < endX; px++)
                {
                    layer.Pixels.GetPixel(px, py, out _, out _, out _, out byte a);
                    if (a > 0)
                    {
                        hasContent = true;
                        break;
                    }
                }
            }

            if (hasContent)
                found.Add(i);
        }
        return found;
    }

    private static (int X, int Y, int Width, int Height) GetStrokeBounds(List<CanvasInputSample> samples)
    {
        if (samples.Count == 0) return (0, 0, 0, 0);
        double minX = samples[0].X, maxX = samples[0].X;
        double minY = samples[0].Y, maxY = samples[0].Y;
        foreach (var s in samples)
        {
            if (s.X < minX) minX = s.X;
            if (s.X > maxX) maxX = s.X;
            if (s.Y < minY) minY = s.Y;
            if (s.Y > maxY) maxY = s.Y;
        }
        return ((int)minX, (int)minY, (int)(maxX - minX) + 1, (int)(maxY - minY) + 1);
    }
}
