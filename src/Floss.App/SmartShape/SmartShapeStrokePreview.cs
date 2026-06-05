using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Brushes;
using Floss.App.Brushes.Engine;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.SmartShape;

/// <summary>
/// Offscreen brush-stroke preview — same performance model as Ctrl+T transform overlay:
/// rasterize once per shape change into a <see cref="WriteableBitmap"/>, never touch document tiles.
/// </summary>
internal sealed class SmartShapeStrokePreview : IDisposable
{
    private const int MinUpdateIntervalMs = 16;

    private readonly BrushEngine _brushEngine;
    private DrawingLayer? _scratch;
    private byte[]? _pixels;
    private WriteableBitmap? _bitmap;
    private Rect _docRect;
    private SmartShapeModel? _cachedShape;
    private double _cachedPressure;
    private double _cachedBrushSize;
    private long _lastUpdateMs;

    public SmartShapeStrokePreview(BrushEngine brushEngine) => _brushEngine = brushEngine;

    public bool IsActive => _bitmap != null;

    public void Update(ToolContext ctx, SmartShapeModel shape, double avgPressure)
    {
        var brush = ctx.Brush;
        var layer = ctx.ActiveLayer;
        if (brush == null || layer == null || layer.IsGroup || layer.IsLocked)
            return;

        var now = Environment.TickCount64;
        if (_cachedShape != null
            && _cachedShape.Equals(shape)
            && Math.Abs(_cachedPressure - avgPressure) < 0.0001
            && Math.Abs(_cachedBrushSize - brush.Size) < 0.0001
            && now - _lastUpdateMs < MinUpdateIntervalMs)
            return;

        var samples = SmartShapePolyline.ToDocumentSamples(shape, layer, avgPressure);
        if (samples.Count < 2)
            return;

        var region = PixelRegion.Empty;
        for (var i = 1; i < samples.Count; i++)
            region = region.Union(_brushEngine.EstimateSegmentRegion(layer, brush, samples[i - 1], samples[i]));
        if (region.IsEmpty)
            return;

        EnsureScratch(region, layer);
        _scratch!.Pixels.Clear();

        _brushEngine.CanvasZoom = ctx.Viewport?.Zoom ?? 1.0;
        _brushEngine.BeginStroke(brush, samples[0]);
        _brushEngine.RasterizeSegments(_scratch, brush, samples, 1, samples.Count - 1);
        _brushEngine.EndStroke();

        var w = region.Width;
        var h = region.Height;
        _pixels = _scratch.Pixels.Capture(region);
        _docRect = new Rect(layer.OffsetX + region.X, layer.OffsetY + region.Y, w, h);

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using (var fb = _bitmap.Lock())
            Marshal.Copy(_pixels, 0, fb.Address, _pixels.Length);

        _cachedShape = shape;
        _cachedPressure = avgPressure;
        _cachedBrushSize = brush.Size;
        _lastUpdateMs = now;
    }

    public void Draw(DrawingContext dc)
    {
        if (_bitmap == null || _docRect.Width <= 0 || _docRect.Height <= 0)
            return;
        dc.DrawImage(_bitmap, _docRect);
    }

    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _pixels = null;
        _docRect = default;
        _cachedShape = null;
        _scratch?.Dispose();
        _scratch = null;
    }

    public void Dispose() => Clear();

    private void EnsureScratch(PixelRegion region, DrawingLayer sourceLayer)
    {
        if (_scratch == null
            || _scratch.Pixels.Width < region.Width
            || _scratch.Pixels.Height < region.Height)
        {
            _scratch?.Dispose();
            _scratch = new DrawingLayer("_smart_shape_preview", region.Width, region.Height);
        }

        _scratch.OffsetX = sourceLayer.OffsetX + region.X;
        _scratch.OffsetY = sourceLayer.OffsetY + region.Y;
    }
}
