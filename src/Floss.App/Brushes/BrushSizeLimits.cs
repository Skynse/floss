using System;

namespace Floss.App.Brushes;

// canvas-scaled brush size ceiling: max diameter scales with canvas short side.
// Default 100% caps at half the short edge; Brush Studio can raise to 400%.
public static class BrushSizeLimits
{
    public const double MinDiameterPx = 0.5;
    public const double DefaultMaxSizePercent = 100;
    public const double MinMaxSizePercent = 100;
    public const double StudioMaxSizePercent = 400;
    public const double CanvasCapFractionAt100Percent = 0.5;
    public const double FallbackMaxDiameterPx = 1024;
    public const double AbsoluteHardCapPx = 8192;

    public static double EffectiveMaximum(int docWidth, int docHeight, double maxSizePercent = DefaultMaxSizePercent)
    {
        if (docWidth <= 0 || docHeight <= 0)
            return FallbackMaxDiameterPx;

        var shortSide = Math.Min(docWidth, docHeight);
        var percent = Math.Clamp(maxSizePercent, MinMaxSizePercent, StudioMaxSizePercent);
        var max = shortSide * CanvasCapFractionAt100Percent * (percent / 100.0);
        return Math.Clamp(max, MinDiameterPx, AbsoluteHardCapPx);
    }
}
