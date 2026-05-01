using System;
using Avalonia;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public sealed class GradientToolOperation : DragToolOperation
{
    private readonly GradientType _gradientType;

    public GradientToolOperation(ToolContext context, CanvasInputSample firstSample, GradientType gradientType)
        : base(context, firstSample)
    {
        _gradientType = gradientType;
    }

    public override void RenderOverlay(DrawingContext dc, double zoom)
    {
        var t = Math.Max(0.5, 1.0 / zoom);
        var white = Avalonia.Media.Brushes.White;
        var black = Avalonia.Media.Brushes.Black;
        var penW = new Pen(white, t);
        var penK = new Pen(black, t, new DashStyle([4, 4], 0));
        var a = new Point(StartSample.X, StartSample.Y);
        var b = new Point(CurrentSample.X, CurrentSample.Y);
        dc.DrawLine(penW, a, b);
        dc.DrawLine(penK, a, b);
        dc.DrawEllipse(white, null, a, 4 / zoom, 4 / zoom);
        dc.DrawEllipse(white, null, b, 3 / zoom, 3 / zoom);
    }

    protected override void Apply()
    {
        var layer = Context.ActiveLayer;
        if (layer == null || !Context.Document.CanPaintActiveLayer) return;

        int w = layer.Width;
        int h = layer.Height;
        float sx = (float)StartSample.X - layer.OffsetX;
        float sy = (float)StartSample.Y - layer.OffsetY;
        float ex = (float)CurrentSample.X - layer.OffsetX;
        float ey = (float)CurrentSample.Y - layer.OffsetY;

        var c = Context.PaintColor;
        var skFrom = new SKColor(c.R, c.G, c.B, c.A);
        var skTo = new SKColor(c.R, c.G, c.B, 0);

        SKShader shader;
        if (_gradientType == GradientType.Radial)
        {
            float radius = (float)Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
            if (radius < 1) return;
            shader = SKShader.CreateRadialGradient(
                new SKPoint(sx, sy), radius,
                [skFrom, skTo], SKShaderTileMode.Clamp);
        }
        else
        {
            var dx = ex - sx;
            var dy = ey - sy;
            if (dx * dx + dy * dy < 1) return;
            shader = SKShader.CreateLinearGradient(
                new SKPoint(sx, sy), new SKPoint(ex, ey),
                [skFrom, skTo], SKShaderTileMode.Clamp);
        }

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);

        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(0, 0, w, h, paint);
        shader.Dispose();

        bool alphaLocked = layer.IsAlphaLocked;
        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                if (!Context.Selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY)) continue;
                var pix = bmp.GetPixel(px, py);
                if (pix.Alpha == 0) continue;
                if (alphaLocked) { layer.Pixels.GetPixel(px, py, out _, out _, out _, out byte ea); if (ea == 0) continue; }
                layer.Pixels.SetPixel(px, py, pix.Blue, pix.Green, pix.Red, pix.Alpha);
            }
        }

        layer.MarkThumbnailDirty();
        var dirtyRegion = new PixelRegion(layer.OffsetX, layer.OffsetY, w, h);
        Context.CommitMutation(Context.ActiveLayerIndex, beforeTiles, dirtyRegion);
    }
}
