using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Avalonia.Media;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

internal static class LayerCompositorPixelOps
{
    internal const int MonochromeThreshold = 128;

    internal static unsafe void CompositeBgraBuffer(
        byte* dst,
        int dstStride,
        byte* src,
        int srcStride,
        BlendMode blendMode,
        double opacity,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (opacity <= 0) return;

        if (blendMode == BlendMode.Normal)
        {
            var opacityByte = (uint)Math.Round(opacity * 255);
            for (var docY = clip.Y; docY < clip.Bottom; docY++)
            {
                var srcRow = src + (docY - clip.Y) * srcStride;
                var dstRow = dst + (docY - originY) * dstStride;
                var dstPtr = dstRow + (clip.X - originX) * 4;
                CompositeNormalRowManaged.Composite(dstPtr, srcRow, clip.Width, opacityByte);
            }
            return;
        }

        for (var docY = clip.Y; docY < clip.Bottom; docY++)
        {
            var srcRow = src + (docY - clip.Y) * srcStride;
            var dstRow = dst + (docY - originY) * dstStride;

            for (var docX = clip.X; docX < clip.Right; docX++)
            {
                var srcIdx = (docX - clip.X) * 4;
                var rawA = srcRow[srcIdx + 3];
                if (rawA == 0) continue;

                var dstIdx = (docX - originX) * 4;
                var srcA = rawA / 255.0 * opacity;
                if (srcA <= 0) continue;

                var srcB = srcRow[srcIdx + 0] / 255.0;
                var srcG = srcRow[srcIdx + 1] / 255.0;
                var srcR = srcRow[srcIdx + 2] / 255.0;
                var dstB = dstRow[dstIdx + 0] / 255.0;
                var dstG = dstRow[dstIdx + 1] / 255.0;
                var dstR = dstRow[dstIdx + 2] / 255.0;
                var dstA = dstRow[dstIdx + 3] / 255.0;

                var (blendR, blendG, blendB) = ApplyBlendMode(srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendMode);
                BlendPixel(dstRow + dstIdx, srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendR, blendG, blendB);
            }
        }
    }

    /// <summary>
    /// Alpha-preserving merge of a BGRA buffer onto dst. Used when a clip group
    /// (isolated group as clip layer) must be merged without altering dst alpha.
    /// </summary>
    internal static unsafe void CompositeBgraBufferAlphaPreserving(
        byte* dst, int dstStride, byte* src, int srcStride,
        BlendMode blendMode, double opacity, PixelRegion clip, int originX, int originY)
    {
        if (opacity <= 0) return;
        var isNormal = blendMode == BlendMode.Normal;
        var hasLut = HasLut(blendMode);
        var lut = hasLut ? GetLut(blendMode) : null;
        var opacityByte = (uint)Math.Round(opacity * 255);

        for (var docY = clip.Y; docY < clip.Bottom; docY++)
        {
            var srcRow = src + (docY - clip.Y) * srcStride;
            var dstRow = dst + (docY - originY) * dstStride;

            for (var docX = clip.X; docX < clip.Right; docX++)
            {
                var srcIdx = (docX - clip.X) * 4;
                uint rawA = srcRow[srcIdx + 3];
                if (rawA == 0) continue;

                uint srcA = (rawA * opacityByte + 127) / 255;
                if (srcA == 0) continue;

                var dstIdx = (docX - originX) * 4;
                byte srcB = srcRow[srcIdx + 0];
                byte srcG = srcRow[srcIdx + 1];
                byte srcR = srcRow[srcIdx + 2];

                if (isNormal)
                {
                    BlendColorOnly(dstRow + dstIdx, srcB, srcG, srcR, srcA);
                }
                else if (hasLut)
                {
                    uint db = dstRow[dstIdx + 0], dg = dstRow[dstIdx + 1], dr = dstRow[dstIdx + 2];
                    uint blendedR = lut![((uint)srcR << 8) | dr];
                    uint blendedG = lut![((uint)srcG << 8) | dg];
                    uint blendedB = lut![((uint)srcB << 8) | db];
                    BlendColorOnly(dstRow + dstIdx, (byte)blendedB, (byte)blendedG, (byte)blendedR, srcA);
                }
                else
                {
                    double sB = srcB / 255.0;
                    double sG = srcG / 255.0;
                    double sR = srcR / 255.0;
                    double sA = srcA / 255.0;
                    double dB = dstRow[dstIdx + 0] / 255.0;
                    double dG = dstRow[dstIdx + 1] / 255.0;
                    double dR = dstRow[dstIdx + 2] / 255.0;
                    double dA = dstRow[dstIdx + 3] / 255.0;
                    var (blendR, blendG, blendB) = ApplyBlendMode(sR, sG, sB, sA, dR, dG, dB, dA, blendMode);
                    double outR = blendR * sA + dR * (1.0 - sA);
                    double outG = blendG * sA + dG * (1.0 - sA);
                    double outB = blendB * sA + dB * (1.0 - sA);
                    dstRow[dstIdx + 0] = (byte)Math.Clamp((int)(outB * 255.0 + 0.5), 0, 255);
                    dstRow[dstIdx + 1] = (byte)Math.Clamp((int)(outG * 255.0 + 0.5), 0, 255);
                    dstRow[dstIdx + 2] = (byte)Math.Clamp((int)(outR * 255.0 + 0.5), 0, 255);
                    // dst alpha preserved
                }
            }
        }
    }

    internal static unsafe void CompositeProjectionBuffer(
        byte* dst,
        int dstStride,
        TiledPixelBuffer projection,
        BlendMode blendMode,
        double opacity,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (opacity <= 0 || !projection.HasContentTiles(clip)) return;

        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(clip.X, ts);
        var firstTileY = FloorDiv(clip.Y, ts);
        var lastTileX = FloorDiv(clip.Right - 1, ts);
        var lastTileY = FloorDiv(clip.Bottom - 1, ts);

        if (blendMode == BlendMode.Normal)
        {
            var opacityByte = (uint)Math.Round(opacity * 255);

            for (var ty = firstTileY; ty <= lastTileY; ty++)
            {
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var tile = projection.GetTileOrNull(tx, ty);
                    if (tile == null) continue;

                    var tileLeft = Math.Max(clip.X, tx * ts);
                    var tileTop = Math.Max(clip.Y, ty * ts);
                    var tileRight = Math.Min(clip.Right, tx * ts + ts);
                    var tileBottom = Math.Min(clip.Bottom, ty * ts + ts);

                    for (int docY = tileTop; docY < tileBottom; docY++)
                    {
                        var tileLocalY = docY - ty * ts;
                        var tileLocalX0 = tileLeft - tx * ts;
                        var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                        var dstRow = dst + (docY - originY) * dstStride;
                        var rowWidth = tileRight - tileLeft;
                        var dstPtr = dstRow + (tileLeft - originX) * 4;
                        fixed (byte* tileFix = tile)
                            CompositeNormalRowManaged.Composite(dstPtr, tileFix + tileRowBase, rowWidth, opacityByte);
                    }
                }
            }
            return;
        }

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var tile = projection.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                var tileLeft = Math.Max(clip.X, tx * ts);
                var tileTop = Math.Max(clip.Y, ty * ts);
                var tileRight = Math.Min(clip.Right, tx * ts + ts);
                var tileBottom = Math.Min(clip.Bottom, ty * ts + ts);

