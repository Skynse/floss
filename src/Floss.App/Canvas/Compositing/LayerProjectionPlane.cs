using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Krita-style layer stack merge: walk sibling lists on the node tree only,
/// merge group children into per-group projection buffers, then apply groups
/// onto the parent buffer (see KisGroupLayer + KisAsyncMerger).
/// Simplified: no stroke-below/above caches, no range caches, no tryObligeChild.
/// </summary>
internal sealed class LayerProjectionPlane : IDisposable
{
    private readonly ILayerMergeHost _host;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DrawingLayer, GroupProjectionCache> _groupCaches = new();
    private int _width;
    private int _height;

    public LayerProjectionPlane(ILayerMergeHost host)
    {
        _host = host;
    }

    public void Dispose()
    {
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
    }

    public void SetSize(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = width;
        _height = height;
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
    }

    public void RemoveGroupCache(DrawingLayer group)
    {
        if (_groupCaches.TryRemove(group, out var cache))
            cache.Buffer.Dispose();
    }

    /// <summary>
    /// Invalidate group projection caches along the parent chain (KisMergeWalker-style).
    /// </summary>
    public void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex,
        bool fullGroupInvalidation = false)
    {
        DrawingLayer? changed = null;
        if (layers != null && layerIndex is >= 0 and var idx && idx < layers.Count)
            changed = layers[idx];
        InvalidateGroupCaches(region, changed, fullGroupInvalidation);
    }

    public void InvalidateGroupCaches(PixelRegion? region, DrawingLayer? changedLayer,
        bool fullGroupInvalidation = false)
    {
        if (region is null || region.Value.IsEmpty || changedLayer is null)
        {
            foreach (var cache in _groupCaches.Values)
            {
                lock (cache.SyncRoot)
                    cache.Invalidate(region);
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

        var projection = GetGroupProjection(group, clip);
        _host.CompositeProjectionBuffer(dst, dstStride, projection, group.BlendMode, groupOpacity, clip, originX, originY);
    }

    private readonly object _groupCacheCreateLock = new();

    private unsafe TiledPixelBuffer GetGroupProjection(DrawingLayer group, PixelRegion requestedClip)
    {
        if (!_groupCaches.TryGetValue(group, out var cache))
        {
            lock (_groupCacheCreateLock)
                cache = _groupCaches.GetOrAdd(group, _ => new GroupProjectionCache(_width, _height));
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
