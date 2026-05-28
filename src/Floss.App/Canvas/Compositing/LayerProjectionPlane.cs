using System;
using System.Buffers;
using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Krita-style layer stack merge: walk sibling lists on the node tree only,
/// merge group children into per-group projection buffers, then apply groups
/// onto the parent buffer (see KisGroupLayer + KisAsyncMerger).
/// </summary>
internal sealed class LayerProjectionPlane : IDisposable
{
    private readonly ILayerMergeHost _host;
    // ConcurrentDictionary so parallel tile-composite workers can look up
    // (and lazily create) a group's projection cache without contending on a
    // global dictionary lock. Per-cache state mutations are serialised inside
    // GroupProjectionCache itself.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DrawingLayer, GroupProjectionCache> _groupCaches = new();
    private int _width;
    private int _height;

    /// <summary>Cached composite of layers below the active stroke layer (stroke fast path).</summary>
    public GroupProjectionCache? StrokeBelow { get; private set; }

    /// <summary>Cached composite of layers above the active stroke layer (stroke fast path).</summary>
    public GroupProjectionCache? StrokeAbove { get; private set; }

    /// <summary>
    /// When true, non–pass-through groups merge children directly into a temp
    /// buffer instead of the group projection cache. Avoids clear-and-refill
    /// races on the cache during live brush strokes inside folders.
    /// </summary>
    public bool LiveMergeGroups { get; set; }

    public LayerProjectionPlane(ILayerMergeHost host)
    {
        _host = host;
    }

    public void Dispose()
    {
        ResetStrokeBelow();
        ResetStrokeAbove();
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
        ClearRangeCaches();
    }

    public void SetSize(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = width;
        _height = height;
        ResetStrokeBelow();
        ResetStrokeAbove();
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
        ClearRangeCaches();
    }

    public void RemoveGroupCache(DrawingLayer group)
    {
        if (_groupCaches.TryRemove(group, out var cache))
            cache.Buffer.Dispose();
    }

    public void ResetStrokeBelow()
    {
        StrokeBelow?.Buffer.Dispose();
        StrokeBelow = null;
    }

    public void InvalidateStrokeBelow(PixelRegion? region)
    {
        if (StrokeBelow is not { } strokeBelow) return;
        lock (strokeBelow.SyncRoot)
            strokeBelow.Invalidate(region);
    }

    public GroupProjectionCache GetOrCreateStrokeBelow(int width, int height)
    {
        if (StrokeBelow == null)
        {
            StrokeBelow = new GroupProjectionCache(width, height);
            StrokeBelow.Invalidate(null);
        }
        else
            StrokeBelow.EnsureSize(width, height);
        return StrokeBelow;
    }

    public void ResetStrokeAbove()
    {
        StrokeAbove?.Buffer.Dispose();
        StrokeAbove = null;
    }

    public void InvalidateStrokeAbove(PixelRegion? region)
    {
        if (StrokeAbove is not { } strokeAbove) return;
        lock (strokeAbove.SyncRoot)
            strokeAbove.Invalidate(region);
    }

    public GroupProjectionCache GetOrCreateStrokeAbove(int width, int height)
    {
        if (StrokeAbove == null)
        {
            StrokeAbove = new GroupProjectionCache(width, height);
            StrokeAbove.Invalidate(null);
        }
        else
            StrokeAbove.EnsureSize(width, height);
        return StrokeAbove;
    }

