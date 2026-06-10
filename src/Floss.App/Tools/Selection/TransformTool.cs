using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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

public readonly record struct TransformCompletionFrame(
    Rect BaseRect,
    Rect CurrentRect,
    double Angle,
    TransformEditSnapshot Snapshot);

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
    private TransformCompletionFrame? _lastCompletionFrame;

    public int ViewportFlipX { get; set; } = 1;
    public int ViewportFlipY { get; set; } = 1;

    // Called after commit/cancel/delete so the canvas can restore the previous tool.
    public Action<TransformCompletionKind>? OnCompleted { get; set; }

    public bool HasPendingOperation => _operation != null;

    public IReadOnlyList<int> TransformingLayerIndices =>
        _operation?.LayerIndices ?? Array.Empty<int>();

    public PixelRegion? TransformDirtyRegion => _operation?.SourceRegion;

    public PixelRegion? CurrentDirtyRegion => _operation?.CurrentDirtyRegion;

    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public TransformCompletionFrame? LastCompletionFrame => _lastCompletionFrame;

    public bool BeginTransform(ToolContext ctx, IReadOnlyList<int>? layerIndices = null, PixelRegion? contentBounds = null)
    {
        _operation?.Cancel();
        _lastCompletionFrame = null;
        _operation = SelectionTransformOperation.TryCreate(ctx, layerIndices, contentBounds);
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
    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
        => _operation?.RenderOverlay(dc, zoom, ViewportFlipX, ViewportFlipY);
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
        _lastCompletionFrame = _operation?.CompletionFrame;
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

    public bool ConsumesModifier(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Control);

    public TransformEditSnapshot? EditSnapshot => _operation?.Snapshot;

    public void ApplyEdit(TransformEditSnapshot edit) => _operation?.ApplyEdit(edit);

    public void ResetEdit() => _operation?.ResetToBase();

    public void FlipHorizontal() => _operation?.FlipHorizontal();

    public void FlipVertical() => _operation?.FlipVertical();

    public void EndDrag(ToolContext ctx) => _operation?.EndDrag();
}

internal sealed class SelectionTransformOperation : IToolOperationOverlay
{
    private const int PreviewUpdateIntervalMs = 16;
    private const int PreviewLodMaxDimension = 1024;
    private const int PreviewLodCommitMaxDimension = 2000;

    // Per-layer extraction data
    private readonly record struct LayerData(
        int Index,
        int SrcX, int SrcY, int SrcW, int SrcH,
        byte[] FloatPixels,
        Dictionary<(int, int), byte[]?> BeforeTiles);

    private readonly ToolContext _context;
    private readonly List<LayerData> _layerData = [];
    private readonly Dictionary<int, PixelRegion?> _lastPreviewDestByLayer = [];
    private readonly Dictionary<int, Dictionary<(int, int), byte[]?>> _commitBeforeTilesByLayer = [];

    // Combined bounds (bounds of all layers' content)
    private readonly int _sourceX, _sourceY, _sourceW, _sourceH;

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
    private int _previewLod;
    private readonly bool _usingSelection;
    private bool _strokeSuspendActive;
    private bool _initComplete;
    private readonly Dictionary<(int Layer, int Lod), SKBitmap> _sourceBitmaps = [];
    private readonly Dictionary<(int Layer, int Lod), GCHandle> _sourceBitmapPins = [];
    private readonly Stopwatch _previewTimer = Stopwatch.StartNew();

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

    public TransformCompletionFrame CompletionFrame
        => new(NormalizedRect(_baseRect), NormalizedRect(_rect), _angle, Snapshot);

    public OverlayAction RequestedAction { get; private set; }

    public IReadOnlyList<int> LayerIndices =>
        _layerData.ConvertAll(static d => d.Index);

    public PixelRegion SourceRegion => new(_sourceX, _sourceY, _sourceW, _sourceH);

    public PixelRegion CurrentDirtyRegion
    {
        get
        {
            var region = SourceRegion;
            foreach (var (_, dest) in _lastPreviewDestByLayer)
            {
                if (dest is { IsEmpty: false } d)
                    region = region.Union(d);
            }
            return region;
        }
    }

    private static readonly IBrush BtnBg = new SolidColorBrush(Color.Parse("#2a2e38"));
    private static readonly IBrush BtnBgHover = new SolidColorBrush(Color.Parse("#3a4050"));
    private static readonly Pen BtnBorder = new(new SolidColorBrush(Color.Parse("#4a5268")), 1);
    private static readonly IBrush BtnText = new SolidColorBrush(Color.Parse("#c8d0e0"));
    private static readonly IBrush AccentBlue = new SolidColorBrush(Color.Parse("#5a9fd8"));
    private static readonly IBrush AccentRed = new SolidColorBrush(Color.Parse("#d85a5a"));

