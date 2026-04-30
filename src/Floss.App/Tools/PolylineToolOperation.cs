using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public sealed class PolylineToolOperation : IToolOperation
{
    private readonly ToolContext _context;
    private readonly float _strokeWidth;
    private readonly bool _closePath;
    private readonly List<SKPoint> _points = [];
    private SKPoint _cursorPoint;

    public PolylineToolOperation(ToolContext context, CanvasInputSample firstSample, float strokeWidth, bool closePath)
    {
        _context = context;
        _strokeWidth = strokeWidth;
        _closePath = closePath;
        _cursorPoint = Pt(firstSample);
        _points.Add(_cursorPoint);
        SampleCount = 1;
    }

    public int SampleCount { get; private set; }

    public void AddPoint(CanvasInputSample sample)
    {
        _cursorPoint = Pt(sample);
        _points.Add(_cursorPoint);
        SampleCount++;
        _context.InvalidateRender();
    }

    public void Update(CanvasInputSample sample)
    {
        _cursorPoint = Pt(sample);
        _context.InvalidateRender();
    }

    public void Commit(CanvasInputSample sample)
    {
        if (_points.Count >= 2)
            Apply();
        Cancel();
    }

    public void Cancel()
    {
        _points.Clear();
        SampleCount = 0;
        _context.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (_points.Count == 0) return;
        var t = Math.Max(0.5, 1.0 / zoom);
        var penW = new Pen(Avalonia.Media.Brushes.White, t);
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, new DashStyle([4, 4], 4));

        var geo = BuildPreviewGeometry();
        dc.DrawGeometry(null, penW, geo);
        dc.DrawGeometry(null, penK, geo);
    }

    private StreamGeometry BuildPreviewGeometry()
    {
        var geo = new StreamGeometry();
        using var c = geo.Open();
        c.BeginFigure(new Avalonia.Point(_points[0].X, _points[0].Y), false);
        for (int i = 1; i < _points.Count; i++)
            c.LineTo(new Avalonia.Point(_points[i].X, _points[i].Y));
        c.LineTo(new Avalonia.Point(_cursorPoint.X, _cursorPoint.Y));
        c.EndFigure(false);
        return geo;
    }

    private void Apply()
    {
        var layer = _context.ActiveLayer;
        if (layer == null || layer.IsLocked) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);

        using var bmp = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var c = _context.PaintColor;
        using var paint = new SKPaint
        {
            Color = new SKColor(c.R, c.G, c.B, c.A),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = _strokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var path = new SKPath();
        float ox = layer.OffsetX;
        float oy = layer.OffsetY;
        path.MoveTo(_points[0].X - ox, _points[0].Y - oy);
        for (int i = 1; i < _points.Count; i++)
            path.LineTo(_points[i].X - ox, _points[i].Y - oy);
        if (_closePath) path.Close();
        canvas.DrawPath(path, paint);

        for (int py = 0; py < layer.Height; py++)
        {
            for (int px = 0; px < layer.Width; px++)
            {
                if (!_context.Selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY)) continue;
                var pix = bmp.GetPixel(px, py);
                if (pix.Alpha == 0) continue;
                layer.Pixels.SetPixel(px, py, pix.Blue, pix.Green, pix.Red, pix.Alpha);
            }
        }

        layer.MarkThumbnailDirty();
        var dirty = new PixelRegion(layer.OffsetX, layer.OffsetY, layer.Width, layer.Height);
        _context.CommitMutation(_context.ActiveLayerIndex, beforeTiles, dirty);
    }

    private static SKPoint Pt(CanvasInputSample s) => new((float)s.X, (float)s.Y);
}