    public void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex,
        bool fullGroupInvalidation = false, bool invalidateStrokeBelow = true, bool invalidateStrokeAbove = true)
    {
        DrawingLayer? changed = null;
        if (layers != null && layerIndex is >= 0 and var idx && idx < layers.Count)
            changed = layers[idx];
        InvalidateGroupCaches(region, changed, fullGroupInvalidation, invalidateStrokeBelow, invalidateStrokeAbove);
    }

    /// <summary>
    /// Invalidate group projection caches along the parent chain (KisMergeWalker-style).
    /// </summary>
    public void InvalidateGroupCaches(PixelRegion? region, DrawingLayer? changedLayer,
        bool fullGroupInvalidation = false, bool invalidateStrokeBelow = true, bool invalidateStrokeAbove = true)
    {
        if (region is null || region.Value.IsEmpty || changedLayer is null)
        {
            foreach (var cache in _groupCaches.Values)
            {
                lock (cache.SyncRoot)
                    cache.Invalidate(region);
            }
            if (invalidateStrokeBelow && StrokeBelow is { } strokeBelow)
            {
                lock (strokeBelow.SyncRoot)
                    strokeBelow.Invalidate(region);
            }
            if (invalidateStrokeAbove && StrokeAbove is { } strokeAbove)
            {
                lock (strokeAbove.SyncRoot)
                    strokeAbove.Invalidate(region);
            }
            return;
        }

        var r = region.Value;
        if (changedLayer.IsGroup && _groupCaches.TryGetValue(changedLayer, out var ownCache))
        {
            lock (ownCache.SyncRoot)
                ownCache.Invalidate(fullGroupInvalidation ? null : r);
        }

        for (var parent = changedLayer.Parent; parent != null; parent = parent.Parent)
        {
            if (_groupCaches.TryGetValue(parent, out var cache))
            {
                lock (cache.SyncRoot)
                    cache.Invalidate(fullGroupInvalidation ? null : r);
            }
        }

        if (invalidateStrokeBelow && StrokeBelow is { } strokeBelowInv)
        {
            lock (strokeBelowInv.SyncRoot)
                strokeBelowInv.Invalidate(r);
        }
        if (invalidateStrokeAbove && StrokeAbove is { } strokeAboveInv)
        {
            lock (strokeAboveInv.SyncRoot)
                strokeAboveInv.Invalidate(r);
        }
    }

    public void ClearRangeCaches()
    {
        foreach (var cache in _rangeCache.Values)
            cache.Buffer.Dispose();
        _rangeCache.Clear();
    }

    /// <summary>
    /// Composite renderList[first..first+count) into dst using pre-baked power-of-2-aligned
    /// range caches where possible (O(log N) buffer composites instead of O(N) layer passes).
    /// Only Normal/PassThrough/Copy-blend ranges are cached; mixed-blend slices fall back to direct.
    /// </summary>
    public unsafe void CompositeRenderListViaRangeCache(
        byte* dst, int dstStride, int width, int height,
        IReadOnlyList<ProjectionSiblingItem> renderList,
        int first, int count,
        double opacityScale, PixelRegion clip, int originX, int originY)
    {
        if (count <= 0 || opacityScale <= 0) return;
        var last = first + count - 1;
        var i = first;
        while (i <= last)
        {
            var blockSize = FindAlignedBlockSize(i, last - i + 1);
            var blockEnd = i + blockSize - 1;
            if (blockSize >= RangeCacheMinBlock && CanCacheRange(renderList, i, blockEnd))
            {
                var buf = GetOrBuildRangeBuffer(renderList, i, blockEnd, clip);
                LayerCompositorPixelOps.CompositeProjectionBuffer(dst, dstStride, buf, "Normal", opacityScale, clip, originX, originY);
            }
            else
            {
                CompositeSiblingStackSlice(dst, dstStride, width, height, renderList, i, blockEnd + 1, opacityScale, clip, originX, originY);
            }
            i = blockEnd + 1;
        }
    }

    private static int FindAlignedBlockSize(int start, int maxCount)
    {
        var size = 1;
        while (size * 2 <= maxCount && (start & (size * 2 - 1)) == 0)
            size *= 2;
        return size;
    }

    private static bool CanCacheRange(IReadOnlyList<ProjectionSiblingItem> list, int first, int last)
    {
        for (var i = first; i <= last; i++)
        {
            var blend = list[i].Layer.BlendMode;
            if (blend != "Normal" && blend != "PassThrough" && blend != "Copy")
                return false;
        }
        return true;
    }

    private unsafe TiledPixelBuffer GetOrBuildRangeBuffer(
        IReadOnlyList<ProjectionSiblingItem> renderList, int first, int last, PixelRegion clip)
    {
        var key = (renderList[first].Layer, renderList[last].Layer);
        if (!_rangeCache.TryGetValue(key, out var cache))
        {
            lock (_rangeCacheLock)
                cache = _rangeCache.GetOrAdd(key, _ => new GroupProjectionCache(_width, _height));
        }

        lock (cache.SyncRoot)
        {
            cache.EnsureSize(_width, _height);
            var clipToCanvas = clip.ClipTo(_width, _height);
            if (clipToCanvas.IsEmpty) return cache.Buffer;

            if (!cache.Buffer.HasContentTiles(clipToCanvas))
            {
                cache.Buffer.Clear(clipToCanvas);
                var tempLen = clipToCanvas.Width * clipToCanvas.Height * 4;
                var temp = System.Buffers.ArrayPool<byte>.Shared.Rent(tempLen);
                try
                {
                    Array.Clear(temp, 0, tempLen);
                    fixed (byte* tempPtr = temp)
                        CompositeSiblingStackSlice(tempPtr, clipToCanvas.Width * 4, clipToCanvas.Width, clipToCanvas.Height,
                            renderList, first, last + 1, 1.0, clipToCanvas, clipToCanvas.X, clipToCanvas.Y);
                    cache.Buffer.CopyFromBgra(clipToCanvas, temp, clipToCanvas.Width * 4);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(temp); }
            }
        }
        return cache.Buffer;
    }

    /// <summary>
    /// Bottom-to-top composite of one sibling list (KisNode children under a parent).
    /// </summary>
    public unsafe void CompositeSiblingList(
        byte* dst,
        int dstStride,
        int width,
        int height,
        IReadOnlyList<DrawingLayer> siblings,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (opacityScale <= 0) return;

        var stack = BuildSiblingStack(siblings);
        CompositeSiblingStack(dst, dstStride, width, height, stack, opacityScale, clip, originX, originY);
    }

    public unsafe void CompositeSiblingStack(
        byte* dst,
        int dstStride,
        int width,
        int height,
        IReadOnlyList<ProjectionSiblingItem> stack,
        double opacityScale,
        PixelRegion clip,
        int originX,
        int originY)
    {
        if (opacityScale <= 0) return;

        for (var i = 0; i < stack.Count; i++)
        {
            var item = stack[i];
            if (!item.Layer.IsVisible) continue;

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = stack[item.BaseLayerIndex].Layer;
                if (!baseLayer.IsVisible) continue;
                if (item.Layer.IsGroup)
                {
                    _host.CompositeClippedGroupIntoBuffer(dst, dstStride, width, height, item.Layer, baseLayer,
                        opacityScale, clip, originX, originY);
                }
                else if (item.Layer.Adjustment != null)
                {
                    AdjustmentLayerProcessor.ApplyClipped(dst, dstStride, width, height,
                        item.Layer.Adjustment, item.Layer.Opacity * opacityScale,
                        baseLayer, clip, originX, originY);
                }
                else
                {
                    _host.CompositeClippedPaintLayer(dst, dstStride, width, height, item.Layer, baseLayer,
                        opacityScale, clip, originX, originY);
                }
            }
            else if (item.Layer.IsGroup)
            {
                CompositeGroupNode(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            }
            else if (item.Layer.Adjustment != null)
            {
                AdjustmentLayerProcessor.Apply(dst, dstStride, width, height,
                    item.Layer.Adjustment, item.Layer.Opacity * opacityScale,
                    clip, originX, originY);
            }
            else
            {
                _host.CompositePaintLayer(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            }
        }
    }

    private unsafe void CompositeSiblingStackSlice(
        byte* dst, int dstStride, int width, int height,
        IReadOnlyList<ProjectionSiblingItem> stack,
        int startIndex, int endIndex,
        double opacityScale, PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0) return;
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = stack[i];
            if (!item.Layer.IsVisible) continue;

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = stack[item.BaseLayerIndex].Layer;
                if (!baseLayer.IsVisible) continue;
                if (item.Layer.IsGroup)
                    _host.CompositeClippedGroupIntoBuffer(dst, dstStride, width, height, item.Layer, baseLayer, opacityScale, clip, originX, originY);
                else if (item.Layer.Adjustment != null)
                    AdjustmentLayerProcessor.ApplyClipped(dst, dstStride, width, height, item.Layer.Adjustment, item.Layer.Opacity * opacityScale, baseLayer, clip, originX, originY);
                else
                    _host.CompositeClippedPaintLayer(dst, dstStride, width, height, item.Layer, baseLayer, opacityScale, clip, originX, originY);
            }
            else if (item.Layer.IsGroup)
                CompositeGroupNode(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            else if (item.Layer.Adjustment != null)
                AdjustmentLayerProcessor.Apply(dst, dstStride, width, height, item.Layer.Adjustment, item.Layer.Opacity * opacityScale, clip, originX, originY);
            else
                _host.CompositePaintLayer(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
        }
    }

    public static List<ProjectionSiblingItem> BuildSiblingStack(IReadOnlyList<DrawingLayer> siblings)
    {
        var result = new List<ProjectionSiblingItem>(siblings.Count);
        int? lastNonClippingIndex = null;

        for (var i = 0; i < siblings.Count; i++)
        {
            var layer = siblings[i];
            if (layer.IsClipping && lastNonClippingIndex.HasValue)
                result.Add(new ProjectionSiblingItem(layer, true, lastNonClippingIndex.Value));
            else
            {
                result.Add(new ProjectionSiblingItem(layer, false, -1));
                if (!layer.IsClipping)
                    lastNonClippingIndex = result.Count - 1;
            }
        }

        return result;
    }

    public unsafe void CompositeGroupNode(
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
            CompositeSiblingList(dst, dstStride, width, height, group.Children, groupOpacity, clip, originX, originY);
            return;
        }

        if (LiveMergeGroups)
        {
            LiveMergeGroupNode(dst, dstStride, width, height, group, groupOpacity, clip, originX, originY);
            return;
        }

        if (TryObligeChild(group, clip, out var _, out var obligedProjection) && obligedProjection != null)
        {
            _host.CompositeProjectionBuffer(dst, dstStride, obligedProjection, group.BlendMode, groupOpacity, clip,
                originX, originY);
            return;
        }

        var projection = GetGroupProjection(group, clip);
        _host.CompositeProjectionBuffer(dst, dstStride, projection, group.BlendMode, groupOpacity, clip, originX, originY);
    }

    private unsafe void LiveMergeGroupNode(
        byte* dst,
        int dstStride,
        int width,
        int height,
        DrawingLayer group,
        double groupOpacity,
        PixelRegion clip,
        int originX,
        int originY)
    {
        var tempLen = clip.Width * clip.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(tempLen);
        try
        {
            Array.Clear(temp, 0, tempLen);
            var stack = BuildSiblingStack(group.Children);
            fixed (byte* tempPtr = temp)
            {
                CompositeSiblingStack(tempPtr, clip.Width * 4, clip.Width, clip.Height,
                    stack, 1.0, clip, clip.X, clip.Y);
            }

            fixed (byte* tempPtr = temp)
            {
                LayerCompositorPixelOps.CompositeBgraBuffer(dst, dstStride, tempPtr, clip.Width * 4,
                    group.BlendMode, groupOpacity, clip, originX, originY);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    /// <summary>
    /// KisGroupLayer::tryObligeChild — reuse a single child's projection when safe.
    /// </summary>
    private bool TryObligeChild(
        DrawingLayer group,
        PixelRegion clip,
        out DrawingLayer? obligedLayer,
        out TiledPixelBuffer? childProjection)
    {
        obligedLayer = null;
        childProjection = null;

        var child = OnlyMeaningfulChild(group);
        if (child == null || !child.IsGroup || !CanObligeChild(group, child))
            return false;

        // Reuse nested group projection only (Krita tryObligeChild). Paint-layer oblige
        // would skip building the group buffer but our tile path uses tile-local buffers;
        // compositing the child paint device directly is not equivalent yet.
        childProjection = GetGroupProjection(child, clip);
        return true;
    }

    private static DrawingLayer? OnlyMeaningfulChild(DrawingLayer group)
    {
        DrawingLayer? only = null;
        foreach (var child in group.Children)
        {
            if (child.IsPaper)
                continue;
            if (only != null)
                return null;
            only = child;
        }

        return only;
    }

    private static bool CanObligeChild(DrawingLayer group, DrawingLayer child)
    {
        if (!child.IsVisible || child.Opacity < 0.999)
            return false;
        if (child.LayerColor.HasValue || child.ExpressionColor != ExpressionColorMode.Color)
            return false;

        var blend = child.BlendMode;
        if (blend is not ("Normal" or "Copy" or "AlphaDarken" or "Alpha Darken"))
            return false;

        // Krita: group projection buffer default pixel must be transparent.
        if (group.Pixels.HasContentTiles(new PixelRegion(0, 0, group.Width, group.Height)))
            return false;

        return true;
    }

    private readonly object _groupCacheCreateLock = new();

    // Range cache: pre-composited buffers for power-of-2-aligned contiguous slices of the
    // flat render list, keyed by (first layer identity, last layer identity).
    // Used to rebuild StrokeBelow/StrokeAbove in O(log N) buffer composites instead of O(N) layer iterates.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(DrawingLayer First, DrawingLayer Last), GroupProjectionCache> _rangeCache = new();
    private readonly object _rangeCacheLock = new();
    private const int RangeCacheMinBlock = 4;

    private unsafe TiledPixelBuffer GetGroupProjection(DrawingLayer group, PixelRegion requestedClip)
    {
        if (!_groupCaches.TryGetValue(group, out var cache))
        {
            lock (_groupCacheCreateLock)
            {
                cache = _groupCaches.GetOrAdd(group, _ => new GroupProjectionCache(_width, _height));
            }
        }

        lock (cache.SyncRoot)
        {
            cache.EnsureSize(_width, _height);

            var clip = requestedClip.ClipTo(_width, _height);
            if (clip.IsEmpty) return cache.Buffer;

            if (!cache.Buffer.HasContentTiles(clip))
            {
                cache.Buffer.Clear(clip);
                var tempLen = clip.Width * clip.Height * 4;
                var temp = ArrayPool<byte>.Shared.Rent(tempLen);
                try
                {
                    Array.Clear(temp, 0, tempLen);

                    var stack = BuildSiblingStack(group.Children);
                    fixed (byte* tempPtr = temp)
                    {
                        CompositeSiblingStack(tempPtr, clip.Width * 4, clip.Width, clip.Height,
                            stack, 1.0, clip, clip.X, clip.Y);
                    }

                    cache.Buffer.CopyFromBgra(clip, temp, clip.Width * 4);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }

        return cache.Buffer;
    }

    internal sealed class GroupProjectionCache
    {
        /// <summary>
        /// Lock object exposed to <see cref="LayerProjectionPlane.GetGroupProjection"/>
        /// so parallel tile-composite workers serialise on this single cache
        /// while still letting different groups composite concurrently.
        /// </summary>
        public object SyncRoot { get; } = new();

        public GroupProjectionCache(int width, int height)
        {
            Buffer = new TiledPixelBuffer(width, height);
        }

        public TiledPixelBuffer Buffer { get; private set; }

        public void EnsureSize(int width, int height)
        {
            if (Buffer.Width == width && Buffer.Height == height) return;

            Buffer.Dispose();
            Buffer = new TiledPixelBuffer(width, height);
        }

        /// <summary>
        /// Krita-aligned: clear the group projection buffer for the given region
        /// (KisGroupLayer::original()->clear(rect)). Next time GetGroupProjection
        /// needs this region, it will re-merge children because the buffer is empty.
        /// </summary>
        public void Invalidate(PixelRegion? region)
        {
            if (region is null || region.Value.IsEmpty)
            {
                Buffer.Clear();
                return;
            }

            Buffer.Clear(region.Value);
        }
    }
}

internal readonly record struct ProjectionSiblingItem(DrawingLayer Layer, bool IsClipped, int BaseLayerIndex);
