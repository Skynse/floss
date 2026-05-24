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

internal enum OverlayAction
{
    None,
    Commit,
    Cancel
}

public enum TransformCompletionKind
{
    Commit,
    Cancel,
    Delete
}

public enum TransformMode
{
    ScaleRotate,
    Scale,
    Rotate,
    FreeTransform,
    Distort,
    Skew,
    Perspective
}

public sealed record TransformEditSnapshot(
    TransformMode Mode,
    double ScaleWPercent,
    double ScaleHPercent,
    double Angle,
    bool KeepAspectRatio);

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

public sealed class TransformTool : ITool
{
    private SelectionTransformOperation? _operation;
    private ITool? _previousTool;

    // Called after commit/cancel/delete so the canvas can restore the previous tool.
    public Action<TransformCompletionKind>? OnCompleted { get; set; }

    public bool HasPendingOperation => _operation != null;

    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public bool BeginTransform(ToolContext ctx, IReadOnlyList<int>? layerIndices = null)
    {
        _operation?.Cancel();
        _operation = SelectionTransformOperation.TryCreate(ctx, layerIndices);
        ctx.InvalidateRender();
        return _operation != null;
    }

    public void SetPreviousTool(ITool? tool) => _previousTool = tool;
    public ITool? GetPreviousTool() => _previousTool;

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        if (_operation == null) return;
        _operation.PointerDown(s);

        if (_operation.RequestedAction == OverlayAction.Commit)
            Commit(ctx);
        else if (_operation.RequestedAction == OverlayAction.Cancel)
            Cancel(ctx);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s) => _operation?.Update(s);
    public void PointerUp(ToolContext ctx, CanvasInputSample s) => _operation?.PointerUp(s);
    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) => _operation?.RenderOverlay(dc, zoom);
    public void Activate(ToolContext ctx) { }

    public void Cancel(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = null;
        ctx.InvalidateRender();
        var cb = OnCompleted; OnCompleted = null; cb?.Invoke(TransformCompletionKind.Cancel);
    }

    public void Commit(ToolContext ctx)
    {
        _operation?.CommitCurrent();
        _operation = null;
        ctx.InvalidateRender();
        var cb = OnCompleted; OnCompleted = null; cb?.Invoke(TransformCompletionKind.Commit);
    }

    public void Delete(ToolContext ctx)
    {
        _operation?.CommitDelete();
        _operation = null;
        ctx.InvalidateRender();
        var cb = OnCompleted; OnCompleted = null; cb?.Invoke(TransformCompletionKind.Delete);
    }

    public StandardCursorType? CursorFor(Point canvasPos, double zoom) => _operation?.CursorFor(canvasPos, zoom);

    public TransformEditSnapshot? EditSnapshot => _operation?.Snapshot;

    public void ApplyEdit(TransformEditSnapshot edit) => _operation?.ApplyEdit(edit);

    public void ResetEdit() => _operation?.ResetToBase();

    public void FlipHorizontal() => _operation?.FlipHorizontal();

    public void FlipVertical() => _operation?.FlipVertical();
}

internal sealed class SelectionTransformOperation : IToolOperationOverlay
{
    // Per-layer extraction data
    private readonly record struct LayerData(
        int Index,
        int SrcX, int SrcY, int SrcW, int SrcH,
        byte[] FloatPixels,
        Dictionary<(int, int), byte[]?> BeforeTiles);

    private readonly ToolContext _context;
    private readonly List<LayerData> _layerData = [];

    // Combined bounds (bounds of all layers' content)
    private readonly int _sourceX, _sourceY, _sourceW, _sourceH;

    // Combined float buffer for overlay rendering
    private readonly byte[] _combinedPixels;

    private WriteableBitmap? _overlayBitmap;
    private Rect _rect;
    private Rect _startRect;
    private readonly Rect _baseRect;
    private double _angle;
    private double _startAngle;
    private Point _dragStart;
    private TransformDragPart _dragPart;
    private bool _isDragging;
    private double _lastZoom = 1.0;
    private bool _flipX;
    private bool _flipY;

    public TransformMode Mode { get; set; } = TransformMode.ScaleRotate;
    public bool KeepAspectRatio { get; set; } = true;

    public TransformEditSnapshot Snapshot
    {
        get
        {
            var r = NormalizedRect(_rect);
            return new TransformEditSnapshot(
                Mode,
                _baseRect.Width > 0.001 ? r.Width / _baseRect.Width * 100 : 100,
                _baseRect.Height > 0.001 ? r.Height / _baseRect.Height * 100 : 100,
                _angle,
                KeepAspectRatio);
        }
    }

