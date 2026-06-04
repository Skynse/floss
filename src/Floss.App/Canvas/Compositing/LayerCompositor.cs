using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Floss.App;
using Floss.App.Canvas.Engine;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Drawpile GlCanvasImpl: all tiles stored in one contiguous flat buffer
/// (split into cells for huge canvases). Tiles are memcpy'd into the cell,
/// each cell gets one SKImage, DrawTiles draws one SkiaTileDrawOp per cell.
/// </summary>
public sealed class LayerCompositor : IDisposable
{
    private static readonly RenderThreadPool _renderPool = RenderThreadPool.Create("FlossComposite");
    private WriteableBitmap? _cachedBitmap;
    private int _cachedBitmapW, _cachedBitmapH;
    private readonly ManualResetEventSlim _compositeIdleEvent = new(true);
    private LayerProjectionPlane? _projection;
    private LayerProjectionPlane Projection =>
        _projection ??= new LayerProjectionPlane(new MergeHost(this));
    private static readonly System.Collections.Concurrent.ConcurrentBag<CountdownEvent> _cdePool = [];
    public const int DirtyTileBudget = 32;
    private const int MaxMissingTilesPerFrame = 96;
    private const int MaxCompositeCacheTiles = 8192;
    private const int CmpTileSize = 64;
    // Drawpile PixmapGrid max cell dimension (32000 for Qt's max pixmap)
    private const int MaxCellDim = 4096 * 4; // 16384 — fits in most GPU texture limits

    // ── Cells: one SKBitmap per cell (Drawpile: PixmapGrid) ───────────────
    private SKBitmap?[] _cellBitmaps = [];
    private SKImage?[] _cellImages = [];
    private bool[] _cellDirty = [];
    private int _cellCols, _cellRows;

    // ── Per-tile composite scratch (Drawpile: transient tile per thread) ──
    private SKBitmap?[] _tileScratch = [];
    private int _xtiles, _ytiles, _tileTotal, _width, _height;

    private bool _fullDirty = true;
    private PixelRegion? _dirtyRegion;
    private readonly HashSet<int> _pendingComposite = [];

    private readonly ConcurrentQueue<(object Resource, int Delay)> _delayedDispose = new();

    internal readonly object CompositeGate = new();
    private int _compositeActive;
    public bool IsCompositeActive => Volatile.Read(ref _compositeActive) != 0;
    public bool HasAnyTiles => _tileTotal > 0 && _cellBitmaps.Length > 0;

    private int _strokeSuspendDepth;
    private int _strokePaintLayerIndex = -1;
    public bool StrokeSuspendActive => _strokeSuspendDepth > 0;

    // During strokes, cache the composite of layers below the paint layer per tile.
    // Only the paint layer + layers above are re-merged each dab.
    private SKBitmap?[] _strokeBelowCache = [];
    private bool[] _strokeBelowCacheReady = [];
    private int _strokeBelowCacheTileTotal;

    public void Dispose()
    {
        _compositeIdleEvent.Wait(); // Efficiently blocks without burning CPU
        ClearAll();
        _compositeIdleEvent.Dispose();
    }

