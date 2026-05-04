using System;
using Floss.App.Document;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Translates the active layer by the drag delta.
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
        ctx.Document.NotifyChanged(null, ctx.ActiveLayerIndex);
        ctx.InvalidateRender();
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;

        var layer = ctx.ActiveLayer;
        if (layer == null) return;

        int dx = (int)(drag.Current.X - drag.Start.X);
        int dy = (int)(drag.Current.Y - drag.Start.Y);

        if (dx == 0 && dy == 0)
        {
            _hasPreview = false;
            return;
        }

        // Restore original position so we can capture proper before-tiles
        if (_hasPreview)
        {
            layer.OffsetX = _origOffsetX;
            layer.OffsetY = _origOffsetY;
        }

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        layer.OffsetX = _origOffsetX + dx;
        layer.OffsetY = _origOffsetY + dy;

        var dirty = new PixelRegion(
            Math.Min(layer.OffsetX, _origOffsetX),
            Math.Min(layer.OffsetY, _origOffsetY),
            layer.Width + Math.Abs(dx),
            layer.Height + Math.Abs(dy));

        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
        _hasPreview = false;
    }
}
