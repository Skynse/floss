using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Floss.App.Brushes;

public sealed partial class BrushDynamics
{
    public CurveOption Size     { get; set; } = CurveOption.Off();
    public CurveOption Opacity  { get; set; } = CurveOption.Off();
    public CurveOption Flow     { get; set; } = CurveOption.Off();
    public CurveOption Hardness { get; set; } = CurveOption.Off();
    public CurveOption Scatter  { get; set; } = CurveOption.Off();
    public CurveOption Rotation { get; set; } = CurveOption.Off();
    public CurveOption Spacing  { get; set; } = CurveOption.Off();

    // ── Evaluation ───────────────────────────────────────────────────────────

    public float EvalSize    (in StrokePoint sp) => Size.Compute(sp);
    public float EvalOpacity (in StrokePoint sp) => Opacity.Compute(sp);
    public float EvalFlow    (in StrokePoint sp) => Flow.Compute(sp);
    public float EvalHardness(in StrokePoint sp) => Hardness.Compute(sp);
    public float EvalScatter (in StrokePoint sp) => Scatter.Compute(sp);
    // Rotation: returns degrees offset = (Compute - 0.5) * 360 * strength
    public float EvalRotationDeg(in StrokePoint sp)
    {
        if (!Rotation.IsEnabled || Rotation.Sensors.Count == 0) return 0f;
        return (Rotation.Compute(sp) - 0.5f) * 360f;
    }
    public float EvalSpacing(in StrokePoint sp) => Spacing.Compute(sp);

    public BrushDynamics Clone() => new()
    {
        Size     = Size.Clone(),
        Opacity  = Opacity.Clone(),
        Flow     = Flow.Clone(),
        Hardness = Hardness.Clone(),
        Scatter  = Scatter.Clone(),
        Rotation = Rotation.Clone(),
        Spacing  = Spacing.Clone()
    };

    // ── Serialization ────────────────────────────────────────────────────────

    public string Serialize() => JsonSerializer.Serialize(ToDto(), DtoContext.Default.BrushDynamicsDto);

