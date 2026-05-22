using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Floss.App;
using Floss.App.Document;

namespace Floss.App.Canvas;

public sealed class LayerCompositor : IDisposable
{
    private const int MonochromeThreshold = 128;
    public const int DirtyTileBudget = 32;
    private const int MaxMissingTilesPerFrame = 96;
    private const int MaxCompositeCacheTiles = 768;

    public void Dispose()
    {
        ClearAllTiles();
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
    }

    // Keep compositor cache tiles close to paint tile granularity. 1024px tiles
    // made small strokes on large documents recomposite up to 1M pixels.
    private const int CmpTileSize = 256;

    private int _currentLod;
    // ConcurrentDictionary so DrawTiles (UI thread) can snapshot entries while
    // Composite (background thread) adds new tiles. Removal of disposed tiles
    // is funneled through _tilesPendingDispose -> UI thread to avoid disposing
    // a WriteableBitmap that's mid-draw on the UI thread.
    private readonly ConcurrentDictionary<(int X, int Y, int Lod), WriteableBitmap> _compTiles = new();
    private int _width;
    private int _height;
    private bool _fullDirty = true;
    private bool _metadataOnlyPass;
    private PixelRegion? _dirtyRegion;
    private readonly HashSet<(int X, int Y, int Lod)> _pendingDirtyTiles = [];
    private readonly HashSet<(int X, int Y, int Lod)> _tilesToPrune = [];
    private readonly Dictionary<DrawingLayer, GroupProjectionCache> _groupCaches = new();
    // Tiles removed during a composite pass — disposed lazily on the UI thread
    // so DrawImage(bitmap) on the UI thread never observes a freed bitmap.
    private readonly ConcurrentQueue<WriteableBitmap> _tilesPendingDispose = new();
    // Serialises Composite() vs DrawTiles() — UI render acquires this briefly
    // to atomically observe the tile map. Background composite holds it for
    // the whole pass. Reentrancy avoided by always taking from the outside in.
    internal readonly object CompositeGate = new();
    // Set to true while another thread is mid-Composite. UI render reads this
    // to avoid scheduling overlapping passes.
    private int _compositeActive;
    public bool IsCompositeActive => Volatile.Read(ref _compositeActive) != 0;
    public bool HasAnyTiles => !_compTiles.IsEmpty;
    // Stroke-suspend mode: active while a brush stroke is in progress. Used to
    // uncap the dirty-tile budget and enable the below-active-layer composite
    // fast path. Reference-counted for overlapping queued strokes.
    private int _strokeSuspendDepth;
    private int _strokePaintLayerIndex = -1;
    private GroupProjectionCache? _strokeBelowCache;
    public bool StrokeSuspendActive => _strokeSuspendDepth > 0;

    public void BeginStrokeSuspend(PixelRegion _)
    {
        lock (CompositeGate)
        {
            _strokeSuspendDepth++;
            _strokePaintLayerIndex = -1;
            _strokeBelowCache?.Buffer.Dispose();
            _strokeBelowCache = null;
        }
    }

    public void ExtendStrokeSuspend(PixelRegion _) { }

    public void EndStrokeSuspend()
    {
        lock (CompositeGate)
        {
            if (_strokeSuspendDepth > 0) _strokeSuspendDepth--;
            if (_strokeSuspendDepth > 0) return;
            _strokePaintLayerIndex = -1;
            _strokeBelowCache?.Buffer.Dispose();
            _strokeBelowCache = null;
        }
    }
    public int LastDirtyTileCount { get; private set; }
    public int LastMissingTileCount { get; private set; }
    public int LastLod => _currentLod;
    public int PendingDirtyTileCount => _pendingDirtyTiles.Count;