    public OverlayAction RequestedAction { get; private set; }

    private static readonly IBrush BtnBg = new SolidColorBrush(Color.Parse("#2a2e38"));
    private static readonly IBrush BtnBgHover = new SolidColorBrush(Color.Parse("#3a4050"));
    private static readonly Pen BtnBorder = new(new SolidColorBrush(Color.Parse("#4a5268")), 1);
    private static readonly IBrush BtnText = new SolidColorBrush(Color.Parse("#c8d0e0"));
    private static readonly IBrush AccentBlue = new SolidColorBrush(Color.Parse("#5a9fd8"));
    private static readonly IBrush AccentRed = new SolidColorBrush(Color.Parse("#d85a5a"));

    private SelectionTransformOperation(
        ToolContext context,
        int sourceX, int sourceY, int sourceW, int sourceH,
        byte[] combinedPixels)
    {
        _context = context;
        _sourceX = sourceX;
        _sourceY = sourceY;
        _sourceW = sourceW;
        _sourceH = sourceH;
        _combinedPixels = combinedPixels;
        _rect = new Rect(sourceX, sourceY, sourceW, sourceH);
        _baseRect = _rect;
    }

    public static SelectionTransformOperation? TryCreate(
        ToolContext ctx, IReadOnlyList<int>? layerIndices = null)
    {
        // Determine which layers to transform
        var layers = new List<(int Index, DrawingLayer Layer)>();
        if (layerIndices is { Count: > 0 })
        {
            foreach (var idx in layerIndices)
            {
                if (idx < 0 || idx >= ctx.Document.Layers.Count) continue;
                var l = ctx.Document.Layers[idx];
                if (l.IsGroup || l.IsLocked) continue;
                layers.Add((idx, l));
            }
        }
        else
        {
            var layer = ctx.ActiveLayer;
            if (layer == null || layer.IsGroup || layer.IsLocked) return null;
            layers.Add((ctx.ActiveLayerIndex, layer));
        }

        if (layers.Count == 0) return null;

        // Compute combined bounding box (document space)
        PixelRegion? combinedBounds = null;
        bool usingSelection = ctx.Selection.HasSelection;

        if (usingSelection)
        {
            var maskBounds = ctx.Selection.GetMaskBounds();
            if (maskBounds == null) return null;
            combinedBounds = new PixelRegion(maskBounds.Value.Left, maskBounds.Value.Top, maskBounds.Value.Width, maskBounds.Value.Height);
        }
        else
        {
            foreach (var (_, layer) in layers)
            {
                var b = layer.Pixels.ComputeContentBounds();
                if (b.IsEmpty) continue;
                b = b.Translate(layer.OffsetX, layer.OffsetY);
                combinedBounds = combinedBounds.HasValue ? combinedBounds.Value.Union(b) : b;
            }
        }

        if (combinedBounds == null || combinedBounds.Value.Width <= 0 || combinedBounds.Value.Height <= 0)
            return null;

        var cb = combinedBounds.Value;
        var combined = new byte[cb.Width * cb.Height * 4];

        // Extract pixels from each layer
        var operation = new SelectionTransformOperation(ctx, cb.X, cb.Y, cb.Width, cb.Height, combined);

        foreach (var (layerIdx, layer) in layers)
        {
            var layerBounds = new PixelRegion(
                cb.X - layer.OffsetX,
                cb.Y - layer.OffsetY,
                cb.Width,
                cb.Height);
            if (layerBounds.IsEmpty) continue;

            // Determine clipping base alpha (layer below's content at each pixel)
            byte[]? clipAlpha = null;
            if (layer.IsClipping)
            {
                var clipIdx = layerIdx - 1;
                if (clipIdx >= 0)
                {
                    var clipLayer = ctx.Document.Layers[clipIdx];
                    if (clipLayer != null && !layers.Any(d => d.Index == clipIdx))
                    {
                        clipAlpha = new byte[cb.Width * cb.Height];
                        for (var docY = cb.Y; docY < cb.Bottom; docY++)
                        {
                            var clY = docY - clipLayer.OffsetY;
                            for (var docX = cb.X; docX < cb.Right; docX++)
                            {
                                var clX = docX - clipLayer.OffsetX;
                                clipLayer.Pixels.GetPixel(clX, clY, out _, out _, out _, out var ca);
                                var idx = (docY - cb.Y) * cb.Width + (docX - cb.X);
                                clipAlpha[idx] = ca;
                            }
                        }
                    }
                }
            }

            var floatPixels = new byte[cb.Width * cb.Height * 4];
            var beforeTiles = layer.Pixels.CaptureTiles(layerBounds);
            bool layerHasPixels = false;
            float opacityScale = (float)Math.Clamp(layer.Opacity, 0, 1);

            for (var docY = cb.Y; docY < cb.Bottom; docY++)
            {
                var layY = docY - layer.OffsetY;

                for (var docX = cb.X; docX < cb.Right; docX++)
                {
                    if (usingSelection && !ctx.Selection.IsSelected(docX, docY)) continue;

                    var layX = docX - layer.OffsetX;

                    layer.Pixels.GetPixel(layX, layY, out var pxB, out var pxG, out var pxR, out var pxA);
                    if (pxA == 0) continue;

                    // Apply clipping mask
                    if (clipAlpha != null)
                    {
                        var ci = (docY - cb.Y) * cb.Width + (docX - cb.X);
                        if (clipAlpha[ci] == 0) continue;
                    }

                    var fi = ((docY - cb.Y) * cb.Width + (docX - cb.X)) * 4;
                    floatPixels[fi] = pxB;
                    floatPixels[fi + 1] = pxG;
                    floatPixels[fi + 2] = pxR;
                    floatPixels[fi + 3] = pxA;

                    // Composite into combined overlay (with layer opacity applied for display)
                    var overlayA = (byte)(pxA * opacityScale + 0.5f);
                    combined[fi] = pxB;
                    combined[fi + 1] = pxG;
                    combined[fi + 2] = pxR;
                    combined[fi + 3] = overlayA;

                    layer.Pixels.SetPixel(layX, layY, 0, 0, 0, 0);
                    layerHasPixels = true;
                }
            }

            if (layerHasPixels)
            {
                layer.MarkThumbnailDirty();
                operation._layerData.Add(new LayerData(
                    layerIdx, cb.X, cb.Y, cb.Width, cb.Height,
                    floatPixels, beforeTiles));
            }
        }

        if (operation._layerData.Count > 0)
            ctx.Document.NotifyChanged(cb, ctx.ActiveLayerIndex);
        return operation;
    }

