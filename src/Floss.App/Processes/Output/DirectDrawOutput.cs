using System;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Paints a stroke onto the active layer using the brush engine.
// Supports incremental application for real-time preview during drag.
public sealed class DirectDrawOutput : IOutputProcess
{
    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;
    private readonly DrawingDocument _document;

    public bool Antialiasing { get; set; } = true;

    // Incremental stroke state
    private int _lastProcessedIndex = -1;
    private System.Collections.Generic.Dictionary<(int, int), byte[]?>? _beforeTiles;
    private PixelRegion _dirtyRegion;        // per-preview dirty (reset after notify)
    private DrawingLayer? _currentLayer;
    private bool _strokeActive;

    public DirectDrawOutput(BrushEngine brushEngine, DrawingDocument document)
    {
        _brushEngine = brushEngine;
        _document = document;
    }

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var samples = stroke.SmoothedSamples;
        var brush = ctx.Brush;
        var selection = ctx.Selection;

        // Initialize stroke on first preview call
        if (!_strokeActive)
        {
            _strokeActive = true;
            _lastProcessedIndex = -1;
            _beforeTiles = new System.Collections.Generic.Dictionary<(int, int), byte[]?>();
            _dirtyRegion = PixelRegion.Empty;
            _currentLayer = layer;
            _brushEngine.BeginStroke(brush, ToLayerSample(layer, samples[0]));

            // Initial dab for mouse/touch
            if (samples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
            {
                ApplyDab(layer, brush, selection, samples[0], velocity: 0);
                _lastProcessedIndex = 0;
            }
        }

        // Process any new samples since last preview
        var startIdx = Math.Max(1, _lastProcessedIndex + 1);
        for (int i = startIdx; i < samples.Count; i++)
        {
            var from = ToLayerSample(layer, samples[i - 1]);
            var to = ToLayerSample(layer, samples[i]);
            ApplySegment(layer, brush, selection, from, to);
            _lastProcessedIndex = i;
        }

        if (!_dirtyRegion.IsEmpty)
        {
            layer.MarkThumbnailDirty();
            _document.NotifyChanged(_dirtyRegion, ctx.ActiveLayerIndex);
            _dirtyRegion = PixelRegion.Empty; // Reset after notifying
        }
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0)
        {
            Cleanup();
            return;
        }

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked)
        {
            Cleanup();
            return;
        }

        // Ensure all samples are processed
        Preview(ctx, input);

        _brushEngine.EndStroke();

        if (_beforeTiles != null && _beforeTiles.Count > 0)
        {
            var tileDirty = ComputeTileDirtyRegion(_beforeTiles).Translate(layer.OffsetX, layer.OffsetY);
            if (!tileDirty.IsEmpty)
            {
                layer.MarkThumbnailDirty();
                _document.CommitLayerTileMutation(ctx.ActiveLayerIndex, _beforeTiles, tileDirty);
                _document.NotifyChanged(tileDirty, ctx.ActiveLayerIndex);
            }
        }

        _document.CommitStroke();
        Cleanup();
    }

    private void ApplyDab(DrawingLayer layer, BrushPreset brush, SelectionMask selection, CanvasInputSample sample, double velocity)
    {
        var localSample = ToLayerSample(layer, sample);
        var region = _brushEngine.EstimateDabRegion(layer, brush, localSample);
        if (region.IsEmpty) return;

        CaptureBeforeTiles(layer, region);
        var dirty = _brushEngine.RasterizeDab(layer, brush, localSample, velocity);
        if (!dirty.IsEmpty)
        {
            RestoreUnselectedPixels(layer, dirty, selection);
            _dirtyRegion = _dirtyRegion.Union(dirty.Translate(layer.OffsetX, layer.OffsetY));
        }
    }

    private void ApplySegment(DrawingLayer layer, BrushPreset brush, SelectionMask selection, CanvasInputSample from, CanvasInputSample to)
    {
        var region = _brushEngine.EstimateSegmentRegion(layer, brush, from, to);
        if (region.IsEmpty) return;

        CaptureBeforeTiles(layer, region);
        var dirty = _brushEngine.RasterizeSegment(layer, brush, from, to);
        if (!dirty.IsEmpty)
        {
            RestoreUnselectedPixels(layer, dirty, selection);
            _dirtyRegion = _dirtyRegion.Union(dirty.Translate(layer.OffsetX, layer.OffsetY));
        }
    }

    private void Cleanup()
    {
        _strokeActive = false;
        _lastProcessedIndex = -1;
        _beforeTiles = null;
        _dirtyRegion = PixelRegion.Empty;
        _currentLayer = null;
    }

    private static PixelRegion ComputeTileDirtyRegion(System.Collections.Generic.Dictionary<(int, int), byte[]?> tiles)
    {
        if (tiles.Count == 0) return PixelRegion.Empty;
        const int ts = TiledPixelBuffer.TileSize;
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        foreach (var ((tx, ty), _) in tiles)
        {
            minX = Math.Min(minX, tx * ts);
            minY = Math.Min(minY, ty * ts);
            maxX = Math.Max(maxX, tx * ts + ts);
            maxY = Math.Max(maxY, ty * ts + ts);
        }
        return new PixelRegion(minX, minY, maxX - minX, maxY - minY);
    }

    private static CanvasInputSample ToLayerSample(DrawingLayer layer, CanvasInputSample s)
        => s.WithPosition(s.X - layer.OffsetX, s.Y - layer.OffsetY, s.Pressure, s.TimeMicros);

    private void CaptureBeforeTiles(DrawingLayer layer, PixelRegion region)
    {
        if (_beforeTiles == null) return;
        layer.CaptureTiles(region, _beforeTiles);
    }

    private void RestoreUnselectedPixels(DrawingLayer layer, PixelRegion dirty, SelectionMask selection)
    {
        if (_beforeTiles == null) return;

        if (dirty.IsEmpty) return;

        bool hasSelection = selection.HasSelection;
        bool alphaLocked = layer.IsAlphaLocked;
        if (!hasSelection && !alphaLocked) return;

        const int ts = TiledPixelBuffer.TileSize;
        int firstTileX = dirty.X / ts;
        int firstTileY = dirty.Y / ts;
        int lastTileX = (dirty.Right - 1) / ts;
        int lastTileY = (dirty.Bottom - 1) / ts;

        for (int ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (int tx = firstTileX; tx <= lastTileX; tx++)
            {
                _beforeTiles.TryGetValue((tx, ty), out var beforeTile);
                if (alphaLocked && beforeTile != null && IsTileAllZero(beforeTile))
                    continue;

                int pxMin = Math.Max(dirty.X, tx * ts);
                int pxMax = Math.Min(dirty.Right, tx * ts + ts);
                int pyMin = Math.Max(dirty.Y, ty * ts);
                int pyMax = Math.Min(dirty.Bottom, ty * ts + ts);

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
