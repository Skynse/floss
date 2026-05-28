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
using Floss.App.Canvas.Engine;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Canvas.Compositing;

public sealed class LayerCompositor : IDisposable
{
    private static readonly RenderThreadPool _renderPool = RenderThreadPool.Create("FlossComposite");

    // Leave at least one core free for the UI thread so menus and pointer
    // interaction stay responsive while the compositor fills background tiles.
    private static readonly int _compositeParallelism = Math.Max(1, Environment.ProcessorCount - 1);
    private static readonly int _liveStrokeParallelism = Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2));

    private LayerProjectionPlane? _projection;
    private LayerProjectionPlane Projection =>
        _projection ??= new LayerProjectionPlane(new MergeHost(this));

    private const int MaxCompositeLod = 2;
    public const int DirtyTileBudget = 32;
    private const int MaxMissingTilesPerFrame = 96;
    private const int MaxCompositeCacheTiles = 8192;
    // Drawpile has zero budget cap during DP_RENDERER_CONTINUOUS (live stroke) —
    // all dirty tiles are enqueued and rendered by the background thread pool.
    // Capping to 2-8 tiles per frame here means TryBuildDisplayFrame never
    // publishes because one pending tile blocks the entire all-or-nothing frame,
    // producing the checkerboard/square-tile artifact.
    private static int LiveStrokeDirtyTileBudget => int.MaxValue;
    private static int LiveStrokeMissingTileBudget => int.MaxValue;

    public void Dispose()
    {
        ClearAllTiles();
        _projection?.Dispose();
        _projection = null;
    }

    private static void DispatchToPool(int count, Action<int> action)
    {
        if (count <= 0) return;
        using var remaining = new CountdownEvent(count);
        for (var i = 0; i < count; i++)
        {
            var idx = i;
            _renderPool.Enqueue(() => { action(idx); remaining.Signal(); });
        }
        remaining.Wait();
    }

    // Drawpile's compositor tile grid is 64x64 (DP_TILE_SIZE). Keep the display
    // compositor on the same granularity: smaller dirty units, predictable edge
    // tiles, and no giant 256/1024px recomposite blocks during strokes.
    private const int CmpTileSize = 64;

    private int _currentLod;
    // ConcurrentDictionary so DrawTiles (UI thread) can snapshot entries while
    // Composite (background thread) adds new tiles. Removal of disposed tiles
    // is funneled through _tilesPendingDispose -> UI thread to avoid disposing
    // an SKBitmap that's mid-draw on the UI thread.
    private readonly ConcurrentDictionary<(int X, int Y, int Lod), SKBitmap> _compTiles = new();
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
    // Tiles removed during a composite pass — disposed lazily on the UI thread
    // so DrawImage(bitmap) on the UI thread never observes a freed bitmap.
    private readonly ConcurrentQueue<SKBitmap> _tilesPendingDispose = new();

    private readonly record struct TileArea(int Left, int Top, int Right, int Bottom)
    {
        public bool IsEmpty => Right < Left || Bottom < Top;
    }

    // The UI must never draw directly from the mutable work cache. Composite
    // passes publish complete DisplayFrame snapshots; DrawTiles only renders the
    // last committed frame, so LOD changes and dirty-tile work cannot leak as
    // checkerboard holes or blocky mixed-resolution patches.
    private sealed class DisplayFrame : IDisposable
    {
        public sealed record DisplayTile(SKImage Image, SKData Data) : IDisposable
        {
            public void Dispose()
            {
                try { Image.Dispose(); }
                catch { /* best-effort */ }
                try { Data.Dispose(); }
                catch { /* best-effort */ }
            }
        }

        public DisplayFrame(int lod, int width, int height, PixelRegion coverage, TileArea area, DisplayTile?[] tiles)
        {
            Lod = lod;
            Width = width;
            Height = height;
            Coverage = coverage;
            Area = area;
            Tiles = tiles;
        }

        public int Lod { get; }
        public int Width { get; }
        public int Height { get; }
        public PixelRegion Coverage { get; }
        public TileArea Area { get; }
        public DisplayTile?[] Tiles { get; }

        public int Columns => Area.IsEmpty ? 0 : Area.Right - Area.Left + 1;

        public void Dispose()
        {
            foreach (var tile in Tiles)
                tile?.Dispose();
            Array.Clear(Tiles);
        }
    }

    private DisplayFrame? _currentFrame;
    private readonly record struct RetiredDisplayFrame(DisplayFrame Frame, int DrainDelay);
    private readonly ConcurrentQueue<RetiredDisplayFrame> _framesPendingDispose = new();
    // Serialises Composite() vs DrawTiles() — UI render acquires this briefly
    // to atomically observe the tile map. Background composite holds it for
    // the whole pass. Reentrancy avoided by always taking from the outside in.
    internal readonly object CompositeGate = new();
    // Set to true while another thread is mid-Composite. UI render reads this
    // to avoid scheduling overlapping passes.
    private int _compositeActive;
    public bool IsCompositeActive => Volatile.Read(ref _compositeActive) != 0;
    public bool HasAnyTiles => Volatile.Read(ref _currentFrame) != null || !_compTiles.IsEmpty;
    // Stroke-suspend mode: active while a brush stroke is in progress. Used to
    // uncap the dirty-tile budget and enable the below-active-layer composite
    // fast path. Reference-counted for overlapping queued strokes.
    private int _strokeSuspendDepth;
    private int _strokePaintLayerIndex = -1;
    public bool StrokeSuspendActive => _strokeSuspendDepth > 0;

    public void BeginStrokeSuspend(PixelRegion _, int layerIndex = -1)
    {
        lock (CompositeGate)
        {
            _strokeSuspendDepth++;
            _strokePaintLayerIndex = layerIndex;
            Projection.ResetStrokeBelow();
            Projection.ResetStrokeAbove();
        }
    }

    public void ExtendStrokeSuspend(PixelRegion region)
    {
        if (region.IsEmpty) return;
        // Active-layer dab dirties do not change the cached projection below
        // the stroke. Newly touched compositor tiles warm themselves on demand.
    }

    public void EndStrokeSuspend()
    {
        lock (CompositeGate)
        {
            if (_strokeSuspendDepth > 0) _strokeSuspendDepth--;
            if (_strokeSuspendDepth > 0) return;
            _strokePaintLayerIndex = -1;
            Projection.ResetStrokeBelow();
            Projection.ResetStrokeAbove();
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
            Projection.ResetStrokeAbove();
        }
    }
    public int LastDirtyTileCount { get; private set; }
    public int LastMissingTileCount { get; private set; }
    public int LastLod => _currentLod;

    /// <summary>
    /// True when the viewport contains tile positions that are either missing
    /// from the compositor cache, pending a dirty recomposite, or the current
    /// published DisplayFrame doesn't cover the viewport at all (pan/zoom
    /// revealed completely new area).
    /// </summary>
    public bool IsFrameMissingTiles(PixelRegion viewport)
    {
        var frame = Volatile.Read(ref _currentFrame);
        if (frame == null)
            return true;

        var coverage = viewport.ClipTo(_width, _height);
        if (coverage.IsEmpty)
            return false;

        // Current published frame doesn't cover this viewport at all —
        // pan/zoom revealed a completely new area. Force sync.
        var frameCoverage = frame.Coverage;
        if (frameCoverage.X > coverage.X || frameCoverage.Y > coverage.Y
            || frameCoverage.Right < coverage.Right || frameCoverage.Bottom < coverage.Bottom)
            return true;

        var area = TileAreaForRegion(coverage, frame.Lod);
        if (area.IsEmpty)
            return false;

        for (var ty = area.Top; ty <= area.Bottom; ty++)
        {
            for (var tx = area.Left; tx <= area.Right; tx++)
            {
                var key = (tx, ty, frame.Lod);
                if (_pendingDirtyTiles.Contains(key)
                    || _tilesPendingComposite.ContainsKey(key)
                    || !_compTiles.ContainsKey(key))
                    return true;
            }
        }
        return false;
    }
    public int PendingDirtyTileCount => _pendingDirtyTiles.Count;

    public void DrainDisposalQueue()
    {
        // Called from UI thread to release bitmaps freed during background composite.
        var delayedCount = _framesPendingDispose.Count;
        for (var i = 0; i < delayedCount && _framesPendingDispose.TryDequeue(out var retired); i++)
        {
            if (retired.DrainDelay > 0)
            {
                _framesPendingDispose.Enqueue(retired with { DrainDelay = retired.DrainDelay - 1 });
                continue;
            }

            try { retired.Frame.Dispose(); }
            catch { /* best-effort */ }
        }

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
        var oldFrame = Interlocked.Exchange(ref _currentFrame, null);
        if (oldFrame != null) RetireDisplayFrame(oldFrame);

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
                int tw = srcBmp.Width;
                int th = srcBmp.Height;
                int dx = tx * stride;
                int dy = ty * stride;

                if (tw <= 0 || th <= 0) { tw = 1; th = 1; }

                var src = (byte*)srcBmp.GetPixels().ToPointer();
                var rowBytes = srcBmp.RowBytes;

                for (int y = 0; y < th; y++)
                    Buffer.MemoryCopy(src + y * rowBytes, dst + (dy + y) * dstFrame.RowBytes + dx * 4, tw * 4, tw * 4);
            }
            return result;
        }
    }

    public void DrawTiles(DrawingContext context, Rect target, PixelRegion? visibleViewport = null)
    {
        var frame = Volatile.Read(ref _currentFrame);
        if (frame == null) return;

        var drawArea = visibleViewport.HasValue
            ? TileAreaForRegion(visibleViewport.Value.ClipTo(frame.Width, frame.Height), frame.Lod)
            : frame.Area;
        drawArea = IntersectTileAreas(drawArea, frame.Area);
        if (drawArea.IsEmpty) return;

        for (var ty = drawArea.Top; ty <= drawArea.Bottom; ty++)
        {
            for (var tx = drawArea.Left; tx <= drawArea.Right; tx++)
            {
                var tile = frame.Tiles[FrameTileIndex(frame, tx, ty)];
                if (tile != null)
                    DrawFrameTile(context, tile.Image, tx, ty, frame.Lod, frame.Width, frame.Height, visibleViewport);
            }
        }
    }

    private void DrawFrameTile(
        DrawingContext context,
        SKImage image,
        int tx,
        int ty,
        int lod,
        int frameWidth,
        int frameHeight,
        PixelRegion? visibleViewport)
    {
        var stride = CmpTileSize * (1 << lod);
        var docTileW = Math.Min(stride, frameWidth - tx * stride);
        var docTileH = Math.Min(stride, frameHeight - ty * stride);
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

        var src = new SKRect(0, 0, image.Width, image.Height);
        var dest = new Rect(tileLeft, tileTop, docTileW, docTileH);
        context.Custom(new SkiaTileDrawOp(image, src, dest));
    }

    private void PublishDisplayFrameIfComplete(int lod, PixelRegion? viewportClip)
    {
        if (!TryBuildDisplayFrame(lod, viewportClip, out var frame) || frame == null)
            return;

        var old = Interlocked.Exchange(ref _currentFrame, frame);
        if (old != null) RetireDisplayFrame(old);
    }

    private void RetireDisplayFrame(DisplayFrame frame)
    {
        // Avalonia stores custom draw operations for the render thread. Keep
        // replaced frames alive for several UI disposal cycles so a previously
        // recorded operation cannot render with a disposed native SKImage.
        _framesPendingDispose.Enqueue(new RetiredDisplayFrame(frame, 8));
    }

    private bool TryBuildDisplayFrame(int lod, PixelRegion? viewportClip, out DisplayFrame? frame)
    {
        frame = null;
        if (_width <= 0 || _height <= 0) return false;

        var coverage = (viewportClip?.ClipTo(_width, _height) ?? new PixelRegion(0, 0, _width, _height));
        if (coverage.IsEmpty) return false;

        var area = TileAreaForRegion(coverage, lod);
        if (area.IsEmpty) return false;
        var columns = area.Right - area.Left + 1;
        var rows = area.Bottom - area.Top + 1;
        var images = new DisplayFrame.DisplayTile?[columns * rows];

        try
        {
            for (var ty = area.Top; ty <= area.Bottom; ty++)
            {
                for (var tx = area.Left; tx <= area.Right; tx++)
                {
                    var key = (tx, ty, lod);
                    if (_pendingDirtyTiles.Contains(key)
                        || _tilesPendingComposite.ContainsKey(key)
                        || !_compTiles.TryGetValue(key, out var bmp))
                        return false;

                    var image = CreateOwnedImageCopy(bmp);
                    if (image == null)
                        return false;
                    images[(ty - area.Top) * columns + (tx - area.Left)] = image;
                }
            }

            frame = new DisplayFrame(lod, _width, _height, coverage, area, images);
            images = null!;
            return true;
        }
        finally
        {
            if (images != null)
            {
                foreach (var image in images)
                    image?.Dispose();
            }
        }
    }

    private static unsafe DisplayFrame.DisplayTile? CreateOwnedImageCopy(SKBitmap bitmap)
    {
        var data = SKData.CreateCopy(bitmap.GetPixels(), bitmap.ByteCount);
        if (data == null) return null;
        var image = SKImage.FromPixels(bitmap.Info, data, bitmap.RowBytes);
        if (image != null) return new DisplayFrame.DisplayTile(image, data);
        data.Dispose();
        return null;
    }

    private static int FrameTileIndex(DisplayFrame frame, int tx, int ty)
        => (ty - frame.Area.Top) * frame.Columns + (tx - frame.Area.Left);

    private static TileArea IntersectTileAreas(TileArea a, TileArea b)
        => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

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

            if (layerIndex is >= 0 && _strokeSuspendDepth > 0 && _strokePaintLayerIndex < 0)
                _strokePaintLayerIndex = layerIndex.Value;
            var changedStrokeLayer = _strokeSuspendDepth > 0
                                     && layerIndex.HasValue
                                     && layerIndex.Value == _strokePaintLayerIndex;

            if (region is null || region.Value.IsEmpty)
            {
                _fullDirty = true;
                _dirtyRegion = null;
                if (viewportClip is { IsEmpty: false } vp && !_compTiles.IsEmpty)
                    DropCachedTilesOutside(vp);
                else
                    ClearAllTiles();
                if (!changedStrokeLayer)
                    Projection.InvalidateStrokeBelow(null);
            }
            else
            {
                var fullRegion = region.Value;
                var tileRegion = fullRegion;
                if (viewportClip is { IsEmpty: false } vp)
                {
                    tileRegion = fullRegion.Intersect(vp);
                }

                // Always drop cached tiles for the FULL dirty region — tiles at
                // the old layer position may be outside the current viewport but
                // will ghost if the user later pans or zooms out to reveal them.
                if (!fullRegion.IsEmpty)
                    DropCachedTilesOverlapping(fullRegion, exceptLod: _currentLod);

                if (!tileRegion.IsEmpty)
                {
                    if (!_fullDirty)
                        _dirtyRegion = _dirtyRegion is { } existing ? existing.Union(tileRegion) : tileRegion;
                    QueueDirtyTilesForRegionAtLod(tileRegion, _currentLod);
                }

                // Group caches need the full region too — a child moving inside
                // a group shifts the group projection across the full area.
                InvalidateGroupCaches(fullRegion, layers, layerIndex, invalidateStrokeBelow: !changedStrokeLayer, invalidateStrokeAbove: !changedStrokeLayer);
                return;
            }

            InvalidateGroupCaches(region, layers, layerIndex, invalidateStrokeBelow: !changedStrokeLayer, invalidateStrokeAbove: !changedStrokeLayer);
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
                // Keep older LOD tiles as visual fallbacks until the target LOD
                // is ready. Dropping them here makes async zoom transitions show
                // transparent holes for a frame or two.
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
                    var area = TileAreaForRegion(region, lod);
                    for (var ty = area.Top; ty <= area.Bottom; ty++)
                        for (var tx = area.Left; tx <= area.Right; tx++)
                            _pendingDirtyTiles.Add((tx, ty, lod));
                }
            }

            // Finer-LOD tiles stay in the cache as fallbacks while coarser tiles
            // are built — either via downscale sync (below) or layer recomposite.
        }

        var wasFullDirty = _fullDirty;
        var dirtyClip = (wasFullDirty ? new PixelRegion(0, 0, width, height) : _dirtyRegion ?? PixelRegion.Empty).ClipTo(width, height);

        // Queue ALL dirty tiles regardless of viewport. Drawpile's two-tier
        // priority queue avoids pan ghosting: in-viewport tiles render at HIGH
        // priority, outside-viewport tiles render at LOW priority during idle
        // background passes so they're already cached when the user pans there.
        // The viewport only affects which tiles get budget allocation first
        // (via distance-sort in SelectPendingDirtyTiles), not which tiles get
        // queued at all.
        var queueDirtyClip = dirtyClip;

        // Fast path: nothing dirty and viewport not provided — nothing to do.
        if (queueDirtyClip.IsEmpty && viewportClip is null && _pendingDirtyTiles.Count == 0)
        {
            _fullDirty = false;
            _dirtyRegion = null;
            PublishDisplayFrameIfComplete(lod, viewportClip);
            RenderTelemetry.RecordComposite(ElapsedMs(started), 0, 0, lod, _pendingDirtyTiles.Count);
            return false;
        }

        var tileKeys = new System.Collections.Generic.List<(int tx, int ty)>();
        var missingTileKeys = new System.Collections.Generic.List<(int tx, int ty)>();

        // 1. Dirty tiles.
        if (!queueDirtyClip.IsEmpty)
        {
            var dirtyArea = TileAreaForRegion(queueDirtyClip, lod);

            for (var ty = dirtyArea.Top; ty <= dirtyArea.Bottom; ty++)
                for (var tx = dirtyArea.Left; tx <= dirtyArea.Right; tx++)
                {
                    var tileRect = TileRect(tx, ty, lod, width, height).Intersect(queueDirtyClip);
                    if (!tileRect.IsEmpty)
                        _pendingDirtyTiles.Add((tx, ty, lod));
                }
        }

        // 2. Missing tiles in viewport (pan / zoom reveals new area).
        if (viewportClip is { } visibleViewport && !visibleViewport.IsEmpty)
        {
            var visibleArea = TileAreaForRegion(visibleViewport, lod);

            for (var ty = visibleArea.Top; ty <= visibleArea.Bottom; ty++)
                for (var tx = visibleArea.Left; tx <= visibleArea.Right; tx++)
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
        var liveStrokePass = _strokeSuspendDepth > 0;
        var maxMissing = liveStrokePass
            ? LiveStrokeMissingTileBudget
            : lodChanged || wasFullDirty || viewportClip is { IsEmpty: false }
                ? int.MaxValue
                : MaxMissingTilesPerFrame;
        var deferredMissingTiles = _pendingDirtyTiles.Count > 0;
        // Krita-style: when a viewport clip is known, composite every visible
        // tile in one pass — per-frame caps leave permanent checkerboard grids.
        var dirtyTileBudget = liveStrokePass
            ? LiveStrokeDirtyTileBudget
            : _metadataOnlyPass || lodChanged || wasFullDirty || viewportClip is { IsEmpty: false }
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
            PublishDisplayFrameIfComplete(lod, viewportClip);
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
                    var tileRect = TileRect(tx, ty, lod, width, height);
                    if (tileRect.IsEmpty) continue;
                    warmList[i++] = (tx, ty, tileRect);
                }
                if (i < warmList.Length) Array.Resize(ref warmList, i);
            }

            // WarmStrokeCachesForTile only touches disjoint regions of
            // StrokeBelow/StrokeAbove buffers (TiledPixelBuffer has internal
            // locks) and ignores TakeDirty's return value, so parallel
            // warming of disjoint tile rects is safe.
            if (liveStrokePass && warmList.Length >= 2 && Environment.ProcessorCount > 1)
            {
                var remaining = new CountdownEvent(warmList.Length);
                for (var wi = 0; wi < warmList.Length; wi++)
                {
                    var idx = wi;
                    _renderPool.Enqueue(() =>
                    {
                        var (_, _, rect) = warmList[idx];
                        WarmStrokeCachesForTile(renderList, strokeSplit, rect, width, height);
                        remaining.Signal();
                    });
                }
                remaining.Wait();
                remaining.Dispose();
            }
            else if (warmList.Length >= 4 && Environment.ProcessorCount > 1)
            {
                var remaining = new CountdownEvent(warmList.Length);
                for (var wi = 0; wi < warmList.Length; wi++)
                {
                    var idx = wi;
                    _renderPool.Enqueue(() =>
                    {
                        var (_, _, rect) = warmList[idx];
                        WarmStrokeCachesForTile(renderList, strokeSplit, rect, width, height);
                        remaining.Signal();
                    });
                }
                remaining.Wait();
                remaining.Dispose();
            }
            else
            {
                for (var i = 0; i < warmList.Length; i++)
                {
                    var (_, _, rect) = warmList[i];
                    WarmStrokeCachesForTile(renderList, strokeSplit, rect, width, height);
                }
            }
        }

        var strokeSplitLocal = strokeSplit;

        void CompositeOne(int idx)
        {
            var (tx, ty) = allTiles[idx];
            var key = (tx, ty, lodLocal);
            var tileRect = TileRect(tx, ty, lodLocal, widthLocal, heightLocal);
            if (tileRect.IsEmpty) tileRect = new PixelRegion(tx * strideLocal, ty * strideLocal, 1, 1);

            if (_tilesPendingComposite.ContainsKey(key))
            {
                // New tile — render thread already skips it via _tilesPendingComposite,
                // so in-place write is safe.
                CompositeTileCpu(compTilesLocal[key], tileRect, renderListLocal, tx * strideLocal, ty * strideLocal, lodLocal, paperLocal, strokeSplitLocal);
            }
            else
            {
                // Dirty existing tile — render thread reads it concurrently.
                // Composite into a fresh bitmap and atomically swap to avoid
                // torn reads (row-by-row in-place write vs concurrent render read).
                var fresh = AllocTileBitmap(tx, ty, lodLocal, widthLocal, heightLocal);
                CompositeTileCpu(fresh, tileRect, renderListLocal, tx * strideLocal, ty * strideLocal, lodLocal, paperLocal, strokeSplitLocal);
                SKBitmap? old = null;
                compTilesLocal.AddOrUpdate(key, fresh, (_, existing) => { old = existing; return fresh; });
                if (old != null) _tilesPendingDispose.Enqueue(old);
            }
        }

        // Group projection caches now have per-cache locks
        // (LayerProjectionPlane.GroupProjectionCache.SyncRoot), so parallel
        // workers compositing different tiles only contend when they touch the
        // same group cache. Paint layers write to their own per-tile bitmap
        // (no inter-tile sharing). Stroke-suspend warmth path mutates
        // Projection.StrokeBelow before the parallel loop (above), so it's
        // read-only by the time we get here.
        if (liveStrokePass && allTiles.Length >= 2 && Environment.ProcessorCount > 1)
            DispatchToPool(allTiles.Length, CompositeOne);
        else if (allTiles.Length >= 4 && Environment.ProcessorCount > 1)
            DispatchToPool(allTiles.Length, CompositeOne);
        else
            for (var i = 0; i < allTiles.Length; i++) CompositeOne(i);

        // HashSet is not thread-safe — clear pending flags on the composite thread only.
        for (var i = 0; i < allTiles.Length; i++)
        {
            var (tx, ty) = allTiles[i];
            _tilesPendingComposite.TryRemove((tx, ty, lodLocal), out _);
        }

        PublishDisplayFrameIfComplete(lod, viewportClip);
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
        var area = TileAreaForRegion(clipped, lod);
        for (var ty = area.Top; ty <= area.Bottom; ty++)
            for (var tx = area.Left; tx <= area.Right; tx++)
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
        var area = TileAreaForRegion(new PixelRegion(docLeft, docTop, docW, docH), sourceLod);

        for (var sty = area.Top; sty <= area.Bottom; sty++)
        {
            for (var stx = area.Left; stx <= area.Right; stx++)
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
            var src = (byte*)bmp.GetPixels().ToPointer();
            var ptr = src + by * bmp.RowBytes + bx * 4;
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

        var srcStride = CmpTileSize * (1 << sourceLod);
        var srcScale = 1 << sourceLod;
        var docScale = 1 << targetLod;

        // Determine the grid of source tiles that cover this target tile.
        var stxFirst = FloorDiv(docLeft, srcStride);
        var styFirst = FloorDiv(docTop, srcStride);
        var stxLast = FloorDiv(docLeft + docW - 1, srcStride);
        var styLast = FloorDiv(docTop + docH - 1, srcStride);
        var srcCols = stxLast - stxFirst + 1;
        var srcRows = styLast - styFirst + 1;
        var srcTileCount = srcCols * srcRows;

        var target = EnsureTile(tx, ty, targetLod);

        // Snapshot pixel pointers for each source tile once rather than per output pixel.
        var ptrs = new byte*[srcTileCount];
        var rowBytesArr = new int[srcTileCount];
        var bmpWArr = new int[srcTileCount];
        var bmpHArr = new int[srcTileCount];

        for (var sty = styFirst; sty <= styLast; sty++)
        {
            for (var stx = stxFirst; stx <= stxLast; stx++)
            {
                var idx = (sty - styFirst) * srcCols + (stx - stxFirst);
                if (!_compTiles.TryGetValue((stx, sty, sourceLod), out var bmp)) continue;
                var tileDocW = Math.Min(srcStride, _width - stx * srcStride);
                var tileDocH = Math.Min(srcStride, _height - sty * srcStride);
                bmpWArr[idx] = Math.Max(1, (tileDocW + srcScale - 1) / srcScale);
                bmpHArr[idx] = Math.Max(1, (tileDocH + srcScale - 1) / srcScale);
                ptrs[idx] = (byte*)bmp.GetPixels().ToPointer();
                rowBytesArr[idx] = bmp.RowBytes;
            }
        }

        {
            var dst = (byte*)target.GetPixels().ToPointer();
            var dstStride = target.RowBytes;
            var outW = target.Width;
            var outH = target.Height;

            for (var oy = 0; oy < outH; oy++)
            {
                for (var ox = 0; ox < outW; ox++)
                {
                    var docX0 = docLeft + ox * docScale;
                    var docY0 = docTop + oy * docScale;
                    var docX1 = Math.Min(docX0 + docScale, docLeft + docW);
                    var docY1 = Math.Min(docY0 + docScale, docTop + docH);

                    double pb = 0, pg = 0, pr = 0, pa = 0;
                    var count = 0;

                    for (var dy = docY0; dy < docY1; dy++)
                    {
                        for (var dx = docX0; dx < docX1; dx++)
                        {
                            if ((uint)dx >= (uint)_width || (uint)dy >= (uint)_height) continue;
                            var stx = FloorDiv(dx, srcStride);
                            var sty = FloorDiv(dy, srcStride);
                            var idx = (sty - styFirst) * srcCols + (stx - stxFirst);
                            var srcPtr = ptrs[idx];
                            if (srcPtr == null) continue;
                            var bx = (dx - stx * srcStride) / srcScale;
                            var by = (dy - sty * srcStride) / srcScale;
                            if (bx >= bmpWArr[idx] || by >= bmpHArr[idx]) continue;
                            var srcPx = srcPtr + by * rowBytesArr[idx] + bx * 4;
                            AccumulatePremultiplied(srcPx[0], srcPx[1], srcPx[2], srcPx[3],
                                ref pb, ref pg, ref pr, ref pa);
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
        var area = TileAreaForRegion(region, lod);
        return area.IsEmpty ? 0 : (area.Right - area.Left + 1) * (area.Bottom - area.Top + 1);
    }

    private static TileArea TileAreaForRegion(PixelRegion region, int lod)
    {
        if (region.IsEmpty) return new TileArea(0, 0, -1, -1);
        var stride = CmpTileSize * (1 << Math.Clamp(lod, 0, 8));
        return new TileArea(
            FloorDiv(region.X, stride),
            FloorDiv(region.Y, stride),
            FloorDiv(region.Right - 1, stride),
            FloorDiv(region.Bottom - 1, stride));
    }

    private static PixelRegion TileRect(int tx, int ty, int lod, int width, int height)
    {
        var stride = CmpTileSize * (1 << lod);
        return new PixelRegion(tx * stride, ty * stride, stride, stride).ClipTo(width, height);
    }

    private static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static SKBitmap AllocTileBitmap(int tx, int ty, int lod, int width, int height)
    {
        var scale = 1 << lod;
        var docStride = CmpTileSize * scale;
        var docW = Math.Min(docStride, width - tx * docStride);
        var docH = Math.Min(docStride, height - ty * docStride);
        if (docW <= 0 || docH <= 0) docW = docH = 1;
        var tileW = Math.Max(1, (docW + scale - 1) / scale);
        var tileH = Math.Max(1, (docH + scale - 1) / scale);
        return new SKBitmap(new SKImageInfo(tileW, tileH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    }

    private SKBitmap EnsureTile(int tx, int ty, int lod)
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
        var fresh = new SKBitmap(new SKImageInfo(tileW, tileH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        fresh.Erase(SKColors.Transparent);
        // GetOrAdd here protects against a racy duplicate create. The wasted
        // bitmap is enqueued for UI-thread disposal.
        var t = _compTiles.GetOrAdd(key, fresh);
        if (!ReferenceEquals(t, fresh))
            _tilesPendingDispose.Enqueue(fresh);
        else
            _tilesPendingComposite.TryAdd(key, 0);
        return t;
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

            var tileRegion = TileRect(key.X, key.Y, key.Lod, _width, _height);
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

    private void DropCachedTilesOutside(PixelRegion keepRegion)
    {
        if (_compTiles.IsEmpty) return;
        var keep = keepRegion.ClipTo(_width, _height);
        if (keep.IsEmpty)
        {
            ClearAllTiles();
            return;
        }

        var toDrop = new List<(int X, int Y, int Lod)>();
        foreach (var key in _compTiles.Keys)
        {
            var tileRegion = TileRect(key.X, key.Y, key.Lod, _width, _height);
            if (tileRegion.Intersect(keep).IsEmpty)
                toDrop.Add(key);
        }

        foreach (var key in toDrop)
        {
            if (_compTiles.TryRemove(key, out var bmp))
                _tilesPendingDispose.Enqueue(bmp);
            _pendingDirtyTiles.Remove(key);
            _tilesPendingComposite.TryRemove(key, out _);
        }
    }

    private void TrimCompositeCache(PixelRegion? viewportClip)
    {
        if (_compTiles.Count <= MaxCompositeCacheTiles) return;

        var cx = viewportClip is { } vp ? vp.X + vp.Width / 2 : _width / 2;
        var cy = viewportClip is { } vp2 ? vp2.Y + vp2.Height / 2 : _height / 2;

        // Build set of viewport-visible tile keys — these must NOT be evicted
        // because they'd just get recomposited next frame (idle thrash loop).
        var viewportProtected = new HashSet<(int X, int Y, int Lod)>();
        if (viewportClip is { IsEmpty: false } vpRegion)
        {
            AddProtectedTilesForRegion(viewportProtected, vpRegion, _currentLod);
        }

        var frame = Volatile.Read(ref _currentFrame);
        if (frame != null)
        {
            for (var ty = frame.Area.Top; ty <= frame.Area.Bottom; ty++)
                for (var tx = frame.Area.Left; tx <= frame.Area.Right; tx++)
                    viewportProtected.Add((tx, ty, frame.Lod));
        }

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
            if (viewportProtected.Contains(key)) continue; // never evict viewport tiles
            if (_compTiles.TryRemove(key, out var bmp))
            {
                _tilesPendingDispose.Enqueue(bmp);
                _pendingDirtyTiles.Remove(key);
                if (viewportClip is null && key.Lod == _currentLod)
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

    private void AddProtectedTilesForRegion(HashSet<(int X, int Y, int Lod)> protectedTiles, PixelRegion region, int lod)
    {
        var area = TileAreaForRegion(region.ClipTo(_width, _height), lod);
        if (area.IsEmpty) return;
        for (var ty = area.Top; ty <= area.Bottom; ty++)
            for (var tx = area.Left; tx <= area.Right; tx++)
                protectedTiles.Add((tx, ty, lod));
    }

    private static int FindRenderSplit(IReadOnlyList<ProjectionSiblingItem> renderList, DrawingLayer paintLayer)
    {
        for (var i = 0; i < renderList.Count; i++)
            if (ReferenceEquals(renderList[i].Layer, paintLayer))
                return i;
        return -1;
    }

    private unsafe void WarmStrokeCachesForTile(
        IReadOnlyList<ProjectionSiblingItem> renderList, int split, PixelRegion tileRect, int width, int height)
    {
        // Always warm the full compositor tile. Partial warms (from TakeDirty
        // intersecting the stroke-invalidation bbox) left tile edges empty while
        // CompositeTileStrokeCpu composites the whole WriteableBitmap — checkerboard
        // holes in a stair-step tile pattern during live strokes.
        var dirty = tileRect;
        var tempLen = dirty.Width * dirty.Height * 4;

        if (split > 0)
        {
            var strokeBelow = Projection.GetOrCreateStrokeBelow(width, height);
            strokeBelow.Buffer.Clear(dirty);
            var temp = ArrayPool<byte>.Shared.Rent(tempLen);
            Array.Clear(temp, 0, tempLen);
            fixed (byte* tempPtr = temp)
                Projection.CompositeRenderListViaRangeCache(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height,
                    renderList, 0, split, 1.0, dirty, dirty.X, dirty.Y);
            strokeBelow.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
            ArrayPool<byte>.Shared.Return(temp);
        }

        var aboveStart = split + 1;
        if (aboveStart < renderList.Count)
        {
            var strokeAbove = Projection.GetOrCreateStrokeAbove(width, height);
            strokeAbove.Buffer.Clear(dirty);
            var temp = ArrayPool<byte>.Shared.Rent(tempLen);
            Array.Clear(temp, 0, tempLen);
            fixed (byte* tempPtr = temp)
                Projection.CompositeRenderListViaRangeCache(tempPtr, dirty.Width * 4, dirty.Width, dirty.Height,
                    renderList, aboveStart, renderList.Count - aboveStart, 1.0, dirty, dirty.X, dirty.Y);
            strokeAbove.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4);
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private unsafe void CompositeTileCpu(SKBitmap tile, PixelRegion tileRect,
        IReadOnlyList<ProjectionSiblingItem> renderList, int originX, int originY, int lod, uint paperColor = 0,
        int strokeSplit = -1)
    {
        if (lod > 0)
        {
            CompositeTileLodCpu(tile, tileRect, renderList, originX, originY, lod, paperColor);
            return;
        }

        if (strokeSplit >= 0 && Projection.StrokeBelow != null
            && (strokeSplit == 0 || Projection.StrokeBelow.Buffer.HasContentTiles(tileRect))
            && (strokeSplit + 1 >= renderList.Count || (Projection.StrokeAbove != null && Projection.StrokeAbove.Buffer.HasContentTiles(tileRect))))
        {
            CompositeTileStrokeCpu(tile, tileRect, renderList, strokeSplit, originX, originY, paperColor);
            return;
        }

        var tw = tile.Width;
        var th = tile.Height;
        var dst = (byte*)tile.GetPixels().ToPointer();
        var dstStride = tile.RowBytes;
        ClearTile(dst, dstStride, tileRect, originX, originY, paperColor);
        CompositeRenderList(dst, dstStride, tw, th, renderList, 1.0, tileRect, originX, originY);
    }

    private unsafe void CompositeTileStrokeCpu(SKBitmap tile, PixelRegion tileRect,
        IReadOnlyList<ProjectionSiblingItem> renderList, int split, int originX, int originY, uint paperColor)
    {
        var tw = tile.Width;
        var th = tile.Height;
        var dst = (byte*)tile.GetPixels().ToPointer();
        var dstStride = tile.RowBytes;
        ClearTile(dst, dstStride, tileRect, originX, originY, paperColor);
        // 1. Pre-baked below cache
        LayerCompositorPixelOps.CompositeProjectionBuffer(dst, dstStride, Projection.StrokeBelow!.Buffer, "Normal", 1.0, tileRect, originX, originY);
        // 2. Active stroke layer only
        CompositeRenderListRange(dst, dstStride, tw, th, renderList, split, split + 1, 1.0, tileRect, originX, originY);
        // 3. Pre-baked above cache (null when active layer is topmost)
        if (Projection.StrokeAbove != null)
            LayerCompositorPixelOps.CompositeProjectionBuffer(dst, dstStride, Projection.StrokeAbove.Buffer, "Normal", 1.0, tileRect, originX, originY);
    }

    private unsafe void CompositeTileLodCpu(SKBitmap tile, PixelRegion tileRect,
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

        // Clipped layers require the base-layer mask. Fall back to full-res
        // composite + bilinear downsample for any render list with clipped items.
        var hasClipped = HasClippedDescendant(renderList);
        if (hasClipped)
        {
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
                    var fd = (byte*)tile.GetPixels().ToPointer();
                    var fds = tile.RowBytes;
                    var scl = 1 << lod;
                    for (var y = 0; y < tile.Height; y++)
                    {
                        var sy = Math.Min(tileRect.Height - 1.0f, (y + 0.5f) * scl - 0.5f);
                        var sy0 = Math.Clamp((int)MathF.Floor(sy), 0, tileRect.Height - 1);
                        var sy1 = Math.Min(tileRect.Height - 1, sy0 + 1);
                        var fy = sy - sy0;
                        var srcRow0 = tp + sy0 * tileRect.Width * 4;
                        var srcRow1 = tp + sy1 * tileRect.Width * 4;
                        var dstRow = fd + y * fds;
                        for (var x = 0; x < tile.Width; x++)
                        {
                            var sx = Math.Min(tileRect.Width - 1.0f, (x + 0.5f) * scl - 0.5f);
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
            finally { ArrayPool<byte>.Shared.Return(temp); }
            return;
        }

        var dst = (byte*)tile.GetPixels().ToPointer();
        var dstStride = tile.RowBytes;
        var dstW = tile.Width;
        var dstH = tile.Height;

        for (var y = 0; y < dstH; y++)
        {
            var row = (uint*)(dst + y * dstStride);
            for (var x = 0; x < dstW; x++) row[x] = paperColor;
        }

        var siblingCache = new Dictionary<DrawingLayer, List<ProjectionSiblingItem>>();
        CompositeRenderListLod(dst, dstStride, dstW, dstH, renderList,
            1.0, tileRect, originX, originY, lod, siblingCache);
    }

    private unsafe void CompositeRenderListLod(
        byte* dst, int dstStride, int dstW, int dstH,
        IReadOnlyList<ProjectionSiblingItem> renderList,
        double opacityScale, PixelRegion clip,
        int originX, int originY, int lod,
        Dictionary<DrawingLayer, List<ProjectionSiblingItem>> siblingCache)
    {
        if (opacityScale <= 0) return;

        var scale = 1 << lod;

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

                if (!siblingCache.TryGetValue(layer, out var childStack))
                {
                    childStack = LayerProjectionPlane.BuildSiblingStack(layer.Children);
                    siblingCache[layer] = childStack;
                }

                if (layer.BlendMode == "PassThrough"
                    || (layer.BlendMode == "Normal" && layer.Opacity >= 0.999))
                {
                    // PassThrough and Normal@100%: recurse directly without temp
                    // buffer. Normal@<100% needs temp buffer for correct SrcOver.
                    CompositeRenderListLod(dst, dstStride, dstW, dstH,
                        childStack, groupOpacity, clip, originX, originY, lod, siblingCache);
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
                            childStack, 1.0, clip, originX, originY, lod, siblingCache);
                        BlendTempLod(dst, dstStride, tempPtr, tempStride,
                            dstW, dstH, layer.BlendMode, groupOpacity);
                    }
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
            else if (layer.Adjustment != null)
            {
                // dst is LOD-scaled (dstW×dstH); Apply* methods index by clip coords,
                // so pass a zero-based LOD-space region instead of document-space clip.
                AdjustmentLayerProcessor.Apply(dst, dstStride, dstW, dstH,
                    layer.Adjustment, layer.Opacity * opacityScale,
                    new PixelRegion(0, 0, dstW, dstH), 0, 0);
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
                        var dstRow = dst + py * dstStride;

                        for (var px = 0; px < dstW; px++)
                        {
                            var srcBlockX = originX + px * scale - offsetX;
                            var srcBlockY = originY + py * scale - offsetY;
                            if (!SampleLayerBlockAverage(layer, srcBlockX, srcBlockY, scale,
                                    out var sb, out var sg, out var sr, out var srcA))
                                continue;

                            if (!fullOpacity)
                                srcA = (srcA * opacityByte + 127) / 255;
                            if (srcA == 0) continue;

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
        var blendMode = layer.BlendMode;
        var opacityByte = (uint)Math.Round(groupOpacity * 255);
        const int ts = TiledPixelBuffer.TileSize;

        layer.Pixels.EnterPixelReadLock();
        try
        {
            for (var py = 0; py < dstH; py++)
            {
                var docY = originY + py * scale + halfStep;
                var srcY = docY - layer.OffsetY;
                if (srcY < layer.Pixels.MinY || srcY >= layer.Pixels.MaxY) continue;

                var tileY = FloorDiv(srcY, ts);
                var tileLocalY = srcY - tileY * ts;
                var dstRow = dst + py * dstStride;
                int prevTileX = int.MinValue;
                byte[]? srcTile = null;

                for (var px = 0; px < dstW; px++)
                {
                    var docX = originX + px * scale + halfStep;
                    var srcX = docX - layer.OffsetX;
                    if (srcX < layer.Pixels.MinX || srcX >= layer.Pixels.MaxX) continue;

                    var tileX = FloorDiv(srcX, ts);
                    if (tileX != prevTileX)
                    {
                        srcTile = layer.Pixels.GetTileOrNull(tileX, tileY);
                        prevTileX = tileX;
                    }
                    if (srcTile == null) continue;

                    var tax = srcX - tileX * ts;
                    var srcIdx = (tileLocalY * ts + tax) * 4;
                    uint rawA = srcTile[srcIdx + 3];
                    if (rawA == 0) continue;

                    uint srcA = (rawA * opacityByte + 127) / 255;
                    if (srcA == 0) continue;

                    double sB = srcTile[srcIdx] / 255.0;
                    double sG = srcTile[srcIdx + 1] / 255.0;
                    double sR = srcTile[srcIdx + 2] / 255.0;

                    var dO = px * 4;
                    uint dB = dstRow[dO];
                    uint dG = dstRow[dO + 1];
                    uint dR = dstRow[dO + 2];
                    uint dA = dstRow[dO + 3];

                    double dstR = dR / 255.0;
                    double dstG = dG / 255.0;
                    double dstB = dB / 255.0;
                    double dstA = dA / 255.0;
                    double normSrcA = srcA / 255.0;

                    var (blendR, blendG, blendB) = LayerCompositorPixelOps.ApplyBlendMode(
                        sR, sG, sB, normSrcA, dstR, dstG, dstB, dstA, blendMode);

                    double invSrcA = 1.0 - normSrcA;
                    double dstContA = dstA * invSrcA;
                    double outA = normSrcA + dstContA;
                    if (outA <= 0) continue;

                    dstRow[dO]     = (byte)Math.Clamp((blendB * normSrcA + dstB * dstContA) / outA * 255, 0, 255);
                    dstRow[dO + 1] = (byte)Math.Clamp((blendG * normSrcA + dstG * dstContA) / outA * 255, 0, 255);
                    dstRow[dO + 2] = (byte)Math.Clamp((blendR * normSrcA + dstR * dstContA) / outA * 255, 0, 255);
                    dstRow[dO + 3] = (byte)Math.Clamp(outA * 255, 0, 255);
                }
            }
        }
        finally
        {
            layer.Pixels.ExitPixelReadLock();
        }
    }

    private static bool CanLodFastPath(IReadOnlyList<ProjectionSiblingItem> renderList)
    {
        for (var i = 0; i < renderList.Count; i++)
        {
            var item = renderList[i];
            var layer = item.Layer;
            if (item.IsClipped) return false;
            if (layer.Adjustment != null) return false;
            if (layer.LayerColor.HasValue) return false;
            if (layer.ExpressionColor != ExpressionColorMode.Color) return false;
            if (layer.IsGroup)
            {
                // Normal groups at <100% opacity need a temp buffer (SrcOver
                // is not associative with alpha pre-multiplication). PassThrough
                // groups and Normal groups at full opacity can recurse directly.
                if (layer.BlendMode == "Normal" && layer.Opacity < 0.999) return false;
                if (layer.BlendMode is not ("Normal" or "PassThrough")) return false;
                if (!CanLodFastGroupChildren(layer)) return false;
            }
            else if (layer.BlendMode != "Normal")
            {
                return false;
            }
        }
        return true;
    }

    private static bool CanLodFastGroupChildren(DrawingLayer group)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0) continue;
            if (child.IsClipping) return false;
            if (child.Adjustment != null) return false;
            if (child.LayerColor.HasValue) return false;
            if (child.ExpressionColor != ExpressionColorMode.Color) return false;
            if (child.IsGroup)
            {
                if (child.BlendMode == "Normal" && child.Opacity < 0.999) return false;
                if (child.BlendMode is not ("Normal" or "PassThrough")) return false;
                if (!CanLodFastGroupChildren(child)) return false;
            }
            else if (child.BlendMode != "Normal")
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasClippedDescendant(IReadOnlyList<ProjectionSiblingItem> renderList)
    {
        for (var i = 0; i < renderList.Count; i++)
        {
            if (renderList[i].IsClipped) return true;
            var layer = renderList[i].Layer;
            if (layer.IsGroup && HasClippingInChildren(layer.Children)) return true;
        }
        return false;
    }

    private static bool HasClippingInChildren(IReadOnlyList<DrawingLayer> children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i].IsClipping) return true;
            if (children[i].IsGroup && HasClippingInChildren(children[i].Children)) return true;
        }
        return false;
    }

    private unsafe void CompositeTileLodFastCpu(SKBitmap tile, PixelRegion tileRect,
        IReadOnlyList<ProjectionSiblingItem> renderList, int originX, int originY, int lod, uint paperColor)
    {
        var dst = (byte*)tile.GetPixels().ToPointer();
        var dstStride = tile.RowBytes;
        var scale = 1 << lod;
        var dstW = tile.Width;
        var dstH = tile.Height;

        for (var y = 0; y < dstH; y++)
        {
            var row = (uint*)(dst + y * dstStride);
            for (var x = 0; x < dstW; x++) row[x] = paperColor;
        }

        for (var i = 0; i < renderList.Count; i++)
            CompositeFastItem(dst, dstStride, dstW, dstH, originX, originY, scale,
                renderList[i].Layer, 1.0);
    }

    private unsafe void CompositeFastItem(
        byte* dst, int dstStride, int dstW, int dstH,
        int originX, int originY, int scale,
        DrawingLayer layer, double inheritedOpacity)
    {
        if (!layer.IsVisible || layer.Opacity <= 0 || inheritedOpacity <= 0) return;

        var layerOpacity = layer.Opacity * inheritedOpacity;
        if (layerOpacity <= 0) return;
        var opacityByte = (uint)Math.Round(layerOpacity * 255);
        if (opacityByte == 0) return;

        if (layer.IsGroup && layer.Children.Count > 0)
        {
            // Normal/PassThrough group: recurse into children with group opacity
            for (var i = 0; i < layer.Children.Count; i++)
                CompositeFastItem(dst, dstStride, dstW, dstH, originX, originY, scale,
                    layer.Children[i], layerOpacity);
            return;
        }

        var fullOpacity = opacityByte == 255;
        var offsetX = layer.OffsetX;
        var offsetY = layer.OffsetY;

        layer.Pixels.EnterPixelReadLock();
        try
        {
            for (var py = 0; py < dstH; py++)
            {
                var dstRow = (uint*)(dst + py * dstStride);

                for (var px = 0; px < dstW; px++)
                {
                    var srcBlockX = originX + px * scale - offsetX;
                    var srcBlockY = originY + py * scale - offsetY;
                    if (!SampleLayerBlockAverage(layer, srcBlockX, srcBlockY, scale,
                            out var sb, out var sg, out var sr, out var rawA))
                        continue;

                    uint srcA = fullOpacity ? rawA : (rawA * opacityByte + 127) / 255;
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

    private static bool SampleLayerBlockAverage(DrawingLayer layer, int srcBlockX, int srcBlockY, int scale,
        out uint b, out uint g, out uint r, out uint a)
    {
        b = g = r = a = 0;

        var x0 = Math.Max(srcBlockX, layer.Pixels.MinX);
        var y0 = Math.Max(srcBlockY, layer.Pixels.MinY);
        var x1 = Math.Min(srcBlockX + scale, layer.Pixels.MaxX);
        var y1 = Math.Min(srcBlockY + scale, layer.Pixels.MaxY);
        if (x0 >= x1 || y0 >= y1) return false;

        const int ts = TiledPixelBuffer.TileSize;
        ulong sumA = 0;
        ulong sumPB = 0;
        ulong sumPG = 0;
        ulong sumPR = 0;

        var tileX0 = FloorDiv(x0, ts);
        var tileY0 = FloorDiv(y0, ts);
        var tileX1 = FloorDiv(x1 - 1, ts);
        var tileY1 = FloorDiv(y1 - 1, ts);
        if (tileX0 == tileX1 && tileY0 == tileY1)
        {
            var tile = layer.Pixels.GetTileOrNull(tileX0, tileY0);
            if (tile == null) return false;

            for (var y = y0; y < y1; y++)
            {
                var localY = y - tileY0 * ts;
                for (var x = x0; x < x1; x++)
                {
                    var localX = x - tileX0 * ts;
                    var o = (localY * ts + localX) * 4;
                    var aa = (uint)tile[o + 3];
                    if (aa == 0) continue;

                    sumA += aa;
                    sumPB += (ulong)tile[o + 0] * aa;
                    sumPG += (ulong)tile[o + 1] * aa;
                    sumPR += (ulong)tile[o + 2] * aa;
                }
            }

            return FinishLayerBlockAverage(sumA, sumPB, sumPG, sumPR, scale, out b, out g, out r, out a);
        }

        for (var y = y0; y < y1; y++)
        {
            var tileY = FloorDiv(y, ts);
            var localY = y - tileY * ts;
            int prevTileX = int.MinValue;
            byte[]? tile = null;
            for (var x = x0; x < x1; x++)
            {
                var tileX = FloorDiv(x, ts);
                if (tileX != prevTileX)
                {
                    tile = layer.Pixels.GetTileOrNull(tileX, tileY);
                    prevTileX = tileX;
                }
                if (tile == null) continue;

                var localX = x - tileX * ts;
                var o = (localY * ts + localX) * 4;
                var aa = (uint)tile[o + 3];
                if (aa == 0) continue;

                sumA += aa;
                sumPB += (ulong)tile[o + 0] * aa;
                sumPG += (ulong)tile[o + 1] * aa;
                sumPR += (ulong)tile[o + 2] * aa;
            }
        }

        return FinishLayerBlockAverage(sumA, sumPB, sumPG, sumPR, scale, out b, out g, out r, out a);
    }

    private static bool FinishLayerBlockAverage(ulong sumA, ulong sumPB, ulong sumPG, ulong sumPR, int scale,
        out uint b, out uint g, out uint r, out uint a)
    {
        b = g = r = a = 0;
        if (sumA == 0) return false;

        var blockArea = (uint)(scale * scale);
        a = (uint)((sumA + (blockArea >> 1)) / blockArea);
        if (a == 0) return false;

        b = (uint)((sumPB + (sumA >> 1)) / sumA);
        g = (uint)((sumPG + (sumA >> 1)) / sumA);
        r = (uint)((sumPR + (sumA >> 1)) / sumA);
        return true;
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
                else if (item.Layer.Adjustment != null)
                    AdjustmentLayerProcessor.ApplyClipped(dst, dstStride, width, height,
                        item.Layer.Adjustment, item.Layer.Opacity * opacityScale,
                        baseLayer, clip, originX, originY);
                else
                    LayerCompositorPixelOps.CompositeClippedLayer(dst, dstStride, width, height, item.Layer, baseLayer, opacityScale, clip, originX, originY);
            }
            else if (item.Layer.IsGroup)
                Projection.CompositeGroupNode(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
            else if (item.Layer.Adjustment != null)
                AdjustmentLayerProcessor.Apply(dst, dstStride, width, height,
                    item.Layer.Adjustment, item.Layer.Opacity * opacityScale,
                    clip, originX, originY);
            else
                LayerCompositorPixelOps.CompositeLayer(dst, dstStride, width, height, item.Layer, opacityScale, clip, originX, originY);
        }
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

    private void InvalidateGroupCaches(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers, int? layerIndex,
        bool fullGroupInvalidation = false, bool invalidateStrokeBelow = true, bool invalidateStrokeAbove = true)
    {
        // Range caches are safe when ONLY the stroke layer changed (it's excluded from both ranges).
        // Clear them when any other layer changes so stale sub-range composites don't bleed through.
        if (invalidateStrokeBelow || invalidateStrokeAbove)
            Projection.ClearRangeCaches();
        Projection.InvalidateGroupCaches(region, layers, layerIndex, fullGroupInvalidation, invalidateStrokeBelow, invalidateStrokeAbove);
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

                var src = (byte*)srcBmp.GetPixels().ToPointer();
                var tw = Math.Min(srcBmp.Width, outputWidth - dx);
                var th = Math.Min(srcBmp.Height, outputHeight - dy);
                if (tw <= 0 || th <= 0)
                    continue;

                var rowBytes = tw * 4;
                for (var y = 0; y < th; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcBmp.RowBytes,
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
        b = g = r = a = 0;
        var frame = Volatile.Read(ref _currentFrame);
        if (frame == null || (uint)docX >= (uint)frame.Width || (uint)docY >= (uint)frame.Height)
            return false;

        var stride = CmpTileSize * (1 << frame.Lod);
        var scale = 1 << frame.Lod;
        var tx = FloorDiv(docX, stride);
        var ty = FloorDiv(docY, stride);
        if (tx < frame.Area.Left || tx > frame.Area.Right || ty < frame.Area.Top || ty > frame.Area.Bottom)
            return false;

        var tile = frame.Tiles[FrameTileIndex(frame, tx, ty)];
        if (tile == null) return false;

        var image = tile.Image;
        var localDocX = docX - tx * stride;
        var localDocY = docY - ty * stride;
        var px = localDocX / scale;
        var py = localDocY / scale;
        if ((uint)px >= (uint)image.Width || (uint)py >= (uint)image.Height)
            return false;

        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            return false;

        unsafe
        {
            var src = (byte*)bitmap.GetPixels().ToPointer();
            var ptr = src + py * bitmap.RowBytes + px * 4;
            b = ptr[0];
            g = ptr[1];
            r = ptr[2];
            a = ptr[3];
        }

        return true;
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

/// <summary>
/// Draws a single compositor tile via the Skia GPU path, bypassing Avalonia's
/// software WriteableBitmap route. The draw op snapshots an SKImage so the
/// render thread never touches a compositor tile after it has been replaced.
/// </summary>
internal sealed class SkiaTileDrawOp : Avalonia.Rendering.SceneGraph.ICustomDrawOperation
{
    private readonly SKImage? _image;
    private readonly bool _ownsImage;
    private readonly SKRect _src;
    private readonly Rect _dest;

    public SkiaTileDrawOp(SKBitmap bmp, SKRect src, Rect dest)
    {
        _image = SKImage.FromBitmap(bmp);
        _ownsImage = true;
        _src = src;
        _dest = dest;
    }

    public SkiaTileDrawOp(SKImage image, SKRect src, Rect dest)
    {
        _image = image;
        _ownsImage = false;
        _src = src;
        _dest = dest;
    }

    public Rect Bounds => _dest;
    public bool HitTest(Point p) => false;
    public bool Equals(Avalonia.Rendering.SceneGraph.ICustomDrawOperation? other) => false;
    public void Dispose()
    {
        if (_ownsImage)
            _image?.Dispose();
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (_image == null) return;
        var lease = context.TryGetFeature<Avalonia.Skia.ISkiaSharpApiLeaseFeature>()?.Lease();
        if (lease == null) return;
        using (lease)
        {
            var dstRect = new SKRect(
                (float)_dest.X, (float)_dest.Y,
                (float)_dest.Right, (float)_dest.Bottom);
            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            lease.SkCanvas.DrawImage(_image, _src, dstRect, sampling);
        }
    }
}
