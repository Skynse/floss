namespace Floss.App.Canvas.Compositing;

/// <summary>
/// Configuration for the layer compositor. All magic numbers extracted here.
/// </summary>
public sealed class CompositorConfig
{
    public static CompositorConfig Default { get; } = new();

    /// <summary>Max dirty tiles composited per frame (DirtyTileBudget).</summary>
    public int DirtyTileBudget { get; set; } = 32;

    /// <summary>Max missing tiles loaded per frame.</summary>
    public int MaxMissingTilesPerFrame { get; set; } = 96;

    /// <summary>Max composite cache tiles before LRU trim.</summary>
    public int MaxCompositeCacheTiles { get; set; } = 8192;

    /// <summary>Compositor tile size in pixels (64).</summary>
    public int CmpTileSize { get; } = 64;

    /// <summary>Max cell dimension for display (16384).</summary>
    public int MaxCellDimension { get; set; } = 16384;

    /// <summary>LRU trim target as percentage of max cache tiles.</summary>
    public int LruTrimTargetPercent { get; set; } = 75;

    /// <summary>Delayed dispose frame count for GPU resources.</summary>
    public int DelayedDisposeFrames { get; set; } = 8;
}