    public void PointerDown(CanvasInputSample sample)
    {
        var pt = new Point(sample.X, sample.Y);
        var btn = HitTestButton(pt.X, pt.Y);
        if (btn != OverlayAction.None)
        {
            RequestedAction = btn;
            return;
        }

        var zoom = _lastZoom;
        var part = HitTest(pt.X, pt.Y, zoom);
        if (part == TransformDragPart.None) return;

        _startRect = NormalizedRect(_rect);
        _startAngle = _angle;
        _dragPart = part;
        _dragStart = pt;
        _isDragging = true;
        RequestedAction = OverlayAction.None;
    }

    public void Update(CanvasInputSample sample)
    {
        if (!_isDragging) return;

        var pt = new Point(sample.X, sample.Y);
        var dx = pt.X - _dragStart.X;
        var dy = pt.Y - _dragStart.Y;
        var rect = _startRect;
        var ctrl = _context.CurrentModifiers.HasFlag(KeyModifiers.Control);
        var uniform = KeepAspectRatio && !ctrl && Mode is TransformMode.ScaleRotate or TransformMode.Scale;

        switch (_dragPart)
        {
            case TransformDragPart.Move:
                if (Mode != TransformMode.Rotate)
                    _rect = new Rect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
                break;

            case TransformDragPart.Rotate:
                if (Mode is TransformMode.ScaleRotate or TransformMode.Rotate or TransformMode.FreeTransform)
                {
                    var c = CenterOf(rect);
                    var startAngle = Math.Atan2(_dragStart.Y - c.Y, _dragStart.X - c.X) * 180 / Math.PI;
                    var currentAngle = Math.Atan2(pt.Y - c.Y, pt.X - c.X) * 180 / Math.PI;
                    _angle = _startAngle + (currentAngle - startAngle);
                }
                break;

            default:
                if (Mode == TransformMode.Rotate)
                    break;
                _rect = uniform && IsCornerHandle(_dragPart)
                    ? UniformScaleRect(rect, _dragPart, dx, dy)
                    : ResizeRect(rect, _dragPart, dx, dy, uniform);
                break;
        }

        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
    }

