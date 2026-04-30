using System;
using Avalonia;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public sealed class ShapeToolOperation : DragToolOperation
{
    private readonly ShapeKind _kind;
    private readonly ShapeDrawMode _drawMode;
    private readonly float _strokeWidth;

    public ShapeToolOperation(
        ToolContext context,
        CanvasInputSample firstSample,
        ShapeKind kind,
        ShapeDrawMode drawMode,
        float strokeWidth)
        : base(context, firstSample)
    {
        _kind = kind;
        _drawMode = drawMode;
        _strokeWidth = strokeWidth;
    }

    public override void RenderOverlay(DrawingContext dc, double zoom)
    {
        var t = Math.Max(0.5, 1.0 / zoom);
        var pen = new Pen(Avalonia.Media.Brushes.White, t, new DashStyle([4, 4], 0));
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, new DashStyle([4, 4], 4));
        DrawPreview(dc, pen, penK);
    }

    protected override void Apply()
    {
        var layer = Context.ActiveLayer;
        if (layer == null || !Context.Document.CanPaintActiveLayer) return;

        float lsx = (float)StartSample.X - layer.OffsetX;
        float lsy = (float)StartSample.Y - layer.OffsetY;
        float lex = (float)CurrentSample.X - layer.OffsetX;
        float ley = (float)CurrentSample.Y - layer.OffsetY;

        using var bmp = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        var c = Context.PaintColor;
        var skColor = new SKColor(c.R, c.G, c.B, c.A);
        using var fillPaint = new SKPaint { Color = skColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint { Color = skColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = _strokeWidth };

        var rect = new SKRect(Math.Min(lsx, lex), Math.Min(lsy, ley), Math.Max(lsx, lex), Math.Max(lsy, ley));

        bool doFill = _drawMode is ShapeDrawMode.Fill or ShapeDrawMode.FillAndStroke;
        bool doStroke = _drawMode is ShapeDrawMode.Stroke or ShapeDrawMode.FillAndStroke;

        switch (_kind)
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

        var dirty = FindPaintedBounds(layer, bmp);
        if (dirty.IsEmpty) return;

        var beforeTiles = layer.Pixels.CaptureTiles(dirty);
        for (int py = dirty.Y; py < dirty.Bottom; py++)
        {
            for (int px = dirty.X; px < dirty.Right; px++)
            {
                if (!Context.Selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY)) continue;
                var pix = bmp.GetPixel(px, py);
                if (pix.Alpha == 0) continue;
                layer.Pixels.SetPixel(px, py, pix.Blue, pix.Green, pix.Red, pix.Alpha);
            }
        }

        layer.MarkThumbnailDirty();
        Context.CommitMutation(Context.ActiveLayerIndex, beforeTiles, dirty.Translate(layer.OffsetX, layer.OffsetY));
    }

    private void DrawPreview(DrawingContext dc, Pen penW, Pen penK)
    {
        var r = MakeRect(StartSample, CurrentSample);
        switch (_kind)
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
                dc.DrawLine(penW, new Point(StartSample.X, StartSample.Y), new Point(CurrentSample.X, CurrentSample.Y));
                dc.DrawLine(penK, new Point(StartSample.X, StartSample.Y), new Point(CurrentSample.X, CurrentSample.Y));
                break;
        }
    }

    private static Rect MakeRect(CanvasInputSample a, CanvasInputSample b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private PixelRegion FindPaintedBounds(DrawingLayer layer, SKBitmap bmp)
    {
        var minX = layer.Width;
        var minY = layer.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < layer.Height; y++)
        {
            for (var x = 0; x < layer.Width; x++)
            {
                if (!Context.Selection.IsSelected(x + layer.OffsetX, y + layer.OffsetY)) continue;
                if (bmp.GetPixel(x, y).Alpha == 0) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX ? PixelRegion.Empty : new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
