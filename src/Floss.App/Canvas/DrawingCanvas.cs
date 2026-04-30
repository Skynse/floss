using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Canvas;

public sealed class DrawingCanvas : Control
{
    private readonly DrawingDocument _document = new();
    private readonly CanvasTool _tool;
    private readonly LayerCompositor _compositor;
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
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _tool = new CanvasTool(_document, new BrushEngine());
        _compositor = new LayerCompositor();
        _document.Changed += (_, e) =>
        {
            _compositor.Invalidate(e.DirtyRegion, _document.Layers, e.LayerIndex);
            InvalidateVisual();
            StatsChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        _document.LayersChanged += (_, _) =>
        {
            _compositor.Invalidate(null);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.LayerMetadataChanged += (_, e) => LayerMetadataChanged?.Invoke(this, e);
    }

    public event EventHandler? StatsChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;
    public event EventHandler<LayerMetadataChangedEventArgs>? LayerMetadataChanged;

    public int ActiveSampleCount => _tool.ActiveSampleCount;
    public int CommittedStrokeCount => _document.CommittedStrokeCount;
    public bool CanUndo => _document.CanUndo;
    public bool CanRedo => _document.CanRedo;
    public bool CanDeleteLayer => _document.CanDeleteLayer;
    public BrushPreset Brush => _brush;
    public Color PaintColor => _paintColor;
    public bool EraserEnabled => _tool.Kind == ToolKind.Eraser;
    public DrawingDocument Document => _document;
    public IReadOnlyList<DrawingLayer> Layers => _document.Layers;
    public int ActiveLayerIndex => _document.ActiveLayerIndex;

    public double CanvasRotation { get; set; }
    public bool PaintInputSuspended { get; set; }
    public double CanvasZoom { get; set; } = 1.0;

    public void SetBrush(BrushPreset preset)
    {
        _brush = preset with { Color = _paintColor };
        if (preset.Kind == BrushKind.Eraser)
        {
            _tool.SetKind(ToolKind.Eraser);
        }
        InvalidateVisual();
    }

    public void SetPaintColor(Color color)
    {
        _paintColor = color;
        if (!EraserEnabled)
        {
            _brush = _brush with { Color = color };
        }
        InvalidateVisual();
    }

    public void SetBrushSize(double size)
    {
        _brush = _brush with { Size = Math.Clamp(size, 1, 2000) };
        InvalidateVisual();
    }

    public void SetBrushOpacity(double opacity)
    {
        _brush = _brush with { Opacity = Math.Clamp(opacity, 0.01, 1) };
        InvalidateVisual();
    }

    public void SetBrushHardness(double hardness)
    {
        _brush = _brush with { Hardness = Math.Clamp(hardness, 0, 1) };
        InvalidateVisual();
    }

    public void SetBrushSpacing(double spacing)
    {
        _brush = _brush with { Spacing = Math.Clamp(spacing, 0.02, 1) };
        InvalidateVisual();
    }

    public void SetBrushSmoothing(double smoothing)
    {
        _brush = _brush with { Smoothing = Math.Clamp(smoothing, 0, 0.95) };
        InvalidateVisual();
    }

    public void SetBrushGrain(double grain)
    {
        _brush = _brush with { Grain = Math.Clamp(grain, 0, 1) };
        InvalidateVisual();
    }

    public void SetTool(string tool)
    {
        _tool.SetKind(tool.Equals("eraser", StringComparison.OrdinalIgnoreCase)
            ? ToolKind.Eraser
            : ToolKind.Brush);
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

    public void Clear(bool pushHistory = true) => _document.ClearActiveLayer(pushHistory);
    public void Undo() => _document.Undo();
    public void Redo() => _document.Redo();
    public void AddLayer() => _document.AddLayer();
    public void DuplicateLayer() => _document.DuplicateActiveLayer();
    public void DeleteLayer() => _document.DeleteActiveLayer();
    public void SelectLayer(int index) => _document.SelectLayer(index);
    public void ToggleLayerVisibility(int index) => _document.ToggleLayerVisibility(index);
    public void ToggleLayerLock(int index) => _document.ToggleLayerLock(index);
    public void MoveActiveLayer(int delta) => _document.MoveActiveLayer(delta);
    public void SetActiveLayerOpacity(double opacity) => _document.SetActiveLayerOpacity(opacity);
    public void SetActiveLayerBlendMode(string blendMode) => _document.SetActiveLayerBlendMode(blendMode);
    public void SetActiveLayerName(string name) => _document.SetActiveLayerName(name);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _compositor.Composite(_document.Layers, _document.Width, _document.Height);
        var target = new Rect(Bounds.Size);
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None,
            EdgeMode = EdgeMode.Aliased
        }))
        {
            context.DrawImage(_compositor.Bitmap, target);
        }

        if (_isPointerOver || _isCursorPreviewLocked)
        {
            var mode = App.Config.BrushCursorMode;
            var pos = _isCursorPreviewLocked ? _lockedPointerPos : _pointerPos;
            var t = Math.Max(0.5, 1.5 / CanvasZoom);
            if (_forceBrushOutlineCursor || mode is BrushCursorMode.Outline or BrushCursorMode.DotAndOutline)
            {
                var r = _brush.Size * 0.5;
                context.DrawEllipse(null, new Pen(CursorOuterBrush, t * 3), pos, r, r);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), pos, r, r);
            }

            if (mode is BrushCursorMode.Dot or BrushCursorMode.DotAndOutline)
            {
                var r = Math.Max(2.5 / CanvasZoom, t * 2);
                context.DrawEllipse(CursorOuterBrush, null, pos, r, r);
                context.DrawEllipse(CursorInnerBrush, null, pos, Math.Max(0.5 / CanvasZoom, r * 0.45), Math.Max(0.5 / CanvasZoom, r * 0.45));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (PaintInputSuspended)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!IsPaintInput(point))
        {
            return;
        }

        Focus();
        var sample = CanvasInputSample.FromPointerPoint(
            point,
            Bounds.Size,
            _document.Width,
            _document.Height,
            CanvasInputPhase.Down,
            CanvasRotation);
        if (sample.Source == CanvasInputSource.Eraser)
        {
            _tool.SetKind(ToolKind.Eraser);
        }

        _activePointerId = point.Pointer.Id;
        _tool.Begin(_brush, sample);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        Cursor = new Cursor(StandardCursorType.None);
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
        _pointerPos = point.Position;

        if (PaintInputSuspended)
        {
            if (_activePointerId >= 0)
            {
                _activePointerId = -1;
                _tool.Cancel();
                e.Pointer.Capture(null);
            }
            InvalidateVisual();
            return;
        }

        if (point.Pointer.Id == _activePointerId && IsPaintInput(point))
        {
            _tool.Update(
                _brush,
                CanvasInputSample.FromPointerPoint(
                    point,
                    Bounds.Size,
                    _document.Width,
                    _document.Height,
                    CanvasInputPhase.Move,
                    CanvasRotation));
            e.Handled = true;
        }
        else
        {
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        if (point.Pointer.Id != _activePointerId)
        {
            return;
        }

        _tool.End(
            _brush,
            CanvasInputSample.FromPointerPoint(
                point,
                Bounds.Size,
                _document.Width,
                _document.Height,
                CanvasInputPhase.Up,
                CanvasRotation));
        _activePointerId = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _activePointerId = -1;
        _tool.Cancel();
    }

    private Point TransformPointer(Point point)
    {
        if (Math.Abs(CanvasRotation) < 0.01)
            return point;

        var cx = _document.Width / 2.0;
        var cy = _document.Height / 2.0;
        var angle = -CanvasRotation * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        var dx = point.X - cx;
        var dy = point.Y - cy;

        return new Point(
            cx + dx * cos - dy * sin,
            cy + dx * sin + dy * cos);
    }

    private static bool IsPaintInput(PointerPoint point)
    {
        var properties = point.Properties;
        if (properties.IsEraser)
            return properties.Pressure > 0 || properties.IsLeftButtonPressed;

        return point.Pointer.Type switch
        {
            PointerType.Pen => properties.Pressure > 0 || properties.IsLeftButtonPressed,
            PointerType.Touch => properties.Pressure > 0 || properties.IsLeftButtonPressed,
            PointerType.Mouse => properties.IsLeftButtonPressed,
            _ => false
        };
    }
}
