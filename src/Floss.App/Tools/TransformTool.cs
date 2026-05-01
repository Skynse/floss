using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public sealed class TransformTool : ITool
{
    private SelectionTransformOperation? _operation;
    private ITool? _previousTool;

    public bool HasPendingOperation => _operation != null;

    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public bool BeginTransform(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = SelectionTransformOperation.TryCreate(ctx);
        ctx.InvalidateRender();
        return _operation != null;
    }

    public void SetPreviousTool(ITool? tool) => _previousTool = tool;
    public ITool? GetPreviousTool() => _previousTool;

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        if (_operation == null)
            BeginTransform(ctx);
        _operation?.PointerDown(s);

        if (_operation?.RequestedAction == OverlayAction.Commit)
            Commit(ctx);
        else if (_operation?.RequestedAction == OverlayAction.Cancel)
            Cancel(ctx);
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

    public StandardCursorType? CursorFor(Point canvasPos, double zoom)
        => _operation?.CursorFor(canvasPos, zoom);
}

internal enum OverlayAction
{
    None,
    Commit,
    Cancel
}

internal enum TransformDragPart
{
    None,
    Move,
    Rotate,
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
    private double _angle;
    private double _startAngle;
    private double _dragStartAngle;
    private Point _dragStart;
    private TransformDragPart _dragPart;
    private bool _isDragging;

    public OverlayAction RequestedAction { get; private set; }

    private static readonly IBrush BtnBg = new SolidColorBrush(Color.Parse("#2a2e38"));
    private static readonly IBrush BtnBgHover = new SolidColorBrush(Color.Parse("#3a4050"));
    private static readonly Pen BtnBorder = new(new SolidColorBrush(Color.Parse("#4a5268")), 1);
    private static readonly IBrush BtnText = new SolidColorBrush(Color.Parse("#c8d0e0"));
    private static readonly IBrush AccentBlue = new SolidColorBrush(Color.Parse("#5a9fd8"));
    private static readonly IBrush AccentRed = new SolidColorBrush(Color.Parse("#d85a5a"));

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
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return null;

        PixelRegion b;
        bool usingSelection;

        if (ctx.Selection.HasSelection)
        {
            var bounds = ctx.Selection.GetMaskBounds();
            if (bounds == null) return null;
            b = new PixelRegion(bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height);
            usingSelection = true;
        }
        else
        {
            var contentBounds = layer.Pixels.ComputeContentBounds();
            if (contentBounds.IsEmpty) return null;
            b = contentBounds.Translate(layer.OffsetX, layer.OffsetY);
            usingSelection = false;
        }

        if (b.Width <= 0 || b.Height <= 0) return null;

        var layerBounds = new PixelRegion(
            b.X - layer.OffsetX,
            b.Y - layer.OffsetY,
            b.Width,
            b.Height).ClipTo(layer.Width, layer.Height);
        if (layerBounds.IsEmpty) return null;

        var floatPixels = new byte[b.Width * b.Height * 4];
        var beforeTiles = layer.Pixels.CaptureTiles(layerBounds);
        var hasPixels = false;

