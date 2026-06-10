using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Tools;

namespace Floss.App.Features.Overlays;

public sealed class CanvasOverlayRegistry : ICanvasOverlayRegistry, ICanvasBoundService
{
    private readonly List<ICanvasOverlay> _overlays = [];
    private DrawingCanvas? _canvas;
    private ICanvasOverlay? _capturingOverlay;
    private int _capturingPointerId = -1;

    public void Register(ICanvasOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (_overlays.Contains(overlay))
            return;

        _overlays.Add(overlay);
        _overlays.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        _canvas?.InvalidateVisual();
    }

    public void Unregister(ICanvasOverlay overlay)
    {
        if (!_overlays.Remove(overlay))
            return;

        if (ReferenceEquals(_capturingOverlay, overlay))
            ClearCapture();
        _canvas?.InvalidateVisual();
    }

    public void BindCanvas(DrawingCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (_canvas != null && !ReferenceEquals(_canvas, canvas))
            _canvas.OverlayRegistry = null;

        ClearCapture();
        _canvas = canvas;
        canvas.OverlayRegistry = this;
    }

    public void Render(DrawingContext context, DrawingCanvas canvas, double zoom)
    {
        if (_overlays.Count == 0 || !canvas.HasDocument)
            return;

        var doc = canvas.Document;
        var ctx = new CanvasOverlayContext
        {
            DrawingContext = context,
            Zoom = zoom,
            DocumentWidth = doc.Width,
            DocumentHeight = doc.Height,
            CanvasWidth = Math.Max(1, canvas.Bounds.Width),
            CanvasHeight = Math.Max(1, canvas.Bounds.Height),
        };

        foreach (var overlay in _overlays)
            overlay.Render(ctx);
    }

    public bool TryHandlePointer(
        ToolInputEventKind kind,
        PointerPoint canvasPoint,
        KeyModifiers modifiers,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight)
    {
        if (_overlays.Count == 0)
            return false;

        var pointerKind = kind switch
        {
            ToolInputEventKind.Down => CanvasOverlayPointerKind.Down,
            ToolInputEventKind.Move => CanvasOverlayPointerKind.Move,
            _ => CanvasOverlayPointerKind.Up,
        };

        var canvasPos = canvasPoint.Position;
        var evt = new CanvasOverlayPointerEvent
        {
            Kind = pointerKind,
            CanvasPosition = canvasPos,
            DocumentPosition = new Point(
                canvasPos.X / Math.Max(1, canvasWidth) * documentWidth,
                canvasPos.Y / Math.Max(1, canvasHeight) * documentHeight),
            PointerId = canvasPoint.Pointer.Id,
            Pressure = (float)canvasPoint.Properties.Pressure,
            Modifiers = modifiers,
        };

        IEnumerable<ICanvasOverlay> candidates = _capturingOverlay != null
            ? [_capturingOverlay]
            : _overlays.OrderBy(static o => o.Order);

        foreach (var overlay in candidates)
        {
            if (!overlay.TryHandlePointer(evt))
                continue;

            if (pointerKind == CanvasOverlayPointerKind.Down)
            {
                _capturingOverlay = overlay;
                _capturingPointerId = evt.PointerId;
            }
            else if (pointerKind == CanvasOverlayPointerKind.Up && _capturingPointerId == evt.PointerId)
            {
                ClearCapture();
            }

            _canvas?.InvalidateVisual();
            return true;
        }

        if (pointerKind == CanvasOverlayPointerKind.Up && _capturingPointerId == evt.PointerId)
            ClearCapture();

        return false;
    }

    private void ClearCapture()
    {
        _capturingOverlay = null;
        _capturingPointerId = -1;
    }
}
