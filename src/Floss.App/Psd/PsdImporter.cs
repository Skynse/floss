using System;
using System.IO;
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

                document.AppendLayerForImport(layer);
                break;
            }

            case PsdLayerNode psdLayer:
            {
                var layer = document.AddLayerForImport(
                    psdLayer.Name,
                    bitmapWidth: Math.Max(1, psdLayer.Width),
                    bitmapHeight: Math.Max(1, psdLayer.Height));
                layer.Opacity    = psdLayer.Opacity / 255.0;
                layer.IsVisible  = psdLayer.IsVisible;
                layer.BlendMode  = MapBlendMode(psdLayer.BlendMode);
                layer.IsClipping  = psdLayer.Clipping;
                layer.IndentLevel = depth;
                layer.OffsetX     = psdLayer.Left;
                layer.OffsetY     = psdLayer.Top;
                layer.Parent      = parent;
                parent?.Children.Add(layer);

                if (psdLayer.Bgra != null && psdLayer.Width > 0 && psdLayer.Height > 0)
                {
                    CopyPixels(layer, psdLayer);
                    psdLayer.Bgra = null; // release decoded plane data immediately
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
