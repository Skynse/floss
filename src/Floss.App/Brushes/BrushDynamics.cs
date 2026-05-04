using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Floss.App.Brushes;

public sealed partial class BrushDynamics
{
    public CurveOption Size { get; set; } = CurveOption.Off();
    public CurveOption Opacity { get; set; } = CurveOption.Off();
    public CurveOption Flow { get; set; } = CurveOption.Off();
    public CurveOption Hardness { get; set; } = CurveOption.Off();
    public CurveOption Scatter { get; set; } = CurveOption.Off();
    public CurveOption Rotation { get; set; } = CurveOption.Off();
    public CurveOption Spacing { get; set; } = CurveOption.Off();

    public enum AngleSource { None, DirectionOfLine, PenTilt, PenTwist }

    // ── Evaluation ───────────────────────────────────────────────────────────

    public float EvalSize(in StrokePoint sp) => Size.Compute(sp);
    public float EvalOpacity(in StrokePoint sp) => Opacity.Compute(sp);
    public float EvalFlow(in StrokePoint sp) => Flow.Compute(sp);
    public float EvalHardness(in StrokePoint sp) => Hardness.Compute(sp);
    public float EvalScatter(in StrokePoint sp) => Scatter.Compute(sp);
    // Rotation: returns degrees offset = (Compute - 0.5) * 360 * strength
    public float EvalRotationDeg(in StrokePoint sp)
    {
        if (!Rotation.IsEnabled || Rotation.Sensors.Count == 0) return 0f;
        return (Rotation.Compute(sp) - 0.5f) * 360f;
    }
    public float EvalSpacing(in StrokePoint sp) => Spacing.Compute(sp);

    public BrushDynamics Clone() => new()
    {
        Size = Size.Clone(),
        Opacity = Opacity.Clone(),
        Flow = Flow.Clone(),
        Hardness = Hardness.Clone(),
        Scatter = Scatter.Clone(),
        Rotation = Rotation.Clone(),
        Spacing = Spacing.Clone()
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
                Size = FromDto(dto.Size),
                Opacity = FromDto(dto.Opacity),
                Flow = FromDto(dto.Flow),
                Hardness = FromDto(dto.Hardness),
                Scatter = FromDto(dto.Scatter),
                Rotation = FromDto(dto.Rotation),
                Spacing = FromDto(dto.Spacing)
            };
        }
        catch { return new BrushDynamics(); }
    }

    // ── Legacy migration ─────────────────────────────────────────────────────

    public static BrushDynamics FromLegacy(ParameterDynamics sizeDyn, ParameterDynamics opacDyn)
    {
        var d = new BrushDynamics();
        d.Size = BuildLegacyCurveOption(sizeDyn);
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
            var pts = new List<CurvePoint>();
            for (var i = 0; i < dyn.CurveData.Length; i += 2)
                pts.Add(new CurvePoint(
                    Math.Clamp(dyn.CurveData[i], 0, 1),
                    Math.Clamp(dyn.CurveData[i + 1], 0, 1)));
            var curve = new CubicCurve();
            curve.SetPoints([.. pts]);
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

        var curveData = new List<float> { 0f, 0f, 1f, 1f };
        if (pressure != null && pressure.Curve.Points.Count >= 2)
        {
            curveData.Clear();
            foreach (var p in pressure.Curve.Points)
            {
                curveData.Add(p.X);
                curveData.Add(p.Y);
            }
        }

        return new ParameterDynamics
        {
            PressureEnabled = option.IsEnabled && pressure != null,
            CurveData = [.. curveData],
            Min = option.MinOutput,
            Max = option.MaxOutput,
            VelocityEnabled = option.IsEnabled && speed != null,
            VelocityStrength = speed == null ? 0.3f : Math.Clamp(1f - speed.Curve.Evaluate(1f), 0f, 1f)
        };
    }

    // ── DTO layer ─────────────────────────────────────────────────────────────

    private BrushDynamicsDto ToDto() => new()
    {
        Size = ToDto(Size),
        Opacity = ToDto(Opacity),
        Flow = ToDto(Flow),
        Hardness = ToDto(Hardness),
        Scatter = ToDto(Scatter),
        Rotation = ToDto(Rotation),
        Spacing = ToDto(Spacing)
    };

    private static CurveOptionDto ToDto(CurveOption opt) => new()
    {
        Enabled = opt.IsEnabled,
        Strength = opt.Strength,
        Min = opt.MinOutput,
        Max = opt.MaxOutput,
        Mode = (int)opt.CombineMode,
        Sensors = ToDto(opt.Sensors)
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
            IsEnabled = dto.Enabled,
            Strength = dto.Strength,
            MinOutput = dto.Min,
            MaxOutput = dto.Max,
            CombineMode = (SensorCombineMode)dto.Mode
        };
        if (dto.Sensors != null)
            foreach (var s in dto.Sensors)
                opt.Sensors.Add(new SensorConfig
                {
                    Type = (SensorType)s.T,
                    Curve = CubicCurve.Deserialize(s.C ?? ""),
                    Length = s.L
                });
        return opt;
    }

    // ── JSON DTO classes ──────────────────────────────────────────────────────

    internal sealed class BrushDynamicsDto
    {
        public CurveOptionDto? Size { get; set; }
        public CurveOptionDto? Opacity { get; set; }
        public CurveOptionDto? Flow { get; set; }
        public CurveOptionDto? Hardness { get; set; }
        public CurveOptionDto? Scatter { get; set; }
        public CurveOptionDto? Rotation { get; set; }
        public CurveOptionDto? Spacing { get; set; }
    }

    internal sealed class CurveOptionDto
    {
        public bool Enabled { get; set; }
        public float Strength { get; set; }
        public float Min { get; set; }
        public float Max { get; set; }
        public int Mode { get; set; }
        public List<SensorDto>? Sensors { get; set; }
    }

    internal sealed class SensorDto
    {
        public int T { get; set; }    // SensorType
        public string? C { get; set; }   // CubicCurve serialized
        public float L { get; set; }   // Length
    }

    [JsonSerializable(typeof(BrushDynamicsDto))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    private sealed partial class DtoContext : JsonSerializerContext { }
}
