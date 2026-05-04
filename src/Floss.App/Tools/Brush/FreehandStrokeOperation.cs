using System;
using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class FreehandStrokeOperation : IToolOperation
{
    private const double MinimumMoveDistance = 0.25;
    private const double MinimumPressureDelta = 0.01;

    private readonly DrawingDocument _document;
    private readonly BrushEngine _brushEngine;
    private readonly BrushPreset _brush;
    private readonly bool _eraser;
    private readonly SelectionMask _selection;
    private readonly int _activeLayerIndex;
    private readonly Dictionary<(int X, int Y), byte[]?> _beforeTiles = [];

    private CanvasInputSample _lastSample;
    private CanvasInputSample _firstSample;
    private PixelRegion _dirtyRegion;
    private bool _active = true;
    private bool _renderedInitialDab;

    public FreehandStrokeOperation(
        DrawingDocument document,
        BrushEngine brushEngine,
        BrushPreset brush,
        bool eraser,
        SelectionMask selection,
        CanvasInputSample firstSample)
    {
        _document = document;
        _brushEngine = brushEngine;
        _brush = brush;
        _eraser = eraser;
        _selection = selection;
        _activeLayerIndex = document.ActiveLayerIndex;
        SampleCount = 1;

        var layer = document.ActiveLayer;
        var localSample = ToLayerSample(layer, firstSample);
        _lastSample = localSample;
        _firstSample = localSample;
        _brushEngine.BeginStroke(_brush, localSample);

        // For mouse/touch we render the initial dab immediately (a click should leave a mark).
        // For pen/eraser we defer — the PointerPressed event often reports incorrect pressure
        // (0, 1, or driver-default) before the first PointerMoved delivers the real reading.
        if (firstSample.Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
        {
            RasterizeDabWithHistory(layer, firstSample, localSample, velocity: 0);
            _renderedInitialDab = true;
        }
    }

    public int SampleCount { get; private set; }

    public void Update(CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        if (sample.Phase == CanvasInputPhase.Move &&
            _lastSample.DistanceTo(localSample) < MinimumMoveDistance &&
            Math.Abs(_lastSample.Pressure - localSample.Pressure) < MinimumPressureDelta)
        {
            return;
        }

        var smoothed = ApplyStreamline(localSample, _brush.Smoothing);
        RasterizeSegmentWithHistory(layer, sample, _lastSample, smoothed);

        // If this was the first real segment and the initial pressure was suspiciously
        // high while the first move shows lower pressure, the PointerPressed event
        // likely reported a bogus value. Replace the stored initial pressure so it
        // doesn't keep polluting smoothing on future segments.
        if (SampleCount == 1 && !_renderedInitialDab &&
            _lastSample.Pressure >= 0.95 && smoothed.Pressure < _lastSample.Pressure * 0.5)
        {
            _lastSample = _lastSample.WithPosition(_lastSample.X, _lastSample.Y, smoothed.Pressure, _lastSample.TimeMicros);
        }

        _lastSample = smoothed;
        SampleCount++;
    }

    public void Commit(CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        RasterizeFinalSegmentWithHistory(layer, sample, _lastSample, localSample);

        // If the stroke never moved enough to render anything (just a tap), draw the
        // initial dab now so a pure tap still leaves a mark.
        if (_dirtyRegion.IsEmpty && !_renderedInitialDab)
        {
            RasterizeDabWithHistory(layer, sample, _firstSample, velocity: 0);
        }

        _active = false;
        SampleCount = 0;
        _document.CommitLayerTileMutation(_activeLayerIndex, _beforeTiles, _dirtyRegion);
        _beforeTiles.Clear();
        _dirtyRegion = PixelRegion.Empty;
        _brushEngine.EndStroke();
        _document.CommitStroke();
    }

    public void Cancel()
    {
        _active = false;
        SampleCount = 0;
        _beforeTiles.Clear();
        _dirtyRegion = PixelRegion.Empty;
        _brushEngine.EndStroke();
    }

    private CanvasInputSample ApplyStreamline(CanvasInputSample raw, double smoothing)
    {
        if (smoothing <= 0) return raw;
        var alpha = 1.0 - smoothing;
        return raw.WithPosition(
            _lastSample.X + (raw.X - _lastSample.X) * alpha,
            _lastSample.Y + (raw.Y - _lastSample.Y) * alpha,
            _lastSample.Pressure + (raw.Pressure - _lastSample.Pressure) * alpha,
            raw.TimeMicros);
    }

    private static CanvasInputSample ToLayerSample(DrawingLayer layer, CanvasInputSample sample)
        => sample.WithPosition(sample.X - layer.OffsetX, sample.Y - layer.OffsetY, sample.Pressure, sample.TimeMicros);

    private void RasterizeDabWithHistory(DrawingLayer layer, CanvasInputSample sourceSample, CanvasInputSample localSample, double velocity)
    {
        var region = _brushEngine.EstimateDabRegion(layer, _brush, localSample);
        if (region.IsEmpty) return;

        layer.ExpandToAccommodate(region.X, region.Y, region.Right, region.Bottom);
        CaptureBeforeTiles(layer, region);
        var dirty = _brushEngine.RasterizeDab(layer, _brush, localSample, velocity);
        if (dirty.IsEmpty) return;
        RestoreUnselectedPixels(layer, dirty);

        layer.MarkThumbnailDirty();
        var docDirty = dirty.Translate(layer.OffsetX, layer.OffsetY);
        _dirtyRegion = _dirtyRegion.Union(docDirty);
        _document.NotifyChanged(docDirty, _activeLayerIndex);
    }

    private void RasterizeSegmentWithHistory(DrawingLayer layer, CanvasInputSample sourceSample, CanvasInputSample from, CanvasInputSample to)
    {
        RasterizeSegmentWithHistoryInternal(layer, sourceSample, from, to, final: false);
    }

    private void RasterizeFinalSegmentWithHistory(DrawingLayer layer, CanvasInputSample sourceSample, CanvasInputSample from, CanvasInputSample to)
    {
        RasterizeSegmentWithHistoryInternal(layer, sourceSample, from, to, final: true);
    }

    private void RasterizeSegmentWithHistoryInternal(DrawingLayer layer, CanvasInputSample sourceSample, CanvasInputSample from, CanvasInputSample to, bool final)
    {
        var region = _brushEngine.EstimateSegmentRegion(layer, _brush, from, to);
        if (region.IsEmpty) return;

        layer.ExpandToAccommodate(region.X, region.Y, region.Right, region.Bottom);
        CaptureBeforeTiles(layer, region);
        var dirty = final
            ? _brushEngine.RasterizeFinalSegment(layer, _brush, from, to)
            : _brushEngine.RasterizeSegment(layer, _brush, from, to);
        if (dirty.IsEmpty) return;
        RestoreUnselectedPixels(layer, dirty);

        layer.MarkThumbnailDirty();
        var docDirty = dirty.Translate(layer.OffsetX, layer.OffsetY);
        _dirtyRegion = _dirtyRegion.Union(docDirty);
        _document.NotifyChanged(docDirty, _activeLayerIndex);
    }

    private void CaptureBeforeTiles(DrawingLayer layer, PixelRegion region)
    {
        layer.CaptureTiles(region, _beforeTiles);
    }

    private void RestoreUnselectedPixels(DrawingLayer layer, PixelRegion dirty)
    {
        var clipped = dirty.ClipTo(layer.Width, layer.Height);
        if (clipped.IsEmpty) return;

        bool hasSelection = _selection.HasSelection;
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
                _beforeTiles.TryGetValue((tx, ty), out var beforeTile);

                // Fast-path: if alpha-locked and the before-tile was entirely
                // transparent, no existing pixels need preserving — skip.
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
                        bool inSelection = !hasSelection || _selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY);

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
            {
                if (w[i] != 0) return false;
            }
        }
        return true;
    }
}
