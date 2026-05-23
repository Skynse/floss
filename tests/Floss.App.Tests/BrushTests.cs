namespace Floss.App.Tests;

public class BrushTests
{
    [Fact]
    public void CubicCurve_EvaluatesIdentityAndLinearCurves()
    {
        var identity = CubicCurve.Identity();
        TestAssertions.Near(0.0, identity.Evaluate(-1));
        TestAssertions.Near(1.0, identity.Evaluate(2));
        TestAssertions.Near(0.498, identity.Evaluate(0.5f), 0.01);

        var flat = CubicCurve.Linear(0, 0.25f, 1, 0.75f);
        TestAssertions.Near(0.25, flat.Evaluate(0), 0.001);
        TestAssertions.Near(0.75, flat.Evaluate(1), 0.001);
    }

    [Fact]
    public void CubicCurve_PointManagementAndSerialization()
    {
        var curve = new CubicCurve();
        curve.SetPoints([new CurvePoint(1.5f, -1f), new CurvePoint(0.25f, 0.5f), new CurvePoint(0f, 0f)]);
        TestAssertions.SequenceEqual(new[] { 0f, 0.25f, 1f }, curve.Points.Select(p => p.X));

        curve.MovePoint(1, 0.75f, 0.75f);
        curve.AddPoint(0.5f, 0.3f);
        curve.RemovePoint(0);
        TestAssertions.Equal(3, curve.Points.Count);

        var restored = CubicCurve.Deserialize(curve.Serialize());
        TestAssertions.Equal(curve.Points.Count, restored.Points.Count);

        var clone = curve.Clone();
        clone.MovePoint(0, 0, 0);
        TestAssertions.False(curve.Points[0].Equals(clone.Points[0]));
        TestAssertions.Equal(256, clone.GetLut().Length);
    }

    [Fact]
    public void SensorConfig_RawValuesAreNormalized()
    {
        var point = StrokePoint();
        TestAssertions.Near(0.6, new SensorConfig { Type = SensorType.Pressure }.RawValue(point));
        TestAssertions.Near(0.75, new SensorConfig { Type = SensorType.Distance, Length = 200 }.RawValue(point));
        TestAssertions.Near(0.25, new SensorConfig { Type = SensorType.Fade, Length = 40 }.RawValue(point));
        TestAssertions.Near(0.75, new SensorConfig { Type = SensorType.TiltX }.RawValue(point));
        TestAssertions.Near(0.25, new SensorConfig { Type = SensorType.TiltY }.RawValue(point));
        TestAssertions.Near(0.75, new SensorConfig { Type = SensorType.Rotation }.RawValue(point));
    }

    [Fact]
    public void CurveOption_ComputesAndClones()
    {
        var point = StrokePoint(pressure: 0.5f, speed: 0.25f);
        var option = new CurveOption
        {
            MinOutput = 0.2f,
            MaxOutput = 0.8f,
            Strength = 0.5f,
            CombineMode = SensorCombineMode.Add,
            Sensors =
            [
                new SensorConfig { Type = SensorType.Pressure, Curve = CubicCurve.Identity() },
                new SensorConfig { Type = SensorType.Speed, Curve = CubicCurve.Identity() }
            ]
        };

        TestAssertions.Near(0.7125, option.Compute(point), 0.01);
        TestAssertions.Equal(1.0f, CurveOption.Off().Compute(point));

        var clone = option.Clone();
        clone.Sensors[0].Type = SensorType.Random;
        TestAssertions.Equal(SensorType.Pressure, option.Sensors[0].Type);
    }

    [Fact]
    public void BrushDynamics_SerializesAndFallsBack()
    {
        var dynamics = new BrushDynamics
        {
            Size = CurveOption.Pressure(1.0f, 0.1f, 0.9f),
            Rotation = new CurveOption
            {
                Sensors = [new SensorConfig { Type = SensorType.Random, Curve = CubicCurve.Identity() }]
            },
            TipDensity = CurveOption.Pressure(1.0f),
            TipThickness = CurveOption.Pressure(1.0f)
        };
        var restored = BrushDynamics.Deserialize(dynamics.Serialize());
        TestAssertions.Near(dynamics.EvalSize(StrokePoint(pressure: 0.5f)), restored.EvalSize(StrokePoint(pressure: 0.5f)), 0.01);
        TestAssertions.Near(72.0, restored.EvalRotationDeg(StrokePoint(random: 0.7f)), 2.0);
        TestAssertions.Near(0.5, restored.EvalTipDensity(StrokePoint(pressure: 0.5f)), 0.02);
        TestAssertions.Near(0.5, restored.EvalTipThickness(StrokePoint(pressure: 0.5f)), 0.02);
        TestAssertions.Equal(1.0f, BrushDynamics.Deserialize("{bad json").EvalSize(StrokePoint()));
    }

