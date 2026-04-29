using System;
using System.Collections.Generic;
using Floss.App.Input;

namespace Floss.App.Brushes;

public enum DynamicInputSource
{
    Pressure,
    Tilt,
    Velocity
}

public enum DynamicOutputTarget
{
    Size,
    Opacity,
    Scatter,
    Angle
}

public enum ResponseCurveKind
{
    Linear,
    Power,
    Bezier
}

public sealed class ResponseCurve
{
    public const int LutSize = 256;

    public ResponseCurve(
        DynamicInputSource source,
        DynamicOutputTarget target,
        ResponseCurveKind kind = ResponseCurveKind.Power,
        float strength = 1.0f,
        float gamma = 1.0f,
        float minimum = 0.0f,
        float maximum = 1.0f,
        float bezierX1 = 0.25f,
        float bezierY1 = 0.25f,
        float bezierX2 = 0.75f,
        float bezierY2 = 0.75f,
        bool invert = false)
    {
        Source = source;
        Target = target;
        Kind = kind;
        Strength = Math.Clamp(strength, 0, 1);
        Gamma = Math.Max(0.001f, gamma);
        Minimum = minimum;
        Maximum = maximum;
        BezierX1 = bezierX1;
        BezierY1 = bezierY1;
        BezierX2 = bezierX2;
        BezierY2 = bezierY2;
        Invert = invert;
        Lut = new float[LutSize];
        RebuildLut();
    }

    public DynamicInputSource Source { get; }
    public DynamicOutputTarget Target { get; }
    public ResponseCurveKind Kind { get; }
    public float Strength { get; }
    public float Gamma { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public float BezierX1 { get; }
    public float BezierY1 { get; }
    public float BezierX2 { get; }
    public float BezierY2 { get; }
    public bool Invert { get; }
    public float[] Lut { get; }

    public float Evaluate(float normalizedInput)
        => Lut[Math.Clamp((int)(Math.Clamp(normalizedInput, 0, 1) * 255), 0, LutSize - 1)];

    private void RebuildLut()
    {
        for (var i = 0; i < Lut.Length; i++)
        {
            var x = i / 255.0f;
            var y = Kind switch
            {
                ResponseCurveKind.Linear => x,
                ResponseCurveKind.Bezier => CubicBezierY(x),
                _ => MathF.Pow(x, Gamma)
            };

            if (Invert) y = 1.0f - y;
            y = Minimum + (Maximum - Minimum) * y;
            Lut[i] = 1.0f + (y - 1.0f) * Strength;
        }
    }

    private float CubicBezierY(float t)
    {
        var inv = 1.0f - t;
        return inv * inv * inv * 0.0f
            + 3.0f * inv * inv * t * BezierY1
            + 3.0f * inv * t * t * BezierY2
            + t * t * t;
    }
}

public sealed class DynamicsMatrix
{
    private readonly IReadOnlyList<ResponseCurve> _curves;

    public DynamicsMatrix(IReadOnlyList<ResponseCurve> curves)
    {
        _curves = curves;
    }

    public static DynamicsMatrix FromBrush(BrushPreset brush)
    {
        var curves = new List<ResponseCurve>(4);

        if (brush.PressureToSize)
        {
            curves.Add(new ResponseCurve(
                DynamicInputSource.Pressure,
                DynamicOutputTarget.Size,
                gamma: (float)brush.PressureCurve,
                minimum: 0.0f,
                maximum: 1.0f));
        }

        if (brush.PressureToOpacity)
        {
            curves.Add(new ResponseCurve(
                DynamicInputSource.Pressure,
                DynamicOutputTarget.Opacity,
                gamma: (float)brush.PressureCurve,
                minimum: 0.0f,
                maximum: 1.0f));
        }

        if (brush.VelocityToSize)
        {
            curves.Add(new ResponseCurve(
                DynamicInputSource.Velocity,
                DynamicOutputTarget.Size,
                kind: ResponseCurveKind.Linear,
                strength: 1.0f,
                minimum: (float)Math.Clamp(1.0 - brush.VelocitySize, 0.1, 1.0),
                maximum: 1.0f,
                invert: true));
        }

        if (brush.VelocityToOpacity)
        {
            curves.Add(new ResponseCurve(
                DynamicInputSource.Velocity,
                DynamicOutputTarget.Opacity,
                kind: ResponseCurveKind.Linear,
                strength: 1.0f,
                minimum: (float)Math.Clamp(1.0 - brush.VelocityOpacity, 0.1, 1.0),
                maximum: 1.0f,
                invert: true));
        }

        return new DynamicsMatrix(curves);
    }

    public float Evaluate(DynamicOutputTarget target, CanvasInputSample sample, float velocity01)
    {
        var result = 1.0f;
        for (var i = 0; i < _curves.Count; i++)
        {
            var curve = _curves[i];
            if (curve.Target != target) continue;
            result *= curve.Evaluate(InputValue(curve.Source, sample, velocity01));
        }

        return result;
    }

    private static float InputValue(DynamicInputSource source, CanvasInputSample sample, float velocity01)
    {
        return source switch
        {
            DynamicInputSource.Pressure => (float)Math.Clamp(sample.Pressure, 0, 1),
            DynamicInputSource.Tilt => (float)Math.Clamp(Math.Sqrt(sample.TiltX * sample.TiltX + sample.TiltY * sample.TiltY) / 90.0, 0, 1),
            DynamicInputSource.Velocity => Math.Clamp(velocity01, 0, 1),
            _ => 0
        };
    }
}
