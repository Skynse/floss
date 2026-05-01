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
    private const double PressureFloor = 0.005;

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

        // Clamp NaN so it doesn't propagate through brush math
        if (double.IsNaN(pressure))
            pressure = 0;

        if (source == CanvasInputSource.Mouse && properties.IsLeftButtonPressed)
        {
            // Heuristic: if this "mouse" reports pressure, tilt, or twist, it's likely a pen
            // tablet that the platform driver is exposing as a mouse. Trust the reported
            // pressure instead of forcing full pressure.
            var hasTabletData = pressure > 0
                || properties.XTilt != 0
                || properties.YTilt != 0
                || properties.Twist != 0;

            if (!hasTabletData && pressure <= 0)
            {
                pressure = 1;
            }
        }
        else if ((source is CanvasInputSource.Pen or CanvasInputSource.Eraser or CanvasInputSource.Touch) && pressure < PressureFloor)
        {
            pressure = 0;
        }

        var x = point.Position.X / Math.Max(surfaceSize.Width, 1) * canvasWidth;
        var y = point.Position.Y / Math.Max(surfaceSize.Height, 1) * canvasHeight;

        return new CanvasInputSample(
            X: Math.Clamp(x, 0, canvasWidth - 1),
            Y: Math.Clamp(y, 0, canvasHeight - 1),
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
