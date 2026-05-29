using System;
using System.Linq;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Applies a linear or radial gradient between two drag points.
public sealed class GradientOutput : IOutputProcess
{
    public bool IsPaintOutput => true;
    public bool Antialiasing { get; set; } = true;
    public GradientType GradientType { get; set; } = GradientType.Linear;
    public double Opacity { get; set; } = 1.0;
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var x1 = drag.Start.X;
        var y1 = drag.Start.Y;
        var x2 = drag.Current.X;
        var y2 = drag.Current.Y;
        var color = ctx.PaintColor;
        var blendMode = BlendMode;
        var effectiveA = (byte)Math.Round(color.A * Math.Clamp(Opacity, 0.0, 1.0));

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        bool changed = false;

        using var bitmap = new SKBitmap(layer.Width, layer.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { IsAntialias = Antialiasing };
        if (GradientType == GradientType.Linear)
        {
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint((float)(x1 - layer.OffsetX), (float)(y1 - layer.OffsetY)),
                new SKPoint((float)(x2 - layer.OffsetX), (float)(y2 - layer.OffsetY)),
                new[] { new SKColor(color.R, color.G, color.B, effectiveA), SKColors.Transparent },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp);
        }
        else
        {
            var cx = (float)(x1 - layer.OffsetX);
            var cy = (float)(y1 - layer.OffsetY);
            var radius = (float)Math.Sqrt(
                Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
            paint.Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), radius,
                new[] { new SKColor(color.R, color.G, color.B, effectiveA), SKColors.Transparent },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp);
        }

        canvas.DrawRect(0, 0, layer.Width, layer.Height, paint);

        // Copy to layer with selection/alpha-lock awareness
        for (int ly = 0; ly < layer.Height; ly++)
        {
            for (int lx = 0; lx < layer.Width; lx++)
            {
                int docX = lx + layer.OffsetX;
                int docY = ly + layer.OffsetY;
                if (!ctx.Selection.IsSelected(docX, docY)) continue;

                var skColor = bitmap.GetPixel(lx, ly);
                if (skColor.Alpha == 0) continue;

                if (AlphaLockPixelOps.TryWriteColor(layer.Pixels, lx, ly,
                        skColor.Blue, skColor.Green, skColor.Red, skColor.Alpha, layer.IsAlphaLocked, blendMode))
                    changed = true;
            }
        }

        if (!changed) return;

        var dirty = new PixelRegion(layer.OffsetX, layer.OffsetY, layer.Width, layer.Height);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }
}