    public void ApplyEdit(TransformEditSnapshot edit)
    {
        Mode = edit.Mode;
        KeepAspectRatio = edit.KeepAspectRatio;
        _angle = edit.Angle;

        var center = CenterOf(_baseRect);
        var w = Math.Max(8, _baseRect.Width * edit.ScaleWPercent / 100.0);
        var h = Math.Max(8, _baseRect.Height * edit.ScaleHPercent / 100.0);
        if (KeepAspectRatio && Mode is TransformMode.ScaleRotate or TransformMode.Scale)
        {
            var uniform = Math.Max(w / _baseRect.Width, h / _baseRect.Height);
            w = _baseRect.Width * uniform;
            h = _baseRect.Height * uniform;
        }

        _rect = new Rect(center.X - w * 0.5, center.Y - h * 0.5, w, h);
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
    }

    public void ResetToBase()
    {
        _rect = _baseRect;
        _angle = 0;
        _flipX = _flipY = false;
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
    }

    public void FlipHorizontal()
    {
        _flipX = !_flipX;
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
    }

    public void FlipVertical()
    {
        _flipY = !_flipY;
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
    }

    private static bool IsCornerHandle(TransformDragPart part)
        => part is TransformDragPart.TopLeft or TransformDragPart.TopRight
            or TransformDragPart.BottomLeft or TransformDragPart.BottomRight;

    public void PointerUp(CanvasInputSample sample)
    {
        _isDragging = false;
    }

    public void Cancel()
    {
        foreach (var data in _layerData)
        {
            if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;
            var layer = _context.Document.Layers[data.Index];

            for (var relY = 0; relY < data.SrcH; relY++)
            {
                var layY = data.SrcY + relY - layer.OffsetY;

                for (var relX = 0; relX < data.SrcW; relX++)
                {
                    var fi = (relY * data.SrcW + relX) * 4;
                    if (data.FloatPixels[fi + 3] == 0) continue;

                    var layX = data.SrcX + relX - layer.OffsetX;

                    layer.Pixels.SetPixel(layX, layY,
                        data.FloatPixels[fi],
                        data.FloatPixels[fi + 1],
                        data.FloatPixels[fi + 2],
                        data.FloatPixels[fi + 3]);
                }
            }

            layer.MarkThumbnailDirty();
            _context.Document.NotifyChanged(new PixelRegion(_sourceX, _sourceY, _sourceW, _sourceH), data.Index);
        }

        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
    }

