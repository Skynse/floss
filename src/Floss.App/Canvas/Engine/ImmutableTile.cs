using System;
using System.Threading;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// Reference-counted immutable tile — modeled on Drawpile's DP_Tile.
/// Tile data is read-only once created. To modify, first create a TransientTile
/// copy, mutate, then Publish() to swap. Readers never block on locks.
/// </summary>
public sealed class ImmutableTile
{
    private readonly byte[] _data;
    private int _refCount;

    /// <summary>Create an immutable tile. Takes ownership of the data array.</summary>
    public ImmutableTile(byte[] data)
    {
        _data = data;
        _refCount = 1;
    }

    public byte[] Data => _data;
    public int RefCount => Volatile.Read(ref _refCount);

    public void AddRef() => Interlocked.Increment(ref _refCount);

    /// <summary>Decrements the ref count and returns the data to the pool when it hits zero.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            TileMemoryPool.Return(_data);
        }
    }

    /// <summary>Cheap non-atomic check — use for early-outs only.</summary>
    public bool HasSingleRef => Volatile.Read(ref _refCount) == 1;
}

/// <summary>
/// Mutable tile for in-place editing — modeled on Drawpile's DP_TransientTile.
/// Created by copying an ImmutableTile. After editing, call Persist() to 
/// create a new ImmutableTile snapshot for readers.
/// </summary>
public sealed class TransientTile
{
    public byte[] Data { get; }

    public TransientTile(byte[] data)
    {
        Data = data;
    }

    /// <summary>Creates a mutable copy of an immutable tile (copy-on-write).</summary>
    public static TransientTile FromImmutable(ImmutableTile tile)
    {
        var copy = TileMemoryPool.Rent();
        Array.Copy(tile.Data, copy, TileMemoryPool.TileByteSize);
        return new TransientTile(copy);
    }

    /// <summary>Create a new blank mutable tile (all zeros).</summary>
    public static TransientTile NewBlank()
    {
        return new TransientTile(TileMemoryPool.Rent());
    }

    /// <summary>Publish the mutable tile as an immutable snapshot. Transfers ownership of Data.</summary>
    public ImmutableTile Persist()
    {
        var result = new ImmutableTile(Data);
        // Data ownership transferred — don't reuse this TransientTile
        return result;
    }

    /// <summary>Discard the mutable tile, returning data to the pool.</summary>
    public void Discard()
    {
        TileMemoryPool.Return(Data);
    }
}
