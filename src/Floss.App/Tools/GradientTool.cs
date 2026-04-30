using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public enum GradientType { Linear, Radial }

// Drag to define gradient direction; release to apply to the active layer (respects selection).
public sealed class GradientTool : ITool
{
    public GradientType GradientType { get; set; } = GradientType.Linear;

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
        var white = Avalonia.Media.Brushes.White;
        var black = Avalonia.Media.Brushes.Black;
        var penW = new Pen(white, t);
        var penK = new Pen(black, t, new DashStyle([4, 4], 0));
        var a = new Avalonia.Point(_start.X, _start.Y);
        var b = new Avalonia.Point(_end.X, _end.Y);
        dc.DrawLine(penW, a, b);
        dc.DrawLine(penK, a, b);
        dc.DrawEllipse(white, null, a, 4 / zoom, 4 / zoom);
        dc.DrawEllipse(white, null, b, 3 / zoom, 3 / zoom);
    }

    private void Apply(ToolContext ctx)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsLocked) return;

        // Build an image-space shader covering the whole layer.
        int w = layer.Width, h = layer.Height;
        float sx = _start.X - layer.OffsetX, sy = _start.Y - layer.OffsetY;
        float ex = _end.X - layer.OffsetX, ey = _end.Y - layer.OffsetY;

        var c = ctx.PaintColor;
        var skFrom = new SKColor(c.R, c.G, c.B, c.A);
        var skTo   = new SKColor(c.R, c.G, c.B, 0);

        SKShader shader;
        if (GradientType == GradientType.Radial)
        {
            float radius = (float)Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
            if (radius < 1) return;
            shader = SKShader.CreateRadialGradient(
                new SKPoint(sx, sy), radius,
                [skFrom, skTo], SKShaderTileMode.Clamp);
        }
        else
        {
            var dx = ex - sx; var dy = ey - sy;
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

        // Blit into layer respecting selection.
        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                if (!ctx.Selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY)) continue;
                var pix = bmp.GetPixel(px, py);
                if (pix.Alpha == 0) continue;
                layer.Pixels.SetPixel(px, py, pix.Blue, pix.Green, pix.Red, pix.Alpha);
            }
        }

        layer.MarkThumbnailDirty();
        var dirtyRegion = new PixelRegion(layer.OffsetX, layer.OffsetY, w, h);
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirtyRegion);
    }
}
