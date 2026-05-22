using System;

namespace Floss.App.Brushes;

// Krita-style spacing: manual (size × spacing), auto (√size × coeff), optional speed widening.
public static class BrushSpacing
{
    public const float MinDistancePx = 0.5f;
    public const float MinStampSizePx = 0.01f;

    public static float CalcAutoSpacing(float brushSize, float coeff)
        => coeff * (brushSize < 1f ? brushSize : MathF.Sqrt(brushSize));

    public static float EffectiveDistance(
        BrushPreset brush,
        float stampSize,
        float spacingMultiplier,
        float speed01)
    {
        stampSize = Math.Max(MinStampSizePx, stampSize);
        spacingMultiplier = Math.Clamp(spacingMultiplier, 0.05f, 4f);

        float baseSpacing;
        if (brush.AutoSpacingActive)
        {
            baseSpacing = CalcAutoSpacing(stampSize, (float)brush.AutoSpacingCoeff) * spacingMultiplier;
        }
        else
        {
            baseSpacing = stampSize
                * Math.Clamp((float)brush.Spacing, 0.005f, 4f)
                * spacingMultiplier;
        }

        var flow = Math.Clamp((float)brush.Flow, 0.01f, 1f);
        baseSpacing *= MathF.Sqrt(flow);

        if (brush.SpeedSpacingStrength > 0.001)
        {
            var speedMul = 1f + (float)brush.SpeedSpacingStrength * Math.Clamp(speed01, 0f, 1f);
            baseSpacing *= speedMul;
        }

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