        for (var docY = b.Y; docY < b.Bottom; docY++)
        {
            var layY = docY - layer.OffsetY;
            if ((uint)layY >= (uint)layer.Height) continue;
            for (var docX = b.X; docX < b.Right; docX++)
            {
                if (usingSelection && !ctx.Selection.IsSelected(docX, docY)) continue;

                var layX = docX - layer.OffsetX;
                if ((uint)layX >= (uint)layer.Width) continue;
                layer.Pixels.GetPixel(layX, layY, out var pxB, out var pxG, out var pxR, out var pxA);
                if (pxA == 0) continue;

                var fi = ((docY - b.Y) * b.Width + (docX - b.X)) * 4;
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
        ctx.Document.NotifyChanged(b, ctx.ActiveLayerIndex);
        return new SelectionTransformOperation(ctx, ctx.ActiveLayerIndex, b.X, b.Y, b.Width, b.Height, floatPixels, beforeTiles);
    }

    public void PointerDown(CanvasInputSample sample)
    {
        RequestedAction = OverlayAction.None;

        var btn = HitTestButton(sample.X, sample.Y);
        if (btn == OverlayAction.Commit || btn == OverlayAction.Cancel)
        {
            RequestedAction = btn;
            return;
        }

        _dragPart = HitTest(sample.X, sample.Y, _context.ActiveLayer == null ? 1 : 1);
        if (_dragPart == TransformDragPart.None) return;

        _isDragging = true;
        _dragStart = new Point(sample.X, sample.Y);
        _startRect = _rect;
        _startAngle = _angle;

        if (_dragPart == TransformDragPart.Rotate)
        {
            var center = CenterOf(_rect);
            _dragStartAngle = Math.Atan2(sample.Y - center.Y, sample.X - center.X) * 180 / Math.PI;
        }
    }

    public void Update(CanvasInputSample sample)
    {
        if (!_isDragging) return;

        var current = new Point(sample.X, sample.Y);
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;

        if (_dragPart == TransformDragPart.Rotate)
        {
            var center = CenterOf(_startRect);
            var currentAngle = Math.Atan2(current.Y - center.Y, current.X - center.X) * 180 / Math.PI;
            _angle = _startAngle + (currentAngle - _dragStartAngle);
        }
        else if (_dragPart == TransformDragPart.Move)
        {
            _rect = _startRect.Translate(new Vector(dx, dy));
        }
        else
        {
            _rect = ResizeRect(_startRect, _dragPart, dx, dy);
        }

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

        var dest = RotatedBounds(_rect, _angle);
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
        StampRotated(layer, dest);

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
        var center = CenterOf(rect);
        var angleRad = _angle * Math.PI / 180;

        var matrix = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angleRad)
            * Matrix.CreateTranslation(center.X, center.Y);

        using (dc.PushTransform(matrix))
        {
            dc.DrawImage(_overlayBitmap, rect);

            var t = Math.Max(0.75, 1.0 / zoom);
            var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(90, 150, 255)), t);
            var fill = new SolidColorBrush(Color.FromRgb(245, 248, 255));
            dc.DrawRectangle(null, borderPen, rect);

