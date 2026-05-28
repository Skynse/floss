using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

/// <summary>
/// Translates the active layer by shifting pixel data within the tile buffer.
/// Uses the same pattern as the selection Transform tool: capture → erase
/// old position → write at new position → tile-level undo. The compositor
/// sees actual tile mutations and invalidates correctly at any LOD.
/// </summary>
public sealed class MoveLayerOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;

    private int _origOffsetX;
    private int _origOffsetY;
    private bool _hasPreview;

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;
        var layer = ctx.ActiveLayer;
        if (layer == null) return;

        if (!_hasPreview)
        {
            _origOffsetX = layer.OffsetX;
            _origOffsetY = layer.OffsetY;
            _hasPreview = true;
        }

        int dx = (int)(drag.Current.X - drag.Start.X);
        int dy = (int)(drag.Current.Y - drag.Start.Y);

        // Preview uses offset for speed — compositor reads the offset during
        // compositing, so no tile mutations needed during live drag.
        layer.OffsetX = _origOffsetX + dx;
        layer.OffsetY = _origOffsetY + dy;
        layer.MarkThumbnailDirty();

        var oldContent = layer.Pixels.ContentTileBounds.Translate(_origOffsetX, _origOffsetY);
        var newContent = layer.Pixels.ContentTileBounds.Translate(
            _origOffsetX + dx, _origOffsetY + dy);
        ctx.Document.NotifyChanged(oldContent.Union(newContent), ctx.ActiveLayerIndex);
        ctx.InvalidateRender();
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;
        var layer = ctx.ActiveLayer;
        if (layer == null) return;

        if (!_hasPreview)
        {
            _origOffsetX = layer.OffsetX;
            _origOffsetY = layer.OffsetY;
        }

        int dx = (int)(drag.Current.X - drag.Start.X);
        int dy = (int)(drag.Current.Y - drag.Start.Y);

        if (dx == 0 && dy == 0)
        {
            _hasPreview = false;
            return;
        }

        // Shift pixels within the layer buffer by the drag delta.
        // The offset stays at its original value — no offset history needed,
        // undoing the tile mutations alone restores the correct state.
        ShiftPixels(layer, dx, dy, ctx);
        layer.OffsetX = _origOffsetX;
        layer.OffsetY = _origOffsetY;

        _hasPreview = false;
    }

    public void Cancel()
    {
        _hasPreview = false;
    }

    private static void ShiftPixels(DrawingLayer layer, int dx, int dy, ToolContext ctx)
    {
        var contentBounds = layer.Pixels.ContentTileBounds;
        if (contentBounds.IsEmpty)
            return;

        // Expand capture regions to whole tiles — CaptureTiles works at tile
        // granularity and we need undo coverage for every touched tile.
        const int ts = TiledPixelBuffer.TileSize;

        int oldX = contentBounds.X;
        int oldY = contentBounds.Y;
        int oldW = contentBounds.Width;
        int oldH = contentBounds.Height;

        var oldTileRegion = new PixelRegion(
            FloorDown(oldX, ts),
            FloorDown(oldY, ts),
            AlignUp(oldX + oldW, ts) - FloorDown(oldX, ts),
            AlignUp(oldY + oldH, ts) - FloorDown(oldY, ts));

        int newX = oldX + dx;
        int newY = oldY + dy;

        var newTileRegion = new PixelRegion(
            FloorDown(newX, ts),
            FloorDown(newY, ts),
            AlignUp(newX + oldW, ts) - FloorDown(newX, ts),
            AlignUp(newY + oldH, ts) - FloorDown(newY, ts));

        // 1. Capture all tiles in old + new regions (before any mutation).
        var beforeTiles = layer.CaptureTiles(oldTileRegion);
        var destBefore = layer.CaptureTiles(newTileRegion);

        // 2. Capture content pixels as flat BGRA.
        var captured = layer.Pixels.Capture(contentBounds);

        // 3. Write shifted pixels at the new position.
        var newContentBounds = new PixelRegion(newX, newY, oldW, oldH);
        layer.Pixels.Restore(newContentBounds, captured);

        // 4. Erase the old position.
        var zeroBuf = new byte[oldW * oldH * 4];
        layer.Pixels.Restore(contentBounds, zeroBuf);

        // 5. Prune fully-transparent tiles from both old and new areas.
        layer.Pixels.PruneRegion(oldTileRegion);
        layer.Pixels.PruneRegion(newTileRegion);

        // 6. Merge before-tile dictionaries for undo.
        var allBefore = new Dictionary<(int X, int Y), byte[]?>(beforeTiles);
        foreach (var kv in destBefore)
            allBefore[kv.Key] = kv.Value;

        // 7. Push tile-level undo. The compositor invalidates tiles overlapping
        //    the combined dirty region — which covers both old and new positions
        //    regardless of viewport (fixed in LayerCompositor.Invalidate).
        ctx.Document.CommitLayerTileMutations(
            [new LayerTileMutation(ctx.ActiveLayerIndex, allBefore, oldTileRegion.Union(newTileRegion))]);

        layer.MarkThumbnailDirty();
    }

    private static int FloorDown(int v, int d) => v >= 0 ? (v / d) * d : ((v - d + 1) / d) * d;
    private static int AlignUp(int v, int d) => ((v + d - 1) / d) * d;
}
