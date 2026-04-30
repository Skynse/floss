using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

// Click to add vertices; double-click or Enter to commit the polyline onto the active layer.
public sealed class PolylineTool : ITool
{
    public float StrokeWidth { get; set; } = 4f;
    public bool ClosePath { get; set; } = false;

    private readonly List<SKPoint> _pts = [];
    private bool _pending;
    private SKPoint _cursorPt;

    public void Activate(ToolContext ctx) { }

    public void Deactivate(ToolContext ctx)
    {
        _pts.Clear();
        _pending = false;
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        var p = new SKPoint((float)s.X, (float)s.Y);
        if (!_pending)
        {
            _pending = true;
            _pts.Clear();
            _pts.Add(p);
        }
        else
        {
            _pts.Add(p);
        }
        _cursorPt = p;
        ctx.InvalidateRender();
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        _cursorPt = new SKPoint((float)s.X, (float)s.Y);
        if (_pending) ctx.InvalidateRender();
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s) { }

    // Called from MainWindow on double-click or Enter.
    public void Commit(ToolContext ctx)
    {
        if (!_pending || _pts.Count < 2) { Cancel(ctx); return; }
        Apply(ctx);
        _pts.Clear();
        _pending = false;
        ctx.InvalidateRender();
    }

    public void Cancel(ToolContext ctx)
    {
        _pts.Clear();
        _pending = false;
        ctx.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        if (!_pending || _pts.Count == 0) return;
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
        c.BeginFigure(new Avalonia.Point(_pts[0].X, _pts[0].Y), false);
        for (int i = 1; i < _pts.Count; i++)
            c.LineTo(new Avalonia.Point(_pts[i].X, _pts[i].Y));
        c.LineTo(new Avalonia.Point(_cursorPt.X, _cursorPt.Y));
        c.EndFigure(false);
        return geo;
    }

    private void Apply(ToolContext ctx)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsLocked) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);

        using var bmp = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var c = ctx.PaintColor;
        using var paint = new SKPaint
        {
            Color = new SKColor(c.R, c.G, c.B, c.A),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = StrokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var path = new SKPath();
        float ox = layer.OffsetX, oy = layer.OffsetY;
        path.MoveTo(_pts[0].X - ox, _pts[0].Y - oy);
        for (int i = 1; i < _pts.Count; i++)
            path.LineTo(_pts[i].X - ox, _pts[i].Y - oy);
        if (ClosePath) path.Close();
        canvas.DrawPath(path, paint);

        for (int py = 0; py < layer.Height; py++)
        {
            for (int px = 0; px < layer.Width; px++)
            {
                if (!ctx.Selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY)) continue;
                var pix = bmp.GetPixel(px, py);
                if (pix.Alpha == 0) continue;
                layer.Pixels.SetPixel(px, py, pix.Blue, pix.Green, pix.Red, pix.Alpha);
            }
        }

        layer.MarkThumbnailDirty();
        var dirty = new PixelRegion(layer.OffsetX, layer.OffsetY, layer.Width, layer.Height);
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }
}