            var handleSize = Math.Max(6.0 / zoom, t * 5);
            foreach (var handle in Handles(rect))
            {
                var color = handle.Part == TransformDragPart.Rotate
                    ? new SolidColorBrush(Color.FromRgb(255, 180, 60))
                    : fill;
                dc.DrawRectangle(color, borderPen, CenteredRect(handle.Point, handleSize));
            }
        }

        // Draw OK / Cancel buttons below the transform box (in screen space, not rotated)
        DrawButtons(dc, rect, angleRad, center, zoom);
    }

    public StandardCursorType? CursorFor(Point canvasPos, double zoom)
    {
        var btn = HitTestButton(canvasPos.X, canvasPos.Y);
        if (btn == OverlayAction.Commit) return StandardCursorType.Hand;
        if (btn == OverlayAction.Cancel) return StandardCursorType.Hand;

        var part = HitTest(canvasPos.X, canvasPos.Y, zoom);
        return part switch
        {
            TransformDragPart.Move => StandardCursorType.SizeAll,
            TransformDragPart.Rotate => StandardCursorType.Hand,
            TransformDragPart.Top or TransformDragPart.Bottom => StandardCursorType.SizeNorthSouth,
            TransformDragPart.Left or TransformDragPart.Right => StandardCursorType.SizeWestEast,
            TransformDragPart.TopLeft or TransformDragPart.BottomRight => StandardCursorType.TopLeftCorner,
            TransformDragPart.TopRight or TransformDragPart.BottomLeft => StandardCursorType.TopRightCorner,
            _ => null
        };
    }

    private void DrawButtons(DrawingContext dc, Rect rect, double angleRad, Point center, double zoom)
    {
        var btnW = 52 / zoom;
        var btnH = 22 / zoom;
        var gap = 6 / zoom;
        var below = 18 / zoom;

        // Compute bottom-center of the rotated rect in canvas space
        var rotMatrix = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angleRad)
            * Matrix.CreateTranslation(center.X, center.Y);

        var bottomCenter = new Point(rect.X + rect.Width / 2, rect.Bottom + below);
        var screenBottomCenter = rotMatrix.Transform(bottomCenter);

        var totalW = btnW * 2 + gap;
        var okRect = new Rect(screenBottomCenter.X - totalW / 2, screenBottomCenter.Y, btnW, btnH);
        var cancelRect = new Rect(okRect.Right + gap, screenBottomCenter.Y, btnW, btnH);

        DrawButton(dc, okRect, "OK", AccentBlue);
        DrawButton(dc, cancelRect, "✕", AccentRed);
    }

    private static void DrawButton(DrawingContext dc, Rect r, string text, IBrush accent)
    {
        dc.FillRectangle(BtnBg, r);
        dc.DrawRectangle(BtnBorder, r);

        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, BtnText);
        dc.DrawText(ft, new Point(
            r.X + (r.Width - ft.Width) * 0.5,
            r.Y + (r.Height - ft.Height) * 0.5));
    }

    private OverlayAction HitTestButton(double x, double y)
    {
        // Simplified: we recompute button rects in screen space and test against (x,y)
        var rect = NormalizedRect(_rect);
        var center = CenterOf(rect);
        var angleRad = _angle * Math.PI / 180;
        var zoom = _context.ActiveLayer == null ? 1 : 1; // rough fallback
        var btnW = 52 / zoom;
        var btnH = 22 / zoom;
        var gap = 6 / zoom;
        var below = 18 / zoom;

        var rotMatrix = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angleRad)
            * Matrix.CreateTranslation(center.X, center.Y);

        var bottomCenter = new Point(rect.X + rect.Width / 2, rect.Bottom + below);
        var screenBottomCenter = rotMatrix.Transform(bottomCenter);
        var totalW = btnW * 2 + gap;
        var okRect = new Rect(screenBottomCenter.X - totalW / 2, screenBottomCenter.Y, btnW, btnH);
        var cancelRect = new Rect(okRect.Right + gap, screenBottomCenter.Y, btnW, btnH);

        if (okRect.Contains(new Point(x, y))) return OverlayAction.Commit;
        if (cancelRect.Contains(new Point(x, y))) return OverlayAction.Cancel;
        return OverlayAction.None;
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

        if (rect.Contains(p))
            return TransformDragPart.Move;

        return TransformDragPart.Rotate;
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

    private static PixelRegion RotatedBounds(Rect rect, double angleDeg)
    {
        if (Math.Abs(angleDeg % 360) < 0.1)
            return NormalizedPixelRect(rect);

        var r = NormalizedRect(rect);
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        var angle = angleDeg * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));

        var w = r.Width * cos + r.Height * sin;
        var h = r.Width * sin + r.Height * cos;

        var x = (int)Math.Round(cx - w / 2);
        var y = (int)Math.Round(cy - h / 2);
        return new PixelRegion(x, y, (int)Math.Round(w), (int)Math.Round(h));
    }

    private void StampRotated(DrawingLayer layer, PixelRegion dest)
    {
        var clipped = new PixelRegion(
            dest.X - layer.OffsetX,
            dest.Y - layer.OffsetY,
            dest.Width,
            dest.Height).ClipTo(layer.Width, layer.Height);
        if (clipped.IsEmpty) return;

        var info = new SKImageInfo(_sourceW, _sourceH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var srcBitmap = new SKBitmap();
        var handle = GCHandle.Alloc(_floatPixels, GCHandleType.Pinned);
        try
        {
            srcBitmap.InstallPixels(info, handle.AddrOfPinnedObject());

            var center = CenterOf(NormalizedRect(_rect));
            var r = NormalizedRect(_rect);

            layer.Pixels.RenderWithSkia(clipped, canvas =>
            {
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    BlendMode = SKBlendMode.SrcOver
                };

                canvas.Save();
                canvas.Translate((float)center.X, (float)center.Y);
                canvas.RotateDegrees((float)_angle);
                canvas.Translate((float)(-center.X), (float)(-center.Y));
                canvas.DrawBitmap(srcBitmap,
                    new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom),
                    paint);
                canvas.Restore();
            });
        }
        finally
        {
            handle.Free();
        }
    }

    private static Point CenterOf(Rect rect)
    {
        var r = NormalizedRect(rect);
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
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

        // Free rotation handle below the box
        var rotY = rect.Bottom + Math.Max(rect.Height * 0.25, 12);
        yield return (TransformDragPart.Rotate, new Point(midX, rotY));
    }
}
