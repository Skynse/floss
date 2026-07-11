using System;
using System.IO;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;

namespace Floss.App.Psd;

public static class PsdImporter
{
    public static void Import(Stream stream, DrawingDocument document)
    {
        document.ReplaceWith(Load(stream));
    }

    public static DrawingDocument Load(Stream stream)
    {
        var psd = PsdReader.Read(stream);
        var document = new DrawingDocument(psd.Width, psd.Height);

        document.ClearForImport();

        foreach (var node in psd.Layers)
            ImportNode(document, node, null, 0);

        document.FinalizeImport();
        return document;
    }

    private static void ImportNode(DrawingDocument document, PsdNode node, DrawingLayer? parent, int depth)
    {
        switch (node)
        {
            case PsdGroupNode group:
                {
                    var layer = document.CreateLayerForImport(group.Name, isGroup: true);
                    layer.Opacity = group.Opacity / 255.0;
                    layer.IsVisible = group.IsVisible;
                    layer.BlendMode = MapBlendMode(group.BlendMode);
                    layer.IsClipping = group.Clipping;
                    layer.IndentLevel = depth;
                    layer.IsOpen = group.IsOpen;
                    layer.Parent = parent;
                    parent?.Children.Add(layer);

                    foreach (var child in group.Children)
                        ImportNode(document, child, layer, depth + 1);

                    document.AppendLayerForImport(layer);
                    break;
                }

            case PsdLayerNode psdLayer:
                {
                    var layer = document.AddLayerForImport(
                        psdLayer.Name,
                        bitmapWidth: Math.Max(1, psdLayer.Width),
                        bitmapHeight: Math.Max(1, psdLayer.Height));
                    layer.Opacity = psdLayer.Opacity / 255.0;
                    layer.IsVisible = psdLayer.IsVisible;
                    layer.BlendMode = MapBlendMode(psdLayer.BlendMode);
                    layer.IsClipping = psdLayer.Clipping;
                    layer.IndentLevel = depth;
                    layer.OffsetX = psdLayer.Left;
                    layer.OffsetY = psdLayer.Top;
                    layer.Parent = parent;
                    parent?.Children.Add(layer);

                    if (psdLayer.Bgra != null && psdLayer.Width > 0 && psdLayer.Height > 0)
                    {
                        CopyPixels(layer, psdLayer);
                        psdLayer.Bgra = null; // release decoded plane data immediately
                    }

                    if (psdLayer.MaskPlane != null && psdLayer.MaskRight > psdLayer.MaskLeft && psdLayer.MaskBottom > psdLayer.MaskTop)
                    {
                        layer.CreateMask();
                        var maskW = psdLayer.MaskRight - psdLayer.MaskLeft;
                        var maskH = psdLayer.MaskBottom - psdLayer.MaskTop;
                        var maskOffsetX = psdLayer.MaskLeft - psdLayer.Left;
                        var maskOffsetY = psdLayer.MaskTop - psdLayer.Top;
                        CopyMaskPixels(layer, psdLayer.MaskPlane, maskW, maskH, maskOffsetX, maskOffsetY, psdLayer.MaskDisabled);
                        psdLayer.MaskPlane = null;
                    }
                    break;
                }
        }
    }

    private static void CopyPixels(DrawingLayer dst, PsdLayerNode src)
    {
        dst.Pixels.CopyFromBgra(src.Bgra!, src.Width, src.Height);
        dst.MarkThumbnailDirty();
    }

    private static void CopyMaskPixels(DrawingLayer dst, byte[] maskPlane, int maskW, int maskH, int offsetX, int offsetY, bool disabled)
    {
        var docW = dst.Width;
        var docH = dst.Height;
        for (var y = 0; y < maskH; y++)
        {
            var docY = y + offsetY;
            if (docY < 0 || docY >= docH) continue;
            for (var x = 0; x < maskW; x++)
            {
                var docX = x + offsetX;
                if (docX < 0 || docX >= docW) continue;
                var v = maskPlane[y * maskW + x];
                dst.MaskPixels!.SetPixel(docX, docY, v, v, v, v);
            }
        }
        dst.IsMaskVisible = !disabled;
        dst.MarkMaskThumbnailDirty();
    }

    private static BlendMode MapBlendMode(string key) => key.TrimEnd() switch
    {
        "norm" => BlendMode.Normal,
        "pass" => BlendMode.PassThrough,
        "diss" => BlendMode.Dissolve,
        "dark" => BlendMode.Darken,
        "mul" => BlendMode.Multiply,
        "idiv" => BlendMode.ColorBurn,
        "lbrn" => BlendMode.LinearBurn,
        "dkCl" => BlendMode.DarkerColor,
        "lite" => BlendMode.Lighten,
        "scrn" => BlendMode.Screen,
        "div" => BlendMode.ColorDodge,
        "lddg" => BlendMode.LinearDodge,
        "lgCl" => BlendMode.LighterColor,
        "over" => BlendMode.Overlay,
        "sLit" => BlendMode.SoftLight,
        "hLit" => BlendMode.HardLight,
        "vLit" => BlendMode.VividLight,
        "lLit" => BlendMode.LinearLight,
        "pLit" => BlendMode.PinLight,
        "hMix" => BlendMode.HardMix,
        "diff" => BlendMode.Difference,
        "smud" => BlendMode.Exclusion,
        "fsub" => BlendMode.Subtract,
        "fdiv" => BlendMode.Divide,
        "hue" => BlendMode.Hue,
        "sat" => BlendMode.Saturation,
        "colr" => BlendMode.Color,
        "lum" => BlendMode.Luminosity,
        _ => BlendMode.Normal
    };
}
