using System;
using System.Collections.Generic;
using Avalonia.Media;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes;

public enum MixingMode { Standard, Perceptual }
public enum SmudgeMode { Blend, Smear, Smudge }
public enum BrushTipDirection { Horizontal, Vertical }
public enum BrushQuality { Low, High }
public enum BrushTipSelectionMode { Single, Sequential, Random }

public sealed record BrushPreset(
    string Name,
    double Size,
    double Opacity,
    double Hardness,
    double Spacing,
    Color Color,
    double Angle)
{
    public BrushDynamics Dynamics { get; init; } = new();
    public double Flow { get; init; } = 1.0;
    public bool ColorMix { get; init; } = false;
    public double ColorLoad { get; init; } = 1.0;
    public double ColorStretch { get; init; } = 0.5;
    public double BlurAmount { get; init; } = 0.0;
    public SmudgeMode SmudgeMode { get; init; } = SmudgeMode.Blend;
    public MixingMode MixingMode { get; init; } = MixingMode.Standard;
    public double AmountOfPaint { get; init; } = 1.0;
    public double DensityOfPaint { get; init; } = 1.0;
    public double TipDensity { get; init; } = 1.0;
    public double TipThickness { get; init; } = 1.0;
    public BrushTipDirection TipDirection { get; init; } = BrushTipDirection.Horizontal;
    public double Grain { get; init; } = 0.0;
    public string? Texture { get; init; } = null;
    public double Smoothing { get; init; } = 0.3;
    /// <summary>Brush Studio ceiling for the size slider, as percent of the canvas-scaled default max (100–400).</summary>
    public double MaxSizePercent { get; init; } = BrushSizeLimits.DefaultMaxSizePercent;
    public bool AutoSpacingActive { get; init; } = false;
    public double AutoSpacingCoeff { get; init; } = 1.0;
    public double SpeedSpacingStrength { get; init; } = 0.0;
    public BrushGapMode GapMode { get; init; } = BrushGapMode.Normal;
    public bool ContinuousSpraying { get; init; } = false;
    public BrushQuality Quality { get; init; } = BrushQuality.High;
    public IBrushTip Tip { get; init; } = new ProceduralBrushTip();
    public SkiaSharp.SKBlendMode BlendMode { get; init; } = SkiaSharp.SKBlendMode.SrcOver;
    public ProceduralBrushTip? Shape { get; init; } = null;
    public AngleSource BaseAngleSource { get; init; } = AngleSource.None;
    public float AngleJitter { get; init; } = 0f;
    public bool FlipHorizontal { get; init; } = false;
    public bool FlipVertical { get; init; } = false;
    public IReadOnlyList<BrushTipData> Tips { get; init; } = [];
    public BrushTipSelectionMode TipSelectionMode { get; init; } = BrushTipSelectionMode.Single;
    public IReadOnlyList<BrushParameterGraph> ParameterGraphs { get; init; } = [];

    public ParameterDynamics SizeDynamics
    {
        get => BrushDynamics.ToParameterDynamics(Dynamics.Size);
        init
        {
            var dynamics = Dynamics.Clone();
            dynamics.Size = BrushDynamics.ToCurveOption(value);
            Dynamics = dynamics;
        }
    }

    public ParameterDynamics OpacityDynamics
    {
        get => BrushDynamics.ToParameterDynamics(Dynamics.Opacity);
        init
        {
            var dynamics = Dynamics.Clone();
            dynamics.Opacity = BrushDynamics.ToCurveOption(value);
            Dynamics = dynamics;
        }
    }

    public static IReadOnlyList<BrushPreset> Defaults { get; } =
    [
        // ── Pencils ──────────────────────────────────────────────────────────

        new("HB Pencil", 14, 0.72, 0.60, 0.18, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.35f),
                Opacity = CurveOption.Pressure(1.2f)
            },
            Flow = 0.82,
            Smoothing = 0.22,
            Grain = 0.28,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "mask",   Kind = BrushTipNodeKind.Circle,    Radius = 0.49f, Hardness = 0.78f },
                    new BrushTipNode { Id = "noise",  Kind = BrushTipNodeKind.Noise,     Density = 0.85f, Scale = 3.5f, Seed = 17 },
                    new BrushTipNode { Id = "grain",  Kind = BrushTipNodeKind.Multiply,  Inputs = ["mask", "noise"] },
                    new BrushTipNode { Id = "cut",    Kind = BrushTipNodeKind.Threshold, Inputs = ["grain"], Threshold = 0.06f },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,    Inputs = ["cut"] }
                ]
            })
        },

        new("Mechanical Pencil", 5, 1.0, 0.95, 0.10, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Narrow,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.15f),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.48,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },

        new("Charcoal", 38, 0.62, 0.32, 0.22, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.25f)
            },
            Flow = 0.68,
            Smoothing = 0.12,
            Grain = 0.62,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "mask",     Kind = BrushTipNodeKind.Circle,    Radius = 0.49f, Hardness = 0.45f },
                    new BrushTipNode { Id = "noise",    Kind = BrushTipNodeKind.Noise,     Density = 0.78f, Scale = 2.2f, Seed = 31 },
                    new BrushTipNode { Id = "raw",      Kind = BrushTipNodeKind.Multiply,  Inputs = ["mask", "noise"] },
                    new BrushTipNode { Id = "contrast", Kind = BrushTipNodeKind.Power,     Inputs = ["raw"], Scale = 1.8f },
                    new BrushTipNode { Id = "cut",      Kind = BrushTipNodeKind.Threshold, Inputs = ["contrast"], Threshold = 0.10f },
                    new BrushTipNode { Id = "output",   Kind = BrushTipNodeKind.Output,    Inputs = ["cut"] }
                ]
            })
        },

        new("Soft Graphite", 22, 0.52, 0.26, 0.20, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.1f),
                Opacity = CurveOption.Pressure(1.15f)
            },
            Flow = 0.74,
            Smoothing = 0.16,
            Grain = 0.52,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },

        new("6B Graphite", 26, 0.68, 0.35, 0.20, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(0.85f),
                Opacity = CurveOption.Pressure(1.5f)
            },
            Flow = 0.78,
            Smoothing = 0.18,
            Grain = 0.38,
            Tip = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.72f)
        },

        // ── Pens ─────────────────────────────────────────────────────────────

        new("Technical Pen", 8, 1.0, 0.96, 0.10, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Narrow,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.25f),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.42,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },

        new("Ballpoint", 7, 0.82, 0.92, 0.12, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Narrow,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.1f),
                Opacity = CurveOption.Pressure(0.9f)
            },
            Flow = 0.88,
            Smoothing = 0.38,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },

        new("Brush Pen", 18, 0.88, 0.85, 0.14, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.7f),
                Opacity = CurveOption.Pressure(1.2f)
            },
            Flow = 0.92,
            Smoothing = 0.44,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "circle", Kind = BrushTipNodeKind.Circle,          Radius = 0.49f, Hardness = 0.82f },
                    new BrushTipNode { Id = "blur",   Kind = BrushTipNodeKind.DirectionalBlur,  Inputs = ["circle"], RotationDegrees = 90f, Radius = 0.12f },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,           Inputs = ["blur"] }
                ]
            })
        },

        new("Felt Tip", 28, 0.72, 0.52, 0.18, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(0.85f),
                Opacity = CurveOption.Off()
            },
            Flow = 0.62,
            Smoothing = 0.42,
            Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 2.5f)
        },

        new("Calligraphy Pen", 32, 0.82, 0.88, 0.14, Color.Parse("#000000"), 45)
        {
            GapMode = BrushGapMode.Narrow,
            BaseAngleSource = AngleSource.DirectionOfLine,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.4f),
                Opacity = CurveOption.Pressure(0.8f)
            },
            Flow = 0.85,
            Smoothing = 0.52,
            Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 4.0f)
        },

        // ── Brushes ───────────────────────────────────────────────────────────

        new("Round Sable", 20, 0.90, 0.68, 0.22, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.55f),
                Opacity = CurveOption.Pressure(1.4f)
            },
            Flow = 0.86,
            Smoothing = 0.46,
            Tip = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.82f)
        },

        new("Flat Brush", 36, 0.78, 0.72, 0.20, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.2f),
                Opacity = CurveOption.Pressure(1.1f)
            },
            Flow = 0.74,
            Smoothing = 0.36,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "rect",    Kind = BrushTipNodeKind.Rectangle, Width = 0.92f, Height = 0.28f, Hardness = 0.82f },
                    new BrushTipNode { Id = "bristle", Kind = BrushTipNodeKind.Bristle,   Density = 0.58f, Width = 0.88f, Height = 0.35f, Hardness = 0.90f, Seed = 7 },
                    new BrushTipNode { Id = "masked",  Kind = BrushTipNodeKind.Multiply,  Inputs = ["rect", "bristle"] },
                    new BrushTipNode { Id = "output",  Kind = BrushTipNodeKind.Output,    Inputs = ["masked"] }
                ]
            })
        },

        new("Fan Brush", 52, 0.65, 0.55, 0.28, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Wide,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.05f),
                Opacity = CurveOption.Pressure(1.3f)
            },
            Flow = 0.56,
            Smoothing = 0.28,
            AngleJitter = 2.5f,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "rect",    Kind = BrushTipNodeKind.Rectangle, Width = 0.96f, Height = 0.16f, Hardness = 0.72f },
                    new BrushTipNode { Id = "bristle", Kind = BrushTipNodeKind.Bristle,   Density = 0.32f, Width = 0.95f, Height = 0.20f, Hardness = 0.85f, Seed = 19 },
                    new BrushTipNode { Id = "masked",  Kind = BrushTipNodeKind.Multiply,  Inputs = ["rect", "bristle"] },
                    new BrushTipNode { Id = "output",  Kind = BrushTipNodeKind.Output,    Inputs = ["masked"] }
                ]
            })
        },

        new("Bristle Oil", 28, 0.82, 0.76, 0.18, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.3f),
                Opacity = CurveOption.Pressure(1.2f)
            },
            Flow = 0.78,
            Smoothing = 0.32,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "circle",   Kind = BrushTipNodeKind.Circle,   Radius = 0.48f, Hardness = 0.88f },
                    new BrushTipNode { Id = "bristle",  Kind = BrushTipNodeKind.Bristle,  Density = 0.72f, Width = 0.85f, Height = 0.72f, Hardness = 0.95f, Seed = 42 },
                    new BrushTipNode { Id = "raw",      Kind = BrushTipNodeKind.Multiply, Inputs = ["circle", "bristle"] },
                    new BrushTipNode { Id = "contrast", Kind = BrushTipNodeKind.Power,    Inputs = ["raw"], Scale = 1.4f },
                    new BrushTipNode { Id = "output",   Kind = BrushTipNodeKind.Output,   Inputs = ["contrast"] }
                ]
            })
        },

        new("Watercolor Wash", 58, 0.52, 0.18, 0.25, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.15f),
                Opacity = CurveOption.Pressure(1.45f)
            },
            Flow = 0.38,
            Smoothing = 0.68,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "mask",  Kind = BrushTipNodeKind.Circle,       Radius = 0.49f, Hardness = 0.12f },
                    new BrushTipNode { Id = "noise", Kind = BrushTipNodeKind.Noise,        Density = 0.62f, Scale = 1.8f, Seed = 55 },
                    new BrushTipNode { Id = "raw",   Kind = BrushTipNodeKind.Multiply,     Inputs = ["mask", "noise"] },
                    new BrushTipNode { Id = "blur",  Kind = BrushTipNodeKind.IsotropicBlur, Inputs = ["raw"], Radius = 0.08f },
                    new BrushTipNode { Id = "output",Kind = BrushTipNodeKind.Output,       Inputs = ["blur"] }
                ]
            })
        },

        new("Gouache Flat", 42, 0.92, 0.88, 0.18, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(0.9f)
            },
            Flow = 0.88,
            Smoothing = 0.42,
            Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 2.0f)
        },

        // ── Digital ───────────────────────────────────────────────────────────

        new("Soft Round", 32, 0.78, 0.42, 0.22, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.45f),
                Opacity = CurveOption.Pressure(1.3f)
            },
            Flow = 0.72,
            Smoothing = 0.38,
            Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
        },

        new("Hard Round", 20, 0.95, 0.95, 0.12, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Narrow,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.2f),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.35,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },

        new("Pixel Pen", 8, 1.0, 1.0, 0.08, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Narrow,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Off(),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.0,
            Tip = new ProceduralBrushTip(BrushTipShape.Rectangle)
        },

        new("Soft Airbrush", 64, 0.34, 0.08, 0.22, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            ContinuousSpraying = true,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.24,
            Smoothing = 0.62,
            Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
        },

        new("Hard Airbrush", 48, 0.55, 0.72, 0.22, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            ContinuousSpraying = true,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.1f),
                Opacity = CurveOption.Pressure(1.1f)
            },
            Flow = 0.45,
            Smoothing = 0.48,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "circle", Kind = BrushTipNodeKind.Circle,    Radius = 0.49f, Hardness = 0.82f },
                    new BrushTipNode { Id = "noise",  Kind = BrushTipNodeKind.Noise,     Density = 0.55f, Scale = 2.8f, Seed = 3 },
                    new BrushTipNode { Id = "raw",    Kind = BrushTipNodeKind.Multiply,  Inputs = ["circle", "noise"] },
                    new BrushTipNode { Id = "cut",    Kind = BrushTipNodeKind.Threshold, Inputs = ["raw"], Threshold = 0.05f },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,    Inputs = ["cut"] }
                ]
            })
        },

        new("Splatter", 32, 0.78, 0.65, 0.25, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Wide,
            ContinuousSpraying = true,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.3f),
                Opacity = CurveOption.Pressure(1.1f)
            },
            Flow = 0.62,
            Smoothing = 0.08,
            AngleJitter = 180f,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "noise",  Kind = BrushTipNodeKind.Noise,     Density = 0.32f, Scale = 1.0f, Seed = 91 },
                    new BrushTipNode { Id = "cut",    Kind = BrushTipNodeKind.Threshold, Inputs = ["noise"], Threshold = 0.22f },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,    Inputs = ["cut"] }
                ]
            })
        },

        // ── Procedural ────────────────────────────────────────────────────────

        new("Perlin Erosion", 24, 0.82, 0.78, 0.20, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.25f),
                Opacity = CurveOption.Pressure(1.3f)
            },
            Flow = 0.78,
            Smoothing = 0.28,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "mask",   Kind = BrushTipNodeKind.Circle,     Radius = 0.49f, Hardness = 0.92f },
                    new BrushTipNode { Id = "perlin", Kind = BrushTipNodeKind.Perlin,     Scale = 4.5f, Density = 0.60f, Seed = 7 },
                    new BrushTipNode { Id = "pow",    Kind = BrushTipNodeKind.Power,      Inputs = ["perlin"], Scale = 2.5f },
                    new BrushTipNode { Id = "step",   Kind = BrushTipNodeKind.SmoothStep, Inputs = ["pow"], Threshold = 0.35f, Hardness = 0.40f },
                    new BrushTipNode { Id = "masked", Kind = BrushTipNodeKind.Multiply,   Inputs = ["mask", "step"] },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,     Inputs = ["masked"] }
                ]
            })
        },

        new("Voronoi Crackle", 20, 0.88, 0.82, 0.16, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.3f),
                Opacity = CurveOption.Pressure(1.15f)
            },
            Flow = 0.84,
            Smoothing = 0.24,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "mask",   Kind = BrushTipNodeKind.Circle,    Radius = 0.49f, Hardness = 0.85f },
                    new BrushTipNode { Id = "voro",   Kind = BrushTipNodeKind.Voronoi,   Scale = 5.0f, Density = 0.65f, Seed = 13 },
                    new BrushTipNode { Id = "inv",    Kind = BrushTipNodeKind.Invert,    Inputs = ["voro"] },
                    new BrushTipNode { Id = "cut",    Kind = BrushTipNodeKind.Threshold, Inputs = ["inv"], Threshold = 0.55f },
                    new BrushTipNode { Id = "masked", Kind = BrushTipNodeKind.Multiply,  Inputs = ["mask", "cut"] },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,    Inputs = ["masked"] }
                ]
            })
        },

        new("Ragged Ink", 16, 0.92, 0.88, 0.12, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Narrow,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.55f),
                Opacity = CurveOption.Pressure(1.05f)
            },
            Flow = 0.90,
            Smoothing = 0.38,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "circle", Kind = BrushTipNodeKind.Circle,    Radius = 0.49f, Hardness = 0.88f },
                    new BrushTipNode { Id = "ragged", Kind = BrushTipNodeKind.RaggedEdge, Inputs = ["circle"], Density = 0.35f, Scale = 8.0f },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,    Inputs = ["ragged"] }
                ]
            })
        },

        new("Warp Bristle", 22, 0.78, 0.65, 0.22, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.2f),
                Opacity = CurveOption.Pressure(1.25f)
            },
            Flow = 0.72,
            Smoothing = 0.30,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "coords",  Kind = BrushTipNodeKind.Coordinates },
                    new BrushTipNode { Id = "disp",    Kind = BrushTipNodeKind.Noise,           Inputs = ["coords"], Density = 0.65f, Scale = 2.5f, Seed = 5 },
                    new BrushTipNode { Id = "warp",    Kind = BrushTipNodeKind.WarpCoordinates, Inputs = ["coords", "disp"], Density = 0.28f, Width = 0.35f, Height = 0.35f },
                    new BrushTipNode { Id = "perlin",  Kind = BrushTipNodeKind.Perlin,          Inputs = ["warp"], Scale = 4.0f, Density = 0.70f },
                    new BrushTipNode { Id = "mask",    Kind = BrushTipNodeKind.Circle,          Radius = 0.49f, Hardness = 0.75f },
                    new BrushTipNode { Id = "bristle", Kind = BrushTipNodeKind.Bristle,         Density = 0.55f, Width = 0.88f, Height = 0.65f, Hardness = 0.90f, Seed = 23 },
                    new BrushTipNode { Id = "pattern", Kind = BrushTipNodeKind.Multiply,        Inputs = ["perlin", "bristle"] },
                    new BrushTipNode { Id = "masked",  Kind = BrushTipNodeKind.Multiply,        Inputs = ["mask", "pattern"] },
                    new BrushTipNode { Id = "output",  Kind = BrushTipNodeKind.Output,          Inputs = ["masked"] }
                ]
            })
        },

        new("Noise Grain", 18, 0.75, 0.70, 0.18, Color.Parse("#000000"), 100)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.2f),
                Opacity = CurveOption.Pressure(1.35f)
            },
            Flow = 0.70,
            Smoothing = 0.20,
            Grain = 0.22,
            Tip = new NodeBrushTip(new BrushTipNodeGraph
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode { Id = "mask",   Kind = BrushTipNodeKind.Circle,    Radius = 0.49f, Hardness = 0.72f },
                    new BrushTipNode { Id = "noise",  Kind = BrushTipNodeKind.Noise,     Density = 0.45f, Scale = 4.2f, Seed = 61 },
                    new BrushTipNode { Id = "raw",    Kind = BrushTipNodeKind.Multiply,  Inputs = ["mask", "noise"] },
                    new BrushTipNode { Id = "boost",  Kind = BrushTipNodeKind.Power,     Inputs = ["raw"], Scale = 1.6f },
                    new BrushTipNode { Id = "cut",    Kind = BrushTipNodeKind.Threshold, Inputs = ["boost"], Threshold = 0.08f },
                    new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output,    Inputs = ["cut"] }
                ]
            })
        },

        // ── Blending ──────────────────────────────────────────────────────────

        new("Soft Blend", 28, 0.72, 0.55, 0.22, Color.Parse("#000000"), 0)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.1f),
                Opacity = CurveOption.Pressure(1.1f)
            },
            Flow = 0.62,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = 0.18,
            BlurAmount = 0.85,
            AmountOfPaint = 0.0,
            DensityOfPaint = 0.0,
            TipThickness = 0.45,
            MixingMode = MixingMode.Perceptual,
            SmudgeMode = SmudgeMode.Blend,
            Smoothing = 0.52,
            Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
        },

        new("Smudge Finger", 24, 0.68, 0.75, 0.22, Color.Parse("#000000"), 0)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.58,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = 0.79,
            BlurAmount = 0.62,
            MixingMode = MixingMode.Perceptual,
            AmountOfPaint = 0.74,
            DensityOfPaint = 1.0,
            TipThickness = 0.42,
            TipDirection = BrushTipDirection.Horizontal,
            SmudgeMode = SmudgeMode.Smudge,
            Smoothing = 0.45,
            Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
        },

        new("Bristle Blend", 32, 0.65, 0.60, 0.24, Color.Parse("#000000"), 0)
        {
            GapMode = BrushGapMode.Normal,
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.15f),
                Opacity = CurveOption.Pressure(1.2f)
            },
            Flow = 0.55,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = 0.22,
            BlurAmount = 0.72,
            AmountOfPaint = 0.0,
            DensityOfPaint = 0.0,
            TipThickness = 0.48,
            MixingMode = MixingMode.Perceptual,
            SmudgeMode = SmudgeMode.Blend,
            Smoothing = 0.40,
            Tip = new NodeBrushTip(BrushTipNodeGraph.BristleRound())
        },
    ];
}
