using System;

namespace Floss.App.Brushes;

public enum SensorType
{
    Pressure,
    Speed,
    Distance,
    Fade,
    Random,
    StrokeRandom,
    DrawingAngle,
    TiltX,
    TiltY,
    Rotation,
    /// <summary>Combined pen tilt magnitude (horizontal → vertical).</summary>
    Tilt
}

public sealed class SensorConfig
{
    public SensorType Type { get; set; } = SensorType.Pressure;
    public CubicCurve Curve { get; set; } = CubicCurve.Identity();
    public float Length { get; set; } = 1000f; // pixels for Distance; dab count for Fade

    public float RawValue(in StrokePoint sp) => Type switch
    {
        SensorType.Pressure => sp.Pressure,
        SensorType.Speed => sp.Speed,
        SensorType.Distance => Math.Clamp(sp.TotalDistance / Math.Max(1f, Length), 0, 1),
        SensorType.Fade => Math.Clamp(sp.DabSeqNo / Math.Max(1f, Length), 0, 1),
        SensorType.Random => sp.Random,
        SensorType.StrokeRandom => sp.StrokeRandom,
        SensorType.DrawingAngle => ((sp.DrawingAngle / MathF.Tau) % 1f + 1f) % 1f,
        SensorType.TiltX => Math.Clamp((sp.TiltX + 90f) / 180f, 0, 1),
        SensorType.TiltY => Math.Clamp((sp.TiltY + 90f) / 180f, 0, 1),
        SensorType.Tilt => CombinedTilt01(sp),
        SensorType.Rotation => Math.Clamp((sp.Twist + 180f) / 360f, 0, 1),
        _ => 0f
    };

    private static float CombinedTilt01(in StrokePoint sp)
    {
        var nx = Math.Clamp((sp.TiltX + 90f) / 180f, 0, 1);
        var ny = Math.Clamp((sp.TiltY + 90f) / 180f, 0, 1);
        return Math.Clamp(MathF.Max(nx, ny), 0, 1);
    }

    public float CurvedValue(in StrokePoint sp) => Curve.Evaluate(RawValue(sp));

    public SensorConfig Clone() => new() { Type = Type, Curve = Curve.Clone(), Length = Length };
}
