using System;
using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Paints a stroke onto the active layer using the brush engine.
// All rasterization runs synchronously on the UI thread — no background worker,
// no shared mutable state, no locking needed.
public sealed class DirectDrawOutput : IOutputProcess
{
    private const long PreviewNotifyIntervalMs = 12;

    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;

    public bool Antialiasing { get; set; } = true;

    private int _lastProcessedIndex = -1;
    private Dictionary<(int, int), byte[]?>? _beforeTiles;
    private PixelRegion _dirtyRegion;
    private PixelRegion _pendingPreviewDirty;
    private DrawingLayer? _currentLayer;
    private bool _strokeActive;
    private ToolContext? _currentCtx;
    private long _lastPreviewNotifyMs;

    public DirectDrawOutput(BrushEngine brushEngine, DrawingDocument _)
    {
        _brushEngine = brushEngine;
    }

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var samples = stroke.SmoothedSamples;
        var brush = ctx.Brush;
        var selection = ctx.Selection;
        var batchDirty = PixelRegion.Empty;

        if (!_strokeActive)
        {
            _strokeActive = true;
            _lastProcessedIndex = -1;
            _beforeTiles = new Dictionary<(int, int), byte[]?>();
            _dirtyRegion = PixelRegion.Empty;
            _currentLayer = layer;
            _currentCtx = ctx;
            _brushEngine.BeginStroke(brush, ToLayerSample(layer, samples[0]));

            if (samples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
            {
                batchDirty = batchDirty.Union(ProcessSegment(layer, brush, selection, ToLayerSample(layer, samples[0]), ToLayerSample(layer, samples[0])));
                _lastProcessedIndex = 0;
            }
        }

        var startIdx = Math.Max(1, _lastProcessedIndex + 1);
        for (int i = startIdx; i < samples.Count; i++)
        {
            batchDirty = batchDirty.Union(ProcessSegment(layer, brush, selection, ToLayerSample(layer, samples[i - 1]), ToLayerSample(layer, samples[i])));
            _lastProcessedIndex = i;
        }

        if (!batchDirty.IsEmpty)
        {
            _pendingPreviewDirty = _pendingPreviewDirty.Union(batchDirty);
            FlushPreviewDirty(ctx, force: false);
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

        Preview(ctx, input);
        FlushPreviewDirty(ctx, force: true);
        _brushEngine.EndStroke();
        Commit();
    }

    private void FlushPreviewDirty(ToolContext ctx, bool force)
    {
        if (_pendingPreviewDirty.IsEmpty) return;

        var now = Environment.TickCount64;
        if (!force && now - _lastPreviewNotifyMs < PreviewNotifyIntervalMs)
            return;

        ctx.Document.NotifyChanged(_pendingPreviewDirty, ctx.ActiveLayerIndex);
        ctx.InvalidateRender();
        _pendingPreviewDirty = PixelRegion.Empty;
        _lastPreviewNotifyMs = now;
    }

    private PixelRegion ProcessSegment(DrawingLayer layer, BrushPreset brush, SelectionMask selection, CanvasInputSample from, CanvasInputSample to)
    {
        var region = _brushEngine.EstimateSegmentRegion(layer, brush, from, to);
        if (!region.IsEmpty && _beforeTiles != null)
            layer.CaptureTiles(region, _beforeTiles);

        var dirty = _brushEngine.RasterizeSegment(layer, brush, from, to,
            (x, y, out b, out g, out r, out a) => ReadBeforeStrokePixelFrom(_beforeTiles, layer, x, y, out b, out g, out r, out a));

        if (!dirty.IsEmpty)
        {
            if (_beforeTiles != null)
                RestoreUnselectedPixels(layer, dirty, selection, _beforeTiles);

            var translatedDirty = dirty.Translate(layer.OffsetX, layer.OffsetY);
            _dirtyRegion = _dirtyRegion.Union(translatedDirty);
            return translatedDirty;
        }

        return PixelRegion.Empty;
    }

    private void Commit()
    {
        try
        {
            if (_beforeTiles != null && _beforeTiles.Count > 0 && _currentLayer != null && _currentCtx != null)
            {
                var tileDirty = ComputeTileDirtyRegion(_beforeTiles).Translate(_currentLayer.OffsetX, _currentLayer.OffsetY);
                if (!tileDirty.IsEmpty)
                {
                    _currentLayer.MarkThumbnailDirty();
                    _currentCtx.Document.CommitLayerTileMutation(_currentCtx.ActiveLayerIndex, _beforeTiles, tileDirty);
                }
            }

            if (_currentCtx != null)
                _currentCtx.Document.CommitStroke();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "DirectDrawOutput.Commit");
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _strokeActive = false;
        _lastProcessedIndex = -1;
        _beforeTiles = null;
        _dirtyRegion = PixelRegion.Empty;
        _pendingPreviewDirty = PixelRegion.Empty;
        _currentLayer = null;
        _currentCtx = null;
        _lastPreviewNotifyMs = 0;
    }

    public void Cancel()
    {
        _brushEngine.EndStroke();

        if (_strokeActive && _beforeTiles != null && _currentLayer != null && _currentCtx != null)
        {
            foreach (var ((tx, ty), tile) in _beforeTiles)
                _currentLayer.RestoreTile(tx, ty, tile);

            var tileDirty = ComputeTileDirtyRegion(_beforeTiles).Translate(_currentLayer.OffsetX, _currentLayer.OffsetY);
            if (!tileDirty.IsEmpty)
            {
                _currentLayer.MarkThumbnailDirty();
                _currentCtx.Document.NotifyChanged(tileDirty, _currentCtx.ActiveLayerIndex);
                _currentCtx.InvalidateRender();
            }
        }

        Cleanup();
    }

    private static PixelRegion ComputeTileDirtyRegion(Dictionary<(int, int), byte[]?> tiles)
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

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);

    private static void ReadBeforeStrokePixelFrom(Dictionary<(int, int), byte[]?>? beforeTiles, DrawingLayer? currentLayer, int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        if (beforeTiles == null)
        {
            if (currentLayer != null)
                currentLayer.Pixels.GetPixel(x, y, out b, out g, out r, out a);
            else
                b = g = r = a = 0;
            return;
        }

        const int ts = TiledPixelBuffer.TileSize;
        var tx = FloorDiv(x, ts);
        var ty = FloorDiv(y, ts);
        if (!beforeTiles.TryGetValue((tx, ty), out var tile) || tile == null)
        {
            b = g = r = a = 0;
            return;
        }

        var lx = x - tx * ts;
        var ly = y - ty * ts;
        var offset = (ly * ts + lx) * 4;
        b = tile[offset];
        g = tile[offset + 1];
        r = tile[offset + 2];
        a = tile[offset + 3];
    }

    private void RestoreUnselectedPixels(DrawingLayer layer, PixelRegion dirty, SelectionMask selection, Dictionary<(int, int), byte[]?> beforeTiles)
    {
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
                beforeTiles.TryGetValue((tx, ty), out var beforeTile);
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
