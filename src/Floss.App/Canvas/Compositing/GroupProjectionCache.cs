using System;
using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// GroupProjectionCache: caches flattened group content per tile.
/// Non-pass-through groups flatten children once, then reapply group blend+opacity.
/// Only dirty tiles are recomposited on invalidation.
/// </summary>
internal sealed class GroupProjectionCache
{
    private readonly Dictionary<(int X, int Y), byte[]> _cachedTiles = [];
    private readonly DrawingLayer _group;
    private PixelRegion? _cleanBounds;

    public GroupProjectionCache(DrawingLayer group)
    {
        _group = group;
    }

    public DrawingLayer Group => _group;
    public int TileCount => _cachedTiles.Count;

    /// <summary>Invalidate cached tiles overlapping the given region.</summary>
    public void Invalidate(PixelRegion? region, bool fullInvalidation = false)
    {
        if (fullInvalidation || region is null)
        {
            _cachedTiles.Clear();
            _cleanBounds = null;
            return;
        }

        var r = region.Value;
        if (_cleanBounds is null || _cleanBounds.Value.Intersect(r).IsEmpty)
            return;

        const int ts = 64; // CmpTileSize
        var left = LayerCompositorPixelOps.FloorDiv(r.X, ts);
        var top = LayerCompositorPixelOps.FloorDiv(r.Y, ts);
        var right = LayerCompositorPixelOps.FloorDiv(r.Right - 1, ts);
        var bottom = LayerCompositorPixelOps.FloorDiv(r.Bottom - 1, ts);

        for (var ty = top; ty <= bottom; ty++)
            for (var tx = left; tx <= right; tx++)
                _cachedTiles.Remove((tx, ty));

        // Shrink clean bounds
        _cleanBounds = null; // conservative: full invalidation after partial
    }

    /// <summary>Check if cache has valid data for the given tile.</summary>
    public bool HasCachedTile(int tx, int ty) => _cachedTiles.ContainsKey((tx, ty));

    /// <summary>Get cached tile data, or null.</summary>
    public byte[]? GetCachedTile(int tx, int ty)
        => _cachedTiles.GetValueOrDefault((tx, ty));

    /// <summary>Store flattened tile data in cache.</summary>
    public void SetCachedTile(int tx, int ty, byte[] tileData)
    {
        _cachedTiles[(tx, ty)] = tileData;
    }

    /// <summary>Clear all cached data.</summary>
    public void Clear()
    {
        _cachedTiles.Clear();
        _cleanBounds = null;
    }

    /// <summary>Mark region as clean.</summary>
    public void MarkClean(PixelRegion region)
    {
        _cleanBounds = _cleanBounds is { } existing ? existing.Union(region) : region;
    }
}
