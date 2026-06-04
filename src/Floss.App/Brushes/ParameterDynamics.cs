using System;

namespace Floss.App.Brushes;

public sealed record ParameterDynamics
{
    public bool PressureEnabled { get; init; } = false;
    public float[] CurveData { get; init; } = IdentityCurve;
    public float Min { get; init; } = 0.0f;
    public float Max { get; init; } = 1.0f;

    public bool VelocityEnabled { get; init; } = false;
    /// <summary>Legacy scalar; kept for old brush files. Prefer <see cref="VelocityCurveData"/>.</summary>
    public float VelocityStrength { get; init; } = 0.3f;
    public float[] VelocityCurveData { get; init; } = DefaultVelocityCurveData;

    public bool TiltEnabled { get; init; } = false;
    public float[] TiltCurveData { get; init; } = IdentityCurve;

    public bool RandomEnabled { get; init; } = false;
    public float[] RandomCurveData { get; init; } = IdentityCurve;

    public bool DistanceEnabled { get; init; } = false;
    public float DistanceLength { get; init; } = 1000f;
    public float[] DistanceCurveData { get; init; } = DefaultDistanceCurveData;

    public bool FadeEnabled { get; init; } = false;
    public float FadeLength { get; init; } = 120f;
    public float[] FadeCurveData { get; init; } = DefaultFadeCurveData;

    public static float[] IdentityCurve => [0f, 0f, 1f, 1f];
    public static float[] DefaultVelocityCurveData => [0f, 1f, 1f, 0.7f];
    public static float[] DefaultDistanceCurveData => [0f, 1f, 1f, 0f];
    public static float[] DefaultFadeCurveData => [0f, 1f, 1f, 0f];

    public static float[] VelocityCurveFromStrength(float strength)
        => [0f, 1f, 1f, Math.Clamp(1f - strength, 0.05f, 1f)];

    /// <summary>Collapse legacy 9-point auto-sampled curves to two endpoints for the editor.</summary>
    public static float[] NormalizeCurveDataForEditor(float[] data)
    {
        if (data == null || data.Length <= 4)
            return data is { Length: >= 4 } ? data : IdentityCurve;

        var count = data.Length / 2;
        if (count < 3)
            return IdentityCurve;

        var evenlySpaced = true;
        for (var i = 0; i < count; i++)
        {
            var expected = i / (float)(count - 1);
            if (Math.Abs(data[i * 2] - expected) > 0.03f)
            {
                evenlySpaced = false;
                break;
            }
        }

        return evenlySpaced ? IdentityCurve : data;
    }

    public static ParameterDynamics Off => new();

    public static ParameterDynamics DefaultSize => new()
    {
        PressureEnabled = true,
        CurveData = [0f, 0f, 0.4f, 0.25f, 1f, 1f],
        Min = 0.0f,
        Max = 1.0f,
        VelocityEnabled = true,
        VelocityStrength = 0.18f,
        VelocityCurveData = VelocityCurveFromStrength(0.18f)
    };

    public static ParameterDynamics DefaultOpacity => new()
    {
        PressureEnabled = true,
        CurveData = [0f, 0f, 0.4f, 0.25f, 1f, 1f],
        Min = 0.0f,
        Max = 1.0f,
        VelocityEnabled = false
    };
}