    public void DrainDisposalQueue()
    {
        // Called from UI thread to release bitmaps freed during background composite.
        while (_tilesPendingDispose.TryDequeue(out var bmp))
        {
            try { bmp.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    public void RemoveGroupCache(DrawingLayer group)
    {
        lock (CompositeGate)
        {
            if (_groupCaches.Remove(group, out var cache))
                cache.Buffer.Dispose();
        }
    }

    public void SetSize(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = width;
        _height = height;
        ClearAllTiles();
        _fullDirty = true;
        _dirtyRegion = null;
        _pendingDirtyTiles.Clear();
        foreach (var cache in _groupCaches.Values)
            cache.Buffer.Dispose();
        _groupCaches.Clear();
    }

    public void ClearAllTiles()
    {
        // Funnel disposal through the queue so any in-flight UI draw of these
        // bitmaps can finish before we free them.
        foreach (var t in _compTiles.Values) _tilesPendingDispose.Enqueue(t);
        _compTiles.Clear();
        _pendingDirtyTiles.Clear();
    }

    public unsafe WriteableBitmap Bitmap
    {
        get
        {
            if (_compTiles.Count == 0)
                return new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            if (_width * _height > 64_000_000)
                throw new InvalidOperationException(
                    $"Canvas is too large ({_width}x{_height}) for monolithic Bitmap assembly. Use CompositeToBgra instead.");

            var result = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var dstFrame = result.Lock();
            var dst = (byte*)dstFrame.Address;

            foreach (var ((tx, ty, lod), srcBmp) in _compTiles)
            {
                var stride = CmpTileSize;
                int tw = srcBmp.PixelSize.Width;
                int th = srcBmp.PixelSize.Height;
                int dx = tx * stride;
                int dy = ty * stride;

                if (tw <= 0 || th <= 0) { tw = 1; th = 1; }

                using var srcFrame = srcBmp.Lock();
                var src = (byte*)srcFrame.Address;
                var rowBytes = srcFrame.RowBytes;

                for (int y = 0; y < th; y++)
                    Buffer.MemoryCopy(src + y * rowBytes, dst + (dy + y) * dstFrame.RowBytes + dx * 4, tw * 4, tw * 4);
            }
            return result;
        }
    }

    public void DrawTiles(DrawingContext context, Rect target, PixelRegion? visibleViewport = null)
    {
        // Snapshot the tile map under the gate so a background composite pass
        // can't remove a bitmap mid-iteration. The snapshot pins references; the
        // disposal queue defers Dispose() until the next UI tick.
        KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot;
        lock (CompositeGate)
        {
            snapshot = new KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[_compTiles.Count];
            var i = 0;
            foreach (var kv in _compTiles)
            {
                if (i >= snapshot.Length) break;
                snapshot[i++] = kv;
            }
            if (i < snapshot.Length) Array.Resize(ref snapshot, i);
        }

        if (CurrentLodHasVisibleHoles(visibleViewport, snapshot))
        {
            foreach (var lod in CachedFallbackLods(snapshot))
            {
                for (var i = 0; i < snapshot.Length; i++)
                {
                    var ((tx, ty, tileLod), bmp) = snapshot[i];
                    if (tileLod != lod) continue;
                    DrawTile(context, bmp, tx, ty, tileLod, visibleViewport);
                }
            }
        }

        for (var i = 0; i < snapshot.Length; i++)
        {
            var ((tx, ty, lod), bmp) = snapshot[i];
            if (lod != _currentLod) continue;
            DrawTile(context, bmp, tx, ty, lod, visibleViewport);
        }
    }

    private IEnumerable<int> CachedFallbackLods(KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot)
    {
        var lods = new HashSet<int>();
        for (var i = 0; i < snapshot.Length; i++)
        {
            var key = snapshot[i].Key;
            if (key.Lod != _currentLod)
                lods.Add(key.Lod);
        }

        var ordered = new List<int>(lods);
        ordered.Sort((a, b) =>
        {
            var da = Math.Abs(a - _currentLod);
            var db = Math.Abs(b - _currentLod);
            var cmp = db.CompareTo(da); // farthest first, nearest fallback last
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        return ordered;
    }

    private void DrawTile(DrawingContext context, WriteableBitmap bmp, int tx, int ty, int lod, PixelRegion? visibleViewport)
    {
        var stride = CmpTileSize * (1 << lod);
        var docTileW = Math.Min(stride, _width - tx * stride);
        var docTileH = Math.Min(stride, _height - ty * stride);
        if (docTileW <= 0 || docTileH <= 0) { docTileW = 1; docTileH = 1; }
        var tileLeft = tx * stride;
        var tileTop = ty * stride;
        var tileRight = tileLeft + docTileW;
        var tileBottom = tileTop + docTileH;

        if (visibleViewport.HasValue)
        {
            var v = visibleViewport.Value;
            if (tileRight <= v.X || tileBottom <= v.Y || tileLeft >= v.Right || tileTop >= v.Bottom)
                return;
        }

        var src = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
        var dest = new Rect(tileLeft, tileTop, docTileW, docTileH);
        context.DrawImage(bmp, src, dest);
    }

    private bool CurrentLodHasVisibleHoles(PixelRegion? visibleViewport, KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot)
    {
        if (snapshot.Length == 0) return false;
        var stride = CmpTileSize * (1 << _currentLod);
        var clip = visibleViewport?.ClipTo(_width, _height) ?? new PixelRegion(0, 0, _width, _height);
        if (clip.IsEmpty) return false;

        var firstTX = FloorDiv(clip.X, stride);
        var firstTY = FloorDiv(clip.Y, stride);
        var lastTX = FloorDiv(clip.Right - 1, stride);
        var lastTY = FloorDiv(clip.Bottom - 1, stride);

        var have = new HashSet<(int X, int Y)>();
        for (var i = 0; i < snapshot.Length; i++)
        {
            var key = snapshot[i].Key;
            if (key.Lod == _currentLod) have.Add((key.X, key.Y));
        }

        for (var ty = firstTY; ty <= lastTY; ty++)
            for (var tx = firstTX; tx <= lastTX; tx++)
                if (!have.Contains((tx, ty)))
                    return true;
        return false;
    }

    public void Invalidate(PixelRegion? region = null)
        => Invalidate(region, null, null);

    public void Invalidate(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex, bool metadataOnly = false)
    {
        lock (CompositeGate)
        {
            if (metadataOnly)
                _metadataOnlyPass = true;

            if (layerIndex is >= 0 && _strokeSuspendDepth > 0)
                _strokePaintLayerIndex = layerIndex.Value;

            if (region is null || region.Value.IsEmpty)
            {
                _fullDirty = true;
                _dirtyRegion = null;
                ClearAllTiles();
                _strokeBelowCache?.Invalidate(null);
            }
            else if (!_fullDirty)
            {
                _dirtyRegion = _dirtyRegion is { } existing ? existing.Union(region.Value) : region.Value;
                // Do NOT drop cached compositor tiles here. Removing tiles before
                // the async composite finishes is what caused the horizontal-band
                // flashing with large brushes (256px tile holes). Stale tiles stay
                // visible until recomposited — one frame of lag beats a white gap.
            }

            InvalidateGroupCaches(region, layers, layerIndex);
        }
    }

    public bool Composite(IReadOnlyList<DrawingLayer> layers, int width, int height, uint paperColor = 0, PixelRegion? viewport = null, double zoom = 1.0)
    {
        // Hold the gate for the entire pass so:
        //   1. Invalidate() and SetSize() are blocked until we finish.
        //   2. DrawTiles() snapshots either the previous or this pass's tiles,
        //      never a half-mutated state.
        // We use a plain monitor lock (lock keyword) so re-entrancy from a
        // nested call is treated as a no-op rather than a deadlock — currently
        // CompositeCore does not call back into Invalidate, but be defensive.
        Interlocked.Increment(ref _compositeActive);
        try
        {
            lock (CompositeGate)
            {
                return CompositeCore(layers, width, height, paperColor, viewport, zoom);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _compositeActive);
        }
    }

    private unsafe bool CompositeCore(IReadOnlyList<DrawingLayer> layers, int width, int height, uint paperColor, PixelRegion? viewport, double zoom)
    {
        var started = Stopwatch.GetTimestamp();
        SetSize(width, height);

        // Skip paper layers — they're handled by ClearTile with paperColor.
        var rootLayers = new System.Collections.Generic.List<DrawingLayer>(layers.Count);
        foreach (var l in layers)
            if (l.Parent == null && !l.IsPaper) rootLayers.Add(l);

        var lod = SelectLod(width, height, zoom);
        var lodChanged = lod != _currentLod;
        _currentLod = lod;
        var scale = 1 << lod;
        var stride = CmpTileSize * scale;

        if (lodChanged)
        {
            // Re-key pending dirty tiles to the new LOD instead of discarding them.
            if (_pendingDirtyTiles.Count > 0)
            {
                var rekeyed = new List<(int X, int Y, int Lod)>(_pendingDirtyTiles.Count);
                foreach (var key in _pendingDirtyTiles)
                    rekeyed.Add(key);
                _pendingDirtyTiles.Clear();
                foreach (var key in rekeyed)
                {
                    if (key.Lod == lod) { _pendingDirtyTiles.Add(key); continue; }
                    var oldTileStride = CmpTileSize * (1 << key.Lod);
                    var region = new PixelRegion(key.X * oldTileStride, key.Y * oldTileStride,
                        oldTileStride, oldTileStride).ClipTo(width, height);
                    if (region.IsEmpty) continue;
                    var firstTX = FloorDiv(region.X, stride);
                    var firstTY = FloorDiv(region.Y, stride);
                    var lastTX = FloorDiv(region.Right - 1, stride);
                    var lastTY = FloorDiv(region.Bottom - 1, stride);
                    for (var ty = firstTY; ty <= lastTY; ty++)
                        for (var tx = firstTX; tx <= lastTX; tx++)
                            _pendingDirtyTiles.Add((tx, ty, lod));
                }
            }

            // Keep old LOD tiles as visual fallbacks. Throwing them away here is
            // what caused the hard zoom threshold stalls/blanking.
        }

        var dirtyClip = (_fullDirty ? new PixelRegion(0, 0, width, height) : _dirtyRegion ?? PixelRegion.Empty).ClipTo(width, height);
        var viewportClip = viewport?.ClipTo(width, height);

        // Fast path: nothing dirty and viewport not provided — nothing to do.
        if (dirtyClip.IsEmpty && viewportClip is null && _pendingDirtyTiles.Count == 0)
        {
            _fullDirty = false;
            _dirtyRegion = null;
            RenderTelemetry.RecordComposite(ElapsedMs(started), 0, 0, lod, _pendingDirtyTiles.Count);
            return false;
        }

        var tileKeys = new System.Collections.Generic.List<(int tx, int ty)>();
        var missingTileKeys = new System.Collections.Generic.List<(int tx, int ty)>();

        // 1. Dirty tiles.
        if (!dirtyClip.IsEmpty)
        {
            var firstDirtyTX = FloorDiv(dirtyClip.X, stride);
            var firstDirtyTY = FloorDiv(dirtyClip.Y, stride);
            var lastDirtyTX = FloorDiv(dirtyClip.Right - 1, stride);
            var lastDirtyTY = FloorDiv(dirtyClip.Bottom - 1, stride);

            for (var ty = firstDirtyTY; ty <= lastDirtyTY; ty++)
                for (var tx = firstDirtyTX; tx <= lastDirtyTX; tx++)
                {
                    var tileRect = new PixelRegion(tx * stride, ty * stride, stride, stride).Intersect(dirtyClip);
                    if (!tileRect.IsEmpty)
                        _pendingDirtyTiles.Add((tx, ty, lod));
                }
        }

        // 2. Missing tiles in viewport (pan / zoom reveals new area).
        if (viewportClip is { } visibleViewport && !visibleViewport.IsEmpty)
        {
            var firstVisibleTX = FloorDiv(visibleViewport.X, stride);
            var firstVisibleTY = FloorDiv(visibleViewport.Y, stride);
            var lastVisibleTX = FloorDiv(visibleViewport.Right - 1, stride);
            var lastVisibleTY = FloorDiv(visibleViewport.Bottom - 1, stride);

            for (var ty = firstVisibleTY; ty <= lastVisibleTY; ty++)
                for (var tx = firstVisibleTX; tx <= lastVisibleTX; tx++)
                {
                    if (_compTiles.ContainsKey((tx, ty, lod))) continue;
                    missingTileKeys.Add((tx, ty));
                }
        }

        tileKeys.AddRange(SelectPendingDirtyTiles(viewportClip, stride, lod));

        // Cap both dirty and missing tiles. Dirty tiles are persistent in
        // _pendingDirtyTiles; missing tiles are naturally rediscovered next frame.
        // Boost the missing tile budget when recovering from an LOD switch to
        // repopulate the viewport faster.
        var maxMissing = MaxMissingTilesPerFrame;
        var deferredMissingTiles = _pendingDirtyTiles.Count > 0;
        // During a live stroke, large brushes can dirty dozens of 256px tiles
        // per stamp — the default budget of 32 leaves permanent holes.
        var dirtyTileBudget = _metadataOnlyPass || _strokeSuspendDepth > 0
            ? int.MaxValue
            : DirtyTileBudget;
        _metadataOnlyPass = false;
        if (tileKeys.Count > dirtyTileBudget)
        {
            deferredMissingTiles = true;
            var extra = tileKeys.Count - dirtyTileBudget;
            for (var i = tileKeys.Count - 1; i >= dirtyTileBudget; i--)
            {
                var key = tileKeys[i];
                _pendingDirtyTiles.Add((key.tx, key.ty, lod));
            }
            tileKeys.RemoveRange(dirtyTileBudget, extra);
        }

        if (missingTileKeys.Count > maxMissing)
        {
            deferredMissingTiles = true;
            var cx = viewportClip is { } vp ? vp.X + vp.Width / 2 : _width / 2;
            var cy = viewportClip is { } vp2 ? vp2.Y + vp2.Height / 2 : _height / 2;
            missingTileKeys.Sort((a, b) =>
            {
                var da = Math.Abs(a.tx * stride + stride / 2 - cx) + Math.Abs(a.ty * stride + stride / 2 - cy);
                var db = Math.Abs(b.tx * stride + stride / 2 - cx) + Math.Abs(b.ty * stride + stride / 2 - cy);
                return da.CompareTo(db);
            });
            missingTileKeys.RemoveRange(maxMissing, missingTileKeys.Count - maxMissing);
        }

        if (tileKeys.Count == 0 && missingTileKeys.Count == 0)
        {
            _fullDirty = false;
            _dirtyRegion = null;
            LastDirtyTileCount = 0;
            LastMissingTileCount = 0;
            RenderTelemetry.RecordComposite(ElapsedMs(started), 0, 0, lod, 0);
            return deferredMissingTiles;
        }

        _fullDirty = false;
        _dirtyRegion = null;
        var renderList = FlattenForRender(rootLayers);

        // Ensure all tiles exist first (sequential — dictionary access).
        foreach (var (tx, ty) in tileKeys) EnsureTile(tx, ty, lod);
        foreach (var (tx, ty) in missingTileKeys) EnsureTile(tx, ty, lod);

        // Krita-style pre-warm: fill every group projection cache for the
        // exact set of tile rects we're about to composite, then mark all
        // caches clean. After this the parallel pass only READS from
        // _groupCaches (no Dictionary mutation, no projection refills) so
        // tile compositing becomes fully data-parallel.
        var totalTiles = tileKeys.Count + missingTileKeys.Count;
        if (totalTiles > 0 && _groupCaches.Count > 0 || ContainsAnyGroup(renderList))
            WarmGroupProjections(renderList, tileKeys, missingTileKeys, stride, width, height);

        // Dirty tiles: composite only the dirty sub-region of the tile.
        // Missing tiles: composite the FULL tile bounds — the WriteableBitmap has
        // uninitialized memory outside whatever region we clear, and DrawTiles draws
        // the full bitmap. Partial compositing would leave garbage pixels visible.
        var allTiles = new (int tx, int ty)[totalTiles];
        {
            var i = 0;
            foreach (var k in tileKeys) allTiles[i++] = k;
            foreach (var k in missingTileKeys) allTiles[i++] = k;
        }

        // Capture for closure (parallel body must be a value-type closure).
        var renderListLocal = renderList;
        var strideLocal = stride;
        var widthLocal = width;
        var heightLocal = height;
        var lodLocal = lod;
        var paperLocal = paperColor;
        var compTilesLocal = _compTiles;

        var strokeSplit = -1;
        if (_strokeSuspendDepth > 0 && lod == 0 && _strokePaintLayerIndex >= 0 && _strokePaintLayerIndex < layers.Count)
            strokeSplit = FindRenderSplit(renderList, layers[_strokePaintLayerIndex]);

        if (strokeSplit > 0)
        {
            foreach (var (tx, ty) in tileKeys)
            {
                var tileRect = new PixelRegion(tx * stride, ty * stride, stride, stride).ClipTo(width, height);
                if (!tileRect.IsEmpty)
                    WarmStrokeBelowForTile(renderList, strokeSplit, tileRect, width, height);
            }
            foreach (var (tx, ty) in missingTileKeys)
            {
                var tileRect = new PixelRegion(tx * stride, ty * stride, stride, stride).ClipTo(width, height);
                if (!tileRect.IsEmpty)
                    WarmStrokeBelowForTile(renderList, strokeSplit, tileRect, width, height);
            }
            _strokeBelowCache?.FlushFullDirty();
        }

        var strokeSplitLocal = strokeSplit;

        void CompositeOne(int idx)
        {
            var (tx, ty) = allTiles[idx];
            var tileRect = new PixelRegion(tx * strideLocal, ty * strideLocal, strideLocal, strideLocal).ClipTo(widthLocal, heightLocal);
            if (tileRect.IsEmpty) tileRect = new PixelRegion(tx * strideLocal, ty * strideLocal, 1, 1);
            CompositeTileCpu(compTilesLocal[(tx, ty, lodLocal)], tileRect, renderListLocal, tx * strideLocal, ty * strideLocal, lodLocal, paperLocal, strokeSplitLocal);
        }

        if (allTiles.Length >= 4 && Environment.ProcessorCount > 1)
            Parallel.For(0, allTiles.Length, CompositeOne);
        else
            for (var i = 0; i < allTiles.Length; i++) CompositeOne(i);

        _tilesToPrune.Clear();
        foreach (var (tx, ty) in tileKeys) _tilesToPrune.Add((tx, ty, lod));
        foreach (var (tx, ty) in missingTileKeys) _tilesToPrune.Add((tx, ty, lod));

        PruneTransparentTiles();
        TrimCompositeCache(viewportClip);
        LastDirtyTileCount = tileKeys.Count;
        LastMissingTileCount = missingTileKeys.Count;
        RenderTelemetry.RecordComposite(ElapsedMs(started), tileKeys.Count, missingTileKeys.Count, lod, _pendingDirtyTiles.Count);
        return deferredMissingTiles || _pendingDirtyTiles.Count > 0;
    }

    private static bool ContainsAnyGroup(IReadOnlyList<RenderItem> renderList)
    {
        for (var i = 0; i < renderList.Count; i++)
            if (renderList[i].Layer.IsGroup) return true;
        return false;
    }

    private void WarmGroupProjections(
        IReadOnlyList<RenderItem> renderList,
        List<(int tx, int ty)> tileKeys,
        List<(int tx, int ty)> missingTileKeys,
        int stride, int width, int height)
    {
        // Walk every tile rect we'll touch and pre-fill any group cache for it.
        // This replicates the lazy per-tile fill the old code did inline, but
        // SEQUENTIALLY before the parallel composite pass.
        foreach (var (tx, ty) in tileKeys)
            WarmGroupsForTile(renderList, tx, ty, stride, width, height);
        foreach (var (tx, ty) in missingTileKeys)
            WarmGroupsForTile(renderList, tx, ty, stride, width, height);

        // Mark every group cache clean so parallel tile compositing finds
        // TakeDirty empty → returns cached buffer without further work.
        foreach (var cache in _groupCaches.Values)
            cache.FlushFullDirty();
    }

    private void WarmGroupsForTile(
        IReadOnlyList<RenderItem> renderList, int tx, int ty,
        int stride, int width, int height)
    {
        var tileRect = new PixelRegion(tx * stride, ty * stride, stride, stride).ClipTo(width, height);
        if (tileRect.IsEmpty) return;
        for (var i = 0; i < renderList.Count; i++)
        {
            var layer = renderList[i].Layer;
            if (layer.IsGroup)
                WarmGroupRecursive(layer, tileRect);
        }
    }

    private void WarmGroupRecursive(DrawingLayer group, PixelRegion clip)
    {
        if (group.BlendMode == "PassThrough")
        {
            // PassThrough groups don't have their own projection cache —
            // their children composite directly into the parent. But a
            // non-passthrough subgroup inside still needs warming.
            var children = group.Children;
            for (var i = 0; i < children.Count; i++)
                if (children[i].IsGroup)
                    WarmGroupRecursive(children[i], clip);
            return;
        }

        // Fills cache.Buffer for the tile clip; GetGroupProjection's
        // internal CompositeLayerList encounters nested non-passthrough
        // subgroups and calls THEIR GetGroupProjection, so nested caches
        // get filled recursively.
        GetGroupProjection(group, clip);
    }

    private List<(int tx, int ty)> SelectPendingDirtyTiles(PixelRegion? viewportClip, int stride, int lod)
    {
        var result = new List<(int tx, int ty)>();
        if (_pendingDirtyTiles.Count == 0) return result;

        var cx = viewportClip is { } vp ? vp.X + vp.Width / 2 : _width / 2;
        var cy = viewportClip is { } vp2 ? vp2.Y + vp2.Height / 2 : _height / 2;
        var candidates = new List<(int tx, int ty)>();
        foreach (var key in _pendingDirtyTiles)
            if (key.Lod == lod)
                candidates.Add((key.X, key.Y));

        candidates.Sort((a, b) =>
        {
            var da = Math.Abs(a.tx * stride + stride / 2 - cx) + Math.Abs(a.ty * stride + stride / 2 - cy);
            var db = Math.Abs(b.tx * stride + stride / 2 - cx) + Math.Abs(b.ty * stride + stride / 2 - cy);
            return da.CompareTo(db);
        });

        foreach (var key in candidates)
        {
            _pendingDirtyTiles.Remove((key.tx, key.ty, lod));
            result.Add(key);
        }
        return result;
    }

    public int SelectLod(int width, int height, double zoom)
    {
        var pixels = (long)width * height;
        var candidate = 0;
        if (zoom < 0.18 && pixels > 16_000_000) candidate = 2;
        else if (zoom < 0.35 && pixels > 8_000_000) candidate = 1;

        // Hysteresis: require a wider margin to switch LOD, preventing rapid
        // oscillation when zooming in/out near the boundary.
        if (candidate > _currentLod)
            return candidate; // zooming out → switch eagerly
        if (candidate < _currentLod)
        {
            // zooming in → require more evidence before dropping LOD
            var leaveThreshold = _currentLod == 1 ? 0.42 : 0.24;
            if (zoom > leaveThreshold)
                return candidate;
        }
        return _currentLod;
    }

    public static int CountTilesForRegion(PixelRegion region, int lod)
    {
        if (region.IsEmpty) return 0;
        var stride = CmpTileSize * (1 << Math.Clamp(lod, 0, 8));
        var firstX = FloorDiv(region.X, stride);
        var firstY = FloorDiv(region.Y, stride);
        var lastX = FloorDiv(region.Right - 1, stride);
        var lastY = FloorDiv(region.Bottom - 1, stride);
        return (lastX - firstX + 1) * (lastY - firstY + 1);
    }

    private static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private WriteableBitmap EnsureTile(int tx, int ty, int lod)
    {
        var key = (tx, ty, lod);
        if (_compTiles.TryGetValue(key, out var existing))
            return existing;
        var scale = 1 << lod;
        var docStride = CmpTileSize * scale;
        var docW = Math.Min(docStride, _width - tx * docStride);
        var docH = Math.Min(docStride, _height - ty * docStride);
        if (docW <= 0 || docH <= 0) docW = docH = 1;
        var tileW = Math.Max(1, (docW + scale - 1) / scale);
        var tileH = Math.Max(1, (docH + scale - 1) / scale);
        var fresh = new WriteableBitmap(new PixelSize(tileW, tileH), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        // GetOrAdd here protects against a racy duplicate create. The wasted
        // bitmap is enqueued for UI-thread disposal.
        var t = _compTiles.GetOrAdd(key, fresh);
        if (!ReferenceEquals(t, fresh))
            _tilesPendingDispose.Enqueue(fresh);
        return t;
    }

    private void PruneTransparentTiles()
    {
        if (_tilesToPrune.Count > 16) return;
        foreach (var key in _tilesToPrune)
        {
            if (_compTiles.TryGetValue(key, out var bmp) && IsWriteableBitmapTransparent(bmp))
            {
                if (_compTiles.TryRemove(key, out var removed))
                    _tilesPendingDispose.Enqueue(removed);
            }
        }
    }

    private void DropCachedTilesOverlapping(PixelRegion region)
    {
        if (region.IsEmpty || _compTiles.IsEmpty) return;
        var clipped = region.ClipTo(_width, _height);
        if (clipped.IsEmpty) return;

        var toDrop = new List<(int X, int Y, int Lod)>();
        foreach (var key in _compTiles.Keys)
        {
            var stride = CmpTileSize * (1 << key.Lod);
            var tileRegion = new PixelRegion(key.X * stride, key.Y * stride, stride, stride).ClipTo(_width, _height);
            if (!tileRegion.Intersect(clipped).IsEmpty)
                toDrop.Add(key);
        }

        foreach (var key in toDrop)
        {
            if (_compTiles.TryRemove(key, out var bmp))
                _tilesPendingDispose.Enqueue(bmp);
            _pendingDirtyTiles.Remove(key);
        }
    }

    private void TrimCompositeCache(PixelRegion? viewportClip)
    {
        if (_compTiles.Count <= MaxCompositeCacheTiles) return;

        var cx = viewportClip is { } vp ? vp.X + vp.Width / 2 : _width / 2;
        var cy = viewportClip is { } vp2 ? vp2.Y + vp2.Height / 2 : _height / 2;
        var keys = new List<(int X, int Y, int Lod)>(_compTiles.Keys);
        keys.Sort((a, b) =>
        {
            var da = CacheTileDistance(a, cx, cy);
            var db = CacheTileDistance(b, cx, cy);
            var cmp = db.CompareTo(da); // farthest first
            if (cmp != 0) return cmp;
            var ac = a.Lod == _currentLod ? 1 : 0;
            var bc = b.Lod == _currentLod ? 1 : 0;
            return ac.CompareTo(bc); // prefer dropping fallback before current
        });

        var target = MaxCompositeCacheTiles * 3 / 4;
        for (var i = 0; i < keys.Count && _compTiles.Count > target; i++)
        {
            var key = keys[i];
            if (_compTiles.Count <= DirtyTileBudget) break;
            if (_compTiles.TryRemove(key, out var bmp))
            {
                _tilesPendingDispose.Enqueue(bmp);
                _pendingDirtyTiles.Remove(key);
            }
        }

        double CacheTileDistance((int X, int Y, int Lod) key, double centerX, double centerY)
        {
            var stride = CmpTileSize * (1 << key.Lod);
            var tileCenterX = key.X * stride + stride / 2.0;
            var tileCenterY = key.Y * stride + stride / 2.0;
            var lodPenalty = key.Lod == _currentLod ? 0.0 : stride;
            return Math.Abs(tileCenterX - centerX) + Math.Abs(tileCenterY - centerY) + lodPenalty;
        }
    }

    private static int FindRenderSplit(IReadOnlyList<RenderItem> renderList, DrawingLayer paintLayer)
    {
        for (var i = 0; i < renderList.Count; i++)
            if (ReferenceEquals(renderList[i].Layer, paintLayer))
                return i;
        return -1;
    }

    private unsafe void WarmStrokeBelowForTile(
        IReadOnlyList<RenderItem> renderList, int split, PixelRegion tileRect, int width, int height)
    {
        if (_strokeBelowCache == null)
        {
            _strokeBelowCache = new GroupProjectionCache(width, height);
            _strokeBelowCache.Invalidate(null);
        }
        else
            _strokeBelowCache.EnsureSize(width, height);

        var dirty = _strokeBelowCache.TakeDirty(tileRect);
        if (dirty.IsEmpty) return;

        _strokeBelowCache.Buffer.Clear(dirty);
        var tempLen = dirty.Width * dirty.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(tempLen);
        Array.Clear(temp, 0, tempLen);
        fixed (byte* tempPtr = temp)
        {
            CompositeRenderListRange(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height,
                renderList, 0, split, 1.0, dirty, dirty.X, dirty.Y);
        }
        _strokeBelowCache.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
        _strokeBelowCache.Buffer.CompressTiles();
        ArrayPool<byte>.Shared.Return(temp);
    }

    private unsafe void CompositeTileCpu(WriteableBitmap tile, PixelRegion tileRect,
        IReadOnlyList<RenderItem> renderList, int originX, int originY, int lod, uint paperColor = 0,
        int strokeSplit = -1)
    {
        if (lod > 0)
        {
            CompositeTileLodCpu(tile, tileRect, renderList, originX, originY, lod, paperColor);
            return;
        }

        if (strokeSplit > 0 && _strokeBelowCache != null)
        {
            CompositeTileStrokeCpu(tile, tileRect, renderList, strokeSplit, originX, originY, paperColor);
            return;
        }

        var tw = tile.PixelSize.Width;
        var th = tile.PixelSize.Height;
        using var frame = tile.Lock();
        var dst = (byte*)frame.Address;
        var dstStride = frame.RowBytes;
        ClearTile(dst, dstStride, tileRect, originX, originY, paperColor);
        CompositeRenderList(dst, dstStride, tw, th, renderList, 1.0, tileRect, originX, originY);
    }

    private unsafe void CompositeTileStrokeCpu(WriteableBitmap tile, PixelRegion tileRect,
        IReadOnlyList<RenderItem> renderList, int split, int originX, int originY, uint paperColor)
    {
        var tw = tile.PixelSize.Width;
        var th = tile.PixelSize.Height;
        using var frame = tile.Lock();
        var dst = (byte*)frame.Address;
        var dstStride = frame.RowBytes;
        ClearTile(dst, dstStride, tileRect, originX, originY, paperColor);
        CompositeProjectionBuffer(dst, dstStride, _strokeBelowCache!.Buffer, "Normal", 1.0, tileRect, originX, originY);
        CompositeRenderListRange(dst, dstStride, tw, th, renderList, split, renderList.Count, 1.0, tileRect, originX, originY);
    }

    private unsafe void CompositeTileLodCpu(WriteableBitmap tile, PixelRegion tileRect,
        IReadOnlyList<RenderItem> renderList, int originX, int originY, int lod, uint paperColor)
    {
        // FAST PATH: if every layer is Normal-blend with no fancy color, no
        // clipping group and no nested groups, composite DIRECTLY at the LOD
        // resolution by point-sampling layer pixels with a stride. This
        // bypasses the full-res-then-bilinear-downscale tax that costs 4× at
        // LOD 1 and 16× at LOD 2.
        if (CanLodFastPath(renderList))
        {
            CompositeTileLodFastCpu(tile, tileRect, renderList, originX, originY, lod, paperColor);
            return;
        }

        var fullBytes = tileRect.Width * tileRect.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(fullBytes);
        try
        {
            fixed (byte* tempPtr = temp)
            {
                var count = tileRect.Width * tileRect.Height;
                var clear = (uint*)tempPtr;
                for (var i = 0; i < count; i++) clear[i] = paperColor;

                CompositeRenderList(tempPtr, tileRect.Width * 4, tileRect.Width, tileRect.Height,
                    renderList, 1.0, tileRect, originX, originY);

                using var frame = tile.Lock();
                var dst = (byte*)frame.Address;
                var dstStride = frame.RowBytes;
                var scale = 1 << lod;
                for (var y = 0; y < tile.PixelSize.Height; y++)
                {
                    var sy = Math.Min(tileRect.Height - 1.0f, (y + 0.5f) * scale - 0.5f);
                    var sy0 = Math.Clamp((int)MathF.Floor(sy), 0, tileRect.Height - 1);
                    var sy1 = Math.Min(tileRect.Height - 1, sy0 + 1);
                    var fy = sy - sy0;
                    var srcRow0 = tempPtr + sy0 * tileRect.Width * 4;
                    var srcRow1 = tempPtr + sy1 * tileRect.Width * 4;
                    var dstRow = dst + y * dstStride;
                    for (var x = 0; x < tile.PixelSize.Width; x++)
                    {
                        var sx = Math.Min(tileRect.Width - 1.0f, (x + 0.5f) * scale - 0.5f);
                        var sx0 = Math.Clamp((int)MathF.Floor(sx), 0, tileRect.Width - 1);
                        var sx1 = Math.Min(tileRect.Width - 1, sx0 + 1);
                        var fx = sx - sx0;
                        var p00 = srcRow0 + sx0 * 4;
                        var p10 = srcRow0 + sx1 * 4;
                        var p01 = srcRow1 + sx0 * 4;
                        var p11 = srcRow1 + sx1 * 4;
                        var dstPx = dstRow + x * 4;
                        dstPx[0] = BilinearByte(p00[0], p10[0], p01[0], p11[0], fx, fy);
                        dstPx[1] = BilinearByte(p00[1], p10[1], p01[1], p11[1], fx, fy);
                        dstPx[2] = BilinearByte(p00[2], p10[2], p01[2], p11[2], fx, fy);
                        dstPx[3] = BilinearByte(p00[3], p10[3], p01[3], p11[3], fx, fy);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private static bool CanLodFastPath(IReadOnlyList<RenderItem> renderList)
    {
        for (var i = 0; i < renderList.Count; i++)
        {
            var item = renderList[i];
            var layer = item.Layer;
            if (item.IsClipped) return false;
            if (layer.IsGroup) return false;
            if (layer.BlendMode != "Normal") return false;
            if (layer.LayerColor.HasValue) return false;
            if (layer.ExpressionColor != ExpressionColorMode.Color) return false;
        }
        return true;
    }

    private unsafe void CompositeTileLodFastCpu(WriteableBitmap tile, PixelRegion tileRect,
        IReadOnlyList<RenderItem> renderList, int originX, int originY, int lod, uint paperColor)
    {
        using var frame = tile.Lock();
        var dst = (byte*)frame.Address;
        var dstStride = frame.RowBytes;
        var scale = 1 << lod;
        var halfStep = scale >> 1;
        var dstW = tile.PixelSize.Width;
        var dstH = tile.PixelSize.Height;

        // Paper fill at LOD pixel scale.
        for (var y = 0; y < dstH; y++)
        {
            var row = (uint*)(dst + y * dstStride);
            for (var x = 0; x < dstW; x++) row[x] = paperColor;
        }

        const int ts = TiledPixelBuffer.TileSize;
        for (var i = 0; i < renderList.Count; i++)
        {
            var layer = renderList[i].Layer;
            if (!layer.IsVisible || layer.Opacity <= 0) continue;

            var opacityByte = (uint)Math.Round(layer.Opacity * 255);
            if (opacityByte == 0) continue;
            var fullOpacity = opacityByte == 255;
            var offsetX = layer.OffsetX;
            var offsetY = layer.OffsetY;

            layer.Pixels.EnterPixelReadLock();
            try
            {
                for (var py = 0; py < dstH; py++)
                {
                    var docY = originY + py * scale + halfStep;
                    var srcY = docY - offsetY;
                    if (srcY < layer.MinY || srcY >= layer.MaxY) continue;

                    var tileY = FloorDiv(srcY, ts);
                    var tileLocalY = srcY - tileY * ts;
                    var dstRow = (uint*)(dst + py * dstStride);
                    int prevTileX = int.MinValue;
                    byte[]? srcTile = null;

                    for (var px = 0; px < dstW; px++)
                    {
                        var docX = originX + px * scale + halfStep;
                        var srcX = docX - offsetX;
                        if (srcX < layer.MinX || srcX >= layer.MaxX) continue;

                        var tileX = FloorDiv(srcX, ts);
                        if (tileX != prevTileX)
                        {
                            srcTile = layer.Pixels.GetTileOrNull(tileX, tileY);
                            prevTileX = tileX;
                        }
                        if (srcTile == null) continue;

                        var localX = srcX - tileX * ts;
                        var tileOffset = (tileLocalY * ts + localX) * 4;
                        uint rawA = srcTile[tileOffset + 3];
                        if (rawA == 0) continue;

                        uint srcA = fullOpacity ? rawA : (rawA * opacityByte + 127) / 255;
                        uint sb = srcTile[tileOffset + 0];
                        uint sg = srcTile[tileOffset + 1];
                        uint sr = srcTile[tileOffset + 2];
                        if (srcA == 255)
                        {
                            dstRow[px] = sb | (sg << 8) | (sr << 16) | 0xFF000000u;
                            continue;
                        }
                        var d = dstRow[px];
                        uint dA = (d >> 24) & 0xFF;
                        uint dB = d & 0xFF;
                        uint dG = (d >> 8) & 0xFF;
                        uint dR = (d >> 16) & 0xFF;
                        uint invSrcA = 255 - srcA;
                        uint dstCont = (dA * invSrcA + 127) / 255;
                        uint outA = srcA + dstCont;
                        if (outA == 0) continue;
                        uint half = outA >> 1;
                        uint ob = (sb * srcA + dB * dstCont + half) / outA;
                        uint og = (sg * srcA + dG * dstCont + half) / outA;
                        uint or = (sr * srcA + dR * dstCont + half) / outA;
                        dstRow[px] = ob | (og << 8) | (or << 16) | (outA << 24);
                    }
                }
            }
            finally
            {
                layer.Pixels.ExitPixelReadLock();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte BilinearByte(byte p00, byte p10, byte p01, byte p11, float fx, float fy)
    {
        var top = p00 + (p10 - p00) * fx;
        var bottom = p01 + (p11 - p01) * fx;
        return (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * fy), 0, 255);
    }

    // ── CPU compositing ──────────────────────────────────────────────────────

    private unsafe void CompositeRenderList(byte* dst, int dstStride, int width, int height,
        IReadOnlyList<RenderItem> renderList, double opacityScale, PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0) return;

        for (int i = 0; i < renderList.Count; i++)
        {
            var item = renderList[i];
            if (!item.Layer.IsVisible) continue;

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = renderList[item.BaseLayerIndex].Layer;
                if (!baseLayer.IsVisible) continue;
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

    private unsafe void CompositeRenderListRange(byte* dst, int dstStride, int width, int height,
        IReadOnlyList<RenderItem> renderList, int startIndex, int endIndex,
        double opacityScale, PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0 || startIndex >= endIndex) return;

        for (int i = startIndex; i < endIndex; i++)
        {
            var item = renderList[i];
            if (!item.Layer.IsVisible) continue;

            if (item.IsClipped && item.BaseLayerIndex >= 0)
            {
                var baseLayer = renderList[item.BaseLayerIndex].Layer;
                if (!baseLayer.IsVisible) continue;
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

    private static unsafe bool IsWriteableBitmapTransparent(WriteableBitmap bmp)
    {
        using var frame = bmp.Lock();
        var ptr = (byte*)frame.Address;
        var w = bmp.PixelSize.Width;
        var h = bmp.PixelSize.Height;
        var stride = frame.RowBytes;
        for (int y = 0; y < h; y++)
        {
            var row = (uint*)(ptr + y * stride);
            for (int x = 0; x < w; x++)
                if ((row[x] & 0xFF000000) != 0) return false;
        }
        return true;
    }

    private static unsafe void ClearTile(byte* dst, int stride, PixelRegion clip, int originX, int originY, uint clearValue = 0)
    {
        for (int y = clip.Y; y < clip.Bottom; y++)
        {
            var localY = y - originY;
            if (localY < 0) continue;
            var localX = clip.X - originX;
            if (localX < 0) continue;
            var row = (uint*)(dst + localY * stride + localX * 4);
            for (int x = 0; x < clip.Width; x++)
                row[x] = clearValue;
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

    public unsafe byte[] CompositeToBgra(IReadOnlyList<DrawingLayer> layers, int width, int height, uint paperColor = 0)
    {
        lock (CompositeGate)
        {
            var buf = new byte[width * height * 4];
            if (paperColor != 0)
            {
                var pc = paperColor;
                fixed (byte* dst = buf)
                {
                    var p = (uint*)dst;
                    var count = width * height;
                    for (int i = 0; i < count; i++)
                        p[i] = pc;
                }
            }
            var clip = new PixelRegion(0, 0, width, height);
            fixed (byte* dst = buf)
                CompositeLayerList(dst, width * 4, width, height, layers, 1.0, clip, 0, 0);
            return buf;
        }
    }

    public unsafe Color? SampleCompositePixel(IReadOnlyList<DrawingLayer> layers, int width, int height, int x, int y, uint paperColor = 0)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return null;

        lock (CompositeGate)
        {
            uint pixel = paperColor;
            var rootLayers = new List<DrawingLayer>(layers.Count);
            foreach (var layer in layers)
                if (layer.Parent == null && !layer.IsPaper)
                    rootLayers.Add(layer);

            var renderList = FlattenForRender(rootLayers);
            var clip = new PixelRegion(x, y, 1, 1);
            CompositeRenderList((byte*)&pixel, 4, 1, 1, renderList, 1.0, clip, x, y);

            var b = (byte)(pixel & 0xFF);
            var g = (byte)((pixel >> 8) & 0xFF);
            var r = (byte)((pixel >> 16) & 0xFF);
            var a = (byte)((pixel >> 24) & 0xFF);
            return a == 0 ? null : Color.FromArgb(a, r, g, b);
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
                if (!baseLayer.IsVisible) continue;
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
            var tempLen = dirty.Width * dirty.Height * 4;
            var temp = ArrayPool<byte>.Shared.Rent(tempLen);
            Array.Clear(temp, 0, tempLen);
            fixed (byte* tempPtr = temp)
            {
                CompositeLayerList(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height, group.Children, opacityScale: 1.0, dirty, dirty.X, dirty.Y);
            }
            cache.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
            cache.Buffer.CompressTiles();
            ArrayPool<byte>.Shared.Return(temp);
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
        var firstTileX = FloorDiv(clip.X, ts);
        var firstTileY = FloorDiv(clip.Y, ts);
        var lastTileX = FloorDiv(clip.Right - 1, ts);
        var lastTileY = FloorDiv(clip.Bottom - 1, ts);

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
                        var j = 0;

                        // SIMD pass: handle 4-pixel chunks of trivially opaque
                        // or transparent runs with a single 128-bit store/skip.
                        if (fullOpacity && Sse2.IsSupported)
                        {
                            fixed (byte* tileFix = tile)
                            {
                                for (; j + 3 < rowWidth; j += 4)
                                {
                                    var tileOffset = tileRowBase + j * 4;
                                    var docX = tileLeft + j;
                                    var dstPtr = dstRow + (docX - originX) * 4;

                                    var srcQuad = Sse2.LoadVector128((uint*)(tileFix + tileOffset));
                                    var alphas = Sse2.ShiftRightLogical(srcQuad, 24);
                                    var isOpaque = Sse2.CompareEqual(alphas, Vector128.Create(0xFFu));
                                    if (Sse2.MoveMask(isOpaque.AsByte()) == 0xFFFF)
                                    {
                                        Sse2.Store((uint*)dstPtr, srcQuad);
                                        continue;
                                    }
                                    var isZero = Sse2.CompareEqual(alphas, Vector128<uint>.Zero);
                                    if (Sse2.MoveMask(isZero.AsByte()) == 0xFFFF) continue;
                                    // Mixed alphas: bail out to scalar tail for the whole row.
                                    break;
                                }
                            }
                        }

                        for (int docX = tileLeft + j; docX < tileRight; docX++, j++)
                        {
                            var tileOffset = tileRowBase + j * 4;
                            uint rawA = tile[tileOffset + 3];
                            if (rawA == 0) continue;

                            uint srcA = fullOpacity ? rawA : (rawA * opacityByte + 127) / 255;
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
                            uint dstA = dstPtr[3];
                            uint dstCont = (dstA * invSrcA + 127) / 255;
                            uint outA = srcA + dstCont;
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

        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;
        var baseOffsetX = baseLayer.OffsetX;
        var baseOffsetY = baseLayer.OffsetY;
        var baseW = baseLayer.Width;
        var baseH = baseLayer.Height;

        var docLeft = Math.Max(Math.Max(clip.X, offsetX + layer.MinX), 0);
        var docTop = Math.Max(Math.Max(clip.Y, offsetY + layer.MinY), 0);
        var docRight = Math.Min(Math.Min(clip.Right, offsetX + layer.MaxX), width + originX);
        var docBottom = Math.Min(Math.Min(clip.Bottom, offsetY + layer.MaxY), height + originY);

        if (docLeft >= docRight || docTop >= docBottom) return;

        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        if (!layer.Pixels.HasContentTiles(sourceRegion)) return;

        layer.Pixels.EnterPixelReadLock();
        baseLayer.Pixels.EnterPixelReadLock();
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
                var tile = layer.Pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                var clipLeft = Math.Max(sourceRegion.X, tx * ts);
                var clipTop = Math.Max(sourceRegion.Y, ty * ts);
                var clipRight = Math.Min(sourceRegion.Right, tx * ts + ts);
                var clipBottom = Math.Min(sourceRegion.Bottom, ty * ts + ts);

                var isNormal = blendMode == "Normal";
                var opacityInt = (uint)Math.Round(opacity * 255);

                for (int srcY = clipTop; srcY < clipBottom; srcY++)
                {
                    var tileLocalY = srcY - ty * ts;
                    var tileLocalX0 = clipLeft - tx * ts;
                    var tileRowBase = (tileLocalY * ts + tileLocalX0) * 4;
                    var docY = srcY + offsetY;
                    var dstRow = dst + (docY - originY) * dstStride;
                    var baseY = docY - baseOffsetY;

                    if (baseY < 0 || baseY >= baseH) continue;

                    var baseTileY = FloorDiv(baseY, ts);
                    var baseTileLocalY = baseY - baseTileY * ts;

                    int prevBaseTileX = int.MinValue;
                    byte[]? baseTile = null;

                    for (int j = 0, srcX = clipLeft; srcX < clipRight; srcX++, j++)
                    {
                        var tileOffset = tileRowBase + j * 4;
                        uint rawA = tile[tileOffset + 3];
                        if (rawA == 0) continue;

                        var docX = srcX + offsetX;
                        var baseX = docX - baseOffsetX;
                        if (baseX < 0 || baseX >= baseW) continue;

                        var baseTileX = FloorDiv(baseX, ts);
                        if (baseTileX != prevBaseTileX)
                        {
                            baseTile = baseLayer.Pixels.GetTileOrNull(baseTileX, baseTileY);
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
            baseLayer.Pixels.ExitPixelReadLock();
            layer.Pixels.ExitPixelReadLock();
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

        var tempLen = clip.Width * clip.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(tempLen);
        Array.Clear(temp, 0, tempLen);
        fixed (byte* tempPtr = temp)
        {
            if (group.BlendMode == "PassThrough")
                CompositeLayerList(tempPtr, clip.Width * 4, clip.Width, clip.Height, group.Children, groupOpacity, clip, clip.X, clip.Y);
            else
                CompositeGroup(tempPtr, clip.Width * 4, clip.Width, clip.Height, group, opacityScale, clip, clip.X, clip.Y);

            CompositeClippedBuffer(dst, dstStride, width, height, tempPtr, clip.Width * 4, baseLayer, group.BlendMode, clip, originX, originY);
        }
        ArrayPool<byte>.Shared.Return(temp);
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

        baseLayer.Pixels.EnterPixelReadLock();
        try
        {
        for (int docY = clip.Y; docY < clip.Bottom; docY++)
        {
            var baseY = docY - baseOffsetY;
            if (baseY < 0 || baseY >= baseH) continue;

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
                if (baseX < 0 || baseX >= baseW) continue;

                var baseTileX = FloorDiv(baseX, ts);
                if (baseTileX != prevBaseTileX)
                {
                    baseTile = baseLayer.Pixels.GetTileOrNull(baseTileX, baseTileY);
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
        finally { baseLayer.Pixels.ExitPixelReadLock(); }
    }

    // ── Blend LUTs ─────────────────────────────────────────────────────────--
    private static readonly byte[] LUT_Overlay = BuildLUT((s, d) => Overlay(s, d));
    private static readonly byte[] LUT_SoftLight = BuildLUT((s, d) => SoftLight(s, d));
    private static readonly byte[] LUT_HardLight = BuildLUT((s, d) => HardLight(s, d));
    private static readonly byte[] LUT_ColorDodge = BuildLUT((s, d) => ColorDodge(s, d));
    private static readonly byte[] LUT_ColorBurn = BuildLUT((s, d) => ColorBurn(s, d));
    private static readonly byte[] LUT_LinearBurn = BuildLUT((s, d) => Math.Max(0, d + s - 1));
    private static readonly byte[] LUT_LinearDodge = BuildLUT((s, d) => Math.Min(1, d + s));
    private static readonly byte[] LUT_VividLight = BuildLUT((s, d) => VividLight(s, d));
    private static readonly byte[] LUT_LinearLight = BuildLUT((s, d) => LinearLight(s, d));
    private static readonly byte[] LUT_PinLight = BuildLUT((s, d) => PinLight(s, d));
    private static readonly byte[] LUT_HardMix = BuildLUT((s, d) => HardMix(s, d));
    private static readonly byte[] LUT_Subtract = BuildLUT((s, d) => Math.Max(0, d - s));
    private static readonly byte[] LUT_Divide = BuildLUT((s, d) => s <= 0 ? 1.0 : Math.Min(1.0, d / s));

    private static byte[] BuildLUT(Func<double, double, double> fn)
    {
        var lut = new byte[65536];
        for (int s = 0; s < 256; s++)
            for (int d = 0; d < 256; d++)
                lut[(s << 8) | d] = (byte)Math.Clamp(Math.Round(fn(s / 255.0, d / 255.0) * 255), 0, 255);
        return lut;
    }

    private static byte[] GetLut(string mode) => mode switch
    {
        "Overlay" => LUT_Overlay,
        "SoftLight" => LUT_SoftLight,
        "HardLight" => LUT_HardLight,
        "ColorDodge" => LUT_ColorDodge,
        "ColorBurn" => LUT_ColorBurn,
        "LinearBurn" => LUT_LinearBurn,
        "LinearDodge" => LUT_LinearDodge,
        "VividLight" => LUT_VividLight,
        "LinearLight" => LUT_LinearLight,
        "PinLight" => LUT_PinLight,
        "HardMix" => LUT_HardMix,
        "Subtract" => LUT_Subtract,
        "Divide" => LUT_Divide,
        _ => null!
    };

    private static bool HasLut(string mode) => mode switch
    {
        "Overlay" or "SoftLight" or "HardLight" or "ColorDodge" or "ColorBurn"
        or "LinearBurn" or "LinearDodge" or "VividLight" or "LinearLight"
        or "PinLight" or "HardMix" or "Subtract" or "Divide" => true,
        _ => false
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void BlendPixelInt(byte* dst,
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

    private static unsafe void CompositeLayer(byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, double opacityScale, PixelRegion clip, int originX, int originY)
    {
        var opacity = layer.Opacity * opacityScale;
        if (opacity <= 0) return;
        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;
        var docLeft = Math.Max(Math.Max(clip.X, offsetX + layer.MinX), 0);
        var docTop = Math.Max(Math.Max(clip.Y, offsetY + layer.MinY), 0);
        var docRight = Math.Min(Math.Min(clip.Right, offsetX + layer.MaxX), width + originX);
        var docBottom = Math.Min(Math.Min(clip.Bottom, offsetY + layer.MaxY), height + originY);
        if (docLeft >= docRight || docTop >= docBottom) return;
        var sourceRegion = new PixelRegion(docLeft - offsetX, docTop - offsetY, docRight - docLeft, docBottom - docTop);
        if (!layer.Pixels.HasContentTiles(sourceRegion)) return;

        layer.Pixels.EnterPixelReadLock();
        try
        {

        var blendMode = layer.BlendMode;
        var layerColor = layer.LayerColor;
        var hasLayerColor = layerColor.HasValue;
        var expressionColor = layer.ExpressionColor;
        byte lcR = 255, lcG = 255, lcB = 255;
        if (layerColor is { } lc) { lcR = lc.R; lcG = lc.G; lcB = lc.B; }
        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(sourceRegion.X, ts);
        var firstTileY = FloorDiv(sourceRegion.Y, ts);
        var lastTileX = FloorDiv(sourceRegion.Right - 1, ts);
        var lastTileY = FloorDiv(sourceRegion.Bottom - 1, ts);
        var applyExpr = expressionColor != ExpressionColorMode.Color;

        // Integer fast path for Normal blend
        if (blendMode == "Normal")
        {
            var opacityByte = (uint)Math.Round(opacity * 255);
            var fullOpacity = opacityByte == 255;
            for (var ty = firstTileY; ty <= lastTileY; ty++)
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var tile = layer.Pixels.GetTileOrNull(tx, ty);
                    if (tile == null) continue;
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
                        var j = 0;

                        if (!hasLayerColor && !applyExpr)
                        {
                            // Fast path: no layer color or expression color.
                            // Process 4 pixels at a time for better instruction-level parallelism.
                            for (; j + 3 < rowWidth; j += 4)
                            {
                                var tileOffset = tileRowBase + j * 4;
                                var docX = clipLeft + j + offsetX;
                                var dstPtr = dstRow + (docX - originX) * 4;

                                // SIMD pre-screen: when all 4 source pixels are fully
                                // opaque AND the layer is at full opacity, we can use
                                // a single 16-byte SIMD store. When all 4 are fully
                                // transparent, skip. Falls through otherwise.
                                if (fullOpacity && Sse2.IsSupported)
                                {
                                    bool skipScalar = false;
                                    fixed (byte* tileFix = tile)
                                    {
                                        var srcQuad = Sse2.LoadVector128((uint*)(tileFix + tileOffset));
                                        var alphas = Sse2.ShiftRightLogical(srcQuad, 24);
                                        var isOpaque = Sse2.CompareEqual(alphas, Vector128.Create(0xFFu));
                                        var opaqueMask = Sse2.MoveMask(isOpaque.AsByte());
                                        if (opaqueMask == 0xFFFF)
                                        {
                                            Sse2.Store((uint*)dstPtr, srcQuad);
                                            skipScalar = true;
                                        }
                                        else
                                        {
                                            var isZero = Sse2.CompareEqual(alphas, Vector128<uint>.Zero);
                                            var zeroMask = Sse2.MoveMask(isZero.AsByte());
                                            if (zeroMask == 0xFFFF) skipScalar = true;
                                        }
                                    }
                                    if (skipScalar) continue;
                                }

                                // Pixel 0
                                uint rawA0 = tile[tileOffset + 3];
                                if (rawA0 != 0)
                                {
                                    uint srcA0 = fullOpacity ? rawA0 : (rawA0 * opacityByte + 127) / 255;
                                    byte sb0 = tile[tileOffset + 0], sg0 = tile[tileOffset + 1], sr0 = tile[tileOffset + 2];
                                    if (srcA0 == 255)
                                    {
                                        dstPtr[0] = sb0; dstPtr[1] = sg0; dstPtr[2] = sr0; dstPtr[3] = 255;
                                    }
                                    else
                                    {
                                        uint inv0 = 255 - srcA0, dd0 = dstPtr[3];
                                        uint dc0 = (dd0 * inv0 + 127) / 255;
                                        uint oa0 = srcA0 + dc0;
                                        if (oa0 != 0)
                                        {
                                            uint h0 = oa0 >> 1;
                                            dstPtr[0] = (byte)((sb0 * srcA0 + dstPtr[0] * dc0 + h0) / oa0);
                                            dstPtr[1] = (byte)((sg0 * srcA0 + dstPtr[1] * dc0 + h0) / oa0);
                                            dstPtr[2] = (byte)((sr0 * srcA0 + dstPtr[2] * dc0 + h0) / oa0);
                                            dstPtr[3] = (byte)oa0;
                                        }
                                    }
                                }

                                // Pixel 1
                                uint rawA1 = tile[tileOffset + 7];
                                if (rawA1 != 0)
                                {
                                    uint srcA1 = fullOpacity ? rawA1 : (rawA1 * opacityByte + 127) / 255;
                                    byte sb1 = tile[tileOffset + 4], sg1 = tile[tileOffset + 5], sr1 = tile[tileOffset + 6];
                                    if (srcA1 == 255)
                                    {
                                        dstPtr[4] = sb1; dstPtr[5] = sg1; dstPtr[6] = sr1; dstPtr[7] = 255;
                                    }
                                    else
                                    {
                                        uint inv1 = 255 - srcA1, dd1 = dstPtr[7];
                                        uint dc1 = (dd1 * inv1 + 127) / 255;
                                        uint oa1 = srcA1 + dc1;
                                        if (oa1 != 0)
                                        {
                                            uint h1 = oa1 >> 1;
                                            dstPtr[4] = (byte)((sb1 * srcA1 + dstPtr[4] * dc1 + h1) / oa1);
                                            dstPtr[5] = (byte)((sg1 * srcA1 + dstPtr[5] * dc1 + h1) / oa1);
                                            dstPtr[6] = (byte)((sr1 * srcA1 + dstPtr[6] * dc1 + h1) / oa1);
                                            dstPtr[7] = (byte)oa1;
                                        }
                                    }
                                }

                                // Pixel 2
                                uint rawA2 = tile[tileOffset + 11];
                                if (rawA2 != 0)
                                {
                                    uint srcA2 = fullOpacity ? rawA2 : (rawA2 * opacityByte + 127) / 255;
                                    byte sb2 = tile[tileOffset + 8], sg2 = tile[tileOffset + 9], sr2 = tile[tileOffset + 10];
                                    if (srcA2 == 255)
                                    {
                                        dstPtr[8] = sb2; dstPtr[9] = sg2; dstPtr[10] = sr2; dstPtr[11] = 255;
                                    }
                                    else
                                    {
                                        uint inv2 = 255 - srcA2, dd2 = dstPtr[11];
                                        uint dc2 = (dd2 * inv2 + 127) / 255;
                                        uint oa2 = srcA2 + dc2;
                                        if (oa2 != 0)
                                        {
                                            uint h2 = oa2 >> 1;
                                            dstPtr[8] = (byte)((sb2 * srcA2 + dstPtr[8] * dc2 + h2) / oa2);
                                            dstPtr[9] = (byte)((sg2 * srcA2 + dstPtr[9] * dc2 + h2) / oa2);
                                            dstPtr[10] = (byte)((sr2 * srcA2 + dstPtr[10] * dc2 + h2) / oa2);
                                            dstPtr[11] = (byte)oa2;
                                        }
                                    }
                                }

                                // Pixel 3
                                uint rawA3 = tile[tileOffset + 15];
                                if (rawA3 != 0)
                                {
                                    uint srcA3 = fullOpacity ? rawA3 : (rawA3 * opacityByte + 127) / 255;
                                    byte sb3 = tile[tileOffset + 12], sg3 = tile[tileOffset + 13], sr3 = tile[tileOffset + 14];
                                    if (srcA3 == 255)
                                    {
                                        dstPtr[12] = sb3; dstPtr[13] = sg3; dstPtr[14] = sr3; dstPtr[15] = 255;
                                    }
                                    else
                                    {
                                        uint inv3 = 255 - srcA3, dd3 = dstPtr[15];
                                        uint dc3 = (dd3 * inv3 + 127) / 255;
                                        uint oa3 = srcA3 + dc3;
                                        if (oa3 != 0)
                                        {
                                            uint h3 = oa3 >> 1;
                                            dstPtr[12] = (byte)((sb3 * srcA3 + dstPtr[12] * dc3 + h3) / oa3);
                                            dstPtr[13] = (byte)((sg3 * srcA3 + dstPtr[13] * dc3 + h3) / oa3);
                                            dstPtr[14] = (byte)((sr3 * srcA3 + dstPtr[14] * dc3 + h3) / oa3);
                                            dstPtr[15] = (byte)oa3;
                                        }
                                    }
                                }
                            }
                        }

                        // Slow path (or tail of fast path): handle layer color / expression color.
                        for (; j < rowWidth; j++)
                        {
                            var tileOffset = tileRowBase + j * 4;
                            uint rawA = tile[tileOffset + 3];
                            if (rawA == 0) continue;
                            var docX = clipLeft + j + offsetX;
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
                    var tile = layer.Pixels.GetTileOrNull(tx, ty);
                    if (tile == null) continue;
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
                var tile = layer.Pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;
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
        finally { layer.Pixels.ExitPixelReadLock(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyLayerColor(ref byte b, ref byte g, ref byte r, byte layerB, byte layerG, byte layerR)
    {
        var lum = (r * 299 + g * 587 + b * 114) / 1000;
        var ink = 255 - lum;
        b = (byte)(lum + (layerB * ink) / 255);
        g = (byte)(lum + (layerG * ink) / 255);
        r = (byte)(lum + (layerR * ink) / 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyExpressionColorToSource(ref byte b, ref byte g, ref byte r, ref uint a, ExpressionColorMode mode)
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

    private static unsafe void ApplyExpressionColor(
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

            Buffer.Dispose();
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

            // When fully dirty, return clip for every tile — DO NOT consume _fullDirty
            // here. The compositor calls FlushFullDirty() after all tiles are processed
            // so that each compositor tile gets a correct re-render of the group.
            if (_fullDirty)
                return clip;

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

        public void FlushFullDirty()
        {
            _fullDirty = false;
            _dirtyRegion = null;
        }
    }
}
