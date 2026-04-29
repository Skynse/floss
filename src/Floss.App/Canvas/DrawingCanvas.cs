using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Canvas;

public sealed class DrawingCanvas : Control
{
    private readonly DrawingDocument _document = new();
    private readonly CanvasTool _tool;
    private BrushPreset _brush = BrushPreset.Defaults[0];
    private Color _paintColor = Color.Parse("#111111");
    private long _activePointerId = -1;

    public DrawingCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        _tool = new CanvasTool(_document, new BrushEngine());
        _document.Changed += (_, _) =>
        {
            InvalidateVisual();
            StatsChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        _document.LayersChanged += (_, _) => LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StatsChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;

    public int ActiveSampleCount => _tool.ActiveSampleCount;
    public int CommittedStrokeCount => _document.CommittedStrokeCount;
    public bool CanUndo => _document.CanUndo;
    public bool CanRedo => _document.CanRedo;
    public bool CanDeleteLayer => _document.CanDeleteLayer;
    public BrushPreset Brush => _brush;
    public Color PaintColor => _paintColor;
    public bool EraserEnabled => _tool.Kind == ToolKind.Eraser;
    public IReadOnlyList<DrawingLayer> Layers => _document.Layers;
    public int ActiveLayerIndex => _document.ActiveLayerIndex;

    public void SetBrush(BrushPreset preset)
    {
        _brush = preset with { Color = _paintColor };
        if (preset.Kind == BrushKind.Eraser)
        {
            _tool.SetKind(ToolKind.Eraser);
        }
    }

    public void SetPaintColor(Color color)
    {
        _paintColor = color;
        if (!EraserEnabled)
        {
            _brush = _brush with { Color = color };
        }
    }

    public void SetBrushSize(double size) => _brush = _brush with { Size = Math.Clamp(size, 1, 256) };
    public void SetBrushOpacity(double opacity) => _brush = _brush with { Opacity = Math.Clamp(opacity, 0.01, 1) };
    public void SetBrushHardness(double hardness) => _brush = _brush with { Hardness = Math.Clamp(hardness, 0, 1) };
    public void SetBrushSpacing(double spacing) => _brush = _brush with { Spacing = Math.Clamp(spacing, 0.02, 1) };

    public void SetTool(string tool)
    {
        _tool.SetKind(tool.Equals("eraser", StringComparison.OrdinalIgnoreCase)
            ? ToolKind.Eraser
            : ToolKind.Brush);
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var target = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(DrawingDocument.PaperColor), target);
        foreach (var layer in _document.Layers)
        {
            if (!layer.IsVisible || layer.Opacity <= 0) continue;
            using (context.PushOpacity(layer.Opacity))
            {
                context.DrawImage(layer.Bitmap, target);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!IsPaintInput(point))
        {
            return;
        }

        Focus();
        var sample = CanvasInputSample.FromPointerPoint(
            point,
            Bounds.Size,
            DrawingDocument.CanvasWidth,
            DrawingDocument.CanvasHeight,
            CanvasInputPhase.Down);
        if (sample.Source == CanvasInputSource.Eraser)
        {
            _tool.SetKind(ToolKind.Eraser);
        }

        _activePointerId = point.Pointer.Id;
        _tool.Begin(_brush, sample);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        if (point.Pointer.Id != _activePointerId)
        {
            return;
        }

        _tool.Update(
            _brush,
            CanvasInputSample.FromPointerPoint(
                point,
                Bounds.Size,
                DrawingDocument.CanvasWidth,
                DrawingDocument.CanvasHeight,
                CanvasInputPhase.Move));
        e.Handled = true;
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
                DrawingDocument.CanvasWidth,
                DrawingDocument.CanvasHeight,
                CanvasInputPhase.Up));
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

    private static bool IsPaintInput(PointerPoint point)
    {
        return point.Pointer.Type == PointerType.Pen ||
               point.Properties.IsLeftButtonPressed ||
               point.Properties.IsEraser;
    }
}
