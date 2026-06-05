using System;

namespace Floss.App.Brushes;

// Spacing slider is always a fraction of brush diameter (UI shows %).
// Auto ON uses Krita sqrt curve with coeff derived from that % at brush.Size so
// full-size dabs match the slider; auto OFF is linear stampSize * fraction.
// GapMode is preset-authoring metadata only; runtime always reads Spacing.
public static class BrushSpacing
{
    public const float MinDistancePx = 0.5f;
    public const float MinStampSizePx = 0.01f;
    public const float MinSpacingValue = 0.02f;
    public const float MaxSpacingValue = 4f;

    public const float NormalGapFraction = 0.25f;
    public const float WideGapFraction = 0.40f;
    public const float NarrowGapFraction = 0.12f;

    public static float GapFraction(BrushGapMode mode, float fixedSpacing)
        => mode switch
        {
            BrushGapMode.Fixed => Math.Clamp(fixedSpacing, MinSpacingValue, MaxSpacingValue),
            BrushGapMode.Normal => NormalGapFraction,
            BrushGapMode.Wide => WideGapFraction,
            BrushGapMode.Narrow => NarrowGapFraction,
            _ => NormalGapFraction
        };

    public static float CalcAutoSpacing(float brushSize, float coeff)
        => coeff * (brushSize < 1f ? brushSize : MathF.Sqrt(brushSize));

    /// <summary>
    /// User-facing spacing value from preset. Legacy files may store auto coeff in AutoSpacingCoeff
    /// while Spacing stayed at the 0.1 placeholder (Krita-style).
    /// </summary>
    public static float ResolveSpacing(BrushPreset brush)
    {
        var spacing = (float)brush.Spacing;
        if (brush.AutoSpacingActive
            && brush.AutoSpacingCoeff > 0
            && Math.Abs(spacing - 0.1) < 0.001
            && Math.Abs(brush.AutoSpacingCoeff - 1.0) > 0.001)
        {
            spacing = (float)brush.AutoSpacingCoeff;
        }

        return Math.Clamp(spacing, MinSpacingValue, MaxSpacingValue);
    }

    /// <summary>
    /// One-time normalization when loading a saved preset that still uses CSP GapMode authoring.
    /// </summary>
    public static double NormalizeSpacingAtLoad(
        double spacing,
        double autoSpacingCoeff,
        bool autoSpacingActive,
        BrushGapMode gapMode)
    {
        if (autoSpacingActive
            && autoSpacingCoeff > 0
            && Math.Abs(spacing - 0.1) < 0.001
            && Math.Abs(autoSpacingCoeff - 1.0) > 0.001)
        {
            spacing = autoSpacingCoeff;
        }
        else if (!autoSpacingActive && gapMode != BrushGapMode.Fixed)
        {
            spacing = GapFraction(gapMode, (float)spacing);
        }

        return Math.Clamp(spacing, MinSpacingValue, MaxSpacingValue);
    }

    /// <summary>
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
        var spacingVal = ResolveSpacing(brush);

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
            // UI "%" at brush.Size must equal spacingVal * brush.Size (not raw coeff 0.02 → 0.64px at 1024).
            var refSize = Math.Max(MinStampSizePx, (float)brush.Size);
            var autoCoeff = spacingVal * MathF.Sqrt(refSize);
            baseSpacing = CalcAutoSpacing(stampSize, autoCoeff) * spacingMultiplier;
        }
        else
        {
            baseSpacing = stampSize * spacingVal * spacingMultiplier;
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
