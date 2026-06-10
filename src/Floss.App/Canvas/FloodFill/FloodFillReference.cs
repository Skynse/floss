using Floss.App.Config;
using Floss.App.Document;
using Floss.App.Tools;

namespace Floss.App.Canvas.FloodFill;

/// <summary>Builds flat BGRA reference composites for wand/fill (opacity-only; no blend modes).</summary>
public static class FloodFillReference
{
    public static byte[] BuildCompositeBuffer(ToolContext ctx, FillReferenceMode mode, int width, int height)
    {
        var buf = new byte[width * height * 4];
        foreach (var layer in ctx.Document.Layers)
        {
            if (!layer.IsVisible || layer.IsGroup) continue;
            if (mode == FillReferenceMode.ReferenceLayers && !layer.IsReference) continue;
            layer.Pixels.BlendOnto(buf, width, height, layer.Opacity);
        }

        return buf;
    }
}