    private static void DispatchToPool(int count, Action<int> action)
    {
        if (count <= 0) return;

        if (!_cdePool.TryTake(out var cde))
            cde = new CountdownEvent(1);

        cde.Reset(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var idx = i;
                _renderPool.Enqueue(() =>
                {
                    try { action(idx); }
                    finally { cde.Signal(); }
                });
            }
            cde.Wait();
        }
        finally
        {
            // Return to pool for next frame, cap pool size to prevent memory leaks
            if (_cdePool.Count < 16)
                _cdePool.Add(cde);
            else
                cde.Dispose();
        }
    }

    // ═══ Public API ═══════════════════════════════════════════════════════════

    public int LastLod => 0;
    public int LastDirtyTileCount { get; private set; }
    public int LastMissingTileCount { get; private set; }
    public int PendingDirtyTileCount => _pendingComposite.Count;
    public int SelectLod(int w, int h, double z) => 0;

    public bool IsFrameMissingTiles(PixelRegion vp)
    {
        if (_tileTotal == 0) return true;
        var area = TileAreaForRegion(vp.ClipTo(_width, _height));
        for (var ty = area.Top; ty <= area.Bottom; ty++)
            for (var tx = area.Left; tx <= area.Right; tx++)
            {
                var idx = tileIndex(tx, ty);
                if (idx < 0 || _pendingComposite.Contains(idx)) return true;
            }
        return false;
    }

    public void DrainDisposalQueue()
    {
        var cnt = _delayedDispose.Count;
        for (var i = 0; i < cnt && _delayedDispose.TryDequeue(out var e); i++)
        {
            if (e.Delay > 0) { _delayedDispose.Enqueue((e.Resource, e.Delay - 1)); continue; }
            switch (e.Resource) { case SKBitmap b: b.Dispose(); break; case SKImage img: img.Dispose(); break; }
        }
    }

    public void RemoveGroupCache(DrawingLayer g) { }

    public void SetSize(int w, int h)
    {
        if (_width == w && _height == h) return;
        // Drawpile GlCanvasImpl.resizeImpl: zero the display buffer on resize.
        // All tiles need recompositing with correct current dimensions.
        for (var i = 0; i < _tileScratch.Length; i++) { var t = _tileScratch[i]; if (t != null) { _delayedDispose.Enqueue((t, 8)); _tileScratch[i] = null; } }
        _width = w; _height = h; _fullDirty = true; _dirtyRegion = null;
        _pendingComposite.Clear(); InvalidateCells();
    }

    private void InvalidateCells()
    {
        for (var i = 0; i < _cellImages.Length; i++) { var img = _cellImages[i]; if (img != null) { _delayedDispose.Enqueue((img, 8)); _cellImages[i] = null; } }
        for (var i = 0; i < _cellBitmaps.Length; i++) { var b = _cellBitmaps[i]; if (b != null) { _delayedDispose.Enqueue((b, 8)); _cellBitmaps[i] = null; } }
        _cellBitmaps = []; _cellImages = []; _cellDirty = []; _cellCols = 0; _cellRows = 0;
    }

    private void MarkAllCellsDirty()
    {
        for (var i = 0; i < _cellDirty.Length; i++)
            _cellDirty[i] = true;
    }

    private void ClearAll()
    {
        InvalidateCells();
        ClearStrokeBelowCache();
        _strokeBelowCache = [];
        _strokeBelowCacheReady = [];
        _strokeBelowCacheTileTotal = 0;
        for (var i = 0; i < _tileScratch.Length; i++) { var t = _tileScratch[i]; if (t != null) { _delayedDispose.Enqueue((t, 8)); _tileScratch[i] = null; } }
        _tileScratch = []; _xtiles = 0; _ytiles = 0; _tileTotal = 0;
        _pendingComposite.Clear();
    }

    public void ClearAllTiles() => ClearAll();

    // ═══ DrawTiles — Drawpile: one SkiaTileDrawOp per cell ═══════════════════

    public unsafe WriteableBitmap Bitmap
    {
        get
        {
            if (_width * _height > 64_000_000 || _cellBitmaps.Length == 0)
                return new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            // FIX: Cache the bitmap and only recreate if the canvas size changes
            if (_cachedBitmap == null || _cachedBitmapW != _width || _cachedBitmapH != _height)
            {
                _cachedBitmap?.Dispose();
                _cachedBitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                _cachedBitmapW = _width;
                _cachedBitmapH = _height;
            }

            using var fb = _cachedBitmap.Lock();
            var d = (byte*)fb.Address;
            lock (CompositeGate)
            {
                for (var cy = 0; cy < _cellRows; cy++)
                    for (var cx = 0; cx < _cellCols; cx++)
                    {
                        var ci = cy * _cellCols + cx; var cb = _cellBitmaps[ci]; if (cb == null) continue;
                        var dx = cx * MaxCellDim; var dy = cy * MaxCellDim;
                        var tw = cb.Width; var th = cb.Height; if (tw <= 0 || th <= 0) { tw = 1; th = 1; }
                        var s = (byte*)cb.GetPixels().ToPointer(); var sr = cb.RowBytes;
                        for (var y = 0; y < th; y++) Buffer.MemoryCopy(s + y * sr, d + (dy + y) * fb.RowBytes + dx * 4, tw * 4, tw * 4);
                    }
            }
            return _cachedBitmap;
        }
    }

    public void DrawTiles(DrawingContext context, Rect target, PixelRegion? visibleViewport = null)
    {
        lock (CompositeGate)
        {
            EnsureCells();
            if (_cellBitmaps.Length == 0 || _cellCols <= 0) return;

            var vp = visibleViewport?.ClipTo(_width, _height) ?? new PixelRegion(0, 0, _width, _height);
            if (vp.IsEmpty) return;

            FlushDirtyCellImages();

            for (var ci = 0; ci < _cellBitmaps.Length; ci++)
            {
                var bmp = _cellBitmaps[ci]; if (bmp == null) continue;
                var cx = ci % _cellCols; var cy = ci / _cellCols;
                var x = cx * MaxCellDim; var y = cy * MaxCellDim;
                var w = Math.Min(MaxCellDim, _width - x);
                var h = Math.Min(MaxCellDim, _height - y);
                if (x + w <= vp.X || y + h <= vp.Y || x >= vp.Right || y >= vp.Bottom) continue;

                var img = _cellImages[ci];
                if (img == null) { img = SKImage.FromBitmap(bmp); _cellImages[ci] = img; }
                context.Custom(new SkiaTileDrawOp(img, new SKRect(0, 0, bmp.Width, bmp.Height), new Rect(x, y, w, h)));
            }
        }
    }

    private void EnsureCells()
    {
        var nc = (_width + MaxCellDim - 1) / MaxCellDim; if (nc <= 0) nc = 1;
        var nr = (_height + MaxCellDim - 1) / MaxCellDim; if (nr <= 0) nr = 1;
        if (nc == _cellCols && nr == _cellRows && _cellBitmaps.Length == nc * nr) return;

        // Preserve overlapping cells
        var oldBmps = _cellBitmaps; var oldImgs = _cellImages; var oldDirty = _cellDirty;
        var oc = _cellCols; var or = _cellRows;
        _cellCols = nc; _cellRows = nr; var nt = nc * nr;
        _cellBitmaps = new SKBitmap?[nt]; _cellImages = new SKImage?[nt]; _cellDirty = new bool[nt];
        for (var oy = 0; oy < Math.Min(or, nr); oy++)
            for (var ox = 0; ox < Math.Min(oc, nc); ox++)
            {
                var oi = oy * oc + ox; var ni = oy * nc + ox;
                if (oi < oldBmps.Length) { _cellBitmaps[ni] = oldBmps[oi]; _cellImages[ni] = oldImgs[oi]; _cellDirty[ni] = oldDirty[oi]; }
            }
        for (var i = 0; i < oldBmps.Length; i++) { var oy = i / oc; var ox = i % oc; if (oy >= nr || ox >= nc) { var img = oldImgs[i]; if (img != null) _delayedDispose.Enqueue(((object)img, 8)); var bmp = oldBmps[i]; if (bmp != null) _delayedDispose.Enqueue(((object)bmp, 8)); } }

        for (var ci = 0; ci < nt; ci++)
        {
            if (_cellBitmaps[ci] != null) continue;
            var cw = Math.Min(MaxCellDim, _width - (ci % nc) * MaxCellDim);
            var ch = Math.Min(MaxCellDim, _height - (ci / nc) * MaxCellDim);
            if (cw <= 0 || ch <= 0) { cw = 1; ch = 1; }
            _cellBitmaps[ci] = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            _cellBitmaps[ci]!.Erase(SKColors.Transparent);
            _cellDirty[ci] = true;
        }

        // Copy existing tiles into newly created cells
        CopyAllTilesToCells();
    }

    private unsafe void CopyTileToCell(int ti)
    {
        var ty = ti / _xtiles; var tx = ti % _xtiles;
        var bmp = _tileScratch[ti]; if (bmp == null) return;
        var dx = tx * CmpTileSize; var dy = ty * CmpTileSize;
        var tw = bmp.Width; var th = bmp.Height; if (tw <= 0 || th <= 0) return;

        var cx = dx / MaxCellDim; var cy = dy / MaxCellDim;
        if (cx >= _cellCols || cy >= _cellRows) return;
        var ci = cy * _cellCols + cx;
        var cb = _cellBitmaps[ci]; if (cb == null) return;
        var lx = dx - cx * MaxCellDim; var ly = dy - cy * MaxCellDim;
        var cw = Math.Min(tw, cb.Width - lx); var ch = Math.Min(th, cb.Height - ly);
        if (cw <= 0 || ch <= 0) return;

        var s = (byte*)bmp.GetPixels().ToPointer(); var sr = bmp.RowBytes;
        var d = (byte*)cb.GetPixels().ToPointer(); var dr = cb.RowBytes;
        for (var y = 0; y < ch; y++) Buffer.MemoryCopy(s + y * sr, d + (ly + y) * dr + lx * 4, cw * 4, cw * 4);
        _cellDirty[ci] = true;
    }

    private void CopyAllTilesToCells()
    {
        for (var ti = 0; ti < _tileTotal; ti++)
        {
            var bmp = _tileScratch[ti];
            if (bmp == null) continue;
            CopyTileToCell(ti);
        }
    }

    private void FlushDirtyCellImages()
    {
        for (var ci = 0; ci < _cellBitmaps.Length; ci++)
        {
            if (!_cellDirty[ci]) continue;
            var bmp = _cellBitmaps[ci]; if (bmp == null) continue;
            var old = _cellImages[ci];
            if (old != null) _delayedDispose.Enqueue((old, 8));
            _cellImages[ci] = SKImage.FromBitmap(bmp);
            _cellDirty[ci] = false;
        }
    }

    // ═══ Invalidation ═════════════════════════════════════════════════════════

    public void Invalidate(PixelRegion? r = null) => Invalidate(r, null, null);

    public void Invalidate(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers,
        int? layerIndex, bool metadataOnly = false, PixelRegion? viewportClip = null)
    {
        lock (CompositeGate)
        {
            if (layerIndex is >= 0 && _strokeSuspendDepth > 0 && _strokePaintLayerIndex < 0)
                _strokePaintLayerIndex = layerIndex.Value;
            if (region is null || region.Value.IsEmpty) { _fullDirty = true; _dirtyRegion = null; MarkAllCellsDirty(); }
            else
            {
                // Drawpile: track the FULL dirty region for eventual compositing,
                // but only queue viewport-overlapping tiles for the current frame.
                var fullDirty = region.Value;
                if (!_fullDirty)
                    _dirtyRegion = _dirtyRegion is { } e ? e.Union(fullDirty) : fullDirty;

                var tileRegion = viewportClip is { IsEmpty: false } v2 ? fullDirty.Intersect(v2) : fullDirty;
                if (!tileRegion.IsEmpty)
                    QueueDirtyTilesForRegion(tileRegion);

                InvalidateGroupCaches(fullDirty, layers, layerIndex);
            }
        }
    }

    // ═══ Composite ════════════════════════════════════════════════════════════

    public bool Composite(IReadOnlyList<DrawingLayer> layers, int w, int h,
        uint paperColor = 0, PixelRegion? viewport = null, double zoom = 1.0, int? forceLod = null)
    {
        _compositeIdleEvent.Reset();
        Interlocked.Increment(ref _compositeActive);
        try { lock (CompositeGate) { return CompositeCore(layers, w, h, paperColor, viewport); } }
        finally
        {
            if (Interlocked.Decrement(ref _compositeActive) == 0)
                _compositeIdleEvent.Set();
        }
    }

    private unsafe bool CompositeCore(IReadOnlyList<DrawingLayer> layers, int w, int h, uint paperColor, PixelRegion? viewport)
    {
        SetSize(w, h);
        var roots = LayerStackComposition.SelectLayersForComposite(layers);
        var vpClip = viewport?.ClipTo(w, h);
        EnsureTileScratchArray();

        var wasFull = _fullDirty;
        var dirty = (wasFull ? new PixelRegion(0, 0, w, h) : _dirtyRegion ?? PixelRegion.Empty).ClipTo(w, h);
        if (dirty.IsEmpty && vpClip is null && _pendingComposite.Count == 0) { _fullDirty = false; _dirtyRegion = null; return false; }

        if (!dirty.IsEmpty)
        {
            var da = TileAreaForRegion(dirty);
            for (var ty = da.Top; ty <= da.Bottom; ty++)
                for (var tx = da.Left; tx <= da.Right; tx++) { var idx = tileIndex(tx, ty); if (idx >= 0) _pendingComposite.Add(idx); }
        }

        var missing = new List<int>();
        if (vpClip is { IsEmpty: false } vv)
        {
            var va = TileAreaForRegion(vv);
            for (var ty = va.Top; ty <= va.Bottom; ty++)
                for (var tx = va.Left; tx <= va.Right; tx++) { var idx = tileIndex(tx, ty); if (idx >= 0 && _tileScratch[idx] == null && !_pendingComposite.Contains(idx)) missing.Add(idx); }
        }

        _fullDirty = false; _dirtyRegion = null;
        var rl = LayerProjectionPlane.BuildSiblingStack(roots);
        var unbounded = wasFull || vpClip is { IsEmpty: false };
        var pending = SelectPendingTiles(vpClip, missing,
            unbounded ? int.MaxValue : DirtyTileBudget,
            unbounded ? int.MaxValue : MaxMissingTilesPerFrame);
        if (pending.Count == 0 && missing.Count == 0) { LastDirtyTileCount = 0; LastMissingTileCount = 0; return false; }

        var tl = new List<int>(pending);

        var strokePlan = new StrokeSplitPlan();
        var canUseStrokeCache = _strokeSuspendDepth > 0
            && _strokePaintLayerIndex >= 0
            && !wasFull
            && TryCreateStrokeSplitPlan(layers, _strokePaintLayerIndex, rl, out strokePlan);

        if (canUseStrokeCache)
        {
            EnsureStrokeBelowCache();
            foreach (var idx in tl) EnsureScratchTile(idx);
            var all = tl.ToArray();

            void CompStroke(int slot)
            {
                var ti = all[slot];
                var ty = ti / _xtiles;
                var tx = ti % _xtiles;
                var tr = TileRect(tx, ty);
                var ox = tx * CmpTileSize;
                var oy = ty * CmpTileSize;

                if (ti >= _strokeBelowCache.Length)
                    return;

                if (_strokeBelowCache[ti] == null)
                {
                    var cb = AllocTileBitmap(tr);
                    cb.Erase(SKColors.Transparent);
                    _strokeBelowCache[ti] = cb;
                }

                if (ti >= _strokeBelowCacheReady.Length || !_strokeBelowCacheReady[ti])
                {
                    var cb = _strokeBelowCache[ti]!;
                    var cDst = (byte*)cb.GetPixels().ToPointer();
                    var cDs = cb.RowBytes;
                    cb.Erase(SKColors.Transparent);
                    BuildStrokeBelowTile(cDst, cDs, cb.Width, cb.Height, tr, ox, oy, paperColor, strokePlan);
                    _strokeBelowCacheReady[ti] = true;
                }

                var tile = _tileScratch[ti];
                if (tile == null)
                {
                    tile = AllocTileBitmap(tr);
                    _tileScratch[ti] = tile;
                }
                else
                    tile.Erase(SKColors.Transparent);

                var tDst = (byte*)tile.GetPixels().ToPointer();
                var tDs = tile.RowBytes;
                var cb2 = _strokeBelowCache[ti]!;
                var src = (byte*)cb2.GetPixels().ToPointer();
                var sDs = cb2.RowBytes;
                var copyW = Math.Min(cb2.Width, tile.Width);
                var copyH = Math.Min(cb2.Height, tile.Height);
                var rowBytes = copyW * 4;
                for (var y = 0; y < copyH; y++)
                    Buffer.MemoryCopy(src + y * sDs, tDst + y * tDs, rowBytes, rowBytes);

                CompositeStrokeAboveLayers(tDst, tDs, tile.Width, tile.Height, tr, ox, oy, strokePlan);
                CopyTileToCell(ti);
            }

            if (all.Length >= 4 && Environment.ProcessorCount > 1) DispatchToPool(all.Length, CompStroke);
            else for (var i = 0; i < all.Length; i++) CompStroke(i);

            foreach (var idx in all) _pendingComposite.Remove(idx);
        }
        else
        {
            foreach (var idx in tl) EnsureScratchTile(idx);
            var all = tl.ToArray();

            void Comp1(int slot)
            {
                var ti = all[slot]; var ty = ti / _xtiles; var tx = ti % _xtiles; var tr = TileRect(tx, ty);
                var bmp = _tileScratch[ti];
                if (bmp == null) { bmp = AllocTileBitmap(tr); _tileScratch[ti] = bmp; } else bmp.Erase(SKColors.Transparent);
                var dst = (byte*)bmp.GetPixels().ToPointer(); var ds = bmp.RowBytes;
                ClearTile(dst, ds, tr, tx * CmpTileSize, ty * CmpTileSize, paperColor);
                Projection.CompositeSiblingStack(dst, ds, bmp.Width, bmp.Height, rl, 1.0, tr, tx * CmpTileSize, ty * CmpTileSize);
                CopyTileToCell(ti);
            }

            if (all.Length >= 4 && Environment.ProcessorCount > 1) DispatchToPool(all.Length, Comp1);
            else for (var i = 0; i < all.Length; i++) Comp1(i);

            foreach (var idx in all) _pendingComposite.Remove(idx);
        }
        TrimCompositeCache(vpClip);
        LastDirtyTileCount = pending.Count; LastMissingTileCount = missing.Count;
        return _pendingComposite.Count > 0;
    }

    private static unsafe void ClearTile(byte* dst, int stride, PixelRegion clip, int ox, int oy, uint clr)
    {
        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var ly = y - oy; if (ly < 0) continue;
            var lx = clip.X - ox; if (lx < 0) continue;
            var row = (uint*)(dst + ly * stride + lx * 4);
            for (var x = 0; x < clip.Width; x++) row[x] = clr;
        }
    }

    // ═══ Tile scratch array ═══════════════════════════════════════════════════

    private void EnsureTileScratchArray()
    {
        var nxt = (_width + CmpTileSize - 1) / CmpTileSize; if (nxt <= 0) nxt = 1;
        var nyt = (_height + CmpTileSize - 1) / CmpTileSize; if (nyt <= 0) nyt = 1;
        var nt = nxt * nyt;
        if (nt == _tileTotal && _tileScratch.Length == _tileTotal) return;
        // Drawpile GlCanvasImpl.resizeImpl: zero the flat buffer, don't preserve
        // old tile data — stale dimensions would cause buffer overruns.
        var old = _tileScratch;
        _xtiles = nxt; _ytiles = nyt; _tileTotal = nt;
        _tileScratch = new SKBitmap?[nt];
        for (var i = 0; i < old.Length; i++) { var t = old[i]; if (t != null) _delayedDispose.Enqueue((t, 8)); }
    }

    private int tileIndex(int tx, int ty) => (uint)tx >= (uint)_xtiles || (uint)ty >= (uint)_ytiles ? -1 : ty * _xtiles + tx;

    private void EnsureScratchTile(int ti)
    {
        if (_tileScratch[ti] != null) return;
        var ty = ti / _xtiles; var tx = ti % _xtiles;
        var bmp = AllocTileBitmap(TileRect(tx, ty)); bmp.Erase(SKColors.Transparent); _tileScratch[ti] = bmp;
    }

    private static SKBitmap AllocTileBitmap(PixelRegion tr) =>
        new(new SKImageInfo(Math.Max(1, tr.Width), Math.Max(1, tr.Height), SKColorType.Bgra8888, SKAlphaType.Unpremul));

    private void TrimCompositeCache(PixelRegion? vp)
    {
        var n = 0; for (var i = 0; i < _tileTotal; i++) if (_tileScratch[i] != null) n++;
        if (n <= MaxCompositeCacheTiles) return;
        var cx = vp is { } v ? v.X + v.Width / 2 : _width / 2;
        var cy = vp is { } v2 ? v2.Y + v2.Height / 2 : _height / 2;
        var prot = new HashSet<int>();
        if (vp is { IsEmpty: false } vr) { var a = TileAreaForRegion(vr.ClipTo(_width, _height)); for (var ty = a.Top; ty <= a.Bottom; ty++) for (var tx = a.Left; tx <= a.Right; tx++) { var idx = tileIndex(tx, ty); if (idx >= 0) prot.Add(idx); } }
        var entries = new List<(int idx, double dist)>(n);
        for (var i = 0; i < _tileTotal; i++) { if (_tileScratch[i] == null) continue; var ty = i / _xtiles; var tx = i % _xtiles; entries.Add((i, Math.Abs(tx * CmpTileSize + 32 - cx) + Math.Abs(ty * CmpTileSize + 32 - cy))); }
        entries.Sort((a, b) => b.dist.CompareTo(a.dist));
        var target = MaxCompositeCacheTiles * 3 / 4;
        for (var i = 0; i < entries.Count && n > target; i++)
        {
            var idx = entries[i].idx; if (prot.Contains(idx)) continue;
            var ob = Interlocked.Exchange(ref _tileScratch[idx], null);
            if (ob != null) { _delayedDispose.Enqueue((ob, 8)); n--; }
        }
    }

    // ═══ Tile coordinates ═════════════════════════════════════════════════════

    private readonly record struct TileArea(int Left, int Top, int Right, int Bottom)
    { public bool IsEmpty => Right < Left || Bottom < Top; }

    private TileArea TileAreaForRegion(PixelRegion r) => r.IsEmpty ? new(0, 0, -1, -1)
        : new(FloorDiv(r.X, CmpTileSize), FloorDiv(r.Y, CmpTileSize), FloorDiv(r.Right - 1, CmpTileSize), FloorDiv(r.Bottom - 1, CmpTileSize));

    private PixelRegion TileRect(int tx, int ty) =>
        new PixelRegion(tx * CmpTileSize, ty * CmpTileSize, CmpTileSize, CmpTileSize).ClipTo(_width, _height);

    private void QueueDirtyTilesForRegion(PixelRegion cl)
    { var a = TileAreaForRegion(cl); for (var ty = a.Top; ty <= a.Bottom; ty++) for (var tx = a.Left; tx <= a.Right; tx++) { var idx = tileIndex(tx, ty); if (idx >= 0) _pendingComposite.Add(idx); } }

    private List<int> SelectPendingTiles(PixelRegion? vp, List<int> mis, int maxD, int maxM)
    {

        maxD = Math.Min(maxD, _pendingComposite.Count);
        maxM = Math.Min(maxM, mis.Count);


        var res = new List<int>(maxD + maxM);
        var cx = vp is { } v ? v.X + v.Width / 2 : _width / 2;
        var cy = vp is { } v2 ? v2.Y + v2.Height / 2 : _height / 2;

        if (_pendingComposite.Count > 0 && maxD > 0)
        {
            // O(N) scan to find the closest maxD tiles without a full sort
            var closest = new List<(int idx, int dist)>(maxD);
            foreach (var idx in _pendingComposite)
            {
                int ay = idx / _xtiles, ax = idx % _xtiles;
                int dist = Math.Abs(ax * 64 + 32 - cx) + Math.Abs(ay * 64 + 32 - cy);

                if (closest.Count < maxD)
                {
                    closest.Add((idx, dist));
                    if (closest.Count == maxD) closest.Sort((a, b) => b.dist.CompareTo(a.dist)); // keep max at index 0
                }
                else if (dist < closest[0].dist)
                {
                    closest[0] = (idx, dist);
                    closest.Sort((a, b) => b.dist.CompareTo(a.dist));
                }
            }
            foreach (var item in closest)
            {
                _pendingComposite.Remove(item.idx);
                res.Add(item.idx);
            }
        }

        if (mis.Count > 0 && maxM > 0)
        {
            var closest = new List<(int idx, int dist)>(maxM);
            foreach (var idx in mis)
            {
                if (res.Contains(idx)) continue;
                int ay = idx / _xtiles, ax = idx % _xtiles;
                int dist = Math.Abs(ax * 64 + 32 - cx) + Math.Abs(ay * 64 + 32 - cy);

                if (closest.Count < maxM)
                {
                    closest.Add((idx, dist));
                    if (closest.Count == maxM) closest.Sort((a, b) => b.dist.CompareTo(a.dist));
                }
                else if (dist < closest[0].dist)
                {
                    closest[0] = (idx, dist);
                    closest.Sort((a, b) => b.dist.CompareTo(a.dist));
                }
            }
            foreach (var (idx, dist) in closest)
            {
                res.Add(idx);
            }
        }
        return res;
    }

    // ═══ Stroke suspend ═══════════════════════════════════════════════════════

    public void BeginStrokeSuspend(PixelRegion _, int layerIndex = -1)
    {
        lock (CompositeGate)
        {
            _strokeSuspendDepth++;
            _strokePaintLayerIndex = layerIndex;
            if (_strokeSuspendDepth == 1 && layerIndex >= 0)
            {
                EnsureStrokeBelowCache();
                InvalidateStrokeBelowCacheReady();
            }
        }
    }

    // Below-layer pixels do not change during a stroke — do not invalidate the cache here.
    public void ExtendStrokeSuspend(PixelRegion _) { }

    public void EndStrokeSuspend()
    {
        lock (CompositeGate)
        {
            if (_strokeSuspendDepth > 0) _strokeSuspendDepth--;
            if (_strokeSuspendDepth > 0) return;
            _strokePaintLayerIndex = -1;
            ClearStrokeBelowCache();
        }
    }

    public void ResetStrokeSuspend()
    {
        lock (CompositeGate)
        {
            _strokeSuspendDepth = 0;
            _strokePaintLayerIndex = -1;
            ClearStrokeBelowCache();
        }
    }

    private void EnsureStrokeBelowCache()
    {
        if (_strokeBelowCacheTileTotal == _tileTotal
            && _strokeBelowCache.Length == _tileTotal
            && _strokeBelowCacheReady.Length == _tileTotal)
            return;

        ClearStrokeBelowCache();
        _strokeBelowCache = new SKBitmap?[_tileTotal];
        _strokeBelowCacheReady = new bool[_tileTotal];
        _strokeBelowCacheTileTotal = _tileTotal;
    }

    private void ClearStrokeBelowCache()
    {
        for (var i = 0; i < _strokeBelowCache.Length; i++)
        {
            var bmp = _strokeBelowCache[i];
            if (bmp != null)
            {
                _delayedDispose.Enqueue((bmp, 8));
                _strokeBelowCache[i] = null;
            }
        }
        _strokeBelowCacheReady = [];
    }

    private void InvalidateStrokeBelowCacheReady()
    {
        if (_strokeBelowCacheReady.Length == _tileTotal)
            Array.Clear(_strokeBelowCacheReady, 0, _strokeBelowCacheReady.Length);
    }

    private void InvalidateGroupCaches(PixelRegion? r, IReadOnlyList<DrawingLayer>? l, int? li)
    {
        if (_strokeSuspendDepth > 0 && li is int idx && idx != _strokePaintLayerIndex)
            InvalidateStrokeBelowCacheReady();
    }

    private sealed class StrokeSplitPlan
    {
        public List<ProjectionSiblingItem> BelowRoots { get; } = [];
        public List<ProjectionSiblingItem> AboveRoots { get; } = [];
        public DrawingLayer? NestedGroup { get; set; }
        public int NestedChildSplitIndex { get; set; }
    }

    private static bool TryCreateStrokeSplitPlan(
        IReadOnlyList<DrawingLayer> layers,
        int paintLayerIndex,
        IReadOnlyList<ProjectionSiblingItem> rootStack,
        out StrokeSplitPlan plan)
    {
        plan = new StrokeSplitPlan();
        plan.NestedGroup = null;
        plan.NestedChildSplitIndex = 0;
        if (paintLayerIndex < 0 || paintLayerIndex >= layers.Count) return false;
        var paintLayer = layers[paintLayerIndex];

        for (var i = 0; i < rootStack.Count; i++)
        {
            if (rootStack[i].Layer != paintLayer) continue;
            for (var j = 0; j < i; j++) plan.BelowRoots.Add(rootStack[j]);
            for (var j = i; j < rootStack.Count; j++) plan.AboveRoots.Add(rootStack[j]);
            return true;
        }

        for (var gi = 0; gi < rootStack.Count; gi++)
        {
            var group = rootStack[gi].Layer;
            if (!group.IsGroup || !IsDescendantOf(group, paintLayer)) continue;

            var directChild = FindDirectChildOf(group, paintLayer);
            if (directChild == null) return false;
            var childIdx = group.Children.IndexOf(directChild);
            if (childIdx < 0) return false;

            for (var j = 0; j < gi; j++) plan.BelowRoots.Add(rootStack[j]);
            for (var j = gi + 1; j < rootStack.Count; j++) plan.AboveRoots.Add(rootStack[j]);
            plan.NestedGroup = group;
            plan.NestedChildSplitIndex = childIdx;
            return true;
        }

        return false;
    }

    private static bool IsDescendantOf(DrawingLayer ancestor, DrawingLayer node)
    {
        var current = node;
        while (current.Parent != null)
        {
            if (current.Parent == ancestor) return true;
            current = current.Parent;
        }
        return false;
    }

    private static DrawingLayer? FindDirectChildOf(DrawingLayer group, DrawingLayer descendant)
    {
        var current = descendant;
        while (current.Parent != null && current.Parent != group)
            current = current.Parent;
        return current.Parent == group ? current : null;
    }

    private unsafe void BuildStrokeBelowTile(
        byte* dst, int dstStride, int bmpW, int bmpH,
        PixelRegion tr, int ox, int oy, uint paperColor, StrokeSplitPlan plan)
    {
        ClearTile(dst, dstStride, tr, ox, oy, paperColor);
        if (plan.BelowRoots.Count > 0)
            Projection.CompositeSiblingStack(dst, dstStride, bmpW, bmpH, plan.BelowRoots, 1.0, tr, ox, oy);

        if (plan.NestedGroup is { } group && plan.NestedChildSplitIndex > 0)
        {
            var belowKids = new List<DrawingLayer>(plan.NestedChildSplitIndex);
            for (var i = 0; i < plan.NestedChildSplitIndex; i++)
                belowKids.Add(group.Children[i]);
            CompositeGroupChildrenSubset(dst, dstStride, bmpW, bmpH, group, belowKids, 1.0, tr, ox, oy);
        }
    }

    private unsafe void CompositeStrokeAboveLayers(
        byte* dst, int dstStride, int bmpW, int bmpH,
        PixelRegion tr, int ox, int oy, StrokeSplitPlan plan)
    {
        if (plan.NestedGroup is { } group)
        {
            var aboveCount = group.Children.Count - plan.NestedChildSplitIndex;
            if (aboveCount > 0)
            {
                var aboveKids = new List<DrawingLayer>(aboveCount);
                for (var i = plan.NestedChildSplitIndex; i < group.Children.Count; i++)
                    aboveKids.Add(group.Children[i]);
                CompositeGroupChildrenSubset(dst, dstStride, bmpW, bmpH, group, aboveKids, 1.0, tr, ox, oy);
            }
        }

        if (plan.AboveRoots.Count > 0)
            Projection.CompositeSiblingStack(dst, dstStride, bmpW, bmpH, plan.AboveRoots, 1.0, tr, ox, oy);
    }

    private unsafe void CompositeGroupChildrenSubset(
        byte* dst, int dstStride, int bmpW, int bmpH,
        DrawingLayer group, IReadOnlyList<DrawingLayer> children,
        double opacityScale, PixelRegion tr, int ox, int oy)
    {
        if (children.Count == 0) return;
        var groupOpacity = group.Opacity * opacityScale;
        if (groupOpacity <= 0) return;

        if (group.BlendMode == BlendMode.PassThrough)
        {
            Projection.CompositeSiblingList(dst, dstStride, bmpW, bmpH, children, groupOpacity, tr, ox, oy);
            return;
        }

        var tempLen = tr.Width * tr.Height * 4;
        var temp = ArrayPool<byte>.Shared.Rent(tempLen);
        try
        {
            Array.Clear(temp, 0, tempLen);
            fixed (byte* tp = temp)
            {
                Projection.CompositeSiblingList(tp, tr.Width * 4, bmpW, bmpH, children, 1.0, tr, ox, oy);
                LayerCompositorPixelOps.CompositeBgraBuffer(dst, dstStride, tp, tr.Width * 4,
                    group.BlendMode, groupOpacity, tr, ox, oy);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    // ═══ Test/export helpers ══════════════════════════════════════════════════

    public bool TryReadDisplayPixel(int dx, int dy, out byte b, out byte g, out byte r, out byte a)
    {
        b = g = r = a = 0; if (_tileTotal == 0 || (uint)dx >= (uint)_width || (uint)dy >= (uint)_height) return false;
        var tx = dx / CmpTileSize; var ty = dy / CmpTileSize; var idx = tileIndex(tx, ty); if (idx < 0) return false;
        var bmp = _tileScratch[idx]; if (bmp == null) return false;
        var lx = dx - tx * CmpTileSize; var ly = dy - ty * CmpTileSize;
        if ((uint)lx >= (uint)bmp.Width || (uint)ly >= (uint)bmp.Height) return false;
        unsafe { var ptr = (byte*)bmp.GetPixels().ToPointer(); var off = ly * bmp.RowBytes + lx * 4; b = ptr[off]; g = ptr[off + 1]; r = ptr[off + 2]; a = ptr[off + 3]; }
        return a != 0;
    }

    public unsafe byte[] CompositeToBgra(IReadOnlyList<DrawingLayer> layers, int w, int h, uint paperColor = 0)
    {
        lock (CompositeGate) { SetSize(w, h); var buf = new byte[w * h * 4]; if (paperColor != 0) { fixed (byte* d = buf) { var p = (uint*)d; for (var i = 0; i < w * h; i++) p[i] = paperColor; } } var cl = new PixelRegion(0, 0, w, h); var rl = LayerStackComposition.SelectLayersForComposite(layers); fixed (byte* d = buf) Projection.CompositeSiblingList(d, w * 4, w, h, rl, 1.0, cl, 0, 0); return buf; }
    }

    public unsafe Color? SampleCompositePixel(IReadOnlyList<DrawingLayer> layers, int w, int h, int x, int y, uint paperColor = 0)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return null;
        lock (CompositeGate) { SetSize(w, h); var rl = LayerStackComposition.SelectLayersForComposite(layers); var row = new byte[w * 4]; if (paperColor != 0) { fixed (byte* rp = row) { var f = (uint*)rp; for (var i = 0; i < w; i++) f[i] = paperColor; } } var stack = LayerProjectionPlane.BuildSiblingStack(rl); var cl = new PixelRegion(0, y, w, 1); fixed (byte* d = row) Projection.CompositeSiblingStack(d, w * 4, w, 1, stack, 1.0, cl, 0, y); var o = x * 4; var b = row[o]; var g = row[o + 1]; var r = row[o + 2]; var a = row[o + 3]; return a == 0 ? null : Color.FromArgb(a, r, g, b); }
    }

    public unsafe SKBitmap AssembleSkBitmap(int ow, int oh, int lod = 0, uint paperColor = 0)
    {
        if (ow <= 0 || oh <= 0) throw new ArgumentOutOfRangeException(nameof(ow));
        var bmp = new SKBitmap(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var d = (byte*)bmp.GetPixels().ToPointer(); var ds = bmp.RowBytes;
        if (paperColor != 0) { var p = (uint*)d; for (var i = 0; i < ow * oh; i++) p[i] = paperColor; } else new Span<byte>(d, bmp.ByteCount).Clear();
        lock (CompositeGate) { for (var ti = 0; ti < _tileTotal; ti++) { var sb = _tileScratch[ti]; if (sb == null) continue; var ty = ti / _xtiles; var tx = ti % _xtiles; var dx = tx * CmpTileSize; var dy = ty * CmpTileSize; if (dx >= ow || dy >= oh) continue; var s = (byte*)sb.GetPixels().ToPointer(); var tw = Math.Min(sb.Width, ow - dx); var th = Math.Min(sb.Height, oh - dy); if (tw <= 0 || th <= 0) continue; for (var y = 0; y < th; y++) Buffer.MemoryCopy(s + y * sb.RowBytes, d + (dy + y) * ds + dx * 4, tw * 4, tw * 4); } }
        return bmp;
    }

    public static int CountTilesForRegion(PixelRegion r, int lod = 0) { if (r.IsEmpty) return 0; var a = new TileArea(FloorDiv(r.X, 64), FloorDiv(r.Y, 64), FloorDiv(r.Right - 1, 64), FloorDiv(r.Bottom - 1, 64)); return a.IsEmpty ? 0 : (a.Right - a.Left + 1) * (a.Bottom - a.Top + 1); }
    private static int FloorDiv(int v, int d) => LayerCompositorPixelOps.FloorDiv(v, d);

    // ═══ MergeHost ════════════════════════════════════════════════════════════

    private sealed class MergeHost(LayerCompositor o) : ILayerMergeHost
    {
        public unsafe void CompositePaintLayer(byte* d, int ds, int w, int h, DrawingLayer l, double op, PixelRegion c, int ox, int oy, BlendMode? blendModeOverride = null, double? opacityOverride = null) => LayerCompositorPixelOps.CompositeLayer(d, ds, w, h, l, op, c, ox, oy, blendModeOverride, opacityOverride);
        public unsafe void CompositeProjectionBuffer(byte* d, int ds, TiledPixelBuffer p, BlendMode b, double op, PixelRegion c, int ox, int oy) => LayerCompositorPixelOps.CompositeProjectionBuffer(d, ds, p, b, op, c, ox, oy);
        public unsafe void CompositeClippedPaintLayer(byte* d, int ds, int w, int h, DrawingLayer l, DrawingLayer bl, double op, PixelRegion c, int ox, int oy) => LayerCompositorPixelOps.CompositeClippedLayer(d, ds, w, h, l, bl, op, c, ox, oy);
        public unsafe void CompositeClippedGroupIntoBuffer(byte* d, int ds, int w, int h, DrawingLayer g, DrawingLayer bl, double op, PixelRegion c, int ox, int oy) => o.CompositeClippedGroup(d, ds, w, h, g, bl, op, c, ox, oy);
    }

    private unsafe void CompositeClippedGroup(byte* d, int ds, int w, int h, DrawingLayer g, DrawingLayer bl, double op, PixelRegion c, int ox, int oy)
    {
        var go = g.Opacity * op; if (go <= 0 || g.Children.Count == 0) return;
        var tmp = ArrayPool<byte>.Shared.Rent(c.Width * c.Height * 4);
        try
        {
            Array.Clear(tmp, 0, c.Width * c.Height * 4);
            fixed (byte* tp = tmp)
            {
                if (g.BlendMode == BlendMode.PassThrough) Projection.CompositeSiblingList(tp, c.Width * 4, c.Width, c.Height, g.Children, go, c, c.X, c.Y);
                else Projection.CompositeGroupNode(tp, c.Width * 4, c.Width, c.Height, g, op, c, c.X, c.Y);
                LayerCompositorPixelOps.CompositeClippedBuffer(d, ds, w, h, tp, c.Width * 4, bl, g.BlendMode, c, ox, oy);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }
}

internal sealed class SkiaTileDrawOp : ICustomDrawOperation
{
    private readonly SKImage? _image;
    private readonly bool _ownsImage;
    private readonly SKRect _src;
    private readonly Rect _dest;
    public SkiaTileDrawOp(SKBitmap bmp, SKRect src, Rect dest) { _image = SKImage.FromBitmap(bmp); _ownsImage = true; _src = src; _dest = dest; }
    public SkiaTileDrawOp(SKImage image, SKRect src, Rect dest) { _image = image; _ownsImage = false; _src = src; _dest = dest; }
    public Rect Bounds => _dest;
    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? o) =>
        o is SkiaTileDrawOp other &&
        _image == other._image &&
        _src == other._src &&
        _dest == other._dest;
    public void Dispose() { if (_ownsImage) _image?.Dispose(); }
    public void Render(ImmediateDrawingContext ctx)
    {
        if (_image == null) return;
        var lease = ctx.TryGetFeature<Avalonia.Skia.ISkiaSharpApiLeaseFeature>()?.Lease();
        if (lease == null) return;
        using (lease)
        {
            var dr = new SKRect((float)_dest.X, (float)_dest.Y, (float)_dest.Right, (float)_dest.Bottom);
            lease.SkCanvas.DrawImage(_image, _src, dr, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
    }
}
