using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Floss.App.Tools;

namespace Floss.App.Canvas;

/// <summary>
/// Selection outline drawn outside the main canvas render path so tile
/// recomposites do not re-rasterize the selection border every frame.
/// </summary>
internal sealed class SelectionOutlineOverlay : Control
{
    private DrawingCanvas? _canvas;

    internal DrawingCanvas? Canvas
    {
        get => _canvas;
        set
        {
            if (ReferenceEquals(_canvas, value)) return;
            if (_canvas != null)
            {
                _canvas.SelectionChanged -= OnCanvasSelectionOutlineChanged;
                _canvas.SelectionOutlineChanged -= OnCanvasSelectionOutlineChanged;
            }

            _canvas = value;
            if (_canvas != null)
            {
                _canvas.SelectionChanged += OnCanvasSelectionOutlineChanged;
                _canvas.SelectionOutlineChanged += OnCanvasSelectionOutlineChanged;
            }

            InvalidateVisual();
        }
    }

    public SelectionOutlineOverlay(DrawingCanvas canvas) => Canvas = canvas;

    private void OnCanvasSelectionOutlineChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var canvas = Canvas;
        if (canvas == null || canvas.Document.Width <= 0 || canvas.Document.Height <= 0)
            return;

        if (canvas.ActiveTool is TransformTool)
            return;

        if (canvas.IsActiveSelectionGesture())
            return;

        canvas.Selection.RenderOverlay(context, canvas.CanvasZoom);
    }
}
