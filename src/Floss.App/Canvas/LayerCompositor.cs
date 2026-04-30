using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Document;

namespace Floss.App.Canvas;

public sealed class LayerCompositor
{
    private WriteableBitmap? _composited;
    private int _width;
    private int _height;
    private bool _dirty = true;
    private bool _fullDirty = true;
    private PixelRegion? _dirtyRegion;
    private readonly Dictionary<DrawingLayer, GroupProjectionCache> _groupCaches = new();

    public void SetSize(int width, int height)
    {
        if (_width == width && _height == height && _composited != null) return;
        _width = width;
        _height = height;
        _composited?.Dispose();
        _composited = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        _dirty = true;
        _fullDirty = true;
        _dirtyRegion = null;
        _groupCaches.Clear();
    }

    public WriteableBitmap Bitmap
    {
        get
        {
            if (_composited == null)
                SetSize(2048, 2048);
            return _composited!;
        }
    }

    public void Invalidate(PixelRegion? region = null)
        => Invalidate(region, null, null);

    public void Invalidate(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex)
    {
        _dirty = true;
        if (region is null || region.Value.IsEmpty)
        {
            _fullDirty = true;
            _dirtyRegion = null;
        }
        else if (_fullDirty)
        {
            return;
        }
        else
        {
            _dirtyRegion = _dirtyRegion is { } existing ? existing.Union(region.Value) : region.Value;
        }

        InvalidateGroupCaches(region, layers, layerIndex);
    }

    public unsafe void Composite(IReadOnlyList<DrawingLayer> layers, int width, int height)
    {
        SetSize(width, height);
        if (!_dirty) return;
        _dirty = false;

        using var frame = _composited!.Lock();
        var dst = (byte*)frame.Address;
        var stride = frame.RowBytes;

        var clip = (_fullDirty ? new PixelRegion(0, 0, width, height) : _dirtyRegion ?? PixelRegion.Empty).ClipTo(width, height);
        if (clip.IsEmpty) return;
        _fullDirty = false;
        _dirtyRegion = null;

        FillWithPaperColor(dst, stride, width, height, clip);

        // Only process root-level layers; group children are composited inside CompositeGroup.
        // Filtering by Parent==null avoids double-rendering when the flat document list
        // contains both a group and its children (as happens after PSD import).
        var rootLayers = new System.Collections.Generic.List<DrawingLayer>(layers.Count);
        foreach (var l in layers)
            if (l.Parent == null) rootLayers.Add(l);

        CompositeLayerList(dst, stride, width, height, rootLayers, opacityScale: 1.0, clip, originX: 0, originY: 0);
    }

    private static unsafe void FillWithPaperColor(byte* dst, int stride, int width, int height, PixelRegion clip)
    {
        var paper = DrawingDocument.PaperColor;
        var pixel = (uint)(paper.B | (paper.G << 8) | (paper.R << 16) | (255u << 24));
        for (int y = clip.Y; y < clip.Bottom; y++)
        {
            var row = (uint*)(dst + y * stride + clip.X * 4);
            for (int x = 0; x < clip.Width; x++)
                row[x] = pixel;
        }
    }

    private record RenderItem(DrawingLayer Layer, bool IsClipped, int BaseLayerIndex);

