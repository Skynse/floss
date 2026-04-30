using System;
using System.Collections.Generic;

namespace Floss.App.Brushes;

public enum SensorCombineMode { Multiply, Add }

// Maps one or more sensor inputs through cubic curves to produce a [0,1] multiplier.
// Disabled or empty → returns 1.0 (no effect on the target parameter).
public sealed class CurveOption
{
    public bool             IsEnabled   { get; set; } = true;
    public float            Strength    { get; set; } = 1.0f;
    public float            MinOutput   { get; set; } = 0.0f;
    public float            MaxOutput   { get; set; } = 1.0f;
    public SensorCombineMode CombineMode { get; set; } = SensorCombineMode.Multiply;
    public List<SensorConfig> Sensors   { get; set; } = [];

    // Returns a multiplier in approximately [MinOutput, MaxOutput], lerped toward 1.0
    // by (1 - Strength). No sensors / disabled → 1.0.
    public float Compute(in StrokePoint sp)
    {
        if (!IsEnabled || Sensors.Count == 0) return 1.0f;

        float combined;
        if (CombineMode == SensorCombineMode.Multiply)
        {
            combined = 1.0f;
            foreach (var s in Sensors) combined *= s.CurvedValue(sp);
        }
        else
        {
            combined = 0.0f;
            foreach (var s in Sensors) combined += s.CurvedValue(sp);
            combined = Math.Clamp(combined / Sensors.Count, 0, 1);
        }

        float raw = MinOutput + (MaxOutput - MinOutput) * combined;
        return 1.0f - Strength + Strength * raw;
    }

    public CurveOption Clone()
    {
        var c = new CurveOption
        {
            IsEnabled = IsEnabled, Strength = Strength,
            MinOutput = MinOutput, MaxOutput = MaxOutput,
            CombineMode = CombineMode
        };
        foreach (var s in Sensors) c.Sensors.Add(s.Clone());
        return c;
    }

    // ── Factories ────────────────────────────────────────────────────────────

    public static CurveOption Off() => new() { IsEnabled = false };

    public static CurveOption Pressure(float gamma, float min = 0f, float max = 1f)
    {
        var opt = new CurveOption { MinOutput = min, MaxOutput = max };
        opt.Sensors.Add(new SensorConfig { Type = SensorType.Pressure, Curve = GammaCurve(gamma) });
        return opt;
    }

    // Pressure + inverse-speed combined (size shrinks at high velocity).
    public static CurveOption PressureSpeed(float gamma, float velStrength, float min = 0f, float max = 1f)
    {
        var opt = new CurveOption { MinOutput = min, MaxOutput = max };
        opt.Sensors.Add(new SensorConfig { Type = SensorType.Pressure, Curve = GammaCurve(gamma) });
        opt.Sensors.Add(new SensorConfig { Type = SensorType.Speed,    Curve = InverseSpeedCurve(velStrength) });
        return opt;
    }

    private static CubicCurve GammaCurve(float gamma)
    {
        const int steps = 9;
        var pts = new CurvePoint[steps];
        for (int i = 0; i < steps; i++)
        {
            float x = i / (float)(steps - 1);
            pts[i] = new CurvePoint(x, Math.Clamp(MathF.Pow(x, gamma), 0, 1));
        }
        var c = new CubicCurve();
        c.SetPoints(pts);
        return c;
    }

    // x=0 (slow) → 1.0, x=1 (fast) → (1-strength)
    private static CubicCurve InverseSpeedCurve(float strength)
    {
        var c = new CubicCurve();
        c.SetPoints([new(0f, 1f), new(1f, Math.Clamp(1f - strength, 0.05f, 1f))]);
        return c;
    }
}
