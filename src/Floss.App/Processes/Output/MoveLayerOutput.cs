using System;
using Floss.App.Document;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Translates the active layer by the drag delta.
public sealed class MoveLayerOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;

        var layer = ctx.ActiveLayer;
        if (layer == null) return;

        int dx = (int)(drag.Current.X - drag.Start.X);
        int dy = (int)(drag.Current.Y - drag.Start.Y);

        if (dx == 0 && dy == 0) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        layer.OffsetX += dx;
        layer.OffsetY += dy;

        var dirty = new PixelRegion(
            Math.Min(layer.OffsetX, layer.OffsetX - dx),
            Math.Min(layer.OffsetY, layer.OffsetY - dy),
            layer.Width + Math.Abs(dx),
            layer.Height + Math.Abs(dy));

        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }
}
