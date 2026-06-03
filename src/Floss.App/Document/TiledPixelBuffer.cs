using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Floss.App.Canvas.Engine;
using SkiaSharp;

namespace Floss.App.Document;

public sealed class TiledPixelBuffer : IDisposable
{
    public const int TileSize = 64;
    private const int BytesPerPixel = 4;
    private const int TileBytes = TileSize * TileSize * BytesPerPixel;
    private static readonly object SolidTileTemplateLock = new();
    private static readonly Dictionary<uint, byte[]> SolidTileTemplates = [];

    // Hot raw tiles.
    private readonly Dictionary<(int X, int Y), byte[]> _tiles = [];
    // Cold compressed tiles — swapped out to reduce memory.
    private readonly Dictionary<(int X, int Y), byte[]> _compressed = [];
    // Copy-on-write refcounts. Absent key means rf=0 (mutable in place).
    private readonly Dictionary<(int X, int Y), int> _refs = [];
    // Pre-clone pool (Krita: m_clonesStack). When refcount 1→2, a copy
    // is pre-allocated so CowClone can use it without Rent + BlockCopy.
    private readonly Dictionary<(int X, int Y), Queue<byte[]>> _clones = [];

    private readonly string _scratchDir;
    private readonly object _lock = new();
    private readonly ReaderWriterLockSlim _pixelLock = new(LockRecursionPolicy.NoRecursion);
    private long _compressedBytes;
    private bool _disposed;

    // Bumped whenever the tile-key set changes (add/remove from _tiles or
    // _compressed). Lets ContentTileBounds cache its result and short-circuit
    // on the hot composite path — the getter was previously walking every
    // tile dictionary on every call, which scaled to hundreds of ms per
    // composite pass on a fully painted 4k×4k document. The version is
    // bumped under _lock by InvalidateTileKeyCache so writers don't have to
    // remember to do it manually.
    private long _tileKeyVersion;
    private long _cachedBoundsVersion = -1;
    private PixelRegion _cachedContentBounds;

    /// <summary>Bump the tile-key version. Must be called inside <c>_lock</c>.</summary>
    private void InvalidateTileKeyCache() => _tileKeyVersion++;

    private void Retain((int X, int Y) key, byte[]? data)
    {
        lock (_lock)
        {
            _refs.TryGetValue(key, out var n);
            _refs[key] = n + 1;
            // Pre-clone (Krita: pooler fills m_clonesStack): when refcount goes
            // 1→2 (first share), pre-copy the data so CowClone gets a hit.
            if (n == 1 && data != null)
            {
                var clone = TileMemoryPool.RentUnsafe();
                Buffer.BlockCopy(data, 0, clone, 0, TileBytes);
                if (!_clones.TryGetValue(key, out var stack))
                    _clones[key] = stack = new Queue<byte[]>(2);
                stack.Enqueue(clone);
            }
        }
    }

    internal void ReleaseRef((int X, int Y) key)
    {
        lock (_lock)
        {
            if (!_refs.TryGetValue(key, out var n) || n <= 1)
            {
                _refs.Remove(key);
                if (_clones.TryGetValue(key, out var stack))
                {
                    while (stack.Count > 0)
                        TileMemoryPool.Return(stack.Dequeue());
                    _clones.Remove(key);
                }
            }
            else
                _refs[key] = n - 1;
        }
    }

    internal void ReleaseCapturedRefs(IReadOnlyDictionary<(int X, int Y), byte[]?> captured)
    {
        if (captured.Count == 0) return;
        lock (_lock)
        {
            foreach (var key in captured.Keys)
            {
                if (!_refs.TryGetValue(key, out var n) || n <= 1)
                {
                    _refs.Remove(key);
                    if (_clones.TryGetValue(key, out var stack))
                    {
                        while (stack.Count > 0)
                            TileMemoryPool.Return(stack.Dequeue());
                        _clones.Remove(key);
                    }
                }
                else
                    _refs[key] = n - 1;
            }
        }
    }

    private bool IsShared((int X, int Y) key)
    {
        lock (_lock)
            return _refs.ContainsKey(key);
    }

    private byte[] CowClone((int X, int Y) key, byte[] raw)
    {
        byte[] clone;
        lock (_lock)
        {
            if (!_refs.ContainsKey(key)) return raw;
            // Pre-clone hit (Krita: m_clonesStack.pop): no Rent + no BlockCopy
            if (_clones.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                clone = stack.Dequeue();
                if (stack.Count == 0) _clones.Remove(key);
            }
            else
            {
                clone = TileMemoryPool.RentUnsafe();
                Buffer.BlockCopy(raw, 0, clone, 0, TileBytes);
            }
            _tiles[key] = clone;
            InvalidateTileKeyCache();
        }
        ReleaseRef(key);
        return clone;
    }

