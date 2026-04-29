using System;
using Avalonia;
using Avalonia.Input;

namespace Floss.App.Input;

public enum CanvasInputSource
{
    Mouse,
    Touch,
    Pen,
    Eraser,
    Unknown
}

public enum CanvasInputPhase
{
    Down,
    Move,
    Up,
    Cancel
}

public readonly record struct CanvasInputSample(
    double X,
    double Y,
    double Pressure,
    double TiltX,
    double TiltY,
    double Twist,
    long TimeMicros,
    long PointerId,
    CanvasInputSource Source,
    CanvasInputPhase Phase)
{
    public CanvasInputSample WithPosition(double x, double y, double pressure, long timeMicros)
    {
        return new CanvasInputSample(
            x,
            y,
            pressure,
            TiltX,
            TiltY,
            Twist,
            timeMicros,
            PointerId,
            Source,
            Phase);
    }

    public static CanvasInputSample FromPointerPoint(
        PointerPoint point,
        Size surfaceSize,
        int canvasWidth,
        int canvasHeight,
        CanvasInputPhase phase)
    {
        var properties = point.Properties;
        var source = SourceFromPointer(point);
        var pressure = properties.Pressure;
        if (pressure <= 0 && source == CanvasInputSource.Mouse && properties.IsLeftButtonPressed)
        {
            pressure = 1;
        }

        return new CanvasInputSample(
            X: Math.Clamp(point.Position.X / Math.Max(surfaceSize.Width, 1) * canvasWidth, 0, canvasWidth - 1),
            Y: Math.Clamp(point.Position.Y / Math.Max(surfaceSize.Height, 1) * canvasHeight, 0, canvasHeight - 1),
            Pressure: Math.Clamp(pressure, 0, 1),
            TiltX: properties.XTilt,
            TiltY: properties.YTilt,
            Twist: properties.Twist,
            TimeMicros: Environment.TickCount64 * 1000,
            PointerId: point.Pointer.Id,
            Source: source,
            Phase: phase);
    }

    public double DistanceTo(CanvasInputSample other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static CanvasInputSource SourceFromPointer(PointerPoint point)
    {
        if (point.Properties.IsEraser)
        {
            return CanvasInputSource.Eraser;
        }

        return point.Pointer.Type switch
        {
            PointerType.Pen => CanvasInputSource.Pen,
            PointerType.Touch => CanvasInputSource.Touch,
            PointerType.Mouse => CanvasInputSource.Mouse,
            _ => CanvasInputSource.Unknown
        };
    }
}
