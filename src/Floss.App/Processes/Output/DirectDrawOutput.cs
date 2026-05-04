using System;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Paints a stroke onto the active layer using the brush engine.
public sealed class DirectDrawOutput : IOutputProcess
{
    private readonly BrushEngine _brushEngine;
    private readonly DrawingDocument _document;

    public bool Antialiasing { get; set; } = true;
    public bool IsEraser { get; set; }

    public DirectDrawOutput(BrushEngine brushEngine, DrawingDocument document)
    {
        _brushEngine = brushEngine;
        _document = document;
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke) return;
        if (stroke.SmoothedSamples.Count == 0) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var samples = stroke.SmoothedSamples;
        var brush = ctx.Brush;
        var eraser = IsEraser;
        var selection = ctx.Selection;

        var beforeTiles = new System.Collections.Generic.Dictionary<(int, int), byte[]?>();
        var dirtyRegion = PixelRegion.Empty;

        // Begin stroke
        _brushEngine.BeginStroke(brush, eraser, ToLayerSample(layer, samples[0]));

        // Initial dab for mouse/touch
        if (samples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
        {
            var region = _brushEngine.EstimateDabRegion(layer, brush, ToLayerSample(layer, samples[0]));
            if (!region.IsEmpty)
            {
                layer.ExpandToAccommodate(region.X, region.Y, region.Right, region.Bottom);
                CaptureBeforeTiles(layer, region, beforeTiles);
                var dirty = _brushEngine.RasterizeDab(layer, brush, eraser, ToLayerSample(layer, samples[0]), velocity: 0);
                if (!dirty.IsEmpty)
                {
                    RestoreUnselectedPixels(layer, dirty, selection, beforeTiles);
                    dirtyRegion = dirtyRegion.Union(dirty.Translate(layer.OffsetX, layer.OffsetY));
                }
            }
        }

        // Segments
        for (int i = 1; i < samples.Count; i++)
        {
            var from = ToLayerSample(layer, samples[i - 1]);
            var to = ToLayerSample(layer, samples[i]);
            var region = _brushEngine.EstimateSegmentRegion(layer, brush, from, to);
            if (region.IsEmpty) continue;

            layer.ExpandToAccommodate(region.X, region.Y, region.Right, region.Bottom);
            CaptureBeforeTiles(layer, region, beforeTiles);
            var dirty = _brushEngine.RasterizeSegment(layer, brush, eraser, from, to);
            if (!dirty.IsEmpty)
            {
                RestoreUnselectedPixels(layer, dirty, selection, beforeTiles);
                dirtyRegion = dirtyRegion.Union(dirty.Translate(layer.OffsetX, layer.OffsetY));
            }
        }

        _brushEngine.EndStroke();

        if (!dirtyRegion.IsEmpty)
        {
            layer.MarkThumbnailDirty();
            _document.CommitLayerTileMutation(ctx.ActiveLayerIndex, beforeTiles, dirtyRegion);
            _document.NotifyChanged(dirtyRegion, ctx.ActiveLayerIndex);
        }
    }

    private static CanvasInputSample ToLayerSample(DrawingLayer layer, CanvasInputSample s)
        => s.WithPosition(s.X - layer.OffsetX, s.Y - layer.OffsetY, s.Pressure, s.TimeMicros);

    private static void CaptureBeforeTiles(DrawingLayer layer, PixelRegion region,
        System.Collections.Generic.Dictionary<(int, int), byte[]?> beforeTiles)
    {
        layer.CaptureTiles(region, beforeTiles);
    }

    private static void RestoreUnselectedPixels(DrawingLayer layer, PixelRegion dirty,
        SelectionMask selection, System.Collections.Generic.Dictionary<(int, int), byte[]?> beforeTiles)
    {
        var clipped = dirty.ClipTo(layer.Width, layer.Height);
        if (clipped.IsEmpty) return;

        bool hasSelection = selection.HasSelection;
        bool alphaLocked = layer.IsAlphaLocked;
        if (!hasSelection && !alphaLocked) return;

        const int ts = TiledPixelBuffer.TileSize;
        int firstTileX = clipped.X / ts;
        int firstTileY = clipped.Y / ts;
        int lastTileX = (clipped.Right - 1) / ts;
        int lastTileY = (clipped.Bottom - 1) / ts;

        for (int ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (int tx = firstTileX; tx <= lastTileX; tx++)
            {
                beforeTiles.TryGetValue((tx, ty), out var beforeTile);
                if (alphaLocked && beforeTile != null && IsTileAllZero(beforeTile))
                    continue;

                int pxMin = Math.Max(clipped.X, tx * ts);
                int pxMax = Math.Min(clipped.Right, tx * ts + ts);
                int pyMin = Math.Max(clipped.Y, ty * ts);
                int pyMax = Math.Min(clipped.Bottom, ty * ts + ts);

                for (int py = pyMin; py < pyMax; py++)
                {
                    int ly = py - ty * ts;
                    for (int px = pxMin; px < pxMax; px++)
                    {
                        bool inSelection = !hasSelection || selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY);
                        int lx = px - tx * ts;
                        int offset = (ly * ts + lx) * 4;
                        bool hadAlpha = !alphaLocked || (beforeTile != null && beforeTile[offset + 3] > 0);

                        if (inSelection && hadAlpha) continue;

                        if (beforeTile != null)
                            layer.Pixels.SetPixel(px, py, beforeTile[offset], beforeTile[offset + 1], beforeTile[offset + 2], beforeTile[offset + 3]);
                        else
                            layer.Pixels.SetPixel(px, py, 0, 0, 0, 0);
                    }
                }
            }
        }
    }

    private static unsafe bool IsTileAllZero(byte[] tile)
    {
        fixed (byte* p = tile)
        {
            var len = tile.Length / 4;
            var w = (uint*)p;
            for (var i = 0; i < len; i++)
                if (w[i] != 0) return false;
        }
        return true;
    }
}
