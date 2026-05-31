using System;
using System.Collections.Generic;
using Floss.App.Canvas;
using Floss.App.Canvas.Compositing;
using SkiaSharp;

namespace Floss.App.Document;

/// <summary>
/// Brush pixel compositing. Alpha lock only blocks writes to fully transparent pixels;
/// all blend modes behave the same as unlocked otherwise.
/// </summary>
internal static class AlphaLockPixelOps
{
    public static void CompositeBrushPixel(
        ref byte dstB, ref byte dstG, ref byte dstR, ref byte dstA,
        byte srcB, byte srcG, byte srcR, byte srcA,
        bool alphaLocked,
        SKBlendMode mode)
    {
        if (alphaLocked && dstA == 0) return;

        var cover = srcA > 255 ? (byte)255 : srcA;

        switch (mode)
        {
            case SKBlendMode.DstOut:
                ApplyDstOut(ref dstB, ref dstG, ref dstR, ref dstA, cover);
                return;
            case SKBlendMode.Clear:
                dstB = dstG = dstR = dstA = 0;
                return;
            case SKBlendMode.SrcOver:
                ApplySrcOver(ref dstB, ref dstG, ref dstR, ref dstA, srcB, srcG, srcR, cover, alphaLocked);
                return;
            default:
                ApplySeparableBlend(ref dstB, ref dstG, ref dstR, ref dstA, srcB, srcG, srcR, cover, mode, alphaLocked);
                return;
        }
    }

    private static void ApplyDstOut(ref byte dstB, ref byte dstG, ref byte dstR, ref byte dstA, byte cover)
    {
        if (dstA == 0 || cover == 0) return;
        // DstOut for unpremultiplied alpha: only alpha is reduced by the brush
        // mask. Color channels remain unchanged so partially-erased pixels keep
        // their original hue/saturation and only become more transparent.
        var keep = 255 - cover;
        dstA = (byte)(dstA * keep / 255);
    }

    private static void ApplySrcOver(
        ref byte dstB, ref byte dstG, ref byte dstR, ref byte dstA,
        byte srcB, byte srcG, byte srcR, byte cover,
        bool alphaLocked)
    {
        if (cover == 0) return;

        if (alphaLocked)
        {
            var inv = 255 - cover;
            dstB = (byte)((srcB * cover + dstB * inv) / 255);
            dstG = (byte)((srcG * cover + dstG * inv) / 255);
            dstR = (byte)((srcR * cover + dstR * inv) / 255);
            return;
        }

        if (dstA == 0)
        {
            dstB = srcB;
            dstG = srcG;
            dstR = srcR;
            dstA = cover;
            return;
        }

        var invSrcA = 255 - cover;
        var outA = cover + (dstA * invSrcA + 127) / 255;
        if (outA <= 0) return;
        dstB = (byte)((srcB * cover + dstB * dstA * invSrcA / 255) / outA);
        dstG = (byte)((srcG * cover + dstG * dstA * invSrcA / 255) / outA);
        dstR = (byte)((srcR * cover + dstR * dstA * invSrcA / 255) / outA);
        dstA = (byte)outA;
    }

    private static void ApplySeparableBlend(
        ref byte dstB, ref byte dstG, ref byte dstR, ref byte dstA,
        byte srcB, byte srcG, byte srcR, byte cover,
        SKBlendMode mode,
        bool alphaLocked)
    {
        if (cover == 0) return;

        var blendMode = SkBlendModeToLayerName(mode);
        if (blendMode == null)
        {
            ApplySrcOver(ref dstB, ref dstG, ref dstR, ref dstA, srcB, srcG, srcR, cover, alphaLocked);
            return;
        }

        var sa = cover / 255.0;
        var da = dstA / 255.0;
        var sr = srcR / 255.0;
        var sg = srcG / 255.0;
        var sb = srcB / 255.0;
        var dr = dstR / 255.0;
        var dg = dstG / 255.0;
        var db = dstB / 255.0;

        var (br, bg, bb) = LayerCompositorPixelOps.ApplyBlendMode(sr, sg, sb, sa, dr, dg, db, da, blendMode.Value);

        if (alphaLocked)
        {
            var inv = 1.0 - sa;
            dstR = (byte)Math.Clamp((br * sa + dr * inv) * 255.0 + 0.5, 0, 255);
            dstG = (byte)Math.Clamp((bg * sa + dg * inv) * 255.0 + 0.5, 0, 255);
            dstB = (byte)Math.Clamp((bb * sa + db * inv) * 255.0 + 0.5, 0, 255);
            return;
        }

        if (da <= 0)
        {
            dstR = srcR;
            dstG = srcG;
            dstB = srcB;
            dstA = cover;
            return;
        }

        var outA = sa + da * (1.0 - sa);
        if (outA <= 0) return;
        dstR = (byte)Math.Clamp((br * sa + dr * da * (1.0 - sa)) / outA * 255.0 + 0.5, 0, 255);
        dstG = (byte)Math.Clamp((bg * sa + dg * da * (1.0 - sa)) / outA * 255.0 + 0.5, 0, 255);
        dstB = (byte)Math.Clamp((bb * sa + db * da * (1.0 - sa)) / outA * 255.0 + 0.5, 0, 255);
        dstA = (byte)Math.Clamp(outA * 255.0 + 0.5, 0, 255);
    }

