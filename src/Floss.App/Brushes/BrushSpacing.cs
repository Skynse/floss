using System;

namespace Floss.App.Brushes;

// CSP-style gap spacing: Fixed uses the Spacing slider; Normal/Wide/Narrow pick
// diameter fractions tuned for smooth strokes without over-stamping large brushes.
public static class BrushSpacing
{
    public const float MinDistancePx = 0.5f;
    public const float MinStampSizePx = 0.01f;

    public const float NormalGapFraction = 0.25f;
    public const float WideGapFraction = 0.40f;
    public const float NarrowGapFraction = 0.12f;

    public static float GapFraction(BrushGapMode mode, float fixedSpacing)
        => mode switch
        {
            BrushGapMode.Fixed => Math.Clamp(fixedSpacing, 0.005f, 4f),
            BrushGapMode.Normal => NormalGapFraction,
            BrushGapMode.Wide => WideGapFraction,
            BrushGapMode.Narrow => NarrowGapFraction,
            _ => NormalGapFraction
        };

    public static float CalcAutoSpacing(float brushSize, float coeff)
        => coeff * (brushSize < 1f ? brushSize : MathF.Sqrt(brushSize));

    /// <summary>
    /// MyPaint-style spacing: combines dabs-per-radius and dabs-per-second.
    /// Returns the distance between dabs in pixels.
    /// </summary>
    public static float EffectiveDistance(
        BrushPreset brush,
        float stampSize,
        float spacingMultiplier,
        float speed01 = 0f,
        float dabsPerBasicRadius = -1,
        float dabsPerActualRadius = -1,
        float dabsPerSecond = -1)
    {
        stampSize = Math.Max(MinStampSizePx, stampSize);
        spacingMultiplier = Math.Clamp(spacingMultiplier, 0.05f, 4f);

        // MyPaint-style three-term spacing
        float baseSpacing;
        if (dabsPerBasicRadius >= 0 && dabsPerActualRadius >= 0 && dabsPerSecond >= 0)
        {
            float baseRadius = Math.Max(MinStampSizePx, (float)brush.Size);
            float basicSpacing = dabsPerBasicRadius > 0.001f ? baseRadius / dabsPerBasicRadius : float.MaxValue;
            float actualSpacing = dabsPerActualRadius > 0.001f ? stampSize / dabsPerActualRadius : float.MaxValue;
            float timeSpacing = dabsPerSecond > 0.001f ? speed01 * 5000f / dabsPerSecond : float.MaxValue;
            baseSpacing = Math.Min(basicSpacing, Math.Min(actualSpacing, timeSpacing));
        }
        else if (brush.AutoSpacingActive)
        {
            baseSpacing = CalcAutoSpacing(stampSize, (float)brush.AutoSpacingCoeff) * spacingMultiplier;
        }
        else
        {
            var gapFraction = GapFraction(brush.GapMode, (float)brush.Spacing);
            baseSpacing = stampSize * gapFraction * spacingMultiplier;
        }

        var flow = Math.Clamp((float)brush.Flow, 0.01f, 1f);
        baseSpacing *= MathF.Sqrt(flow);

        return Math.Max(MinDistancePx, baseSpacing);
    }

    public static float EstimateDistance(BrushPreset brush, float stampSize = -1)
    {
        if (stampSize <= 0)
            stampSize = (float)Math.Max(MinStampSizePx, brush.Size);
        return EffectiveDistance(brush, stampSize, 1f, 0f);
    }

    public static bool IsStampTooSmall(float stampSize)
        => stampSize < MinStampSizePx;
}
