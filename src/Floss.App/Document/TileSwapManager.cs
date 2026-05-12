using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Floss.App.Document;

/// <summary>
/// Global tile swap manager. When total compressed tile memory across all buffers
/// exceeds a threshold, least-recently-used tiles are written to a scratch disk
/// (SSD) and evicted from RAM. Tiles are read back on demand.
/// </summary>
public static class TileSwapManager
{
    private static readonly string ScratchDir;
    private static long _totalCompressedBytes;
    private static long _totalSwappedBytes;
    private static long _nextBufferId;
    private static readonly Dictionary<(TiledPixelBuffer Buffer, int X, int Y), SwapEntry> SwappedTiles = new();
    private static readonly LinkedList<SwapEntry> LruList = new();
    private static readonly ReaderWriterLockSlim Lock = new();

    // Default: swap when compressed tile RAM exceeds 400 MB.
    // A 15k×15k canvas = ~225M pixels = ~55k tiles = ~880 MB raw.
    // With typical sparse art, compressed tiles are ~1-4 KB each.
    // 400 MB threshold = ~100k-400k compressed tiles in RAM before swapping.
    public static long MemoryThreshold { get; set; } = 400L * 1024 * 1024;

    static TileSwapManager()
    {
        ScratchDir = Path.Combine(Path.GetTempPath(), "floss_scratch");
        try { Directory.CreateDirectory(ScratchDir); } catch { }
    }

    public static long TotalCompressedBytes
    {
        get { Lock.EnterReadLock(); try { return _totalCompressedBytes; } finally { Lock.ExitReadLock(); } }
    }

    public static long TotalSwappedBytes
    {
        get { Lock.EnterReadLock(); try { return _totalSwappedBytes; } finally { Lock.ExitReadLock(); } }
    }

    /// <summary>Register a buffer and return its scratch subdirectory.</summary>
    public static string RegisterBuffer(TiledPixelBuffer buffer)
    {
        var id = Interlocked.Increment(ref _nextBufferId);
        var dir = Path.Combine(ScratchDir, id.ToString());
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>Unregister a buffer, deleting all its scratch files.</summary>
    public static void UnregisterBuffer(TiledPixelBuffer buffer, string scratchDir)
    {
        Lock.EnterWriteLock();
        try
        {
            var toRemove = SwappedTiles.Keys.Where(k => k.Buffer == buffer).ToList();
            foreach (var key in toRemove)
            {
                if (SwappedTiles.Remove(key, out var entry))
                {
                    LruList.Remove(entry);
                    _totalSwappedBytes -= entry.FileSize;
                    try { File.Delete(entry.FilePath); } catch { }
                }
            }
        }
        finally { Lock.ExitWriteLock(); }

        try
        {
            if (Directory.Exists(scratchDir))
                Directory.Delete(scratchDir, recursive: true);
        }
        catch { }
    }

    /// <summary>Try to swap a tile to disk. Returns true if successful.</summary>
    public static bool TrySwapToDisk(TiledPixelBuffer buffer, int tx, int ty, byte[] compressedData, string scratchDir)
    {
        if (compressedData == null || compressedData.Length == 0) return false;

        var filePath = Path.Combine(scratchDir, $"{tx}_{ty}.tile");
        try
        {
            File.WriteAllBytes(filePath, compressedData);
        }
        catch
        {
            return false;
        }

        Lock.EnterWriteLock();
        try
        {
            var key = (buffer, tx, ty);
            if (SwappedTiles.TryGetValue(key, out var existing))
            {
                LruList.Remove(existing);
                _totalSwappedBytes -= existing.FileSize;
            }

            var entry = new SwapEntry(buffer, tx, ty, filePath, compressedData.Length);
            SwappedTiles[key] = entry;
            LruList.AddLast(entry);
            _totalSwappedBytes += compressedData.Length;
            return true;
        }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Read a tile back from disk. Returns null if not found or failed.</summary>
    public static byte[]? TryReadFromDisk(TiledPixelBuffer buffer, int tx, int ty)
    {
        Lock.EnterUpgradeableReadLock();
        try
        {
            var key = (buffer, tx, ty);
            if (!SwappedTiles.TryGetValue(key, out var entry))
                return null;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(entry.FilePath);
            }
            catch
            {
                return null;
            }

            Lock.EnterWriteLock();
            try
            {
                // Move to end of LRU (most recently used)
                LruList.Remove(entry);
                LruList.AddLast(entry);
            }
            finally { Lock.ExitWriteLock(); }

            return data;
        }
        finally { Lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Remove a tile from disk tracking (e.g. when tile is deleted or cleared).</summary>
    public static void RemoveFromDisk(TiledPixelBuffer buffer, int tx, int ty)
    {
        Lock.EnterWriteLock();
        try
        {
            var key = (buffer, tx, ty);
            if (SwappedTiles.Remove(key, out var entry))
            {
                LruList.Remove(entry);
                _totalSwappedBytes -= entry.FileSize;
                try { File.Delete(entry.FilePath); } catch { }
            }
        }
        finally { Lock.ExitWriteLock(); }
    }

    /// <summary>Evict tiles to disk until we're under the memory threshold.
    /// Call this after adding compressed tiles to RAM.
    /// Returns bytes evicted.
    /// </summary>
    public static long EvictIfNeeded()
    {
        long evicted = 0;

        while (true)
        {
            SwapEntry? victim = null;
            long currentBytes;

            Lock.EnterReadLock();
            try
            {
                currentBytes = _totalCompressedBytes;
                if (currentBytes <= MemoryThreshold || LruList.Count == 0)
                    break;

                // Find oldest entry that still has compressed data in RAM
                foreach (var entry in LruList)
                {
                    if (entry.Buffer.HasCompressedTile(entry.X, entry.Y))
                    {
                        victim = entry;
                        break;
                    }
                }
            }
            finally { Lock.ExitReadLock(); }

            if (victim == null) break;

            // Ask the buffer to evict this tile to disk
            var size = victim.Buffer.TryEvictTileToDisk(victim.X, victim.Y);
            if (size > 0)
            {
                evicted += size;
            }
            else
            {
                // Can't evict, remove from tracking
                Lock.EnterWriteLock();
                try
                {
                    var key = (victim.Buffer, victim.X, victim.Y);
                    if (SwappedTiles.Remove(key, out var removed))
                    {
                        LruList.Remove(removed);
                        _totalSwappedBytes -= removed.FileSize;
                    }
                }
                finally { Lock.ExitWriteLock(); }
            }
        }

        return evicted;
    }

    public static void ReportCompressedBytes(long delta)
    {
        Interlocked.Add(ref _totalCompressedBytes, delta);
    }

    private sealed class SwapEntry(TiledPixelBuffer buffer, int x, int y, string filePath, int fileSize)
    {
        public TiledPixelBuffer Buffer { get; } = buffer;
        public int X { get; } = x;
        public int Y { get; } = y;
        public string FilePath { get; } = filePath;
        public int FileSize { get; } = fileSize;
    }
}
