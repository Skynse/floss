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
using SkiaSharp;

namespace Floss.App.Canvas;

public sealed class LayerCompositor : IDisposable
{
    private LayerProjectionPlane? _projection;
    private LayerProjectionPlane Projection =>
        _projection ??= new LayerProjectionPlane(new MergeHost(this));

    private const int MaxCompositeLod = 2;
    public const int DirtyTileBudget = 32;
    private const int MaxMissingTilesPerFrame = 96;
    private const int MaxCompositeCacheTiles = 768;

    public void Dispose()
    {
        ClearAllTiles();
        _projection?.Dispose();
        _projection = null;
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
    // Tiles allocated but not yet composited — DrawTiles skips these so lower-LOD
    // fallbacks stay visible instead of flashing uninitialized/garbage alpha.
    // ConcurrentDictionary so the UI render thread can snapshot it without
    // acquiring CompositeGate (which the background pass holds for hundreds
    // of ms on large invalidations and would otherwise freeze the UI).
    private readonly ConcurrentDictionary<(int X, int Y, int Lod), byte> _tilesPendingComposite = new();
    private readonly HashSet<(int X, int Y, int Lod)> _tilesToPrune = [];
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
    public bool StrokeSuspendActive => _strokeSuspendDepth > 0;

    public void BeginStrokeSuspend(PixelRegion _)
    {
        lock (CompositeGate)
        {
            _strokeSuspendDepth++;
            _strokePaintLayerIndex = -1;
            Projection.ResetStrokeBelow();
        }
    }

    public void ExtendStrokeSuspend(PixelRegion region)
    {
        if (region.IsEmpty) return;
        lock (CompositeGate)
        {
            if (_strokeSuspendDepth == 0) return;
            Projection.InvalidateStrokeBelow(region);
        }
    }

    public void EndStrokeSuspend()
    {
        lock (CompositeGate)
        {
            if (_strokeSuspendDepth > 0) _strokeSuspendDepth--;
            if (_strokeSuspendDepth > 0) return;
            _strokePaintLayerIndex = -1;
            Projection.ResetStrokeBelow();
        }
    }

    /// <summary>Force-clear stroke-suspend state after an abandoned or interrupted stroke.</summary>
    public void ResetStrokeSuspend()
    {
        lock (CompositeGate)
        {
            _strokeSuspendDepth = 0;
            _strokePaintLayerIndex = -1;
            Projection.ResetStrokeBelow();
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
            Projection.RemoveGroupCache(group);
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
        Projection.SetSize(width, height);
    }

    public void ClearAllTiles()
    {
        // Funnel disposal through the queue so any in-flight UI draw of these
        // bitmaps can finish before we free them.
        foreach (var t in _compTiles.Values) _tilesPendingDispose.Enqueue(t);
        _compTiles.Clear();
        _pendingDirtyTiles.Clear();
        _tilesPendingComposite.Clear();
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
        // Lock-free snapshot — both backing stores are concurrent. Holding
        // CompositeGate here would block the UI for the full duration of any
        // background composite pass (often 50–300ms on large invalidations),
        // which is exactly the freeze users notice. Bitmaps removed mid-iter
        // stay alive via the disposal queue until the next UI tick.
        var snapshot = _compTiles.ToArray();
        var displayLod = _currentLod;
        var pendingCurrentLod = new HashSet<(int X, int Y)>();
        foreach (var kv in _tilesPendingComposite)
        {
            if (kv.Key.Lod == displayLod)
                pendingCurrentLod.Add((kv.Key.X, kv.Key.Y));
        }

        if (CurrentLodHasVisibleHoles(visibleViewport, snapshot, pendingCurrentLod, displayLod))
        {
            foreach (var lod in CachedFallbackLods(snapshot, displayLod))
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
            if (lod != displayLod) continue;
            // Skip not-yet-composited tiles — they are cleared to transparent and
            // punch through to paper white. Prefer finer-LOD fallback when available.
            if (pendingCurrentLod.Contains((tx, ty)))
            {
                if (displayLod > 0 && HasFinerLodFallback(snapshot, tx, ty, displayLod))
                    continue;
                continue;
            }
            DrawTile(context, bmp, tx, ty, lod, visibleViewport);
        }
    }

    private static readonly HashSet<(int X, int Y, int Lod)> _snapshotLookup = new(768);

    private bool HasFinerLodFallback(
        KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot,
        int tx, int ty,
        int displayLod)
    {
        var stride = CmpTileSize * (1 << displayLod);
        var tileLeft = tx * stride;
        var tileTop = ty * stride;
        var tileRight = Math.Min(tileLeft + stride, _width);
        var tileBottom = Math.Min(tileTop + stride, _height);

        _snapshotLookup.Clear();
        for (var i = 0; i < snapshot.Length; i++)
            _snapshotLookup.Add(snapshot[i].Key);

        // Require a full cover of finer-LOD tiles — a single overlapping tile
        // is not enough and produced patchwork holes during LOD transitions.
        for (var finerLod = displayLod - 1; finerLod >= 0; finerLod--)
        {
            var fStride = CmpTileSize * (1 << finerLod);
            var firstTX = FloorDiv(tileLeft, fStride);
            var firstTY = FloorDiv(tileTop, fStride);
            var lastTX = FloorDiv(tileRight - 1, fStride);
            var lastTY = FloorDiv(tileBottom - 1, fStride);
            var complete = true;
            for (var fty = firstTY; fty <= lastTY && complete; fty++)
            {
                for (var ftx = firstTX; ftx <= lastTX; ftx++)
                {
                    if (!_snapshotLookup.Contains((ftx, fty, finerLod)))
                    {
                        complete = false;
                        break;
                    }
                }
            }

            if (complete)
                return true;
        }

        return false;
    }

    private static bool SnapshotHasReadyTile(
        KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot,
        int tx, int ty, int lod)
    {
        for (var i = 0; i < snapshot.Length; i++)
        {
            var key = snapshot[i].Key;
            if (key.Lod == lod && key.X == tx && key.Y == ty)
                return true;
        }

        return false;
    }

    private IEnumerable<int> CachedFallbackLods(KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot, int displayLod)
    {
        var lods = new HashSet<int>();
        for (var i = 0; i < snapshot.Length; i++)
        {
            var key = snapshot[i].Key;
            if (key.Lod != displayLod)
                lods.Add(key.Lod);
        }

        var ordered = new List<int>(lods);
        ordered.Sort((a, b) =>
        {
            var da = Math.Abs(a - displayLod);
            var db = Math.Abs(b - displayLod);
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

    private bool CurrentLodHasVisibleHoles(
        PixelRegion? visibleViewport,
        KeyValuePair<(int X, int Y, int Lod), WriteableBitmap>[] snapshot,
        HashSet<(int X, int Y)> pendingCurrentLod,
        int displayLod)
    {
        if (snapshot.Length == 0 && pendingCurrentLod.Count == 0) return false;
        var stride = CmpTileSize * (1 << displayLod);
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
            if (key.Lod == displayLod) have.Add((key.X, key.Y));
        }

        for (var ty = firstTY; ty <= lastTY; ty++)
            for (var tx = firstTX; tx <= lastTX; tx++)
                if (!have.Contains((tx, ty)) || pendingCurrentLod.Contains((tx, ty)))
                    return true;
        return false;
    }

    public void Invalidate(PixelRegion? region = null)
        => Invalidate(region, null, null);

    public void Invalidate(
        PixelRegion? region,
        IReadOnlyList<DrawingLayer>? layers,
        int? layerIndex,
        bool metadataOnly = false,
        PixelRegion? viewportClip = null)
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
                Projection.InvalidateStrokeBelow(null);
            }
            else
            {
                var tileRegion = region.Value;
                var cacheRegion = region.Value;
                if (viewportClip is { IsEmpty: false } vp)
                {
                    tileRegion = tileRegion.Intersect(vp);
                    if (metadataOnly)
                        cacheRegion = tileRegion;
                }

                if (!tileRegion.IsEmpty)
                {
                    if (!_fullDirty)
                        _dirtyRegion = _dirtyRegion is { } existing ? existing.Union(tileRegion) : tileRegion;
                    // Drop stale fallback LODs only — keep the active LOD bitmap
                    // visible until Composite overwrites it (avoids white flash).
                    DropCachedTilesOverlapping(tileRegion, exceptLod: _currentLod);
                    QueueDirtyTilesForRegionAtLod(tileRegion, _currentLod);
                }

                InvalidateGroupCaches(cacheRegion, layers, layerIndex);
                return;
            }

            InvalidateGroupCaches(region, layers, layerIndex);
        }
    }

    public bool Composite(IReadOnlyList<DrawingLayer> layers, int width, int height, uint paperColor = 0, PixelRegion? viewport = null, double zoom = 1.0, int? forceLod = null)
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
                return CompositeCore(layers, width, height, paperColor, viewport, zoom, forceLod);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _compositeActive);
        }
    }

    private unsafe bool CompositeCore(IReadOnlyList<DrawingLayer> layers, int width, int height, uint paperColor, PixelRegion? viewport, double zoom, int? forceLod)
    {
        var started = Stopwatch.GetTimestamp();
        SetSize(width, height);
        Projection.LiveMergeGroups = _strokeSuspendDepth > 0;
        try
        {

        // Skip paper layers — they're handled by ClearTile with paperColor.
        var rootLayers = LayerStackComposition.SelectLayersForComposite(layers);

        var prevLod = _currentLod;
        var lod = forceLod ?? SelectLod(width, height, zoom);
        var lodChanged = lod != prevLod;
        _currentLod = lod;
        var scale = 1 << lod;
        var stride = CmpTileSize * scale;
        var viewportClip = viewport?.ClipTo(width, height);

        if (lodChanged)
        {
            if (viewportClip is { IsEmpty: false } vp)
            {
                // Remove wrong-LOD fallbacks; refresh active LOD only where edits
                // landed while another LOD was displayed.
                DropCachedTilesOverlapping(vp, exceptLod: lod);
                var dirtyInView = (_dirtyRegion ?? PixelRegion.Empty).ClipTo(width, height).Intersect(vp);
                if (!dirtyInView.IsEmpty)
                {
                    DropCachedTilesOverlapping(dirtyInView, onlyLod: lod);
                    QueueDirtyTilesForRegionAtLod(dirtyInView, lod);
                }
            }

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

            // Finer-LOD tiles stay in the cache as fallbacks while coarser tiles
            // are built — either via downscale sync (below) or layer recomposite.
        }

        var wasFullDirty = _fullDirty;
        var dirtyClip = (wasFullDirty ? new PixelRegion(0, 0, width, height) : _dirtyRegion ?? PixelRegion.Empty).ClipTo(width, height);
        var queueDirtyClip = dirtyClip;
        if (wasFullDirty && viewportClip is { IsEmpty: false } bootstrapViewport)
            queueDirtyClip = bootstrapViewport;

        // Fast path: nothing dirty and viewport not provided — nothing to do.
        if (queueDirtyClip.IsEmpty && viewportClip is null && _pendingDirtyTiles.Count == 0)
        {
            _fullDirty = false;
            _dirtyRegion = null;
            RenderTelemetry.RecordComposite(ElapsedMs(started), 0, 0, lod, _pendingDirtyTiles.Count);
            return false;
        }

        var tileKeys = new System.Collections.Generic.List<(int tx, int ty)>();
        var missingTileKeys = new System.Collections.Generic.List<(int tx, int ty)>();

        // 1. Dirty tiles.
        if (!queueDirtyClip.IsEmpty)
        {
            var firstDirtyTX = FloorDiv(queueDirtyClip.X, stride);
            var firstDirtyTY = FloorDiv(queueDirtyClip.Y, stride);
            var lastDirtyTX = FloorDiv(queueDirtyClip.Right - 1, stride);
            var lastDirtyTY = FloorDiv(queueDirtyClip.Bottom - 1, stride);

            for (var ty = firstDirtyTY; ty <= lastDirtyTY; ty++)
                for (var tx = firstDirtyTX; tx <= lastDirtyTX; tx++)
                {
                    var tileRect = new PixelRegion(tx * stride, ty * stride, stride, stride).Intersect(queueDirtyClip);
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

        // Krita KisSyncLodCacheStrokeStrategy: when zooming out, downscale the
        // existing finer-LOD display cache instead of recompositing the stack.
        var canBootstrapFromFiner = lodChanged && lod > prevLod && !wasFullDirty && queueDirtyClip.IsEmpty;
        if (canBootstrapFromFiner)
        {
            BootstrapLodFromFinerCache(missingTileKeys, prevLod, lod);
            for (var i = tileKeys.Count - 1; i >= 0; i--)
            {
                var key = tileKeys[i];
                if (TryBootstrapTileFromFinerLod(key.tx, key.ty, lod, prevLod))
                {
                    tileKeys.RemoveAt(i);
                    _pendingDirtyTiles.Remove((key.tx, key.ty, lod));
                }
            }
        }

        // Cap both dirty and missing tiles. Dirty tiles are persistent in
        // _pendingDirtyTiles; missing tiles are naturally rediscovered next frame.
        // Repopulate every missing viewport tile in one pass when the visible
        // region is known — Krita uploads the full dirty rect, not N tiles/frame.
        var maxMissing = lodChanged || wasFullDirty || viewportClip is { IsEmpty: false }
            ? int.MaxValue
            : MaxMissingTilesPerFrame;
        var deferredMissingTiles = _pendingDirtyTiles.Count > 0;
        // Krita-style: when a viewport clip is known, composite every visible
        // tile in one pass — per-frame caps leave permanent checkerboard grids.
        var dirtyTileBudget = _metadataOnlyPass || _strokeSuspendDepth > 0 || lodChanged || wasFullDirty
                              || viewportClip is { IsEmpty: false }
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
        var renderList = LayerProjectionPlane.BuildSiblingStack(rootLayers);

        // Ensure all tiles exist first (sequential — dictionary access).
        foreach (var (tx, ty) in tileKeys) EnsureTile(tx, ty, lod);
        foreach (var (tx, ty) in missingTileKeys) EnsureTile(tx, ty, lod);

        // Missing tiles: composite the FULL tile bounds — the WriteableBitmap has
        // uninitialized memory outside whatever region we clear, and DrawTiles draws
        // the full bitmap. Partial compositing would leave garbage pixels visible.
        // Dedupe — on the first frame after invalidate the same viewport tile
        // appears in both lists, doubling per-tile work for no reason.
        var allTileSet = new HashSet<(int tx, int ty)>(tileKeys);
        foreach (var k in missingTileKeys) allTileSet.Add(k);
        var allTiles = new (int tx, int ty)[allTileSet.Count];
        {
            var i = 0;
            foreach (var k in allTileSet) allTiles[i++] = k;
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

        if (strokeSplit >= 0)
        {
            // Dedupe — tileKeys + missingTileKeys can overlap (e.g. on the
            // first frame after invalidate, the same viewport tile is in
            // both lists), and warming the same rect twice is wasted work.
            var warmSet = new HashSet<(int tx, int ty)>(tileKeys);
            foreach (var key in missingTileKeys) warmSet.Add(key);
            var warmList = new (int tx, int ty, PixelRegion rect)[warmSet.Count];
            {
                var i = 0;
                foreach (var (tx, ty) in warmSet)
                {
                    var tileRect = new PixelRegion(tx * stride, ty * stride, stride, stride).ClipTo(width, height);
                    if (tileRect.IsEmpty) continue;
                    warmList[i++] = (tx, ty, tileRect);
                }
                if (i < warmList.Length) Array.Resize(ref warmList, i);
            }

            // WarmStrokeBelowForTile only touches disjoint regions of
            // Projection.StrokeBelow.Buffer (TiledPixelBuffer has internal
            // locks) and ignores TakeDirty's return value, so parallel
            // warming of disjoint tile rects is safe.
            if (warmList.Length >= 4 && Environment.ProcessorCount > 1)
            {
                Parallel.For(0, warmList.Length, idx =>
                {
                    var (_, _, rect) = warmList[idx];
                    WarmStrokeBelowForTile(renderList, strokeSplit, rect, width, height);
                });
            }
            else
            {
                for (var i = 0; i < warmList.Length; i++)
                {
                    var (_, _, rect) = warmList[i];
                    WarmStrokeBelowForTile(renderList, strokeSplit, rect, width, height);
                }
            }
        }

        var strokeSplitLocal = strokeSplit;

        void CompositeOne(int idx)
        {
            var (tx, ty) = allTiles[idx];
            var tileRect = new PixelRegion(tx * strideLocal, ty * strideLocal, strideLocal, strideLocal).ClipTo(widthLocal, heightLocal);
            if (tileRect.IsEmpty) tileRect = new PixelRegion(tx * strideLocal, ty * strideLocal, 1, 1);
            CompositeTileCpu(compTilesLocal[(tx, ty, lodLocal)], tileRect, renderListLocal, tx * strideLocal, ty * strideLocal, lodLocal, paperLocal, strokeSplitLocal);
        }

        // Group projection caches now have per-cache locks
        // (LayerProjectionPlane.GroupProjectionCache.SyncRoot), so parallel
        // workers compositing different tiles only contend when they touch the
        // same group cache. Paint layers write to their own per-tile bitmap
        // (no inter-tile sharing). Stroke-suspend warmth path mutates
        // Projection.StrokeBelow before the parallel loop (above), so it's
        // read-only by the time we get here.
        if (allTiles.Length >= 4 && Environment.ProcessorCount > 1)
            Parallel.For(0, allTiles.Length, CompositeOne);
        else
            for (var i = 0; i < allTiles.Length; i++) CompositeOne(i);

        // HashSet is not thread-safe — clear pending flags on the composite thread only.
        for (var i = 0; i < allTiles.Length; i++)
        {
            var (tx, ty) = allTiles[i];
            _tilesPendingComposite.TryRemove((tx, ty, lodLocal), out _);
        }

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
        finally
        {
            Projection.LiveMergeGroups = false;
        }
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

    private void QueueDirtyTilesForRegionAllLods(PixelRegion region)
    {
        if (region.IsEmpty) return;
        var clipped = _width > 0 && _height > 0
            ? region.ClipTo(_width, _height)
            : region;
        if (clipped.IsEmpty) return;
        for (var lod = 0; lod <= MaxCompositeLod; lod++)
            QueueDirtyTilesForRegionAtLod(clipped, lod);
    }

    private void QueueDirtyTilesForRegionAtLod(PixelRegion clipped, int lod)
    {
        var stride = CmpTileSize * (1 << lod);
        var firstTX = FloorDiv(clipped.X, stride);
        var firstTY = FloorDiv(clipped.Y, stride);
        var lastTX = FloorDiv(clipped.Right - 1, stride);
        var lastTY = FloorDiv(clipped.Bottom - 1, stride);
        for (var ty = firstTY; ty <= lastTY; ty++)
            for (var tx = firstTX; tx <= lastTX; tx++)
                _pendingDirtyTiles.Add((tx, ty, lod));
    }

    public int SelectLod(int width, int height, double zoom)
    {
        _ = width;
        _ = height;
        var candidate = zoom >= 1.0
            ? 0
            : Math.Clamp((int)Math.Floor(Math.Log2(1.0 / zoom)), 0, MaxCompositeLod);

        // Krita: floor(log2(1/zoom)). Zooming out switches eagerly; zooming in
        // waits until the viewport clearly crosses the next threshold.
        if (candidate > _currentLod)
            return candidate;

        if (candidate < _currentLod)
        {
            var threshold = 1.0 / (1 << _currentLod);
            var margin = _currentLod >= 2 ? 0.08 : 0.06;
            if (zoom > threshold + margin)
                return candidate;
        }

        return _currentLod;
    }

    private bool SourceTilesReadyForBootstrap(int docLeft, int docTop, int docW, int docH, int sourceLod)
    {
        var sourceStride = CmpTileSize * (1 << sourceLod);
        var srcFirstTX = FloorDiv(docLeft, sourceStride);
        var srcFirstTY = FloorDiv(docTop, sourceStride);
        var srcLastTX = FloorDiv(docLeft + docW - 1, sourceStride);
        var srcLastTY = FloorDiv(docTop + docH - 1, sourceStride);

        for (var sty = srcFirstTY; sty <= srcLastTY; sty++)
        {
            for (var stx = srcFirstTX; stx <= srcLastTX; stx++)
            {
                var key = (stx, sty, sourceLod);
                if (!_compTiles.ContainsKey(key) || _tilesPendingComposite.ContainsKey(key))
                    return false;
            }
        }

        return true;
    }

    private bool TryReadCachedPixel(int docX, int docY, int sourceLod, out byte b, out byte g, out byte r, out byte a)
    {
        if ((uint)docX >= (uint)_width || (uint)docY >= (uint)_height)
        {
            b = g = r = a = 0;
            return false;
        }

        var sourceStride = CmpTileSize * (1 << sourceLod);
        var scale = 1 << sourceLod;
        var stx = FloorDiv(docX, sourceStride);
        var sty = FloorDiv(docY, sourceStride);
        var key = (stx, sty, sourceLod);
        if (!_compTiles.TryGetValue(key, out var bmp) || _tilesPendingComposite.ContainsKey(key))
        {
            b = g = r = a = 0;
            return false;
        }

        var localDocX = docX - stx * sourceStride;
        var localDocY = docY - sty * sourceStride;
        var bx = localDocX / scale;
        var by = localDocY / scale;

        var tileDocW = Math.Min(sourceStride, _width - stx * sourceStride);
        var tileDocH = Math.Min(sourceStride, _height - sty * sourceStride);
        var bmpW = Math.Max(1, (tileDocW + scale - 1) / scale);
        var bmpH = Math.Max(1, (tileDocH + scale - 1) / scale);
        if (bx >= bmpW || by >= bmpH)
        {
            b = g = r = a = 0;
            return false;
        }

        unsafe
        {
            using var frame = bmp.Lock();
            var src = (byte*)frame.Address;
            var ptr = src + by * frame.RowBytes + bx * 4;
            b = ptr[0];
            g = ptr[1];
            r = ptr[2];
            a = ptr[3];
        }

        return true;
    }

    private unsafe bool TryBootstrapTileFromFinerLod(int tx, int ty, int targetLod, int sourceLod)
    {
        if (sourceLod >= targetLod)
            return false;

        var targetStride = CmpTileSize * (1 << targetLod);
        var docLeft = tx * targetStride;
        var docTop = ty * targetStride;
        var docW = Math.Min(targetStride, _width - docLeft);
        var docH = Math.Min(targetStride, _height - docTop);
        if (docW <= 0 || docH <= 0 || !SourceTilesReadyForBootstrap(docLeft, docTop, docW, docH, sourceLod))
            return false;

        var target = EnsureTile(tx, ty, targetLod);
        var docScale = 1 << targetLod;

        using var dstFrame = target.Lock();
        var dst = (byte*)dstFrame.Address;
        var dstStride = dstFrame.RowBytes;
        var outW = target.PixelSize.Width;
        var outH = target.PixelSize.Height;

        for (var oy = 0; oy < outH; oy++)
        {
            for (var ox = 0; ox < outW; ox++)
            {
                var docX0 = docLeft + ox * docScale;
                var docY0 = docTop + oy * docScale;
                var docX1 = Math.Min(docX0 + docScale, docLeft + docW);
                var docY1 = Math.Min(docY0 + docScale, docTop + docH);

                var pb = 0.0;
                var pg = 0.0;
                var pr = 0.0;
                var pa = 0.0;
                var count = 0;
                for (var dy = docY0; dy < docY1; dy++)
                {
                    for (var dx = docX0; dx < docX1; dx++)
                    {
                        if (!TryReadCachedPixel(dx, dy, sourceLod, out var cb, out var cg, out var cr, out var ca))
                            continue;
                        AccumulatePremultiplied(cb, cg, cr, ca, ref pb, ref pg, ref pr, ref pa);
                        count++;
                    }
                }

                var dstPx = dst + oy * dstStride + ox * 4;
                if (count == 0 || pa <= 0)
                {
                    dstPx[0] = dstPx[1] = dstPx[2] = dstPx[3] = 0;
                    continue;
                }

                WriteUnpremultipliedAverage(pb, pg, pr, pa, count, dstPx);
            }
        }

        _tilesPendingComposite.TryRemove((tx, ty, targetLod), out _);
        return true;
    }

    private void BootstrapLodFromFinerCache(List<(int tx, int ty)> missingTileKeys, int sourceLod, int targetLod)
    {
        for (var i = missingTileKeys.Count - 1; i >= 0; i--)
        {
            var (tx, ty) = missingTileKeys[i];
            if (TryBootstrapTileFromFinerLod(tx, ty, targetLod, sourceLod))
                missingTileKeys.RemoveAt(i);
        }
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
        ClearWriteableBitmap(fresh);
        // GetOrAdd here protects against a racy duplicate create. The wasted
        // bitmap is enqueued for UI-thread disposal.
        var t = _compTiles.GetOrAdd(key, fresh);
        if (!ReferenceEquals(t, fresh))
            _tilesPendingDispose.Enqueue(fresh);
        else
            _tilesPendingComposite.TryAdd(key, 0);
        return t;
    }

    private static unsafe void ClearWriteableBitmap(WriteableBitmap bmp)
    {
        using var frame = bmp.Lock();
        var ptr = (byte*)frame.Address;
        var bytes = frame.RowBytes * bmp.PixelSize.Height;
        new Span<byte>(ptr, bytes).Clear();
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

    private void DropCachedTilesOverlapping(PixelRegion region, int? onlyLod = null, int? exceptLod = null)
    {
        if (region.IsEmpty || _compTiles.IsEmpty) return;
        var clipped = region.ClipTo(_width, _height);
        if (clipped.IsEmpty) return;

        var toDrop = new List<(int X, int Y, int Lod)>();
        foreach (var key in _compTiles.Keys)
        {
            if (onlyLod.HasValue && key.Lod != onlyLod.Value) continue;
            if (exceptLod.HasValue && key.Lod == exceptLod.Value) continue;

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
                if (key.Lod == _currentLod)
                    _pendingDirtyTiles.Add(key);
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

    private static int FindRenderSplit(IReadOnlyList<ProjectionSiblingItem> renderList, DrawingLayer paintLayer)
    {
        for (var i = 0; i < renderList.Count; i++)
            if (ReferenceEquals(renderList[i].Layer, paintLayer))
                return i;
        return -1;
    }

    private unsafe void WarmStrokeBelowForTile(
        IReadOnlyList<ProjectionSiblingItem> renderList, int split, PixelRegion tileRect, int width, int height)
    {
        var strokeBelow = Projection.GetOrCreateStrokeBelow(width, height);

        // Always warm the full compositor tile. Partial warms (from TakeDirty
        // intersecting the stroke-invalidation bbox) left tile edges empty while
        // CompositeTileStrokeCpu composites the whole WriteableBitmap — checkerboard
        // holes in a stair-step tile pattern during live strokes.
        var dirty = tileRect;

        strokeBelow.Buffer.Clear(dirty);
        var tempLen = dirty.Width * dirty.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(tempLen);
        Array.Clear(temp, 0, tempLen);
        fixed (byte* tempPtr = temp)
        {
            CompositeRenderListRange(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height,
                renderList, 0, split, 1.0, dirty, dirty.X, dirty.Y);
        }
        strokeBelow.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
        // Compression is for memory-pressure relief, not correctness; skip on
        // the stroke hot path so warming a tile is just composite + copy.
        ArrayPool<byte>.Shared.Return(temp);
    }

    private unsafe void CompositeTileCpu(WriteableBitmap tile, PixelRegion tileRect,
        IReadOnlyList<ProjectionSiblingItem> renderList, int originX, int originY, int lod, uint paperColor = 0,
        int strokeSplit = -1)
    {
        if (lod > 0)
        {
            CompositeTileLodCpu(tile, tileRect, renderList, originX, originY, lod, paperColor);
            return;
        }

        if (strokeSplit >= 0 && Projection.StrokeBelow != null
            && (strokeSplit == 0 || Projection.StrokeBelow.Buffer.HasContentTiles(tileRect)))
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
        IReadOnlyList<ProjectionSiblingItem> renderList, int split, int originX, int originY, uint paperColor)
    {
        var tw = tile.PixelSize.Width;
        var th = tile.PixelSize.Height;
        using var frame = tile.Lock();
        var dst = (byte*)frame.Address;
        var dstStride = frame.RowBytes;
        ClearTile(dst, dstStride, tileRect, originX, originY, paperColor);
        LayerCompositorPixelOps.CompositeProjectionBuffer(dst, dstStride, Projection.StrokeBelow!.Buffer, "Normal", 1.0, tileRect, originX, originY);
        CompositeRenderListRange(dst, dstStride, tw, th, renderList, split, renderList.Count, 1.0, tileRect, originX, originY);
    }

    private unsafe void CompositeTileLodCpu(WriteableBitmap tile, PixelRegion tileRect,
        IReadOnlyList<ProjectionSiblingItem> renderList, int originX, int originY, int lod, uint paperColor)
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

        // GENERAL LOD PATH: point-sample each layer/group at LOD resolution
        // instead of compositing at full res then bilinear-downsampling.
        // Groups recurse into children; non-Normal layers fall back to
        // full-res composit + nearest-neighbor downsample per layer.
        // Clipped layers require the base-layer mask, which the LOD path
        // doesn't yet support — fall back to full-res composite for any
        // render list containing clipped items.
        var hasClipped = false;
        for (var i = 0; i < renderList.Count; i++)
        {
            if (renderList[i].IsClipped) { hasClipped = true; break; }
        }

        if (hasClipped)
        {
            // Full-res composite + bilinear downsample (original slow path).
            var fullBytes = tileRect.Width * tileRect.Height * 4;
            var temp = ArrayPool<byte>.Shared.Rent(fullBytes);
            try
            {
                fixed (byte* tp = temp)
                {
                    var count = tileRect.Width * tileRect.Height;
                    var clear = (uint*)tp;
                    for (var c = 0; c < count; c++) clear[c] = paperColor;

                    CompositeRenderList(tp, tileRect.Width * 4, tileRect.Width, tileRect.Height,
                        renderList, 1.0, tileRect, originX, originY);

                    using (var f = tile.Lock())
                    {
                        var fd = (byte*)f.Address;
                        var fds = f.RowBytes;
                        var scale = 1 << lod;
                        for (var y = 0; y < tile.PixelSize.Height; y++)
                        {
                            var sy = Math.Min(tileRect.Height - 1.0f, (y + 0.5f) * scale - 0.5f);
                            var sy0 = Math.Clamp((int)MathF.Floor(sy), 0, tileRect.Height - 1);
                            var sy1 = Math.Min(tileRect.Height - 1, sy0 + 1);
                            var fy = sy - sy0;
                            var srcRow0 = tp + sy0 * tileRect.Width * 4;
                            var srcRow1 = tp + sy1 * tileRect.Width * 4;
                            var dstRow = fd + y * fds;
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
                                BilinearPremultipliedBgra(
                                    p00[0], p00[1], p00[2], p00[3],
                                    p10[0], p10[1], p10[2], p10[3],
                                    p01[0], p01[1], p01[2], p01[3],
                                    p11[0], p11[1], p11[2], p11[3],
                                    fx, fy, dstPx);
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
            return;
        }

        using var frame = tile.Lock();
        var dst = (byte*)frame.Address;
        var dstStride = frame.RowBytes;
        var dstW = tile.PixelSize.Width;
        var dstH = tile.PixelSize.Height;

        for (var y = 0; y < dstH; y++)
        {
            var row = (uint*)(dst + y * dstStride);
            for (var x = 0; x < dstW; x++) row[x] = paperColor;
        }

        CompositeRenderListLod(dst, dstStride, dstW, dstH, renderList,
            1.0, tileRect, originX, originY, lod);
    }

    private unsafe void CompositeRenderListLod(
        byte* dst, int dstStride, int dstW, int dstH,
        IReadOnlyList<ProjectionSiblingItem> renderList,
        double opacityScale, PixelRegion clip,
        int originX, int originY, int lod)
    {
        if (opacityScale <= 0) return;

        var scale = 1 << lod;
        var halfStep = scale >> 1;
        const int ts = TiledPixelBuffer.TileSize;

        for (var i = 0; i < renderList.Count; i++)
        {
            var item = renderList[i];
            var layer = item.Layer;
            if (!layer.IsVisible) continue;

            if (layer.IsGroup)
            {
                if (layer.Children.Count == 0) continue;
                var groupOpacity = layer.Opacity * opacityScale;
                if (groupOpacity <= 0) continue;

                var childStack = LayerProjectionPlane.BuildSiblingStack(layer.Children);

                if (layer.BlendMode == "PassThrough")
                {
                    CompositeRenderListLod(dst, dstStride, dstW, dstH,
                        childStack, groupOpacity, clip, originX, originY, lod);
                }
                else
                {
                    var tempStride = dstW * 4;
                    var tempLen = dstH * tempStride;
                    var temp = ArrayPool<byte>.Shared.Rent(tempLen);
                    Array.Clear(temp, 0, tempLen);
                    fixed (byte* tempPtr = temp)
                    {
                        CompositeRenderListLod(tempPtr, tempStride, dstW, dstH,
                            childStack, 1.0, clip, originX, originY, lod);
                        BlendTempLod(dst, dstStride, tempPtr, tempStride,
                            dstW, dstH, layer.BlendMode, groupOpacity);
                    }
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
            else
            {
                var blendMode = layer.BlendMode;
                var isNormal = blendMode == "Normal";
                var needsFullRes = !isNormal || item.IsClipped
                    || layer.LayerColor.HasValue
                    || layer.ExpressionColor != ExpressionColorMode.Color;

                if (needsFullRes)
                {
                    CompositeSingleLayerLod(dst, dstStride, dstW, dstH,
                        layer, opacityScale, clip, originX, originY, lod);
                    continue;
                }

                var groupOpacity = layer.Opacity * opacityScale;
                if (groupOpacity <= 0) continue;

                var opacityByte = (uint)Math.Round(groupOpacity * 255);
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
                        var dstRow = dst + py * dstStride;
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
                            uint srcA = srcTile[tileOffset + 3];
                            if (srcA == 0) continue;
                            if (!fullOpacity)
                                srcA = (srcA * opacityByte + 127) / 255;
                            if (srcA == 0) continue;

                            uint sb = srcTile[tileOffset + 0];
                            uint sg = srcTile[tileOffset + 1];
                            uint sr = srcTile[tileOffset + 2];

                            var dO = px * 4;
                            if (srcA == 255 && dstRow[dO + 3] == 0)
                            {
                                dstRow[dO] = (byte)sb;
                                dstRow[dO + 1] = (byte)sg;
                                dstRow[dO + 2] = (byte)sr;
                                dstRow[dO + 3] = 255;
                            }
                            else
                            {
                                uint dB = dstRow[dO];
                                uint dG = dstRow[dO + 1];
                                uint dR = dstRow[dO + 2];
                                uint dA = dstRow[dO + 3];
                                uint invSrcA = 255 - srcA;
                                uint dstCont = (dA * invSrcA + 127) / 255;
                                uint outA = srcA + dstCont;
                                if (outA == 0) continue;
                                uint half = outA >> 1;
                                dstRow[dO] = (byte)((sb * srcA + dB * dstCont + half) / outA);
                                dstRow[dO + 1] = (byte)((sg * srcA + dG * dstCont + half) / outA);
                                dstRow[dO + 2] = (byte)((sr * srcA + dR * dstCont + half) / outA);
                                dstRow[dO + 3] = (byte)outA;
                            }
                        }
                    }
                }
                finally
                {
                    layer.Pixels.ExitPixelReadLock();
                }
            }
        }
    }

    /// <summary>
    /// Blend a LOD-resolution temp buffer onto dst. Both buffers are at the
    /// same (LOD) resolution — no document-coordinate scaling needed.
    /// </summary>
    private static unsafe void BlendTempLod(
        byte* dst, int dstStride,
        byte* src, int srcStride,
        int w, int h,
        string blendMode, double opacity)
    {
        if (opacity <= 0) return;
        var opacityByte = (uint)Math.Round(opacity * 255);
        var fullOp = opacityByte == 255;

        for (var y = 0; y < h; y++)
        {
            var srcRow = src + y * srcStride;
            var dstRow = dst + y * dstStride;
            for (var x = 0; x < w; x++)
            {
                var idx = x * 4;
                uint sa = srcRow[idx + 3];
                if (sa == 0) continue;
                if (!fullOp)
                    sa = (sa * opacityByte + 127) / 255;
                if (sa == 0) continue;

                if (blendMode == "Normal")
                {
                    if (sa == 255 && dstRow[idx + 3] == 0)
                    {
                        dstRow[idx]     = srcRow[idx];
                        dstRow[idx + 1] = srcRow[idx + 1];
                        dstRow[idx + 2] = srcRow[idx + 2];
                        dstRow[idx + 3] = 255;
                        continue;
                    }

                    uint dB = dstRow[idx];
                    uint dG = dstRow[idx + 1];
                    uint dR = dstRow[idx + 2];
                    uint dA = dstRow[idx + 3];
                    uint invSa = 255 - sa;
                    uint dstCont = (dA * invSa + 127) / 255;
                    uint outA = sa + dstCont;
                    if (outA == 0) continue;
                    uint half = outA >> 1;
                    dstRow[idx]     = (byte)((srcRow[idx]     * sa + dB * dstCont + half) / outA);
                    dstRow[idx + 1] = (byte)((srcRow[idx + 1] * sa + dG * dstCont + half) / outA);
                    dstRow[idx + 2] = (byte)((srcRow[idx + 2] * sa + dR * dstCont + half) / outA);
                    dstRow[idx + 3] = (byte)outA;
                }
                else
                {
                    var sb = srcRow[idx]     / 255.0;
                    var sg = srcRow[idx + 1] / 255.0;
                    var sr = srcRow[idx + 2] / 255.0;
                    var sA = sa / 255.0;
                    var dB = dstRow[idx]     / 255.0;
                    var dG = dstRow[idx + 1] / 255.0;
                    var dR = dstRow[idx + 2] / 255.0;
                    var dA = dstRow[idx + 3] / 255.0;

                    var (blendR, blendG, blendB) = LayerCompositorPixelOps.ApplyBlendMode(
                        sr, sg, sb, sA, dR, dG, dB, dA, blendMode);
                    LayerCompositorPixelOps.BlendPixel(
                        dstRow + idx, sr, sg, sb, sA, dR, dG, dB, dA,
                        blendR, blendG, blendB);
                }
            }
        }
    }

    private unsafe void CompositeSingleLayerLod(
        byte* dst, int dstStride, int dstW, int dstH,
        DrawingLayer layer, double opacityScale,
        PixelRegion clip, int originX, int originY, int lod)
    {
        var groupOpacity = layer.Opacity * opacityScale;
        if (groupOpacity <= 0) return;

        var scale = 1 << lod;
        var halfStep = scale >> 1;

        var fullBytes = clip.Width * clip.Height * 4;
        var fullTemp = ArrayPool<byte>.Shared.Rent(fullBytes);
        try
        {
            fixed (byte* fullPtr = fullTemp)
            {
                var count = clip.Width * clip.Height;
                var clear = (uint*)fullPtr;
                for (var c = 0; c < count; c++) clear[c] = 0u;

                LayerCompositorPixelOps.CompositeLayer(
                    fullPtr, clip.Width * 4, clip.Width, clip.Height,
                    layer, groupOpacity, clip, clip.X, clip.Y);

                for (var py = 0; py < dstH; py++)
                {
                    var sy = Math.Min(clip.Height - 1, py * scale + halfStep);
                    var srcRow = fullPtr + sy * clip.Width * 4;
                    var dstRow = dst + py * dstStride;

                    for (var px = 0; px < dstW; px++)
                    {
                        var sx = Math.Min(clip.Width - 1, px * scale + halfStep);
                        var srcIdx = sx * 4;
                        uint srcA = srcRow[srcIdx + 3];
                        if (srcA == 0) continue;

                        uint sb = srcRow[srcIdx];
                        uint sg = srcRow[srcIdx + 1];
                        uint sr = srcRow[srcIdx + 2];

                        var dO = px * 4;
                        uint dB = dstRow[dO];
                        uint dG = dstRow[dO + 1];
                        uint dR = dstRow[dO + 2];
                        uint dA = dstRow[dO + 3];
                        uint invSrcA = 255 - srcA;
                        uint dstCont = (dA * invSrcA + 127) / 255;
                        uint outA = srcA + dstCont;
                        if (outA == 0) continue;
                        uint half = outA >> 1;
                        dstRow[dO] = (byte)((sb * srcA + dB * dstCont + half) / outA);
                        dstRow[dO + 1] = (byte)((sg * srcA + dG * dstCont + half) / outA);
                        dstRow[dO + 2] = (byte)((sr * srcA + dR * dstCont + half) / outA);
                        dstRow[dO + 3] = (byte)outA;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fullTemp);
        }
    }

    private static bool CanLodFastPath(IReadOnlyList<ProjectionSiblingItem> renderList)
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
        IReadOnlyList<ProjectionSiblingItem> renderList, int originX, int originY, int lod, uint paperColor)
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
    private static void AccumulatePremultiplied(byte b, byte g, byte r, byte a,
        ref double pb, ref double pg, ref double pr, ref double pa)
    {
        var fa = a / 255.0;
        pb += b * fa;
        pg += g * fa;
        pr += r * fa;
        pa += fa;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateWeightedPremultiplied(byte b, byte g, byte r, byte a, double weight,
        ref double pb, ref double pg, ref double pr, ref double pa)
    {
        var fa = a / 255.0;
        var w = weight * fa;
        pb += b * w;
        pg += g * w;
        pr += r * w;
        pa += weight * fa;
    }

    private static unsafe void WriteUnpremultipliedAverage(double pb, double pg, double pr, double pa, int count, byte* dst)
    {
        var avgA = pa / count;
        if (avgA <= 0)
        {
            dst[0] = dst[1] = dst[2] = dst[3] = 0;
            return;
        }

        var outA = (byte)Math.Clamp((int)Math.Round(avgA * 255), 0, 255);
        if (outA == 0)
        {
            dst[0] = dst[1] = dst[2] = dst[3] = 0;
            return;
        }

        var invAvgA = 1.0 / avgA;
        dst[0] = (byte)Math.Clamp((int)Math.Round((pb / count) * invAvgA), 0, 255);
        dst[1] = (byte)Math.Clamp((int)Math.Round((pg / count) * invAvgA), 0, 255);
        dst[2] = (byte)Math.Clamp((int)Math.Round((pr / count) * invAvgA), 0, 255);
        dst[3] = outA;
    }

    private static unsafe void BilinearPremultipliedBgra(
        byte b00, byte g00, byte r00, byte a00,
        byte b10, byte g10, byte r10, byte a10,
        byte b01, byte g01, byte r01, byte a01,
        byte b11, byte g11, byte r11, byte a11,
        float fx, float fy, byte* dst)
    {
        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;
        double pb = 0, pg = 0, pr = 0, pa = 0;
        AccumulateWeightedPremultiplied(b00, g00, r00, a00, w00, ref pb, ref pg, ref pr, ref pa);
        AccumulateWeightedPremultiplied(b10, g10, r10, a10, w10, ref pb, ref pg, ref pr, ref pa);
        AccumulateWeightedPremultiplied(b01, g01, r01, a01, w01, ref pb, ref pg, ref pr, ref pa);
        AccumulateWeightedPremultiplied(b11, g11, r11, a11, w11, ref pb, ref pg, ref pr, ref pa);

        if (pa <= 0)
        {
            dst[0] = dst[1] = dst[2] = dst[3] = 0;
            return;
        }

        var invA = 1.0 / pa;
        var outA = (byte)Math.Clamp((int)Math.Round(pa * 255), 0, 255);
        if (outA == 0)
        {
            dst[0] = dst[1] = dst[2] = dst[3] = 0;
            return;
        }

        dst[0] = (byte)Math.Clamp((int)Math.Round(pb * invA), 0, 255);
        dst[1] = (byte)Math.Clamp((int)Math.Round(pg * invA), 0, 255);
        dst[2] = (byte)Math.Clamp((int)Math.Round(pr * invA), 0, 255);
        dst[3] = outA;
    }

    // ── CPU compositing ──────────────────────────────────────────────────────

    private unsafe void CompositeRenderList(byte* dst, int dstStride, int width, int height,
        IReadOnlyList<ProjectionSiblingItem> renderList, double opacityScale, PixelRegion clip, int originX, int originY)
    {
        Projection.CompositeSiblingStack(dst, dstStride, width, height, renderList, opacityScale, clip, originX, originY);
    }

    private unsafe void CompositeRenderListRange(byte* dst, int dstStride, int width, int height,
        IReadOnlyList<ProjectionSiblingItem> renderList, int startIndex, int endIndex,
        double opacityScale, PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0 || startIndex >= endIndex) return;

        for (var i = startIndex; i < endIndex; i++)
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
                    LayerCompositorPixelOps.CompositeClippedLayer(dst, dstStride, width, height, item.Layer, baseLayer, opacityScale, clip, originX, originY);
            }
            else if (item.Layer.IsGroup)
                Projection.CompositeGroupNode(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            else
                LayerCompositorPixelOps.CompositeLayer(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
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

    private void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex, bool fullGroupInvalidation = false)
    {
        Projection.InvalidateGroupCaches(region, layers, layerIndex, fullGroupInvalidation);
    }

    public unsafe SKBitmap AssembleSkBitmap(int outputWidth, int outputHeight, int lod = -1, uint paperColor = 0)
    {
        if (outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth));

        if (lod < 0)
            lod = _currentLod;

        var bitmap = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var dstPtr = (byte*)bitmap.GetPixels().ToPointer();
        var dstStride = bitmap.RowBytes;
        var tileStride = CmpTileSize * (1 << lod);

        if (paperColor != 0)
        {
            var pixels = (uint*)dstPtr;
            var count = outputWidth * outputHeight;
            for (var i = 0; i < count; i++)
                pixels[i] = paperColor;
        }
        else
        {
            new Span<byte>(dstPtr, bitmap.ByteCount).Clear();
        }

        lock (CompositeGate)
        {
            foreach (var ((tx, ty, tileLod), srcBmp) in _compTiles)
            {
                if (tileLod != lod)
                    continue;

                var dx = tx * tileStride;
                var dy = ty * tileStride;
                if (dx >= outputWidth || dy >= outputHeight)
                    continue;

                using var srcFrame = srcBmp.Lock();
                var src = (byte*)srcFrame.Address;
                var tw = Math.Min(srcBmp.PixelSize.Width, outputWidth - dx);
                var th = Math.Min(srcBmp.PixelSize.Height, outputHeight - dy);
                if (tw <= 0 || th <= 0)
                    continue;

                var rowBytes = tw * 4;
                for (var y = 0; y < th; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcFrame.RowBytes,
                        dstPtr + (dy + y) * dstStride + dx * 4,
                        rowBytes,
                        rowBytes);
                }
            }
        }

        return bitmap;
    }

    public unsafe byte[] CompositeToBgra(IReadOnlyList<DrawingLayer> layers, int width, int height, uint paperColor = 0)
    {
        lock (CompositeGate)
        {
            SetSize(width, height);
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
            var compositeLayers = LayerStackComposition.SelectLayersForComposite(layers);
            fixed (byte* dst = buf)
                Projection.CompositeSiblingList(dst, width * 4, width, height, compositeLayers, 1.0, clip, 0, 0);
            return buf;
        }
    }

    public bool TryReadDisplayPixel(int docX, int docY, out byte b, out byte g, out byte r, out byte a)
    {
        lock (CompositeGate)
            return TryReadCachedPixel(docX, docY, _currentLod, out b, out g, out r, out a);
    }

    public unsafe Color? SampleCompositePixel(IReadOnlyList<DrawingLayer> layers, int width, int height, int x, int y, uint paperColor = 0)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return null;

        lock (CompositeGate)
        {
            SetSize(width, height);
            var compositeLayers = LayerStackComposition.SelectLayersForComposite(layers);
            var row = new byte[width * 4];
            if (paperColor != 0)
            {
                fixed (byte* rowPtr = row)
                {
                    var fill = (uint*)rowPtr;
                    for (var i = 0; i < width; i++)
                        fill[i] = paperColor;
                }
            }

            var renderList = LayerProjectionPlane.BuildSiblingStack(compositeLayers);
            var clip = new PixelRegion(0, y, width, 1);
            fixed (byte* dst = row)
                Projection.CompositeSiblingStack(dst, width * 4, width, 1, renderList, 1.0, clip, 0, y);

            var offset = x * 4;
            var b = row[offset];
            var g = row[offset + 1];
            var r = row[offset + 2];
            var a = row[offset + 3];
            return a == 0 ? null : Color.FromArgb(a, r, g, b);
        }
    }

    private static int FloorDiv(int value, int divisor)
        => LayerCompositorPixelOps.FloorDiv(value, divisor);

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
                Projection.CompositeSiblingList(tempPtr, clip.Width * 4, clip.Width, clip.Height, group.Children, groupOpacity, clip, clip.X, clip.Y);
            else
                Projection.CompositeGroupNode(tempPtr, clip.Width * 4, clip.Width, clip.Height, group, opacityScale, clip, clip.X, clip.Y);

            LayerCompositorPixelOps.CompositeClippedBuffer(dst, dstStride, width, height, tempPtr, clip.Width * 4, baseLayer, group.BlendMode, clip, originX, originY);
        }
        ArrayPool<byte>.Shared.Return(temp);
    }


    private sealed class MergeHost(LayerCompositor owner) : ILayerMergeHost
    {
        public unsafe void CompositePaintLayer(
            byte* dst, int dstStride, int width, int height,
            DrawingLayer layer, double opacityScale, PixelRegion clip, int originX, int originY)
            => LayerCompositorPixelOps.CompositeLayer(dst, dstStride, width, height, layer, opacityScale, clip, originX, originY);

        public unsafe void CompositeProjectionBuffer(
            byte* dst, int dstStride, TiledPixelBuffer projection,
            string blendMode, double opacity, PixelRegion clip, int originX, int originY)
            => LayerCompositorPixelOps.CompositeProjectionBuffer(dst, dstStride, projection, blendMode, opacity, clip, originX, originY);

        public unsafe void CompositeClippedPaintLayer(
            byte* dst, int dstStride, int width, int height,
            DrawingLayer layer, DrawingLayer baseLayer, double opacityScale,
            PixelRegion clip, int originX, int originY)
            => LayerCompositorPixelOps.CompositeClippedLayer(dst, dstStride, width, height, layer, baseLayer, opacityScale, clip, originX, originY);

        public unsafe void CompositeClippedGroupIntoBuffer(
            byte* dst, int dstStride, int width, int height,
            DrawingLayer group, DrawingLayer baseLayer, double opacityScale,
            PixelRegion clip, int originX, int originY)
            => owner.CompositeClippedGroup(dst, dstStride, width, height, group, baseLayer, opacityScale, clip, originX, originY);
    }
}
