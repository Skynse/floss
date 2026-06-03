using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes.Engine;

public readonly record struct BrushRenderStats(
    string Path,
    int StampCount,
    int CachedDabCount,
    int TileBucketCount,
    int DirtyPixels,
    double ElapsedMs)
{
    public static BrushRenderStats Empty { get; } = new("None", 0, 0, 0, 0, 0);

    public static BrushRenderStats From(
        string path,
        int stampCount,
        int cachedDabCount,
        int tileBucketCount,
        int dirtyPixels,
        double elapsedMs)
        => new(path, stampCount, cachedDabCount, tileBucketCount, dirtyPixels, elapsedMs);
}

public sealed partial class BrushEngine : IDisposable
{
    private const int MaxPrecomputedGrainPixels = 1024 * 1024;

    private const int InitialStampCapacity = 1024;
    private const float MinStretchCarry = 0.02f;
    private const float MaxStretchCarry = 0.88f;
    private readonly object _gate = new();
    private readonly List<StampSample> _stamps = new(InitialStampCapacity);
    private readonly List<SKColor> _stampColors = new(InitialStampCapacity);
    private ActiveStroke? _activeStroke;
    private readonly Dictionary<string, SKBitmap?> _textureCache = new();
    private readonly Dictionary<int, List<PlacedDab>> _dabBuckets = new();
    private readonly Dictionary<int, List<PlacedColorDab>> _colorDabBuckets = new();
    private readonly Dictionary<int, byte[]?> _smearSnapshots = new(32);
    private float[]? _grainTable;
    private string _lastRasterPath = "None";
    private int _lastCachedDabCount;
    private int _lastTileBucketCount;

    public BrushRenderStats LastStats { get; private set; } = BrushRenderStats.Empty;

    /// <summary>Current viewport zoom. When &lt; 1.0, stamp rendering uses
    /// a downscaled LOD buffer then upscales to full-res tiles (Krita's
    /// KisLodTransform approach — same visual size, fewer pixels).</summary>
    public double CanvasZoom { get; set; } = 1.0;

    public delegate void PixelSampler(int x, int y, out byte b, out byte g, out byte r, out byte a);

    // Returns the 64x64x4 BGRA bytes for a tile in the layer's pre-stroke state,
    // or null if that tile was transparent. Used to populate the engine's
    // sample buffer in bulk via Buffer.BlockCopy instead of per-pixel delegate
    // calls — the difference between sub-millisecond and hundreds of milliseconds
    // for color-mix brushes covering a large region.
    public delegate byte[]? TileReader(int tileX, int tileY);

    public void BeginStroke(BrushPreset brush, CanvasInputSample sample)
    {
        lock (_gate)
        {
            EndStrokeCore();
            _activeStroke = new ActiveStroke(brush, sample);
        }
    }

    public void EndStroke()
    {
        lock (_gate)
            EndStrokeCore();
    }

    private void EndStrokeCore()
    {
        _activeStroke?.Dispose();
        _activeStroke = null;
        _stamps.Clear();
        _stampColors.Clear();
    }

    public PixelRegion RasterizeSegment(
        DrawingLayer layer, BrushPreset brush,
        CanvasInputSample from, CanvasInputSample to,
        PixelSampler? sampleSource = null,
        TileReader? tileReader = null)
    {
        lock (_gate)
            return RasterizeSegmentInternal(layer, brush, from, to, ensureEndpoint: false, sampleSource, tileReader);
    }

    public PixelRegion RasterizeSegments(
        DrawingLayer layer,
        BrushPreset brush,
        IReadOnlyList<CanvasInputSample> samples,
        int startSegmentIndex,
        int segmentCount,
        PixelSampler? sampleSource = null,
        TileReader? tileReader = null)
    {
        lock (_gate)
            return RasterizeSegmentsCore(layer, brush, samples, startSegmentIndex, segmentCount, sampleSource, tileReader);
    }

