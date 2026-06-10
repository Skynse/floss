using System;

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

    public double DistanceTo(CanvasInputSample other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