    private static BlendMode? SkBlendModeToLayerName(SKBlendMode mode) => mode switch
    {
        SKBlendMode.Multiply => BlendMode.Multiply,
        SKBlendMode.Screen => BlendMode.Screen,
        SKBlendMode.Overlay => BlendMode.Overlay,
        SKBlendMode.Darken => BlendMode.Darken,
        SKBlendMode.Lighten => BlendMode.Lighten,
        SKBlendMode.ColorDodge => BlendMode.ColorDodge,
        SKBlendMode.ColorBurn => BlendMode.ColorBurn,
        SKBlendMode.HardLight => BlendMode.HardLight,
        SKBlendMode.SoftLight => BlendMode.SoftLight,
        SKBlendMode.Difference => BlendMode.Difference,
        SKBlendMode.Exclusion => BlendMode.Exclusion,
        _ => null
    };

    /// <summary>Replace pixel color; when alpha locked, only updates RGB on existing alpha.</summary>
    public static void SetColor(
        ref byte dstB, ref byte dstG, ref byte dstR, ref byte dstA,
        byte srcB, byte srcG, byte srcR, byte srcA,
        bool alphaLocked)
    {
        if (alphaLocked)
        {
            if (dstA == 0) return;
            dstB = srcB;
            dstG = srcG;
            dstR = srcR;
            return;
        }

        dstB = srcB;
        dstG = srcG;
        dstR = srcR;
        dstA = srcA;
    }

    /// <summary>Src-over blend for semi-transparent source; preserves dst alpha when locked.</summary>
    public static void BlendSrcOverColor(
        ref byte dstB, ref byte dstG, ref byte dstR, ref byte dstA,
        byte srcB, byte srcG, byte srcR, byte srcA,
        bool alphaLocked)
    {
        if (srcA == 0) return;
        if (alphaLocked)
        {
            if (dstA == 0) return;
            var srcAf = srcA / 255f;
            var inv = 1f - srcAf;
            dstB = (byte)((srcB * srcAf + dstB * inv) + 0.5f);
            dstG = (byte)((srcG * srcAf + dstG * inv) + 0.5f);
            dstR = (byte)((srcR * srcAf + dstR * inv) + 0.5f);
            return;
        }

        if (srcA == 255)
        {
            dstB = srcB;
            dstG = srcG;
            dstR = srcR;
            dstA = srcA;
            return;
        }

        var dstAf = dstA / 255f;
        var outAf = srcA / 255f + dstAf * (1f - srcA / 255f);
        if (outAf <= 0f) return;
        dstB = (byte)((srcB * (srcA / 255f) + dstB * dstAf * (1f - srcA / 255f)) / outAf + 0.5f);
        dstG = (byte)((srcG * (srcA / 255f) + dstG * dstAf * (1f - srcA / 255f)) / outAf + 0.5f);
        dstR = (byte)((srcR * (srcA / 255f) + dstR * dstAf * (1f - srcA / 255f)) / outAf + 0.5f);
        dstA = (byte)(outAf * 255f + 0.5f);
    }

    /// <summary>Read, apply fill/blend, write. Skips transparent pixels when alpha locked.</summary>
    public static bool TryWriteColor(
        TiledPixelBuffer pixels, int x, int y,
        byte srcB, byte srcG, byte srcR, byte srcA,
        bool alphaLocked,
        SKBlendMode blendMode = SKBlendMode.SrcOver)
    {
        if (srcA == 0) return false;
        pixels.GetPixel(x, y, out byte db, out byte dg, out byte dr, out byte da);
        if (alphaLocked && da == 0) return false;
        CompositeBrushPixel(ref db, ref dg, ref dr, ref da, srcB, srcG, srcR, srcA, alphaLocked, blendMode);
        pixels.SetPixel(x, y, db, dg, dr, da);
        return true;
    }

    /// <summary>After Skia render, revert pixels that were transparent before the dab.</summary>
    public static void RestoreLockedTransparentPixels(
        TiledPixelBuffer pixels,
        PixelRegion dirty,
        IReadOnlyDictionary<(int X, int Y), byte[]?> beforeTiles)
    {
        if (dirty.IsEmpty) return;

        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(dirty.X, ts);
        var firstTileY = FloorDiv(dirty.Y, ts);
        var lastTileX = FloorDiv(dirty.Right - 1, ts);
        var lastTileY = FloorDiv(dirty.Bottom - 1, ts);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            var tilePixY = ty * ts;
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                if (!beforeTiles.TryGetValue((tx, ty), out var beforeTile))
                    continue;

                var pxMin = Math.Max(dirty.X, tx * ts);
                var pxMax = Math.Min(dirty.Right, tx * ts + ts);
                var pyMin = Math.Max(dirty.Y, ty * ts);
                var pyMax = Math.Min(dirty.Bottom, ty * ts + ts);
                if (pxMin >= pxMax || pyMin >= pyMax) continue;

                byte[]? liveTile = null;
                for (var py = pyMin; py < pyMax; py++)
                {
                    var ly = py - tilePixY;
                    var rowBase = ly * ts * 4;
                    for (var px = pxMin; px < pxMax; px++)
                    {
                        var lx = px - tx * ts;
                        var offset = rowBase + lx * 4;
                        var hadAlpha = beforeTile != null && beforeTile[offset + 3] > 0;
                        if (hadAlpha) continue;

                        liveTile ??= pixels.GetOrCreateRawTile(tx, ty);
                        if (beforeTile != null)
                        {
                            liveTile[offset] = beforeTile[offset];
                            liveTile[offset + 1] = beforeTile[offset + 1];
                            liveTile[offset + 2] = beforeTile[offset + 2];
                            liveTile[offset + 3] = beforeTile[offset + 3];
                        }
                        else
                        {
                            liveTile[offset] = 0;
                            liveTile[offset + 1] = 0;
                            liveTile[offset + 2] = 0;
                            liveTile[offset + 3] = 0;
                        }
                    }
                }
            }
        }
    }

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);
}
