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
    public CurveOption TipDensity { get; set; } = CurveOption.Off();
    public CurveOption TipThickness { get; set; } = CurveOption.Off();

    // pressure-dynamic dynamics
    public CurveOption OffsetBySpeed { get; set; } = CurveOption.Off();
    public CurveOption OffsetByRandom { get; set; } = CurveOption.Off();
    public CurveOption EllipticalDabRatio { get; set; } = CurveOption.Off();
    public CurveOption EllipticalDabAngle { get; set; } = CurveOption.Off();
    public CurveOption CustomInput { get; set; } = CurveOption.Off();

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
    public float EvalTipDensity(in StrokePoint sp) => TipDensity.Compute(sp);
    public float EvalTipThickness(in StrokePoint sp) => TipThickness.Compute(sp);

    public BrushDynamics Clone() => new()
    {
        Size = Size.Clone(),
        Opacity = Opacity.Clone(),
        Flow = Flow.Clone(),
        Hardness = Hardness.Clone(),
        Scatter = Scatter.Clone(),
        Rotation = Rotation.Clone(),
        Spacing = Spacing.Clone(),
        TipDensity = TipDensity.Clone(),
        TipThickness = TipThickness.Clone(),
        OffsetBySpeed = OffsetBySpeed.Clone(),
        OffsetByRandom = OffsetByRandom.Clone(),
        EllipticalDabRatio = EllipticalDabRatio.Clone(),
        EllipticalDabAngle = EllipticalDabAngle.Clone(),
        CustomInput = CustomInput.Clone()
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
                Spacing = FromDto(dto.Spacing),
                TipDensity = FromDto(dto.TipDensity),
                TipThickness = FromDto(dto.TipThickness),
                OffsetBySpeed = FromDto(dto.OffsetBySpeed),
                OffsetByRandom = FromDto(dto.OffsetByRandom),
                EllipticalDabRatio = FromDto(dto.EllipticalDabRatio),
                EllipticalDabAngle = FromDto(dto.EllipticalDabAngle),
                CustomInput = FromDto(dto.CustomInput)
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
        var opt = new CurveOption { MinOutput = dyn.Min, MaxOutput = dyn.Max };
        if (dyn.PressureEnabled)
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Pressure, Curve = CurveFromData(dyn.CurveData) });
        if (dyn.VelocityEnabled)
        {
            var velocityData = dyn.VelocityCurveData is { Length: >= 4 }
                ? dyn.VelocityCurveData
                : ParameterDynamics.VelocityCurveFromStrength(dyn.VelocityStrength);
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Speed, Curve = CurveFromData(velocityData) });
        }
        if (dyn.TiltEnabled)
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Tilt, Curve = CurveFromData(dyn.TiltCurveData) });
        if (dyn.RandomEnabled)
            opt.Sensors.Add(new SensorConfig { Type = SensorType.Random, Curve = CurveFromData(dyn.RandomCurveData) });
        if (dyn.DistanceEnabled)
        {
            opt.Sensors.Add(new SensorConfig
            {
                Type = SensorType.Distance,
                Length = Math.Max(1f, dyn.DistanceLength),
                Curve = CurveFromData(dyn.DistanceCurveData)
            });
        }
        if (dyn.FadeEnabled)
        {
            opt.Sensors.Add(new SensorConfig
            {
                Type = SensorType.Fade,
                Length = Math.Max(1f, dyn.FadeLength),
                Curve = CurveFromData(dyn.FadeCurveData)
            });
        }
        return opt;
    }

    internal static ParameterDynamics ToParameterDynamics(CurveOption option)
    {
        var pressure = FindSensor(option, SensorType.Pressure);
        var speed = FindSensor(option, SensorType.Speed);
        var tilt = FindSensor(option, SensorType.Tilt) ?? FindSensor(option, SensorType.TiltX);
        var random = FindSensor(option, SensorType.Random);
        var distance = FindSensor(option, SensorType.Distance);
        var fade = FindSensor(option, SensorType.Fade);

        var curveData = pressure != null && pressure.Curve.Points.Count >= 2
            ? DataFromCurve(pressure.Curve)
            : new List<float> { 0f, 0f, 1f, 1f };

        var velocityCurveData = speed == null
            ? [.. ParameterDynamics.DefaultVelocityCurveData]
            : DataFromCurve(speed.Curve);

        return new ParameterDynamics
        {
            PressureEnabled = option.IsEnabled && pressure != null,
            CurveData = [.. curveData],
            Min = option.MinOutput,
            Max = option.MaxOutput,
            VelocityEnabled = option.IsEnabled && speed != null,
            VelocityCurveData = [.. velocityCurveData],
            VelocityStrength = speed == null
                ? 0.3f
                : Math.Clamp(1f - speed.Curve.Evaluate(1f), 0f, 1f),
            TiltEnabled = option.IsEnabled && tilt != null,
            TiltCurveData = tilt == null ? [.. ParameterDynamics.IdentityCurve] : [.. DataFromCurve(tilt.Curve)],
            RandomEnabled = option.IsEnabled && random != null,
            RandomCurveData = random == null ? [.. ParameterDynamics.IdentityCurve] : [.. DataFromCurve(random.Curve)],
            DistanceEnabled = option.IsEnabled && distance != null,
            DistanceLength = distance?.Length ?? 1000f,
            DistanceCurveData = distance == null
                ? [.. ParameterDynamics.DefaultDistanceCurveData]
                : [.. DataFromCurve(distance.Curve)],
            FadeEnabled = option.IsEnabled && fade != null,
            FadeLength = fade?.Length ?? 120f,
            FadeCurveData = fade == null
                ? [.. ParameterDynamics.DefaultFadeCurveData]
                : [.. DataFromCurve(fade.Curve)]
        };
    }

    private static SensorConfig? FindSensor(CurveOption option, SensorType type)
    {
        foreach (var sensor in option.Sensors)
        {
            if (sensor.Type == type)
                return sensor;
        }

        return null;
    }

    private static CubicCurve CurveFromData(float[] data)
    {
        var pts = new List<CurvePoint>();
        for (var i = 0; i < data.Length; i += 2)
            pts.Add(new CurvePoint(
                Math.Clamp(data[i], 0, 1),
                Math.Clamp(data[i + 1], 0, 1)));
        var curve = new CubicCurve();
        curve.SetPoints([.. pts]);
        return curve;
    }

    private static List<float> DataFromCurve(CubicCurve curve)
    {
        if (curve.Points.Count < 2)
            return [0f, 0f, 1f, 1f];

        var curveData = new List<float>(curve.Points.Count * 2);
        foreach (var p in curve.Points)
        {
            curveData.Add(p.X);
            curveData.Add(p.Y);
        }

        return curveData;
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
        Spacing = ToDto(Spacing),
        TipDensity = ToDto(TipDensity),
        TipThickness = ToDto(TipThickness),
        OffsetBySpeed = ToDto(OffsetBySpeed),
        OffsetByRandom = ToDto(OffsetByRandom),
        EllipticalDabRatio = ToDto(EllipticalDabRatio),
        EllipticalDabAngle = ToDto(EllipticalDabAngle),
        CustomInput = ToDto(CustomInput)
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
        public CurveOptionDto? TipDensity { get; set; }
        public CurveOptionDto? TipThickness { get; set; }
        public CurveOptionDto? OffsetBySpeed { get; set; }
        public CurveOptionDto? OffsetByRandom { get; set; }
        public CurveOptionDto? EllipticalDabRatio { get; set; }
        public CurveOptionDto? EllipticalDabAngle { get; set; }
        public CurveOptionDto? CustomInput { get; set; }
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
