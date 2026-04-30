using System;
using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class FreehandStrokeOperation : IToolOperation
{
    private readonly DrawingDocument _document;
    private readonly BrushEngine _brushEngine;
    private readonly BrushPreset _brush;
    private readonly bool _eraser;
    private readonly int _activeLayerIndex;
    private readonly Dictionary<(int X, int Y), byte[]?> _beforeTiles = [];

    private CanvasInputSample _lastSample;
    private PixelRegion _dirtyRegion;
    private bool _active = true;

    public FreehandStrokeOperation(
        DrawingDocument document,
        BrushEngine brushEngine,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample firstSample)
    {
        _document = document;
        _brushEngine = brushEngine;
        _brush = brush;
        _eraser = eraser;
        _activeLayerIndex = document.ActiveLayerIndex;
        SampleCount = 1;

        var layer = document.ActiveLayer;
        var localSample = ToLayerSample(layer, firstSample);
        _lastSample = localSample;
        _brushEngine.BeginStroke(_brush, _eraser, localSample);
        RasterizeDabWithHistory(layer, firstSample, localSample, velocity: 0);
    }

    public int SampleCount { get; private set; }

    public void Update(CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        var smoothed = ApplyStreamline(localSample, _brush.Smoothing);
        RasterizeSegmentWithHistory(layer, sample, _lastSample, smoothed);
        _lastSample = smoothed;
        SampleCount++;
    }

    public void Commit(CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        RasterizeSegmentWithHistory(layer, sample, _lastSample, localSample);
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

        CaptureBeforeTiles(layer, region);
        var dirty = _brushEngine.RasterizeDab(layer, _brush, _eraser || sourceSample.Source == CanvasInputSource.Eraser, localSample, velocity);
        if (dirty.IsEmpty) return;

        layer.MarkThumbnailDirty();
        var docDirty = dirty.Translate(layer.OffsetX, layer.OffsetY);
        _dirtyRegion = _dirtyRegion.Union(docDirty);
        _document.NotifyChanged(docDirty, _activeLayerIndex);
    }

    private void RasterizeSegmentWithHistory(DrawingLayer layer, CanvasInputSample sourceSample, CanvasInputSample from, CanvasInputSample to)
    {
        var region = _brushEngine.EstimateSegmentRegion(layer, _brush, from, to);
        if (region.IsEmpty) return;

        CaptureBeforeTiles(layer, region);
        var dirty = _brushEngine.RasterizeSegment(layer, _brush, _eraser || sourceSample.Source == CanvasInputSource.Eraser, from, to);
        if (dirty.IsEmpty) return;

        layer.MarkThumbnailDirty();
        var docDirty = dirty.Translate(layer.OffsetX, layer.OffsetY);
        _dirtyRegion = _dirtyRegion.Union(docDirty);
        _document.NotifyChanged(docDirty, _activeLayerIndex);
    }

    private void CaptureBeforeTiles(DrawingLayer layer, PixelRegion region)
    {
        layer.CaptureTiles(region, _beforeTiles);
    }
}