    private PixelRegion RasterizeSegmentsCore(
        DrawingLayer layer,
        BrushPreset brush,
        IReadOnlyList<CanvasInputSample> samples,
        int startSegmentIndex,
        int segmentCount,
        PixelSampler? sampleSource,
        TileReader? tileReader)
    {
        if (segmentCount <= 0 || samples.Count < 2) return PixelRegion.Empty;
        if (startSegmentIndex <= 0 || startSegmentIndex >= samples.Count) return PixelRegion.Empty;

        var started = Stopwatch.GetTimestamp();
        _lastRasterPath = "None";
        _lastCachedDabCount = 0;
        _lastTileBucketCount = 0;

        EnsureStroke(brush, samples[startSegmentIndex - 1]);
        var stroke = _activeStroke!;
        _stamps.Clear();
        _stampColors.Clear();
        _stamps.EnsureCapacity(InitialStampCapacity);
        if (brush.ColorMix)
            _stampColors.EnsureCapacity(InitialStampCapacity);

        var lastSegmentIndex = Math.Min(samples.Count - 1, startSegmentIndex + segmentCount - 1);
        var dirty = PixelRegion.Empty;
        for (var i = startSegmentIndex; i <= lastSegmentIndex; i++)
            dirty = dirty.Union(BuildStamps(stroke, brush, samples[i - 1], samples[i], ensureEndpoint: false));

        if (dirty.IsEmpty || _stamps.Count == 0)
        {
            LastStats = BrushRenderStats.From("Empty", 0, 0, 0, 0, ElapsedMs(started));
            return PixelRegion.Empty;
        }

        if (brush.ColorMix)
        {
            try
            {
                if (UsesLiveSmudgePickup(brush))
                    RasterizeColorMixSequential(layer, brush, stroke, dirty, started);
                else
                    RasterizeColorMixBatch(layer, brush, stroke, dirty, sampleSource, tileReader, started);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BrushEngine.ColorMix", flushToDisk: true);
                _stampColors.Clear();
                    }
        }
        else
        {
            try
            {
                RenderCurrentStamps(layer, stroke, brush, dirty);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BrushEngine.RenderCurrentStamps(Segments)", flushToDisk: true);
            }
        }


        LastStats = BrushRenderStats.From(
            _lastRasterPath,
            _stamps.Count,
            _lastCachedDabCount,
            _lastTileBucketCount,
            dirty.Width * dirty.Height,
            ElapsedMs(started));
        return dirty;
    }
    public PixelRegion RasterizeFinalSegment(
        DrawingLayer layer, BrushPreset brush,
        CanvasInputSample from, CanvasInputSample to,
        PixelSampler? sampleSource = null,
        TileReader? tileReader = null)
    {
        lock (_gate)
            return RasterizeSegmentInternal(layer, brush, from, to, ensureEndpoint: true, sampleSource, tileReader);
    }

    private PixelRegion RasterizeSegmentInternal(
        DrawingLayer layer, BrushPreset brush,
        CanvasInputSample from, CanvasInputSample to, bool ensureEndpoint,
        PixelSampler? sampleSource, TileReader? tileReader = null)
    {
        var started = Stopwatch.GetTimestamp();
        _lastRasterPath = "None";
        _lastCachedDabCount = 0;
        _lastTileBucketCount = 0;

        EnsureStroke(brush, from);
        var stroke = _activeStroke!;
        _stamps.Clear();
        _stampColors.Clear();

        var dirty = BuildStamps(stroke, brush, from, to, ensureEndpoint);
        if (dirty.IsEmpty || _stamps.Count == 0)
        {
            LastStats = BrushRenderStats.From("Empty", 0, 0, 0, 0, ElapsedMs(started));
            return PixelRegion.Empty;
        }

        if (brush.ColorMix)
        {
            try
            {
                if (UsesLiveSmudgePickup(brush))
                    RasterizeColorMixSequential(layer, brush, stroke, dirty, started);
                else
                    RasterizeColorMixBatch(layer, brush, stroke, dirty, sampleSource, tileReader, started);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BrushEngine.ColorMix(Segment)", flushToDisk: true);
                _stampColors.Clear();
                    }
        }
        else
        {
            try
            {
                RenderCurrentStamps(layer, stroke, brush, dirty);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BrushEngine.RenderCurrentStamps(Segment)", flushToDisk: true);
            }
        }


        LastStats = BrushRenderStats.From(
            _lastRasterPath,
            _stamps.Count,
            _lastCachedDabCount,
            _lastTileBucketCount,
            dirty.Width * dirty.Height,
            ElapsedMs(started));
        return dirty;
    }