                for (int docY = tileTop; docY < tileBottom; docY++)
                {
                    var tileLocalY = docY - ty * ts;
                    var tileLocalX0 = tileLeft - tx * ts;
                    var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                    var dstRow = dst + (docY - originY) * dstStride;

                    for (int j = 0, docX = tileLeft; docX < tileRight; docX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        uint rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;

                        var srcA = rawA / 255.0 * opacity;
                        var dstIdx = (docX - originX) * 4;
                        var srcB = tile[tileOffset + 0] / 255.0;
                        var srcG = tile[tileOffset + 1] / 255.0;
                        var srcR = tile[tileOffset + 2] / 255.0;
                        var dstB = dstRow[dstIdx + 0] / 255.0;
                        var dstG = dstRow[dstIdx + 1] / 255.0;
                        var dstR = dstRow[dstIdx + 2] / 255.0;
                        var dstA = dstRow[dstIdx + 3] / 255.0;

                        var (blendR, blendG, blendB) = ApplyBlendMode(srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendMode);
                        BlendPixel(dstRow + dstIdx, srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendR, blendG, blendB);
                    }
                }
            }
        }
    }

    internal static unsafe void CompositeClippedLayer(
        byte* dst,
        int dstStride,
        int width,
        int height,
        DrawingLayer layer,
        DrawingLayer baseLayer,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        var opacity = layer.Opacity * opacityScale;
        if (opacity <= 0) return;

        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;
        var baseOffsetX = baseLayer.OffsetX;
        var baseOffsetY = baseLayer.OffsetY;
        var baseMinX = baseLayer.MinX;
        var baseMaxX = baseLayer.MaxX;
        var baseMinY = baseLayer.MinY;
        var baseMaxY = baseLayer.MaxY;

        var docLeft = Math.Max(Math.Max(clip.X, offsetX + layer.MinX), 0);
        var docTop = Math.Max(Math.Max(clip.Y, offsetY + layer.MinY), 0);
        var docRight = Math.Min(Math.Min(clip.Right, offsetX + layer.MaxX), width + originX);
        var docBottom = Math.Min(Math.Min(clip.Bottom, offsetY + layer.MaxY), height + originY);

        if (docLeft >= docRight || docTop >= docBottom) return;

        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        var pixels = layer.Pixels;
        var basePixels = baseLayer.Pixels;
        var hasMask = layer.HasMask && layer.IsMaskVisible;
        var maskPixels = layer.MaskPixels;
        if (!pixels.HasContentTiles(sourceRegion)) return;

        pixels.EnterPixelReadLock();
        basePixels.EnterPixelReadLock();
        if (hasMask) maskPixels!.EnterPixelReadLock();
        try
        {

        var blendMode = layer.BlendMode;
        var layerColor = layer.LayerColor;
        var hasLayerColor = layerColor.HasValue;
        var expressionColor = layer.ExpressionColor;
        var applyExpr = expressionColor != ExpressionColorMode.Color;
        byte lcR = 255, lcG = 255, lcB = 255;
        if (layerColor is { } lc) { lcR = lc.R; lcG = lc.G; lcB = lc.B; }
        const int ts = TiledPixelBuffer.TileSize;

        var firstTileX = FloorDiv(sourceRegion.X, ts);
        var firstTileY = FloorDiv(sourceRegion.Y, ts);
        var lastTileX = FloorDiv(sourceRegion.Right - 1, ts);
        var lastTileY = FloorDiv(sourceRegion.Bottom - 1, ts);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var tile = pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                byte[]? maskTile = null;
                if (hasMask)
                    maskTile = maskPixels!.GetTileOrNull(tx, ty);

                var clipLeft = Math.Max(sourceRegion.X, tx * ts);
                var clipTop = Math.Max(sourceRegion.Y, ty * ts);
                var clipRight = Math.Min(sourceRegion.Right, tx * ts + ts);
                var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);

                var isNormal = blendMode == BlendMode.Normal;
                var opacityInt = (uint)Math.Round(opacity * 255);

                for (int srcY = clipTop; srcY < clipBottom; srcY++)
                {
                    var tileLocalY = srcY - ty * ts;
                    var tileLocalX0 = clipLeft - tx * ts;
                    var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                    var docY = srcY + offsetY;
                    var dstRow = dst + (docY - originY) * dstStride;
                    var baseY = docY - baseOffsetY;

                    if (baseY < baseMinY || baseY >= baseMaxY) continue;

                    var baseTileY = FloorDiv(baseY, ts);
                    var baseTileLocalY = baseY - baseTileY * ts;

                    int prevBaseTileX = int.MinValue;
                    byte[]? baseTile = null;

                    for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        uint rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;
                        if (hasMask)
                        {
                            if (maskTile == null) continue;
                            uint ma = maskTile[tileOffset + 3];
                            if (ma == 0) continue;
                            rawA = (rawA * ma + 127) / 255;
                            if (rawA == 0) continue;
                        }

                        var docX = srcX + offsetX;
                        var baseX = docX - baseOffsetX;
                        if (baseX < baseMinX || baseX >= baseMaxX) continue;

                        var baseTileX = FloorDiv(baseX, ts);
                        if (baseTileX != prevBaseTileX)
                        {
                            baseTile = basePixels.GetTileOrNull(baseTileX, baseTileY);
                            prevBaseTileX = baseTileX;
                        }

                        uint baseAlphaByte = 0;
                        if (baseTile != null)
                        {
                            var blx = baseX - baseTileX * ts;
                            baseAlphaByte = baseTile[(baseTileLocalY * ts + blx) * 4 + 3];
                        }
                        if (baseAlphaByte == 0) continue;

                        var dstIdx = (docX - originX) * 4;

                        if (isNormal)
                        {
                            byte srcB = tile[tileOffset + 0], srcG = tile[tileOffset + 1], srcR = tile[tileOffset + 2];
                            if (hasLayerColor)
                                ApplyLayerColor(ref srcB, ref srcG, ref srcR, lcB, lcG, lcR);
                            if (applyExpr && !ApplyExpressionColorToSource(ref srcB, ref srcG, ref srcR, ref rawA, expressionColor))
                                continue;

                            uint srcA = (rawA * opacityInt + 127) / 255;
                            srcA = (srcA * baseAlphaByte + 127) / 255;
                            if (srcA == 0) continue;

                            var dstPtr = dstRow + dstIdx;
                            if (srcA == 255)
                            {
                                dstPtr[0] = srcB;
                                dstPtr[1] = srcG;
                                dstPtr[2] = srcR;
                                dstPtr[3] = 255;
                                continue;
                            }

                            uint invSrcA = 255 - srcA;
                            uint dstA = dstPtr[3];
                            uint dstCont = (dstA * invSrcA + 127) / 255;
                            uint outA = srcA + dstCont;
                            if (outA == 0) continue;

                            uint half = outA >> 1;
                            dstPtr[0] = (byte)((srcB * srcA + dstPtr[0] * dstCont + half) / outA);
                            dstPtr[1] = (byte)((srcG * srcA + dstPtr[1] * dstCont + half) / outA);
                            dstPtr[2] = (byte)((srcR * srcA + dstPtr[2] * dstCont + half) / outA);
                            dstPtr[3] = (byte)outA;
                        }
                        else
                        {
                            byte srcBByte = tile[tileOffset + 0], srcGByte = tile[tileOffset + 1], srcRByte = tile[tileOffset + 2];
                            if (hasLayerColor)
                                ApplyLayerColor(ref srcBByte, ref srcGByte, ref srcRByte, lcB, lcG, lcR);
                            if (applyExpr && !ApplyExpressionColorToSource(ref srcBByte, ref srcGByte, ref srcRByte, ref rawA, expressionColor))
                                continue;

                            var srcA = rawA / 255.0 * opacity * (baseAlphaByte / 255.0);
                            if (srcA <= 0) continue;

                            var srcB = srcBByte / 255.0;
                            var srcG = srcGByte / 255.0;
                            var srcR = srcRByte / 255.0;
                            var dstB = dstRow[dstIdx + 0] / 255.0;
                            var dstG = dstRow[dstIdx + 1] / 255.0;
                            var dstR = dstRow[dstIdx + 2] / 255.0;
                            var dstA = dstRow[dstIdx + 3] / 255.0;

                            var (blendR, blendG, blendB) = ApplyBlendMode(srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendMode);
                            BlendPixel(dstRow + dstIdx, srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendR, blendG, blendB);
                        }
                    }
                }
            }
        }
        }
        finally
        {
            if (hasMask) maskPixels!.ExitPixelReadLock();
            basePixels.ExitPixelReadLock();
            pixels.ExitPixelReadLock();
        }
    }

    internal static unsafe void CompositeClippedBuffer(
        byte* dst,
        int dstStride,
        int width,
        int height,
        byte* src,
        int srcStride,
        DrawingLayer baseLayer,
        BlendMode blendMode,
        PixelRegion clip,
        int originX,
        int originY)
    {
        var baseMinX = baseLayer.MinX;
        var baseMaxX = baseLayer.MaxX;
        var baseMinY = baseLayer.MinY;
        var baseMaxY = baseLayer.MaxY;
        var baseOffsetX = baseLayer.OffsetX;
        var baseOffsetY = baseLayer.OffsetY;

        const int ts = TiledPixelBuffer.TileSize;

        var basePixels = baseLayer.Pixels;
        basePixels.EnterPixelReadLock();
        try
        {
        for (int docY = clip.Y; docY < clip.Bottom; docY++)
        {
            var baseY = docY - baseOffsetY;
            if (baseY < baseMinY || baseY >= baseMaxY) continue;

            var srcRow = src + (docY - clip.Y) * srcStride;
            var dstRow = dst + (docY - originY) * dstStride;

            var baseTileY = FloorDiv(baseY, ts);
            var baseTileLocalY = baseY - baseTileY * ts;
            int prevBaseTileX = int.MinValue;
            byte[]? baseTile = null;

            for (int docX = clip.X; docX < clip.Right; docX++)
            {
                var srcIdx = (docX - clip.X) * 4;
                var rawA = srcRow[srcIdx + 3];
                if (rawA == 0) continue;

                var baseX = docX - baseOffsetX;
                if (baseX < baseMinX || baseX >= baseMaxX) continue;

                var baseTileX = FloorDiv(baseX, ts);
                if (baseTileX != prevBaseTileX)
                {
                    baseTile = basePixels.GetTileOrNull(baseTileX, baseTileY);
                    prevBaseTileX = baseTileX;
                }

                uint baseAlphaByte = 0;
                if (baseTile != null)
                {
                    var blx = baseX - baseTileX * ts;
                    baseAlphaByte = baseTile[(baseTileLocalY * ts + blx) * 4 + 3];
                }
                if (baseAlphaByte == 0) continue;

                var dstIdx = (docX - originX) * 4;
                var srcA = rawA / 255.0 * (baseAlphaByte / 255.0);
                if (srcA <= 0) continue;

                var srcB = srcRow[srcIdx + 0] / 255.0;
                var srcG = srcRow[srcIdx + 1] / 255.0;
                var srcR = srcRow[srcIdx + 2] / 255.0;
                var dstB = dstRow[dstIdx + 0] / 255.0;
                var dstG = dstRow[dstIdx + 1] / 255.0;
                var dstR = dstRow[dstIdx + 2] / 255.0;
                var dstA = dstRow[dstIdx + 3] / 255.0;

                var (blendR, blendG, blendB) = ApplyBlendMode(srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendMode);
                BlendPixel(dstRow + dstIdx, srcR, srcG, srcB, srcA, dstR, dstG, dstB, dstA, blendR, blendG, blendB);
            }
        }
        }
        finally { basePixels.ExitPixelReadLock(); }
    }

    // ── Blend LUTs ─────────────────────────────────────────────────────────--
    internal static readonly byte[] LUT_Overlay = BuildLUT((s, d) => Overlay(s, d));
    internal static readonly byte[] LUT_Multiply = BuildLUT((s, d) => s * d);
    internal static readonly byte[] LUT_Screen = BuildLUT((s, d) => 1.0 - (1.0 - s) * (1.0 - d));
    internal static readonly byte[] LUT_SoftLight = BuildLUT((s, d) => SoftLight(s, d));
    internal static readonly byte[] LUT_HardLight = BuildLUT((s, d) => HardLight(s, d));
    internal static readonly byte[] LUT_ColorDodge = BuildLUT((s, d) => ColorDodge(s, d));
    internal static readonly byte[] LUT_ColorBurn = BuildLUT((s, d) => ColorBurn(s, d));
    internal static readonly byte[] LUT_LinearBurn = BuildLUT((s, d) => Math.Max(0, d + s - 1));
    internal static readonly byte[] LUT_LinearDodge = BuildLUT((s, d) => Math.Min(1, d + s));
    internal static readonly byte[] LUT_VividLight = BuildLUT((s, d) => VividLight(s, d));
    internal static readonly byte[] LUT_LinearLight = BuildLUT((s, d) => LinearLight(s, d));
    internal static readonly byte[] LUT_PinLight = BuildLUT((s, d) => PinLight(s, d));
    internal static readonly byte[] LUT_HardMix = BuildLUT((s, d) => HardMix(s, d));
    internal static readonly byte[] LUT_Subtract = BuildLUT((s, d) => Math.Max(0, d - s));
    internal static readonly byte[] LUT_Divide = BuildLUT((s, d) => s <= 0 ? 1.0 : Math.Min(1.0, d / s));

    internal static byte[] BuildLUT(Func<double, double, double> fn)
    {
        var lut = new byte[65536];
        for (int s = 0; s < 256; s++)
            for (int d = 0; d < 256; d++)
                lut[(s << 8) | d] = (byte)Math.Clamp(Math.Round(fn(s / 255.0, d / 255.0) * 255), 0, 255);
        return lut;
    }

    internal static byte[] GetLut(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => LUT_Multiply,
        BlendMode.Screen => LUT_Screen,
        BlendMode.Overlay => LUT_Overlay,
        BlendMode.SoftLight => LUT_SoftLight,
        BlendMode.HardLight => LUT_HardLight,
        BlendMode.ColorDodge => LUT_ColorDodge,
        BlendMode.ColorBurn => LUT_ColorBurn,
        BlendMode.LinearBurn => LUT_LinearBurn,
        BlendMode.LinearDodge => LUT_LinearDodge,
        BlendMode.VividLight => LUT_VividLight,
        BlendMode.LinearLight => LUT_LinearLight,
        BlendMode.PinLight => LUT_PinLight,
        BlendMode.HardMix => LUT_HardMix,
        BlendMode.Subtract => LUT_Subtract,
        BlendMode.Divide => LUT_Divide,
        _ => null!
    };

    internal static bool HasLut(BlendMode mode) => mode.HasLut();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void BlendPixelInt(byte* dst,
        uint srcR, uint srcG, uint srcB, uint srcA,
        uint dstR, uint dstG, uint dstB, uint dstA)
    {
        if (srcA == 0) return;
        if (dstA == 0)
        {
            dst[0] = (byte)((srcB * srcA + 127) / 255);
            dst[1] = (byte)((srcG * srcA + 127) / 255);
            dst[2] = (byte)((srcR * srcA + 127) / 255);
            dst[3] = (byte)srcA;
            return;
        }
        uint outA = srcA + ((dstA * (255 - srcA)) >> 8);
        if (outA == 0) return;
        uint half = outA >> 1;
        uint dstCont = (dstA * (255 - srcA)) >> 8;
        dst[0] = (byte)((srcB * srcA + dstB * dstCont + half) / outA);
        dst[1] = (byte)((srcG * srcA + dstG * dstCont + half) / outA);
        dst[2] = (byte)((srcR * srcA + dstR * dstCont + half) / outA);
        dst[3] = (byte)outA;
    }

    internal static unsafe void CompositeLayer(byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, double opacityScale, PixelRegion clip, int originX, int originY,
        BlendMode? blendModeOverride = null, double? opacityOverride = null)
    {
        var opacity = opacityOverride ?? (layer.Opacity * opacityScale);
        if (opacity <= 0) return;
        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;
        var content = layer.DocumentContentBounds;
        if (content.IsEmpty) return;
        var docLeft = Math.Max(Math.Max(clip.X, content.X), originX);
        var docTop = Math.Max(Math.Max(clip.Y, content.Y), originY);
        var docRight = Math.Min(Math.Min(clip.Right, content.Right), originX + width);
        var docBottom = Math.Min(Math.Min(clip.Bottom, content.Bottom), originY + height);
        if (docLeft >= docRight || docTop >= docBottom) return;
        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        var pixels = layer.Pixels;
        if (!pixels.HasContentTiles(sourceRegion)) return;

        pixels.EnterPixelReadLock();
        try
        {

        var blendMode = blendModeOverride ?? layer.BlendMode;
        var layerColor = layer.LayerColor;
        var hasLayerColor = layerColor.HasValue;
        var expressionColor = layer.ExpressionColor;
        var hasMask = layer.HasMask && layer.IsMaskVisible;
        var applyExpr = expressionColor != ExpressionColorMode.Color;
        byte lcR = 255, lcG = 255, lcB = 255;
        if (layerColor is { } lc) { lcR = lc.R; lcG = lc.G; lcB = lc.B; }
        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(sourceRegion.X, ts);
        var firstTileY = FloorDiv(sourceRegion.Y, ts);
        var lastTileX = FloorDiv(sourceRegion.Right - 1, ts);
        var lastTileY = FloorDiv(sourceRegion.Bottom - 1, ts);

        // Integer fast path for Normal blend
        if (blendMode == BlendMode.Normal)
        {
            var opacityByte = (uint)Math.Round(opacity * 255);
            var fullOpacity = opacityByte == 255;
            for (var ty = firstTileY; ty <= lastTileY; ty++)
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var tile = pixels.GetTileOrNull(tx, ty);
                    if (tile == null) continue;
                    byte[]? maskTile = null;
                    if (hasMask)
                        maskTile = layer.MaskPixels!.GetTileOrNull(tx, ty);
                    var clipLeft = Math.Max(sourceRegion.X, tx * ts);
                    var clipTop = Math.Max(sourceRegion.Y, ty * ts);
                    var clipRight = Math.Min(sourceRegion.Right, tx * ts + ts);
                    var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);
                    for (int srcY = clipTop; srcY < clipBottom; srcY++)
                    {
                        var tileLocalY = srcY - ty * ts;
                        var tileLocalX0 = clipLeft - tx * ts;
                        var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                        var docY = srcY + offsetY;
                        var dstRow = dst + (docY - originY) * dstStride;
                        var rowWidth = clipRight - clipLeft;

                        if (!hasLayerColor && !applyExpr && !hasMask)
                        {
                            var docX0 = clipLeft + offsetX;
                            var dstPtr = dstRow + (docX0 - originX) * 4;
                            fixed (byte* tileFix = tile)
                                CompositeNormalRowManaged.Composite(dstPtr, tileFix + tileRowBase, rowWidth, opacityByte);
                            continue;
                        }

                        for (var j = 0; j < rowWidth; j++)
                        {
                            var tileOffset = tileRowBase + j * 4;
                            uint rawA = tile[tileOffset + 3];
                            if (rawA == 0) continue;
                            var docX = clipLeft + j + offsetX;
                            if (hasMask)
                            {
                                if (maskTile == null) continue;
                                uint ma = maskTile[tileOffset + 3];
                                if (ma == 0) continue;
                                rawA = (rawA * ma + 127) / 255;
                                if (rawA == 0) continue;
                            }
                            var dstPtr = dstRow + (docX - originX) * 4;
                            byte srcB = tile[tileOffset + 0], srcG = tile[tileOffset + 1], srcR = tile[tileOffset + 2];
                            if (hasLayerColor)
                                ApplyLayerColor(ref srcB, ref srcG, ref srcR, lcB, lcG, lcR);
                            if (applyExpr && !ApplyExpressionColorToSource(ref srcB, ref srcG, ref srcR, ref rawA, expressionColor))
                                continue;
                            uint srcA = fullOpacity ? rawA : (rawA * opacityByte + 127) / 255;
                            if (srcA == 255) { dstPtr[0] = srcB; dstPtr[1] = srcG; dstPtr[2] = srcR; dstPtr[3] = 255; continue; }
                            uint invSrcA = 255 - srcA, ddA = dstPtr[3];
                            uint dstCont = (ddA * invSrcA + 127) / 255;
                            uint outA = srcA + dstCont;
                            if (outA == 0) continue;
                            uint halfOutA = outA >> 1;
                            dstPtr[0] = (byte)((srcB * srcA + dstPtr[0] * dstCont + halfOutA) / outA);
                            dstPtr[1] = (byte)((srcG * srcA + dstPtr[1] * dstCont + halfOutA) / outA);
                            dstPtr[2] = (byte)((srcR * srcA + dstPtr[2] * dstCont + halfOutA) / outA);
                            dstPtr[3] = (byte)outA;
                        }
                    }
                }
            return;
        }

        // LUT path for standard per-channel blend modes
        if (HasLut(blendMode))
        {
            var lut = GetLut(blendMode);
            var opacityByte = (uint)Math.Round(opacity * 255);
            for (var ty = firstTileY; ty <= lastTileY; ty++)
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var tile = pixels.GetTileOrNull(tx, ty);
                    if (tile == null) continue;
                    byte[]? maskTile = null;
                    if (hasMask)
                        maskTile = layer.MaskPixels!.GetTileOrNull(tx, ty);
                    var clipLeft = Math.Max(sourceRegion.X, tx * ts);
                    var clipTop = Math.Max(sourceRegion.Y, ty * ts);
                    var clipRight = Math.Min(sourceRegion.Right, tx * ts + ts);
                    var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);
                    for (int srcY = clipTop; srcY < clipBottom; srcY++)
                    {
                        var tileLocalY = srcY - ty * ts;
                        var tileLocalX0 = clipLeft - tx * ts;
                        var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                        var docY = srcY + offsetY;
                        var dstRow = dst + (docY - originY) * dstStride;
                        for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                        {
                            var tileOffset = tileRowBase + j * 4;
                            uint rawA = tile[tileOffset + 3];
                            if (rawA == 0) continue;
                            var docX = srcX + offsetX;
                            if (hasMask)
                            {
                                if (maskTile == null) continue;
                                uint ma = maskTile[tileOffset + 3];
                                if (ma == 0) continue;
                                rawA = (rawA * ma + 127) / 255;
                                if (rawA == 0) continue;
                            }
                            var dstPtr = dstRow + (docX - originX) * 4;
                            byte sb = tile[tileOffset + 0], sg = tile[tileOffset + 1], sr = tile[tileOffset + 2];
                            // layer color & expression
                            if (hasLayerColor)
                                ApplyLayerColor(ref sb, ref sg, ref sr, lcB, lcG, lcR);
                            if (applyExpr && !ApplyExpressionColorToSource(ref sb, ref sg, ref sr, ref rawA, expressionColor))
                                continue;
                            uint srcA = (rawA * opacityByte + 127) / 255;
                            if (srcA == 0) continue;
                            uint db = dstPtr[0], dg = dstPtr[1], dr = dstPtr[2], da = dstPtr[3];
                            BlendPixelInt(dstPtr,
                                lut[((uint)sr << 8) | dr], lut[((uint)sg << 8) | dg], lut[((uint)sb << 8) | db], srcA,
                                dr, dg, db, da);
                        }
                    }
                }
            return;
        }

        // Double fallback for HSL / luminance-based modes
        for (var ty = firstTileY; ty <= lastTileY; ty++)
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                    var tile = pixels.GetTileOrNull(tx, ty);
                    if (tile == null) continue;
                    byte[]? maskTile = null;
                    if (hasMask)
                        maskTile = layer.MaskPixels!.GetTileOrNull(tx, ty);
                    var clipLeft = Math.Max(sourceRegion.X, tx * ts);
                    var clipTop = Math.Max(sourceRegion.Y, ty * ts);
                    var clipRight = Math.Min(sourceRegion.Right, tx * ts + ts);
                    var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);
                    for (int srcY = clipTop; srcY < clipBottom; srcY++)
                    {
                        var tileLocalY = srcY - ty * ts;
                        var tileLocalX0 = clipLeft - tx * ts;
                        var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                        var docY = srcY + offsetY;
                        var dstRow = dst + (docY - originY) * dstStride;
                    for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        uint rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;
                        var docX = srcX + offsetX;
                        if (hasMask)
                        {
                            if (maskTile == null) continue;
                            uint ma = maskTile[tileOffset + 3];
                            if (ma == 0) continue;
                            rawA = (rawA * ma + 127) / 255;
                            if (rawA == 0) continue;
                        }
                        var dstIdx = (docX - originX) * 4;
                        byte tintedB, tintedG, tintedR;
                        if (hasLayerColor) { tintedB = tile[tileOffset + 0]; tintedG = tile[tileOffset + 1]; tintedR = tile[tileOffset + 2]; ApplyLayerColor(ref tintedB, ref tintedG, ref tintedR, lcB, lcG, lcR); }
                        else { tintedB = tile[tileOffset + 0]; tintedG = tile[tileOffset + 1]; tintedR = tile[tileOffset + 2]; }
                        if (applyExpr && !ApplyExpressionColorToSource(ref tintedB, ref tintedG, ref tintedR, ref rawA, expressionColor))
                            continue;
                        var srcA = rawA / 255.0 * opacity;
                        var sB = tintedB / 255.0; var sG = tintedG / 255.0; var sR = tintedR / 255.0;
                        var dB = dstRow[dstIdx + 0] / 255.0; var dG = dstRow[dstIdx + 1] / 255.0; var dR = dstRow[dstIdx + 2] / 255.0; var dA = dstRow[dstIdx + 3] / 255.0;
                        var (blendR, blendG, blendB) = ApplyBlendMode(sR, sG, sB, srcA, dR, dG, dB, dA, blendMode);
                        BlendPixel(dstRow + dstIdx, sR, sG, sB, srcA, dR, dG, dB, dA, blendR, blendG, blendB);
                    }
                }
            }

        }
        finally { pixels.ExitPixelReadLock(); }
    }

    /// <summary>
    /// : alpha-preserving blend for any blend mode.
    /// Applies the layer's actual blend mode to colors, gates by src alpha,
    /// and preserves dst alpha. Clipping layers contribute color only.
    /// </summary>
    internal static unsafe void CompositeLayerAlphaPreserving(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, double opacityScale, PixelRegion clip, int originX, int originY)
    {
        var opacity = layer.Opacity * opacityScale;
        if (opacity <= 0) return;
        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;
        var content = layer.DocumentContentBounds;
        if (content.IsEmpty) return;
        var docLeft = Math.Max(Math.Max(clip.X, content.X), originX);
        var docTop = Math.Max(Math.Max(clip.Y, content.Y), originY);
        var docRight = Math.Min(Math.Min(clip.Right, content.Right), originX + width);
        var docBottom = Math.Min(Math.Min(clip.Bottom, content.Bottom), originY + height);
        if (docLeft >= docRight || docTop >= docBottom) return;
        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        var pixels = layer.Pixels;
        var hasMask = layer.HasMask && layer.IsMaskVisible;
        var maskPixels = layer.MaskPixels;
        if (!pixels.HasContentTiles(sourceRegion)) return;

        var blendMode = layer.BlendMode;
        var isNormal = blendMode == BlendMode.Normal;
        var hasLut = HasLut(blendMode);
        var lut = hasLut ? GetLut(blendMode) : null;

        var layerColor = layer.LayerColor;
        var hasLayerColor = layerColor.HasValue;
        var expressionColor = layer.ExpressionColor;
        var applyExpr = expressionColor != ExpressionColorMode.Color;
        byte lcR = 255, lcG = 255, lcB = 255;
        if (layerColor is { } lc) { lcR = lc.R; lcG = lc.G; lcB = lc.B; }

        pixels.EnterPixelReadLock();
        if (hasMask) maskPixels!.EnterPixelReadLock();
        try
        {
            var opacityByte = (uint)Math.Round(opacity * 255);
            const int ts = TiledPixelBuffer.TileSize;
            var firstTileX = FloorDiv(sourceRegion.X, ts);
            var firstTileY = FloorDiv(sourceRegion.Y, ts);
            var lastTileX = FloorDiv(sourceRegion.Right - 1, ts);
            var lastTileY = FloorDiv(sourceRegion.Bottom - 1, ts);

            for (var ty = firstTileY; ty <= lastTileY; ty++)
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var tile = pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                byte[]? maskTile = null;
                if (hasMask)
                    maskTile = maskPixels!.GetTileOrNull(tx, ty);

                var tileLeft = Math.Max(sourceRegion.X, tx * ts);
                var tileTop = Math.Max(sourceRegion.Y, ty * ts);
                var tileRight = Math.Min(sourceRegion.Right, tx * ts + ts);
                var tileBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);

                for (var srcY = tileTop; srcY < tileBottom; srcY++)
                {
                    var tileLocalY = srcY - ty * ts;
                    var docY = srcY + offsetY;
                    var dstRow = dst + (docY - originY) * dstStride;

                    for (var srcX = tileLeft; srcX < tileRight; srcX++)
                    {
                        var tileLocalX = srcX - tx * ts;
                        var tileOff = (tileLocalY * ts + tileLocalX) * 4;
                        uint rawA = tile[tileOff + 3];
                        if (rawA == 0) continue;
                        if (hasMask)
                        {
                            if (maskTile == null) continue;
                            uint ma = maskTile[tileOff + 3];
                            if (ma == 0) continue;
                            rawA = (rawA * ma + 127) / 255;
                            if (rawA == 0) continue;
                        }
                        var docX = srcX + offsetX;
                        var dstOff = (docX - originX) * 4;
                        var dstPtr = dstRow + dstOff;

                        // Apply layer color and expression color before blending
                        byte srcB = tile[tileOff], srcG = tile[tileOff + 1], srcR = tile[tileOff + 2];
                        if (hasLayerColor)
                            ApplyLayerColor(ref srcB, ref srcG, ref srcR, lcB, lcG, lcR);
                        if (applyExpr && !ApplyExpressionColorToSource(ref srcB, ref srcG, ref srcR, ref rawA, expressionColor))
                            continue;
                        uint srcA = (rawA * opacityByte + 127) / 255;
                        if (srcA == 0) continue;

                        // Alpha-preserving blend: apply blend mode, gate by src alpha, keep dst alpha
                        if (isNormal)
                        {
                            BlendColorOnly(dstPtr, srcB, srcG, srcR, srcA);
                        }
                        else if (hasLut)
                        {
                            uint db = dstPtr[0], dg = dstPtr[1], dr = dstPtr[2];
                            uint blendedR = lut![((uint)srcR << 8) | dr];
                            uint blendedG = lut![((uint)srcG << 8) | dg];
                            uint blendedB = lut![((uint)srcB << 8) | db];
                            BlendColorOnly(dstPtr, (byte)blendedB, (byte)blendedG, (byte)blendedR, srcA);
                        }
                        else
                        {
                            // Double path: compute blend via double math
                            double sB = srcB / 255.0;
                            double sG = srcG / 255.0;
                            double sR = srcR / 255.0;
                            double sA = srcA / 255.0;
                            double dB = dstPtr[0] / 255.0;
                            double dG = dstPtr[1] / 255.0;
                            double dR = dstPtr[2] / 255.0;
                            double dA = dstPtr[3] / 255.0;
                            var (blendR, blendG, blendB) = ApplyBlendMode(sR, sG, sB, sA, dR, dG, dB, dA, blendMode);
                            // Gate by sA: mix blend result with dst color
                            double outR = blendR * sA + dR * (1.0 - sA);
                            double outG = blendG * sA + dG * (1.0 - sA);
                            double outB = blendB * sA + dB * (1.0 - sA);
                            dstPtr[0] = (byte)Math.Clamp((int)(outB * 255.0 + 0.5), 0, 255);
                            dstPtr[1] = (byte)Math.Clamp((int)(outG * 255.0 + 0.5), 0, 255);
                            dstPtr[2] = (byte)Math.Clamp((int)(outR * 255.0 + 0.5), 0, 255);
                            // dst[3] preserved
                        }
                    }
                }
            }
        }
        finally
        {
            if (hasMask) maskPixels!.ExitPixelReadLock();
            pixels.ExitPixelReadLock();
        }
    }

    /// <summary>Blend src_color * sa into dst_color, preserve dst_alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void BlendColorOnly(byte* dst, byte sb, byte sg, byte sr, uint sa)
    {
        if (sa >= 255) { dst[0] = sb; dst[1] = sg; dst[2] = sr; return; }
        uint inv = 255 - sa;
        dst[0] = (byte)((sb * sa + dst[0] * inv + 127) / 255);
        dst[1] = (byte)((sg * sa + dst[1] * inv + 127) / 255);
        dst[2] = (byte)((sr * sa + dst[2] * inv + 127) / 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyLayerColor(ref byte b, ref byte g, ref byte r, byte layerB, byte layerG, byte layerR)
    {
        var lum = (r * 299 + g * 587 + b * 114) / 1000;
        var ink = 255 - lum;
        b = (byte)(lum + (layerB * ink) / 255);
        g = (byte)(lum + (layerG * ink) / 255);
        r = (byte)(lum + (layerR * ink) / 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ApplyExpressionColorToSource(ref byte b, ref byte g, ref byte r, ref uint a, ExpressionColorMode mode)
    {
        if (mode == ExpressionColorMode.Color) return a != 0;

        var lum = (r * 299 + g * 587 + b * 114) / 1000;
        if (mode == ExpressionColorMode.Gray)
        {
            b = g = r = (byte)lum;
            return a != 0;
        }

        if (a < MonochromeThreshold)
        {
            a = 0;
            return false;
        }

        a = 255;
        b = g = r = lum >= MonochromeThreshold ? (byte)255 : (byte)0;
        return true;
    }

    internal static unsafe void ApplyExpressionColor(
        byte* dst, int dstStride,
        int left, int top, int right, int bottom,
        int originX, int originY,
        ExpressionColorMode mode)
    {
        if (mode == ExpressionColorMode.Color) return;

        for (int y = top; y < bottom; y++)
        {
            var row = dst + (y - originY) * dstStride;
            for (int x = left; x < right; x++)
            {
                var p = row + (x - originX) * 4;
                var b = p[0];
                var g = p[1];
                var r = p[2];
                var a = p[3];
                if (a == 0) continue;

                byte gray;
                if (mode == ExpressionColorMode.Gray)
                {
                    // Standard luminance
                    gray = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                }
                else // Monochrome
                {
                    var lum = (r * 299 + g * 587 + b * 114) / 1000;
                    if (a < MonochromeThreshold)
                    {
                        p[3] = 0;
                        continue;
                    }
                    p[3] = 255;
                    gray = lum >= MonochromeThreshold ? (byte)255 : (byte)0;
                }

                p[0] = gray;
                p[1] = gray;
                p[2] = gray;
            }
        }
    }

    internal static unsafe void BlendPixel(
        byte* dst,
        double srcR,
        double srcG,
        double srcB,
        double srcA,
        double dstR,
        double dstG,
        double dstB,
        double dstA,
        double blendR,
        double blendG,
        double blendB)
    {
        if (srcA <= 0) return;

        if (dstA <= 0)
        {
            dst[0] = (byte)Math.Clamp(srcB * 255, 0, 255);
            dst[1] = (byte)Math.Clamp(srcG * 255, 0, 255);
            dst[2] = (byte)Math.Clamp(srcR * 255, 0, 255);
            dst[3] = (byte)Math.Clamp(srcA * 255, 0, 255);
            return;
        }

        var outAlpha = srcA + dstA * (1.0 - srcA);
        if (outAlpha <= 0) return;

        dst[0] = (byte)Math.Clamp((blendB * srcA + dstB * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
        dst[1] = (byte)Math.Clamp((blendG * srcA + dstG * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
        dst[2] = (byte)Math.Clamp((blendR * srcA + dstR * dstA * (1.0 - srcA)) / outAlpha * 255, 0, 255);
        dst[3] = (byte)Math.Clamp(outAlpha * 255, 0, 255);
    }

    internal static (double r, double g, double b) ApplyBlendMode(
        double srcR, double srcG, double srcB, double srcA,
        double dstR, double dstG, double dstB, double dstA,
        BlendMode blendMode)
    {
        return blendMode switch
        {
            BlendMode.Normal or BlendMode.PassThrough => (srcR, srcG, srcB),
            BlendMode.Dissolve => (srcR, srcG, srcB),
            BlendMode.Multiply => (dstR * srcR, dstG * srcG, dstB * srcB),
            BlendMode.Screen => (1.0 - (1.0 - dstR) * (1.0 - srcR),
                                  1.0 - (1.0 - dstG) * (1.0 - srcG),
                                  1.0 - (1.0 - dstB) * (1.0 - srcB)),
            BlendMode.Overlay => (Overlay(dstR, srcR), Overlay(dstG, srcG), Overlay(dstB, srcB)),
            BlendMode.SoftLight => (SoftLight(dstR, srcR), SoftLight(dstG, srcG), SoftLight(dstB, srcB)),
            BlendMode.HardLight => (HardLight(dstR, srcR), HardLight(dstG, srcG), HardLight(dstB, srcB)),
            BlendMode.ColorDodge => (ColorDodge(dstR, srcR), ColorDodge(dstG, srcG), ColorDodge(dstB, srcB)),
            BlendMode.EasyDodge => (EasyDodge(dstR, srcR), EasyDodge(dstG, srcG), EasyDodge(dstB, srcB)),
            BlendMode.ColorBurn => (ColorBurn(dstR, srcR), ColorBurn(dstG, srcG), ColorBurn(dstB, srcB)),
            BlendMode.Darken => (Math.Min(dstR, srcR), Math.Min(dstG, srcG), Math.Min(dstB, srcB)),
            BlendMode.Lighten => (Math.Max(dstR, srcR), Math.Max(dstG, srcG), Math.Max(dstB, srcB)),
            BlendMode.Difference => (Math.Abs(dstR - srcR), Math.Abs(dstG - srcG), Math.Abs(dstB - srcB)),
            BlendMode.Exclusion => (dstR + srcR - 2.0 * dstR * srcR,
                                    dstG + srcG - 2.0 * dstG * srcG,
                                    dstB + srcB - 2.0 * dstB * srcB),
            BlendMode.LinearBurn => (dstR + srcR - 1.0, dstG + srcG - 1.0, dstB + srcB - 1.0),
            BlendMode.LinearDodge => (dstR + srcR, dstG + srcG, dstB + srcB),
            BlendMode.VividLight => (VividLight(dstR, srcR), VividLight(dstG, srcG), VividLight(dstB, srcB)),
            BlendMode.LinearLight => (LinearLight(dstR, srcR), LinearLight(dstG, srcG), LinearLight(dstB, srcB)),
            BlendMode.PinLight => (PinLight(dstR, srcR), PinLight(dstG, srcG), PinLight(dstB, srcB)),
            BlendMode.HardMix => (HardMix(dstR, srcR), HardMix(dstG, srcG), HardMix(dstB, srcB)),
            BlendMode.DarkerColor => LuminosityBlend(dstR, dstG, dstB, srcR, srcG, srcB, useDarker: true),
            BlendMode.LighterColor => LuminosityBlend(dstR, dstG, dstB, srcR, srcG, srcB, useDarker: false),
            BlendMode.Subtract => (dstR - srcR, dstG - srcG, dstB - srcB),
            BlendMode.Divide => (SafeDivide(dstR, srcR), SafeDivide(dstG, srcG), SafeDivide(dstB, srcB)),
            BlendMode.Hue => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 0),
            BlendMode.Saturation => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 1),
            BlendMode.Color => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 2),
            BlendMode.Luminosity => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 3),
            _ => (srcR, srcG, srcB)
        };
    }

    internal static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0) result--;
        return result;
    }

    internal static double Overlay(double dst, double src)
    {
        if (dst < 0.5)
            return 2.0 * dst * src;
        else
            return 1.0 - 2.0 * (1.0 - dst) * (1.0 - src);
    }

    internal static double SoftLight(double dst, double src)
    {
        if (src < 0.5)
            return dst - (1.0 - 2.0 * src) * dst * (1.0 - dst);
        else
        {
            double d = dst < 0.25 ? ((16.0 * dst - 12.0) * dst + 4.0) * dst : Math.Sqrt(dst);
            return dst + (2.0 * src - 1.0) * (d - dst);
        }
    }

    internal static double HardLight(double dst, double src)
    {
        if (src < 0.5)
            return 2.0 * dst * src;
        else
            return 1.0 - 2.0 * (1.0 - dst) * (1.0 - src);
    }

    internal static double ColorDodge(double dst, double src)
    {
        if (dst == 0.0) return 0.0;
        if (src == 1.0) return 1.0;
        return Math.Min(1.0, dst / (1.0 - src));
    }

    /// <summary>
    /// cfEasyDodge: "Glow Dodge" — stronger than Color Dodge.
    /// Uses pow(dst, 1.04/(1-src)) instead of dst/(1-src).
    /// The 1.04 factor can be adjusted to taste.
    /// </summary>
    internal static double EasyDodge(double dst, double src)
    {
        if (dst == 0.0) return 0.0;
        if (src >= 1.0) return 1.0;
        return Math.Pow(dst, 1.04 / (1.0 - src));
    }

    internal static double ColorBurn(double dst, double src)
    {
        if (dst == 1.0) return 1.0;
        if (src == 0.0) return 0.0;
        return 1.0 - Math.Min(1.0, (1.0 - dst) / src);
    }

    internal static double LinearLight(double dst, double src)
    {
        if (src < 0.5)
            return dst + 2.0 * src - 1.0;
        else
            return dst + 2.0 * (src - 0.5);
    }

    internal static double VividLight(double dst, double src)
    {
        if (src < 0.5)
            return ColorBurn(dst, 2.0 * src);
        else
            return ColorDodge(dst, 2.0 * (src - 0.5));
    }

    internal static double PinLight(double dst, double src)
    {
        if (src < 0.5)
            return Math.Min(dst, 2.0 * src);
        else
            return Math.Max(dst, 2.0 * (src - 0.5));
    }

    internal static double HardMix(double dst, double src)
    {
        return VividLight(dst, src) < 0.5 ? 0.0 : 1.0;
    }

    internal static double SafeDivide(double dst, double src)
    {
        if (src == 0.0) return 0.0;
        return Math.Min(1.0, dst / src);
    }

    internal static (double r, double g, double b) LuminosityBlend(
        double dstR, double dstG, double dstB,
        double srcR, double srcG, double srcB,
        bool useDarker)
    {
        var dstLum = RgbToLuma(dstR, dstG, dstB);
        var srcLum = RgbToLuma(srcR, srcG, srcB);
        var cmp = useDarker ? srcLum < dstLum : srcLum > dstLum;
        return cmp ? (srcR, srcG, srcB) : (dstR, dstG, dstB);
    }

    internal static double RgbToLuma(double r, double g, double b)
    {
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    internal static (double r, double g, double b) HslBlend(
        double dstR, double dstG, double dstB,
        double srcR, double srcG, double srcB,
        int mode)
    {
        switch (mode)
        {
            case 0: // Hue: src hue, dst saturation+luminosity
                { var (r, g, b) = SvgSetSat(srcR, srcG, srcB, SvgSat(dstR, dstG, dstB)); return SvgSetLum(r, g, b, SvgLum(dstR, dstG, dstB)); }
            case 1: // Saturation: src saturation, dst hue+luminosity
                { var (r, g, b) = SvgSetSat(dstR, dstG, dstB, SvgSat(srcR, srcG, srcB)); return SvgSetLum(r, g, b, SvgLum(dstR, dstG, dstB)); }
            case 2: // Color: src hue+saturation, dst luminosity
                return SvgSetLum(srcR, srcG, srcB, SvgLum(dstR, dstG, dstB));
            default: // Luminosity: src luminosity, dst hue+saturation
                return SvgSetLum(dstR, dstG, dstB, SvgLum(srcR, srcG, srcB));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SvgLum(double r, double g, double b)
        => 0.3 * r + 0.59 * g + 0.11 * b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SvgSat(double r, double g, double b)
        => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static (double r, double g, double b) SvgSetSat(double r, double g, double b, double sat)
    {
        double min, mid, max;
        int minCh, midCh, maxCh;
        if (r <= g)
        {
            if (r <= b) { min = r; minCh = 0; if (g <= b) { mid = g; midCh = 1; max = b; maxCh = 2; } else { mid = b; midCh = 2; max = g; maxCh = 1; } }
            else { min = b; minCh = 2; mid = r; midCh = 0; max = g; maxCh = 1; }
        }
        else
        {
            if (g <= b) { min = g; minCh = 1; if (r <= b) { mid = r; midCh = 0; max = b; maxCh = 2; } else { mid = b; midCh = 2; max = r; maxCh = 0; } }
            else { min = b; minCh = 2; mid = g; midCh = 1; max = r; maxCh = 0; }
        }

        double nMid, nMax;
        if (max > min) { nMid = ((mid - min) * sat) / (max - min); nMax = sat; }
        else { nMid = 0; nMax = 0; }

        double nr = r, ng = g, nb = b;
        void Set(int ch, double v) { if (ch == 0) nr = v; else if (ch == 1) ng = v; else nb = v; }
        Set(minCh, 0); Set(midCh, nMid); Set(maxCh, nMax);
        return (nr, ng, nb);
    }

    private static (double r, double g, double b) SvgClipColor(double r, double g, double b)
    {
        double lum = SvgLum(r, g, b);
        double n = Math.Min(r, Math.Min(g, b));
        double x = Math.Max(r, Math.Max(g, b));
        if (n < 0) { r = lum + ((r - lum) * lum) / (lum - n); g = lum + ((g - lum) * lum) / (lum - n); b = lum + ((b - lum) * lum) / (lum - n); }
        if (x > 1) { r = lum + ((r - lum) * (1 - lum)) / (x - lum); g = lum + ((g - lum) * (1 - lum)) / (x - lum); b = lum + ((b - lum) * (1 - lum)) / (x - lum); }
        return (r, g, b);
    }

    private static (double r, double g, double b) SvgSetLum(double r, double g, double b, double lum)
    {
        double d = lum - SvgLum(r, g, b);
        return SvgClipColor(r + d, g + d, b + d);
    }
}
