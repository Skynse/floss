using System;
using System.Buffers;
using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Canvas;

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
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
    }

    public void SetSize(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = width;
        _height = height;
        ResetStrokeBelow();
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
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

    public void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex, bool fullGroupInvalidation = false)
    {
        DrawingLayer? changed = null;
        if (layers != null && layerIndex is >= 0 and var idx && idx < layers.Count)
            changed = layers[idx];
        InvalidateGroupCaches(region, changed, fullGroupInvalidation);
    }

    /// <summary>
    /// Invalidate group projection caches along the parent chain (KisMergeWalker-style).
    /// </summary>
    public void InvalidateGroupCaches(PixelRegion? region, DrawingLayer? changedLayer, bool fullGroupInvalidation = false)
    {
        if (region is null || region.Value.IsEmpty || changedLayer is null)
        {
            foreach (var cache in _groupCaches.Values)
            {
                lock (cache.SyncRoot)
                    cache.Invalidate(region);
            }
            if (StrokeBelow is { } strokeBelow)
            {
                lock (strokeBelow.SyncRoot)
                    strokeBelow.Invalidate(region);
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

        // A group that obliges this layer reads its pixels directly — no cache entry, but
        // any cached ancestor above that group still needs invalidation (handled by parent walk).
        if (StrokeBelow is { } strokeBelowInv)
        {
            lock (strokeBelowInv.SyncRoot)
                strokeBelowInv.Invalidate(r);
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

    private unsafe TiledPixelBuffer GetGroupProjection(DrawingLayer group, PixelRegion requestedClip)
    {
        // GetOrAdd's factory can run concurrently — TiledPixelBuffer registers
        // with TileSwapManager in its ctor, so we don't want phantom buffers
        // leaking. Use a small create-lock around the miss case.
        if (!_groupCaches.TryGetValue(group, out var cache))
        {
            lock (_groupCacheCreateLock)
            {
                cache = _groupCaches.GetOrAdd(group, _ => new GroupProjectionCache(_width, _height));
            }
        }

        // Serialise per-cache state (TakeDirty/NoteRegionComposited) plus the
        // dirty-region composite below. Different groups can still composite
        // in parallel; only multiple workers targeting the same group serialise.
        lock (cache.SyncRoot)
        {
            cache.EnsureSize(_width, _height);

            var dirty = cache.TakeDirty(requestedClip);
            if (dirty.IsEmpty && !cache.Buffer.HasContentTiles(requestedClip))
            {
                cache.Invalidate(requestedClip);
                dirty = cache.TakeDirty(requestedClip);
            }

            if (!dirty.IsEmpty)
            {
                cache.Buffer.Clear(dirty);
                var tempLen = dirty.Width * dirty.Height * 4;
                var temp = ArrayPool<byte>.Shared.Rent(tempLen);
                try
                {
                    Array.Clear(temp, 0, tempLen);

                    // Build the sibling stack ONCE — the row-by-row variant
                    // allocated a fresh List<ProjectionSiblingItem> per row,
                    // which was tens of thousands of allocations per group
                    // composite on a large viewport.
                    var stack = BuildSiblingStack(group.Children);
                    fixed (byte* tempPtr = temp)
                    {
                        CompositeSiblingStack(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height,
                            stack, 1.0, dirty, dirty.X, dirty.Y);
                    }

                    cache.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
                    // Always mark the dirty rect composited — even when every
                    // pixel is transparent, the cache was cleared and merged.
                    cache.NoteRegionComposited(dirty);
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
        private bool _fullDirty = true;
        private PixelRegion? _dirtyRegion;
        private PixelRegion? _cleanRegion;

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
            _fullDirty = true;
            _dirtyRegion = null;
            _cleanRegion = null;
        }

        public void Invalidate(PixelRegion? region)
        {
            if (region is null || region.Value.IsEmpty)
            {
                _fullDirty = true;
                _dirtyRegion = null;
                _cleanRegion = null;
                return;
            }

            if (_fullDirty) return;
            _cleanRegion = null;
            _dirtyRegion = _dirtyRegion is { } existing ? existing.Union(region.Value) : region.Value;
        }

        public void NoteRegionComposited(PixelRegion filled)
        {
            if (filled.IsEmpty) return;
            if (_fullDirty)
            {
                _fullDirty = false;
                _cleanRegion = filled;
                _dirtyRegion = null;
                return;
            }

            _cleanRegion = _cleanRegion is { } existing ? existing.Union(filled) : filled;
        }

        public PixelRegion TakeDirty(PixelRegion requestedClip)
        {
            var clip = requestedClip.ClipTo(Buffer.Width, Buffer.Height);
            if (clip.IsEmpty) return PixelRegion.Empty;

            if (_fullDirty)
                return clip;

            // Pending dirty always wins over a previously composited clean region.
            // Checking clean first caused rectangular holes when painting inside
            // folders: the parent group cache was marked clean for a compositor
            // tile while _dirtyRegion still covered the live stroke bbox.
            if (_dirtyRegion is { } dirty)
            {
                var dirtyClip = dirty.Intersect(clip);
                if (!dirtyClip.IsEmpty)
                {
                    if (dirtyClip.X == dirty.X && dirtyClip.Y == dirty.Y &&
                        dirtyClip.Width == dirty.Width && dirtyClip.Height == dirty.Height)
                        _dirtyRegion = null;
                    return dirtyClip;
                }
            }

            if (_cleanRegion is { } clean && IsRegionCovered(clip, clean))
                return PixelRegion.Empty;

            if (_dirtyRegion is null)
                return clip;

            return PixelRegion.Empty;
        }

        public void FlushFullDirty()
        {
            _fullDirty = false;
            _dirtyRegion = null;
            _cleanRegion = null;
        }

        private static bool IsRegionCovered(PixelRegion clip, PixelRegion clean)
            => clip.X >= clean.X && clip.Y >= clean.Y
               && clip.Right <= clean.Right && clip.Bottom <= clean.Bottom;
    }
}

internal readonly record struct ProjectionSiblingItem(DrawingLayer Layer, bool IsClipped, int BaseLayerIndex);
