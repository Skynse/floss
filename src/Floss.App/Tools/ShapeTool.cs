using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public enum ShapeKind { Rectangle, Ellipse, Line }
public enum ShapeDrawMode { Fill, Stroke, FillAndStroke }

// Drag to preview a shape; release to rasterize it onto the active layer.
public sealed class ShapeTool : ITool
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
    public ShapeDrawMode DrawMode { get; set; } = ShapeDrawMode.Fill;
    public float StrokeWidth { get; set; } = 4f;

    private bool _dragging;
    private SKPoint _start, _end;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _dragging = true;
        _start = _end = new SKPoint((float)s.X, (float)s.Y);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        if (!_dragging) return;
        _end = new SKPoint((float)s.X, (float)s.Y);
        ctx.InvalidateRender();
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        if (!_dragging) return;
        _dragging = false;
        _end = new SKPoint((float)s.X, (float)s.Y);
        Apply(ctx);
        ctx.InvalidateRender();
    }

    public void Cancel(ToolContext ctx)
    {
        _dragging = false;
        ctx.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        if (!_dragging) return;
        var t = Math.Max(0.5, 1.0 / zoom);
        var pen = new Pen(Avalonia.Media.Brushes.White, t, new DashStyle([4, 4], 0));
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, new DashStyle([4, 4], 4));
        DrawPreview(dc, pen, penK);
    }

    private void DrawPreview(DrawingContext dc, Pen penW, Pen penK)
    {
        var r = MakeRect(_start, _end);
        switch (Kind)
        {
            case ShapeKind.Rectangle:
                dc.DrawRectangle(null, penW, r);
                dc.DrawRectangle(null, penK, r);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(null, penW, r.Center, r.Width * 0.5, r.Height * 0.5);
                dc.DrawEllipse(null, penK, r.Center, r.Width * 0.5, r.Height * 0.5);
                break;
            case ShapeKind.Line:
                dc.DrawLine(penW, new Point(_start.X, _start.Y), new Point(_end.X, _end.Y));
                dc.DrawLine(penK, new Point(_start.X, _start.Y), new Point(_end.X, _end.Y));
                break;
        }
    }

    private void Apply(ToolContext ctx)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsLocked) return;

        float lsx = _start.X - layer.OffsetX, lsy = _start.Y - layer.OffsetY;
        float lex = _end.X - layer.OffsetX, ley = _end.Y - layer.OffsetY;

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);

        using var bmp = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var c = ctx.PaintColor;
        var skColor = new SKColor(c.R, c.G, c.B, c.A);
        using var fillPaint = new SKPaint { Color = skColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint { Color = skColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = StrokeWidth };

        var rect = new SKRect(Math.Min(lsx, lex), Math.Min(lsy, ley), Math.Max(lsx, lex), Math.Max(lsy, ley));

        bool doFill   = DrawMode is ShapeDrawMode.Fill or ShapeDrawMode.FillAndStroke;
        bool doStroke = DrawMode is ShapeDrawMode.Stroke or ShapeDrawMode.FillAndStroke;

        switch (Kind)
        {
            case ShapeKind.Rectangle:
                if (doFill) canvas.DrawRect(rect, fillPaint);
                if (doStroke) canvas.DrawRect(rect, strokePaint);
                break;
            case ShapeKind.Ellipse:
                if (doFill) canvas.DrawOval(rect, fillPaint);
                if (doStroke) canvas.DrawOval(rect, strokePaint);
                break;
            case ShapeKind.Line:
                canvas.DrawLine(lsx, lsy, lex, ley, strokePaint);
                break;
        }

        // Blit into layer respecting selection.
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

    private static Rect MakeRect(SKPoint a, SKPoint b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
}
