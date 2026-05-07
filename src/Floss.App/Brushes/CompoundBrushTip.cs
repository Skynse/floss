using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Floss.App.Brushes;

public enum StampLayerBlend
{
    Multiply,
    Screen,
    Add,
    Replace
}

public sealed record StampLayer
{
    public required IBrushTip Tip { get; init; }
    public StampLayerBlend Blend { get; init; } = StampLayerBlend.Replace;
    public float Opacity { get; init; } = 1.0f;
    public float Scale { get; init; } = 1.0f;
    public float Rotation { get; init; } = 0.0f;
}

public sealed class CompoundBrushTip : IBrushTip
{
    public IReadOnlyList<StampLayer> Layers { get; }

    public CompoundBrushTip(IReadOnlyList<StampLayer> layers)
    {
        Layers = layers.Count > 0 ? layers
            : [new StampLayer { Tip = new ProceduralBrushTip(), Blend = StampLayerBlend.Replace }];
    }

    public unsafe SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Max(1, baseSize);
        var result = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
        var dst = (byte*)result.GetPixels().ToPointer();
        var total = size * size;

        // init transparent
        for (var i = 0; i < total; i++) dst[i] = 0;

        for (var li = 0; li < Layers.Count; li++)
        {
            var layer = Layers[li];
            var first = li == 0;
            var lSize = Math.Max(1, (int)Math.Round(size * layer.Scale));
            var rawMask = layer.Tip.GenerateMask(lSize, hardness);
            var src = (byte*)rawMask.GetPixels().ToPointer();
            var lw = rawMask.Width;
            var lh = rawMask.Height;
            var opac = (int)Math.Clamp(layer.Opacity * 255, 0, 255);

            for (var y = 0; y < size; y++)
            {
                // tile/clamp sample from layer
                var ly = lh > 1 ? (y * lh / size) % lh : 0;
                for (var x = 0; x < size; x++)
                {
                    var lx = lw > 1 ? (x * lw / size) % lw : 0;
                    var sa = src[ly * lw + lx] * opac / 255;
                    var idx = y * size + x;
                    var existing = (int)dst[idx];

                    dst[idx] = (byte)(layer.Blend switch
                    {
                        StampLayerBlend.Replace => sa,
                        StampLayerBlend.Screen => 255 - (255 - existing) * (255 - sa) / 255,
                        StampLayerBlend.Add => Math.Min(255, existing + sa),
                        _ when first => sa,               // Multiply on empty = Replace
                        _ => existing * sa / 255
                    });
                }
            }

            // Child tips may cache and return a bitmap they still own. The compound
            // tip only owns the composed result created above.
        }

        return result;
    }
}