    public void CommitDelete()
    {
        // Pixels were already erased from layers during TryCreate — just push the tile
        // mutation so undo can restore them.
        var mutations = new List<LayerTileMutation>(_layerData.Count);
        foreach (var data in _layerData)
        {
            if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;
            var dirty = new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH);
            mutations.Add(new LayerTileMutation(data.Index, data.BeforeTiles, dirty));
        }
        _context.Document.CommitLayerTileMutations(mutations);
        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
    }

    public void CommitCurrent()
    {
        var dest = RotatedBounds(_rect, _angle);
        if (dest.IsEmpty) return;

        if (_layerData.Count > 0)
        {
            var mutations = new List<LayerTileMutation>(_layerData.Count);
            foreach (var data in _layerData)
            {
                if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;
                var layer = _context.Document.Layers[data.Index];

                var destLayerRegion = new PixelRegion(
                    dest.X - layer.OffsetX,
                    dest.Y - layer.OffsetY,
                    dest.Width,
                    dest.Height);
                if (destLayerRegion.IsEmpty) continue;

                // Capture destination tiles for undo
                var destTiles = layer.Pixels.CaptureTiles(destLayerRegion);
                var allBefore = new Dictionary<(int, int), byte[]?>(data.BeforeTiles);
                foreach (var (key, value) in destTiles)
                    allBefore.TryAdd(key, value);

                StampRotatedLayer(layer, dest, data);

                layer.MarkThumbnailDirty();
                var dirty = new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH).Union(dest);
                mutations.Add(new LayerTileMutation(data.Index, allBefore, dirty));
            }
            _context.Document.CommitLayerTileMutations(mutations);

            // CommitLayerTileMutations skips NotifyChanged when before==after (no-op transform).
            // Always notify so the compositor flushes the stale "cleared" tile cache.
            foreach (var data in _layerData)
                _context.Document.NotifyChanged(new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH).Union(dest), data.Index);
        }

        _context.Selection.SetFromRect(dest.X, dest.Y, dest.Width, dest.Height);
        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        _lastZoom = zoom;
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
            if (_flipX || _flipY)
            {
                using (dc.PushTransform(
                           Matrix.CreateTranslation(-center.X, -center.Y)
                           * Matrix.CreateScale(_flipX ? -1 : 1, _flipY ? -1 : 1)
                           * Matrix.CreateTranslation(center.X, center.Y)))
                {
                    dc.DrawImage(_overlayBitmap, rect);
                }
            }
            else
            {
                dc.DrawImage(_overlayBitmap, rect);
            }

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

        DrawButtons(dc, rect, angleRad, center, zoom);
    }

    // ── Per-layer stamp (same rotation applied to each layer's extracted pixels) ──

    private unsafe void StampRotatedLayer(DrawingLayer layer, PixelRegion dest, LayerData data)
    {
        var clipped = new PixelRegion(
            dest.X - layer.OffsetX,
            dest.Y - layer.OffsetY,
            dest.Width,
                dest.Height);
        if (clipped.IsEmpty) return;

        var flat = layer.Pixels.Capture(clipped);
        if (flat.Length == 0) flat = new byte[clipped.Width * clipped.Height * 4];

        var srcInfo = new SKImageInfo(data.SrcW, data.SrcH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var srcBitmap = new SKBitmap();
        var srcHandle = GCHandle.Alloc(data.FloatPixels, GCHandleType.Pinned);
        try
        {
            srcBitmap.InstallPixels(srcInfo, srcHandle.AddrOfPinnedObject());

            var dstInfo = new SKImageInfo(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            fixed (byte* flatPtr = flat)
            {
                using var dstBitmap = new SKBitmap();
                dstBitmap.InstallPixels(dstInfo, (IntPtr)flatPtr, clipped.Width * 4);
                using var canvas = new SKCanvas(dstBitmap);

                canvas.Translate(-(clipped.X + layer.OffsetX), -(clipped.Y + layer.OffsetY));

                var center = CenterOf(NormalizedRect(_rect));
                var r = NormalizedRect(_rect);

                using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
                canvas.Translate((float)center.X, (float)center.Y);
                canvas.RotateDegrees((float)_angle);
                if (_flipX || _flipY)
                    canvas.Scale(_flipX ? -1 : 1, _flipY ? -1 : 1);
                canvas.Translate(-(float)center.X, -(float)center.Y);
                canvas.DrawBitmap(srcBitmap,
                    new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom),
                    paint);
                canvas.Flush();
            }
        }
        finally
        {
            srcHandle.Free();
        }

        layer.Pixels.Restore(clipped, flat);
    }

    // ── Helper methods (unchanged from original) ──

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

        var rotMatrix = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angleRad)
            * Matrix.CreateTranslation(center.X, center.Y);

        var bottomCenter = new Point(rect.X + rect.Width / 2, rect.Bottom + below);
        var screenBottomCenter = rotMatrix.Transform(bottomCenter);

        var totalW = btnW * 2 + gap;
        var okRect = new Rect(screenBottomCenter.X - totalW / 2, screenBottomCenter.Y, btnW, btnH);
        var cancelRect = new Rect(okRect.Right + gap, screenBottomCenter.Y, btnW, btnH);

        DrawButton(dc, okRect, "OK", AccentBlue, zoom);
        DrawButton(dc, cancelRect, "✕", AccentRed, zoom);
    }

    private static void DrawButton(DrawingContext dc, Rect r, string text, IBrush accent, double zoom)
    {
        dc.FillRectangle(BtnBg, r);
        dc.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#4a5268")), 1.0 / zoom), r);

        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11.0 / zoom, BtnText);
        dc.DrawText(ft, new Point(
            r.X + (r.Width - ft.Width) * 0.5,
            r.Y + (r.Height - ft.Height) * 0.5));
    }

    private OverlayAction HitTestButton(double x, double y)
    {
        var rect = NormalizedRect(_rect);
        var center = CenterOf(rect);
        var angleRad = _angle * Math.PI / 180;
        var zoom = _lastZoom;
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
        Marshal.Copy(_combinedPixels, 0, fb.Address, _combinedPixels.Length);
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

    private static Rect ResizeRect(Rect rect, TransformDragPart part, double dx, double dy, bool uniformSides)
    {
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (part is TransformDragPart.TopLeft or TransformDragPart.Left or TransformDragPart.BottomLeft)
            left += dx;
        if (part is TransformDragPart.TopRight or TransformDragPart.Right or TransformDragPart.BottomRight)
            right += dx;
        if (part is TransformDragPart.TopLeft or TransformDragPart.Top or TransformDragPart.TopRight)
            top += dy;
        if (part is TransformDragPart.BottomLeft or TransformDragPart.Bottom or TransformDragPart.BottomRight)
            bottom += dy;

        if (uniformSides && !IsCornerHandle(part))
        {
            var dw = right - left;
            var dh = bottom - top;
            var scale = part is TransformDragPart.Left or TransformDragPart.Right
                ? dw / rect.Width
                : dh / rect.Height;
            scale = Math.Max(scale, 8 / Math.Max(rect.Width, rect.Height));
            var c = CenterOf(rect);
            dw = rect.Width * scale;
            dh = rect.Height * scale;
            left = c.X - dw * 0.5;
            top = c.Y - dh * 0.5;
            right = left + dw;
            bottom = top + dh;
        }

        if (right - left < 8 || bottom - top < 8) return rect;
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect UniformScaleRect(Rect rect, TransformDragPart part, double dx, double dy)
    {
        var anchor = AnchorPoint(part, rect);
        var dragged = CornerPoint(part, rect);
        dragged = new Point(dragged.X + dx, dragged.Y + dy);

        var newW = Math.Abs(dragged.X - anchor.X);
        var newH = Math.Abs(dragged.Y - anchor.Y);
        var scale = Math.Max(newW / Math.Max(rect.Width, 1), newH / Math.Max(rect.Height, 1));
        if (scale < 0.01) return rect;

        var w = Math.Max(8, rect.Width * scale);
        var h = Math.Max(8, rect.Height * scale);

        var x = dragged.X < anchor.X ? anchor.X - w : anchor.X;
        var y = dragged.Y < anchor.Y ? anchor.Y - h : anchor.Y;
        return new Rect(x, y, w, h);
    }

    private static Point AnchorPoint(TransformDragPart part, Rect rect) => part switch
    {
        TransformDragPart.TopLeft => rect.BottomRight,
        TransformDragPart.TopRight => rect.BottomLeft,
        TransformDragPart.BottomLeft => rect.TopRight,
        TransformDragPart.BottomRight => rect.TopLeft,
        TransformDragPart.Top => new Point(rect.X + rect.Width * 0.5, rect.Bottom),
        TransformDragPart.Bottom => new Point(rect.X + rect.Width * 0.5, rect.Top),
        TransformDragPart.Left => new Point(rect.Right, rect.Y + rect.Height * 0.5),
        TransformDragPart.Right => new Point(rect.Left, rect.Y + rect.Height * 0.5),
        _ => CenterOf(rect)
    };

    private static Point CornerPoint(TransformDragPart part, Rect rect) => part switch
    {
        TransformDragPart.TopLeft => rect.TopLeft,
        TransformDragPart.TopRight => rect.TopRight,
        TransformDragPart.BottomLeft => rect.BottomLeft,
        TransformDragPart.BottomRight => rect.BottomRight,
        TransformDragPart.Top => new Point(rect.X + rect.Width * 0.5, rect.Top),
        TransformDragPart.Bottom => new Point(rect.X + rect.Width * 0.5, rect.Bottom),
        TransformDragPart.Left => new Point(rect.Left, rect.Y + rect.Height * 0.5),
        TransformDragPart.Right => new Point(rect.Right, rect.Y + rect.Height * 0.5),
        _ => CenterOf(rect)
    };

    private static Rect NormalizedRect(Rect r)
        => new(Math.Min(r.X, r.Right), Math.Min(r.Y, r.Bottom),
               Math.Abs(r.Width), Math.Abs(r.Height));

    private static PixelRegion RotatedBounds(Rect rect, double angle)
    {
        var c = CenterOf(rect);
        var rad = angle * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(rad));
        var sin = Math.Abs(Math.Sin(rad));
        var hw = rect.Width * 0.5;
        var hh = rect.Height * 0.5;
        var w = hw * cos + hh * sin;
        var h = hw * sin + hh * cos;
        var x = (int)Math.Floor(c.X - w);
        var y = (int)Math.Floor(c.Y - h);
        return new PixelRegion(x, y, (int)Math.Round(w * 2), (int)Math.Round(h * 2));
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

        var rotY = rect.Bottom + Math.Max(rect.Height * 0.25, 12);
        yield return (TransformDragPart.Rotate, new Point(midX, rotY));
    }
}
