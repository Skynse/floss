using System;

namespace Floss.App.Input;

public static class BrushSizeAdjustment
{
    public static double FromRadiusDistance(double radiusDistance, double minimum, double maximum)
        => Clamp(radiusDistance * 2.0, minimum, maximum);

    public static double Nudge(double currentSize, int direction, double configuredStep, double minimum, double maximum)
    {
        if (direction == 0) return Clamp(currentSize, minimum, maximum);

        var safeCurrent = Clamp(currentSize, minimum, maximum);
        var safeStep = Math.Max(0.1, configuredStep);
        var delta = Math.Max(safeStep, safeCurrent * safeStep * 0.02);
        return Clamp(safeCurrent + Math.Sign(direction) * delta, minimum, maximum);
    }

    private static double Clamp(double value, double minimum, double maximum)
        => Math.Clamp(value, minimum, maximum);
}