    private void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex)
    {
        if (region is null || region.Value.IsEmpty || layers is null || layerIndex is null ||
            layerIndex.Value < 0 || layerIndex.Value >= layers.Count)
        {
            foreach (var cache in _groupCaches.Values)
                cache.Invalidate(region);
            return;
        }

        var layer = layers[layerIndex.Value];
        if (layer.IsGroup && _groupCaches.TryGetValue(layer, out var ownCache))
            ownCache.Invalidate(region.Value);

        for (var parent = layer.Parent; parent != null; parent = parent.Parent)
        {
            if (_groupCaches.TryGetValue(parent, out var cache))
                cache.Invalidate(region.Value);
        }
    }

    private unsafe void CompositeLayerList(
        byte* dst,
        int dstStride,
        int width,
        int height,
        IReadOnlyList<DrawingLayer> layers,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (opacityScale <= 0) return;

        var renderList = FlattenForRender(layers);
        for (int i = 0; i < renderList.Count; i++)
        {
            var item = renderList[i];
            if (!item.Layer.IsVisible) continue;

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = renderList[item.BaseLayerIndex].Layer;
                if (item.Layer.IsGroup)
                    CompositeClippedGroup(dst, dstStride, width, height, item.Layer, baseLayer, opacityScale, clip, originX, originY);
                else
                    CompositeClippedLayer(dst, dstStride, width, height, item.Layer, baseLayer, opacityScale, clip, originX, originY);
            }
            else if (item.Layer.IsGroup)
            {
                CompositeGroup(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            }
            else
            {
                CompositeLayer(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            }
        }
    }

    private static List<RenderItem> FlattenForRender(IReadOnlyList<DrawingLayer> layers)
    {
        var result = new List<RenderItem>();
        int? lastNonClippingIndex = null;

        // Build bottom-to-top render list with clipping info
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.IsClipping && lastNonClippingIndex.HasValue)
            {
                result.Add(new RenderItem(layer, true, lastNonClippingIndex.Value));
            }
            else
            {
                result.Add(new RenderItem(layer, false, -1));
                if (!layer.IsClipping)
                    lastNonClippingIndex = result.Count - 1;
            }
        }

        return result;
    }

    private unsafe void CompositeGroup(
        byte* dst,
        int dstStride,
        int width,
        int height,
        DrawingLayer group,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (group.Children.Count == 0) return;

        var groupOpacity = group.Opacity * opacityScale;
        if (groupOpacity <= 0) return;

        if (group.BlendMode == "PassThrough")
        {
            CompositeLayerList(dst, dstStride, width, height, group.Children, groupOpacity, clip, originX, originY);
            return;
        }

        var projection = GetGroupProjection(group, clip);
        CompositeProjectionBuffer(dst, dstStride, projection, group.BlendMode, groupOpacity, clip, originX, originY);
    }

    private unsafe TiledPixelBuffer GetGroupProjection(DrawingLayer group, PixelRegion requestedClip)
    {
        if (!_groupCaches.TryGetValue(group, out var cache))
        {
            cache = new GroupProjectionCache(_width, _height);
            _groupCaches.Add(group, cache);
        }
        else
        {
            cache.EnsureSize(_width, _height);
        }

        var dirty = cache.TakeDirty(requestedClip);
        if (!dirty.IsEmpty)
        {
            cache.Buffer.Clear(dirty);
            var temp = new byte[dirty.Width * dirty.Height * 4];
            fixed (byte* tempPtr = temp)
            {
                CompositeLayerList(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height, group.Children, opacityScale: 1.0, dirty, dirty.X, dirty.Y);
            }
            cache.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
        }

        return cache.Buffer;
    }

    private static unsafe void CompositeProjectionBuffer(
        byte* dst,
        int dstStride,
        TiledPixelBuffer projection,
        string blendMode,
        double opacity,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (opacity <= 0 || !projection.HasContentTiles(clip)) return;

        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(clip.X,          ts);
        var firstTileY = FloorDiv(clip.Y,          ts);
        var lastTileX  = FloorDiv(clip.Right  - 1, ts);
        var lastTileY  = FloorDiv(clip.Bottom - 1, ts);

        if (blendMode == "Normal")
        {
            var opacityByte = (uint)Math.Round(opacity * 255);
            var fullOpacity = opacityByte == 255;

            for (var ty = firstTileY; ty <= lastTileY; ty++)
            {
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var tile = projection.GetTileOrNull(tx, ty);
                    if (tile == null) continue;

                    var tileLeft   = Math.Max(clip.X,      tx * ts);
                    var tileTop    = Math.Max(clip.Y,      ty * ts);
                    var tileRight  = Math.Min(clip.Right,  tx * ts + ts);
                    var tileBottom = Math.Min(clip.Bottom, ty * ts + ts);

                    for (int docY = tileTop; docY < tileBottom; docY++)
                    {
                        var tileLocalY  = docY - ty * ts;
                        var tileLocalX0 = tileLeft - tx * ts;
                        var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                        var dstRow = dst + (docY - originY) * dstStride;

                        for (int j = 0, docX = tileLeft; docX < tileRight; docX++, j++)
                        {
                            var tileOffset = tileRowBase + j * 4;
                            uint rawA = tile[tileOffset + 3];
                            if (rawA == 0) continue;

                            uint srcA  = fullOpacity ? rawA : (rawA * opacityByte + 127) / 255;
                            var dstPtr = dstRow + (docX - originX) * 4;

                            if (srcA == 255)
                            {
                                dstPtr[0] = tile[tileOffset + 0];
                                dstPtr[1] = tile[tileOffset + 1];
                                dstPtr[2] = tile[tileOffset + 2];
                                dstPtr[3] = 255;
                                continue;
                            }

                            uint invSrcA = 255 - srcA;
                            uint dstA    = dstPtr[3];
                            uint dstCont = (dstA * invSrcA + 127) / 255;
                            uint outA    = srcA + dstCont;
                            if (outA == 0) continue;

                            uint half = outA >> 1;
                            dstPtr[0] = (byte)((tile[tileOffset + 0] * srcA + dstPtr[0] * dstCont + half) / outA);
                            dstPtr[1] = (byte)((tile[tileOffset + 1] * srcA + dstPtr[1] * dstCont + half) / outA);
                            dstPtr[2] = (byte)((tile[tileOffset + 2] * srcA + dstPtr[2] * dstCont + half) / outA);
                            dstPtr[3] = (byte)outA;
                        }
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

                var tileLeft   = Math.Max(clip.X,      tx * ts);
                var tileTop    = Math.Max(clip.Y,      ty * ts);
                var tileRight  = Math.Min(clip.Right,  tx * ts + ts);
                var tileBottom = Math.Min(clip.Bottom, ty * ts + ts);

                for (int docY = tileTop; docY < tileBottom; docY++)
                {
                    var tileLocalY  = docY - ty * ts;
                    var tileLocalX0 = tileLeft - tx * ts;
                    var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                    var dstRow = dst + (docY - originY) * dstStride;

                    for (int j = 0, docX = tileLeft; docX < tileRight; docX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        var rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;

                        var srcA   = rawA / 255.0 * opacity;
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

    private static unsafe void CompositeClippedLayer(
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

        var offsetX     = layer.OffsetX;
        var offsetY     = layer.OffsetY;
        var baseOffsetX = baseLayer.OffsetX;
        var baseOffsetY = baseLayer.OffsetY;
        var baseW       = baseLayer.Width;
        var baseH       = baseLayer.Height;

        var docLeft   = Math.Max(Math.Max(clip.X, offsetX), 0);
        var docTop    = Math.Max(Math.Max(clip.Y, offsetY), 0);
        var docRight  = Math.Min(Math.Min(clip.Right,  offsetX + layer.Width),  width  + originX);
        var docBottom = Math.Min(Math.Min(clip.Bottom, offsetY + layer.Height), height + originY);

        if (docLeft >= docRight || docTop >= docBottom) return;

        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        if (!layer.Pixels.HasContentTiles(sourceRegion)) return;

        var blendMode = layer.BlendMode;
        const int ts = TiledPixelBuffer.TileSize;

        var firstTileX = FloorDiv(sourceRegion.X,          ts);
        var firstTileY = FloorDiv(sourceRegion.Y,          ts);
        var lastTileX  = FloorDiv(sourceRegion.Right  - 1, ts);
        var lastTileY  = FloorDiv(sourceRegion.Bottom - 1, ts);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var tile = layer.Pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                var clipLeft   = Math.Max(sourceRegion.X,      tx * ts);
                var clipTop    = Math.Max(sourceRegion.Y,      ty * ts);
                var clipRight  = Math.Min(sourceRegion.Right,  tx * ts + ts);
                var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);

                var isNormal   = blendMode == "Normal";
                var opacityInt = (uint)Math.Round(opacity * 255);

                for (int srcY = clipTop; srcY < clipBottom; srcY++)
                {
                    var tileLocalY  = srcY - ty * ts;
                    var tileLocalX0 = clipLeft - tx * ts;
                    var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                    var docY   = srcY + offsetY;
                    var dstRow = dst + (docY - originY) * dstStride;
                    var baseY  = docY - baseOffsetY;

                    if (baseY < 0 || baseY >= baseH) continue;

                    var baseTileY      = FloorDiv(baseY, ts);
                    var baseTileLocalY = baseY - baseTileY * ts;

                    int      prevBaseTileX = int.MinValue;
                    byte[]?  baseTile      = null;

                    for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        var rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;

                        var docX  = srcX + offsetX;
                        var baseX = docX - baseOffsetX;
                        if (baseX < 0 || baseX >= baseW) continue;

                        var baseTileX = FloorDiv(baseX, ts);
                        if (baseTileX != prevBaseTileX)
                        {
                            baseTile      = baseLayer.Pixels.GetTileOrNull(baseTileX, baseTileY);
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
                            uint srcA = (rawA * opacityInt + 127) / 255;
                            srcA = (srcA * baseAlphaByte + 127) / 255;
                            if (srcA == 0) continue;

                            var dstPtr = dstRow + dstIdx;
                            if (srcA == 255)
                            {
                                dstPtr[0] = tile[tileOffset + 0];
                                dstPtr[1] = tile[tileOffset + 1];
                                dstPtr[2] = tile[tileOffset + 2];
                                dstPtr[3] = 255;
                                continue;
                            }

                            uint invSrcA = 255 - srcA;
                            uint dstA    = dstPtr[3];
                            uint dstCont = (dstA * invSrcA + 127) / 255;
                            uint outA    = srcA + dstCont;
                            if (outA == 0) continue;

                            uint half = outA >> 1;
                            dstPtr[0] = (byte)((tile[tileOffset + 0] * srcA + dstPtr[0] * dstCont + half) / outA);
                            dstPtr[1] = (byte)((tile[tileOffset + 1] * srcA + dstPtr[1] * dstCont + half) / outA);
                            dstPtr[2] = (byte)((tile[tileOffset + 2] * srcA + dstPtr[2] * dstCont + half) / outA);
                            dstPtr[3] = (byte)outA;
                        }
                        else
                        {
                            var srcA = rawA / 255.0 * opacity * (baseAlphaByte / 255.0);
                            if (srcA <= 0) continue;

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
    }

    private unsafe void CompositeClippedGroup(
        byte* dst,
        int dstStride,
        int width,
        int height,
        DrawingLayer group,
        DrawingLayer baseLayer,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        var groupOpacity = group.Opacity * opacityScale;
        if (groupOpacity <= 0 || group.Children.Count == 0) return;

        var temp = new byte[clip.Width * clip.Height * 4];
        fixed (byte* tempPtr = temp)
        {
            for (int i = 0; i < temp.Length; i++) tempPtr[i] = 0;

            if (group.BlendMode == "PassThrough")
                CompositeLayerList(tempPtr, clip.Width * 4, clip.Width, clip.Height, group.Children, groupOpacity, clip, clip.X, clip.Y);
            else
                CompositeGroup(tempPtr, clip.Width * 4, clip.Width, clip.Height, group, opacityScale, clip, clip.X, clip.Y);

            CompositeClippedBuffer(dst, dstStride, width, height, tempPtr, clip.Width * 4, baseLayer, group.BlendMode, clip, originX, originY);
        }
    }

    private static unsafe void CompositeClippedBuffer(
        byte* dst,
        int dstStride,
        int width,
        int height,
        byte* src,
        int srcStride,
        DrawingLayer baseLayer,
        string blendMode,
        PixelRegion clip,
        int originX,
        int originY)
    {
        var baseW = baseLayer.Width;
        var baseH = baseLayer.Height;
        var baseOffsetX = baseLayer.OffsetX;
        var baseOffsetY = baseLayer.OffsetY;

        const int ts = TiledPixelBuffer.TileSize;

        for (int docY = clip.Y; docY < clip.Bottom; docY++)
        {
            var baseY = docY - baseOffsetY;
            if (baseY < 0 || baseY >= baseH) continue;

            var srcRow = src + (docY - clip.Y) * srcStride;
            var dstRow = dst + (docY - originY) * dstStride;

            var baseTileY      = FloorDiv(baseY, ts);
            var baseTileLocalY = baseY - baseTileY * ts;
            int     prevBaseTileX = int.MinValue;
            byte[]? baseTile      = null;

            for (int docX = clip.X; docX < clip.Right; docX++)
            {
                var srcIdx = (docX - clip.X) * 4;
                var rawA   = srcRow[srcIdx + 3];
                if (rawA == 0) continue;

                var baseX = docX - baseOffsetX;
                if (baseX < 0 || baseX >= baseW) continue;

                var baseTileX = FloorDiv(baseX, ts);
                if (baseTileX != prevBaseTileX)
                {
                    baseTile      = baseLayer.Pixels.GetTileOrNull(baseTileX, baseTileY);
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

    private static unsafe void CompositeLayer(
        byte* dst,
        int dstStride,
        int width,
        int height,
        DrawingLayer layer,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        var opacity = layer.Opacity * opacityScale;
        if (opacity <= 0) return;

        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;

        var docLeft   = Math.Max(Math.Max(clip.X, offsetX), 0);
        var docTop    = Math.Max(Math.Max(clip.Y, offsetY), 0);
        var docRight  = Math.Min(Math.Min(clip.Right,  offsetX + layer.Width),  width  + originX);
        var docBottom = Math.Min(Math.Min(clip.Bottom, offsetY + layer.Height), height + originY);

        if (docLeft >= docRight || docTop >= docBottom) return;

        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        if (!layer.Pixels.HasContentTiles(sourceRegion)) return;

        var blendMode = layer.BlendMode;
        const int ts = TiledPixelBuffer.TileSize;

        var firstTileX = FloorDiv(sourceRegion.X,          ts);
        var firstTileY = FloorDiv(sourceRegion.Y,          ts);
        var lastTileX  = FloorDiv(sourceRegion.Right  - 1, ts);
        var lastTileY  = FloorDiv(sourceRegion.Bottom - 1, ts);

        // Integer fast path for Normal blend — avoids all float conversions.
        if (blendMode == "Normal")
        {
            var opacityByte = (uint)Math.Round(opacity * 255);
            var fullOpacity = opacityByte == 255;

            for (var ty = firstTileY; ty <= lastTileY; ty++)
            {
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var tile = layer.Pixels.GetTileOrNull(tx, ty);
                    if (tile == null) continue;

                    var clipLeft   = Math.Max(sourceRegion.X,      tx * ts);
                    var clipTop    = Math.Max(sourceRegion.Y,      ty * ts);
                    var clipRight  = Math.Min(sourceRegion.Right,  tx * ts + ts);
                    var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);

                    for (int srcY = clipTop; srcY < clipBottom; srcY++)
                    {
                        var tileLocalY  = srcY - ty * ts;
                        var tileLocalX0 = clipLeft - tx * ts;
                        var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                        var docY  = srcY + offsetY;
                        var dstRow = dst + (docY - originY) * dstStride;

                        for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                        {
                            var tileOffset = tileRowBase + j * 4;
                            uint rawA = tile[tileOffset + 3];
                            if (rawA == 0) continue;

                            uint srcA = fullOpacity ? rawA : (rawA * opacityByte + 127) / 255;
                            var docX   = srcX + offsetX;
                            var dstPtr = dstRow + (docX - originX) * 4;

                            if (srcA == 255)
                            {
                                dstPtr[0] = tile[tileOffset + 0];
                                dstPtr[1] = tile[tileOffset + 1];
                                dstPtr[2] = tile[tileOffset + 2];
                                dstPtr[3] = 255;
                                continue;
                            }

                            uint invSrcA  = 255 - srcA;
                            uint dstA     = dstPtr[3];
                            uint dstCont  = (dstA * invSrcA + 127) / 255;
                            uint outA     = srcA + dstCont;
                            if (outA == 0) continue;

                            uint halfOutA = outA >> 1;
                            dstPtr[0] = (byte)((tile[tileOffset + 0] * srcA + dstPtr[0] * dstCont + halfOutA) / outA);
                            dstPtr[1] = (byte)((tile[tileOffset + 1] * srcA + dstPtr[1] * dstCont + halfOutA) / outA);
                            dstPtr[2] = (byte)((tile[tileOffset + 2] * srcA + dstPtr[2] * dstCont + halfOutA) / outA);
                            dstPtr[3] = (byte)outA;
                        }
                    }
                }
            }
            return;
        }

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var tile = layer.Pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                var clipLeft   = Math.Max(sourceRegion.X,      tx * ts);
                var clipTop    = Math.Max(sourceRegion.Y,      ty * ts);
                var clipRight  = Math.Min(sourceRegion.Right,  tx * ts + ts);
                var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);

                for (int srcY = clipTop; srcY < clipBottom; srcY++)
                {
                    var tileLocalY  = srcY - ty * ts;
                    var tileLocalX0 = clipLeft - tx * ts;
                    var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                    var docY = srcY + offsetY;
                    var dstRow = dst + (docY - originY) * dstStride;

                    for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        var rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;

                        var srcA   = rawA / 255.0 * opacity;
                        var docX   = srcX + offsetX;
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

    private static unsafe void BlendPixel(
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
            dst[0] = (byte)Math.Clamp(srcB * srcA * 255, 0, 255);
            dst[1] = (byte)Math.Clamp(srcG * srcA * 255, 0, 255);
            dst[2] = (byte)Math.Clamp(srcR * srcA * 255, 0, 255);
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

    private static (double r, double g, double b) ApplyBlendMode(
        double srcR, double srcG, double srcB, double srcA,
        double dstR, double dstG, double dstB, double dstA,
        string blendMode)
    {
        return blendMode switch
        {
            "Normal" or "PassThrough" => (srcR, srcG, srcB),
            "Dissolve" => (srcR, srcG, srcB),
            "Multiply" => (dstR * srcR, dstG * srcG, dstB * srcB),
            "Screen" => (1.0 - (1.0 - dstR) * (1.0 - srcR),
                        1.0 - (1.0 - dstG) * (1.0 - srcG),
                        1.0 - (1.0 - dstB) * (1.0 - srcB)),
            "Overlay" => (Overlay(dstR, srcR), Overlay(dstG, srcG), Overlay(dstB, srcB)),
            "SoftLight" => (SoftLight(dstR, srcR), SoftLight(dstG, srcG), SoftLight(dstB, srcB)),
            "HardLight" => (HardLight(dstR, srcR), HardLight(dstG, srcG), HardLight(dstB, srcB)),
            "ColorDodge" => (ColorDodge(dstR, srcR), ColorDodge(dstG, srcG), ColorDodge(dstB, srcB)),
            "ColorBurn" => (ColorBurn(dstR, srcR), ColorBurn(dstG, srcG), ColorBurn(dstB, srcB)),
            "Darken" => (Math.Min(dstR, srcR), Math.Min(dstG, srcG), Math.Min(dstB, srcB)),
            "Lighten" => (Math.Max(dstR, srcR), Math.Max(dstG, srcG), Math.Max(dstB, srcB)),
            "Difference" => (Math.Abs(dstR - srcR), Math.Abs(dstG - srcG), Math.Abs(dstB - srcB)),
            "Exclusion" => (dstR + srcR - 2.0 * dstR * srcR,
                           dstG + srcG - 2.0 * dstG * srcG,
                           dstB + srcB - 2.0 * dstB * srcB),
            "LinearBurn" => (dstR + srcR - 1.0, dstG + srcG - 1.0, dstB + srcB - 1.0),
            "LinearDodge" => (dstR + srcR, dstG + srcG, dstB + srcB),
            "VividLight" => (VividLight(dstR, srcR), VividLight(dstG, srcG), VividLight(dstB, srcB)),
            "LinearLight" => (LinearLight(dstR, srcR), LinearLight(dstG, srcG), LinearLight(dstB, srcB)),
            "PinLight" => (PinLight(dstR, srcR), PinLight(dstG, srcG), PinLight(dstB, srcB)),
            "HardMix" => (HardMix(dstR, srcR), HardMix(dstG, srcG), HardMix(dstB, srcB)),
            "DarkerColor" => LuminosityBlend(dstR, dstG, dstB, srcR, srcG, srcB, useDarker: true),
            "LighterColor" => LuminosityBlend(dstR, dstG, dstB, srcR, srcG, srcB, useDarker: false),
            "Subtract" => (dstR - srcR, dstG - srcG, dstB - srcB),
            "Divide" => (SafeDivide(dstR, srcR), SafeDivide(dstG, srcG), SafeDivide(dstB, srcB)),
            "Hue" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 0),
            "Saturation" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 1),
            "Color" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 2),
            "Luminosity" => HslBlend(dstR, dstG, dstB, srcR, srcG, srcB, mode: 3),
            _ => (srcR, srcG, srcB)
        };
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0) result--;
        return result;
    }

    private static double Overlay(double dst, double src)
    {
        if (dst < 0.5)
            return 2.0 * dst * src;
        else
            return 1.0 - 2.0 * (1.0 - dst) * (1.0 - src);
    }

    private static double SoftLight(double dst, double src)
    {
        if (src < 0.5)
            return dst - (1.0 - 2.0 * src) * dst * (1.0 - dst);
        else
        {
            double d = dst < 0.25 ? ((16.0 * dst - 12.0) * dst + 4.0) * dst : Math.Sqrt(dst);
            return dst + (2.0 * src - 1.0) * (d - dst);
        }
    }

    private static double HardLight(double dst, double src)
    {
        if (src < 0.5)
            return 2.0 * dst * src;
        else
            return 1.0 - 2.0 * (1.0 - dst) * (1.0 - src);
    }

    private static double ColorDodge(double dst, double src)
    {
        if (dst == 0.0) return 0.0;
        if (src == 1.0) return 1.0;
        return Math.Min(1.0, dst / (1.0 - src));
    }

    private static double ColorBurn(double dst, double src)
    {
        if (dst == 1.0) return 1.0;
        if (src == 0.0) return 0.0;
        return 1.0 - Math.Min(1.0, (1.0 - dst) / src);
    }

    private static double LinearLight(double dst, double src)
    {
        if (src < 0.5)
            return dst + 2.0 * src - 1.0;
        else
            return dst + 2.0 * (src - 0.5);
    }

    private static double VividLight(double dst, double src)
    {
        if (src < 0.5)
            return ColorBurn(dst, 2.0 * src);
        else
            return ColorDodge(dst, 2.0 * (src - 0.5));
    }

    private static double PinLight(double dst, double src)
    {
        if (src < 0.5)
            return Math.Min(dst, 2.0 * src);
        else
            return Math.Max(dst, 2.0 * (src - 0.5));
    }

    private static double HardMix(double dst, double src)
    {
        return VividLight(dst, src) < 0.5 ? 0.0 : 1.0;
    }

    private static double SafeDivide(double dst, double src)
    {
        if (src == 0.0) return 0.0;
        return Math.Min(1.0, dst / src);
    }

    private static (double r, double g, double b) LuminosityBlend(
        double dstR, double dstG, double dstB,
        double srcR, double srcG, double srcB,
        bool useDarker)
    {
        var dstLum = RgbToLuma(dstR, dstG, dstB);
        var srcLum = RgbToLuma(srcR, srcG, srcB);
        var cmp = useDarker ? srcLum < dstLum : srcLum > dstLum;
        return cmp ? (srcR, srcG, srcB) : (dstR, dstG, dstB);
    }

    private static double RgbToLuma(double r, double g, double b)
    {
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static (double r, double g, double b) HslBlend(
        double dstR, double dstG, double dstB,
        double srcR, double srcG, double srcB,
        int mode)
    {
        var (dstH, dstS, dstL) = RgbToHsl(dstR, dstG, dstB);
        var (srcH, srcS, srcL) = RgbToHsl(srcR, srcG, srcB);

        double outH, outS, outL;

        switch (mode)
        {
            case 0:
                outH = srcS == 0 ? dstH : srcH;
                outS = dstS;
                outL = dstL;
                break;
            case 1:
                outH = dstH;
                outS = srcS;
                outL = dstL;
                break;
            case 2:
                outH = srcH;
                outS = srcS;
                outL = dstL;
                break;
            default:
                outH = dstH;
                outS = dstS;
                outL = srcL;
                break;
        }

        return HslToRgb(outH, outS, outL);
    }

    private static (double h, double s, double l) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;

        if (max == min)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        else if (max == g)
            h = (b - r) / d + 2.0;
        else
            h = (r - g) / d + 4.0;
        h /= 6.0;

        return (h, s, l);
    }

    private static (double r, double g, double b) HslToRgb(double h, double s, double l)
    {
        if (s == 0)
            return (l, l, l);

        double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        var q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        var p = 2.0 * l - q;

        return (
            HueToRgb(p, q, h + 1.0 / 3.0),
            HueToRgb(p, q, h),
            HueToRgb(p, q, h - 1.0 / 3.0)
        );
    }

    private sealed class GroupProjectionCache
    {
        private bool _fullDirty = true;
        private PixelRegion? _dirtyRegion;

        public GroupProjectionCache(int width, int height)
        {
            Buffer = new TiledPixelBuffer(width, height);
        }

        public TiledPixelBuffer Buffer { get; private set; }

        public void EnsureSize(int width, int height)
        {
            if (Buffer.Width == width && Buffer.Height == height) return;

            Buffer = new TiledPixelBuffer(width, height);
            _fullDirty = true;
            _dirtyRegion = null;
        }

        public void Invalidate(PixelRegion? region)
        {
            if (region is null || region.Value.IsEmpty)
            {
                _fullDirty = true;
                _dirtyRegion = null;
                return;
            }

            if (_fullDirty) return;
            _dirtyRegion = _dirtyRegion is { } existing ? existing.Union(region.Value) : region.Value;
        }

        public PixelRegion TakeDirty(PixelRegion requestedClip)
        {
            var clip = requestedClip.ClipTo(Buffer.Width, Buffer.Height);
            if (clip.IsEmpty) return PixelRegion.Empty;

            if (_fullDirty)
            {
                _fullDirty = false;
                _dirtyRegion = null;
                return clip;
            }

            if (_dirtyRegion is not { } dirty)
                return PixelRegion.Empty;

            var dirtyClip = dirty.Intersect(clip);
            if (dirtyClip.IsEmpty)
                return PixelRegion.Empty;

            var remaining = dirtyClip.Width == dirty.Width && dirtyClip.Height == dirty.Height &&
                            dirtyClip.X == dirty.X && dirtyClip.Y == dirty.Y
                ? PixelRegion.Empty
                : dirty;
            _dirtyRegion = remaining.IsEmpty ? null : remaining;
            return dirtyClip;
        }
    }
}
