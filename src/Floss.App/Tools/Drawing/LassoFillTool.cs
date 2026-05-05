using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

// Draw a freehand lasso and fill the enclosed area with the current paint color.
public sealed class LassoFillTool : ITool
{
    private readonly List<SKPoint> _points = [];
    private bool _isDrawing;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _points.Clear();
        _points.Add(new SKPoint((float)s.X, (float)s.Y));
        _isDrawing = true;
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        if (!_isDrawing) return;
        _points.Add(new SKPoint((float)s.X, (float)s.Y));
        ctx.InvalidateRender();
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        _points.Add(new SKPoint((float)s.X, (float)s.Y));

        if (_points.Count < 3)
        {
            _points.Clear();
            ctx.InvalidateRender();
            return;
        }

        CommitFill(ctx);
        _points.Clear();
        ctx.InvalidateRender();
    }

    public void Cancel(ToolContext ctx)
    {
        _isDrawing = false;
        _points.Clear();
        ctx.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        if (_points.Count < 2) return;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Avalonia.Point(_points[0].X, _points[0].Y), false);
            for (int i = 1; i < _points.Count; i++)
                c.LineTo(new Avalonia.Point(_points[i].X, _points[i].Y));
            c.EndFigure(false);
        }
        var t = System.Math.Max(0.5, 1.0 / zoom);
        dc.DrawGeometry(null, new Pen(Avalonia.Media.Brushes.White, t), geo);
    }

    private void CommitFill(ToolContext ctx)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        using var path = new SKPath();
        path.MoveTo(_points[0]);
        for (int i = 1; i < _points.Count; i++) path.LineTo(_points[i]);
        path.Close();

        using var region = new SKRegion();
        region.SetPath(path, new SKRegion(new SKRectI(0, 0, ctx.Document.Width, ctx.Document.Height)));

        var bounds = path.Bounds;
        int x1 = System.Math.Clamp((int)bounds.Left, 0, ctx.Document.Width - 1);
        int y1 = System.Math.Clamp((int)bounds.Top, 0, ctx.Document.Height - 1);
        int x2 = System.Math.Clamp((int)System.Math.Ceiling(bounds.Right), 0, ctx.Document.Width - 1);
        int y2 = System.Math.Clamp((int)System.Math.Ceiling(bounds.Bottom), 0, ctx.Document.Height - 1);

        var beforeTiles = layer.Pixels.CaptureTiles(layer.Pixels.Bounds);
        var c = ctx.PaintColor;
        byte fillB = c.B, fillG = c.G, fillR = c.R, fillA = c.A;
        bool changed = false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (int docY = y1; docY <= y2; docY++)
        {
            for (int docX = x1; docX <= x2; docX++)
            {
                if (!region.Contains(docX, docY)) continue;
                if (!ctx.Selection.IsSelected(docX, docY)) continue;

                int lx = docX - layer.OffsetX;
                int ly = docY - layer.OffsetY;

                if (layer.IsAlphaLocked)
                {
                    layer.Pixels.GetPixel(lx, ly, out _, out _, out _, out byte ea);
                    if (ea == 0) continue;
                }

                layer.Pixels.SetPixel(lx, ly, fillB, fillG, fillR, fillA);
                changed = true;
                minX = System.Math.Min(minX, docX);
                minY = System.Math.Min(minY, docY);
                maxX = System.Math.Max(maxX, docX);
                maxY = System.Math.Max(maxY, docY);
            }
        }

        if (!changed) return;

        var dirty = new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
        layer.MarkThumbnailDirty();
        ctx.CommitMutation(ctx.ActiveLayerIndex, beforeTiles, dirty);
    }
}