    public TiledPixelBuffer(int width, int height)
    {
        MinX = 0;
        MinY = 0;
        MaxX = Math.Max(1, width);
        MaxY = Math.Max(1, height);
        _scratchDir = TileSwapManager.RegisterBuffer(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TileSwapManager.UnregisterBuffer(this, _scratchDir);
        lock (_lock)
        {
            foreach (var raw in _tiles.Values)
                TileMemoryPool.Return(raw);
            _tiles.Clear();
            _compressed.Clear();
            _refs.Clear();
            foreach (var stack in _clones.Values)
                while (stack.Count > 0)
                    TileMemoryPool.Return(stack.Dequeue());
            _clones.Clear();
        }
        _pixelLock.Dispose();
    }

    // When true, write locks are skipped so brush rendering never blocks waiting
    // for the compositor's read locks. The compositor may read partially-written
    // tile bytes (byte-level data race), but live-stroke preview tearing is
    // invisible in practice and corrected on the next composite frame.
    internal bool LiveStroke { get; set; }

    internal void EnterPixelReadLock() => _pixelLock.EnterReadLock();
    internal void ExitPixelReadLock() => _pixelLock.ExitReadLock();
    internal void EnterPixelWriteLock() { if (!LiveStroke) _pixelLock.EnterWriteLock(); }
    internal void ExitPixelWriteLock() { if (!LiveStroke) _pixelLock.ExitWriteLock(); }

    public int Width => Math.Max(1, MaxX - MinX);
    public int Height => Math.Max(1, MaxY - MinY);
    public PixelRegion Bounds => new(MinX, MinY, Width, Height);
    public int MinX { get; private set; }
    public int MinY { get; private set; }
    public int MaxX { get; private set; }
    public int MaxY { get; private set; }

    public int TileCount => _tiles.Count + _compressed.Count;

    public bool HasCompressedTile(int tx, int ty)
    {
        lock (_lock)
        {
            return _compressed.ContainsKey((tx, ty));
        }
    }

    public long TryEvictTileToDisk(int tx, int ty)
    {
        var key = (tx, ty);
        byte[]? compressed;
        lock (_lock)
        {
            if (!_compressed.TryGetValue(key, out compressed))
                return 0;
        }

        if (!TileSwapManager.TrySwapToDisk(this, tx, ty, compressed, _scratchDir))
            return 0;

        lock (_lock)
        {
            if (_compressed.Remove(key))
            {
                _compressedBytes -= compressed.Length;
                TileSwapManager.ReportCompressedBytes(-compressed.Length);
                InvalidateTileKeyCache();
                return compressed.Length;
            }
        }
        return 0;
    }

    public void Resize(int width, int height)
    {
        MinX = 0;
        MinY = 0;
        MaxX = Math.Max(1, width);
        MaxY = Math.Max(1, height);
        lock (_lock)
        {
            _tiles.Clear();
            _compressed.Clear();
            InvalidateTileKeyCache();
        }
    }

    private void ExtendBounds(int left, int top, int right, int bottom)
    {
        if (left < MinX) MinX = left;
        if (top < MinY) MinY = top;
        if (right > MaxX) MaxX = right;
        if (bottom > MaxY) MaxY = bottom;
    }

    // ── Compression ────────────────────────────────────────────────────────────

    public void CompressTiles()
    {
        List<((int X, int Y) Key, byte[] Copy)> toCompress;
        lock (_lock)
        {
            if (_tiles.Count == 0) return;
            toCompress = new List<((int X, int Y) Key, byte[] Copy)>(_tiles.Count);
            foreach (var (key, tile) in _tiles)
            {
                var copy = new byte[tile.Length];
                Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
                toCompress.Add((key, copy));
            }
            _tiles.Clear();
        }

        var compressedList = new List<((int X, int Y) Key, byte[] Data)>(toCompress.Count);
        foreach (var (key, copy) in toCompress)
        {
            var compressed = Deflate(copy);
            compressedList.Add((key, compressed));
        }

        long addedBytes = 0;
        lock (_lock)
        {
            foreach (var (key, data) in compressedList)
            {
                if (_compressed.ContainsKey(key)) continue;
                _compressed[key] = data;
                addedBytes += data.Length;
            }
            // _tiles → _compressed shift: union set unchanged but bump anyway
            // so a reader that captured an early version is told to recheck.
            InvalidateTileKeyCache();
        }

        if (addedBytes > 0)
        {
            TileSwapManager.ReportCompressedBytes(addedBytes);
            TileSwapManager.EvictIfNeeded();
        }
    }

    private byte[] EnsureRaw((int X, int Y) key)
    {
        byte[]? raw;
        lock (_lock)
        {
            if (_tiles.TryGetValue(key, out raw))
                return raw;

            if (_compressed.TryGetValue(key, out var compressed))
            {
                raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
                _tiles[key] = raw;
                _compressed.Remove(key);
                _compressedBytes -= compressed.Length;
                TileSwapManager.ReportCompressedBytes(-compressed.Length);
                // Union set unchanged but bump so a stale reader recomputes.
                InvalidateTileKeyCache();
                return raw;
            }
        }

        // Not in RAM — check scratch disk
        var fromDisk = TileSwapManager.TryReadFromDisk(this, key.X, key.Y);
        if (fromDisk == null)
            return null!;

        raw = IsProbablyDeflated(fromDisk) ? Inflate(fromDisk) : fromDisk;

        lock (_lock)
        {
            _tiles[key] = raw;
            InvalidateTileKeyCache();
        }
        return raw;
    }

    private static bool IsProbablyDeflated(byte[] data)
        => data.Length != TileBytes;

    private static byte[] GetSolidTileTemplate(byte b, byte g, byte r, byte a)
    {
        var key = (uint)(b | (g << 8) | (r << 16) | (a << 24));
        lock (SolidTileTemplateLock)
        {
            if (SolidTileTemplates.TryGetValue(key, out var template))
                return template;

            var raw = TileMemoryPool.Rent();
            for (var offset = 0; offset < raw.Length; offset += BytesPerPixel)
            {
                raw[offset + 0] = b;
                raw[offset + 1] = g;
                raw[offset + 2] = r;
                raw[offset + 3] = a;
            }

            template = Deflate(raw);
            SolidTileTemplates[key] = template;
            return template;
        }
    }

    // ── Tile lookup ────────────────────────────────────────────────────────────

    public byte[]? GetTileOrNull(int tileX, int tileY)
    {
        var key = (tileX, tileY);
        lock (_lock)
        {
            if (_tiles.TryGetValue(key, out var raw))
                return raw;
            if (!_compressed.ContainsKey(key))
                return null;
        }
        return EnsureRaw(key);
    }

    public byte[] GetOrCreateRawTile(int tileX, int tileY)
    {
        var key = (tileX, tileY);
        var raw = EnsureRaw(key);
        if (raw != null) return CowClone(key, raw);

        raw = TileMemoryPool.Rent();
        lock (_lock)
        {
            _tiles[key] = raw;
            _compressed.Remove(key);
            InvalidateTileKeyCache();
        }
        ExtendBounds(tileX * TileSize, tileY * TileSize, (tileX + 1) * TileSize, (tileY + 1) * TileSize);
        return raw;
    }

    public void PruneRegion(PixelRegion region) => PruneTransparentTiles(region);

    public void PrimeTiles(PixelRegion region)
    {
        if (region.IsEmpty) return;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                bool needsDecompress;
                lock (_lock)
                {
                    if (_tiles.ContainsKey(key)) continue;
                    needsDecompress = _compressed.ContainsKey(key);
                }
                if (needsDecompress)
                    EnsureRaw(key);
            }
        }
    }

    // ── Clear ──────────────────────────────────────────────────────────────────

    public void Clear()
    {
        lock (_lock)
        {
            _tiles.Clear();
            _compressed.Clear();
            _refs.Clear();
            foreach (var stack in _clones.Values)
                while (stack.Count > 0)
                    TileMemoryPool.Return(stack.Dequeue());
            _clones.Clear();
            InvalidateTileKeyCache();
        }
    }

    public void Clear(PixelRegion region)
    {
        if (region.IsEmpty) return;

        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var row = (y - tileY * TileSize) * TileSize * BytesPerPixel + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Array.Clear(tile, row, tileRegion.Width * BytesPerPixel);
            }
        }, create: false);

        PruneTransparentTiles(region);
    }

    public void FillSolid(PixelRegion region, byte b, byte g, byte r, byte a)
    {
        if (region.IsEmpty) return;
        if (a == 0)
        {
            Clear(region);
            return;
        }

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);
        var fullTileTemplate = GetSolidTileTemplate(b, g, r, a);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                var tileRegion = new PixelRegion(tx * TileSize, ty * TileSize, TileSize, TileSize).Intersect(region);
                if (tileRegion.IsEmpty) continue;

                if (tileRegion.Width == TileSize && tileRegion.Height == TileSize)
                {
                    lock (_lock)
                    {
                        if (_tiles.TryGetValue(key, out var existingRaw))
                            TileMemoryPool.Return(existingRaw);
                        _tiles.Remove(key);
                        _compressed[key] = fullTileTemplate;
                        InvalidateTileKeyCache();
                    }
                    ExtendBounds(tx * TileSize, ty * TileSize, (tx + 1) * TileSize, (ty + 1) * TileSize);
                    continue;
                }

                var raw = EnsureRaw(key);
                if (raw == null)
                {
                    raw = TileMemoryPool.Rent();
                    lock (_lock)
                    {
                        _tiles[key] = raw;
                        _compressed.Remove(key);
                        InvalidateTileKeyCache();
                    }
                    ExtendBounds(tx * TileSize, ty * TileSize, (tx + 1) * TileSize, (ty + 1) * TileSize);
                }

                for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
                {
                    var offset = (y - ty * TileSize) * TileSize * BytesPerPixel
                               + (tileRegion.X - tx * TileSize) * BytesPerPixel;
                    for (var x = 0; x < tileRegion.Width; x++, offset += BytesPerPixel)
                    {
                        raw[offset + 0] = b;
                        raw[offset + 1] = g;
                        raw[offset + 2] = r;
                        raw[offset + 3] = a;
                    }
                }
            }
        }
    }

    // ── Capture / Restore ──────────────────────────────────────────────────────

    public byte[] Capture(PixelRegion region)
    {
        if (region.IsEmpty) return [];

        var bytes = new byte[region.Width * region.Height * BytesPerPixel];
        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                var dstOffset = (y - region.Y) * region.Width * BytesPerPixel
                              + (tileRegion.X - region.X) * BytesPerPixel;
                Buffer.BlockCopy(tile, srcOffset, bytes, dstOffset, tileRegion.Width * BytesPerPixel);
            }
        }, create: false);

        return bytes;
    }

    public Dictionary<(int X, int Y), byte[]?> CaptureTiles(PixelRegion region)
    {
        var result = new Dictionary<(int X, int Y), byte[]?>();
        CaptureTiles(region, result);
        return result;
    }

    public void CaptureTiles(PixelRegion region, Dictionary<(int X, int Y), byte[]?> target)
    {
        if (region.IsEmpty) return;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                if (target.ContainsKey(key)) continue;

                var raw = EnsureRaw(key);
                target.Add(key, raw);
                if (raw != null)
                    Retain(key, raw);
            }
        }
    }

    public byte[]? CaptureTile(int tileX, int tileY)
    {
        var key = (tileX, tileY);
        var raw = EnsureRaw(key);
        if (raw != null)
        {
            Retain(key, raw);
            return raw;
        }
        return null;
    }

    public void Restore(PixelRegion region, byte[] bytes)
    {
        if (region.IsEmpty || bytes.Length == 0) return;

        var expected = region.Width * region.Height * BytesPerPixel;
        if (bytes.Length != expected)
        {
            var actualHeight = bytes.Length / (region.Width * BytesPerPixel);
            if (actualHeight <= 0) return;
            region = new PixelRegion(region.X, region.Y, region.Width, Math.Min(region.Height, actualHeight));
            if (region.IsEmpty) return;
        }

        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - region.Y) * region.Width * BytesPerPixel
                              + (tileRegion.X - region.X) * BytesPerPixel;
                var dstOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Buffer.BlockCopy(bytes, srcOffset, tile, dstOffset, tileRegion.Width * BytesPerPixel);
            }
        }, create: true);

        PruneTransparentTiles(region);
    }

    public void RestoreTile(int tileX, int tileY, byte[]? bytes)
    {
        var key = (tileX, tileY);
        if (bytes == null || IsTransparent(bytes))
        {
            lock (_lock)
            {
                _tiles.Remove(key);
                _compressed.Remove(key);
                InvalidateTileKeyCache();
            }
            return;
        }

        var raw = EnsureRaw(key);
        if (raw == null || raw.Length != bytes.Length)
        {
            raw = TileMemoryPool.Rent();
            lock (_lock)
            {
                _tiles[key] = raw;
                _compressed.Remove(key);
                InvalidateTileKeyCache();
            }
            ExtendBounds(tileX * TileSize, tileY * TileSize, (tileX + 1) * TileSize, (tileY + 1) * TileSize);
        }
        else
        {
            raw = CowClone(key, raw);
        }
        Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
    }

    /// <summary>
    /// Fast tile write for bulk importers. Skips transparent-tile pruning;
    /// callers should prune once after a batch if needed.
    /// </summary>
    internal void ImportTile(int tileX, int tileY, byte[] bytes)
    {
        if (bytes == null) return;

        var key = (tileX, tileY);
        var raw = EnsureRaw(key);
        if (raw == null || raw.Length != bytes.Length)
        {
            raw = new byte[bytes.Length];
            lock (_lock)
            {
                _tiles[key] = raw;
                _compressed.Remove(key);
                InvalidateTileKeyCache();
            }

            ExtendBounds(tileX * TileSize, tileY * TileSize, (tileX + 1) * TileSize, (tileY + 1) * TileSize);
        }

        Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
    }

    // ── Bulk copy ──────────────────────────────────────────────────────────────

    public void CopyFromBgra(byte[] src, int srcWidth, int srcHeight)
    {
        var copyW = Math.Min(srcWidth, Width);
        var copyH = Math.Min(srcHeight, Height);
        if (copyW <= 0 || copyH <= 0) return;

        var lastTileX = (copyW - 1) / TileSize;
        var lastTileY = (copyH - 1) / TileSize;

        for (var ty = 0; ty <= lastTileY; ty++)
        {
            var tileY = ty * TileSize;
            var tileH = Math.Min(TileSize, copyH - tileY);
            for (var tx = 0; tx <= lastTileX; tx++)
            {
                var tileX = tx * TileSize;
                var tileW = Math.Min(TileSize, copyW - tileX);
                if (!HasAnyAlpha(src, srcWidth, tileX, tileY, tileW, tileH)) continue;

                var tile = TileMemoryPool.Rent();
                for (var y = 0; y < tileH; y++)
                {
                    var srcOffset = ((tileY + y) * srcWidth + tileX) * BytesPerPixel;
                    var dstOffset = y * TileSize * BytesPerPixel;
                    Buffer.BlockCopy(src, srcOffset, tile, dstOffset, tileW * BytesPerPixel);
                }

                lock (_lock)
                {
                    _tiles[(tx, ty)] = tile;
                    _compressed.Remove((tx, ty));
                    InvalidateTileKeyCache();
                }
            }
        }
    }

    public void CopyFromBgra(PixelRegion region, byte[] src, int srcStride)
    {
        if (region.IsEmpty) return;

        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            var srcX = tileRegion.X - region.X;
            var srcY = tileRegion.Y - region.Y;
            if (!HasAnyAlpha(src, srcStride / BytesPerPixel, srcX, srcY, tileRegion.Width, tileRegion.Height))
                return;

            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - region.Y) * srcStride + srcX * BytesPerPixel;
                var dstOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Buffer.BlockCopy(src, srcOffset, tile, dstOffset, tileRegion.Width * BytesPerPixel);
            }
        }, create: true);

        PruneTransparentTiles(region);
    }

    // ── Skia rendering ─────────────────────────────────────────────────────────

    public unsafe void RenderWithSkia(PixelRegion region, Action<SKCanvas> render)
    {
        if (region.IsEmpty) return;

        if (!LiveStroke) _pixelLock.EnterWriteLock();
        try
        {
            ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
            {
                fixed (byte* tilePtr = tile)
                {
                    var info = new SKImageInfo(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    using var bitmap = new SKBitmap();
                    if (!bitmap.InstallPixels(info, (IntPtr)tilePtr, TileSize * BytesPerPixel))
                        return;

                    using var canvas = new SKCanvas(bitmap);
                    canvas.Translate(-tileX * TileSize, -tileY * TileSize);
                    canvas.ClipRect(new SKRect(tileRegion.X, tileRegion.Y, tileRegion.Right, tileRegion.Bottom));
                    render(canvas);
                    canvas.Flush();
                }
            }, create: true);
        }
        finally
        {
            if (!LiveStroke) _pixelLock.ExitWriteLock();
        }

        PruneTransparentTiles(region);
    }

    // ── Pixel access ───────────────────────────────────────────────────────────

    public void ReadPixel(int x, int y, byte[] dst, int dstOffset)
    {
        var tileKey = ToTileKey(x, y);
        var tile = EnsureRaw(tileKey);
        if (tile == null)
        {
            dst[dstOffset + 0] = 0;
            dst[dstOffset + 1] = 0;
            dst[dstOffset + 2] = 0;
            dst[dstOffset + 3] = 0;
            return;
        }

        var offset = OffsetInTile(x, y);
        dst[dstOffset + 0] = tile[offset + 0];
        dst[dstOffset + 1] = tile[offset + 1];
        dst[dstOffset + 2] = tile[offset + 2];
        dst[dstOffset + 3] = tile[offset + 3];
    }

    public void WritePixel(int x, int y, byte[] src, int srcOffset)
    {
        var tile = GetOrCreateTile(x, y);
        var offset = OffsetInTile(x, y);
        tile[offset + 0] = src[srcOffset + 0];
        tile[offset + 1] = src[srcOffset + 1];
        tile[offset + 2] = src[srcOffset + 2];
        tile[offset + 3] = src[srcOffset + 3];
    }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
    {
        var tile = GetOrCreateTile(x, y);
        var offset = OffsetInTile(x, y);
        tile[offset + 0] = b;
        tile[offset + 1] = g;
        tile[offset + 2] = r;
        tile[offset + 3] = a;
    }

    public void GetPixel(int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        var tile = EnsureRaw(ToTileKey(x, y));
        if (tile == null)
        {
            b = g = r = a = 0;
            return;
        }

        var offset = OffsetInTile(x, y);
        b = tile[offset + 0];
        g = tile[offset + 1];
        r = tile[offset + 2];
        a = tile[offset + 3];
    }

    // ── Queries ────────────────────────────────────────────────────────────────

    public bool HasNonTransparentPixels(PixelRegion region)
    {
        if (region.IsEmpty) return false;

        var found = false;
        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            if (found) return;

            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var offset = (y - tileY * TileSize) * TileSize * BytesPerPixel + (tileRegion.X - tileX * TileSize) * BytesPerPixel + 3;
                for (var x = 0; x < tileRegion.Width; x++, offset += BytesPerPixel)
                {
                    if (tile[offset] == 0) continue;
                    found = true;
                    return;
                }
            }
        }, create: false);

        return found;
    }

    public PixelRegion ContentTileBounds
    {
        get
        {
            lock (_lock)
            {
                var version = _tileKeyVersion;
                if (_cachedBoundsVersion == version)
                    return _cachedContentBounds;

                var total = _tiles.Count + _compressed.Count;
                if (total == 0)
                {
                    _cachedContentBounds = PixelRegion.Empty;
                    _cachedBoundsVersion = version;
                    return PixelRegion.Empty;
                }

                var first = true;
                var minX = 0;
                var minY = 0;
                var maxX = 0;
                var maxY = 0;

                foreach (var key in _tiles.Keys)
                {
                    var x = key.X * TileSize;
                    var y = key.Y * TileSize;
                    if (first) { minX = x; minY = y; maxX = x + TileSize; maxY = y + TileSize; first = false; }
                    else { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x + TileSize); maxY = Math.Max(maxY, y + TileSize); }
                }
                foreach (var key in _compressed.Keys)
                {
                    var x = key.X * TileSize;
                    var y = key.Y * TileSize;
                    if (first) { minX = x; minY = y; maxX = x + TileSize; maxY = y + TileSize; first = false; }
                    else { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x + TileSize); maxY = Math.Max(maxY, y + TileSize); }
                }

                _cachedContentBounds = new PixelRegion(minX, minY, maxX - minX, maxY - minY);
                _cachedBoundsVersion = version;
                return _cachedContentBounds;
            }
        }
    }

    public PixelRegion ComputeContentBounds()
    {
        var total = TileCount;
        if (total == 0) return PixelRegion.Empty;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        var found = false;

        void CheckTile(int tx, int ty, byte[] tileData)
        {
            var tileBaseX = tx * TileSize;
            var tileBaseY = ty * TileSize;
            for (var y = 0; y < TileSize; y++)
            {
                var py = tileBaseY + y;
                for (var x = 0; x < TileSize; x++)
                {
                    var offset = (y * TileSize + x) * BytesPerPixel + 3;
                    if (tileData[offset] == 0) continue;
                    var px = tileBaseX + x;
                    if (px < minX) minX = px;
                    if (py < minY) minY = py;
                    if (px > maxX) maxX = px;
                    if (py > maxY) maxY = py;
                    found = true;
                }
            }
        }

        // Decompress on demand for bounds check — this is cheap (called rarely).
        lock (_lock)
        {
            foreach (var (key, tile) in _tiles)
                CheckTile(key.X, key.Y, tile);
            foreach (var (key, compressed) in _compressed)
            {
                var raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
                CheckTile(key.X, key.Y, raw);
                _tiles[key] = raw;
                _compressed.Remove(key);
            }
            // _compressed → _tiles bulk move: union unchanged but readers may
            // be tracking _tiles specifically (e.g. cache-warming heuristics).
            InvalidateTileKeyCache();
        }

        return found ? new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1) : PixelRegion.Empty;
    }

    public bool HasContentTiles(PixelRegion region)
    {
        if (region.IsEmpty || TileCount == 0) return false;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                lock (_lock)
                {
                    if (_tiles.ContainsKey(key) || _compressed.ContainsKey(key))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when every 64px tile overlapping <paramref name="region"/> exists
    /// (raw or compressed). Used by the stroke-below compositor cache to detect
    /// regions that were never warmed after FlushFullDirty().
    /// </summary>
    public bool HasTileCoverageForRegion(PixelRegion region)
    {
        if (region.IsEmpty) return false;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        lock (_lock)
        {
            for (var ty = firstTileY; ty <= lastTileY; ty++)
            {
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var key = (tx, ty);
                    if (!_tiles.ContainsKey(key) && !_compressed.ContainsKey(key))
                        return false;
                }
            }
        }

        return true;
    }

    // ── Blend onto flat buffer ─────────────────────────────────────────────────

    public void BlendOnto(byte[] dst, int dstWidth, int dstHeight, double opacity)
    {
        if (opacity <= 0) return;
        int opInt = (int)(opacity * 255 + 0.5);

        List<((int tx, int ty), byte[] tile)> tiles;
        List<((int tx, int ty), byte[] compressed)> compressed;
        lock (_lock)
        {
            tiles = new List<((int tx, int ty), byte[] tile)>(_tiles.Count);
            foreach (var ((tx, ty), tile) in _tiles)
                tiles.Add(((tx, ty), tile));

            compressed = new List<((int tx, int ty), byte[] compressed)>(_compressed.Count);
            foreach (var ((tx, ty), c) in _compressed)
                compressed.Add(((tx, ty), c));
        }

        foreach (var ((tx, ty), tile) in tiles)
            BlendTileOnto(tx, ty, tile, dst, dstWidth, dstHeight, opInt);

        foreach (var ((tx, ty), c) in compressed)
        {
            var raw = IsProbablyDeflated(c) ? Inflate(c) : c;
            BlendTileOnto(tx, ty, raw, dst, dstWidth, dstHeight, opInt);
        }
    }

    private static void BlendTileOnto(int tx, int ty, byte[] tile, byte[] dst, int dstWidth, int dstHeight, int opInt)
    {
        int ox = tx * TileSize;
        int oy = ty * TileSize;
        for (int py = 0; py < TileSize; py++)
        {
            int docY = oy + py;
            if ((uint)docY >= (uint)dstHeight) continue;
            int rowBase = docY * dstWidth;
            int srcRow = py * TileSize;
            for (int px = 0; px < TileSize; px++)
            {
                int docX = ox + px;
                if ((uint)docX >= (uint)dstWidth) continue;
                int srcOff = (srcRow + px) * BytesPerPixel;
                int srcA = tile[srcOff + 3] * opInt / 255;
                if (srcA == 0) continue;
                int dstOff = (rowBase + docX) * BytesPerPixel;
                int dstA = dst[dstOff + 3];
                int outA = srcA + dstA * (255 - srcA) / 255;
                if (outA == 0) continue;
                dst[dstOff + 0] = (byte)((tile[srcOff + 0] * srcA + dst[dstOff + 0] * dstA * (255 - srcA) / 255) / outA);
                dst[dstOff + 1] = (byte)((tile[srcOff + 1] * srcA + dst[dstOff + 1] * dstA * (255 - srcA) / 255) / outA);
                dst[dstOff + 2] = (byte)((tile[srcOff + 2] * srcA + dst[dstOff + 2] * dstA * (255 - srcA) / 255) / outA);
                dst[dstOff + 3] = (byte)outA;
            }
        }
    }

    // ── Bulk capture/restore (file I/O, layer snapshots) ───────────────────────

    public Dictionary<(int X, int Y), byte[]> CaptureTiles()
    {
        var total = TileCount;
        var result = new Dictionary<(int X, int Y), byte[]>(total);
        lock (_lock)
        {
            foreach (var (key, tile) in _tiles)
            {
                var copy = new byte[tile.Length];
                Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
                result[key] = copy;
            }
            foreach (var (key, compressed) in _compressed)
            {
                var raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
                var copy = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
                result[key] = copy;
            }
        }
        return result;
    }

    public void RestoreTiles(Dictionary<(int X, int Y), byte[]> tiles)
    {
        lock (_lock)
        {
            _tiles.Clear();
            _compressed.Clear();
            InvalidateTileKeyCache();
        }

        int? minX = null, minY = null, maxX = null, maxY = null;

        foreach (var (key, tile) in tiles)
        {
            var copy = new byte[tile.Length];
            Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
            lock (_lock)
            {
                _tiles[key] = copy;
                InvalidateTileKeyCache();
            }

            var x = key.X * TileSize;
            var y = key.Y * TileSize;
            minX = minX.HasValue ? Math.Min(minX.Value, x) : x;
            minY = minY.HasValue ? Math.Min(minY.Value, y) : y;
            maxX = maxX.HasValue ? Math.Max(maxX.Value, x + TileSize) : x + TileSize;
            maxY = maxY.HasValue ? Math.Max(maxY.Value, y + TileSize) : y + TileSize;
        }

        if (minX.HasValue)
        {
            MinX = minX!.Value;
            MinY = minY!.Value;
            MaxX = maxX!.Value;
            MaxY = maxY!.Value;
        }
    }

    // ── Internal tile management ───────────────────────────────────────────────

    private byte[] GetOrCreateTile(int x, int y)
    {
        var key = ToTileKey(x, y);
        var raw = EnsureRaw(key);
        if (raw != null) return CowClone(key, raw);

        raw = TileMemoryPool.Rent();
        lock (_lock)
        {
            _tiles[key] = raw;
            _compressed.Remove(key);
            InvalidateTileKeyCache();
        }
        ExtendBounds(key.X * TileSize, key.Y * TileSize, key.X * TileSize + TileSize, key.Y * TileSize + TileSize);
        return raw;
    }

    private void ForEachTile(PixelRegion region, Action<int, int, byte[], PixelRegion> action, bool create)
    {
        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                var raw = EnsureRaw(key);
                if (raw == null)
                {
                    if (!create) continue;
                    raw = TileMemoryPool.Rent();
                    lock (_lock)
                    {
                        _tiles[key] = raw;
                        _compressed.Remove(key);
                        InvalidateTileKeyCache();
                    }
                    ExtendBounds(tx * TileSize, ty * TileSize, (tx + 1) * TileSize, (ty + 1) * TileSize);
                }
                else
                {
                    raw = CowClone(key, raw);
                }

                var tileRegion = new PixelRegion(tx * TileSize, ty * TileSize, TileSize, TileSize).Intersect(region);
                if (!tileRegion.IsEmpty) action(tx, ty, raw, tileRegion);
            }
        }
    }

    private void PruneTransparentTiles(PixelRegion region)
    {
        if (LiveStroke) return;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                lock (_lock)
                {
                    if (_tiles.TryGetValue(key, out var raw) && IsTransparent(raw))
                    {
                        TileMemoryPool.Return(raw);
                        _tiles.Remove(key);
                        InvalidateTileKeyCache();
                    }
                    else if (_compressed.TryGetValue(key, out var compressed))
                    {
                        var decomp = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
                        if (IsTransparent(decomp))
                        {
                            _compressed.Remove(key);
                            InvalidateTileKeyCache();
                        }
                        else
                        {
                            _tiles[key] = decomp;
                            _compressed.Remove(key);
                            InvalidateTileKeyCache();
                        }
                    }
                }
            }
        }
    }

    // ── Compression primitives ─────────────────────────────────────────────────

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream(raw.Length / 4);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(TileBytes);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static unsafe bool IsTransparent(byte[] tile)
    {
        fixed (byte* p = tile)
        {
            var up = (uint*)p;
            var count = tile.Length / BytesPerPixel;
            for (int i = 0; i < count; i++)
            {
                if ((up[i] & 0xFF000000u) != 0) return false;
            }
        }
        return true;
    }

    private static bool HasAnyAlpha(byte[] src, int srcWidth, int x, int y, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var offset = ((y + row) * srcWidth + x) * BytesPerPixel + 3;
            for (var col = 0; col < width; col++, offset += BytesPerPixel)
            {
                if (src[offset] != 0) return true;
            }
        }

        return false;
    }

    private static (int X, int Y) ToTileKey(int x, int y)
        => (FloorDiv(x, TileSize), FloorDiv(y, TileSize));

    private static int OffsetInTile(int x, int y)
    {
        var lx = x - FloorDiv(x, TileSize) * TileSize;
        var ly = y - FloorDiv(y, TileSize) * TileSize;
        return (ly * TileSize + lx) * BytesPerPixel;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0) result--;
        return result;
    }
}
