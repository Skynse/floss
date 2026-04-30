using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class TransformTool : ITool
{
    private SelectionTransformOperation? _operation;

    public bool HasPendingOperation => _operation != null;

    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public bool BeginTransform(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = SelectionTransformOperation.TryCreate(ctx);
        ctx.InvalidateRender();
        return _operation != null;
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        if (_operation == null)
            BeginTransform(ctx);
        _operation?.PointerDown(s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s) => _operation?.Update(s);

    public void PointerUp(ToolContext ctx, CanvasInputSample s) => _operation?.PointerUp(s);

    public void Cancel(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = null;
        ctx.InvalidateRender();
    }

    public void Commit(ToolContext ctx)
    {
        _operation?.CommitCurrent();
        _operation = null;
        ctx.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
        => _operation?.RenderOverlay(dc, zoom);
}

internal enum TransformDragPart
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

internal sealed class SelectionTransformOperation : IToolOperationOverlay
{
    private readonly ToolContext _context;
    private readonly int _layerIndex;
    private readonly int _sourceX;
    private readonly int _sourceY;
    private readonly int _sourceW;
    private readonly int _sourceH;
    private readonly byte[] _floatPixels;
    private readonly Dictionary<(int, int), byte[]?> _beforeTiles;

    private WriteableBitmap? _overlayBitmap;
    private Rect _rect;
    private Rect _startRect;
    private Point _dragStart;
    private TransformDragPart _dragPart;
    private bool _isDragging;

    private SelectionTransformOperation(
        ToolContext context,
        int layerIndex,
        int sourceX,
        int sourceY,
        int sourceW,
        int sourceH,
        byte[] floatPixels,
        Dictionary<(int, int), byte[]?> beforeTiles)
    {
        _context = context;
        _layerIndex = layerIndex;
        _sourceX = sourceX;
        _sourceY = sourceY;
        _sourceW = sourceW;
        _sourceH = sourceH;
        _floatPixels = floatPixels;
        _beforeTiles = beforeTiles;
        _rect = new Rect(sourceX, sourceY, sourceW, sourceH);
    }

    public static SelectionTransformOperation? TryCreate(ToolContext ctx)
    {
        if (!ctx.Selection.HasSelection) return null;
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return null;

        var bounds = ctx.Selection.GetMaskBounds();
        if (bounds == null) return null;

        var b = bounds.Value;
        if (b.Width <= 0 || b.Height <= 0) return null;

        var layerBounds = new PixelRegion(
            b.Left - layer.OffsetX,
            b.Top - layer.OffsetY,
            b.Width,
            b.Height).ClipTo(layer.Width, layer.Height);
        if (layerBounds.IsEmpty) return null;

        var floatPixels = new byte[b.Width * b.Height * 4];
        var beforeTiles = layer.Pixels.CaptureTiles(layerBounds);
        var hasPixels = false;

        for (var docY = b.Top; docY < b.Bottom; docY++)
        {
            var layY = docY - layer.OffsetY;
            if ((uint)layY >= (uint)layer.Height) continue;
            for (var docX = b.Left; docX < b.Right; docX++)
            {
                if (!ctx.Selection.IsSelected(docX, docY)) continue;

                var layX = docX - layer.OffsetX;
                if ((uint)layX >= (uint)layer.Width) continue;
                layer.Pixels.GetPixel(layX, layY, out var pxB, out var pxG, out var pxR, out var pxA);
                if (pxA == 0) continue;

                var fi = ((docY - b.Top) * b.Width + (docX - b.Left)) * 4;
                floatPixels[fi] = pxB;
                floatPixels[fi + 1] = pxG;
                floatPixels[fi + 2] = pxR;
                floatPixels[fi + 3] = pxA;
                layer.Pixels.SetPixel(layX, layY, 0, 0, 0, 0);
                hasPixels = true;
            }
        }

        if (!hasPixels) return null;

        layer.MarkThumbnailDirty();
        ctx.Document.NotifyChanged(new PixelRegion(b.Left, b.Top, b.Width, b.Height), ctx.ActiveLayerIndex);
        return new SelectionTransformOperation(ctx, ctx.ActiveLayerIndex, b.Left, b.Top, b.Width, b.Height, floatPixels, beforeTiles);
    }

    public void PointerDown(CanvasInputSample sample)
    {
        _dragPart = HitTest(sample.X, sample.Y, _context.ActiveLayer == null ? 1 : 1);
        if (_dragPart == TransformDragPart.None) return;

        _isDragging = true;
        _dragStart = new Point(sample.X, sample.Y);
        _startRect = _rect;
    }

    public void Update(CanvasInputSample sample)
    {
        if (!_isDragging) return;

        var current = new Point(sample.X, sample.Y);
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;

        _rect = _dragPart == TransformDragPart.Move
            ? _startRect.Translate(new Vector(dx, dy))
            : ResizeRect(_startRect, _dragPart, dx, dy);
        _context.InvalidateRender();
    }

    public void PointerUp(CanvasInputSample sample)
    {
        Update(sample);
        _isDragging = false;
        _dragPart = TransformDragPart.None;
    }

    public void Cancel()
    {
        var layer = _context.ActiveLayer;
        if (layer != null && _layerIndex == _context.ActiveLayerIndex)
        {
            for (var relY = 0; relY < _sourceH; relY++)
            {
                var layY = _sourceY + relY - layer.OffsetY;
                if ((uint)layY >= (uint)layer.Height) continue;
                for (var relX = 0; relX < _sourceW; relX++)
                {
                    var fi = (relY * _sourceW + relX) * 4;
                    if (_floatPixels[fi + 3] == 0) continue;
                    var layX = _sourceX + relX - layer.OffsetX;
                    if ((uint)layX >= (uint)layer.Width) continue;
                    layer.Pixels.SetPixel(layX, layY,
                        _floatPixels[fi],
                        _floatPixels[fi + 1],
                        _floatPixels[fi + 2],
                        _floatPixels[fi + 3]);
                }
            }

            layer.MarkThumbnailDirty();
            _context.Document.NotifyChanged(new PixelRegion(_sourceX, _sourceY, _sourceW, _sourceH), _layerIndex);
        }

        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
    }

    public void CommitCurrent()
    {
        var layer = _context.ActiveLayer;
        if (layer == null || _layerIndex != _context.ActiveLayerIndex) return;

        var dest = NormalizedPixelRect(_rect);
        if (dest.IsEmpty) return;

        var destLayerRegion = new PixelRegion(
            dest.X - layer.OffsetX,
            dest.Y - layer.OffsetY,
            dest.Width,
            dest.Height).ClipTo(layer.Width, layer.Height);
        var destTiles = layer.Pixels.CaptureTiles(destLayerRegion);
        foreach (var (key, value) in destTiles)
            _beforeTiles.TryAdd(key, value);

        var dirty = new PixelRegion(_sourceX, _sourceY, _sourceW, _sourceH).Union(dest);
        StampScaled(layer, dest);

        layer.MarkThumbnailDirty();
        _context.Selection.SetFromRect(dest.X, dest.Y, dest.Width, dest.Height);
        _context.CommitMutation(_layerIndex, _beforeTiles, dirty);
        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        EnsureOverlayBitmap();
        if (_overlayBitmap == null) return;

        var rect = NormalizedRect(_rect);
        dc.DrawImage(_overlayBitmap, rect);

        var t = Math.Max(0.75, 1.0 / zoom);
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(90, 150, 255)), t);
        var fill = new SolidColorBrush(Color.FromRgb(245, 248, 255));
        dc.DrawRectangle(null, borderPen, rect);

        var handleSize = Math.Max(6.0 / zoom, t * 5);
        foreach (var handle in Handles(rect))
            dc.DrawRectangle(fill, borderPen, CenteredRect(handle.Point, handleSize));
    }

    private void EnsureOverlayBitmap()
    {
        if (_overlayBitmap != null) return;
        _overlayBitmap = new WriteableBitmap(
            new PixelSize(_sourceW, _sourceH),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using var fb = _overlayBitmap.Lock();
        Marshal.Copy(_floatPixels, 0, fb.Address, _floatPixels.Length);
    }

    private TransformDragPart HitTest(double x, double y, double zoom)
    {
        var p = new Point(x, y);
        var rect = NormalizedRect(_rect);
        var handleSize = Math.Max(8.0 / zoom, 6);
        foreach (var handle in Handles(rect))
        {
            if (CenteredRect(handle.Point, handleSize).Contains(p))
                return handle.Part;
        }

        return rect.Contains(p) ? TransformDragPart.Move : TransformDragPart.None;
    }

    private static Rect ResizeRect(Rect rect, TransformDragPart part, double dx, double dy)
    {
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (part is TransformDragPart.TopLeft or TransformDragPart.Left or TransformDragPart.BottomLeft) left += dx;
        if (part is TransformDragPart.TopRight or TransformDragPart.Right or TransformDragPart.BottomRight) right += dx;
        if (part is TransformDragPart.TopLeft or TransformDragPart.Top or TransformDragPart.TopRight) top += dy;
        if (part is TransformDragPart.BottomLeft or TransformDragPart.Bottom or TransformDragPart.BottomRight) bottom += dy;

        if (Math.Abs(right - left) < 1) right = left + Math.Sign(right - left == 0 ? 1 : right - left);
        if (Math.Abs(bottom - top) < 1) bottom = top + Math.Sign(bottom - top == 0 ? 1 : bottom - top);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static Rect NormalizedRect(Rect rect)
    {
        var left = Math.Min(rect.Left, rect.Right);
        var top = Math.Min(rect.Top, rect.Bottom);
        var right = Math.Max(rect.Left, rect.Right);
        var bottom = Math.Max(rect.Top, rect.Bottom);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static PixelRegion NormalizedPixelRect(Rect rect)
    {
        var r = NormalizedRect(rect);
        var x = (int)Math.Round(r.X);
        var y = (int)Math.Round(r.Y);
        var w = Math.Max(1, (int)Math.Round(r.Width));
        var h = Math.Max(1, (int)Math.Round(r.Height));
        return new PixelRegion(x, y, w, h);
    }

    private void StampScaled(DrawingLayer layer, PixelRegion dest)
    {
        var clipped = new PixelRegion(
            dest.X - layer.OffsetX,
            dest.Y - layer.OffsetY,
            dest.Width,
            dest.Height).ClipTo(layer.Width, layer.Height);
        if (clipped.IsEmpty) return;

        for (var layY = clipped.Y; layY < clipped.Bottom; layY++)
        {
            var docY = layY + layer.OffsetY;
            var sy = Math.Clamp((int)((docY - dest.Y + 0.5) * _sourceH / dest.Height), 0, _sourceH - 1);
            for (var layX = clipped.X; layX < clipped.Right; layX++)
            {
                var docX = layX + layer.OffsetX;
                var sx = Math.Clamp((int)((docX - dest.X + 0.5) * _sourceW / dest.Width), 0, _sourceW - 1);
                var si = (sy * _sourceW + sx) * 4;
                if (_floatPixels[si + 3] == 0) continue;

                if (layer.IsAlphaLocked)
                {
                    layer.Pixels.GetPixel(layX, layY, out _, out _, out _, out var existingA);
                    if (existingA == 0) continue;
                }

                layer.Pixels.SetPixel(layX, layY,
                    _floatPixels[si],
                    _floatPixels[si + 1],
                    _floatPixels[si + 2],
                    _floatPixels[si + 3]);
            }
        }
    }

    private static Rect CenteredRect(Point p, double size)
        => new(p.X - size * 0.5, p.Y - size * 0.5, size, size);

    private static IEnumerable<(TransformDragPart Part, Point Point)> Handles(Rect rect)
    {
        var midX = rect.Left + rect.Width * 0.5;
        var midY = rect.Top + rect.Height * 0.5;
        yield return (TransformDragPart.TopLeft, rect.TopLeft);
        yield return (TransformDragPart.Top, new Point(midX, rect.Top));
        yield return (TransformDragPart.TopRight, rect.TopRight);
        yield return (TransformDragPart.Right, new Point(rect.Right, midY));
        yield return (TransformDragPart.BottomRight, rect.BottomRight);
        yield return (TransformDragPart.Bottom, new Point(midX, rect.Bottom));
        yield return (TransformDragPart.BottomLeft, rect.BottomLeft);
        yield return (TransformDragPart.Left, new Point(rect.Left, midY));
    }
}
