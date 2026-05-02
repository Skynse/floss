using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Canvas;

public sealed class DrawingCanvas : Control
{
    private const double PenPressureThreshold = 0.02;

    private readonly DrawingDocument _document = new();
    private readonly CanvasTool _canvasTool;
    private readonly LayerCompositor _compositor;
    private readonly ToolContext _ctx;
    private readonly ToolController _toolController;

    private readonly BrushTool _brushTool;
    private readonly BrushTool _eraserTool;
    private readonly SmudgeTool _smudgeTool = new();
    private readonly TransformTool _transformTool = new();

    private BrushPreset _brush = BrushPreset.Defaults[0];
    private Color _paintColor = Color.Parse("#111111");
    private long _activePointerId = -1;
    private Point _pointerPos;
    private Point _lockedPointerPos;
    private bool _isPointerOver;
    private bool _isCursorPreviewLocked;
    private bool _forceBrushOutlineCursor;

    private static readonly IBrush CursorOuterBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
    private static readonly IBrush CursorInnerBrush = new SolidColorBrush(Colors.White);

    public DrawingCanvas()
    {
        _document.DirtyStateChanged += (_, _) => DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        Focusable = true;
        ClipToBounds = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        _canvasTool = new CanvasTool(_document, new BrushEngine());
        _compositor = new LayerCompositor();

        _ctx = new ToolContext(_document)
        {
            Brush = _brush,
            PaintColor = _paintColor,
            InvalidateRender = InvalidateVisual,
            OnColorSampled = c =>
            {
                _paintColor = c;
                _brush = _brush with { Color = c };
                _ctx!.PaintColor = c;
                _ctx.Brush = _brush;
                ColorSampled?.Invoke(this, c);
                InvalidateVisual();
            },
            SampleDocumentColor = SampleDocumentColor
        };
        _ctx.Selection.Resize(_document.Width, _document.Height);

        _brushTool = new BrushTool(_canvasTool, isEraser: false);
        _eraserTool = new BrushTool(_canvasTool, isEraser: true);
        _toolController = new ToolController(_ctx, _brushTool);

        _document.Changed += (_, e) =>
        {
            _compositor.Invalidate(e.DirtyRegion, _document.Layers, e.LayerIndex);
            InvalidateVisual();
            StatsChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        _document.LayersChanged += (_, _) =>
        {
            _ctx.Selection.Resize(_document.Width, _document.Height);
            _compositor.Invalidate(null);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.LayerMetadataChanged += (_, e) => LayerMetadataChanged?.Invoke(this, e);
        _document.PaperChanged += (_, _) =>
        {
            _compositor.Invalidate(null);
            InvalidateVisual();
        };
    }

    public event EventHandler? StatsChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;
    public event EventHandler<LayerMetadataChangedEventArgs>? LayerMetadataChanged;
    public event EventHandler<Color>? ColorSampled;
    public event EventHandler? DirtyStateChanged;

    public int ActiveSampleCount => _canvasTool.ActiveSampleCount;
    public int CommittedStrokeCount => _document.CommittedStrokeCount;
    public bool CanUndo => _document.CanUndo;
    public bool CanRedo => _document.CanRedo;
    public bool CanDeleteLayer => _document.CanDeleteLayer;
    public BrushPreset Brush => _brush;
    public Color PaintColor => _paintColor;
    public bool EraserEnabled => _toolController.ActiveTool == _eraserTool;
    public DrawingDocument Document => _document;
    public IReadOnlyList<DrawingLayer> Layers => _document.Layers;
    public int ActiveLayerIndex => _document.ActiveLayerIndex;
    public ITool ActiveTool => _toolController.ActiveTool;
    public BrushTool BrushTool => _brushTool;
    public BrushTool EraserTool => _eraserTool;
    public SmudgeTool SmudgeTool => _smudgeTool;
    public TransformTool TransformTool => _transformTool;
    public bool HasSelection => _ctx.Selection.HasSelection;
    public bool IsDirty => _document.IsDirty;

    public bool PaintInputSuspended { get; set; }
    public double CanvasZoom { get; set; } = 1.0;

    public void SetActiveTool(ITool tool)
    {
        _toolController.SetActiveTool(tool);
        InvalidateVisual();
    }

    public void SetBrush(BrushPreset preset)
    {
        _brush = preset with { Color = _paintColor };
        _ctx.Brush = _brush;
        if (preset.Kind == BrushKind.Eraser)
            SetActiveTool(_eraserTool);
        InvalidateVisual();
    }

    public void SetPaintColor(Color color)
    {
        _paintColor = color;
        _ctx.PaintColor = color;
        if (!EraserEnabled)
        {
            _brush = _brush with { Color = color };
            _ctx.Brush = _brush;
        }
        InvalidateVisual();
    }

    public void SetPaperColor(Color color) => _document.PaperColor = color;

    public void SetPaperVisible(bool visible) => _document.PaperVisible = visible;

    public void SetBrushSize(double size)
    {
        _brush = _brush with { Size = Math.Clamp(size, 1, 2000) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushOpacity(double opacity)
    {
        _brush = _brush with { Opacity = Math.Clamp(opacity, 0.01, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushHardness(double hardness)
    {
        _brush = _brush with { Hardness = Math.Clamp(hardness, 0, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushSpacing(double spacing)
    {
        _brush = _brush with { Spacing = Math.Clamp(spacing, 0.02, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushSmoothing(double smoothing)
    {
        _brush = _brush with { Smoothing = Math.Clamp(smoothing, 0, 0.95) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushGrain(double grain)
    {
        _brush = _brush with { Grain = Math.Clamp(grain, 0, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void LockCursorPreview(Point position, bool forceBrushOutline)
    {
        _lockedPointerPos = position;
        _isCursorPreviewLocked = true;
        _forceBrushOutlineCursor = forceBrushOutline;
        InvalidateVisual();
    }

    public void UnlockCursorPreview()
    {
        if (!_isCursorPreviewLocked) return;
        _isCursorPreviewLocked = false;
        _forceBrushOutlineCursor = false;
        InvalidateVisual();
    }

    public void CancelActiveTool()
    {
        if (_toolController.ActiveTool is TransformTool)
        {
            _transformTool.Cancel(_ctx);
            EndSelectionTransform();
        }
        else if (!_toolController.Cancel() && _ctx.Selection.HasSelection)
            _ctx.Selection.Clear();
        InvalidateVisual();
    }

    public void CommitActiveTool()
    {
        if (_toolController.ActiveTool is SelectTool st) st.CommitPolyline(_ctx);
        else if (_toolController.ActiveTool is PolylineTool pt) pt.Commit(_ctx);
        else if (_toolController.ActiveTool is TransformTool tt) tt.Commit(_ctx);
        if (_toolController.ActiveTool is TransformTool)
            EndSelectionTransform();
        InvalidateVisual();
    }

    public bool BeginSelectionTransform()
    {
        var previousTool = _toolController.ActiveTool;
        SetActiveTool(_transformTool);
        _transformTool.SetPreviousTool(previousTool);
        if (_transformTool.BeginTransform(_ctx))
            return true;

        _transformTool.SetPreviousTool(null);
        SetActiveTool(previousTool);
        return false;
    }

    public void EndSelectionTransform()
    {
        var previous = _transformTool.GetPreviousTool();
        _transformTool.SetPreviousTool(null);
        _ctx.Selection.Clear();
        if (previous != null)
            SetActiveTool(previous);
    }

    public void MergeSelectedLayers(IReadOnlyList<int> indices) => _document.MergeSelectedLayers(indices, _compositor);
    public void FlattenGroup(int groupIndex) => _document.FlattenGroup(groupIndex, _compositor);

    public void MergeDown(IReadOnlyList<int>? selectedIndices = null)
    {
        var active = _document.ActiveLayerIndex;
        if (selectedIndices is { Count: > 1 })
        {
            _document.MergeSelectedLayers(selectedIndices, _compositor);
            return;
        }
        var layer = _document.ActiveLayer;
        if (layer.IsGroup)
        {
            _document.FlattenGroup(active, _compositor);
            return;
        }
        // Merge with the next layer below in the flat list
        if (active + 1 < _document.Layers.Count)
            _document.MergeSelectedLayers([active, active + 1], _compositor);
    }

    public void SelectAll()
    {
        _ctx.Selection.SetFromRect(0, 0, _document.Width, _document.Height);
        InvalidateVisual();
    }

    public void Deselect()
    {
        _ctx.Selection.Clear();
        InvalidateVisual();
    }

    public void InvertSelection()
    {
        _ctx.Selection.Invert();
        InvalidateVisual();
    }

    public bool IsTransformActive => _toolController.ActiveTool is TransformTool tt && tt.HasPendingOperation;

    public void Clear(bool pushHistory = true) => _document.ClearActiveLayer(pushHistory);
    public void Undo() => _document.Undo();
    public void Redo() => _document.Redo();
    public void AddLayer() => _document.AddLayer();
    public void AddGroupLayer() => _document.AddGroupLayer();
    public void DuplicateLayer() => _document.DuplicateActiveLayer();
    public void DeleteLayer() => _document.DeleteActiveLayer();
    public void SelectLayer(int index) => _document.SelectLayer(index);
    public void ToggleLayerVisibility(int index) => _document.ToggleLayerVisibility(index);
    public void ToggleLayerLock(int index) => _document.ToggleLayerLock(index);
    public void ToggleLayerAlphaLock(int index) => _document.ToggleLayerAlphaLock(index);
    public void ToggleLayerClipping(int index) => _document.ToggleLayerClipping(index);
    public void ToggleLayerOpen(int index) => _document.ToggleLayerOpen(index);
    public bool CanMoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement) => _document.CanMoveLayer(sourceIndex, targetIndex, placement);
    public void MoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement) => _document.MoveLayer(sourceIndex, targetIndex, placement);
    public void MoveActiveLayer(int delta) => _document.MoveActiveLayer(delta);
    public void SetActiveLayerOpacity(double opacity) => _document.SetActiveLayerOpacity(opacity);
    public void SetActiveLayerBlendMode(string blendMode) => _document.SetActiveLayerBlendMode(blendMode);
    public void SetActiveLayerName(string name) => _document.SetActiveLayerName(name);

    private bool IsPaintBlockedByLock =>
        _toolController.ActiveTool is BrushTool && !_document.CanPaintActiveLayer;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Sync cursor to current state so layer lock changes take effect immediately.
        if (_isPointerOver && !(_toolController.ActiveTool is TransformTool { HasPendingOperation: true }))
            Cursor = new Cursor(IsPaintBlockedByLock ? StandardCursorType.No : StandardCursorType.None);

        _compositor.Composite(_document.Layers, _document.Width, _document.Height, _document.PaperColor, _document.PaperVisible);
        var target = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(target)))
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None,
            EdgeMode = EdgeMode.Aliased
        }))
        {
            context.DrawImage(_compositor.Bitmap, target);
        }

        _toolController.RenderOverlay(context, CanvasZoom);
        if (_toolController.ActiveTool is not Floss.App.Tools.TransformTool)
            _ctx.Selection.RenderOverlay(context, CanvasZoom);

        if ((_isPointerOver || _isCursorPreviewLocked) && !IsPaintBlockedByLock)
        {
            var mode = App.Config.BrushCursorMode;
            var pos = _isCursorPreviewLocked ? _lockedPointerPos : _pointerPos;
            var t = Math.Max(0.5, 1.5 / CanvasZoom);
            bool isBrushLike = _toolController.ActiveTool is BrushTool;
            if (_forceBrushOutlineCursor || (isBrushLike && mode is BrushCursorMode.Outline or BrushCursorMode.DotAndOutline))
            {
                var r = _brush.Size * 0.5;
                context.DrawEllipse(null, new Pen(CursorOuterBrush, t * 3), pos, r, r);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), pos, r, r);
            }

            if (!isBrushLike || mode is BrushCursorMode.Dot or BrushCursorMode.DotAndOutline)
            {
                var r = Math.Max(2.5 / CanvasZoom, t * 2);
                context.DrawEllipse(CursorOuterBrush, null, pos, r, r);
                context.DrawEllipse(CursorInnerBrush, null, pos, Math.Max(0.5 / CanvasZoom, r * 0.45), Math.Max(0.5 / CanvasZoom, r * 0.45));
            }

            if (_toolController.ActiveTool is EyedropperTool)
            {
                var swatchR = 10.0 / CanvasZoom;
                var swatchPos = new Point(pos.X + swatchR * 1.6, pos.Y - swatchR * 1.6);
                var colorBrush = new SolidColorBrush(_paintColor);
                context.DrawEllipse(colorBrush, new Pen(CursorOuterBrush, t * 2), swatchPos, swatchR, swatchR);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), swatchPos, swatchR, swatchR);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (PaintInputSuspended) return;
        if (IsPaintBlockedByLock) return;

        var point = e.GetCurrentPoint(this);
        if (!IsPaintInput(point)) return;

        Focus();
        var sample = MakeSample(point, CanvasInputPhase.Down);

        // Auto-switch to eraser when pen eraser tip is used.
        if (sample.Source == CanvasInputSource.Eraser && _toolController.ActiveTool is BrushTool b && !b.IsEraser)
            SetActiveTool(_eraserTool);

        _activePointerId = point.Pointer.Id;
        _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Down, sample));

        if (e.ClickCount > 1 && CanCommitActiveToolFromClick())
        {
            CommitActiveTool();
            _activePointerId = -1;
        }
        else
        {
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        Cursor = new Cursor(IsPaintBlockedByLock ? StandardCursorType.No : StandardCursorType.None);
        _pointerPos = e.GetCurrentPoint(this).Position;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        if (!_isCursorPreviewLocked)
            _pointerPos = point.Position;

        // Transform cursor override
        if (_toolController.ActiveTool is TransformTool tt && tt.HasPendingOperation)
        {
            var canvasPos = new Point(
                point.Position.X / Math.Max(Bounds.Width, 1) * _document.Width,
                point.Position.Y / Math.Max(Bounds.Height, 1) * _document.Height);
            var cursor = tt.CursorFor(canvasPos, CanvasZoom);
            if (cursor.HasValue)
                Cursor = new Cursor(cursor.Value);
        }

        if (PaintInputSuspended)
        {
            if (_activePointerId >= 0)
            {
                _activePointerId = -1;
                _toolController.Cancel();
                e.Pointer.Capture(null);
            }
            InvalidateVisual();
            return;
        }

        if (point.Pointer.Id == _activePointerId)
        {
            if (IsPaintInput(point))
            {
                _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Move, MakeSample(point, CanvasInputPhase.Move)));
            }
            else
            {
                _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Up, MakeSample(point, CanvasInputPhase.Up)));
                _activePointerId = -1;
                e.Pointer.Capture(null);
            }

            e.Handled = true;
        }
        else if (_activePointerId < 0)
        {
            _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Move, MakeSample(point, CanvasInputPhase.Move)));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        if (point.Pointer.Id != _activePointerId) return;

        _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Up, MakeSample(point, CanvasInputPhase.Up)));
        _activePointerId = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_activePointerId < 0) return;
        _activePointerId = -1;
        _toolController.Cancel();
    }

    private bool CanCommitActiveToolFromClick() =>
        _toolController.ActiveTool switch
        {
            SelectTool selectTool => selectTool.CanCommitFromClick,
            PolylineTool polylineTool => polylineTool.CanCommitFromClick,
            _ => false
        };

    private CanvasInputSample MakeSample(PointerPoint point, CanvasInputPhase phase)
        => CanvasInputSample.FromPointerPoint(
            point, Bounds.Size,
            _document.Width, _document.Height,
            phase);

    private unsafe Color? SampleDocumentColor(int x, int y)
    {
        if ((uint)x >= (uint)_document.Width || (uint)y >= (uint)_document.Height) return null;

        _compositor.Composite(_document.Layers, _document.Width, _document.Height, _document.PaperColor, _document.PaperVisible);
        using var frame = _compositor.Bitmap.Lock();
        if (_compositor.Bitmap.Format != PixelFormat.Bgra8888) return null;

        var row = (byte*)frame.Address + y * frame.RowBytes;
        var px = row + x * 4;
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    private static bool IsPaintInput(PointerPoint point)
    {
        var properties = point.Properties;
        if (properties.IsEraser)
            return properties.IsLeftButtonPressed || properties.Pressure >= PenPressureThreshold;

        return point.Pointer.Type switch
        {
            PointerType.Pen => properties.IsLeftButtonPressed || properties.Pressure >= PenPressureThreshold,
            PointerType.Touch => properties.IsLeftButtonPressed || properties.Pressure >= PenPressureThreshold,
            PointerType.Mouse => properties.IsLeftButtonPressed,
            _ => false
        };
    }
}
