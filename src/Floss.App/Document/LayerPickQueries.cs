using System.Collections.Generic;

namespace Floss.App.Document;

/// <summary>Hit-test layers for the Ctrl+Shift canvas layer picker.</summary>
public static class LayerPickQueries
{
    /// <summary>Top-to-bottom: every visible paint layer with opaque pixels at (x, y).</summary>
    public static List<int> FindLayersAtPoint(DrawingDocument document, int x, int y)
    {
        var found = new List<int>();
        var layers = document.Layers;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.IsGroup) continue;
            layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY, out _, out _, out _, out var a);
            if (a > 0)
                found.Add(i);
        }

        return found;
    }

    /// <summary>Top-to-bottom: visible paint layers with content inside the document rect.</summary>
    public static List<int> FindLayersInRect(DrawingDocument document, int x, int y, int w, int h)
    {
        var found = new List<int>();
        var layers = document.Layers;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.IsGroup) continue;

            var layX = x - layer.OffsetX;
            var layY = y - layer.OffsetY;
            var layW = layer.Width;
            var layH = layer.Height;

            if (layX >= layW || layY >= layH || layX + w <= 0 || layY + h <= 0)
                continue;

            var startX = System.Math.Max(0, layX);
            var startY = System.Math.Max(0, layY);
            var endX = System.Math.Min(layW, layX + w);
            var endY = System.Math.Min(layH, layY + h);

            var hasContent = false;
            for (var py = startY; py < endY && !hasContent; py++)
            {
                for (var px = startX; px < endX; px++)
                {
                    layer.Pixels.GetPixel(px, py, out _, out _, out _, out var a);
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
}
