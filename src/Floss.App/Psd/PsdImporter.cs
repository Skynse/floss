using System;
using System.IO;
using Floss.App.Document;

namespace Floss.App.Psd;

public static class PsdImporter
{
    public static void Import(Stream stream, DrawingDocument document)
    {
        var psd = PsdReader.Read(stream);

        document.ResizeForImport(psd.Width, psd.Height);
        document.ClearForImport();

        foreach (var node in psd.Layers)
            ImportNode(document, node, null, 0);

        document.FinalizeImport();
    }

    private static void ImportNode(DrawingDocument document, PsdNode node, DrawingLayer? parent, int depth)
    {
        switch (node)
        {
            case PsdGroupNode group:
            {
                var layer = document.AddLayerForImport(group.Name, isGroup: true);
                layer.Opacity   = group.Opacity / 255.0;
                layer.IsVisible = group.IsVisible;
                layer.BlendMode = MapBlendMode(group.BlendMode);
                layer.IsClipping = group.Clipping;
                layer.IndentLevel = depth;
                layer.IsOpen    = group.IsOpen;
                layer.Parent    = parent;
                parent?.Children.Add(layer);

                foreach (var child in group.Children)
                    ImportNode(document, child, layer, depth + 1);
                break;
            }

            case PsdLayerNode psdLayer:
            {
                var layer = document.AddLayerForImport(psdLayer.Name);
                layer.Opacity    = psdLayer.Opacity / 255.0;
                layer.IsVisible  = psdLayer.IsVisible;
                layer.BlendMode  = MapBlendMode(psdLayer.BlendMode);
                layer.IsClipping = psdLayer.Clipping;
                layer.IndentLevel = depth;
                layer.OffsetX    = psdLayer.Left;
                layer.OffsetY    = psdLayer.Top;
                layer.Parent     = parent;
                parent?.Children.Add(layer);

                if (psdLayer.Bgra != null && psdLayer.Width > 0 && psdLayer.Height > 0)
                    CopyPixels(layer, psdLayer, document.Width, document.Height);
                break;
            }
        }
    }

    private static unsafe void CopyPixels(DrawingLayer dst, PsdLayerNode src, int docW, int docH)
    {
        var srcW   = src.Width;
        var srcH   = src.Height;
        var left   = src.Left;
        var top    = src.Top;
        var srcBuf = src.Bgra!;

        // Clip copy region to document bounds
        var copyLeft   = Math.Max(0, -left);
        var copyTop    = Math.Max(0, -top);
        var copyRight  = Math.Min(srcW, docW - left);
        var copyBottom = Math.Min(srcH, docH - top);
        if (copyRight <= copyLeft || copyBottom <= copyTop) return;

        var dstLeft = Math.Max(0, left);
        var dstTop  = Math.Max(0, top);
        var copyW   = copyRight - copyLeft;
        var copyH   = copyBottom - copyTop;

        using var frame = dst.Bitmap.Lock();
        var dstPtr    = (byte*)frame.Address;
        var dstStride = frame.RowBytes;
        var srcStride = srcW * 4;

        for (int y = 0; y < copyH; y++)
        {
            var srcSpan = srcBuf.AsSpan((copyTop + y) * srcStride + copyLeft * 4, copyW * 4);
            var dstSpan = new Span<byte>(dstPtr + (dstTop + y) * dstStride + dstLeft * 4, copyW * 4);
            srcSpan.CopyTo(dstSpan);
        }
    }

    private static string MapBlendMode(string key) => key.TrimEnd() switch
    {
        "norm" => "Normal",
        "pass" => "PassThrough",
        "diss" => "Dissolve",
        "dark" => "Darken",
        "mul"  => "Multiply",
        "idiv" => "ColorBurn",
        "lbrn" => "LinearBurn",
        "dkCl" => "DarkerColor",
        "lite" => "Lighten",
        "scrn" => "Screen",
        "div"  => "ColorDodge",
        "lddg" => "LinearDodge",
        "lgCl" => "LighterColor",
        "over" => "Overlay",
        "sLit" => "SoftLight",
        "hLit" => "HardLight",
        "vLit" => "VividLight",
        "lLit" => "LinearLight",
        "pLit" => "PinLight",
        "hMix" => "HardMix",
        "diff" => "Difference",
        "smud" => "Exclusion",
        "fsub" => "Subtract",
        "fdiv" => "Divide",
        "hue"  => "Hue",
        "sat"  => "Saturation",
        "colr" => "Color",
        "lum"  => "Luminosity",
        _      => "Normal"
    };
}
