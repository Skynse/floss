using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Floss.App.Features.Overlays;

/// <summary>Draws on the active canvas and optionally handles pointer input before tools.</summary>
public interface ICanvasOverlay
{
    int Order { get; }

    void Render(CanvasOverlayContext context);

    /// <summary>Return true when the event is consumed (blocks the active tool for this event).</summary>
    bool TryHandlePointer(CanvasOverlayPointerEvent pointerEvent);
}

public sealed class CanvasOverlayContext
{
    public required DrawingContext DrawingContext { get; init; }

    public required double Zoom { get; init; }

    public required int DocumentWidth { get; init; }

    public required int DocumentHeight { get; init; }

    public required double CanvasWidth { get; init; }

    public required double CanvasHeight { get; init; }

    public Point CanvasToDocument(Point canvasPoint)
    {
        var w = Math.Max(1, CanvasWidth);
        var h = Math.Max(1, CanvasHeight);
        return new Point(
            canvasPoint.X / w * DocumentWidth,
            canvasPoint.Y / h * DocumentHeight);
    }
}

public enum CanvasOverlayPointerKind
{
    Down,
    Move,
    Up,
}

public sealed class CanvasOverlayPointerEvent
{
    public required CanvasOverlayPointerKind Kind { get; init; }

    public required Point CanvasPosition { get; init; }

    public required Point DocumentPosition { get; init; }

    public required int PointerId { get; init; }

    public required float Pressure { get; init; }

    public KeyModifiers Modifiers { get; init; }
}
