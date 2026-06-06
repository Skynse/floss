using System;

namespace Floss.App.Canvas.FloodFill;

/// <summary>Unified Manhattan RGBA tolerance for wand and bucket fill.</summary>
public static class ColorDifference
{
    public static int Tolerance01ToThreshold(double tolerance01)
        => (int)(Math.Clamp(tolerance01, 0, 1) * 255 * 4);

    public static bool IsSimilarBgra(
        byte b, byte g, byte r, byte a,
        byte refB, byte refG, byte refR, byte refA,
        int threshold)
        => Math.Abs(b - refB) + Math.Abs(g - refG) + Math.Abs(r - refR) + Math.Abs(a - refA) <= threshold;

    public static bool IsSimilarBgra(
        ReadOnlySpan<byte> pixelBgra,
        byte refB, byte refG, byte refR, byte refA,
        int threshold)
        => IsSimilarBgra(pixelBgra[0], pixelBgra[1], pixelBgra[2], pixelBgra[3],
            refB, refG, refR, refA, threshold);
}
