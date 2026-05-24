using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Tools;

namespace Floss.App.Canvas;

/// <summary>
/// Selection outline drawn outside the main canvas render path so tile
/// recomposites do not re-rasterize the selection border every frame.
/// </summary>
internal sealed class SelectionOutlineOverlay : Control
{
    private DrawingCanvas? _canvas;
    private readonly DispatcherTimer _animTimer;
    private float _phase;
    private bool _useCpuFallback;

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

            _useCpuFallback = false;
            UpdateAnimationState();
            InvalidateVisual();
        }
    }

    public SelectionOutlineOverlay(DrawingCanvas canvas)
    {
        _animTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, OnAnimationTick);
        Canvas = canvas;
    }

    private void OnCanvasSelectionOutlineChanged(object? sender, EventArgs e)
    {
        _useCpuFallback = false;
        UpdateAnimationState();
        InvalidateVisual();
    }

    private void UpdateAnimationState()
    {
        if (_canvas?.Selection.HasSelection == true && !_useCpuFallback)
            _animTimer.Start();
        else
            _animTimer.Stop();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_canvas?.Selection.HasSelection != true)
        {
            _animTimer.Stop();
            return;
        }

        _phase += SelectionMarchingAntsRenderer.AntPhaseStepPx;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var canvas = Canvas;
        if (canvas == null || canvas.Document.Width <= 0 || canvas.Document.Height <= 0)
            return;

        if (canvas.ActiveTool is TransformTool)
            return;

        if (canvas.ShouldHideCommittedSelectionDuringGesture())
            return;

        if (!_useCpuFallback
            && SelectionMarchingAntsRenderer.TryDraw(context, canvas.Selection, canvas.CanvasZoom, _phase))
        {
            return;
        }

        _useCpuFallback = true;
        _animTimer.Stop();
        canvas.Selection.RenderOverlay(context, canvas.CanvasZoom);
    }
}
