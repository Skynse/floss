using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// Bucket-based memory pool for tile-sized (16384 byte) allocations.
/// Modeled on Drawpile's DP_MemoryPool: free-list with lazy bucket expansion.
/// Eliminates GC pressure from per-tile `new byte[tileBytes]` during strokes.
/// Thread-safe via lock-free ConcurrentStack.
/// </summary>
public static class TileMemoryPool
{
    /// <summary>16384 bytes = 64×64×4 BGRA</summary>
    public const int TileByteSize = 64 * 64 * 4;

    // Number of tiles per allocation bucket (1024 tiles = ~16 MB per bucket)
    private const int BucketSize = 1024;

    private static readonly ConcurrentStack<byte[]> FreeList = new();
    private static int _allocatedCount;

    /// <summary>Rent a tile-sized byte array from the pool. Zeroed on return.</summary>
    public static byte[] Rent()
    {
        if (FreeList.TryPop(out var tile))
        {
            Array.Clear(tile);
            return tile;
        }

        Interlocked.Increment(ref _allocatedCount);
        return GC.AllocateArray<byte>(TileByteSize, pinned: false);
    }

    /// <summary>Rent without zeroing — caller guarantees full overwrite.</summary>
    public static byte[] RentUnsafe()
    {
        if (FreeList.TryPop(out var tile))
            return tile;

        Interlocked.Increment(ref _allocatedCount);
        return GC.AllocateArray<byte>(TileByteSize, pinned: false);
    }

    /// <summary>Return a tile-sized byte array to the pool. Must be exactly TileByteSize length.</summary>
    public static void Return(byte[] tile)
    {
        if (tile == null || tile.Length != TileByteSize)
            throw new ArgumentException($"Tile must be exactly {TileByteSize} bytes");

        FreeList.Push(tile);
    }

    /// <summary>Return multiple tiles at once (more efficient).</summary>
    public static void ReturnMany(byte[][] tiles)
    {
        FreeList.PushRange(tiles);
    }

    /// <summary>Clear the pool, freeing all cached tiles. Use on document close.</summary>
    public static void Clear()
    {
        FreeList.Clear();
        Interlocked.Exchange(ref _allocatedCount, 0);
    }

    /// <summary>Debug statistics.</summary>
    public static (int free, int allocated) Stats => (FreeList.Count, Volatile.Read(ref _allocatedCount));
}
