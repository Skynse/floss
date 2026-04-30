using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public enum ToolKind
{
    Brush,
    Eraser
}

public sealed class CanvasTool
{
    private readonly DrawingDocument _document;
    private readonly BrushEngine _brushEngine;
    private CanvasInputSample _lastSample;
    private bool _active;
    private int _activeLayerIndex;
    private readonly List<LayerRegionPatch> _strokePatches = [];
    private PixelRegion _strokeDirtyRegion;

    public CanvasTool(DrawingDocument document, BrushEngine brushEngine)
    {
        _document = document;
        _brushEngine = brushEngine;
    }

    public ToolKind Kind { get; private set; } = ToolKind.Brush;
    public int ActiveSampleCount { get; private set; }

    public void SetKind(ToolKind kind) => Kind = kind;

    public void Begin(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_document.CanPaintActiveLayer) return;
        _active = true;
        _activeLayerIndex = _document.ActiveLayerIndex;
        _strokePatches.Clear();
        _strokeDirtyRegion = PixelRegion.Empty;
        ActiveSampleCount = 1;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        _lastSample = localSample;
        _brushEngine.BeginStroke(brush, IsEraser(sample), localSample);
    }

    public void Update(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        var smoothed = ApplyStreamline(localSample, brush.Smoothing);
        RasterizeSegmentWithHistory(layer, brush, sample, _lastSample, smoothed);
        _lastSample = smoothed;
        ActiveSampleCount++;
    }

    public void End(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var localSample = ToLayerSample(layer, sample);
        // Always draw the final segment so the stroke reaches the pen-up position
        RasterizeSegmentWithHistory(layer, brush, sample, _lastSample, localSample);
        _active = false;
        ActiveSampleCount = 0;
        _document.CommitLayerRegionMutation(_activeLayerIndex, _strokePatches, _strokeDirtyRegion);
        _strokePatches.Clear();
        _strokeDirtyRegion = PixelRegion.Empty;
        _brushEngine.EndStroke();
        _document.CommitStroke();
    }

    public void Cancel()
    {
        _active = false;
        ActiveSampleCount = 0;
        _brushEngine.EndStroke();
    }

    // Exponential moving average toward the new sample ("Streamline" smoothing)
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

    private void RasterizeDabWithHistory(DrawingLayer layer, BrushPreset brush, CanvasInputSample sourceSample, CanvasInputSample localSample, double velocity)
    {
        var region = _brushEngine.EstimateDabRegion(layer, brush, localSample);
        if (region.IsEmpty) return;

        var before = layer.CapturePixels(region);
        var dirty = _brushEngine.RasterizeDab(layer, brush, IsEraser(sourceSample), localSample, velocity);
        if (dirty.IsEmpty) return;

        layer.MarkThumbnailDirty();
        _strokePatches.Add(new LayerRegionPatch(region, before));
        var docDirty = region.Translate(layer.OffsetX, layer.OffsetY);
        _strokeDirtyRegion = _strokeDirtyRegion.Union(docDirty);
        _document.NotifyChanged(docDirty, _activeLayerIndex);
    }

    private void RasterizeSegmentWithHistory(DrawingLayer layer, BrushPreset brush, CanvasInputSample sourceSample, CanvasInputSample from, CanvasInputSample to)
    {
        var region = _brushEngine.EstimateSegmentRegion(layer, brush, from, to);
        if (region.IsEmpty) return;

        var before = layer.CapturePixels(region);
        var dirty = _brushEngine.RasterizeSegment(layer, brush, IsEraser(sourceSample), from, to);
        if (dirty.IsEmpty) return;

        layer.MarkThumbnailDirty();
        _strokePatches.Add(new LayerRegionPatch(region, before));
        var docDirty = region.Translate(layer.OffsetX, layer.OffsetY);
        _strokeDirtyRegion = _strokeDirtyRegion.Union(docDirty);
        _document.NotifyChanged(docDirty, _activeLayerIndex);
    }

    private bool IsEraser(CanvasInputSample sample)
        => Kind == ToolKind.Eraser || sample.Source == CanvasInputSource.Eraser;
}