    public static BrushDynamics Deserialize(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize(json, DtoContext.Default.BrushDynamicsDto);
            if (dto == null) return new BrushDynamics();
            return new BrushDynamics
            {
                Size     = FromDto(dto.Size),
                Opacity  = FromDto(dto.Opacity),
                Flow     = FromDto(dto.Flow),
                Hardness = FromDto(dto.Hardness),
                Scatter  = FromDto(dto.Scatter),
                Rotation = FromDto(dto.Rotation),
                Spacing  = FromDto(dto.Spacing)
            };
        }
        catch { return new BrushDynamics(); }
    }

    // ── Legacy migration ─────────────────────────────────────────────────────

    public static BrushDynamics FromLegacy(ParameterDynamics sizeDyn, ParameterDynamics opacDyn)
    {
        var d = new BrushDynamics();
        d.Size    = BuildLegacyCurveOption(sizeDyn);
        d.Opacity = BuildLegacyCurveOption(opacDyn);
        return d;
    }

    private static CurveOption BuildLegacyCurveOption(ParameterDynamics dyn)
    {
        bool hasPressure = dyn.PressureEnabled;
        bool hasVelocity = dyn.VelocityEnabled;
        if (!hasPressure && !hasVelocity) return CurveOption.Off();

        var opt = ToCurveOption(dyn);
        return opt;
    }

    internal static CurveOption ToCurveOption(ParameterDynamics dyn)
    {
        bool hasPressure = dyn.PressureEnabled;
        bool hasVelocity = dyn.VelocityEnabled;

        var opt = new CurveOption { MinOutput = dyn.Min, MaxOutput = dyn.Max };
        if (hasPressure)
        {
            CubicCurve curve;
            if (dyn.Kind == ResponseCurveKind.Bezier)
                curve = BezierCurve(dyn.X1, dyn.Y1, dyn.X2, dyn.Y2);
            else
                curve = GammaCurve(dyn.Kind == ResponseCurveKind.Linear ? 1f : dyn.Gamma);
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Pressure, Curve = curve });
        }
        if (hasVelocity)
        {
            var vc = new CubicCurve();
            vc.SetPoints([new(0f, 1f), new(1f, Math.Clamp(1f - dyn.VelocityStrength, 0.05f, 1f))]);
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Speed, Curve = vc });
        }
        return opt;
    }

    internal static ParameterDynamics ToParameterDynamics(CurveOption option)
    {
        SensorConfig? pressure = null;
        SensorConfig? speed = null;
        foreach (var sensor in option.Sensors)
        {
            if (sensor.Type == SensorType.Pressure && pressure == null)
                pressure = sensor;
            else if (sensor.Type == SensorType.Speed && speed == null)
                speed = sensor;
        }

        return new ParameterDynamics
        {
            PressureEnabled = option.IsEnabled && pressure != null,
            Kind = ResponseCurveKind.Power,
            Gamma = pressure == null ? 1.25f : EstimateGamma(pressure.Curve),
            Min = option.MinOutput,
            Max = option.MaxOutput,
            VelocityEnabled = option.IsEnabled && speed != null,
            VelocityStrength = speed == null ? 0.3f : Math.Clamp(1f - speed.Curve.Evaluate(1f), 0f, 1f)
        };
    }

    private static float EstimateGamma(CubicCurve curve)
    {
        var y = Math.Clamp(curve.Evaluate(0.5f), 0.001f, 1f);
        return Math.Clamp(MathF.Log(y) / MathF.Log(0.5f), 0.1f, 4f);
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

    private static CubicCurve BezierCurve(float x1, float y1, float x2, float y2)
    {
        const int steps = 9;
        var pts = new CurvePoint[steps];
        for (int i = 0; i < steps; i++)
        {
            float t = i / (float)(steps - 1);
            float bx = CubicBez(t, 0, x1, x2, 1);
            float by = Math.Clamp(CubicBez(t, 0, y1, y2, 1), 0, 1);
            pts[i] = new CurvePoint(bx, by);
        }
        var c = new CubicCurve();
        c.SetPoints(pts);
        return c;
    }

    private static float CubicBez(float t, float p0, float p1, float p2, float p3)
    {
        float inv = 1 - t;
        return inv * inv * inv * p0 + 3 * inv * inv * t * p1 + 3 * inv * t * t * p2 + t * t * t * p3;
    }

    // ── DTO layer ─────────────────────────────────────────────────────────────

    private BrushDynamicsDto ToDto() => new()
    {
        Size     = ToDto(Size),
        Opacity  = ToDto(Opacity),
        Flow     = ToDto(Flow),
        Hardness = ToDto(Hardness),
        Scatter  = ToDto(Scatter),
        Rotation = ToDto(Rotation),
        Spacing  = ToDto(Spacing)
    };

    private static CurveOptionDto ToDto(CurveOption opt) => new()
    {
        Enabled  = opt.IsEnabled,
        Strength = opt.Strength,
        Min      = opt.MinOutput,
        Max      = opt.MaxOutput,
        Mode     = (int)opt.CombineMode,
        Sensors  = ToDto(opt.Sensors)
    };

    private static List<SensorDto> ToDto(List<SensorConfig> sensors)
    {
        var list = new List<SensorDto>(sensors.Count);
        foreach (var s in sensors)
            list.Add(new SensorDto { T = (int)s.Type, C = s.Curve.Serialize(), L = s.Length });
        return list;
    }

    private static CurveOption FromDto(CurveOptionDto? dto)
    {
        if (dto == null) return CurveOption.Off();
        var opt = new CurveOption
        {
            IsEnabled   = dto.Enabled,
            Strength    = dto.Strength,
            MinOutput   = dto.Min,
            MaxOutput   = dto.Max,
            CombineMode = (SensorCombineMode)dto.Mode
        };
        if (dto.Sensors != null)
            foreach (var s in dto.Sensors)
                opt.Sensors.Add(new SensorConfig
                {
                    Type   = (SensorType)s.T,
                    Curve  = CubicCurve.Deserialize(s.C ?? ""),
                    Length = s.L
                });
        return opt;
    }

    // ── JSON DTO classes ──────────────────────────────────────────────────────

    internal sealed class BrushDynamicsDto
    {
        public CurveOptionDto? Size     { get; set; }
        public CurveOptionDto? Opacity  { get; set; }
        public CurveOptionDto? Flow     { get; set; }
        public CurveOptionDto? Hardness { get; set; }
        public CurveOptionDto? Scatter  { get; set; }
        public CurveOptionDto? Rotation { get; set; }
        public CurveOptionDto? Spacing  { get; set; }
    }

    internal sealed class CurveOptionDto
    {
        public bool             Enabled  { get; set; }
        public float            Strength { get; set; }
        public float            Min      { get; set; }
        public float            Max      { get; set; }
        public int              Mode     { get; set; }
        public List<SensorDto>? Sensors  { get; set; }
    }

    internal sealed class SensorDto
    {
        public int    T { get; set; }    // SensorType
        public string? C { get; set; }   // CubicCurve serialized
        public float   L { get; set; }   // Length
    }

    [JsonSerializable(typeof(BrushDynamicsDto))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    private sealed partial class DtoContext : JsonSerializerContext { }
}