    private SelectionTransformOperation(
        ToolContext context,
        int sourceX, int sourceY, int sourceW, int sourceH,
        bool usingSelection)
    {
        _context = context;
        _sourceX = sourceX;
        _sourceY = sourceY;
        _sourceW = sourceW;
        _sourceH = sourceH;
        _usingSelection = usingSelection;
        _rect = new Rect(sourceX, sourceY, sourceW, sourceH);
        _baseRect = _rect;
    }

    public static SelectionTransformOperation? TryCreate(
        ToolContext ctx, IReadOnlyList<int>? layerIndices = null, PixelRegion? contentBounds = null)
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
        var useContentBounds = contentBounds is { IsEmpty: false };
        bool usingSelection = !useContentBounds && ctx.Selection.HasSelection;

        if (useContentBounds)
        {
            combinedBounds = contentBounds!.Value.ClipTo(ctx.Document.Width, ctx.Document.Height);
        }
        else if (usingSelection)
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

        var operation = new SelectionTransformOperation(ctx, cb.X, cb.Y, cb.Width, cb.Height, usingSelection);
        ctx.Selection.TryGetMaskBuffer(out var selMask, out var selDocW, out var selDocH);

        foreach (var (layerIdx, layer) in layers)
        {
            var layerBounds = new PixelRegion(
                cb.X - layer.OffsetX,
                cb.Y - layer.OffsetY,
                cb.Width,
                cb.Height);
            if (layerBounds.IsEmpty) continue;

            byte[]? clipCapture = null;
            if (layer.IsClipping)
            {
                var clipIdx = layerIdx - 1;
                if (clipIdx >= 0)
                {
                    var clipLayer = ctx.Document.Layers[clipIdx];
                    if (clipLayer != null && !layers.Any(d => d.Index == clipIdx))
                        clipCapture = clipLayer.Pixels.Capture(layerBounds);
                }
            }

            var floatPixels = new byte[cb.Width * cb.Height * 4];
            var beforeTiles = layer.Pixels.CaptureTiles(layerBounds);
            var layerCapture = layer.Pixels.Capture(layerBounds);
            var hasPixels = ExtractFloatPixels(
                layerCapture, cb.Width, cb.Height, cb.X, cb.Y,
                clipCapture, selMask, selDocW, selDocH, usingSelection, floatPixels);

            if (hasPixels || useContentBounds || usingSelection)
            {
                operation._layerData.Add(new LayerData(
                    layerIdx, cb.X, cb.Y, cb.Width, cb.Height,
                    floatPixels, beforeTiles));
                operation._commitBeforeTilesByLayer[layerIdx] =
                    new Dictionary<(int, int), byte[]?>(beforeTiles);
            }
        }

        if (operation._layerData.Count == 0) return null;