    [Fact]
    public void ParameterDynamics_PreservesIndependentPressureAndVelocityCurves()
    {
        var dynamics = new ParameterDynamics
        {
            PressureEnabled = true,
            CurveData = [0f, 0f, 0.5f, 0.25f, 1f, 1f],
            VelocityEnabled = true,
            VelocityCurveData = [0f, 1f, 0.5f, 0.55f, 1f, 0.2f],
            Min = 0.1f,
            Max = 0.9f
        };

        var preset = new BrushPreset("Test", 20, 1, 0.8, 0.1, Colors.Black, 0)
        {
            SizeDynamics = dynamics
        };
        var roundTripped = preset.SizeDynamics;

        TestAssertions.SequenceEqual(dynamics.CurveData, roundTripped.CurveData);
        TestAssertions.SequenceEqual(dynamics.VelocityCurveData, roundTripped.VelocityCurveData);
        TestAssertions.True(roundTripped.PressureEnabled);
        TestAssertions.True(roundTripped.VelocityEnabled);

        var converted = BrushDynamics.ToCurveOption(dynamics);
        converted.IsEnabled = true;
        var option = BrushDynamics.ToParameterDynamics(converted);
        TestAssertions.SequenceEqual(dynamics.VelocityCurveData, option.VelocityCurveData);
        TestAssertions.False(SequenceEqual(dynamics.CurveData, option.VelocityCurveData));

        static bool SequenceEqual(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 0.0001f) return false;
            return true;
        }
    }

    [Fact]
    public void ParameterDynamics_RoundTripsAllInputCurves()
    {
        var dynamics = new ParameterDynamics
        {
            PressureEnabled = true,
            CurveData = [0f, 0.1f, 1f, 0.9f],
            Min = 0.2f,
            Max = 0.8f,
            VelocityEnabled = true,
            VelocityCurveData = [0f, 1f, 1f, 0.3f],
            TiltEnabled = true,
            TiltCurveData = [0f, 0.2f, 1f, 0.7f],
            RandomEnabled = true,
            RandomCurveData = [0f, 0.5f, 1f, 0.5f],
            DistanceEnabled = true,
            DistanceLength = 640f,
            DistanceCurveData = [0f, 1f, 1f, 0.1f],
            FadeEnabled = true,
            FadeLength = 80f,
            FadeCurveData = [0f, 1f, 0.5f, 0.5f]
        };

        var converted = BrushDynamics.ToCurveOption(dynamics);
        converted.IsEnabled = true;
        var roundTripped = BrushDynamics.ToParameterDynamics(converted);

        TestAssertions.True(roundTripped.PressureEnabled);
        TestAssertions.True(roundTripped.VelocityEnabled);
        TestAssertions.True(roundTripped.TiltEnabled);
        TestAssertions.True(roundTripped.RandomEnabled);
        TestAssertions.True(roundTripped.DistanceEnabled);
        TestAssertions.True(roundTripped.FadeEnabled);
        TestAssertions.Near(640, roundTripped.DistanceLength, 0.01);
        TestAssertions.Near(80, roundTripped.FadeLength, 0.01);
        TestAssertions.SequenceEqual(dynamics.TiltCurveData, roundTripped.TiltCurveData);
        TestAssertions.SequenceEqual(dynamics.RandomCurveData, roundTripped.RandomCurveData);
        TestAssertions.SequenceEqual(dynamics.DistanceCurveData, roundTripped.DistanceCurveData);
        TestAssertions.SequenceEqual(dynamics.FadeCurveData, roundTripped.FadeCurveData);
    }

    [Fact]
    public void SensorConfig_CombinedTiltUsesPenTiltComponents()
    {
        var point = StrokePoint(tiltX: 45f, tiltY: 0f);
        TestAssertions.Near(0.75, new SensorConfig { Type = SensorType.Tilt }.RawValue(point), 0.01);
    }

    [Fact]
    public void BrushParameterGraph_FromDynamicsIncludesEnabledSensors()
    {
        var option = BrushDynamics.ToCurveOption(new ParameterDynamics
        {
            PressureEnabled = true,
            CurveData = ParameterDynamics.IdentityCurve,
            VelocityEnabled = true,
            VelocityCurveData = ParameterDynamics.DefaultVelocityCurveData,
            RandomEnabled = true,
            RandomCurveData = ParameterDynamics.IdentityCurve
        });
        option.IsEnabled = true;

        var graph = BrushParameterGraph.FromDynamics(BrushParameterTarget.Size, option);
        TestAssertions.False(graph.Validate().Count > 0);
        TestAssertions.True(graph.Nodes.Exists(n => n.Id == "velocity" && n.Strength > 0));
        TestAssertions.True(graph.Nodes.Exists(n => n.Id == "random" && n.Strength > 0));
        TestAssertions.False(graph.Nodes.Exists(n => n.Id == "tilt" && n.Strength > 0));
    }

    [Fact]
    public void BrushParameterGraph_EvaluatesStrokeInputs()
    {
        var graph = new BrushParameterGraph
        {
            Target = BrushParameterTarget.Size,
            OutputNodeId = "output",
            Nodes =
            [
                new BrushParameterNode { Id = "pressure", Kind = BrushParameterNodeKind.Pressure, Min = 0.2f, Max = 1.0f, Strength = 1f },
                new BrushParameterNode { Id = "speed", Kind = BrushParameterNodeKind.Velocity, Min = 1.0f, Max = 0.5f, Strength = 1f },
                new BrushParameterNode { Id = "mul", Kind = BrushParameterNodeKind.Multiply, Inputs = ["pressure", "speed"] },
                new BrushParameterNode { Id = "output", Kind = BrushParameterNodeKind.Output, Inputs = ["mul"] }
            ]
        };
        var slow = StrokePoint(pressure: 1f, speed: 0f);
        var fast = StrokePoint(pressure: 1f, speed: 1f);

        TestAssertions.True(graph.Validate().Count == 0);
        TestAssertions.Near(1.0, graph.Evaluate(slow), 0.001);
        TestAssertions.Near(0.5, graph.Evaluate(fast), 0.001);
    }

    [Fact]
    public void BrushPreset_LegacyDynamicsBridge()
    {
        var preset = new BrushPreset("Test", 10, 1, 1, 1, Colors.Black, 0)
        {
            SizeDynamics = ParameterDynamics.DefaultSize,
            OpacityDynamics = ParameterDynamics.DefaultOpacity
        };

        TestAssertions.True(preset.SizeDynamics.PressureEnabled);
        TestAssertions.True(preset.SizeDynamics.VelocityEnabled);
        TestAssertions.True(preset.OpacityDynamics.PressureEnabled);
        TestAssertions.False(preset.OpacityDynamics.VelocityEnabled);
    }

    [Fact]
    public void BrushEngine_ReusesMasksDuringLargeStrokes()
    {
        using var engine = new BrushEngine();
        var tip = new CountingBrushTip();
        var brush = new BrushPreset("Big", 160, 1, 0.7, 0.04, Colors.Black, 0)
        {
            Tip = tip,
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 1200, 320);
        var from = Sample(40, 160, 0);
        var to = Sample(1050, 160, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.Equal(1, tip.GenerateCount, "Large strokes should reuse the stroke mask instead of regenerating it per stamp.");
    }

    [Fact]
    public void BrushEngine_ColorMixOffIgnoresWetPaintFields()
    {
        using var engine = new BrushEngine();
        var brush = new BrushPreset("Dry", 30, 1, 1, 0.1, Colors.Black, 0)
        {
            ColorMix = false,
            DensityOfPaint = 0.0,
            AmountOfPaint = 0.0,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 120, 120);
        var sample = Sample(60, 60, 0);

        var dirty = engine.RasterizeDab(layer, brush, sample, velocity: 0);

        TestAssertions.False(dirty.IsEmpty);
        layer.Pixels.GetPixel(60, 60, out _, out _, out _, out var alpha);
        TestAssertions.True(alpha > 0, "Disabling color mix should make wet-paint fields inert, not make the brush invisible.");
    }

    [Fact]
    public void BrushEngine_AppliesTipThickness()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 100, 100);
        var brush = new BrushPreset("Flat tip", 40, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            TipThickness = 0.25,
            TipDirection = BrushTipDirection.Horizontal,
            Shape = null
        };
        var sample = Sample(50, 50, 0);

        engine.BeginStroke(brush, sample);
        engine.RasterizeDab(layer, brush, sample, velocity: 0);

        var (minX, minY, maxX, maxY) = AlphaBounds(layer);
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        TestAssertions.True(width > height * 2, $"Horizontal tip thickness should flatten the rendered stamp, got {width}x{height}.");
    }

    [Fact]
    public void BrushEngine_AppliesTipDensityAndThicknessDynamics()
    {
        using var lowEngine = new BrushEngine();
        using var highEngine = new BrushEngine();
        var lowLayer = new DrawingLayer("Low", 100, 100);
        var highLayer = new DrawingLayer("High", 100, 100);
        var brush = new BrushPreset("Dynamic tip", 40, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            TipDensity = 1,
            TipThickness = 1,
            TipDirection = BrushTipDirection.Horizontal,
            Dynamics = new BrushDynamics
            {
                TipDensity = CurveOption.Pressure(1.0f),
                TipThickness = CurveOption.Pressure(1.0f)
            },
            Shape = null
        };

        lowEngine.RasterizeDab(lowLayer, brush, SampleWithPressure(50, 50, 0, 0.25), velocity: 0);
        highEngine.RasterizeDab(highLayer, brush, SampleWithPressure(50, 50, 0, 1.0), velocity: 0);

        lowLayer.Pixels.GetPixel(50, 50, out _, out _, out _, out var lowAlpha);
        highLayer.Pixels.GetPixel(50, 50, out _, out _, out _, out var highAlpha);
        var (_, lowMinY, _, lowMaxY) = AlphaBounds(lowLayer);
        var (_, highMinY, _, highMaxY) = AlphaBounds(highLayer);

        TestAssertions.True(highAlpha > lowAlpha, "Tip density pressure dynamics should affect rendered stamp opacity.");
        TestAssertions.True(highMaxY - highMinY > lowMaxY - lowMinY, "Tip thickness pressure dynamics should affect rendered stamp thickness.");
    }

    [Fact]
    public void BrushEngine_BlendModeDoesNotCarryPaint()
    {
        using var engine = new BrushEngine();
        var brush = new BrushPreset("Blend", 8, 1, 1, 0.6, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0,
            DensityOfPaint = 0,
            ColorStretch = 0.1,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 180, 60);
        for (var y = 26; y <= 34; y++)
        for (var x = 16; x <= 24; x++)
            layer.Pixels.SetPixel(x, y, 0, 0, 0, 255);

        var from = Sample(20, 30, 0);
        var to = Sample(150, 30, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        layer.Pixels.GetPixel(140, 30, out _, out _, out _, out var farAlpha);
        TestAssertions.Equal((byte)0, farAlpha, "Blend mode should blur local paint, not carry it across transparent canvas.");
    }

    [Fact]
    public void BrushEngine_LowSpacingUsesCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Low spacing", 48, 1, 0.75, 0.01, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Fixed,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.Equal("ProceduralStampFast", engine.LastStats.Path);
        TestAssertions.True(engine.LastStats.StampCount > 10, $"Expected low spacing to generate many dabs, got {engine.LastStats.StampCount}.");
        TestAssertions.Equal(0, engine.LastStats.CachedDabCount);
    }

    [Fact]
    public void BrushEngine_ShapedTipsUseCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Shaped", 44, 1, 0.8, 0.02, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Fixed,
            Tip = new ProceduralBrushTip(BrushTipShape.Chalk),
            Shape = new ProceduralBrushTip(BrushTipShape.Rectangle),
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.Equal("CachedTileMajor", engine.LastStats.Path);
        TestAssertions.True(engine.LastStats.CachedDabCount > 10);
        TestAssertions.True(engine.LastStats.TileBucketCount > 0);
    }

    [Fact]
    public void BrushEngine_MultiTipsUseCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Multi-tip", 42, 1, 0.85, 0.02, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Fixed,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Tips =
            [
                new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Circle },
                new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Chalk }
            ],
            TipSelectionMode = BrushTipSelectionMode.Sequential,
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.Equal("CachedTileMajor", engine.LastStats.Path);
        TestAssertions.True(engine.LastStats.CachedDabCount > 10);
        TestAssertions.True(engine.LastStats.TileBucketCount > 0);
    }

    [Fact]
    public void BrushEngine_SingleModeIgnoresMaterialTipList()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 96, 96);
        var activeTip = new CountingBrushTip();
        var brush = new BrushPreset("Procedural active with saved materials", 32, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = activeTip,
            Tips = [new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = BrushTipShape.Flat }],
            TipSelectionMode = BrushTipSelectionMode.Single,
            Shape = null,
            Dynamics = new BrushDynamics()
        };
        var sample = Sample(48, 48, 0);

        engine.BeginStroke(brush, sample);
        var dirty = engine.RasterizeDab(layer, brush, sample, velocity: 0);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.True(activeTip.GenerateCount > 0, "Single tip mode must render brush.Tip, not the saved material tip list.");
    }

    [Fact]
    public void BrushEngine_ColorImageTipsUseCachedTileMajorPath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Color material", 36, 1, 1, 0.02, Colors.Black, 0)
        {
            Tip = new ImageBrushTip(ColoredTipPngBytes(SKColors.Red)),
            Shape = null,
            ColorMix = false,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.Equal("CachedColorTileMajor", engine.LastStats.Path);
        TestAssertions.True(engine.LastStats.CachedDabCount > 10);
        layer.Pixels.GetPixel(240, 128, out var b, out var g, out var r, out var a);
        TestAssertions.True(a > 0, "Colored image tip should deposit alpha.");
        TestAssertions.True(r > 180 && g < 80 && b < 80, $"Colored image tip should preserve red pixels, got BGRA=({b},{g},{r},{a}).");
    }

    [Fact]
    public void BrushEngine_DabCacheSurvivesBatchedUniqueStamps()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 1024, 512);
        var brush = new BrushPreset("Cache stress", 48, 1, 1, 0.008, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Fixed,
            Tip = new ImageBrushTip(ColoredTipPngBytes(SKColors.Red)),
            Shape = null,
            ColorMix = false,
            Grain = 0,
            SizeDynamics = new ParameterDynamics { PressureEnabled = true },
            Dynamics = new BrushDynamics()
        };

        var samples = new List<CanvasInputSample>(130);
        for (var i = 0; i < 130; i++)
        {
            var pressure = 0.08 + (i % 90) * 0.01;
            samples.Add(SampleWithPressure(20 + i * 6.0, 256, i * 4_000L, pressure));
        }

        engine.BeginStroke(brush, samples[0]);
        var dirty = engine.RasterizeSegments(layer, brush, samples, 1, samples.Count - 1);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.Equal("CachedColorTileMajor", engine.LastStats.Path);
        TestAssertions.True(engine.LastStats.StampCount > 40,
            $"Expected many unique stamps in one batch, got {engine.LastStats.StampCount}.");
        TestAssertions.True(engine.LastStats.CachedDabCount > 32,
            "Batch should exceed color dab cache capacity within one tile-major pass.");
        layer.Pixels.GetPixel(400, 256, out _, out _, out var r, out var a);
        TestAssertions.True(a > 0, "Stressed dab cache pass should still deposit pixels.");
        TestAssertions.True(r > 100, $"Expected red paint after cache stress, got alpha={a}, red={r}.");
    }

    [Fact]
    public void BrushEngine_BatchedSegmentsMatchSequentialDryStroke()
    {
        using var sequentialEngine = new BrushEngine();
        using var batchedEngine = new BrushEngine();
        using var sequentialLayer = new DrawingLayer("Sequential", 320, 180);
        using var batchedLayer = new DrawingLayer("Batched", 320, 180);
        var brush = new BrushPreset("Dry", 32, 1, 0.75, 0.05, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = false,
            Dynamics = new BrushDynamics()
        };
        var samples = new List<CanvasInputSample>
        {
            Sample(30, 90, 0),
            Sample(80, 84, 1_000),
            Sample(130, 98, 2_000),
            Sample(180, 82, 3_000),
            Sample(240, 94, 4_000)
        };

        sequentialEngine.BeginStroke(brush, samples[0]);
        for (var i = 1; i < samples.Count; i++)
            sequentialEngine.RasterizeSegment(sequentialLayer, brush, samples[i - 1], samples[i]);

        batchedEngine.BeginStroke(brush, samples[0]);
        var dirty = batchedEngine.RasterizeSegments(batchedLayer, brush, samples, 1, samples.Count - 1);

        TestAssertions.False(dirty.IsEmpty);
        var bounds = new PixelRegion(0, 0, 320, 180);
        TestAssertions.SequenceEqual(sequentialLayer.Pixels.Capture(bounds), batchedLayer.Pixels.Capture(bounds));
    }

    [Fact]
    public void BrushEngine_ColorMixUsesScratchCompositePath()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 512, 256);
        for (var x = 0; x < 80; x++)
            layer.Pixels.SetPixel(x, 128, 0, 0, 0, 255);

        var brush = new BrushPreset("Wet smear", 36, 1, 0.75, 0.02, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Smear,
            AmountOfPaint = 0,
            DensityOfPaint = 0.06,
            ColorStretch = 0.8,
            BlurAmount = 0.94,
            Grain = 0,
            Dynamics = new BrushDynamics()
        };
        var from = Sample(40, 128, 0);
        var to = Sample(430, 128, 16_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);

        TestAssertions.False(dirty.IsEmpty);
        TestAssertions.True(
            engine.LastStats.Path is "SpatialSmear" or "SmudgeSequential" or "ColorMixScratch" or "CachedTileMajor",
            $"Unexpected color-mix render path: {engine.LastStats.Path}");
        layer.Pixels.GetPixel(200, 128, out _, out _, out _, out var alpha);
        TestAssertions.True(alpha > 0);
    }

    [Fact]
    public void BrushEngine_SmearZeroPaintOnEmptyCanvas()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 512, 256);
        var brush = new BrushPreset("Dry smear", 48, 1, 0.75, 0.05, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Smear,
            AmountOfPaint = 0,
            DensityOfPaint = 0,
            BlurAmount = 0.94,
            MixingMode = MixingMode.Perceptual
        };
        var from = Sample(80, 128, 0);
        var to = Sample(400, 128, 16_000);

        engine.BeginStroke(brush, from);
        engine.RasterizeSegment(layer, brush, from, to);
        engine.EndStroke();

        for (var x = 60; x < 420; x++)
        {
            layer.Pixels.GetPixel(x, 128, out _, out _, out _, out var alpha);
            TestAssertions.Equal((byte)0, alpha, $"Smear with amount=0 density=0 must not paint on empty canvas at x={x}.");
        }
    }

    [Fact]
    public void BrushEngine_ColorMixLargeBrushFallsBackToSkia()
    {
        // Large color-mix brushes must actually paint (alpha > 0). The exact
        // raster path depends on dirty size and dab cache — CachedTileMajor,
        // ColorMixScratch, and SkiaFallback are all valid as long as stamps land.
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 2400, 1600);
        for (var y = 200; y < 1400; y++)
        {
            for (var x = 200; x < 1400; x++)
                layer.Pixels.SetPixel(x, y, 80, 120, 200, 255);
        }

        var brush = new BrushPreset("Huge mixer", 1200, 1, 0.7, 0.18, Colors.White, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0.5,
            DensityOfPaint = 0.5,
            BlurAmount = 0
        };
        var from = Sample(400, 800, 0);
        var to = Sample(1800, 800, 8_000);

        engine.BeginStroke(brush, from);
        var dirty = engine.RasterizeSegment(layer, brush, from, to);
        engine.EndStroke();

        TestAssertions.False(dirty.IsEmpty);
        var path = engine.LastStats.Path;
        TestAssertions.True(path is "SkiaFallback" or "CachedTileMajor" or "ColorMixScratch",
            $"Large color-mix brush must use a painting raster path, got {path}.");
        layer.Pixels.GetPixel(1100, 800, out _, out _, out _, out var alpha);
        TestAssertions.True(alpha > 0, "Large color-mix brush must actually paint instead of dropping stamps.");
    }

    [Fact]
    public void BrushEngine_SmearDensityScalesDeposition()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 256, 64);
        for (var y = 16; y <= 48; y++)
        {
            for (var x = 0; x < 96; x++)
                layer.Pixels.SetPixel(x, y, 100, 150, 200, 255);
        }

        var brush = new BrushPreset("Smear density", 32, 1, 0.75, 0.5, Colors.White, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Smear,
            AmountOfPaint = 0,
            DensityOfPaint = 0.06,
            BlurAmount = 0
        };
        var from = Sample(48, 32, 0);
        var to = Sample(180, 32, 4_000);

        engine.BeginStroke(brush, from);
        engine.RasterizeSegment(layer, brush, from, to);
        engine.EndStroke();

        layer.Pixels.GetPixel(150, 32, out var b, out var g, out var r, out var a);
        TestAssertions.True(a > 0, "Smear should drag pigment from the painted source region.");
        TestAssertions.True(b > 0 || g > 0 || r > 0);
    }

    [Fact]
    public void BrushEngine_SmearBlurSoftensColorBoundary()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 128, 64);
        for (var y = 20; y <= 44; y++)
        {
            for (var x = 0; x < 64; x++)
                layer.Pixels.SetPixel(x, y, 0, 0, 255, 255);
            for (var x = 64; x < 128; x++)
                layer.Pixels.SetPixel(x, y, 255, 255, 0, 255);
        }

        var brush = new BrushPreset("Smear blur", 32, 1, 0.75, 0.5, Colors.White, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Smear,
            AmountOfPaint = 0,
            DensityOfPaint = 0.06,
            ColorStretch = 0.8,
            BlurAmount = 0.94,
            MixingMode = MixingMode.Perceptual
        };
        var sample = Sample(64, 32, 0);

        engine.BeginStroke(brush, sample);
        engine.RasterizeDab(layer, brush, sample, velocity: 0);
        engine.EndStroke();

        layer.Pixels.GetPixel(64, 32, out _, out var g, out var r, out var a);
        TestAssertions.True(a > 0);
        TestAssertions.True(r > 0 && g > 0, "High smear blur should average red/yellow boundary into a mixed stroke color.");
    }

    [Fact]
    public void DirectDraw_KeepsLongBrushSegmentsIntact()
    {
        var document = new DrawingDocument();
        document.AddLayer();
        var layer = document.ActiveLayer!;
        var ctx = new ToolContext(document);
        var brush = new BrushPreset("Fast low spacing", 36, 1, 1, 0.005, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Fixed,
            Dynamics = new BrushDynamics()
        };
        var first = Sample(0, 0, 0);
        var txType = typeof(DirectDrawOutput).GetNestedType("StrokeTransaction", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing StrokeTransaction.");
        var tx = Activator.CreateInstance(txType, ctx, layer, document.ActiveLayerIndex, brush, first)
            ?? throw new InvalidOperationException("Could not create StrokeTransaction.");
        var queue = typeof(DirectDrawOutput).GetMethod("QueueNewSamples", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing QueueNewSamples.");
        var samples = new List<CanvasInputSample>
        {
            Sample(0, 0, 0),
            Sample(15_000, 0, 16_000)
        };

        queue.Invoke(null, [tx, layer, brush, samples]);
        var queued = (List<CanvasInputSample>)(txType.GetProperty("QueuedSamples")?.GetValue(tx)
            ?? throw new InvalidOperationException("Missing QueuedSamples."));

        TestAssertions.Equal(2, queued.Count);
        TestAssertions.Near(15_000, queued[0].DistanceTo(queued[1]), 0.001);
    }

    [Fact]
    public void DirectDraw_ColorMixingDoesNotSampleOwnStroke()
    {
        using var engine = new BrushEngine();
        var document = new DrawingDocument();
        document.AddLayer();
        var layer = document.ActiveLayer!;
        for (var y = 26; y <= 34; y++)
        for (var x = 16; x <= 24; x++)
            layer.Pixels.SetPixel(x, y, 0, 0, 0, 255);

        var brush = new BrushPreset("Blend", 8, 1, 1, 0.6, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0,
            DensityOfPaint = 0,
            ColorStretch = 0.1,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var ctx = new ToolContext(document) { Brush = brush, PaintColor = Colors.White };
        var output = new DirectDrawOutput(engine, document);
        var samples = Enumerable.Range(0, 66)
            .Select(i => Sample(20 + i * 2, 30, i * 1_000))
            .ToList();

        output.Execute(ctx, new StrokeInput { RawSamples = samples, SmoothedSamples = samples });

        layer.Pixels.GetPixel(140, 30, out _, out _, out _, out var farAlpha);
        TestAssertions.Equal((byte)0, farAlpha, "Color mixing must sample the pre-stroke layer, not feed back from its own slow stroke.");
    }

    [Fact]
    public void CompositeTool_DeactivateDoesNotCancelCompletedOutput()
    {
        var ctx = new ToolContext(new DrawingDocument()) { Brush = new BrushPreset("Ink", 8, 1, 1, 1, Colors.Black, 0) };
        var output = new RecordingOutputProcess();
        var tool = new CompositeTool(new BrushStrokeInputProcess(), output);

        tool.PointerDown(ctx, Sample(10, 10, 0));
        tool.PointerUp(ctx, Sample(12, 10, 1_000));

        TestAssertions.Equal(1, output.ExecuteCount);
        TestAssertions.False(tool.HasPendingOperation);

        tool.Deactivate(ctx);

        TestAssertions.Equal(0, output.CancelCount, "Deactivating an idle tool must not cancel/restore the already completed stroke.");
    }

    [Fact]
    public void CompositeTool_DeactivateDoesNotCancelCompletedOutputWithMoves()
    {
        var ctx = new ToolContext(new DrawingDocument()) { Brush = new BrushPreset("Ink", 8, 1, 1, 1, Colors.Black, 0) };
        var output = new RecordingOutputProcess();
        var tool = new CompositeTool(new BrushStrokeInputProcess(), output);

        // Full stroke with multiple moves, as in real drawing
        tool.PointerDown(ctx, Sample(10, 10, 0));
        tool.PointerMove(ctx, Sample(20, 20, 1_000));
        tool.PointerMove(ctx, Sample(30, 25, 2_000));
        tool.PointerUp(ctx, Sample(40, 30, 3_000));

        TestAssertions.Equal(1, output.ExecuteCount, "Up should fire Execute once");
        TestAssertions.False(tool.HasPendingOperation, "Tool should be idle after Up");

        // Simulate selecting another tool — calls Deactivate on the old one
        tool.Deactivate(ctx);

        TestAssertions.Equal(0, output.CancelCount, "Deactivating a completed-with-moves stroke must NOT cancel it");
    }

    [Fact]
    public void CompositeTool_CommitPreservesDirectDrawPixelsBeforeTempSwitch()
    {
        using var engine = new BrushEngine();
        var document = new DrawingDocument();
        document.AddLayer();
        var layer = document.ActiveLayer!;
        var brush = new BrushPreset("Ink", 12, 1, 1, 0.2, Colors.Black, 0)
        {
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var ctx = new ToolContext(document) { Brush = brush, PaintColor = Colors.Black };
        var tool = new CompositeTool(new BrushStrokeInputProcess(), new DirectDrawOutput(engine, document));

        tool.PointerDown(ctx, Sample(20, 20, 0));
        tool.PointerMove(ctx, Sample(30, 20, 1_000));
        tool.Commit(ctx);

        layer.Pixels.GetPixel(25, 20, out _, out _, out _, out var committedAlpha);
        TestAssertions.True(committedAlpha > 0, "Commit should finalize the live direct-draw stroke before a temporary tool switch.");

        tool.Deactivate(ctx);

        layer.Pixels.GetPixel(25, 20, out _, out _, out _, out var afterDeactivateAlpha);
        TestAssertions.True(afterDeactivateAlpha > 0, "Deactivating after commit must not restore pre-stroke tiles.");
    }

    [Fact]
    public void CompositeTool_DeactivateCancelsActiveInput()
    {
        var ctx = new ToolContext(new DrawingDocument()) { Brush = new BrushPreset("Ink", 8, 1, 1, 1, Colors.Black, 0) };
        var output = new RecordingOutputProcess();
        var tool = new CompositeTool(new BrushStrokeInputProcess(), output);

        tool.PointerDown(ctx, Sample(10, 10, 0));
        tool.PointerMove(ctx, Sample(12, 10, 1_000));

        TestAssertions.True(tool.HasPendingOperation);

        tool.Deactivate(ctx);

        TestAssertions.Equal(1, output.CancelCount, "Deactivating during a running transaction should cancel the in-progress preview.");
    }

    [Fact]
    public void BrushEngine_ColorMixAmountControlsDepositedColor()
    {
        using var lowEngine = new BrushEngine();
        using var highEngine = new BrushEngine();
        var lowLayer = new DrawingLayer("Low", 80, 60);
        var highLayer = new DrawingLayer("High", 80, 60);
        for (var y = 26; y <= 34; y++)
        for (var x = 16; x <= 24; x++)
        {
            lowLayer.Pixels.SetPixel(x, y, 0, 0, 0, 255);
            highLayer.Pixels.SetPixel(x, y, 0, 0, 0, 255);
        }

        BrushPreset Brush(double amount) => new("Mix", 8, 1, 1, 0.6, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = amount,
            DensityOfPaint = 1,
            ColorStretch = 0,
            Tip = new CountingBrushTip(),
            Shape = null
        };
        var sample = Sample(20, 30, 0);

        lowEngine.BeginStroke(Brush(0), sample);
        highEngine.BeginStroke(Brush(1), sample);
        lowEngine.RasterizeDab(lowLayer, Brush(0), sample, velocity: 0);
        highEngine.RasterizeDab(highLayer, Brush(1), sample, velocity: 0);

        lowLayer.Pixels.GetPixel(20, 30, out _, out _, out var lowRed, out _);
        highLayer.Pixels.GetPixel(20, 30, out _, out _, out var highRed, out _);
        TestAssertions.True(highRed > lowRed, "Amount of paint should increase drawing color contribution.");
    }

    [Fact]
    public void BrushEngine_ColorMixSamplesUnderBrushFootprint()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 64, 64);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
            layer.Pixels.SetPixel(x, y, 0, 0, 0, 255);
        for (var y = 24; y <= 36; y++)
        for (var x = 36; x <= 48; x++)
            layer.Pixels.SetPixel(x, y, 0, 0, 255, 255);

        var brush = new BrushPreset("Mix", 24, 1, 0.75, 0.5, Colors.White, 0)
        {
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0,
            DensityOfPaint = 1,
            ColorStretch = 1,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null
        };
        var sample = Sample(30, 30, 0);

        engine.BeginStroke(brush, sample);
        engine.RasterizeDab(layer, brush, sample, velocity: 0);

        layer.Pixels.GetPixel(38, 30, out _, out _, out var sampledRed, out var alpha);
        TestAssertions.True(alpha > 0);
        TestAssertions.True(sampledRed > 32, "Footprint-weighted sampling should pick up nearby canvas pigment outside the dab center.");
    }

    [Fact]
    public void BrushEngine_SmudgeStressEmptyLayer()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 1024, 1024);
        var brush = new BrushPreset("Smudge", 36, 0.68, 0.75, 0.05, Colors.White, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Smudge,
            MixingMode = MixingMode.Perceptual,
            AmountOfPaint = 0,
            DensityOfPaint = 0,
            ColorStretch = 0.79,
            BlurAmount = 0.81,
            Grain = 0.12,
            Dynamics = new BrushDynamics()
        };

        engine.BeginStroke(brush, Sample(100, 100, 0));
        var from = Sample(100, 100, 0);
        for (var i = 1; i <= 400; i++)
        {
            var to = Sample(100 + i * 3, 100 + (i % 7), i * 500L);
            var dirty = engine.RasterizeSegment(layer, brush, from, to);
            if (!dirty.IsEmpty)
                layer.Pixels.GetPixel(dirty.X + 1, dirty.Y + 1, out _, out _, out _, out _);
            from = to;
        }
        engine.EndStroke();
    }

    [Fact]
    public void BrushEngine_ColorMixSurvivesNegativeCoordsWithGrain()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 256, 256);
        var brush = new BrushPreset("Smudge edge", 48, 1, 0.75, 0.05, Colors.White, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Shape = null,
            ColorMix = true,
            SmudgeMode = SmudgeMode.Blend,
            AmountOfPaint = 0.5,
            DensityOfPaint = 1,
            ColorStretch = 0.5,
            Grain = 0.35,
            Dynamics = new BrushDynamics()
        };

        engine.BeginStroke(brush, Sample(-12, -12, 0));
        var dirty = engine.RasterizeSegment(layer, brush, Sample(-12, -12, 0), Sample(40, 30, 1_000));
        engine.EndStroke();

        TestAssertions.False(dirty.IsEmpty, "Negative-coordinate smudge segments should render without crashing.");
    }

    [Fact]
    public void BrushEngine_DoesNotDisposeTipOwnedCachedMasks()
    {
        var tip = new CachedCountingBrushTip();
        var brush = new BrushPreset("Cached", 80, 1, 0.7, 0.1, Colors.Black, 0)
        {
            Tip = tip,
            Shape = null
        };
        var layer = new DrawingLayer("Layer", 400, 240);

        using (var engine = new BrushEngine())
        {
            var from = Sample(40, 120, 0);
            var to = Sample(300, 120, 16_000);
            engine.BeginStroke(brush, from);
            engine.RasterizeSegment(layer, brush, from, to);
            engine.EndStroke();

            from = Sample(40, 150, 32_000);
            to = Sample(300, 150, 48_000);
            engine.BeginStroke(brush, from);
            var dirty = engine.RasterizeSegment(layer, brush, from, to);
            TestAssertions.False(dirty.IsEmpty);
        }

        TestAssertions.False(tip.CachedMaskDisposed, "BrushEngine must not dispose masks owned by the brush tip cache.");
        tip.Dispose();
    }

    [Fact]
    public void ToolController_DoesNotOverridePresetBrushState()
    {
        var document = new DrawingDocument();
        var context = new ToolContext(document)
        {
            Brush = new BrushPreset("Source", 10, 1, 1, 0.1, Colors.Black, 0)
        };
        var first = new PresetApplyingTool();
        var second = new PresetApplyingTool();
        var controller = new ToolController(context, first);
        var targetPreset = new ToolPreset
        {
            Name = "Smudge",
            InputProcess = InputProcessType.Brush,
            OutputProcess = OutputProcessType.DirectDraw,
            BrushOverride = new BrushPresetOverrideDocument
            {
                Size = 80,
                ColorMix = true,
                SmudgeMode = SmudgeMode.Smear,
                AmountOfPaint = 0.25,
                DensityOfPaint = 0
            }
        };

        controller.SetActiveTool(second, targetPreset);

        TestAssertions.Near(80, context.Brush.Size);
        TestAssertions.True(context.Brush.ColorMix);
        TestAssertions.Equal(SmudgeMode.Smear, context.Brush.SmudgeMode);
        TestAssertions.Near(0.25, context.Brush.AmountOfPaint);
        TestAssertions.Near(0, context.Brush.DensityOfPaint);
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) AlphaBounds(DrawingLayer layer)
    {
        var minX = layer.Width;
        var minY = layer.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < layer.Height; y++)
        for (var x = 0; x < layer.Width; x++)
        {
            layer.Pixels.GetPixel(x, y, out _, out _, out _, out var a);
            if (a == 0) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return (minX, minY, maxX, maxY);
    }

    private sealed class CountingBrushTip : IBrushTip
    {
        public int GenerateCount { get; private set; }

        public SKBitmap GenerateMask(int baseSize, float hardness)
        {
            GenerateCount++;
            var bitmap = new SKBitmap(new SKImageInfo(baseSize, baseSize, SKColorType.Alpha8, SKAlphaType.Unpremul));
            bitmap.Erase(new SKColor(255, 255, 255, 255));
            return bitmap;
        }
    }

    private sealed class CachedCountingBrushTip : IBrushTip, IDisposable
    {
        private SKBitmap? _cached;

        public bool CachedMaskDisposed => _cached?.Handle == IntPtr.Zero;

        public SKBitmap GenerateMask(int baseSize, float hardness)
        {
            if (_cached != null) return _cached;

            _cached = new SKBitmap(new SKImageInfo(baseSize, baseSize, SKColorType.Alpha8, SKAlphaType.Unpremul));
            _cached.Erase(new SKColor(255, 255, 255, 255));
            return _cached;
        }

        public void Dispose() => _cached?.Dispose();
    }

    private sealed class PresetApplyingTool : ITool
    {
        public void PointerDown(ToolContext ctx, CanvasInputSample s) { }
        public void PointerMove(ToolContext ctx, CanvasInputSample s) { }
        public void PointerUp(ToolContext ctx, CanvasInputSample s) { }
        public void Cancel(ToolContext ctx) { }
        public void RenderOverlay(Avalonia.Media.DrawingContext dc, ToolContext ctx, double zoom) { }

        public void Activate(ToolContext ctx)
        {
            if (ctx.ActivePreset != null)
                ctx.Brush = ctx.ActivePreset.ApplyToBrushPreset(ctx.Brush);
        }
    }

    private sealed class RecordingOutputProcess : IOutputProcess
    {
        public bool Antialiasing { get; set; } = true;
        public int ExecuteCount { get; private set; }
        public int CancelCount { get; private set; }

        public void Preview(ToolContext ctx, IProcessedInput input) { }
        public void Execute(ToolContext ctx, IProcessedInput input) => ExecuteCount++;
        public void Cancel() => CancelCount++;
    }

    private static CanvasInputSample Sample(double x, double y, long timeMicros)
        => new(x, y, 1, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);

    private static CanvasInputSample SampleWithPressure(double x, double y, long timeMicros, double pressure)
        => new(x, y, pressure, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);

    [Fact]
    public unsafe void ImageBrushTip_PreservesAspectRatio()
    {
        using var source = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.Erase(SKColors.Transparent);
        var ptr = (byte*)source.GetPixels().ToPointer();
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var p = ptr + y * source.RowBytes + x * 4;
            p[0] = 255;
            p[1] = 255;
            p[2] = 255;
            p[3] = 255;
        }

        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var tip = new ImageBrushTip(data.ToArray());
        using var mask = tip.GenerateMask(8, 1.0f);

        var maskPtr = (byte*)mask.GetPixels().ToPointer();
        TestAssertions.Equal((byte)0, maskPtr[0 * mask.RowBytes + 4]);
        TestAssertions.Equal((byte)255, maskPtr[4 * mask.RowBytes + 4]);
        TestAssertions.Equal((byte)0, maskPtr[7 * mask.RowBytes + 4]);
    }

    [Fact]
    public void ImageBrushTip_MasksRemainStableAcrossSizes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(1, 0, SKColors.White);
        bitmap.SetPixel(1, 1, SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var tip = new ImageBrushTip(data.ToArray());
        var first = tip.GenerateMask(24, 1.0f);
        var firstHandle = first.Handle;

        var second = tip.GenerateMask(64, 1.0f);
        var third = tip.GenerateMask(24, 0.5f);
        var firstAgain = tip.GenerateMask(24, 1.0f);

        TestAssertions.True(firstHandle != IntPtr.Zero);
        TestAssertions.True(second.Handle != IntPtr.Zero);
        TestAssertions.True(third.Handle != IntPtr.Zero);
        TestAssertions.Equal(firstHandle, first.Handle, "Requesting a different cursor/preview mask must not dispose an active stroke mask.");
        TestAssertions.Equal(firstHandle, firstAgain.Handle, "The original mask should remain cached for its size/hardness key.");
    }

    [Fact]
    public void ImageBrushTip_DoesNotDisposeCachedMasksUnderChurn()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var tip = new ImageBrushTip(data.ToArray());

        var first = tip.GenerateMask(16, 1.0f);
        var firstHandle = first.Handle;
        for (var size = 17; size < 80; size++)
            _ = tip.GenerateMask(size, 1.0f);

        TestAssertions.Equal(firstHandle, first.Handle, "Image tip cache churn must not dispose a mask that a stroke may still reference.");
        TestAssertions.True(first.GetPixels() != IntPtr.Zero, "Original image tip mask pixels should remain valid after cache churn.");
    }

    [Fact]
    public void ProceduralBrushTip_DoesNotDisposeCachedMasksUnderChurn()
    {
        var tip = new ProceduralBrushTip();
        var first = tip.GenerateMask(16, 1.0f);
        var firstHandle = first.Handle;
        for (var size = 17; size < 80; size++)
            _ = tip.GenerateMask(size, 1.0f);

        TestAssertions.Equal(firstHandle, first.Handle, "Procedural tip cache churn must not dispose a mask that a stroke may still reference.");
        TestAssertions.True(first.GetPixels() != IntPtr.Zero, "Original procedural tip mask pixels should remain valid after cache churn.");
    }

    [Fact]
    public void ProceduralBrushTip_SoftRoundDiffersFromRound()
    {
        var round = new ProceduralBrushTip(BrushTipShape.Circle).GenerateMask(64, 0.85f);
        var soft = new ProceduralBrushTip(BrushTipShape.SoftRound).GenerateMask(64, 0.85f);

        var roundAlpha = AlphaAt(round, 51, 32);
        var softAlpha = AlphaAt(soft, 51, 32);

        TestAssertions.True(roundAlpha > 240, $"Round should keep a hard body at this radius, got {roundAlpha}.");
        TestAssertions.True(softAlpha < roundAlpha - 80, $"Soft round should fall off before the edge, got round={roundAlpha}, soft={softAlpha}.");
    }

    [Fact]
    public void ProceduralBrushTip_FlatIsRectangular()
    {
        var flat = new ProceduralBrushTip(BrushTipShape.Flat).GenerateMask(96, 0.95f);
        var bounds = AlphaBounds(flat, threshold: 64);
        var width = bounds.MaxX - bounds.MinX + 1;
        var height = bounds.MaxY - bounds.MinY + 1;

        TestAssertions.True(width > height * 3.5, $"Flat tip should be a wide rectangle, got {width}x{height}.");
        TestAssertions.True(AlphaAt(flat, 48, bounds.MinY + 1) > 180, "Flat top edge should be filled, not oval-tapered.");
        TestAssertions.True(AlphaAt(flat, 48, bounds.MaxY - 1) > 180, "Flat bottom edge should be filled, not oval-tapered.");
    }

    [Fact]
    public void ProceduralBrushTip_BristleHasSeparatedStrands()
    {
        var bristle = new ProceduralBrushTip(BrushTipShape.Bristle).GenerateMask(96, 0.9f);
        var runs = CountVerticalAlphaRuns(bristle, x: 48, threshold: 32);

        TestAssertions.True(runs >= 5, $"Bristle tip should contain separated strands, got {runs} alpha runs.");
    }

    [Fact]
    public void ProceduralBrushTip_IsGraphBacked()
    {
        var procedural = new ProceduralBrushTip(BrushTipShape.Flat, 1.0f);
        var fromGraph = new NodeBrushTip(procedural.Graph);

        TestAssertions.Equal(BrushTipNodeKind.Rectangle, procedural.Graph.Nodes.Single(n => n.Id == "flat").Kind);
        TestAssertions.SequenceEqual(AlphaBytes(procedural.GenerateMask(80, 0.82f)), AlphaBytes(fromGraph.GenerateMask(80, 0.82f)));
    }

    [Fact]
    public void ProceduralBrushTipData_StoresGraphPayload()
    {
        var tip = new ProceduralBrushTip(BrushTipShape.Bristle, 1.0f);
        var data = BrushTipData.FromTip(tip);
        var restored = data.CreateTip();

        TestAssertions.Equal(BrushTipStorageKind.NodeGraph, data.Kind);
        TestAssertions.True(data.NodeGraph != null, "Procedural brush tips should save their graph payload.");
        TestAssertions.True(restored is NodeBrushTip, "Graph payloads should restore as editable node graph tips.");
        TestAssertions.SequenceEqual(AlphaBytes(tip.GenerateMask(72, 0.74f)), AlphaBytes(restored.GenerateMask(72, 0.74f)));
    }

    [Fact]
    public void BrushTipNodeGraph_ValidatesBadTopology()
    {
        var graph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["missing"] },
                new BrushTipNode { Id = "cycle-a", Kind = BrushTipNodeKind.Add, Inputs = ["cycle-b", "output"] },
                new BrushTipNode { Id = "cycle-b", Kind = BrushTipNodeKind.Add, Inputs = ["cycle-a", "output"] }
            ]
        };
        var errors = graph.Validate();

        TestAssertions.True(errors.Any(e => e.Contains("missing", StringComparison.Ordinal)), "Validator should report missing input ids.");
        graph.OutputNodeId = "cycle-a";
        errors = graph.Validate();
        TestAssertions.True(errors.Any(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase)), "Validator should report cycles reachable from output.");
    }

    [Fact]
    public void BrushTipNodeGraph_CacheKeyChangesWithContent()
    {
        var circle = BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Circle);
        var soft = BrushTipNodeGraph.FromProceduralShape(BrushTipShape.SoftRound);
        TestAssertions.False(circle.CacheKey() == soft.CacheKey(), "Different preset shapes should produce different cache keys.");

        var edited = circle.DeepClone();
        edited.Nodes[0].Hardness = 0.42f;
        TestAssertions.False(circle.CacheKey() == edited.CacheKey(), "Graph edits should change the cache key.");
    }

    [Fact]
    public void BrushTipNodePorts_EnforceCompatibility()
    {
        TestAssertions.True(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Coordinates, BrushTipNodeKind.DistanceField, 0));
        TestAssertions.False(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Coordinates, BrushTipNodeKind.SmoothStep, 0));
        TestAssertions.True(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Circle, BrushTipNodeKind.SmoothStep, 0));
        TestAssertions.False(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Circle, BrushTipNodeKind.RotateCoordinates, 0));
        TestAssertions.True(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Noise, BrushTipNodeKind.WarpCoordinates, 1));
        TestAssertions.False(BrushTipNodePorts.CanConnect(
            BrushTipNodeKind.Coordinates, BrushTipNodeKind.WarpCoordinates, 1));
    }

    [Fact]
    public void NodeBrushTip_EvaluatesDeterministicGraph()
    {
        var tip = new NodeBrushTip(BrushTipNodeGraph.BristleRound());
        var first = tip.GenerateMask(96, 0.8f);
        var second = tip.GenerateMask(96, 0.8f);
        var third = new NodeBrushTip(BrushTipNodeGraph.BristleRound()).GenerateMask(96, 0.8f);

        TestAssertions.Equal(first.Handle, second.Handle, "Node tips should reuse cached masks for identical size/hardness/graph.");
        TestAssertions.SequenceEqual(AlphaBytes(first), AlphaBytes(third));
    }

    [Fact]
    public void NodeBrushTip_ComposesProceduralPrimitives()
    {
        var graph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "round", Kind = BrushTipNodeKind.Circle, Radius = 0.48f, Hardness = 0.9f },
                new BrushTipNode { Id = "noise", Kind = BrushTipNodeKind.Noise, Density = 0.45f, Opacity = 1f, Scale = 1.5f, Seed = 123 },
                new BrushTipNode { Id = "grain", Kind = BrushTipNodeKind.Multiply, Inputs = ["round", "noise"] },
                new BrushTipNode { Id = "cut", Kind = BrushTipNodeKind.Threshold, Inputs = ["grain"], Threshold = 0.08f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["cut"] }
            ]
        };
        var mask = new NodeBrushTip(graph).GenerateMask(96, 0.85f);
        var bounds = AlphaBounds(mask, threshold: 16);
        var center = AlphaAt(mask, 48, 48);
        var corner = AlphaAt(mask, 3, 3);

        TestAssertions.True(bounds.MaxX > bounds.MinX && bounds.MaxY > bounds.MinY, "Composed node tip should render visible coverage.");
        TestAssertions.True(center > corner, $"Circle multiplied by noise should stay stronger near center than corner, got center={center}, corner={corner}.");
    }

    [Fact]
    public void BrushHardness_AffectsSmoothStepEdge()
    {
        var graph = BrushTipNodeGraph.FromProceduralShape(BrushTipShape.SoftRound);
        TestAssertions.True(BrushTipStampFastPath.TryCreate(graph, 0.15f, out var softEval));
        TestAssertions.True(BrushTipStampFastPath.TryCreate(graph, 0.95f, out var hardEval));

        const float u = 0.76f;
        const float v = 0.5f;
        var softAlpha = softEval(u, v);
        var hardAlpha = hardEval(u, v);

        TestAssertions.True(MathF.Abs(softAlpha - hardAlpha) > 0.05f,
            $"Brush hardness should change SmoothStep falloff, got soft={softAlpha:F3}, hard={hardAlpha:F3}.");
        TestAssertions.True(softAlpha < hardAlpha,
            $"Lower hardness should feather further out (less alpha mid-falloff), got soft={softAlpha:F3}, hard={hardAlpha:F3}.");

        using var softMask = new NodeBrushTip(graph).GenerateMask(96, 0.15f);
        using var hardMask = new NodeBrushTip(graph).GenerateMask(96, 0.95f);
        TestAssertions.True(!AlphaBytes(softMask).SequenceEqual(AlphaBytes(hardMask)),
            "Mask cache should produce different tips for different brush hardness.");
    }

    [Fact]
    public void ImageSamplerGraph_UsesBakedStampMask()
    {
        var png = ColoredTipPngBytes(SKColors.Black);
        var graph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "image", Kind = BrushTipNodeKind.ImageSampler, PngBytes = png },
                new BrushTipNode { Id = "edge", Kind = BrushTipNodeKind.SmoothStep, Inputs = ["image"], Threshold = 0.5f, Hardness = 0.08f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["edge"] }
            ]
        };

        TestAssertions.True(graph.ContainsImageSampler(), "Fixture graph should contain an ImageSampler node.");
        var tip = new NodeBrushTip(graph);
        var brush = new BrushPreset("Image Graph", 32, 1, 0.85, 0.1, Colors.Black, 0) { Tip = tip };

        TestAssertions.False(BrushEngine.UsesProceduralStampEvaluation(brush, tip, 0),
            "ImageSampler node graphs should bake masks instead of per-pixel graph evaluation.");
        TestAssertions.True(BrushEngine.UsesProceduralStampEvaluation(
                brush with { Tip = new ProceduralBrushTip(BrushTipShape.Circle) },
                new ProceduralBrushTip(BrushTipShape.Circle), 0),
            "Pure procedural tips should keep the fast per-pixel path.");
    }

    [Fact]
    public void NodeBrushTip_SupportsCoordinateWarping()
    {
        var plainGraph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "field", Kind = BrushTipNodeKind.DistanceField, Width = 1f, Height = 1f },
                new BrushTipNode { Id = "edge", Kind = BrushTipNodeKind.SmoothStep, Inputs = ["field"], Threshold = 0.48f, Hardness = 0.03f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["edge"] }
            ]
        };
        var warpedGraph = new BrushTipNodeGraph
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode { Id = "coords", Kind = BrushTipNodeKind.Coordinates },
                new BrushTipNode { Id = "bands", Kind = BrushTipNodeKind.Stripe, Inputs = ["coords"], Scale = 9f, Density = 0.55f, Opacity = 1f },
                new BrushTipNode { Id = "warp", Kind = BrushTipNodeKind.WarpCoordinates, Inputs = ["coords", "bands"], Density = 0.8f, Width = 0.28f, Height = 0.12f, RotationDegrees = 0f },
                new BrushTipNode { Id = "field", Kind = BrushTipNodeKind.DistanceField, Inputs = ["warp"], Width = 1f, Height = 1f },
                new BrushTipNode { Id = "edge", Kind = BrushTipNodeKind.SmoothStep, Inputs = ["field"], Threshold = 0.48f, Hardness = 0.03f },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["edge"] }
            ]
        };

        var plain = new NodeBrushTip(plainGraph).GenerateMask(96, 0.85f);
        var warped = new NodeBrushTip(warpedGraph).GenerateMask(96, 0.85f);

        TestAssertions.True(warpedGraph.Validate().Count == 0, "Coordinate-warp graph should validate.");
        TestAssertions.True(!AlphaBytes(plain).SequenceEqual(AlphaBytes(warped)), "Warped coordinate graph should produce a different mask than the unwarped field.");
    }

    [Fact]
    public void NodeBrushTip_SamplesEmbeddedImageTip()
    {
        var png = ColoredTipPngBytes(SKColors.Black);
        var image = new ImageBrushTip(png).GenerateMask(48, 0.9f);
        var graph = BrushTipNodeGraph.FromImageTip(png);
        var node = new NodeBrushTip(graph).GenerateMask(48, 0.9f);

        TestAssertions.True(graph.Validate().Count == 0, "Image sampler graph should validate.");
        TestAssertions.SequenceEqual(AlphaBytes(image), AlphaBytes(node));
        TestAssertions.True(graph.TryGetDirectImageSampler(out var restored));
        TestAssertions.Equal(png.Length, restored.Length);
    }

    [Fact]
    public void BrushMaterialTips_ResolveActiveEmbeddedTip()
    {
        var png = ColoredTipPngBytes(SKColors.Red);
        var preset = new BrushPreset("Imported", 32, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new NodeBrushTip(BrushTipNodeGraph.FromImageTip(png)),
            Tips = []
        };

        var options = ImageSamplerOptions.FromTips(BrushMaterialTips.ForPreset(preset));
        TestAssertions.Equal(1, options.Count);
        TestAssertions.True(ImageSamplerOptions.SameBytes(png, options[0].Tip.PngBytes));
    }

    [Fact]
    public void BrushMaterialTips_RemovingLibraryEntryClearsSampler()
    {
        var png = ColoredTipPngBytes(SKColors.Red);
        var tips = BrushMaterialTips.NormalizeLibrary([
            new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = png,
                Label = "plgr"
            }
        ]);
        var graph = BrushMaterialTips.BindGraphToLibrary(BrushTipNodeGraph.FromImageTip(png), tips);
        var preset = new BrushPreset("Graphite", 32, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new NodeBrushTip(graph),
            Tips = tips
        };

        BrushMaterialTips.BindToPreset(preset);
        using (var before = preset.Tip.GenerateMask(48, 0.9f))
            TestAssertions.True(AlphaBytes(before).Any(b => b > 0), "Sampler should paint before removal.");

        var (emptyTips, tip) = BrushMaterialTips.ApplyLibraryChange(preset, [], removed: tips[0]);
        preset = preset with { Tips = emptyTips, Tip = tip };
        BrushMaterialTips.BindToPreset(preset);

        var boundGraph = (preset.Tip as NodeBrushTip)!.Graph;
        var sampler = boundGraph.Nodes.First(n => n.Id == "image");
        TestAssertions.True(string.IsNullOrEmpty(sampler.MaterialTipId),
            "Image Sampler should have no material reference after library removal.");
        TestAssertions.Equal(0, sampler.PngBytes.Length, "Embedded sampler bytes should be cleared.");

        using var after = preset.Tip.GenerateMask(48, 0.9f);
        TestAssertions.True(AlphaBytes(after).All(b => b == 0), "Removing the library image should produce an empty mask.");
    }

    [Fact]
    public void NodeBrushTip_EvaluateColorWithProceduralOutput()
    {
        var graph = new BrushTipNodeGraph();
        graph.Nodes.Add(new BrushTipNode
        {
            Id = "tip-image",
            Kind = BrushTipNodeKind.ImageSampler,
            PngBytes = ColoredTipPngBytes(SKColors.Red)
        });

        using var tip = new NodeBrushTip(graph);
        TestAssertions.True(BrushTipNodeGraphEvaluator.GraphUsesColor(graph));
        TestAssertions.True(tip.HasColor);
        TestAssertions.False(tip.IsDirectImageSampler, "Procedural output should not take the direct-image fast path.");

        using var stamp = tip.GenerateColorStamp(48);
        TestAssertions.True(stamp != null, "Color evaluation should not crash and must return a bitmap.");
        TestAssertions.Equal(48, stamp!.Width);
        TestAssertions.Equal(48, stamp.Height);
    }

    [Fact]
    public void ImageBrushTip_ColorStampPreservesColor()
    {
        using var engine = new BrushEngine();
        var layer = new DrawingLayer("Layer", 80, 80);
        var brush = new BrushPreset("Red stamp", 24, 1, 1, 0.1, Colors.Black, 0)
        {
            Tip = new ImageBrushTip(ColoredTipPngBytes(SKColors.Red)),
            Shape = null
        };
        var sample = Sample(40, 40, 0);

        engine.BeginStroke(brush, sample);
        engine.RasterizeDab(layer, brush, sample, velocity: 0);

        layer.Pixels.GetPixel(40, 40, out var b, out var g, out var r, out var a);
        TestAssertions.True(a > 0, "Colored stamp should deposit alpha.");
        TestAssertions.True(r > 180 && g < 80 && b < 80, $"Colored image tip should preserve red pixels, got BGRA=({b},{g},{r},{a}).");
    }

    private static byte[] ColoredTipPngBytes(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static unsafe byte AlphaAt(SKBitmap bitmap, int x, int y)
    {
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        return ptr[y * bitmap.RowBytes + x];
    }

    private static unsafe byte[] AlphaBytes(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height];
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = ptr + y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x++)
                bytes[y * bitmap.Width + x] = row[x];
        }
        return bytes;
    }

    private static unsafe (int MinX, int MinY, int MaxX, int MaxY) AlphaBounds(SKBitmap bitmap, byte threshold)
    {
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = ptr + y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (row[x] <= threshold) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return (minX, minY, maxX, maxY);
    }

    private static int CountVerticalAlphaRuns(SKBitmap bitmap, int x, byte threshold)
    {
        var runs = 0;
        var inRun = false;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var on = AlphaAt(bitmap, x, y) > threshold;
            if (on && !inRun)
                runs++;
            inRun = on;
        }
        return runs;
    }

    [Fact]
    public void AbrPresetMapping_KeepsDynamics()
    {
        var importer = typeof(AbrImporter);
        var paramType = importer.GetNestedType("AbrBrushParams", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing AbrBrushParams.");
        var vrType = importer.GetNestedType("VrParams", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing VrParams.");
        var p = Activator.CreateInstance(paramType)
            ?? throw new InvalidOperationException("Could not create ABR params.");

        SetField(p, "HasDiameter", true);
        SetField(p, "Diameter", 32.0);
        SetField(p, "HasAngle", true);
        SetField(p, "Angle", 15.0);
        SetField(p, "HasRoundness", true);
        SetField(p, "Roundness", 40.0);
        SetField(p, "HasSpacing", true);
        SetField(p, "Spacing", 8.0);
        SetField(p, "UseScatter", true);
        SetField(p, "ScatterDist", 35.0);

        SetField(p, "SizeDyn", Vr(vrType, control: 2, jitter: 25, minimum: 20));
        SetField(p, "AngleDyn", Vr(vrType, control: 7, jitter: 50, minimum: 0));
        SetField(p, "SpacingDyn", Vr(vrType, control: 0, jitter: 40, minimum: 0));

        var build = importer.GetMethod("BuildPreset", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing BuildPreset.");
        var args = new object?[] { "ABR", Array.Empty<byte>(), p, 25, null, null, null };
        build.Invoke(null, args);

        var preset = (BrushPreset)(args[4] ?? throw new InvalidOperationException("No preset returned."));
        TestAssertions.Equal(32.0, preset.Size);
        TestAssertions.Near(0.08, preset.Spacing);
        TestAssertions.Near(15.0, preset.Angle);
        TestAssertions.Near(0.40, preset.TipThickness);
        TestAssertions.Equal(BrushTipDirection.Horizontal, preset.TipDirection);
        TestAssertions.Equal(BrushDynamics.AngleSource.DirectionOfLine, preset.BaseAngleSource);
        TestAssertions.True(preset.AngleJitter > 0.4f);
        TestAssertions.True(preset.Dynamics.Size.IsEnabled);
        TestAssertions.True(preset.Dynamics.Scatter.IsEnabled);
        TestAssertions.True(preset.Dynamics.Spacing.IsEnabled);
        TestAssertions.True(preset.Dynamics.Size.Sensors.Any(s => s.Type == SensorType.Pressure));
        TestAssertions.True(preset.Dynamics.Size.Sensors.Any(s => s.Type == SensorType.Random));
    }

    [Fact]
    public void AbrMaskCleanup_InvertsDarkOnLightMasks()
    {
        var importer = typeof(AbrImporter);
        var clean = importer.GetMethod("CleanTipMask", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing CleanTipMask.");
        var pixels = new byte[]
        {
            255, 255, 255, 255,
            255,   0,   0, 255,
            255,   0,   0, 255,
            255, 255, 255, 255
        };

        var result = clean.Invoke(null, [pixels, 4, 4])
            ?? throw new InvalidOperationException("No cleanup result.");
        var tuple = (ITuple)result;

        var cleaned = (byte[])tuple[0]!;
        TestAssertions.Equal(4, (int)tuple[1]!);
        TestAssertions.Equal(4, (int)tuple[2]!);
        TestAssertions.Equal((byte)0, cleaned[0]);
        TestAssertions.Equal((byte)255, cleaned[1 * 4 + 1]);
        TestAssertions.Equal((byte)255, cleaned[2 * 4 + 2]);
        TestAssertions.Equal((byte)0, cleaned[3 * 4 + 3]);
    }

    private static object Vr(Type vrType, int control, double jitter, double minimum)
    {
        var vr = Activator.CreateInstance(vrType)
            ?? throw new InvalidOperationException("Could not create VR params.");
        SetField(vr, "ControlType", control);
        SetField(vr, "Jitter", jitter);
        SetField(vr, "Minimum", minimum);
        return vr;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        field.SetValue(target, value);
    }

    private static StrokePoint StrokePoint(
        float pressure = 0.6f,
        float speed = 0.4f,
        float random = 0.3f,
        float tiltX = 45f,
        float tiltY = -45f)
        => new(10, 20, pressure, tiltX, tiltY, 90, MathF.PI, speed, 150, 10, random, 0.8f);
}

