using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App.Canvas;

/// <summary>
/// Draws the tool cursor across the entire workspace viewport (margins and canvas).
/// Hit-test disabled so pointer events reach the canvas and viewport handlers below.
/// </summary>
internal sealed class ViewportCursorOverlay : Control
{
    private DrawingCanvas? _canvas;

    internal DrawingCanvas? Canvas
    {
        get => _canvas;
        set
        {
            if (ReferenceEquals(_canvas, value)) return;
            _canvas = value;
            InvalidateVisual();
        }
    }

    public ViewportCursorOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    internal void NotifyCursorChanged() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_canvas == null)
            return;

        if (_canvas.IsBrushResizePreviewActive)
        {
            _canvas.RenderBrushResizePreviewInViewportSpace(context, this);
            return;
        }

        _canvas.RenderToolCursorInViewportSpace(context, this);
    }
}
