using System;
using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class SmudgeStrokeOperation
{
    private readonly DrawingDocument _document;
    private readonly SmudgeEngine _engine;
    private readonly int _layerIndex;
    private readonly Dictionary<(int X, int Y), byte[]?> _beforeTiles = [];
    private PixelRegion _dirty;
    private CanvasInputSample _last;
    private bool _active = true;

    public SmudgeStrokeOperation(DrawingDocument document, BrushPreset brush, CanvasInputSample sample)
    {
        _document = document;
        _engine = new SmudgeEngine();
        _engine.Begin();
        _layerIndex = document.ActiveLayerIndex;
        _last = ToLayer(document.ActiveLayer, sample);
    }

    public void Update(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_active) return;
        var layer = _document.ActiveLayer;
        var local = ToLayer(layer, sample);
        SmudgeWithHistory(layer, brush, local);
        _last = local;
    }

    public void Commit(CanvasInputSample sample)
    {
        if (!_active) return;
        _active = false;
        if (!_dirty.IsEmpty)
        {
            _document.CommitLayerTileMutation(_layerIndex, _beforeTiles, _dirty);
            _document.CommitStroke();
        }
        _beforeTiles.Clear();
    }

    public void Cancel()
    {
        _active = false;
        _beforeTiles.Clear();
        _dirty = PixelRegion.Empty;
    }

    private void SmudgeWithHistory(DrawingLayer layer, BrushPreset brush, CanvasInputSample local)
    {
        var strength = (float)Math.Clamp(brush.Opacity, 0.05, 1.0);
        var radius = (float)brush.Size * 0.75f + 2;
        var region = new PixelRegion(
            (int)(local.X - radius), (int)(local.Y - radius),
            (int)(radius * 2 + 4), (int)(radius * 2 + 4))
            .ClipTo(layer.Width, layer.Height);
        if (region.IsEmpty) return;

        layer.CaptureTiles(region, _beforeTiles);
        var painted = _engine.Smudge(layer, brush, (float)local.X, (float)local.Y, strength);
        if (painted.IsEmpty) return;

        layer.MarkThumbnailDirty();
        var docDirty = painted.Translate(layer.OffsetX, layer.OffsetY);
        _dirty = _dirty.Union(docDirty);
        _document.NotifyChanged(docDirty, _layerIndex);
    }

    private static CanvasInputSample ToLayer(DrawingLayer layer, CanvasInputSample s)
        => s.WithPosition(s.X - layer.OffsetX, s.Y - layer.OffsetY, s.Pressure, s.TimeMicros);
}
