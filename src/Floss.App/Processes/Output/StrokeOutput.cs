using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Strokes a polygon or path onto the active layer.
public sealed class StrokeOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = true;
    public float StrokeWidth { get; set; } = 4;
    public bool ClosePath { get; set; }
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public ShapeDrawMode ShapeDrawMode { get; set; } = ShapeDrawMode.Fill;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var color = ctx.PaintColor;
        var points = new System.Collections.Generic.List<CanvasInputSample>();

        switch (input)
        {
            case PolygonInput poly when poly.SmoothedPoints.Count >= 2:
                points = poly.SmoothedPoints;
                break;
            case RectInput rect:
                {
                    float x1 = (float)Math.Min(rect.Start.X, rect.End.X);
                    float y1 = (float)Math.Min(rect.Start.Y, rect.End.Y);
                    float x2 = (float)Math.Max(rect.Start.X, rect.End.X);
                    float y2 = (float)Math.Max(rect.Start.Y, rect.End.Y);
                    points = shape(ShapeKind, x1, y1, x2, y2);
                }
                break;
            default:
                return;
        }

        using var bitmap = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var fillPaint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A),
            IsAntialias = Antialiasing,
            Style = SKPaintStyle.Fill
        };
        using var strokePaint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A),
            StrokeWidth = StrokeWidth,
            IsAntialias = Antialiasing,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var path = new SKPath();
        path.MoveTo((float)(points[0].X - layer.OffsetX), (float)(points[0].Y - layer.OffsetY));
        for (int i = 1; i < points.Count; i++)
            path.LineTo((float)(points[i].X - layer.OffsetX), (float)(points[i].Y - layer.OffsetY));
        if (ClosePath) path.Close();

        // Line shapes are always stroked (zero-area fill is invisible)
        if (ShapeKind == ShapeKind.Line)
        {
            canvas.DrawPath(path, strokePaint);
        }
        else if (ShapeDrawMode == ShapeDrawMode.FillAndStroke)
        {
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }
        else if (ShapeDrawMode == ShapeDrawMode.Fill)
        {
            canvas.DrawPath(path, fillPaint);
        }
        else
        {
            canvas.DrawPath(path, strokePaint);
        }

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        bool changed = false;

        for (int ly = 0; ly < layer.Height; ly++)
        {
            for (int lx = 0; lx < layer.Width; lx++)
            {
                int docX = lx + layer.OffsetX;
                int docY = ly + layer.OffsetY;
                if (!ctx.Selection.IsSelected(docX, docY)) continue;

                var skColor = bitmap.GetPixel(lx, ly);
                if (skColor.Alpha == 0) continue;

                if (layer.IsAlphaLocked)
                {
                    layer.Pixels.GetPixel(lx, ly, out _, out _, out _, out byte ea);
                    if (ea == 0) continue;
                }

                layer.Pixels.SetPixel(lx, ly, skColor.Blue, skColor.Green, skColor.Red, skColor.Alpha);
                changed = true;
            }
        }

        if (!changed) return;

        var dirty = new PixelRegion(layer.OffsetX, layer.OffsetY, layer.Width, layer.Height);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }

    private static List<CanvasInputSample> shape(ShapeKind kind, float x1, float y1, float x2, float y2)
    {
        return kind switch
        {
            ShapeKind.Ellipse => ellipse(x1, y1, x2, y2),
            ShapeKind.Line => [new CanvasInputSample(x1, y1, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move), new CanvasInputSample(x2, y2, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move)],
            _ => [new CanvasInputSample(x1, y1, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move), new CanvasInputSample(x2, y1, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move), new CanvasInputSample(x2, y2, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move), new CanvasInputSample(x1, y2, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move)],
        };
    }

    private static List<CanvasInputSample> ellipse(float x1, float y1, float x2, float y2)
    {
        var cx = (x1 + x2) / 2f;
        var cy = (y1 + y2) / 2f;
        var rx = Math.Abs(x2 - x1) / 2f;
        var ry = Math.Abs(y2 - y1) / 2f;
        var pts = new List<CanvasInputSample>();
        const int segs = 64;
        for (int i = 0; i < segs; i++)
        {
            var a = i * MathF.Tau / segs;
            pts.Add(new CanvasInputSample(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry, 0, 0, 0, 0, 0, 0, 0, CanvasInputPhase.Move));
        }
        return pts;
    }
}
