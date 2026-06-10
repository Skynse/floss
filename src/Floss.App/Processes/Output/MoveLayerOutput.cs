using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

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

        ShiftPixels(layer, dx, dy, ctx);
        _hasPreview = false;
    }

    public void Cancel()
    {
        _hasPreview = false;
    }

    /// <summary>
    /// Captures layer content at the current local position, erases old local
    /// tiles, writes captured pixels shifted by (dx, dy), and restores the
    /// original offset. Matches : select → erase
    /// source → put at destination, all on one transient canvas state.
    /// </summary>
    private void ShiftPixels(DrawingLayer layer, int dx, int dy, ToolContext ctx)
    {
        var contentBounds = layer.Pixels.ContentTileBounds;
        if (contentBounds.IsEmpty) return;

        const int ts = TiledPixelBuffer.TileSize;

        int oldX = contentBounds.X;
        int oldY = contentBounds.Y;
        int oldW = contentBounds.Width;
        int oldH = contentBounds.Height;

        // Local tile regions for undo capture.
        var localOldRegion = new PixelRegion(
            FloorDown(oldX, ts), FloorDown(oldY, ts),
            AlignUp(oldX + oldW, ts) - FloorDown(oldX, ts),
            AlignUp(oldY + oldH, ts) - FloorDown(oldY, ts));

        int newX = oldX + dx;
        int newY = oldY + dy;
        var localNewRegion = new PixelRegion(
            FloorDown(newX, ts), FloorDown(newY, ts),
            AlignUp(newX + oldW, ts) - FloorDown(newX, ts),
            AlignUp(newY + oldH, ts) - FloorDown(newY, ts));

        // The compositor cache is indexed by DOCUMENT coordinates. The offset
        // is at the preview value during this function. After we restore it
        // below, the compositor reads pixels via (docPos - restoredOffset).
        var previewOffsetX = _origOffsetX + dx;
        var previewOffsetY = _origOffsetY + dy;

        // Document-space dirty region from the layer's OWN perspective.
        // After we shift local pixels + restore offset, content lands at:
        // doc = localNew + origOffset = (localOld + dx) + origOffset
        // = localOld + (origOffset + dx) = localOld + previewOffset
        // Which is exactly where Preview showed it. The compositor already
        // rendered tiles at that position during Preview, so those tiles
        // are valid. We only need to invalidate the old local position's
        // document footprint (which is now empty) and the new local position
        // (in case compositor tiles there were stale).

        // Old document region: where content WAS before the drag.
        var docOld = localOldRegion.Translate(_origOffsetX, _origOffsetY);

        // New document region: where content IS after shift + offset restore.
        var docNew = localNewRegion.Translate(_origOffsetX, _origOffsetY);

        // Snapshot tiles before any mutation (undo).
        var beforeTiles = layer.CaptureTiles(localOldRegion);
        var destBefore = layer.CaptureTiles(localNewRegion);

        // Capture content pixels ().
        var captured = layer.Pixels.Capture(contentBounds);

        // Erase old position FIRST (fill_rect REPLACE+transparent).
        var zeroBuf = new byte[oldW * oldH * 4];
        layer.Pixels.Restore(contentBounds, zeroBuf);

        // Write shifted pixels at new local position (put_image).
        var newContentBounds = new PixelRegion(newX, newY, oldW, oldH);
        layer.Pixels.Restore(newContentBounds, captured);

        // Restore the original offset — pixels now live at new local coords,
        // the compositor reads them through the original offset and they land
        // at the same document position as Preview showed.
        layer.OffsetX = _origOffsetX;
        layer.OffsetY = _origOffsetY;

        layer.Pixels.PruneRegion(localOldRegion);
        layer.Pixels.PruneRegion(localNewRegion);

        var allBefore = new Dictionary<(int X, int Y), byte[]?>(beforeTiles);
        foreach (var kv in destBefore)
            allBefore[kv.Key] = kv.Value;

        // Dirty region in document space — the compositor drops and
        // re-composites tiles here. Both old (now empty) and new (now
        // contains shifted content) positions need invalidation.
        var combinedDocDirty = docOld.Union(docNew);
        ctx.Document.CommitLayerTileMutations(
            [new LayerTileMutation(ctx.ActiveLayerIndex, allBefore, combinedDocDirty)]);

        // DirectDrawOutput calls NotifyChanged + InvalidateRender on commit.
        // CommitLayerTileMutations fires NotifyChanged internally, but the
        // scheduler may capture a stale viewport from the preview cycle.
        // Explicitly invalidate here so the compositor sees the full dirty
        // region with the current viewport.
        ctx.Document.NotifyChanged(combinedDocDirty, ctx.ActiveLayerIndex);
        ctx.InvalidateRender();

        layer.MarkThumbnailDirty();
    }

    private static int FloorDown(int v, int d) => v >= 0 ? (v / d) * d : ((v - d + 1) / d) * d;
    private static int AlignUp(int v, int d) => ((v + d - 1) / d) * d;
}