    public PixelRegion EstimateSegmentRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample from, CanvasInputSample to)
    {
        var radius = EstimateBrushRadius(brush);
        var minX = (int)Math.Floor(Math.Min(from.X, to.X) - radius);
        var minY = (int)Math.Floor(Math.Min(from.Y, to.Y) - radius);
        var maxX = (int)Math.Ceiling(Math.Max(from.X, to.X) + radius);
        var maxY = (int)Math.Ceiling(Math.Max(from.Y, to.Y) + radius);
        return new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public PixelRegion EstimateDabRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample sample)
        => EstimateSegmentRegion(layer, brush, sample, sample);

    public void Dispose()
    {
        lock (_gate)
        {
            EndStrokeCore();
            foreach (var bmp in _textureCache.Values)
                bmp?.Dispose();
            _textureCache.Clear();
        }
    }
    private void EnsureStroke(BrushPreset brush, CanvasInputSample sample)
    {
        if (_activeStroke == null || !_activeStroke.Matches(brush))
            BeginStroke(brush, sample);
    }
    public static bool UsesProceduralStampEvaluation(BrushPreset brush, IBrushTip primaryTip, int stampColorCount)
    {
        if (stampColorCount != 0) return false;
        if (brush.ColorMix) return false;
        if (!SupportsCpuRasterBlendMode(brush.BlendMode)) return false;
        if (brush.Shape != null) return false;
        if (HasMultiTipSelection(brush)) return false;
        if (primaryTip.HasColor) return false;

        return primaryTip switch
        {
            ImageBrushTip => true,
            NodeBrushTip { IsDirectImageSampler: true } => true,
            ProceduralBrushTip proc => CanDirectEvaluateGraph(proc.Graph, brush),
            // ImageSampler graphs must bake a mask once — per-pixel graph eval re-samples
            // the PNG for every stamp pixel and stalls the UI thread.
            NodeBrushTip node when node.Graph.ContainsImageSampler(BrushMaterialTips.ForPreset(brush)) => false,
            NodeBrushTip node => CanDirectEvaluateGraph(node.Graph, brush),
            _ => false
        };
    }

    private static bool CanDirectEvaluateGraph(BrushTipNodeGraph graph, BrushPreset brush)
        => BrushTipStampFastPath.TryCreate(graph, (float)brush.Hardness, out _);

    private static bool HasMultiTipSelection(BrushPreset brush)
        => brush.TipSelectionMode != BrushTipSelectionMode.Single && brush.Tips.Count > 1;
    private readonly record struct StampSample(
        float X,
        float Y,
        float Size,
        float Opacity,
        float Angle,
        float Hardness,
        float SpacingMultiplier,
        float TipThicknessMultiplier,
        int TipIndex);
    private readonly record struct PlacedDab(
        StampSample Stamp,
        ActiveStroke.CachedDab Dab,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int ColorB,
        int ColorG,
        int ColorR,
        float ColorA);
    private readonly record struct PlacedColorDab(
        StampSample Stamp,
        ActiveStroke.CachedColorDab Dab,
        int Left,
        int Top,
        int Right,
        int Bottom);
    private readonly record struct SplinePoint(float X, float Y, float Pressure, float TiltX, float TiltY, float Twist);
}