        operation._previewLod = operation.CalculatePreviewLod(PreviewLodMaxDimension);
        operation.BuildSourceBitmaps();
        Dispatcher.UIThread.Post(operation.CompleteInit, DispatcherPriority.Background);
        return operation;
    }

    private static bool ExtractFloatPixels(
        byte[] layerCapture,
        int cbW,
        int cbH,
        int docX,
        int docY,
        byte[]? clipCapture,
        byte[]? selMask,
        int selDocW,
        int selDocH,
        bool usingSelection,
        byte[] floatPixels)
    {
        if (layerCapture.Length == 0) return false;

        var hasPixels = false;
        for (var y = 0; y < cbH; y++)
        {
            var docYOff = docY + y;
            for (var x = 0; x < cbW; x++)
            {
                if (usingSelection && selMask != null)
                {
                    var mx = docX + x;
                    var my = docYOff;
                    if ((uint)mx >= (uint)selDocW || (uint)my >= (uint)selDocH || selMask[my * selDocW + mx] == 0)
                        continue;
                }

                var i = (y * cbW + x) * 4;
                var a = layerCapture[i + 3];
                if (a == 0) continue;

                if (clipCapture != null && clipCapture[i + 3] == 0)
                    continue;

                floatPixels[i] = layerCapture[i];
                floatPixels[i + 1] = layerCapture[i + 1];
                floatPixels[i + 2] = layerCapture[i + 2];
                floatPixels[i + 3] = a;
                hasPixels = true;
            }
        }

        return hasPixels;
    }

    private void CompleteInit()
    {
        BeginTransformSession();
        foreach (var data in _layerData)
        {
            ClearLayerSourceFromDocument(data);
            _context.Document.NotifyChanged(new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH), data.Index);
        }

        _initComplete = true;
        ReapplyPreview(force: true);
    }

    private void BeginTransformSession()
    {
        if (_layerData.Count == 0 || _strokeSuspendActive) return;
        _context.Document.NotifyStrokeSuspendBegin(CurrentDirtyRegion, _layerData[0].Index);
        _strokeSuspendActive = true;
    }

    private void EndTransformSession()
    {
        DisposeSourceBitmaps();
        if (!_strokeSuspendActive) return;
        _context.Document.NotifyStrokeSuspendEnd();
        _strokeSuspendActive = false;
    }

    private void BuildSourceBitmaps()
    {
        DisposeSourceBitmaps();
        foreach (var data in _layerData)
        {
            EnsureSourceBitmap(data, 0);
            if (_previewLod > 0)
                EnsureSourceBitmap(data, _previewLod);
        }
    }

    private void DisposeSourceBitmaps()
    {
        foreach (var bmp in _sourceBitmaps.Values)
            bmp.Dispose();
        _sourceBitmaps.Clear();
        foreach (var pin in _sourceBitmapPins.Values)
        {
            if (pin.IsAllocated) pin.Free();
        }
        _sourceBitmapPins.Clear();
    }

    private SKBitmap EnsureSourceBitmap(LayerData data, int lod)
    {
        var key = (data.Index, lod);
        if (_sourceBitmaps.TryGetValue(key, out var existing))
            return existing;

        var scale = lod > 0 ? 1 << lod : 1;
        var srcW = lod > 0 ? Math.Max(1, data.SrcW / scale) : data.SrcW;
        var srcH = lod > 0 ? Math.Max(1, data.SrcH / scale) : data.SrcH;
        byte[] pixels = lod > 0
            ? DownsampleBgra(data.FloatPixels, data.SrcW, data.SrcH, scale)
            : data.FloatPixels;

        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var info = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bmp = new SKBitmap();
        bmp.InstallPixels(info, pin.AddrOfPinnedObject());
        _sourceBitmaps[key] = bmp;
        _sourceBitmapPins[key] = pin;
        return bmp;
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
        SchedulePreviewUpdate();
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
        SchedulePreviewUpdate(force: true);
    }

    public void ResetToBase()
    {
        _rect = _baseRect;
        _angle = 0;
        _flipX = _flipY = false;
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
        SchedulePreviewUpdate(force: true);
    }

    public void FlipHorizontal()
    {
        _flipX = !_flipX;
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
        SchedulePreviewUpdate(force: true);
    }

    public void FlipVertical()
    {
        _flipY = !_flipY;
        _context.InvalidateRender();
        _context.TransformEditChanged?.Invoke();
        SchedulePreviewUpdate(force: true);
    }

    private static bool IsCornerHandle(TransformDragPart part)
        => part is TransformDragPart.TopLeft or TransformDragPart.TopRight
            or TransformDragPart.BottomLeft or TransformDragPart.BottomRight;

    public void PointerUp(CanvasInputSample sample)
    {
        EndDrag();
    }

    public void EndDrag()
    {
        _isDragging = false;
        ReapplyPreview(force: true, fullResolution: true);
    }

    public void Cancel()
    {
        foreach (var data in _layerData)
        {
            if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;
            var layer = _context.Document.Layers[data.Index];

            foreach (var (key, before) in GetCommitBeforeTiles(data))
                layer.RestoreTile(key.Item1, key.Item2, before);

            layer.MarkThumbnailDirty();
        }

        if (_layerData.Count > 0)
            _context.Document.NotifyChanged(CurrentDirtyRegion, _layerData[0].Index);

        _lastPreviewDestByLayer.Clear();
        EndTransformSession();
    }

    public void CommitDelete()
    {
        var mutations = new List<LayerTileMutation>(_layerData.Count);
        foreach (var data in _layerData)
        {
            if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;
            var layer = _context.Document.Layers[data.Index];

            if (_lastPreviewDestByLayer.TryGetValue(data.Index, out var lastDest)
                && lastDest is { IsEmpty: false } dest)
            {
                ClearPreviewStamp(layer, dest, data);
            }

            ClearLayerSourceFromDocument(data);
            layer.MarkThumbnailDirty();
            var dirty = new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH);
            if (_lastPreviewDestByLayer.TryGetValue(data.Index, out var prevDest) && prevDest is { IsEmpty: false } pd)
                dirty = dirty.Union(pd);
            mutations.Add(new LayerTileMutation(data.Index, GetCommitBeforeTiles(data), dirty));
        }

        _context.Document.SetPendingHistoryLabel(HistoryLabels.FromPresetOrDefault(_context.ActivePreset, "Transform"));
        _context.Document.CommitLayerTileMutations(mutations);
        _lastPreviewDestByLayer.Clear();
        EndTransformSession();
    }

    public void CommitCurrent()
    {
        ReapplyPreview(force: true, fullResolution: true);

        var dest = RotatedBounds(_rect, _angle);
        if (dest.IsEmpty)
        {
            EndTransformSession();
            return;
        }

        if (_layerData.Count > 0)
        {
            var mutations = new List<LayerTileMutation>(_layerData.Count);
            foreach (var data in _layerData)
            {
                if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;

                var dirty = new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH).Union(dest);
                var before = GetCommitBeforeTiles(data);
                mutations.Add(new LayerTileMutation(data.Index, before, dirty));
            }

            _context.Document.SetPendingHistoryLabel(HistoryLabels.FromPresetOrDefault(_context.ActivePreset, "Transform"));
            _context.Document.CommitLayerTileMutations(mutations);
        }

        _context.Selection.SetFromRect(dest.X, dest.Y, dest.Width, dest.Height);
        _lastPreviewDestByLayer.Clear();
        EndTransformSession();
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
        => RenderOverlay(dc, zoom, 1, 1);

    public void RenderOverlay(DrawingContext dc, double zoom, int viewportFlipX, int viewportFlipY)
    {
        _lastZoom = zoom;

        var rect = NormalizedRect(_rect);
        var center = CenterOf(rect);
        var angleRad = _angle * Math.PI / 180;

        var matrix = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angleRad)
            * Matrix.CreateTranslation(center.X, center.Y);

        using (dc.PushTransform(matrix))
        {
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

        DrawButtons(dc, rect, angleRad, center, zoom, viewportFlipX, viewportFlipY);
    }

    // ── preview: undo previous stamp, merge cache → live layer ──

    private void SchedulePreviewUpdate(bool force = false) => ReapplyPreview(force);

    private void ReapplyPreview(bool force = false, bool fullResolution = false)
    {
        if (!_initComplete) return;

        if (!force)
        {
            var interval = _sourceW * _sourceH > 2_000_000 ? 32 : PreviewUpdateIntervalMs;
            if (_previewTimer.ElapsedMilliseconds < interval) return;
            if (_context.IsCompositorBusy?.Invoke() == true) return;
        }

        _previewTimer.Restart();

        var dest = RotatedBounds(_rect, _angle);
        if (dest.IsEmpty) return;

        var lod = fullResolution ? 0 : _previewLod;
        var combinedDirty = PixelRegion.Empty;

        foreach (var data in _layerData)
        {
            if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) continue;
            var layer = _context.Document.Layers[data.Index];
            var sourceDoc = new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH);
            var dirty = sourceDoc;

            if (_lastPreviewDestByLayer.TryGetValue(data.Index, out var lastDest)
                && lastDest is { IsEmpty: false } prev)
            {
                ClearPreviewStamp(layer, prev, data);
                dirty = dirty.Union(prev);
            }

            CaptureDestBeforeStamp(data, layer, dest);
            if (CanUseTranslateStamp())
                StampTranslatedLayer(layer, dest, data);
            else
                StampRotatedLayer(layer, dest, data, lod);
            _lastPreviewDestByLayer[data.Index] = dest;
            dirty = dirty.Union(dest);

            layer.MarkThumbnailDirty();
            combinedDirty = combinedDirty.IsEmpty ? dirty : combinedDirty.Union(dirty);
        }

        if (!combinedDirty.IsEmpty && _layerData.Count > 0)
        {
            _context.Document.NotifyStrokeSuspendExtend(combinedDirty);
            _context.Document.NotifyChanged(combinedDirty, _layerData[0].Index);
        }

        _context.InvalidateRender();
    }

    private int CalculatePreviewLod(int maxDimension) =>
        Math.Max(_sourceW, _sourceH) <= maxDimension
            ? 0
            : (int)Math.Ceiling(Math.Log2(Math.Max(1.0, (double)Math.Max(_sourceW, _sourceH) / maxDimension)));

    private bool CanUseTranslateStamp()
    {
        if (_angle != 0 || _flipX || _flipY) return false;
        var r = NormalizedRect(_rect);
        return Math.Abs(r.Width - _baseRect.Width) < 0.5
            && Math.Abs(r.Height - _baseRect.Height) < 0.5;
    }

    private void StampTranslatedLayer(DrawingLayer layer, PixelRegion destDoc, LayerData data)
    {
        var destLayer = ToLayerRegion(destDoc, layer);
        if (destLayer.IsEmpty) return;

        var byteCount = destLayer.Width * destLayer.Height * 4;
        var flat = new byte[byteCount];
        var existing = layer.Pixels.Capture(destLayer);
        if (existing.Length > 0)
            Buffer.BlockCopy(existing, 0, flat, 0, Math.Min(existing.Length, flat.Length));

        if (destLayer.Width == data.SrcW && destLayer.Height == data.SrcH)
            MergeFloatPixelsIntoBuffer(data.FloatPixels, data.SrcW, data.SrcH, flat);

        layer.Pixels.Restore(destLayer, flat);
        layer.MarkThumbnailDirty();
    }

    /// <summary>
    /// Only write pixels that belong to the transformed selection/content — transparent
    /// entries in <see cref="LayerData.FloatPixels"/> must not erase destination art.
    /// </summary>
    private static void MergeFloatPixelsIntoBuffer(byte[] floatPixels, int srcW, int srcH, byte[] dest)
    {
        var count = Math.Min(floatPixels.Length, dest.Length);
        for (var i = 3; i < count; i += 4)
        {
            if (floatPixels[i] == 0) continue;
            var o = i - 3;
            dest[o] = floatPixels[o];
            dest[o + 1] = floatPixels[o + 1];
            dest[o + 2] = floatPixels[o + 2];
            dest[o + 3] = floatPixels[i];
        }
    }

    /// <summary>
    /// Erase only pixels that were stamped from the transform source, not the full AABB.
    /// </summary>
    private void ClearPreviewStamp(DrawingLayer layer, PixelRegion destDoc, LayerData data)
    {
        var destLayer = ToLayerRegion(destDoc, layer);
        if (destLayer.IsEmpty) return;

        if (!_usingSelection)
        {
            layer.Clear(destLayer);
            layer.MarkThumbnailDirty();
            return;
        }

        var capture = layer.Pixels.Capture(destLayer);
        if (capture.Length == 0) return;

        var w = Math.Min(data.SrcW, destLayer.Width);
        var h = Math.Min(data.SrcH, destLayer.Height);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var si = (y * data.SrcW + x) * 4;
                if (data.FloatPixels[si + 3] == 0) continue;
                var di = (y * destLayer.Width + x) * 4;
                capture[di] = 0;
                capture[di + 1] = 0;
                capture[di + 2] = 0;
                capture[di + 3] = 0;
            }
        }

        layer.Pixels.Restore(destLayer, capture);
        layer.MarkThumbnailDirty();
    }

    private Dictionary<(int, int), byte[]?> GetCommitBeforeTiles(LayerData data) =>
        _commitBeforeTilesByLayer.TryGetValue(data.Index, out var tiles)
            ? tiles
            : data.BeforeTiles;

    private void CaptureDestBeforeStamp(LayerData data, DrawingLayer layer, PixelRegion destDoc)
    {
        if (!_commitBeforeTilesByLayer.TryGetValue(data.Index, out var before))
        {
            before = new Dictionary<(int, int), byte[]?>(data.BeforeTiles);
            _commitBeforeTilesByLayer[data.Index] = before;
        }

        var destLayer = ToLayerRegion(destDoc, layer);
        if (destLayer.IsEmpty) return;
        layer.CaptureTiles(destLayer, before);
    }

    private static PixelRegion ToLayerRegion(PixelRegion docRegion, DrawingLayer layer) =>
        new(docRegion.X - layer.OffsetX, docRegion.Y - layer.OffsetY, docRegion.Width, docRegion.Height);

    private static byte[] DownsampleBgra(byte[] src, int srcW, int srcH, int scale)
    {
        var dstW = Math.Max(1, srcW / scale);
        var dstH = Math.Max(1, srcH / scale);
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            var sy = Math.Min(srcH - 1, y * scale);
            for (var x = 0; x < dstW; x++)
            {
                var sx = Math.Min(srcW - 1, x * scale);
                var si = (sy * srcW + sx) * 4;
                var di = (y * dstW + x) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
        return dst;
    }

    // ── Per-layer stamp (same rotation applied to each layer's extracted pixels) ──

    private unsafe void StampRotatedLayer(DrawingLayer layer, PixelRegion dest, LayerData data, int previewLod = 0)
    {
        var clipped = ToLayerRegion(dest, layer);
        if (clipped.IsEmpty) return;

        var byteCount = clipped.Width * clipped.Height * 4;
        var flat = new byte[byteCount];
        var existing = layer.Pixels.Capture(clipped);
        if (existing.Length > 0)
            Buffer.BlockCopy(existing, 0, flat, 0, Math.Min(existing.Length, flat.Length));

        var srcBitmap = EnsureSourceBitmap(data, previewLod);

        var dstInfo = new SKImageInfo(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        fixed (byte* flatPtr = flat)
        {
            using var dstBitmap = new SKBitmap();
            dstBitmap.InstallPixels(dstInfo, (IntPtr)flatPtr, clipped.Width * 4);
            using var canvas = new SKCanvas(dstBitmap);

            canvas.Translate(-(clipped.X + layer.OffsetX), -(clipped.Y + layer.OffsetY));

            var center = CenterOf(NormalizedRect(_rect));
            var r = NormalizedRect(_rect);

            using var paint = new SKPaint
            {
                IsAntialias = previewLod == 0,
                BlendMode = SKBlendMode.SrcOver
            };
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

        layer.Pixels.Restore(clipped, flat);
        layer.MarkThumbnailDirty();
    }

    private void ClearLayerSourceFromDocument(LayerData data)
    {
        if (data.Index < 0 || data.Index >= _context.Document.Layers.Count) return;
        var layer = _context.Document.Layers[data.Index];
        var layerBounds = ToLayerRegion(new PixelRegion(data.SrcX, data.SrcY, data.SrcW, data.SrcH), layer);
        if (layerBounds.IsEmpty) return;

        if (!_usingSelection)
        {
            layer.Clear(layerBounds);
            layer.MarkThumbnailDirty();
            return;
        }

        _context.Selection.TryGetMaskBuffer(out var selMask, out var selDocW, out var selDocH);
        if (selMask == null)
        {
            layer.Clear(layerBounds);
            layer.MarkThumbnailDirty();
            return;
        }

        var capture = layer.Pixels.Capture(layerBounds);
        if (capture.Length == 0) return;

        for (var y = 0; y < data.SrcH; y++)
        {
            var docY = data.SrcY + y;
            for (var x = 0; x < data.SrcW; x++)
            {
                var docX = data.SrcX + x;
                if ((uint)docX >= (uint)selDocW || (uint)docY >= (uint)selDocH || selMask[docY * selDocW + docX] == 0)
                    continue;

                var i = (y * data.SrcW + x) * 4;
                if (data.FloatPixels[i + 3] == 0) continue;
                capture[i] = capture[i + 1] = capture[i + 2] = capture[i + 3] = 0;
            }
        }

        layer.Pixels.Restore(layerBounds, capture);
        layer.MarkThumbnailDirty();
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

    private void DrawButtons(DrawingContext dc, Rect rect, double angleRad, Point center, double zoom, int viewportFlipX, int viewportFlipY)
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

        DrawButton(dc, okRect, "OK", AccentBlue, zoom, viewportFlipX, viewportFlipY);
        DrawButton(dc, cancelRect, "✕", AccentRed, zoom, viewportFlipX, viewportFlipY);
    }

    private static void DrawButton(DrawingContext dc, Rect r, string text, IBrush accent, double zoom, int viewportFlipX, int viewportFlipY)
    {
        dc.FillRectangle(BtnBg, r);
        dc.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#4a5268")), 1.0 / zoom), r);

        void DrawText()
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 11.0 / zoom, BtnText);
            dc.DrawText(ft, new Point(
                r.X + (r.Width - ft.Width) * 0.5,
                r.Y + (r.Height - ft.Height) * 0.5));
        }

        var fx = viewportFlipX < 0 ? -1.0 : 1.0;
        var fy = viewportFlipY < 0 ? -1.0 : 1.0;
        if (fx == 1.0 && fy == 1.0)
        {
            DrawText();
            return;
        }

        var center = r.Center;
        using (dc.PushTransform(
                   Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateScale(fx, fy)
                   * Matrix.CreateTranslation(center.X, center.Y)))
        {
            DrawText();
        }
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
