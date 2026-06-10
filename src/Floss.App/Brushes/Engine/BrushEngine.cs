using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Floss.App.Brushes.Graph;
using Floss.App.Brushes.Tips;
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

public sealed class BrushEngine : IDisposable
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TiledPixelBuffer PaintBuf(DrawingLayer layer) => layer.ActivePixels;

    private static bool MaskPaint(DrawingLayer layer) => layer.IsMaskEditing && layer.HasMask;

    private const int MaxPrecomputedGrainPixels = 1024 * 1024;
    private const int MaxColorMixScratchPixels = 2048 * 2048;
    // Profile live-1024brush-30s: procedural direct eval at 1024px ~1M px/dab; bake+blit instead.
    internal const float LargeStampCachedRasterMinDiameterPx = 256f;
    private const int LargeDabCpuBakeMaxDimension = 4096;

    // brush engine grows dab batches from a 1024-element floor. Keep
    // the managed stamp/color lists in the same range so normal fast strokes do
    // not repeatedly resize while input is being coalesced.
    private const int InitialStampCapacity = 1024;
    private const float MinStretchCarry = 0.02f;
    private const float MaxStretchCarry = 0.88f;

    // Precomputed LUTs for sRGB gamma decode/encode and cube root to avoid
    // MathF.Pow in the hot RgbToLCh/LChToRgb color mixing path.
    private const int CsLutSize = 4096;
    private static readonly float[] s_srgbToLinearLut = CreateSrgbToLinearLut(CsLutSize);
    private static readonly float[] s_linearToSrgbLut = CreateLinearToSrgbLut(CsLutSize);
    private static readonly float[] s_cubeRootLut = CreateCubeRootLut(CsLutSize);
    private static readonly float[] s_cubeLut = CreateCubeLut(CsLutSize);

    private static float[] CreateSrgbToLinearLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x > 0.04045f ? MathF.Pow((x + 0.055f) / 1.055f, 2.4f) : x / 12.92f;
        }
        return lut;
    }

    private static float[] CreateLinearToSrgbLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x > 0.0031308f ? 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f : 12.92f * x;
        }
        return lut;
    }

    private static float[] CreateCubeRootLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x > 0.008856f ? MathF.Pow(x, 1f / 3f) : 7.787f * x + 16f / 116f;
        }
        return lut;
    }

    private static float[] CreateCubeLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x * x * x;
        }
        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SrgbToLinear(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_srgbToLinearLut[(int)(x * (CsLutSize - 1) + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToSrgb(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_linearToSrgbLut[(int)(x * (CsLutSize - 1) + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FAST_Cbrt(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_cubeRootLut[(int)(x * (CsLutSize - 1) + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FAST_Cube(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_cubeLut[(int)(x * (CsLutSize - 1) + 0.5f)];
    private readonly object _gate = new();
    private readonly List<StampSample> _stamps = new(InitialStampCapacity);
    private readonly List<SKColor> _stampColors = new(InitialStampCapacity);
    private ActiveStroke? _activeStroke;
    private SKBitmap? _scratch;
    private SKBitmap? _lodScratch;
    private readonly SKPaint _scratchCompositePaint = new() { BlendMode = SKBlendMode.SrcOver };
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
    /// a downscaled LOD buffer then upscales to full-res tiles (same visual size, fewer pixels).</summary>
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

    /// <summary>Pre-stroke tile snapshot (first capture per tile). Used to rebuild the
    /// layer from max-alpha stroke mask without compounding dab overlaps across batches.</summary>
    public void BindStrokeBeforeTiles(IReadOnlyDictionary<(int X, int Y), byte[]?>? beforeTiles)
    {
        lock (_gate)
        {
            if (_activeStroke != null)
                _activeStroke.BeforeTiles = beforeTiles;
        }
    }

    /// <summary>Snapshot dab-spacing state (e.g. before a preview tail that will be tile-reverted).</summary>
    public bool TryCaptureStrokeState(out StrokeState state)
    {
        lock (_gate)
        {
            if (_activeStroke == null)
            {
                state = default;
                return false;
            }

            state = _activeStroke.State;
            return true;
        }
    }

    public void RestoreStrokeState(StrokeState state)
    {
        lock (_gate)
        {
            if (_activeStroke != null)
                _activeStroke.State = state;
        }
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

    private static bool UsesLiveSmudgePickup(BrushPreset brush)
        => brush.ColorMix && brush.SmudgeMode != SmudgeMode.Blend;

    private void RasterizeColorMixBatch(
        DrawingLayer layer,
        BrushPreset brush,
        ActiveStroke stroke,
        PixelRegion dirty,
        PixelSampler? sampleSource,
        TileReader? tileReader,
        long started)
    {
        PrepareStampColors(layer, brush, stroke, sampleSource);
        if (dirty.IsEmpty || _stamps.Count == 0)
        {
            LastStats = BrushRenderStats.From("Empty", _stamps.Count, 0, 0, 0, ElapsedMs(started));
            return;
        }

        RenderCurrentStamps(layer, stroke, brush, dirty);
    }

    // Smear and Running Color must read the LIVE layer and render stamp-by-stamp
    // so each dab picks up pigment left by the previous dab. Batch sampling from
    // the pre-stroke snapshot produces disconnected circular blobs — the #1 reason
    // smudge looked nothing like even with matching settings.
    private void RasterizeColorMixSequential(
        DrawingLayer layer,
        BrushPreset brush,
        ActiveStroke stroke,
        PixelRegion dirty,
        long started)
    {
        _lastRasterPath = brush.SmudgeMode == SmudgeMode.Smear ? "SpatialSmear" : "SmudgeSequential";
        if (_stamps.Count == 0)
        {
            LastStats = BrushRenderStats.From("Empty", 0, 0, 0, 0, ElapsedMs(started));
            return;
        }

        var allStamps = _stamps.ToArray();
        _stamps.Clear();
        _stampColors.Clear();
        var useSpatialSmear = brush.SmudgeMode == SmudgeMode.Smear;

        try
        {
            for (var i = 0; i < allStamps.Length; i++)
            {
                var stamp = allStamps[i];
                var stampDirty = StampBounds(stamp);

                if (useSpatialSmear)
                {
                    try
                    {
                        var smearResult = TryRenderSpatialSmearStamp(layer, stroke, brush, stamp, stampDirty);
                        if (smearResult == SpatialSmearResult.Rendered)
                        {
                            PaintBuf(layer).PruneRegion(stampDirty);
                        }
                        else if (smearResult == SpatialSmearResult.Failed)
                        {
                                                _stampColors.Clear();
                            _stamps.Add(stamp);
                            PrepareOneStampColor(layer, brush, stroke, stamp, sampleSource: null);
                            if (_stampColors.Count > 0 && _stampColors[0].Alpha > 0)
                                RenderCurrentStamps(layer, stroke, brush, stampDirty);
                            _stamps.Clear();
                            _stampColors.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "BrushEngine.TryRenderSpatialSmearStamp", flushToDisk: true);
                    }
                    continue;
                }

                        _stampColors.Clear();
                _stamps.Add(stamp);
                PrepareOneStampColor(layer, brush, stroke, stamp, sampleSource: null);
                if (_stampColors.Count == 0 || _stampColors[0].Alpha == 0)
                {
                    _stamps.Clear();
                    continue;
                }

                try
                {
                    RenderCurrentStamps(layer, stroke, brush, stampDirty);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "BrushEngine.RenderCurrentStamps(SmudgeSequential)", flushToDisk: true);
                }
                _stamps.Clear();
                _stampColors.Clear();
            }
        }
        finally
        {
        }

        _stamps.AddRange(allStamps);
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


        if (!dirty.IsEmpty && MaskPaint(layer))
            SyncMaskRgbFromAlpha(PaintBuf(layer), dirty);

        MarkPaintThumbnailsDirty(layer);

        LastStats = BrushRenderStats.From(
            _lastRasterPath,
            _stamps.Count,
            _lastCachedDabCount,
            _lastTileBucketCount,
            dirty.Width * dirty.Height,
            ElapsedMs(started));
        return dirty;
    }

    private static void SyncMaskRgbFromAlpha(TiledPixelBuffer mask, PixelRegion dirty)
    {
        if (dirty.IsEmpty) return;
        const int tsz = TiledPixelBuffer.TileSize;
        var firstTx = FloorDiv(dirty.X, tsz);
        var firstTy = FloorDiv(dirty.Y, tsz);
        var lastTx = FloorDiv(dirty.Right - 1, tsz);
        var lastTy = FloorDiv(dirty.Bottom - 1, tsz);
        mask.EnterPixelWriteLock();
        try
        {
            for (var ty = firstTy; ty <= lastTy; ty++)
            for (var tx = firstTx; tx <= lastTx; tx++)
            {
                var tile = mask.GetTileOrNull(tx, ty);
                if (tile == null) continue;
                var pxMinX = Math.Max(dirty.X, tx * tsz);
                var pxMinY = Math.Max(dirty.Y, ty * tsz);
                var pxMaxX = Math.Min(dirty.Right, tx * tsz + tsz);
                var pxMaxY = Math.Min(dirty.Bottom, ty * tsz + tsz);
                for (var py = pxMinY; py < pxMaxY; py++)
                {
                    var rowBase = (py - ty * tsz) * tsz * 4;
                    for (var px = pxMinX; px < pxMaxX; px++)
                    {
                        var off = rowBase + (px - tx * tsz) * 4;
                        var a = tile[off + 3];
                        tile[off] = a;
                        tile[off + 1] = a;
                        tile[off + 2] = a;
                    }
                }
            }
        }
        finally
        {
            mask.ExitPixelWriteLock();
        }
    }

    private unsafe void RenderCurrentStamps(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        // For standard SrcOver brushes without color mixing, render stamps to a
        // temporary scratch so overlapping stamps within this batch accumulate
        // src-over alpha (same-color coverage). The scratch is then composited
        // onto the layer with SrcOver once.
        // Skip scratch for few stamps (large brushes) — the overhead of allocating
        // and blitting a huge scratch bitmap exceeds the cost of direct per-stamp draw.
        bool isMultiTipSingle = false;
        var primaryTip = stroke.TipFor(0);
        bool usesProceduralStamp = UsesProceduralStampEvaluation(brush, primaryTip, _stampColors.Count);
        bool preferCachedTileMajor = PreferCachedTileMajorRaster(brush);

        bool blendModeCanRasterize = SupportsCpuRasterBlendMode(brush.BlendMode);

        if (blendModeCanRasterize && !isMultiTipSingle && (!usesProceduralStamp || preferCachedTileMajor))
        {
            var baseColor = stroke.BaseColor;
            float brushGrain = 0f;
            byte* texPx = null;
            int texW = 0, texH = 0, texStride = 0;
            var grainTable = PrecomputeGrain(dirty, texPx, texW, texH, texStride, brushGrain);

            float brushThickness = MathF.Max(0.01f, MathF.Min(4f, (float)brush.TipThickness));
            if (stroke.HasAnyColorTip && _stampColors.Count == 0 &&
                TryRasterizeCachedColorDabsTileMajor(layer, stroke, brush, brush.BlendMode,
                    brushGrain, texPx, texW, texH, texStride, dirty, grainTable))
            {
                _lastRasterPath = "CachedColorTileMajor";
                PaintBuf(layer).PruneRegion(dirty);
                return;
            }

            if (!stroke.HasAnyColorTip)
            {
                // Live large strokes: accumulate max-alpha in a persistent stroke mask and
                // rebuild dirty tiles from the pre-stroke snapshot each batch. Prevents
                // overlap compounding across UI time-slices (the square banding artifact).
                if (PreferStrokeMaskComposite(layer, brush)
                    && TryAccumulateCachedDabsToStrokeMask(stroke, brush, brush.BlendMode,
                        baseColor.Blue, baseColor.Green, baseColor.Red, baseColor.Alpha,
                        brushGrain, texPx, texW, texH, texStride, dirty, grainTable))
                {
                    ApplyStrokeMaskToLayer(layer, stroke, dirty,
                        baseColor.Blue, baseColor.Green, baseColor.Red, brush.BlendMode);
                    _lastRasterPath = "StrokeMaskCached";
                    PaintBuf(layer).PruneRegion(dirty);
                    return;
                }

                var multiLargeSrcOver = preferCachedTileMajor && _stamps.Count > 1
                    && brush.BlendMode == SKBlendMode.SrcOver;

                if (multiLargeSrcOver &&
                    TryRasterizeCachedDabsLightenScratch(layer, stroke, brush,
                        baseColor.Blue, baseColor.Green, baseColor.Red, baseColor.Alpha, dirty))
                {
                    _lastRasterPath = "CachedLightenScratch";
                    PaintBuf(layer).PruneRegion(dirty);
                    return;
                }

                if (TryRasterizeCachedDabsTileMajor(layer, stroke, brush, brush.BlendMode,
                        baseColor.Blue, baseColor.Green, baseColor.Red, baseColor.Alpha,
                        brushGrain, texPx, texW, texH, texStride, dirty, grainTable,
                        perTileLighten: multiLargeSrcOver))
                {
                    _lastRasterPath = multiLargeSrcOver ? "CachedTileMajorLighten" : "CachedTileMajor";
                    PaintBuf(layer).PruneRegion(dirty);
                    return;
                }
            }
        }

        var blurAmount = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
        bool useScratch = !usesProceduralStamp
            && brush.BlendMode == SKBlendMode.SrcOver
            && dirty.Width <= 4096 && dirty.Height <= 4096 // guard against OOM
            && (long)dirty.Width * dirty.Height <= MaxColorMixScratchPixels
            && _stamps.Count >= 1
            && (_stampColors.Count == 0 || _stampColors.Count == _stamps.Count);

        if (usesProceduralStamp)
            RasterizeStampsDirect(layer, stroke, brush, dirty);
        else if (useScratch)
        {
            _lastRasterPath = _stampColors.Count > 0 ? "ColorMixScratch" : "Scratch";
            if (_stampColors.Count > 0)
                RenderColorMixStampsViaScratch(layer, stroke, brush, dirty);
            else
                RenderStampsViaScratch(layer, stroke, brush, dirty);
        }
        else
        {
            _lastRasterPath = "SkiaFallback";
            RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
        }
    }

    public PixelRegion RasterizeDab(
        DrawingLayer layer,
        BrushPreset brush,

        CanvasInputSample sample,
        double velocity,
        PixelSampler? sampleSource = null)
    {
        lock (_gate)
        {
            EnsureStroke(brush, sample);
            var stroke = _activeStroke!;
            _stamps.Clear();
            _stampColors.Clear();
    
            var velocity01 = (float)Math.Clamp(velocity / 5000.0, 0, 1);
            var sp = BuildStrokePoint(stroke, sample, velocity01);
            var stamp = CreateStamp(stroke, brush, sp);
            _stamps.Add(stamp);

            if (brush.ColorMix)
            {
                try
                {
                    try
                    {
                        PrepareStampColors(layer, brush, stroke, sampleSource);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "BrushEngine.PrepareStampColors(Dab)", flushToDisk: true);
                        _stampColors.Clear();
                    }

                    var dirty = StampBounds(stamp);
                    if (dirty.IsEmpty) return PixelRegion.Empty;

                    try
                    {
                        RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "BrushEngine.RasterizeDab", flushToDisk: true);
                    }
                    return dirty;
                }
                finally
                {
                }
            }

            var dabDirty = StampBounds(stamp);
            if (dabDirty.IsEmpty) return PixelRegion.Empty;

            try
            {
                RenderWithSkiaOnLayer(layer, dabDirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BrushEngine.RasterizeDab", flushToDisk: true);
            }
            return dabDirty;
        }
    }

    private static float LerpAngle(float a, float b, float t)
    {
        var delta = b - a;
        if (delta > MathF.PI) delta -= MathF.Tau;
        else if (delta < -MathF.PI) delta += MathF.Tau;
        return a + delta * t;
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
            _scratch?.Dispose();
            _scratch = null;
            _lodScratch?.Dispose();
            _lodScratch = null;
            _scratchCompositePaint.Dispose();
            foreach (var bmp in _textureCache.Values)
                bmp?.Dispose();
            _textureCache.Clear();
        }
    }

    private SKBitmap? GetOrLoadTexture(string path)
    {
        if (_textureCache.TryGetValue(path, out var cached)) return cached;
        SKBitmap? bmp = null;
        try
        {
            using var original = SKBitmap.Decode(path);
            if (original != null)
                bmp = original.Copy(SKColorType.Gray8);
        }
        catch { }
        _textureCache[path] = bmp;
        return bmp;
    }

    private void EnsureStroke(BrushPreset brush, CanvasInputSample sample)
    {
        if (_activeStroke == null || !_activeStroke.Matches(brush))
            BeginStroke(brush, sample);
    }

    private PixelRegion BuildStamps(ActiveStroke stroke, BrushPreset brush, CanvasInputSample from, CanvasInputSample to, bool ensureEndpoint = false)
    {
        if (from.Pressure <= 0 && to.Pressure <= 0) return PixelRegion.Empty;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var elapsedSeconds = Math.Max(0.001, (to.TimeMicros - from.TimeMicros) / 1_000_000.0);
        var velocity01 = Math.Clamp((float)(distance / elapsedSeconds / 5000.0), 0, 1);

        if (distance <= 0.001)
            return BuildStampsContinuous(stroke, brush, from, to, velocity01);

        var currentAngle = MathF.Atan2((float)dy, (float)dx);
        stroke.State.DrawingAngle = LerpAngle(stroke.State.DrawingAngle, currentAngle, 0.5f);

        // Round procedural tips: stamp on the chord polyline (input samples). Catmull-Rom
        // between sparse stabilized points creates uneven dab density (beads).
        if (PreferLinearStampPath(brush) || velocity01 >= 0.5f || IsStraightSegment(stroke, from, to))
            return BuildStampsLinear(stroke, brush, from, to, distance, velocity01, ensureEndpoint);

        // Subdivide based on expected stamp count, not raw pixel distance.
        // A 1000px brush with 250px spacing needs ~4 stamps; 96 subdivisions
        // would waste time on 90+ pointless Catmull-Rom evaluations.
        var stampSpacing = Math.Max(1, stroke.State.NextStampDistance);
        var estimatedStamps = distance / stampSpacing;
        var subdivisions = Math.Max(4, Math.Min(48, (int)Math.Ceiling(estimatedStamps * 2)));

        var p0 = new SplinePoint(
            stroke.State.LastX, stroke.State.LastY, stroke.State.LastPressure,
            stroke.State.LastTiltX, stroke.State.LastTiltY, (float)from.Twist);
        var p1 = ToSplinePoint(from);
        var p2 = ToSplinePoint(to);
        var p3 = new SplinePoint(
            (float)(to.X + (to.X - from.X)), (float)(to.Y + (to.Y - from.Y)),
            (float)to.Pressure, (float)to.TiltX, (float)to.TiltY, (float)to.Twist);

        var previous = p1;
        var dirty = PixelRegion.Empty;

        for (var i = 1; i <= subdivisions; i++)
        {
            var t = i / (float)subdivisions;
            var current = CatmullRom(p0, p1, p2, p3, t);
            var segDx = current.X - previous.X;
            var segDy = current.Y - previous.Y;
            var segLen = MathF.Sqrt(segDx * segDx + segDy * segDy);

            if (segLen > 0.0001f)
            {
                var consumed = stroke.State.NextStampDistance - stroke.State.DistanceLeftover;
                while (consumed <= segLen)
                {
                    var ratio = consumed / segLen;
                    var sample = Lerp(previous, current, ratio, from, to);
                    var sp = BuildStrokePoint(stroke, sample, velocity01);
                    var stamp = CreateStamp(stroke, brush, sp);

                    if (!BrushSpacing.IsStampTooSmall(stamp.Size))
                    {
                        _stamps.Add(stamp);
                        dirty = dirty.Union(StampBounds(stamp));
                        stroke.State.TotalDistance += stroke.State.NextStampDistance;
                        stroke.State.DabSeqNo++;
                    }

                    stroke.State.NextStampDistance = StampSpacing(brush, stamp);
                    consumed += stroke.State.NextStampDistance;
                }

                stroke.State.DistanceLeftover = segLen - (consumed - stroke.State.NextStampDistance);
                if (stroke.State.DistanceLeftover >= stroke.State.NextStampDistance)
                    stroke.State.DistanceLeftover = 0;
            }

            previous = current;
        }

        // On the final segment, the spacing accumulator may leave a small gap to
        // the pen-up endpoint. Expand the dirty region by one stamp radius to
        // ensure soft brush edges cover any sub-spacing gap.
        if (ensureEndpoint && _stamps.Count > 0)
        {
            dirty = dirty.Inflate((int)(_stamps[^1].Size * 0.25f + 1));
        }

        stroke.State.LastX = (float)to.X;
        stroke.State.LastY = (float)to.Y;
        stroke.State.LastPressure = (float)to.Pressure;
        stroke.State.LastTiltX = (float)to.TiltX;
        stroke.State.LastTiltY = (float)to.TiltY;

        return dirty;
    }

    private static bool IsStraightSegment(ActiveStroke stroke, CanvasInputSample from, CanvasInputSample to)
    {
        var segDx = to.X - from.X;
        var segDy = to.Y - from.Y;
        var segLenSq = segDx * segDx + segDy * segDy;
        if (segLenSq < 1e-6) return true;

        var prevDx = from.X - stroke.State.LastX;
        var prevDy = from.Y - stroke.State.LastY;
        if (prevDx * prevDx + prevDy * prevDy < 1e-4) return true;

        var cross = prevDx * segDy - prevDy * segDx;
        var tol = Math.Sqrt(segLenSq) * 0.01 + 0.5;
        return Math.Abs(cross) <= tol;
    }

    private PixelRegion BuildStampsLinear(
        ActiveStroke stroke,
        BrushPreset brush,
        CanvasInputSample from,
        CanvasInputSample to,
        double distance,
        float velocity01,
        bool ensureEndpoint)
    {
        var dirty = PixelRegion.Empty;
        var consumed = stroke.State.NextStampDistance - stroke.State.DistanceLeftover;
        while (consumed <= distance)
        {
            var ratio = (float)(consumed / distance);
            var sample = LerpCanvas(from, to, ratio);
            var sp = BuildStrokePoint(stroke, sample, velocity01);
            var stamp = CreateStamp(stroke, brush, sp);

            if (!BrushSpacing.IsStampTooSmall(stamp.Size))
            {
                _stamps.Add(stamp);
                dirty = dirty.Union(StampBounds(stamp));
                stroke.State.TotalDistance += stroke.State.NextStampDistance;
                stroke.State.DabSeqNo++;
            }

            stroke.State.NextStampDistance = StampSpacing(brush, stamp);
            consumed += stroke.State.NextStampDistance;
        }

        stroke.State.DistanceLeftover = (float)distance - (consumed - stroke.State.NextStampDistance);
        if (stroke.State.DistanceLeftover >= stroke.State.NextStampDistance)
            stroke.State.DistanceLeftover = 0;

        if (ensureEndpoint && _stamps.Count > 0)
            dirty = dirty.Inflate((int)(_stamps[^1].Size * 0.25f + 1));

        stroke.State.LastX = (float)to.X;
        stroke.State.LastY = (float)to.Y;
        stroke.State.LastPressure = (float)to.Pressure;
        stroke.State.LastTiltX = (float)to.TiltX;
        stroke.State.LastTiltY = (float)to.TiltY;
        return dirty;
    }

    private static StrokePoint BuildStrokePoint(ActiveStroke stroke, CanvasInputSample sample, float velocity01)
        => new(
            x: (float)sample.X, y: (float)sample.Y,
            pressure: (float)sample.Pressure,
            tiltX: (float)sample.TiltX, tiltY: (float)sample.TiltY, twist: (float)sample.Twist,
            drawingAngle: stroke.State.DrawingAngle,
            speed: velocity01,
            totalDistance: stroke.State.TotalDistance,
            dabSeqNo: stroke.State.DabSeqNo,
            random: Hash01((int)(sample.X * 7919f), (int)(sample.Y * 6353f)),
            strokeRandom: stroke.StrokeRandom);

    private static float StampSpacing(BrushPreset brush, StampSample stamp)
        => BrushSpacing.EffectiveDistance(brush, stamp.Size, stamp.SpacingMultiplier);

    private PixelRegion BuildStampsContinuous(
        ActiveStroke stroke,
        BrushPreset brush,
        CanvasInputSample from,
        CanvasInputSample to,
        float velocity01)
    {
        if (!brush.ContinuousSpraying)
            return PixelRegion.Empty;

        var elapsedMs = Math.Max(0, (to.TimeMicros - from.TimeMicros) / 1000.0);
        if (elapsedMs <= 0)
            return PixelRegion.Empty;

        var sp = BuildStrokePoint(stroke, to, velocity01);
        var stamp = CreateStamp(stroke, brush, sp);
        var spacing = StampSpacing(brush, stamp);

        const float referenceVelocity = 1000f;
        var msPerDab = spacing / referenceVelocity * 1000f;
        msPerDab = Math.Clamp(msPerDab, 12, 400);

        stroke.State.TimeLeftoverMs += elapsedMs;
        var dirty = PixelRegion.Empty;
        while (stroke.State.TimeLeftoverMs >= msPerDab)
        {
            stroke.State.TimeLeftoverMs -= msPerDab;
            sp = BuildStrokePoint(stroke, to, velocity01);
            stamp = CreateStamp(stroke, brush, sp);
            if (!BrushSpacing.IsStampTooSmall(stamp.Size))
            {
                _stamps.Add(stamp);
                dirty = dirty.Union(StampBounds(stamp));
                stroke.State.DabSeqNo++;
            }
        }

        stroke.State.LastX = (float)to.X;
        stroke.State.LastY = (float)to.Y;
        stroke.State.LastPressure = (float)to.Pressure;
        stroke.State.LastTiltX = (float)to.TiltX;
        stroke.State.LastTiltY = (float)to.TiltY;
        return dirty;
    }

    private static bool PreferLinearStampPath(BrushPreset brush)
    {
        if (brush.Dynamics.Scatter.IsEnabled)
            return false;

        return brush.Tip switch
        {
            ProceduralBrushTip { Shape: BrushTipShape.Circle or BrushTipShape.SoftRound or BrushTipShape.Ellipse } => true,
            _ => false
        };
    }

    private static StampSample CreateStamp(ActiveStroke stroke, BrushPreset brush, in StrokePoint sp)
    {
        var dyn = brush.Dynamics;
        var paramLookup = stroke.ParamGraphLookup;
        var sizeMul = EvalParameter(paramLookup, BrushParameterTarget.Size, sp, dyn.EvalSize(sp));
        var opacMul = EvalParameter(paramLookup, BrushParameterTarget.Opacity, sp, dyn.EvalOpacity(sp));
        var flowMul = EvalParameter(paramLookup, BrushParameterTarget.Flow, sp, dyn.EvalFlow(sp));
        var hardness = EvalParameter(paramLookup, BrushParameterTarget.Hardness, sp,
            dyn.Hardness.IsEnabled ? dyn.EvalHardness(sp) : (float)brush.Hardness);
        var spacingMul = EvalParameter(paramLookup, BrushParameterTarget.Spacing, sp, dyn.EvalSpacing(sp));
        var tipDensityMul = EvalParameter(paramLookup, BrushParameterTarget.TipDensity, sp,
            dyn.TipDensity.IsEnabled ? dyn.EvalTipDensity(sp) : 1f);
        var tipThicknessMul = EvalParameter(paramLookup, BrushParameterTarget.TipThickness, sp,
            dyn.TipThickness.IsEnabled ? dyn.EvalTipThickness(sp) : 1f);
        var scatter = EvalParameter(paramLookup, BrushParameterTarget.Scatter, sp,
            dyn.Scatter.IsEnabled ? dyn.EvalScatter(sp) : 0f);
        var rotDeg = EvalParameter(paramLookup, BrushParameterTarget.Angle, sp, dyn.EvalRotationDeg(sp));
        var size = Math.Max(0.5f, (float)brush.Size * sizeMul);
        // Opacity is independent of AmountOfPaint. AmountOfPaint controls how much
        // brush color is deposited, not the visibility of the stamp.
        var opacity = (float)Math.Clamp(brush.Opacity * brush.Flow * brush.TipDensity * tipDensityMul * opacMul * flowMul, 0, 1);
        var trajectoryDeg = sp.DrawingAngle * (180f / MathF.PI);
        float directionContrib = brush.BaseAngleSource switch
        {
            AngleSource.DirectionOfLine => trajectoryDeg,
            AngleSource.PenTilt => MathF.Atan2(sp.TiltX, sp.TiltY) * (180f / MathF.PI),
            AngleSource.PenTwist => sp.Twist * (180f / MathF.PI),
            _ => 0f
        };
        var jitter = brush.AngleJitter > 0.001f
            ? (sp.Random * 2f - 1f) * brush.AngleJitter * 180f
            : 0f;
        var angle = (float)brush.Angle + directionContrib + rotDeg + jitter;
        var x = sp.X;
        var y = sp.Y;
        if (scatter > 0.001f)
        {
            var radians = sp.Random * MathF.Tau;
            var amount = (Hash01(sp.DabSeqNo, (int)(sp.StrokeRandom * 100_000f)) * 2f - 1f) * scatter * size;
            x += MathF.Cos(radians) * amount;
            y += MathF.Sin(radians) * amount;
        }

        if (BrushQualityPolicy.SnapStampCenterToPixel(brush.Quality))
        {
            x = MathF.Round(x);
            y = MathF.Round(y);
        }

        return new StampSample(
            x, y, size, opacity, angle,
            Math.Clamp(hardness, 0.001f, 1f),
            Math.Clamp(spacingMul, 0.05f, 4f),
            Math.Clamp(tipThicknessMul, 0.01f, 4f),
            SelectTipIndex(brush, sp));
    }

    private static float EvalParameter(Dictionary<BrushParameterTarget, BrushParameterGraph> lookup, BrushParameterTarget target, in StrokePoint sp, float fallback)
        => lookup.TryGetValue(target, out var graph) ? graph.Evaluate(sp, fallback) : fallback;

    private static int SelectTipIndex(BrushPreset brush, in StrokePoint sp)
    {
        if (brush.TipSelectionMode == BrushTipSelectionMode.Single || brush.Tips.Count <= 1)
            return 0;

        return brush.TipSelectionMode switch
        {
            BrushTipSelectionMode.Sequential => sp.DabSeqNo % brush.Tips.Count,
            BrushTipSelectionMode.Random => Math.Clamp((int)(Hash01(sp.DabSeqNo, (int)(sp.StrokeRandom * 100_000f)) * brush.Tips.Count), 0, brush.Tips.Count - 1),
            _ => 0
        };
    }

    private static double EstimateBrushRadius(BrushPreset brush)
    {
        var maxSize = brush.Size * Math.Max(1.0, brush.Dynamics.Size.MaxOutput);
        var spacing = BrushSpacing.EstimateDistance(brush, (float)maxSize)
            * Math.Max(1.0f, (float)brush.Dynamics.Spacing.MaxOutput);
        var scatter = brush.Dynamics.Scatter.IsEnabled ? maxSize * Math.Max(0.0, brush.Dynamics.Scatter.MaxOutput) : 0.0;
        return Math.Max(1.0, maxSize * 0.75 + spacing + scatter + 3.0);
    }

    private unsafe void RenderStampsViaScratch(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        var w = dirty.Width;
        var h = dirty.Height;

        // Allocate scratch at exact dirty size.
        if (_scratch == null || _scratch.Width < w || _scratch.Height < h
            || _scratch.Width > w * 4 || _scratch.Height > h * 4)
        {
            _scratch?.Dispose();
            _scratch = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        }

        // Render stamps into the scratch with normal alpha compositing.
        using (var sc = new SKCanvas(_scratch))
        {
            sc.Save();
            sc.ClipRect(SKRect.Create(0, 0, w, h));
            sc.Clear(SKColors.Transparent);
            sc.Restore();

            sc.Save();
            sc.Translate(-dirty.X, -dirty.Y);
            sc.ClipRect(SKRect.Create(dirty.X, dirty.Y, w, h));
            stroke.Paint.BlendMode = SKBlendMode.SrcOver;
            RenderPreparedStamps(stroke, brush, sc);
            stroke.Paint.BlendMode = SKBlendMode.SrcOver;
            sc.Restore();
        }

        // Apply grain to the scratch in canvas space before compositing.
        float grain = 0f;
        if (grain > 0f)
        {
            SKBitmap? texBmp = brush.Texture != null ? GetOrLoadTexture(brush.Texture) : null;
            byte* texPx = texBmp != null ? (byte*)texBmp.GetPixels().ToPointer() : null;
            int texW = texBmp?.Width ?? 0, texH = texBmp?.Height ?? 0, texStride = texBmp?.RowBytes ?? 0;

            var ptr = (byte*)_scratch.GetPixels().ToPointer();
            int stride = _scratch.RowBytes;
            if ((long)w * h >= 16 * 1024 && Environment.ProcessorCount > 1)
            {
                Parallel.For(0, h, sy =>
                {
                    int cy = dirty.Y + sy;
                    byte* row = ptr + sy * stride;
                    float noise;
                    for (int sx = 0; sx < w; sx++)
                    {
                        byte a = row[sx * 4 + 3];
                        if (a == 0) continue;
                        if (texPx != null)
                        {
                            int tx = (dirty.X + sx) % texW; if (tx < 0) tx += texW;
                            int ty = cy % texH; if (ty < 0) ty += texH;
                            noise = texPx[ty * texStride + tx] / 255.0f;
                        }
                        else
                            noise = GrainNoise(dirty.X + sx, cy);
                        row[sx * 4 + 3] = (byte)(a * (1f - grain + noise * grain) + 0.5f);
                    }
                });
            }
            else
            {
                for (int sy = 0; sy < h; sy++)
                {
                    int cy = dirty.Y + sy;
                    byte* row = ptr + sy * stride;
                    float noise;
                    for (int sx = 0; sx < w; sx++)
                    {
                        byte a = row[sx * 4 + 3];
                        if (a == 0) continue;
                        if (texPx != null)
                        {
                            int tx = (dirty.X + sx) % texW; if (tx < 0) tx += texW;
                            int ty = cy % texH; if (ty < 0) ty += texH;
                            noise = texPx[ty * texStride + tx] / 255.0f;
                        }
                        else
                            noise = GrainNoise(dirty.X + sx, cy);
                        row[sx * 4 + 3] = (byte)(a * (1f - grain + noise * grain) + 0.5f);
                    }
                }
            }
        }

        // Composite the scratch result onto layer tiles directly — avoids
        // RenderWithSkia's per-tile Skia canvas setup overhead.
        var scratchPtr = (byte*)_scratch.GetPixels().ToPointer();
        CompositeScratchBgraOntoLayer(PaintBuf(layer), dirty, scratchPtr, _scratch.RowBytes,
            layer.IsAlphaLocked && !MaskPaint(layer), SKBlendMode.SrcOver, MaskPaint(layer));
        MarkPaintThumbnailsDirty(layer);
    }

    private unsafe void RenderColorMixStampsViaScratch(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        var w = dirty.Width;
        var h = dirty.Height;
        if (w <= 0 || h <= 0)
            return;

        if ((long)w * h > MaxColorMixScratchPixels)
        {
            _lastRasterPath = "SkiaFallback";
            RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
            return;
        }

        try
        {
            if (_scratch == null || _scratch.Width < w || _scratch.Height < h
                || _scratch.Width > w * 4 || _scratch.Height > h * 4)
            {
                _scratch?.Dispose();
                _scratch = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            }

            if (_scratch == null || _scratch.IsEmpty)
            {
                _lastRasterPath = "SkiaFallback";
                RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
                return;
            }

            var pixels = _scratch.GetPixels();
            if (pixels == IntPtr.Zero)
            {
                _lastRasterPath = "SkiaFallback";
                RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
                return;
            }

            var ptr = (byte*)pixels.ToPointer();
            var stride = _scratch.RowBytes;
            for (var sy = 0; sy < h; sy++)
            {
                var row = ptr + sy * stride;
                for (var sx = 0; sx < w; sx++)
                {
                    var offset = sx * 4;
                    row[offset] = 0;
                    row[offset + 1] = 0;
                    row[offset + 2] = 0;
                    row[offset + 3] = 0;
                }
            }

            // Legacy grain slider is ignored for color-mix rendering — node-graph
            // noise/texture handles grain for brush tips instead.
            float brushGrain = 0f;
            byte* texPx = null;
            int texW = 0, texH = 0, texStride = 0;
            float[]? grainTable = null;
            var blurSoftening = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
            var renderedAny = false;
            var needsSkiaFallback = false;

            // Precompute alpha-softening LUT once per batch instead of running
            // MathF.Pow per pixel (94% blur over a 230² dab = 53k pow() per
            // stamp; the LUT replaces all of those with a byte lookup).
            bool applyBlurSoften = blurSoftening >= 0.35f;
            byte* softenLut = stackalloc byte[256];
            if (applyBlurSoften)
            {
                var soften = (blurSoftening - 0.35f) / 0.65f;
                var exponent = 1f - soften * 0.75f;
                for (int v = 0; v < 256; v++)
                {
                    var a = v / 255f;
                    var transformed = MathF.Pow(a, exponent);
                    softenLut[v] = (byte)Math.Clamp((int)(transformed * 255f + 0.5f), 0, 255);
                }
            }

            bool hasGrainFx = grainTable != null || brushGrain > 0f;

            try
            {
                for (var i = 0; i < _stamps.Count; i++)
                {
                    if (_stampColors.Count <= i) break;
                    var stamp = _stamps[i];
                    var color = _stampColors[i];
                    if (stamp.Opacity <= 0 || stamp.Size <= 0 || color.Alpha == 0) continue;
                    if (!stroke.TryGetCachedDab(stamp, out var dab) || dab.Mask.IsEmpty)
                    {
                        needsSkiaFallback = true;
                        break;
                    }

                    var maskPixels = dab.Mask.GetPixels();
                    if (maskPixels == IntPtr.Zero || dab.Mask.IsEmpty)
                    {
                        needsSkiaFallback = true;
                        break;
                    }

                    renderedAny = true;
                    var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                    var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                    var right = left + dab.LogicalWidth;
                    var bottom = top + dab.LogicalHeight;
                    var pxMinX = Math.Max(left, dirty.X);
                    var pxMinY = Math.Max(top, dirty.Y);
                    var pxMaxX = Math.Min(right, dirty.Right);
                    var pxMaxY = Math.Min(bottom, dirty.Bottom);
                    if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) continue;

                    var maskPtr = (byte*)maskPixels.ToPointer();
                    var maskStride = dab.Mask.RowBytes;
                    var stampOpacityF = stamp.Opacity * color.Alpha / 255f;
                    var useFastMaskPath = !dab.IsScaled && !hasGrainFx && !applyBlurSoften;
                    var dirtyX = dirty.X;
                    var dirtyY = dirty.Y;
                    var colorB = color.Blue;
                    var colorG = color.Green;
                    var colorR = color.Red;

                    for (var py = pxMinY; py < pxMaxY; py++)
                    {
                        var localY = py - top;
                        var scratchY = py - dirtyY;
                        if ((uint)scratchY >= (uint)h) continue;
                        var scratchRow = ptr + scratchY * stride;
                        var maskRow = useFastMaskPath ? maskPtr + localY * maskStride : null;
                        var gy = py - dirtyY;

                        // Fast path: no grain, no blur soften, no scaled mask.
                        if (useFastMaskPath)
                        {
                            for (var px = pxMinX; px < pxMaxX; px++)
                            {
                                var maskA = maskRow![px - left];
                                if (maskA == 0) continue;

                                var stampA = (int)(maskA * stampOpacityF + 0.5f);
                                if (stampA <= 0) continue;
                                if (stampA > 255) stampA = 255;

                                var offset = (px - dirtyX) * 4;
                                var dstA = scratchRow[offset + 3];
                                if (dstA == 0)
                                {
                                    scratchRow[offset + 0] = colorB;
                                    scratchRow[offset + 1] = colorG;
                                    scratchRow[offset + 2] = colorR;
                                    scratchRow[offset + 3] = (byte)stampA;
                                }
                                else
                                {
                                    var srcA = stampA;
                                    var invSrcA = 255 - srcA;
                                    var outA = srcA + (dstA * invSrcA + 127) / 255;
                                    if (outA <= 0) continue;
                                    scratchRow[offset + 0] = (byte)((colorB * srcA + scratchRow[offset + 0] * dstA * invSrcA / 255) / outA);
                                    scratchRow[offset + 1] = (byte)((colorG * srcA + scratchRow[offset + 1] * dstA * invSrcA / 255) / outA);
                                    scratchRow[offset + 2] = (byte)((colorR * srcA + scratchRow[offset + 2] * dstA * invSrcA / 255) / outA);
                                    scratchRow[offset + 3] = (byte)outA;
                                }
                            }
                            continue;
                        }

                        for (var px = pxMinX; px < pxMaxX; px++)
                        {
                            var maskA = SampleMaskAlpha(dab, px - left, localY);
                            if (maskA == 0) continue;

                            byte softenedMask;
                            if (applyBlurSoften)
                                softenedMask = softenLut[maskA];
                            else
                                softenedMask = (byte)maskA;

                            float alphaF;
                            if (hasGrainFx)
                            {
                                var gx = px - dirtyX;
                                var grain = SampleBrushGrain(
                                    px, py, gx, gy, dirty, grainTable, w, h,
                                    brushGrain, texPx, texW, texH, texStride);
                                alphaF = softenedMask * grain;
                            }
                            else
                            {
                                alphaF = softenedMask;
                            }

                            var stampA = (int)(alphaF * stampOpacityF + 0.5f);
                            if (stampA <= 0) continue;
                            if (stampA > 255) stampA = 255;

                            var offset = (px - dirtyX) * 4;
                            var dstA = scratchRow[offset + 3];
                            if (stampA <= 0) continue;

                            if (dstA == 0)
                            {
                                scratchRow[offset + 0] = colorB;
                                scratchRow[offset + 1] = colorG;
                                scratchRow[offset + 2] = colorR;
                                scratchRow[offset + 3] = (byte)stampA;
                            }
                            else
                            {
                                var srcA = stampA;
                                var invSrcA = 255 - srcA;
                                var outA = srcA + (dstA * invSrcA + 127) / 255;
                                if (outA <= 0) continue;
                                scratchRow[offset + 0] = (byte)((colorB * srcA + scratchRow[offset + 0] * dstA * invSrcA / 255) / outA);
                                scratchRow[offset + 1] = (byte)((colorG * srcA + scratchRow[offset + 1] * dstA * invSrcA / 255) / outA);
                                scratchRow[offset + 2] = (byte)((colorR * srcA + scratchRow[offset + 2] * dstA * invSrcA / 255) / outA);
                                scratchRow[offset + 3] = (byte)outA;
                            }
                        }
                    }
                }
            }
            finally
            {
            }

            if (needsSkiaFallback)
            {
                _lastRasterPath = "SkiaFallback";
                RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
                return;
            }

            if (!renderedAny)
                return;

            CompositeScratchBgraOntoLayer(PaintBuf(layer), dirty, ptr, stride,
                layer.IsAlphaLocked && !MaskPaint(layer), SKBlendMode.SrcOver, MaskPaint(layer));
            MarkPaintThumbnailsDirty(layer);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "BrushEngine.RenderColorMixStampsViaScratch", flushToDisk: true);
            _lastRasterPath = "SkiaFallback";
            RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, brush, canvas));
        }
    }

    private static unsafe float SampleBrushGrain(
        int px, int py, int gx, int gy, PixelRegion dirty, float[]? grainTable, int tableW, int tableH,
        float brushGrain, byte* texPx, int texW, int texH, int texStride)
    {
        if (grainTable != null)
        {
            if ((uint)gx < (uint)tableW && (uint)gy < (uint)tableH)
                return grainTable[gy * tableW + gx];
            return 1f;
        }

        if (brushGrain <= 0f)
            return 1f;

        float noise;
        if (texPx != null && texW > 0 && texH > 0)
        {
            var tx = px % texW;
            if (tx < 0) tx += texW;
            var ty = py % texH;
            if (ty < 0) ty += texH;
            noise = texPx[ty * texStride + tx] / 255.0f;
        }
        else
        {
            noise = GrainNoise(px, py);
        }

        return 1f - brushGrain + noise * brushGrain;
    }

    private unsafe void RasterizeStampsDirect(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        var procTip = brush.Tip as ProceduralBrushTip;
        bool isImageTip = brush.Tip is ImageBrushTip or NodeBrushTip { IsDirectImageSampler: true };
        BrushTipNodeGraph? stampGraph = brush.Tip switch
        {
            ProceduralBrushTip p => p.Graph,
            NodeBrushTip n when !n.IsDirectImageSampler => n.Graph,
            _ => null
        };
        bool useGraphEval = stampGraph != null && BrushTipStampContext.CanEvaluate(stampGraph, BrushMaterialTips.ForPreset(brush));
        BrushTipStampFastPath.AlphaAt? fastAlpha = null;
        if (useGraphEval && stampGraph != null &&
            BrushTipStampFastPath.TryCreate(stampGraph, (float)brush.Hardness, out var fast))
            fastAlpha = fast;
        bool isSoft = procTip?.Shape == BrushTipShape.SoftRound;
        bool isCircle = procTip?.Shape is BrushTipShape.Circle or BrushTipShape.SoftRound;
        float aspect = procTip != null && !isCircle
            ? MathF.Max(0.05f, MathF.Min(20f, procTip.AspectRatio))
            : stampGraph?.BuiltInAspectRatio is > 0f and var ar
                ? MathF.Max(0.05f, MathF.Min(20f, ar))
                : 1f;

        _lastRasterPath = fastAlpha != null ? "ProceduralStampFast"
            : useGraphEval ? "ProceduralStamp"
            : "AnalyticalDirect";

        var baseColor = stroke.BaseColor;
        int brushB = baseColor.Blue, brushG = baseColor.Green, brushR = baseColor.Red;
        float baseAlpha = baseColor.Alpha;
        var blendMode = brush.BlendMode;
        float brushGrain = 0f;
        byte* texPx = null!;
        int texW = 0, texH = 0, texStride = 0;
        var grainTable = PrecomputeGrain(dirty, texPx, texW, texH, texStride, brushGrain);

        float brushThickness = MathF.Max(0.01f, MathF.Min(4f, (float)brush.TipThickness));
        bool isHorizontal = brush.TipDirection == BrushTipDirection.Horizontal;
        const int tsz = TiledPixelBuffer.TileSize;

        PaintBuf(layer).EnterPixelWriteLock();
        try
        {
            for (int si = 0; si < _stamps.Count; si++)
            {
                var stamp = _stamps[si];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                BrushTipStampContext? stampEval = null;
                SKBitmap? maskBmp = null;
                if (useGraphEval && stampGraph != null && fastAlpha == null)
                    stampEval = new BrushTipStampContext(stampGraph, stamp.Hardness, BrushMaterialTips.ForPreset(brush));
                try
                {
                    int maskRasterW = BrushTipMaskRasterization.MaskResolutionForStamp(stamp.Size, brush);
                    float halfMaskW = maskRasterW * 0.5f;

                    float thickMul = MathF.Max(0.01f, MathF.Min(4f, brushThickness * stamp.TipThicknessMultiplier));
                    float scale = stamp.Size / MathF.Max(1f, maskRasterW);

                    // scaleX/scaleY: pixels per mask-pixel in each axis (used by image tip path)
                    float scaleX = isHorizontal ? scale : scale * thickMul;
                    float scaleY = isHorizontal ? scale * thickMul : scale;
                    if (brush.FlipHorizontal) scaleX = -scaleX;
                    if (brush.FlipVertical) scaleY = -scaleY;

                    // rx/ry: physical half-axes in pixels (used by procedural path + bbox)
                    float maxR = (maskRasterW * 0.5f - 0.5f) * scale;
                    float rxBase = aspect >= 1f ? maxR : maxR * aspect;
                    float ryBase = aspect >= 1f ? maxR / aspect : maxR;
                    float rx = isHorizontal ? rxBase : rxBase * thickMul;
                    float ry = isHorizontal ? ryBase * thickMul : ryBase;

                    // Image tip: get the cached Alpha8 mask and pin its pixels
                    byte* maskPx = null;
                    int maskStride = 0;
                    if (isImageTip)
                    {
                        maskBmp = stroke.MaskFor(stamp);
                        maskPx = (byte*)maskBmp.GetPixels().ToPointer();
                        maskStride = maskBmp.RowBytes;
                        maskRasterW = maskBmp.Width;
                        halfMaskW = maskRasterW * 0.5f;
                    }

                    // For image tips the effective half-extent is the full mask half-size × scale
                    float bboxHalfX = isImageTip ? halfMaskW * MathF.Abs(scaleX) : rx;
                    float bboxHalfY = isImageTip ? halfMaskW * MathF.Abs(scaleY) : ry;
                    if (bboxHalfX < 0.5f || bboxHalfY < 0.5f) continue;

                    // Always rotate for image tips (texture has directionality); skip for rotationally-symmetric circles
                    bool hasRot = MathF.Abs(stamp.Angle) > 0.1f && (isImageTip || useGraphEval || !isCircle);
                    float cosA = 1f, sinA = 0f;
                    if (hasRot)
                    {
                        float rad = stamp.Angle * MathF.PI / 180f;
                        cosA = MathF.Cos(rad);
                        sinA = MathF.Sin(rad);
                    }

                    float stampOpacity255 = StampOpacity255(blendMode, stamp.Opacity, baseAlpha);
                    if (stampOpacity255 <= 0) continue;

                    // Procedural-only params
                    float hardness = stamp.Hardness;
                    float hardnessRange = 1f - hardness;
                    float h2 = hardness * hardness;
                    bool hardEdge = !isImageTip && hardness >= 0.999f;

                    // Composite strategy — hoist outside pixel loops
                    bool isSrcOver = blendMode == SKBlendMode.SrcOver;
                    bool alphaLocked = layer.IsAlphaLocked;

                    // Grain strategy — precompute base value and nullity
                    float grainBase = 1f - brushGrain;
                    bool hasGrainTable = grainTable != null;
                    bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
                    bool hasTexGrain = hasProceduralGrain && texPx != null;

                    // Tight bounding box — for rotated image tips use the rotated-rectangle formula
                    float boxHX = hasRot ? (bboxHalfX * MathF.Abs(cosA) + bboxHalfY * MathF.Abs(sinA)) : bboxHalfX;
                    float boxHY = hasRot ? (bboxHalfX * MathF.Abs(sinA) + bboxHalfY * MathF.Abs(cosA)) : bboxHalfY;
                    float margin = 1.5f;
                    int bLeft = (int)MathF.Floor(stamp.X - boxHX - margin);
                    int bTop = (int)MathF.Floor(stamp.Y - boxHY - margin);
                    int bRight = (int)MathF.Ceiling(stamp.X + boxHX + margin);
                    int bBottom = (int)MathF.Ceiling(stamp.Y + boxHY + margin);

                    int firstTx = (int)Math.Floor((double)bLeft / tsz);
                    int firstTy = (int)Math.Floor((double)bTop / tsz);
                    int lastTx = (int)Math.Floor((double)(bRight - 1) / tsz);
                    int lastTy = (int)Math.Floor((double)(bBottom - 1) / tsz);

                    for (int ty = firstTy; ty <= lastTy; ty++)
                    {
                        int tilePixY = ty * tsz;
                        int pxMinY = Math.Max(bTop, tilePixY);
                        int pxMaxY = Math.Min(bBottom, tilePixY + tsz);
                        if (pxMinY >= pxMaxY) continue;

                        for (int tx = firstTx; tx <= lastTx; tx++)
                        {
                            int tilePixX = tx * tsz;
                            int pxMinX = Math.Max(bLeft, tilePixX);
                            int pxMaxX = Math.Min(bRight, tilePixX + tsz);
                            if (pxMinX >= pxMaxX) continue;

                            var tile = PaintBuf(layer).GetOrCreateRawTile(tx, ty);

                            for (int py = pxMinY; py < pxMaxY; py++)
                            {
                                int ly = py - tilePixY;
                                int rowBase = ly * tsz * 4;
                                float dy = py + 0.5f - stamp.Y;

                                for (int px = pxMinX; px < pxMaxX; px++)
                                {
                                    float dx = px + 0.5f - stamp.X;

                                    // Apply inverse rotation to get brush-local coords
                                    float fdx, fdy;
                                    if (hasRot)
                                    {
                                        fdx = dx * cosA + dy * sinA;
                                        fdy = -dx * sinA + dy * cosA;
                                    }
                                    else { fdx = dx; fdy = dy; }

                                    float alpha;
                                    if (isImageTip)
                                    {
                                        // Map brush-local coords to mask pixel coords via inverse scale
                                        float mx = fdx / scaleX + halfMaskW;
                                        float my = fdy / scaleY + halfMaskW;
                                        if (mx < 0f || my < 0f || mx >= maskRasterW || my >= maskRasterW) continue;

                                        // Bilinear sample the Alpha8 mask
                                        int ix0 = (int)mx, iy0 = (int)my;
                                        int ix1 = Math.Min(ix0 + 1, maskRasterW - 1);
                                        int iy1 = Math.Min(iy0 + 1, maskRasterW - 1);
                                        float fx = mx - ix0, fy = my - iy0;
                                        float a00 = maskPx[iy0 * maskStride + ix0];
                                        float a10 = maskPx[iy0 * maskStride + ix1];
                                        float a01 = maskPx[iy1 * maskStride + ix0];
                                        float a11 = maskPx[iy1 * maskStride + ix1];
                                        alpha = (a00 + (a10 - a00) * fx + (a01 - a00) * fy + (a00 - a10 - a01 + a11) * fx * fy) / 255f;
                                    }
                                    else if (useGraphEval)
                                    {
                                        float u = 0.5f + fdx / (rx * 2f);
                                        float v = 0.5f + fdy / (ry * 2f);
                                        if (u < 0f || v < 0f || u > 1f || v > 1f) continue;
                                        alpha = fastAlpha != null
                                            ? fastAlpha(u, v)
                                            : stampEval!.EvaluateAlpha(u, v);
                                        if (alpha <= 0f) continue;
                                    }
                                    else
                                    {
                                        // Analytical radial alpha — squared comparison avoids Sqrt for core pixels
                                        float ndx = fdx / rx;
                                        float ndy = fdy / ry;
                                        float t2 = ndx * ndx + ndy * ndy;
                                        if (t2 >= 1f) continue;

                                        if (hardEdge)
                                        {
                                            alpha = 1f;
                                        }
                                        else if (t2 <= h2)
                                        {
                                            alpha = 1f;
                                        }
                                        else
                                        {
                                            float t = MathF.Sqrt(t2);
                                            float fade = hardnessRange > 0.001f ? (t - hardness) / hardnessRange : 1f;
                                            var smooth = fade * fade * (3f - 2f * fade);
                                            var exponent = 1f + hardness * 5f;
                                            alpha = isSoft
                                                ? 1f - MathF.Pow(smooth, exponent * 0.7f)
                                                : 1f - MathF.Pow(smooth, exponent);
                                        }
                                    }

                                    if (hasGrainTable)
                                        alpha *= grainTable![(py - dirty.Y) * dirty.Width + (px - dirty.X)];
                                    else if (hasProceduralGrain)
                                    {
                                        if (texPx != null)
                                            alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                                        else
                                            alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                                    }

                                    int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                                    if (stampA <= 0) continue;
                                    if (stampA > 255) stampA = 255;

                                    int lx = px - tilePixX;
                                    int offset = rowBase + lx * 4;
                                    if (isSrcOver)
                                    {
                                        fixed (byte* tp = tile)
                                            Canvas.Engine.SimdPixelOps.StampSrcOver(tp + offset,
                                                (byte)brushB, (byte)brushG, (byte)brushR, stampA, alphaLocked);
                                    }
                                    else
                                    {
                                        WriteCompositeStamp(tile, offset,
                                            (byte)brushB, (byte)brushG, (byte)brushR, (byte)stampA,
                                            alphaLocked, blendMode);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (maskBmp != null)
                        stroke.ReleaseMask(maskBmp);
                    stampEval?.Dispose();
                }
            }
        }
        finally
        {
            PaintBuf(layer).ExitPixelWriteLock();
        }

        PaintBuf(layer).PruneRegion(dirty);
    }

    private unsafe bool TryRasterizeCachedDabsTileMajor(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        SKBlendMode blendMode,
        int brushB,
        int brushG,
        int brushR,
        float baseAlpha,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        PixelRegion dirty,
        float[]? grainTable,
        bool perTileLighten = false)
    {
        if (_stamps.Count == 0)
            return false;

        const int tsz = TiledPixelBuffer.TileSize;
        _dabBuckets.Clear();
        var buckets = _dabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        try
        {
            for (var i = 0; i < _stamps.Count; i++)
            {
                var stamp = _stamps[i];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                var colorB = brushB;
                var colorG = brushG;
                var colorR = brushR;
                var colorA = baseAlpha;
                if (!UsesMaskOpacity(blendMode) && _stampColors.Count > i)
                {
                    var color = _stampColors[i];
                    if (color.Alpha == 0) continue;
                    colorB = color.Blue;
                    colorG = color.Green;
                    colorR = color.Red;
                    colorA = color.Alpha;
                }

                if (!stroke.TryGetCachedDab(stamp, out var dab))
                {
                    buckets.Clear();
                    return false;
                }
                _lastCachedDabCount++;

                var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                var right = left + dab.LogicalWidth;
                var bottom = top + dab.LogicalHeight;
                if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                    continue;

                var placed = new PlacedDab(stamp, dab, left, top, right, bottom, colorB, colorG, colorR, colorA);
                var firstTx = FloorDiv(left, tsz);
                var firstTy = FloorDiv(top, tsz);
                var lastTx = FloorDiv(right - 1, tsz);
                var lastTy = FloorDiv(bottom - 1, tsz);

                for (var ty = firstTy; ty <= lastTy; ty++)
                {
                    for (var tx = firstTx; tx <= lastTx; tx++)
                    {
                        var tileLeft = tx * tsz;
                        var tileTop = ty * tsz;
                        if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                            continue;

                        int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<PlacedDab>(8);
                            buckets.Add(key, list);
                        }
                        list.Add(placed);
                    }
                }
            }

            if (buckets.Count == 0)
                return true;
            _lastTileBucketCount = buckets.Count;

            PaintBuf(layer).EnterPixelWriteLock();
            try
            {
                foreach (var (key, dabs) in buckets)
                {
                    int tx = (short)key;
                    int ty = (short)(key >> 16);
                    var tile = PaintBuf(layer).GetOrCreateRawTile(tx, ty);
                    var tilePixX = tx * tsz;
                    var tilePixY = ty * tsz;

                    if (perTileLighten)
                    {
                        ApplyCachedDabsLightenToTile(
                            tile, tilePixX, tilePixY, dirty, grainTable, dabs, blendMode,
                            brushGrain, texPx, texW, texH, texStride, layer.IsAlphaLocked);
                    }
                    else
                    {
                        foreach (var placed in dabs)
                        {
                            ApplyCachedDabToTile(
                                tile, tilePixX, tilePixY, dirty, grainTable,
                                placed, blendMode,
                                brushGrain, texPx, texW, texH, texStride,
                                layer.IsAlphaLocked);
                        }
                    }
                }
            }
            finally { PaintBuf(layer).ExitPixelWriteLock(); }

            return true;
        }
        finally
        {
        }
    }

    private unsafe bool TryRasterizeCachedDabsLightenScratch(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        int brushB,
        int brushG,
        int brushR,
        float baseAlpha,
        PixelRegion dirty)
    {
        if (_stamps.Count <= 1)
            return false;

        var w = dirty.Width;
        var h = dirty.Height;
        if (w <= 0 || h <= 0 || (long)w * h > MaxColorMixScratchPixels)
            return false;

        if (_scratch == null || _scratch.Width < w || _scratch.Height < h
            || _scratch.Width > w * 4 || _scratch.Height > h * 4)
        {
            _scratch?.Dispose();
            _scratch = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        }

        if (_scratch == null || _scratch.IsEmpty)
            return false;

        var pixels = _scratch.GetPixels();
        if (pixels == IntPtr.Zero)
            return false;

        var ptr = (byte*)pixels.ToPointer();
        var stride = _scratch.RowBytes;
        for (var sy = 0; sy < h; sy++)
        {
            var row = ptr + sy * stride;
            for (var sx = 0; sx < w; sx++)
            {
                var offset = sx * 4;
                row[offset] = 0;
                row[offset + 1] = 0;
                row[offset + 2] = 0;
                row[offset + 3] = 0;
            }
        }

        var dirtyX = dirty.X;
        var dirtyY = dirty.Y;
        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;
            if (!stroke.TryGetCachedDab(stamp, out var dab) || dab.Mask.IsEmpty)
                return false;
            _lastCachedDabCount++;

            var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
            var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
            var right = left + dab.LogicalWidth;
            var bottom = top + dab.LogicalHeight;
            var pxMinX = Math.Max(left, dirty.X);
            var pxMinY = Math.Max(top, dirty.Y);
            var pxMaxX = Math.Min(right, dirty.Right);
            var pxMaxY = Math.Min(bottom, dirty.Bottom);
            if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) continue;

            var stampOpacity255 = StampOpacity255(brush.BlendMode, stamp.Opacity, baseAlpha);
            if (stampOpacity255 <= 0) continue;

            var useFastMaskPath = !dab.IsScaled;
            byte* maskPtr = null;
            int maskStride = 0;
            if (useFastMaskPath)
            {
                maskPtr = (byte*)dab.Mask.GetPixels().ToPointer();
                maskStride = dab.Mask.RowBytes;
            }

            for (var py = pxMinY; py < pxMaxY; py++)
            {
                var localY = py - top;
                var scratchY = py - dirtyY;
                if ((uint)scratchY >= (uint)h) continue;
                var scratchRow = ptr + scratchY * stride;
                var maskRow = useFastMaskPath ? maskPtr + localY * maskStride : null;

                for (var px = pxMinX; px < pxMaxX; px++)
                {
                    int maskA = useFastMaskPath
                        ? maskRow![px - left]
                        : SampleMaskAlpha(dab, px - left, localY);
                    if (maskA == 0) continue;

                    var stampA = (int)(maskA / 255f * stampOpacity255 + 0.5f);
                    if (stampA <= 0) continue;
                    if (stampA > 255) stampA = 255;

                    var offset = (px - dirtyX) * 4;
                    var mergedA = AccumulateSrcOverAlpha(scratchRow[offset + 3], stampA);
                    if (mergedA > 0)
                    {
                        scratchRow[offset + 0] = (byte)brushB;
                        scratchRow[offset + 1] = (byte)brushG;
                        scratchRow[offset + 2] = (byte)brushR;
                        scratchRow[offset + 3] = mergedA;
                    }
                }
            }
        }

        CompositeScratchBgraOntoLayer(PaintBuf(layer), dirty, ptr, stride,
            layer.IsAlphaLocked && !MaskPaint(layer), SKBlendMode.SrcOver, MaskPaint(layer));
        return true;
    }

    private static bool PreferStrokeMaskComposite(DrawingLayer layer, BrushPreset brush)
        => layer.ActivePixels.LiveStroke
            && PreferCachedTileMajorRaster(brush)
            && brush.BlendMode == SKBlendMode.SrcOver
            && !brush.ColorMix;

    private unsafe bool TryAccumulateCachedDabsToStrokeMask(
        ActiveStroke stroke,
        BrushPreset brush,
        SKBlendMode blendMode,
        int brushB,
        int brushG,
        int brushR,
        float baseAlpha,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        PixelRegion dirty,
        float[]? grainTable)
    {
        if (stroke.BeforeTiles == null || _stamps.Count == 0)
            return false;

        const int tsz = TiledPixelBuffer.TileSize;
        _dabBuckets.Clear();
        var buckets = _dabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

            var colorB = brushB;
            var colorG = brushG;
            var colorR = brushR;
            var colorA = baseAlpha;
            if (!UsesMaskOpacity(blendMode) && _stampColors.Count > i)
            {
                var color = _stampColors[i];
                if (color.Alpha == 0) continue;
                colorB = color.Blue;
                colorG = color.Green;
                colorR = color.Red;
                colorA = color.Alpha;
            }

            if (!stroke.TryGetCachedDab(stamp, out var dab))
            {
                buckets.Clear();
                return false;
            }
            _lastCachedDabCount++;

            var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
            var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
            var right = left + dab.LogicalWidth;
            var bottom = top + dab.LogicalHeight;
            if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                continue;

            var placed = new PlacedDab(stamp, dab, left, top, right, bottom, colorB, colorG, colorR, colorA);
            var firstTx = FloorDiv(left, tsz);
            var firstTy = FloorDiv(top, tsz);
            var lastTx = FloorDiv(right - 1, tsz);
            var lastTy = FloorDiv(bottom - 1, tsz);

            for (var ty = firstTy; ty <= lastTy; ty++)
            {
                for (var tx = firstTx; tx <= lastTx; tx++)
                {
                    var tileLeft = tx * tsz;
                    var tileTop = ty * tsz;
                    if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                        continue;

                    int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<PlacedDab>(8);
                        buckets.Add(key, list);
                    }
                    list.Add(placed);
                }
            }
        }

        if (buckets.Count == 0)
            return true;
        _lastTileBucketCount = buckets.Count;

        foreach (var (key, dabs) in buckets)
        {
            int tx = (short)key;
            int ty = (short)(key >> 16);
            var tilePixX = tx * tsz;
            var tilePixY = ty * tsz;
            MaxCachedDabsIntoStrokeMask(stroke, tx, ty, tilePixX, tilePixY, dirty, grainTable, dabs, blendMode,
                brushGrain, texPx, texW, texH, texStride);
        }

        return true;
    }

    private static unsafe void MaxCachedDabsIntoStrokeMask(
        ActiveStroke stroke,
        int tx,
        int ty,
        int tilePixX,
        int tilePixY,
        PixelRegion dirty,
        float[]? grainTable,
        List<PlacedDab> dabs,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride)
    {
        const int tsz = TiledPixelBuffer.TileSize;
        var tileKey = (tx, ty);
        if (!stroke.StrokeMaskTiles.TryGetValue(tileKey, out var maskTile))
        {
            maskTile = new byte[tsz * tsz];
            stroke.StrokeMaskTiles[tileKey] = maskTile;
        }

        float grainBase = 1f - brushGrain;
        bool hasGrainTable = grainTable != null;
        bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
        bool hasTexGrain = hasProceduralGrain && texPx != null;

        foreach (var placed in dabs)
        {
            float stampOpacity255 = StampOpacity255(blendMode, placed.Stamp.Opacity, placed.ColorA);
            if (stampOpacity255 <= 0) continue;

            var pxMinX = Math.Max(placed.Left, tilePixX);
            var pxMinY = Math.Max(placed.Top, tilePixY);
            var pxMaxX = Math.Min(placed.Right, tilePixX + tsz);
            var pxMaxY = Math.Min(placed.Bottom, tilePixY + tsz);
            if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) continue;

            var useFastPath = !placed.Dab.IsScaled && !hasGrainTable && brushGrain <= 0f;
            var maskPtr = (byte*)placed.Dab.Mask.GetPixels().ToPointer();
            var maskStride = placed.Dab.Mask.RowBytes;

            for (int py = pxMinY; py < pxMaxY; py++)
            {
                var localY = py - placed.Top;
                var ly = py - tilePixY;
                var maskRow = useFastPath ? maskPtr + localY * maskStride : null;

                if (useFastPath)
                {
                    for (int px = pxMinX; px < pxMaxX; px++)
                    {
                        int maskA = maskRow![px - placed.Left];
                        if (maskA == 0) continue;

                        int stampA = (int)(maskA / 255f * stampOpacity255 + 0.5f);
                        if (stampA <= 0) continue;
                        if (stampA > 255) stampA = 255;

                        var idx = ly * tsz + (px - tilePixX);
                        maskTile[idx] = AccumulateSrcOverAlpha(maskTile[idx], stampA);
                    }
                    continue;
                }

                for (int px = pxMinX; px < pxMaxX; px++)
                {
                    int maskA = SampleMaskAlpha(placed.Dab, px - placed.Left, localY);
                    if (maskA == 0) continue;

                    float alpha = maskA / 255f;
                    if (hasGrainTable)
                    {
                        int gy = py - dirty.Y, gx = px - dirty.X;
                        if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                            alpha *= grainTable![gy * dirty.Width + gx];
                    }
                    else if (hasProceduralGrain)
                    {
                        if (hasTexGrain)
                            alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                        else
                            alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                    }

                    int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                    if (stampA <= 0) continue;
                    if (stampA > 255) stampA = 255;

                    var idx = ly * tsz + (px - tilePixX);
                    maskTile[idx] = AccumulateSrcOverAlpha(maskTile[idx], stampA);
                }
            }
        }
    }

    private static unsafe void ApplyStrokeMaskToLayer(
        DrawingLayer layer,
        ActiveStroke stroke,
        PixelRegion dirty,
        int brushB,
        int brushG,
        int brushR,
        SKBlendMode blendMode)
    {
        if (dirty.IsEmpty || stroke.BeforeTiles == null)
            return;

        const int tsz = TiledPixelBuffer.TileSize;
        var beforeTiles = stroke.BeforeTiles;
        var maskTiles = stroke.StrokeMaskTiles;
        var alphaLocked = layer.IsAlphaLocked;
        var maskMode = MaskPaint(layer);
        var pixels = PaintBuf(layer);

        pixels.EnterPixelWriteLock();
        try
        {
            var firstTx = FloorDiv(dirty.X, tsz);
            var firstTy = FloorDiv(dirty.Y, tsz);
            var lastTx = FloorDiv(dirty.Right - 1, tsz);
            var lastTy = FloorDiv(dirty.Bottom - 1, tsz);

            for (var ty = firstTy; ty <= lastTy; ty++)
            {
                var tilePixY = ty * tsz;
                for (var tx = firstTx; tx <= lastTx; tx++)
                {
                    var tilePixX = tx * tsz;
                    var pxMinX = Math.Max(dirty.X, tilePixX);
                    var pxMinY = Math.Max(dirty.Y, tilePixY);
                    var pxMaxX = Math.Min(dirty.Right, tilePixX + tsz);
                    var pxMaxY = Math.Min(dirty.Bottom, tilePixY + tsz);
                    if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) continue;

                    var tile = pixels.GetOrCreateRawTile(tx, ty);
                    beforeTiles.TryGetValue((tx, ty), out var beforeTile);
                    maskTiles.TryGetValue((tx, ty), out var maskTile);

                    for (var py = pxMinY; py < pxMaxY; py++)
                    {
                        var ly = py - tilePixY;
                        var rowBase = ly * tsz * 4;
                        for (var px = pxMinX; px < pxMaxX; px++)
                        {
                            var lx = px - tilePixX;
                            var dstOffset = rowBase + lx * 4;
                            var maskA = maskTile != null ? maskTile[ly * tsz + lx] : (byte)0;

                            byte beforeB = 0, beforeG = 0, beforeR = 0, beforeA = 0;
                            if (beforeTile != null)
                            {
                                var beforeOffset = rowBase + lx * 4;
                                beforeB = beforeTile[beforeOffset];
                                beforeG = beforeTile[beforeOffset + 1];
                                beforeR = beforeTile[beforeOffset + 2];
                                beforeA = beforeTile[beforeOffset + 3];
                            }

                            if (maskA == 0)
                            {
                                if (beforeTile != null)
                                {
                                    tile[dstOffset] = beforeB;
                                    tile[dstOffset + 1] = beforeG;
                                    tile[dstOffset + 2] = beforeR;
                                    tile[dstOffset + 3] = beforeA;
                                }
                                continue;
                            }

                            if (maskMode)
                            {
                                WriteCompositeStamp(tile, dstOffset, (byte)brushB, (byte)brushG, (byte)brushR, maskA,
                                    alphaLocked, blendMode, maskMode: true);
                                continue;
                            }

                            fixed (byte* tp = tile)
                            {
                                tp[dstOffset] = beforeB;
                                tp[dstOffset + 1] = beforeG;
                                tp[dstOffset + 2] = beforeR;
                                tp[dstOffset + 3] = beforeA;
                                if (blendMode == SKBlendMode.SrcOver)
                                    Canvas.Engine.SimdPixelOps.StampSrcOver(tp + dstOffset,
                                        (byte)brushB, (byte)brushG, (byte)brushR, maskA, alphaLocked);
                                else
                                    WriteCompositeStamp(tile, dstOffset,
                                        (byte)brushB, (byte)brushG, (byte)brushR, maskA, alphaLocked, blendMode);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            pixels.ExitPixelWriteLock();
        }
    }

    private unsafe bool TryRasterizeCachedColorDabsTileMajor(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        PixelRegion dirty,
        float[]? grainTable)
    {
        if (_stamps.Count == 0)
            return false;

        const int tsz = TiledPixelBuffer.TileSize;
        _colorDabBuckets.Clear();
        var buckets = _colorDabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        try
        {
            for (var i = 0; i < _stamps.Count; i++)
            {
                var stamp = _stamps[i];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                if (!stroke.TryGetCachedColorDab(stamp, out var dab))
                {
                    buckets.Clear();
                    return false;
                }
                _lastCachedDabCount++;

                var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                var right = left + dab.LogicalWidth;
                var bottom = top + dab.LogicalHeight;
                if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                    continue;

                var placed = new PlacedColorDab(stamp, dab, left, top, right, bottom);
                var firstTx = FloorDiv(left, tsz);
                var firstTy = FloorDiv(top, tsz);
                var lastTx = FloorDiv(right - 1, tsz);
                var lastTy = FloorDiv(bottom - 1, tsz);

                for (var ty = firstTy; ty <= lastTy; ty++)
                {
                    for (var tx = firstTx; tx <= lastTx; tx++)
                    {
                        var tileLeft = tx * tsz;
                        var tileTop = ty * tsz;
                        if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                            continue;

                        int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<PlacedColorDab>(8);
                            buckets.Add(key, list);
                        }
                        list.Add(placed);
                    }
                }
            }

            if (buckets.Count == 0)
                return true;
            _lastTileBucketCount = buckets.Count;

            PaintBuf(layer).EnterPixelWriteLock();
            try
            {
                foreach (var (key, dabs) in buckets)
                {
                    int tx = (short)key;
                    int ty = (short)(key >> 16);
                    var tile = PaintBuf(layer).GetOrCreateRawTile(tx, ty);
                    var tilePixX = tx * tsz;
                    var tilePixY = ty * tsz;

                    foreach (var placed in dabs)
                    {
                        ApplyCachedColorDabToTile(
                            tile, tilePixX, tilePixY, dirty, grainTable,
                            placed, blendMode,
                            brushGrain, texPx, texW, texH, texStride,
                            layer.IsAlphaLocked);
                    }
                }
            }
            finally { PaintBuf(layer).ExitPixelWriteLock(); }

            return true;
        }
        finally
        {
        }
    }

    private static unsafe void ApplyCachedColorDabToTile(
        byte[] tile,
        int tilePixX,
        int tilePixY,
        PixelRegion dirty,
        float[]? grainTable,
        PlacedColorDab placed,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        bool alphaLocked)
    {
        var stamp = placed.Stamp;
        var opacity = stamp.Opacity;
        if (opacity <= 0) return;

        const int tsz = TiledPixelBuffer.TileSize;
        var pxMinX = Math.Max(placed.Left, tilePixX);
        var pxMinY = Math.Max(placed.Top, tilePixY);
        var pxMaxX = Math.Min(placed.Right, tilePixX + tsz);
        var pxMaxY = Math.Min(placed.Bottom, tilePixY + tsz);
        if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) return;

        var srcPtr = (byte*)placed.Dab.Bitmap.GetPixels().ToPointer();
        var srcStride = placed.Dab.Bitmap.RowBytes;
        var useFastPath = !placed.Dab.IsScaled && grainTable == null && brushGrain <= 0f;

        bool isSrcOver = blendMode == SKBlendMode.SrcOver;
        float grainBase = 1f - brushGrain;
        bool hasGrainTable = grainTable != null;
        bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
        bool hasTexGrain = hasProceduralGrain && texPx != null;

        for (int py = pxMinY; py < pxMaxY; py++)
        {
            var localY = py - placed.Top;
            var ly = py - tilePixY;
            var rowBase = ly * tsz * 4;
            var srcRow = useFastPath ? srcPtr + localY * srcStride : null;

            for (int px = pxMinX; px < pxMaxX; px++)
            {
                byte srcB, srcG, srcR, srcA;
                if (useFastPath)
                {
                    var srcOffset = (px - placed.Left) * 4;
                    srcB = srcRow![srcOffset];
                    srcG = srcRow[srcOffset + 1];
                    srcR = srcRow[srcOffset + 2];
                    srcA = srcRow[srcOffset + 3];
                }
                else
                {
                    SampleColorDabPixel(placed.Dab, px - placed.Left, localY, out srcB, out srcG, out srcR, out srcA);
                }
                if (srcA == 0) continue;

                float alpha = srcA / 255f;
                if (hasGrainTable)
                {
                    int gy = py - dirty.Y, gx = px - dirty.X;
                    if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                        alpha *= grainTable![gy * dirty.Width + gx];
                }
                else if (hasProceduralGrain)
                {
                    if (hasTexGrain)
                        alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                    else
                        alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                }

                int stampA = (int)(alpha * opacity * 255f + 0.5f);
                if (stampA <= 0) continue;
                if (stampA > 255) stampA = 255;

                var lx = px - tilePixX;
                var offset = rowBase + lx * 4;
                if (isSrcOver)
                {
                    fixed (byte* tp = tile)
                        Canvas.Engine.SimdPixelOps.StampSrcOver(tp + offset,
                            srcB, srcG, srcR, stampA, alphaLocked);
                }
                else
                {
                    WriteCompositeStamp(tile, offset, srcB, srcG, srcR, (byte)stampA, alphaLocked, blendMode);
                }
            }
        }
    }

    private static unsafe int SampleMaskAlpha(ActiveStroke.CachedDab dab, int localX, int localY)
    {
        if ((uint)localX >= (uint)dab.LogicalWidth || (uint)localY >= (uint)dab.LogicalHeight || dab.Mask.IsEmpty)
            return 0;

        var maskPtr = (byte*)dab.Mask.GetPixels().ToPointer();
        var maskW = dab.Mask.Width;
        var maskH = dab.Mask.Height;
        var stride = dab.Mask.RowBytes;
        if (!dab.IsScaled)
            return maskPtr[localY * stride + localX];

        var fx = localX / dab.MaskScaleX;
        var fy = localY / dab.MaskScaleY;
        if (fx < 0f || fy < 0f || fx >= maskW || fy >= maskH)
            return 0;

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, maskW - 1);
        var y1 = Math.Min(y0 + 1, maskH - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var a00 = maskPtr[y0 * stride + x0];
        var a10 = maskPtr[y0 * stride + x1];
        var a01 = maskPtr[y1 * stride + x0];
        var a11 = maskPtr[y1 * stride + x1];
        var top = a00 + (a10 - a00) * tx;
        var bottom = a01 + (a11 - a01) * tx;
        return (int)(top + (bottom - top) * ty + 0.5f);
    }

    private static unsafe void SampleColorDabPixel(
        ActiveStroke.CachedColorDab dab,
        int localX,
        int localY,
        out byte b,
        out byte g,
        out byte r,
        out byte a)
    {
        b = g = r = a = 0;
        if ((uint)localX >= (uint)dab.LogicalWidth || (uint)localY >= (uint)dab.LogicalHeight || dab.Bitmap.IsEmpty)
            return;

        var srcPtr = (byte*)dab.Bitmap.GetPixels().ToPointer();
        var srcW = dab.Bitmap.Width;
        var srcH = dab.Bitmap.Height;
        var stride = dab.Bitmap.RowBytes;
        if (!dab.IsScaled)
        {
            var offset = localY * stride + localX * 4;
            b = srcPtr[offset];
            g = srcPtr[offset + 1];
            r = srcPtr[offset + 2];
            a = srcPtr[offset + 3];
            return;
        }

        var fx = localX / dab.MaskScaleX;
        var fy = localY / dab.MaskScaleY;
        BilinearSampleBgraUnpremul(srcPtr, srcW, srcH, stride, fx, fy, out b, out g, out r, out a);
    }

    /// <summary>Bilinear filter in premul space, output unpremul BGRA (avoids dark fringes).</summary>
    private static unsafe void BilinearSampleBgraUnpremul(
        byte* src,
        int srcW,
        int srcH,
        int stride,
        float fx,
        float fy,
        out byte b,
        out byte g,
        out byte r,
        out byte a)
    {
        b = g = r = a = 0;
        if (fx < 0f || fy < 0f || fx >= srcW || fy >= srcH)
            return;

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, srcW - 1);
        var y1 = Math.Min(y0 + 1, srcH - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        static void ReadPremul(byte* p, int offset, out float pb, out float pg, out float pr, out float pa)
        {
            pa = p[offset + 3] / 255f;
            pb = p[offset + 0] / 255f * pa;
            pg = p[offset + 1] / 255f * pa;
            pr = p[offset + 2] / 255f * pa;
        }

        ReadPremul(src, y0 * stride + x0 * 4, out var pb00, out var pg00, out var pr00, out var pa00);
        ReadPremul(src, y0 * stride + x1 * 4, out var pb10, out var pg10, out var pr10, out var pa10);
        ReadPremul(src, y1 * stride + x0 * 4, out var pb01, out var pg01, out var pr01, out var pa01);
        ReadPremul(src, y1 * stride + x1 * 4, out var pb11, out var pg11, out var pr11, out var pa11);

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        var pb = Lerp(Lerp(pb00, pb10, tx), Lerp(pb01, pb11, tx), ty);
        var pg = Lerp(Lerp(pg00, pg10, tx), Lerp(pg01, pg11, tx), ty);
        var pr = Lerp(Lerp(pr00, pr10, tx), Lerp(pr01, pr11, tx), ty);
        var pa = Lerp(Lerp(pa00, pa10, tx), Lerp(pa01, pa11, tx), ty);
        if (pa <= 1e-5f) return;

        b = (byte)Math.Clamp(pb / pa * 255f + 0.5f, 0, 255);
        g = (byte)Math.Clamp(pg / pa * 255f + 0.5f, 0, 255);
        r = (byte)Math.Clamp(pr / pa * 255f + 0.5f, 0, 255);
        a = (byte)Math.Clamp(pa * 255f + 0.5f, 0, 255);
    }

    private static unsafe void ApplyCachedDabToTile(
        byte[] tile,
        int tilePixX,
        int tilePixY,
        PixelRegion dirty,
        float[]? grainTable,
        PlacedDab placed,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        bool alphaLocked)
    {
        var stamp = placed.Stamp;
        float stampOpacity255 = StampOpacity255(blendMode, stamp.Opacity, placed.ColorA);
        if (stampOpacity255 <= 0) return;

        const int tsz = TiledPixelBuffer.TileSize;
        var pxMinX = Math.Max(placed.Left, tilePixX);
        var pxMinY = Math.Max(placed.Top, tilePixY);
        var pxMaxX = Math.Min(placed.Right, tilePixX + tsz);
        var pxMaxY = Math.Min(placed.Bottom, tilePixY + tsz);
        if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) return;

        var maskPtr = (byte*)placed.Dab.Mask.GetPixels().ToPointer();
        var maskStride = placed.Dab.Mask.RowBytes;
        var useFastPath = !placed.Dab.IsScaled && grainTable == null && brushGrain <= 0f;

        bool isSrcOver = blendMode == SKBlendMode.SrcOver;
        float grainBase = 1f - brushGrain;
        bool hasGrainTable = grainTable != null;
        bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
        bool hasTexGrain = hasProceduralGrain && texPx != null;
        int brushB = placed.ColorB, brushG = placed.ColorG, brushR = placed.ColorR;

        for (int py = pxMinY; py < pxMaxY; py++)
        {
            var localY = py - placed.Top;
            var ly = py - tilePixY;
            var rowBase = ly * tsz * 4;

            // fast path: row-level premultiplied stamp blend, no per-pixel call overhead
            if (useFastPath && isSrcOver)
            {
                var maskRow = maskPtr + localY * maskStride;
                var dstOffset = rowBase + (pxMinX - tilePixX) * 4;
                var maskOffset = pxMinX - placed.Left;
                var opacity255 = (int)(stampOpacity255 + 0.5f);
                fixed (byte* tp = tile)
                    Canvas.Engine.SimdPixelOps.StampSrcOverMaskedRow(
                        tp + dstOffset, maskRow + maskOffset,
                        pxMaxX - pxMinX, brushB, brushG, brushR, opacity255, alphaLocked);
                continue;
            }

            for (int px = pxMinX; px < pxMaxX; px++)
            {
                int maskA = SampleMaskAlpha(placed.Dab, px - placed.Left, localY);
                if (maskA == 0) continue;

                float alpha = maskA / 255f;
                if (hasGrainTable)
                {
                    int gy = py - dirty.Y, gx = px - dirty.X;
                    if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                        alpha *= grainTable![gy * dirty.Width + gx];
                }
                else if (hasProceduralGrain)
                {
                    if (hasTexGrain)
                        alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                    else
                        alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                }

                int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                if (stampA <= 0) continue;
                if (stampA > 255) stampA = 255;

                var lx = px - tilePixX;
                var offset = rowBase + lx * 4;
                if (isSrcOver)
                {
                    fixed (byte* tp = tile)
                        Canvas.Engine.SimdPixelOps.StampSrcOver(tp + offset,
                            (byte)brushB, (byte)brushG, (byte)brushR, stampA, alphaLocked);
                }
                else
                {
                    WriteCompositeStamp(tile, offset,
                        (byte)brushB, (byte)brushG, (byte)brushR, (byte)stampA,
                        alphaLocked, blendMode);
                }
            }
        }
    }

    /// <summary>
    /// Merge all dabs touching one 64×64 layer tile via src-over alpha, then composite once.
    /// Avoids per-dab layer compounding when large brush stamps overlap within a batch.
    /// </summary>
    private static unsafe void ApplyCachedDabsLightenToTile(
        byte[] tile,
        int tilePixX,
        int tilePixY,
        PixelRegion dirty,
        float[]? grainTable,
        List<PlacedDab> dabs,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        bool alphaLocked)
    {
        const int tsz = TiledPixelBuffer.TileSize;
        Span<byte> scratchSpan = stackalloc byte[tsz * tsz * 4];
        scratchSpan.Clear();

        bool isSrcOver = blendMode == SKBlendMode.SrcOver;
        float grainBase = 1f - brushGrain;
        bool hasGrainTable = grainTable != null;
        bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
        bool hasTexGrain = hasProceduralGrain && texPx != null;

        fixed (byte* scratch = scratchSpan)
        {
            foreach (var placed in dabs)
            {
                float stampOpacity255 = StampOpacity255(blendMode, placed.Stamp.Opacity, placed.ColorA);
                if (stampOpacity255 <= 0) continue;

                var pxMinX = Math.Max(placed.Left, tilePixX);
                var pxMinY = Math.Max(placed.Top, tilePixY);
                var pxMaxX = Math.Min(placed.Right, tilePixX + tsz);
                var pxMaxY = Math.Min(placed.Bottom, tilePixY + tsz);
                if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) continue;

                int brushB = placed.ColorB, brushG = placed.ColorG, brushR = placed.ColorR;
                var useFastPath = !placed.Dab.IsScaled && !hasGrainTable && brushGrain <= 0f;
                var maskPtr = (byte*)placed.Dab.Mask.GetPixels().ToPointer();
                var maskStride = placed.Dab.Mask.RowBytes;

                for (int py = pxMinY; py < pxMaxY; py++)
                {
                    var localY = py - placed.Top;
                    var ly = py - tilePixY;
                    var scratchRow = scratch + ly * tsz * 4;
                    var maskRow = useFastPath ? maskPtr + localY * maskStride : null;

                    if (useFastPath)
                    {
                        for (int px = pxMinX; px < pxMaxX; px++)
                        {
                            int maskA = maskRow![px - placed.Left];
                            if (maskA == 0) continue;

                            int stampA = (int)(maskA / 255f * stampOpacity255 + 0.5f);
                            if (stampA <= 0) continue;
                            if (stampA > 255) stampA = 255;

                            var offset = (px - tilePixX) * 4;
                            var mergedA = AccumulateSrcOverAlpha(scratchRow[offset + 3], stampA);
                            if (mergedA > 0)
                            {
                                scratchRow[offset + 0] = (byte)brushB;
                                scratchRow[offset + 1] = (byte)brushG;
                                scratchRow[offset + 2] = (byte)brushR;
                                scratchRow[offset + 3] = mergedA;
                            }
                        }
                        continue;
                    }

                    for (int px = pxMinX; px < pxMaxX; px++)
                    {
                        int maskA = SampleMaskAlpha(placed.Dab, px - placed.Left, localY);
                        if (maskA == 0) continue;

                        float alpha = maskA / 255f;
                        if (hasGrainTable)
                        {
                            int gy = py - dirty.Y, gx = px - dirty.X;
                            if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                                alpha *= grainTable![gy * dirty.Width + gx];
                        }
                        else if (hasProceduralGrain)
                        {
                            if (hasTexGrain)
                                alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                            else
                                alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                        }

                        int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                        if (stampA <= 0) continue;
                        if (stampA > 255) stampA = 255;

                        var offset = (px - tilePixX) * 4;
                        var mergedA = AccumulateSrcOverAlpha(scratchRow[offset + 3], stampA);
                        if (mergedA > 0)
                        {
                            scratchRow[offset + 0] = (byte)brushB;
                            scratchRow[offset + 1] = (byte)brushG;
                            scratchRow[offset + 2] = (byte)brushR;
                            scratchRow[offset + 3] = mergedA;
                        }
                    }
                }
            }

            fixed (byte* tp = tile)
            {
                for (int ly = 0; ly < tsz; ly++)
                {
                    var rowBase = ly * tsz * 4;
                    var scratchRow = scratch + ly * tsz * 4;
                    for (int lx = 0; lx < tsz; lx++)
                    {
                        var scratchOffset = lx * 4;
                        var stampA = scratchRow[scratchOffset + 3];
                        if (stampA == 0) continue;

                        var dstOffset = rowBase + lx * 4;
                        if (isSrcOver)
                        {
                            Canvas.Engine.SimdPixelOps.StampSrcOver(tp + dstOffset,
                                scratchRow[scratchOffset + 0],
                                scratchRow[scratchOffset + 1],
                                scratchRow[scratchOffset + 2],
                                stampA, alphaLocked);
                        }
                        else
                        {
                            WriteCompositeStamp(tile, dstOffset,
                                scratchRow[scratchOffset + 0],
                                scratchRow[scratchOffset + 1],
                                scratchRow[scratchOffset + 2],
                                stampA, alphaLocked, blendMode);
                        }
                    }
                }
            }
        }
    }

    private unsafe bool TryRasterizeCachedDab(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        StampSample stamp,
        SKBlendMode blendMode,
        int brushB,
        int brushG,
        int brushR,
        float baseAlpha,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        float[]? grainTable,
        PixelRegion dirty)
    {
        if (!stroke.TryGetCachedDab(stamp, out var dab))
            return false;
        _lastCachedDabCount++;

        float stampOpacity255 = StampOpacity255(blendMode, stamp.Opacity, baseAlpha);
        if (stampOpacity255 <= 0) return true;

        var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
        var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
        var right = left + dab.LogicalWidth;
        var bottom = top + dab.LogicalHeight;
        const int tsz = TiledPixelBuffer.TileSize;

        var maskPtr = (byte*)dab.Mask.GetPixels().ToPointer();
        var maskStride = dab.Mask.RowBytes;
        var useFastPath = !dab.IsScaled && grainTable == null && brushGrain <= 0f;

        int firstTx = (int)Math.Floor((double)left / tsz);
        int firstTy = (int)Math.Floor((double)top / tsz);
        int lastTx = (int)Math.Floor((double)(right - 1) / tsz);
        int lastTy = (int)Math.Floor((double)(bottom - 1) / tsz);

        PaintBuf(layer).EnterPixelWriteLock();
        try
        {
            for (int ty = firstTy; ty <= lastTy; ty++)
            {
                int tilePixY = ty * tsz;
                int pxMinY = Math.Max(top, tilePixY);
                int pxMaxY = Math.Min(bottom, tilePixY + tsz);
                if (pxMinY >= pxMaxY) continue;

                for (int tx = firstTx; tx <= lastTx; tx++)
                {
                    int tilePixX = tx * tsz;
                    int pxMinX = Math.Max(left, tilePixX);
                    int pxMaxX = Math.Min(right, tilePixX + tsz);
                    if (pxMinX >= pxMaxX) continue;

                    var tile = PaintBuf(layer).GetOrCreateRawTile(tx, ty);

                    for (int py = pxMinY; py < pxMaxY; py++)
                    {
                        int localY = py - top;
                        int ly = py - tilePixY;
                        int rowBase = ly * tsz * 4;
                        var maskRow = useFastPath ? maskPtr + localY * maskStride : null;

                        for (int px = pxMinX; px < pxMaxX; px++)
                        {
                            int maskA = useFastPath
                                ? maskRow![px - left]
                                : SampleMaskAlpha(dab, px - left, localY);
                            if (maskA == 0) continue;

                            float alpha = maskA / 255f;
                            if (grainTable != null)
                            {
                                int gy = py - dirty.Y, gx = px - dirty.X;
                                if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                                    alpha *= grainTable[gy * dirty.Width + gx];
                            }
                            else if (brushGrain > 0f)
                            {
                                float noise;
                                if (texPx != null)
                                {
                                    int gtx = px % texW; if (gtx < 0) gtx += texW;
                                    int gty = py % texH; if (gty < 0) gty += texH;
                                    noise = texPx[gty * texStride + gtx] / 255.0f;
                                }
                                else
                                    noise = GrainNoise(px, py);
                                alpha *= 1f - brushGrain + noise * brushGrain;
                            }

                            int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                            if (stampA <= 0) continue;
                            if (stampA > 255) stampA = 255;

                            int lx = px - tilePixX;
                            int offset = rowBase + lx * 4;
                            WriteCompositeStamp(tile, offset,
                                (byte)brushB, (byte)brushG, (byte)brushR, (byte)stampA,
                                layer.IsAlphaLocked, blendMode);
                        }
                    }
                }
            }
        }
        finally { PaintBuf(layer).ExitPixelWriteLock(); }

        return true;
    }

    private static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static void ApplyBrushQualityToPaint(SKPaint paint, BrushQuality quality)
    {
        paint.IsAntialias = BrushQualityPolicy.IsAntialiasEnabled(quality);
#pragma warning disable CS0618
        paint.FilterQuality = BrushQualityPolicy.FilterQuality(quality);
#pragma warning restore CS0618
    }

    private void RenderPreparedStamps(ActiveStroke stroke, BrushPreset brush, SKCanvas canvas)
    {
        using var colorStampPaint = new SKPaint { BlendMode = stroke.Paint.BlendMode };
        ApplyBrushQualityToPaint(colorStampPaint, brush.Quality);
        using var stackingPaint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
        ApplyBrushQualityToPaint(stackingPaint, brush.Quality);

        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

            if (_stampColors.Count > i)
            {
                var color = _stampColors[i];
                if (color.Alpha == 0) continue;
                stroke.UpdateColor(color);
            }

            stroke.UpdateOpacity(stamp.Opacity);

            var tipIndices = stroke.TipIndicesFor(stamp.TipIndex);
            for (var ti = 0; ti < tipIndices.Length; ti++)
            {
                var tipIndex = tipIndices[ti];
                var tip = stroke.TipFor(tipIndex);
                var maskResolution = BrushTipMaskRasterization.MaskResolutionForStamp(stamp.Size, brush);
                stroke.UpdateMatrix(stamp, maskResolution, maskResolution);

                if (tip.HasColor && tip.GenerateColorStamp(maskResolution) is { } colorStamp)
                {
                    var alpha = (byte)Math.Clamp((int)(stamp.Opacity * 255), 0, 255);
                    colorStampPaint.BlendMode = ti == 0 ? stroke.Paint.BlendMode : SKBlendMode.SrcOver;
                    colorStampPaint.Color = new SKColor(255, 255, 255, alpha);
                    canvas.Save();
                    canvas.Concat(in stroke.Matrix);
                    canvas.DrawBitmap(colorStamp, 0f, 0f, colorStampPaint);
                    canvas.Restore();
                    continue;
                }

                if (ti > 0)
                {
                    stackingPaint.Color = stroke.Paint.Color;
                    stackingPaint.ColorFilter = stroke.Paint.ColorFilter;
                }
                var mask = stroke.MaskFor(tipIndex, stamp);
                var paint = ti == 0 ? stroke.Paint : stackingPaint;

                canvas.Save();
                canvas.Concat(in stroke.Matrix);
                canvas.DrawBitmap(mask, 0f, 0f, paint);
                canvas.Restore();

                stroke.ReleaseMask(mask);
            }
        }
    }

    private const int MaxMixBlurRadius = 48;

    private void PrepareStampColors(DrawingLayer layer, BrushPreset brush, ActiveStroke stroke, PixelSampler? sampleSource)
    {
        _stampColors.Clear();
        try
        {
            for (var i = 0; i < _stamps.Count; i++)
                PrepareOneStampColor(layer, brush, stroke, _stamps[i], sampleSource);
        }
        finally
        {
        }
    }

    private void PrepareOneStampColor(
        DrawingLayer layer,
        BrushPreset brush,
        ActiveStroke stroke,
        StampSample stamp,
        PixelSampler? sampleSource)
    {
        var amount = ComputeEffectivePaintAmount(brush);
        var density = Math.Clamp((float)brush.DensityOfPaint, 0f, 1f);
        var stretch = Math.Clamp((float)brush.ColorStretch, 0f, 1f);
        var stretchCarry = MinStretchCarry + (MaxStretchCarry - MinStretchCarry) * stretch;
        var blur = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
        var mixingMode = brush.MixingMode;

        var existing = SampleExistingPigment(layer, stroke, stamp, blur, sampleSource, mixingMode);
        if (existing.Alpha == 0 && stroke.CarriedColor.Alpha == 0 && amount <= 0f)
        {
            _stampColors.Add(SKColors.Transparent);
            return;
        }

        var pigment = existing.Alpha > 0
            ? MixColors(existing, stroke.CarriedColor, stretch, mixingMode)
            : stroke.CarriedColor;
        var baseColor = stroke.BaseColor;
        var mixedRgb = MixRgb(pigment, baseColor, amount, mixingMode);
        var alpha = ComputeSmudgeDepositionAlpha(brush, pigment, baseColor, amount, density);
        if (alpha == 0)
        {
            _stampColors.Add(SKColors.Transparent);
        }
        else
        {
            _stampColors.Add(new SKColor(mixedRgb.Red, mixedRgb.Green, mixedRgb.Blue, alpha));
        }

        stroke.CarriedColor = brush.SmudgeMode switch
        {
            SmudgeMode.Blend => SKColors.Transparent,
            SmudgeMode.Smudge => existing.Alpha > 0
                ? DecayAlpha(MixColors(stroke.CarriedColor, existing, 1f, mixingMode), stretchCarry)
                : stroke.CarriedColor,
            _ => DecayAlpha(
                existing.Alpha > 0
                    ? MixColors(MixColors(stroke.CarriedColor, existing, 1f, mixingMode), baseColor, amount, mixingMode)
                    : MixColors(stroke.CarriedColor, baseColor, amount, mixingMode),
                existing.Alpha > 0 ? stretchCarry : 1f)
        };
    }

    private static byte ComputeSmudgeDepositionAlpha(
        BrushPreset brush, SKColor pigment, SKColor baseColor, float amount, float density)
    {
        if (pigment.Alpha == 0 && amount <= 0f)
            return 0;

        return brush.SmudgeMode switch
        {
            // Amount/density gate NEW brush paint. Picked-up pigment smears at
            // full strength when amount=0 — matching Running Color / Smear.
            SmudgeMode.Smear when amount <= 0f => pigment.Alpha,
            SmudgeMode.Smear => (byte)Math.Clamp(
                Math.Max(pigment.Alpha, baseColor.Alpha * amount),
                0, 255),
            SmudgeMode.Smudge when amount <= 0f => pigment.Alpha,
            SmudgeMode.Smudge => MixAlpha(pigment.Alpha, baseColor.Alpha, Math.Max(amount, density)),
            _ => amount <= 0f
                ? pigment.Alpha
                : MixAlpha(pigment.Alpha, baseColor.Alpha, density)
        };
    }

    private SKColor SampleExistingPigment(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        float blur,
        PixelSampler? sampleSource,
        MixingMode mixingMode)
        => SampleExistingPigmentCore(layer, stroke, stamp, blur, sampleSource, mixingMode, () =>
        {
            if (stroke.TryGetCachedDab(stamp, out var dab))
                return SamplePigmentUnderDab(layer, stroke, stamp, dab, sampleSource);

            SamplePixel(layer, sampleSource, (int)stamp.X, (int)stamp.Y, out var sb, out var sg, out var sr, out var sa);
            return sa > 0 ? new SKColor(sr, sg, sb, sa) : SKColors.Transparent;
        });

    private SKColor SampleExistingPigmentCore(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        float blur,
        PixelSampler? sampleSource,
        MixingMode mixingMode,
        Func<SKColor> sampleFootprint)
    {
        var referenceSize = MathF.Max(8f, stamp.Size);
        if (blur > 0.001f)
        {
            var blurred = SampleHaltonDullingColor(layer, stroke, stamp, blur, referenceSize, sampleSource);
            if (blur >= 0.85f)
                return blurred.Alpha > 0 ? blurred : SKColors.Transparent;

            var footprint = sampleFootprint();
            if (footprint.Alpha == 0 && blurred.Alpha > 0)
                return blurred;

            return MixColors(footprint, blurred, blur, mixingMode);
        }

        return sampleFootprint();
    }

    // squares color rate before applying opacity so low amounts feel
    // genuinely zero — matching /slider response.
    private static float ComputeEffectivePaintAmount(BrushPreset brush)
    {
        var raw = Math.Clamp((float)brush.AmountOfPaint, 0f, 1f)
            * Math.Clamp((float)brush.ColorLoad, 0f, 1f);
        return raw * raw;
    }

    private static float ComputeSmearRate(BrushPreset brush, float stampOpacity)
    {
        var stretch = Math.Clamp((float)brush.ColorStretch, 0f, 1f);
        return stampOpacity * (0.2f + 0.8f * stretch);
    }

    private static float Halton(int index, int basis)
    {
        var result = 0f;
        var f = 1f / basis;
        var i = index;
        while (i > 0)
        {
            result += f * (i % basis);
            i /= basis;
            f /= basis;
        }
        return result;
    }

    private static int MaxRgbDifference(SKColor a, SKColor b)
        => Math.Max(Math.Abs(a.Red - b.Red),
            Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    private static float TryGetMaskWeight(ActiveStroke.CachedDab dab, int cx, int cy, int px, int py)
    {
        var localX = px - (cx + dab.OffsetX);
        var localY = py - (cy + dab.OffsetY);
        if ((uint)localX >= (uint)dab.LogicalWidth || (uint)localY >= (uint)dab.LogicalHeight || dab.Mask.IsEmpty)
            return -1f;
        return SampleMaskAlpha(dab, localX, localY) / 255f;
    }

    // dulling pickup: Halton-weighted samples inside the dab
    // footprint, converging early when the estimate stabilizes.
    private SKColor SampleHaltonDullingColor(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        float blur,
        float referenceSize,
        PixelSampler? sampleSource)
    {
        var cx = (int)MathF.Round(stamp.X);
        var cy = (int)MathF.Round(stamp.Y);
        var sampleRadius = Math.Clamp(blur, 0f, 1f);

        int srcLeft, srcTop, srcW, srcH;
        ActiveStroke.CachedDab? dab = null;
        if (stroke.TryGetCachedDab(stamp, out var cachedDab))
        {
            dab = cachedDab;
            srcLeft = cx + cachedDab.OffsetX;
            srcTop = cy + cachedDab.OffsetY;
            srcW = cachedDab.LogicalWidth;
            srcH = cachedDab.LogicalHeight;
        }
        else
        {
            var radius = MathF.Min(MaxMixBlurRadius, MathF.Max(1f, blur * referenceSize * 0.85f));
            var iradius = (int)MathF.Ceiling(radius);
            srcLeft = cx - iradius;
            srcTop = cy - iradius;
            srcW = srcH = iradius * 2 + 1;
        }

        var currentRadius = sampleRadius;
        SKColor result = SKColors.Transparent;
        do
        {
            var blow = sampleRadius > 0f ? 0.5f * (currentRadius - 1f) : 0f;
            var sampleLeft = srcLeft - (int)MathF.Floor(blow);
            var sampleTop = srcTop - (int)MathF.Floor(blow);
            var sampleRight = srcLeft + srcW + (int)MathF.Ceiling(blow);
            var sampleBottom = srcTop + srcH + (int)MathF.Ceiling(blow);
            var sampleW = Math.Max(1, sampleRight - sampleLeft);
            var sampleH = Math.Max(1, sampleBottom - sampleTop);
            var numPixels = sampleW * sampleH;
            var minSamples = Math.Min(numPixels, Math.Clamp((int)MathF.Round(0.02f * numPixels), 64, 256));

            float accR = 0f, accG = 0f, accB = 0f, accA = 0f, colorWeightSum = 0f, alphaWeightSum = 0f;
            var hIndex2 = 1;
            var hIndex3 = 1;
            var restartWithBiggerRadius = false;

            for (var i = 0; i < minSamples; i++)
            {
                var localX = sampleW <= 1 ? 0 : (int)(Halton(hIndex2++, 2) * (sampleW - 1));
                var localY = sampleH <= 1 ? 0 : (int)(Halton(hIndex3++, 3) * (sampleH - 1));
                var px = sampleLeft + localX;
                var py = sampleTop + localY;
                SamplePixel(layer, sampleSource, px, py, out var b, out var g, out var r, out var a);

                var weight = 1f;
                if (dab != null)
                {
                    weight = TryGetMaskWeight(dab, cx, cy, px, py);
                    if (weight < 0f)
                    {
                        restartWithBiggerRadius = true;
                        weight = 0f;
                    }
                    else if (weight <= 0f)
                    {
                        restartWithBiggerRadius = true;
                    }
                }

                if (weight <= 0f) continue;

                alphaWeightSum += weight;
                accA += a * weight;
                if (a == 0) continue;

                var colorWeight = weight * (a / 255f);
                accR += r * colorWeight;
                accG += g * colorWeight;
                accB += b * colorWeight;
                colorWeightSum += colorWeight;
            }

            if (colorWeightSum > 0.0001f)
            {
                result = new SKColor(
                    (byte)Math.Clamp(accR / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accG / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accB / colorWeightSum, 0, 255),
                    alphaWeightSum > 0.0001f
                        ? (byte)Math.Clamp(accA / alphaWeightSum, 0, 255)
                        : (byte)0);
            }
            else
            {
                SamplePixel(layer, sampleSource, cx, cy, out var cb, out var cg, out var cr, out var ca);
                result = ca > 0 ? new SKColor(cr, cg, cb, ca) : SKColors.Transparent;
            }

            var lastResult = result;
            var samplesLeft = numPixels - minSamples;
            while (samplesLeft > 0 && colorWeightSum > 0.0001f)
            {
                var batchSize = Math.Min(samplesLeft, 16);
                for (var i = 0; i < batchSize; i++)
                {
                    var localX = sampleW <= 1 ? 0 : (int)(Halton(hIndex2++, 2) * (sampleW - 1));
                    var localY = sampleH <= 1 ? 0 : (int)(Halton(hIndex3++, 3) * (sampleH - 1));
                    var px = sampleLeft + localX;
                    var py = sampleTop + localY;
                    SamplePixel(layer, sampleSource, px, py, out var b, out var g, out var r, out var a);

                    var weight = 1f;
                    if (dab != null)
                    {
                        weight = TryGetMaskWeight(dab, cx, cy, px, py);
                        if (weight < 0f || weight <= 0f)
                        {
                            restartWithBiggerRadius = true;
                            continue;
                        }
                    }

                    alphaWeightSum += weight;
                    accA += a * weight;
                    if (a == 0) continue;
                    var colorWeight = weight * (a / 255f);
                    accR += r * colorWeight;
                    accG += g * colorWeight;
                    accB += b * colorWeight;
                    colorWeightSum += colorWeight;
                }

                result = new SKColor(
                    (byte)Math.Clamp(accR / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accG / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accB / colorWeightSum, 0, 255),
                    alphaWeightSum > 0.0001f
                        ? (byte)Math.Clamp(accA / alphaWeightSum, 0, 255)
                        : (byte)0);

                if (MaxRgbDifference(result, lastResult) <= 2)
                    break;

                lastResult = result;
                samplesLeft -= batchSize;
            }

            if (!restartWithBiggerRadius || currentRadius >= 1f)
                break;

            currentRadius = Math.Min(1f, currentRadius + 0.05f);
        } while (true);

        return result;
    }

    private enum SpatialSmearResult { SkippedFirstDab, Rendered, Failed }

    // smearing mode: each dab reads pixels from the previous dab rect
    // translated by cursor movement, then optionally deposits new paint.
    private unsafe SpatialSmearResult TryRenderSpatialSmearStamp(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        StampSample stamp,
        PixelRegion stampDirty)
    {
        if (stamp.Opacity <= 0f || stamp.Size <= 0f)
            return SpatialSmearResult.Failed;

        if (!stroke.TryGetCachedDab(stamp, out var dab) || dab.Mask.IsEmpty)
            return SpatialSmearResult.Failed;

        var maskPixels = dab.Mask.GetPixels();
        if (maskPixels == IntPtr.Zero)
            return SpatialSmearResult.Failed;

        var cx = (int)MathF.Round(stamp.X);
        var cy = (int)MathF.Round(stamp.Y);
        var left = cx + dab.OffsetX;
        var top = cy + dab.OffsetY;
        var right = left + dab.LogicalWidth;
        var bottom = top + dab.LogicalHeight;
        var centerX = (left + right) * 0.5f;
        var centerY = (top + bottom) * 0.5f;

        if (stroke.SmearFirstDabPending)
        {
            stroke.LastSmearCenterX = centerX;
            stroke.LastSmearCenterY = centerY;
            stroke.SmearFirstDabPending = false;
            return SpatialSmearResult.SkippedFirstDab;
        }

        var offsetX = (int)MathF.Round(stroke.LastSmearCenterX - centerX);
        var offsetY = (int)MathF.Round(stroke.LastSmearCenterY - centerY);
        stroke.LastSmearCenterX = centerX;
        stroke.LastSmearCenterY = centerY;

        var smearRate = ComputeSmearRate(brush, stamp.Opacity);
        var paintRate = ComputeEffectivePaintAmount(brush) * stamp.Opacity;
        var density = Math.Clamp((float)brush.DensityOfPaint, 0f, 1f);
        if (paintRate > 0f)
            paintRate *= density;

        var blurSoftening = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
        var applyBlurSoften = blurSoftening >= 0.35f;
        Span<byte> softenLut = stackalloc byte[256];
        if (applyBlurSoften)
        {
            var soften = (blurSoftening - 0.35f) / 0.65f;
            var exponent = 1f - soften * 0.75f;
            for (var v = 0; v < 256; v++)
            {
                var a = v / 255f;
                softenLut[v] = (byte)Math.Clamp((int)(MathF.Pow(a, exponent) * 255f + 0.5f), 0, 255);
            }
        }

        var baseColor = stroke.BaseColor;
        var useFastMaskPath = !dab.IsScaled && !applyBlurSoften;
        var maskPtr = useFastMaskPath ? (byte*)maskPixels.ToPointer() : null;
        var maskStride = dab.Mask.RowBytes;
        var renderedAny = false;

        var pxMinX = Math.Max(left, stampDirty.X);
        var pxMinY = Math.Max(top, stampDirty.Y);
        var pxMaxX = Math.Min(right, stampDirty.Right);
        var pxMaxY = Math.Min(bottom, stampDirty.Bottom);
        if (pxMinX >= pxMaxX || pxMinY >= pxMaxY)
            return SpatialSmearResult.Failed;

        const int tsz = TiledPixelBuffer.TileSize;
        _smearSnapshots.Clear();
        var srcSnapshots = _smearSnapshots;
        var srcMinX = pxMinX + offsetX;
        var srcMinY = pxMinY + offsetY;
        var srcMaxX = pxMaxX + offsetX;
        var srcMaxY = pxMaxY + offsetY;
        var needsPaint = paintRate > 0f && baseColor.Alpha > 0;

        var srcFirstTx = FloorDiv(srcMinX, tsz);
        var srcFirstTy = FloorDiv(srcMinY, tsz);
        var srcLastTx = FloorDiv(srcMaxX - 1, tsz);
        var srcLastTy = FloorDiv(srcMaxY - 1, tsz);
        for (var ty = srcFirstTy; ty <= srcLastTy; ty++)
        {
            for (var tx = srcFirstTx; tx <= srcLastTx; tx++)
            {
                int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                if (srcSnapshots.ContainsKey(key)) continue;
                var raw = PaintBuf(layer).GetTileOrNull(tx, ty);
                if (raw == null)
                {
                    srcSnapshots[key] = null;
                    continue;
                }

                var copy = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
                srcSnapshots[key] = copy;
            }
        }

        PaintBuf(layer).EnterPixelWriteLock();
        try
        {
            var firstTx = FloorDiv(pxMinX, tsz);
            var firstTy = FloorDiv(pxMinY, tsz);
            var lastTx = FloorDiv(pxMaxX - 1, tsz);
            var lastTy = FloorDiv(pxMaxY - 1, tsz);

            for (var ty = firstTy; ty <= lastTy; ty++)
            {
                var tilePixY = ty * tsz;
                for (var tx = firstTx; tx <= lastTx; tx++)
                {
                    var tilePixX = tx * tsz;
                    var dstTile = PaintBuf(layer).GetOrCreateRawTile(tx, ty);

                    var tilePxMinX = Math.Max(pxMinX, tilePixX);
                    var tilePxMinY = Math.Max(pxMinY, tilePixY);
                    var tilePxMaxX = Math.Min(pxMaxX, tilePixX + tsz);
                    var tilePxMaxY = Math.Min(pxMaxY, tilePixY + tsz);

                    for (var py = tilePxMinY; py < tilePxMaxY; py++)
                    {
                        var localY = py - top;
                        var maskRow = useFastMaskPath ? maskPtr! + localY * maskStride : null;
                        var rowBase = (py - tilePixY) * tsz * 4;

                        for (var px = tilePxMinX; px < tilePxMaxX; px++)
                        {
                            var maskA = useFastMaskPath
                                ? maskRow![px - left]
                                : SampleMaskAlpha(dab, px - left, localY);
                            if (maskA == 0) continue;
                            if (applyBlurSoften)
                                maskA = softenLut[maskA];
                            if (maskA == 0) continue;

                            var dstOffset = rowBase + (px - tilePixX) * 4;
                            var db = dstTile[dstOffset];
                            var dg = dstTile[dstOffset + 1];
                            var dr = dstTile[dstOffset + 2];
                            var da = dstTile[dstOffset + 3];

                            var srcX = px + offsetX;
                            var srcY = py + offsetY;
                            byte sb = 0, sg = 0, sr = 0, sa = 0;
                            if (srcX >= 0 && srcY >= 0 && srcX < layer.Width && srcY < layer.Height)
                            {
                                var srcTx = FloorDiv(srcX, tsz);
                                var srcTy = FloorDiv(srcY, tsz);
                                int srcKey = (srcTx & 0xFFFF) | ((srcTy & 0xFFFF) << 16);
                                srcSnapshots.TryGetValue(srcKey, out var srcTile);
                                ReadPixelFromTile(srcTile, srcTx * tsz, srcTy * tsz, srcX, srcY, out sb, out sg, out sr, out sa);
                            }

                            if (sa == 0 && da == 0 && !needsPaint)
                                continue;

                            var b = db;
                            var g = dg;
                            var r = dr;
                            var a = da;
                            var changed = false;

                            if (sa > 0 && smearRate > 0f)
                            {
                                var smearA = (int)(maskA * smearRate + 0.5f);
                                if (smearA > 0)
                                {
                                    if (smearA > 255) smearA = 255;
                                    AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a,
                                        sb, sg, sr, (byte)smearA, layer.IsAlphaLocked, SKBlendMode.SrcOver);
                                    changed = true;
                                }
                            }

                            if (needsPaint)
                            {
                                var paintA = (int)(maskA * paintRate + 0.5f);
                                if (paintA > 0)
                                {
                                    if (paintA > 255) paintA = 255;
                                    AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a,
                                        baseColor.Blue, baseColor.Green, baseColor.Red, (byte)paintA,
                                        layer.IsAlphaLocked, SKBlendMode.SrcOver);
                                    changed = true;
                                }
                            }

                            if (changed)
                            {
                                dstTile[dstOffset] = b;
                                dstTile[dstOffset + 1] = g;
                                dstTile[dstOffset + 2] = r;
                                dstTile[dstOffset + 3] = a;
                                renderedAny = true;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            PaintBuf(layer).ExitPixelWriteLock();
        }

        return renderedAny ? SpatialSmearResult.Rendered : SpatialSmearResult.Failed;
    }

    private static void ReadPixelFromTile(byte[]? tile, int tilePixX, int tilePixY, int px, int py,
        out byte b, out byte g, out byte r, out byte a)
    {
        if (tile == null)
        {
            b = g = r = a = 0;
            return;
        }

        const int tsz = TiledPixelBuffer.TileSize;
        var lx = px - tilePixX;
        var ly = py - tilePixY;
        if ((uint)lx >= tsz || (uint)ly >= tsz)
        {
            b = g = r = a = 0;
            return;
        }

        var offset = (ly * tsz + lx) * 4;
        b = tile[offset];
        g = tile[offset + 1];
        r = tile[offset + 2];
        a = tile[offset + 3];
    }

    private static void MarkPaintThumbnailsDirty(DrawingLayer layer)
    {
        layer.MarkThumbnailDirty();
        if (MaskPaint(layer))
            layer.MarkMaskThumbnailDirty();
    }

    private static unsafe void CompositeScratchBgraOntoLayer(
        TiledPixelBuffer pixels, PixelRegion dirty, byte* scratch, int scratchStride,
        bool alphaLocked, SKBlendMode blendMode, bool maskMode = false)
    {
        if (dirty.IsEmpty) return;

        const int tsz = TiledPixelBuffer.TileSize;
        var firstTx = FloorDiv(dirty.X, tsz);
        var firstTy = FloorDiv(dirty.Y, tsz);
        var lastTx = FloorDiv(dirty.Right - 1, tsz);
        var lastTy = FloorDiv(dirty.Bottom - 1, tsz);

        pixels.EnterPixelWriteLock();
        try
        {
            for (var ty = firstTy; ty <= lastTy; ty++)
            {
                var tilePixY = ty * tsz;
                for (var tx = firstTx; tx <= lastTx; tx++)
                {
                    var tilePixX = tx * tsz;
                    var pxMinX = Math.Max(dirty.X, tilePixX);
                    var pxMinY = Math.Max(dirty.Y, tilePixY);
                    var pxMaxX = Math.Min(dirty.Right, tilePixX + tsz);
                    var pxMaxY = Math.Min(dirty.Bottom, tilePixY + tsz);
                    if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) continue;

                    var tile = pixels.GetOrCreateRawTile(tx, ty);
                    for (var py = pxMinY; py < pxMaxY; py++)
                    {
                        var ly = py - tilePixY;
                        var rowBase = ly * tsz * 4;
                        var scratchRow = scratch + (py - dirty.Y) * scratchStride;
                        for (var px = pxMinX; px < pxMaxX; px++)
                        {
                            var scratchOffset = (px - dirty.X) * 4;
                            var srcA = scratchRow[scratchOffset + 3];
                            if (srcA == 0) continue;

                            var dstOffset = rowBase + (px - tilePixX) * 4;
                            WriteCompositeStamp(tile, dstOffset,
                                scratchRow[scratchOffset + 0],
                                scratchRow[scratchOffset + 1],
                                scratchRow[scratchOffset + 2],
                                srcA, alphaLocked, blendMode, maskMode);
                        }
                    }
                }
            }
        }
        finally
        {
            pixels.ExitPixelWriteLock();
        }
    }

    private static void WriteCompositeStamp(
        byte[] tile, int offset,
        byte srcB, byte srcG, byte srcR, byte stampA,
        bool alphaLocked, SKBlendMode blendMode, bool maskMode = false)
    {
        if (maskMode)
        {
            var dstA = tile[offset + 3];
            var effA = UsesMaskOpacity(blendMode) ? stampA : (byte)((stampA * Math.Max(srcB, Math.Max(srcG, srcR)) + 127) / 255);
            if (effA == 0) return;
            byte outA = blendMode switch
            {
                SKBlendMode.DstOut or SKBlendMode.Clear => (byte)(dstA * (255 - effA) / 255),
                _ => (byte)(effA + (dstA * (255 - effA) + 127) / 255)
            };
            tile[offset] = outA;
            tile[offset + 1] = outA;
            tile[offset + 2] = outA;
            tile[offset + 3] = outA;
            return;
        }

        byte db = tile[offset], dg = tile[offset + 1], dr = tile[offset + 2], da = tile[offset + 3];
        AlphaLockPixelOps.CompositeBrushPixel(ref db, ref dg, ref dr, ref da, srcB, srcG, srcR, stampA, alphaLocked, blendMode);
        tile[offset] = db;
        tile[offset + 1] = dg;
        tile[offset + 2] = dr;
        tile[offset + 3] = da;
    }

    private static bool SupportsCpuRasterBlendMode(SKBlendMode mode) =>
        mode is SKBlendMode.SrcOver or SKBlendMode.DstOut or SKBlendMode.Clear
            or SKBlendMode.Multiply or SKBlendMode.Screen or SKBlendMode.Overlay
            or SKBlendMode.Darken or SKBlendMode.Lighten or SKBlendMode.ColorDodge
            or SKBlendMode.ColorBurn or SKBlendMode.HardLight or SKBlendMode.SoftLight
            or SKBlendMode.Difference or SKBlendMode.Exclusion;

    private static bool UsesMaskOpacity(SKBlendMode mode)
        => mode is SKBlendMode.DstOut or SKBlendMode.Clear;

    private static float StampOpacity255(SKBlendMode mode, float stampOpacity, float colorAlpha)
        => UsesMaskOpacity(mode) ? stampOpacity * 255f : stampOpacity * colorAlpha;

    /// <summary>
    /// Porter-Duff src-over alpha for same-color dab masks. Max-alpha was lighter in overlap zones.
    /// </summary>
    private static byte AccumulateSrcOverAlpha(byte existingAlpha, int stampAlpha)
    {
        if (stampAlpha <= 0) return existingAlpha;
        if (stampAlpha >= 255) return 255;
        if (existingAlpha <= 0) return (byte)stampAlpha;
        return (byte)Math.Min(255, existingAlpha + (stampAlpha * (255 - existingAlpha) + 127) / 255);
    }

    private static void RenderWithSkiaOnLayer(DrawingLayer layer, PixelRegion dirty, Action<SKCanvas> render)
    {
        if (!layer.IsAlphaLocked)
        {
            PaintBuf(layer).RenderWithSkia(dirty, render);
            return;
        }

        var before = PaintBuf(layer).CaptureTiles(dirty);
        PaintBuf(layer).RenderWithSkia(dirty, render);
        PaintBuf(layer).EnterPixelWriteLock();
        try
        {
            AlphaLockPixelOps.RestoreLockedTransparentPixels(PaintBuf(layer), dirty, before);
        }
        finally
        {
            PaintBuf(layer).ExitPixelWriteLock();
        }
    }

    private SKColor SamplePigmentUnderDab(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        ActiveStroke.CachedDab dab,
        PixelSampler? sampleSource)
    {
        // Sample at a sparse, fixed grid around the stamp centre rather than
        // walking the entire dab mask. The previous implementation iterated
        // ~width*height/step² mask pixels per stamp; for a 230² dab that was
        // ~3300 samples just to compute one averaged pickup colour, repeated
        // for every stamp in the batch. A small fixed grid (~37 samples)
        // gives perceptually identical results because the spatial weighting
        // already concentrates contribution near the centre.
        var cx = (int)MathF.Round(stamp.X);
        var cy = (int)MathF.Round(stamp.Y);
        var radius = MathF.Max(1f, stamp.Size * 0.35f);
        var iradius = (int)MathF.Ceiling(radius);
        // Coarse step: ~7 samples across the diameter regardless of size.
        var step = Math.Max(1, iradius / 3);

        float accR = 0, accG = 0, accB = 0, accA = 0, colorWeightSum = 0, alphaWeightSum = 0;
        for (var dy = -iradius; dy <= iradius; dy += step)
        {
            for (var dx = -iradius; dx <= iradius; dx += step)
            {
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;

                var weight = 1f - (dist / radius);
                weight *= weight; // bias toward centre
                SamplePixel(layer, sampleSource, cx + dx, cy + dy, out var b, out var g, out var r, out var a);
                alphaWeightSum += weight;
                accA += a * weight;
                if (a == 0) continue;

                var colorWeight = weight * (a / 255f);
                accR += r * colorWeight;
                accG += g * colorWeight;
                accB += b * colorWeight;
                colorWeightSum += colorWeight;
            }
        }

        if (colorWeightSum > 0.0001f)
        {
            var avgAlpha = alphaWeightSum > 0.0001f
                ? (byte)Math.Clamp(accA / alphaWeightSum, 0, 255)
                : (byte)0;
            return new SKColor(
                (byte)Math.Clamp(accR / colorWeightSum, 0, 255),
                (byte)Math.Clamp(accG / colorWeightSum, 0, 255),
                (byte)Math.Clamp(accB / colorWeightSum, 0, 255),
                avgAlpha);
        }

        SamplePixel(layer, sampleSource, cx, cy, out var fb, out var fg, out var fr, out var fa);
        return fa > 0 ? new SKColor(fr, fg, fb, fa) : SKColors.Transparent;
    }

    private static SKColor MixRgb(SKColor from, SKColor to, float t, MixingMode mode)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;
        if (mode == MixingMode.Perceptual)
        {
            var fromLch = RgbToLCh(from.Red, from.Green, from.Blue);
            var toLch = RgbToLCh(to.Red, to.Green, to.Blue);
            var mixed = new Vector3(
                fromLch.X + (toLch.X - fromLch.X) * t,
                fromLch.Y + (toLch.Y - fromLch.Y) * t,
                MixHue(fromLch.Z, toLch.Z, t));
            var (r, g, b) = LChToRgb(mixed);
            return new SKColor((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), from.Alpha);
        }

        return new SKColor(
            (byte)Math.Clamp(from.Red * (1f - t) + to.Red * t, 0, 255),
            (byte)Math.Clamp(from.Green * (1f - t) + to.Green * t, 0, 255),
            (byte)Math.Clamp(from.Blue * (1f - t) + to.Blue * t, 0, 255),
            from.Alpha);
    }

    private static SKColor MixColors(SKColor from, SKColor to, float t, MixingMode mode)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;
        if (mode == MixingMode.Perceptual)
        {
            var fromLch = RgbToLCh(from.Red, from.Green, from.Blue);
            var toLch = RgbToLCh(to.Red, to.Green, to.Blue);
            var mixed = new Vector3(
                fromLch.X + (toLch.X - fromLch.X) * t,
                fromLch.Y + (toLch.Y - fromLch.Y) * t,
                MixHue(fromLch.Z, toLch.Z, t));
            var (r, g, b) = LChToRgb(mixed);
            var alpha = from.Alpha + (to.Alpha - from.Alpha) * t;
            return new SKColor((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(alpha, 0, 255));
        }

        return new SKColor(
            (byte)Math.Clamp(from.Red * (1f - t) + to.Red * t, 0, 255),
            (byte)Math.Clamp(from.Green * (1f - t) + to.Green * t, 0, 255),
            (byte)Math.Clamp(from.Blue * (1f - t) + to.Blue * t, 0, 255),
            (byte)Math.Clamp(from.Alpha * (1f - t) + to.Alpha * t, 0, 255));
    }

    private static byte MixAlpha(byte from, byte to, float t)
        => (byte)Math.Clamp(from * (1f - t) + to * t, 0, 255);

    private static SKColor DecayAlpha(SKColor color, float persistence)
        => new(color.Red, color.Green, color.Blue, (byte)Math.Clamp(color.Alpha * persistence, 0, 255));

    // The sample buffer caches the BEFORE-stroke pixel state across the dirty
    // region so that PrepareStampColors can do hundreds of thousands of pixel
    // reads as O(1) array indexing rather than per-call dictionary+tile lookups.
    // Margin extends the buffer to cover blur-kernel reach outside dirty bounds.
    private const int SampleBufferMargin = 64;
    private const long MaxSampleBufferPixels = 6L * 1024 * 1024;

    private void SamplePixel(DrawingLayer layer, PixelSampler? sampleSource, int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        if (sampleSource != null)
        {
            sampleSource(x, y, out b, out g, out r, out a);
            return;
        }

        PaintBuf(layer).GetPixel(x, y, out b, out g, out r, out a);
    }

    // Simplified RGB <-> LCh conversion for perceptual mixing
    private static Vector3 RgbToLCh(float r, float g, float b)
    {
        float xr = r / 255f;
        float xg = g / 255f;
        float xb = b / 255f;

        float R = SrgbToLinear(xr);
        float G = SrgbToLinear(xg);
        float B = SrgbToLinear(xb);

        float X = R * 0.4124564f + G * 0.3575761f + B * 0.1804375f;
        float Y = R * 0.2126729f + G * 0.7151522f + B * 0.0721750f;
        float Z = R * 0.0193339f + G * 0.1191920f + B * 0.9503041f;

        float fx = FAST_Cbrt(X);
        float fy = FAST_Cbrt(Y);
        float fz = FAST_Cbrt(Z);

        float L = 116f * fy - 16f;
        float A = 500f * (fx - fy);
        float B_ = 200f * (fy - fz);

        float C = MathF.Sqrt(A * A + B_ * B_);
        float H = MathF.Atan2(B_, A);

        return new Vector3(L, C, H);
    }

    private static (float R, float G, float B) LChToRgb(Vector3 lch)
    {
        float L = lch.X;
        float C = lch.Y;
        float H = lch.Z;

        float A = C * MathF.Cos(H);
        float B_ = C * MathF.Sin(H);

        float fy = (L + 16f) / 116f;
        float fx = A / 500f + fy;
        float fz = fy - B_ / 200f;

        float X = FAST_Cube(fx);
        float Y = FAST_Cube(fy);
        float Z = FAST_Cube(fz);

        float R_ = X * 3.2404542f + Y * -1.5371385f + Z * -0.4985314f;
        float G_ = X * -0.9692660f + Y * 1.8760108f + Z * 0.0415560f;
        float B__ = X * 0.0556434f + Y * -0.2040259f + Z * 1.0572252f;

        float r = LinearToSrgb(R_);
        float g = LinearToSrgb(G_);
        float b = LinearToSrgb(B__);

        return (r * 255f, g * 255f, b * 255f);
    }

    private static float MixHue(float h1, float h2, float t)
    {
        float diff = h2 - h1;
        if (diff > MathF.PI) diff -= MathF.Tau;
        else if (diff < -MathF.PI) diff += MathF.Tau;
        return h1 + diff * t;
    }

    private static PixelRegion StampBounds(StampSample stamp)
    {
        var radius = stamp.Size * 0.75f + 2.0f;
        var left = (int)MathF.Floor(stamp.X - radius);
        var top = (int)MathF.Floor(stamp.Y - radius);
        var right = (int)MathF.Ceiling(stamp.X + radius);
        var bottom = (int)MathF.Ceiling(stamp.Y + radius);
        return new PixelRegion(left, top, right - left + 1, bottom - top + 1);
    }

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);

    private static SplinePoint ToSplinePoint(CanvasInputSample s)
        => new((float)s.X, (float)s.Y, (float)s.Pressure, (float)s.TiltX, (float)s.TiltY, (float)s.Twist);

    private static CanvasInputSample Lerp(SplinePoint a, SplinePoint b, float t, CanvasInputSample from, CanvasInputSample to)
    {
        var time = (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t);
        return new CanvasInputSample(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            a.Pressure + (b.Pressure - a.Pressure) * t,
            a.TiltX + (b.TiltX - a.TiltX) * t,
            a.TiltY + (b.TiltY - a.TiltY) * t,
            a.Twist + (b.Twist - a.Twist) * t,
            time, to.PointerId, to.Source, to.Phase);
    }

    private static CanvasInputSample LerpCanvas(CanvasInputSample from, CanvasInputSample to, float t)
        => new(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Pressure + (to.Pressure - from.Pressure) * t,
            from.TiltX + (to.TiltX - from.TiltX) * t,
            from.TiltY + (to.TiltY - from.TiltY) * t,
            from.Twist + (to.Twist - from.Twist) * t,
            (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t),
            to.PointerId,
            to.Source,
            to.Phase);

    private static SplinePoint CatmullRom(SplinePoint p0, SplinePoint p1, SplinePoint p2, SplinePoint p3, float t)
    {
        var t2 = t * t; var t3 = t2 * t;
        return new SplinePoint(
            Catmull(p0.X, p1.X, p2.X, p3.X, t, t2, t3),
            Catmull(p0.Y, p1.Y, p2.Y, p3.Y, t, t2, t3),
            Catmull(p0.Pressure, p1.Pressure, p2.Pressure, p3.Pressure, t, t2, t3),
            Catmull(p0.TiltX, p1.TiltX, p2.TiltX, p3.TiltX, t, t2, t3),
            Catmull(p0.TiltY, p1.TiltY, p2.TiltY, p3.TiltY, t, t2, t3),
            Catmull(p0.Twist, p1.Twist, p2.Twist, p3.Twist, t, t2, t3));
    }

    private static float Catmull(float p0, float p1, float p2, float p3, float t, float t2, float t3)
        => 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17; h *= 0xbf324c81u;
            h ^= h >> 13; h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0f;
        }
    }

    private unsafe float[]? PrecomputeGrain(PixelRegion region, byte* texPx, int texW, int texH, int texStride, float brushGrain)
    {
        if (brushGrain <= 0f) return null;
        int w = region.Width, h = region.Height;
        if ((long)w * h > MaxPrecomputedGrainPixels)
            return null;

        if (_grainTable == null || _grainTable.Length < w * h)
            _grainTable = new float[w * h];
        var table = _grainTable;
        if (texPx != null)
        {
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int px = region.X + x, py = region.Y + y;
                    int gtx = px % texW; if (gtx < 0) gtx += texW;
                    int gty = py % texH; if (gty < 0) gty += texH;
                    float noise = texPx[gty * texStride + gtx] / 255.0f;
                    table[y * w + x] = 1f - brushGrain + noise * brushGrain;
                }
            });
        }
        else
        {
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float noise = GrainNoise(region.X + x, region.Y + y);
                    table[y * w + x] = 1f - brushGrain + noise * brushGrain;
                }
            });
        }
        return table;
    }

    private sealed class ActiveStroke : IDisposable
    {
        private readonly BrushPreset _brush;
        private const int MaxCachedMasks = 16;
        // Match 1024-dab batching floor. Pressure/rotation dynamics
        // create many nearby keys; trimming below normal batch size turns the
        // cache into churn during fast strokes.
        private const int MaxCachedDabs = 1024;
        // Brushes larger than 1024² logical footprint are downscaled into the
        // cache bitmap and bilinear-upsampled at stamp time.
        private const int MaxCachedDabPixels = 4 * 1024 * 1024;
        private const int MaxCachedColorDabs = 256;
        private readonly Dictionary<(int TipIndex, int Hardness, int Resolution), SKBitmap> _maskCache = new();
        private readonly Dictionary<CachedDabKey, CachedDab> _dabCache = new();
        private readonly Queue<CachedDabKey> _dabCacheOrder = new();
        private readonly Dictionary<CachedDabKey, CachedColorDab> _colorDabCache = new();
        private readonly Queue<CachedDabKey> _colorDabCacheOrder = new();
        private readonly HashSet<SKBitmap> _ownedMasks = new();
        private readonly List<IBrushTip>? _ownedTipSet;
        private int[]? _cachedTipIndices;

        private readonly SKColor _baseColor;
        private SKColor _currentColor;
        private SKColorFilter? _mixedFilter;

        public ActiveStroke(BrushPreset brush, CanvasInputSample sample)
        {
            _brush = brush;
            ParamGraphLookup = new Dictionary<BrushParameterTarget, BrushParameterGraph>(brush.ParameterGraphs.Count);
            foreach (var g in brush.ParameterGraphs)
                if (g.Validate().Count == 0)
                    ParamGraphLookup[g.Target] = g;
            BrushMaterialTips.BindToPreset(brush);
            if (brush.TipSelectionMode != BrushTipSelectionMode.Single && brush.Tips.Count > 0)
                _ownedTipSet = brush.Tips.Select(t => t.CreateTip()).ToList();
            HasAnyColorTip = _ownedTipSet is { Count: > 0 }
                ? _ownedTipSet.Any(t => t.HasColor)
                : brush.Tip.HasColor;

            StrokeRandom = Hash01((int)(sample.X * 997), (int)(sample.Y * 991));
            State = new StrokeState(
                (float)sample.X, (float)sample.Y,
                (float)sample.Pressure, (float)sample.TiltX, (float)sample.TiltY);

            var sp = new StrokePoint(
                (float)sample.X, (float)sample.Y, (float)sample.Pressure,
                (float)sample.TiltX, (float)sample.TiltY, (float)sample.Twist,
                0, 0, 0, 0, 0, StrokeRandom);
            var initSizeMul = EvalParameter(ParamGraphLookup, BrushParameterTarget.Size, sp, brush.Dynamics.EvalSize(sp));
            var initSpacingMul = EvalParameter(ParamGraphLookup, BrushParameterTarget.Spacing, sp, brush.Dynamics.EvalSpacing(sp));
            var initSize = Math.Max(BrushSpacing.MinStampSizePx, (float)brush.Size * Math.Max(0.5f, initSizeMul));
            State.NextStampDistance = BrushSpacing.EffectiveDistance(
                brush, initSize, Math.Clamp(initSpacingMul, 0.05f, 4f), 0f);

            BaseMaskSize = BrushTipMaskRasterization.StrokePeakMaskSize(brush);
            _deferMaskGeneration = UsesProceduralStampEvaluation(brush, TipFor(0), 0);
            if (!_deferMaskGeneration)
                _mask = TipFor(0).GenerateMask(BaseMaskSize, (float)brush.Hardness);
            _baseColor = ToSkColor(brush.Color);
            _currentColor = _baseColor;
            var initDensity = (float)brush.DensityOfPaint;
            CarriedColor = SKColors.Transparent;
            SmearFirstDabPending = brush.ColorMix && brush.SmudgeMode == SmudgeMode.Smear;
            Paint = new SKPaint
            {
                BlendMode = brush.BlendMode,
                Color = _baseColor,
                ColorFilter = SKColorFilter.CreateBlendMode(_baseColor, SKBlendMode.SrcIn)
            };
            ApplyBrushQualityToPaint(Paint, brush.Quality);
        }

        public float StrokeRandom { get; }
        public readonly Dictionary<BrushParameterTarget, BrushParameterGraph> ParamGraphLookup;
        public StrokeState State;
        public int BaseMaskSize { get; }
        private SKBitmap? _mask;
        private readonly bool _deferMaskGeneration;
        public SKBitmap Mask => _mask ??= TipFor(0).GenerateMask(BaseMaskSize, (float)_brush.Hardness);
        public SKPaint Paint { get; }
        public SKMatrix Matrix;
        public SKColor CarriedColor;
        public float LastSmearCenterX;
        public float LastSmearCenterY;
        public bool SmearFirstDabPending;
        public SKColor BaseColor => _baseColor;
        public bool HasAnyColorTip { get; }
        public Dictionary<(int X, int Y), byte[]> StrokeMaskTiles { get; } = new();
        public IReadOnlyDictionary<(int X, int Y), byte[]?>? BeforeTiles { get; set; }

        public bool Matches(BrushPreset brush)
            => ReferenceEquals(_brush, brush);

        public void UpdateColor(SKColor color)
        {
            if (_currentColor == color) return;
            _currentColor = color;
            _mixedFilter?.Dispose();
            _mixedFilter = SKColorFilter.CreateBlendMode(new SKColor(color.Red, color.Green, color.Blue, 255), SKBlendMode.SrcIn);
            Paint.ColorFilter = _mixedFilter;
        }

        public void UpdateOpacity(float opacity)
        {
            var colorAlphaRatio = _currentColor.Alpha / 255f;
            var alpha = (byte)Math.Clamp((int)(opacity * 255 * colorAlphaRatio), 0, 255);
            Paint.Color = new SKColor(_currentColor.Red, _currentColor.Green, _currentColor.Blue, alpha);
        }

        public void UpdateMatrix(StampSample stamp)
        {
            var maskW = BrushTipMaskRasterization.MaskResolutionForStamp(stamp.Size, _brush);
            var maskH = maskW;
            UpdateMatrix(stamp, maskW, maskH);
        }

        public void UpdateMatrix(StampSample stamp, int maskWidth, int maskHeight)
        {
            var scaleX = stamp.Size / Math.Max(1, maskWidth);
            var scaleY = stamp.Size / Math.Max(1, maskHeight);
            var thickness = Math.Clamp((float)_brush.TipThickness * stamp.TipThicknessMultiplier, 0.01f, 1f);
            if (_brush.TipDirection == BrushTipDirection.Horizontal)
                scaleY *= thickness;
            else
                scaleX *= thickness;
            if (_brush.FlipHorizontal) scaleX = -scaleX;
            if (_brush.FlipVertical) scaleY = -scaleY;
            Matrix = SKMatrix.CreateTranslation(-maskWidth * 0.5f, -maskHeight * 0.5f);
            Matrix = Matrix.PostConcat(SKMatrix.CreateScale(scaleX, scaleY));
            if (Math.Abs(stamp.Angle) > 0.001f)
                Matrix = Matrix.PostConcat(SKMatrix.CreateRotationDegrees(stamp.Angle));
            Matrix = Matrix.PostConcat(SKMatrix.CreateTranslation(stamp.X, stamp.Y));
        }

        public IBrushTip TipFor(int tipIndex)
        {
            if (_ownedTipSet is { Count: > 0 })
                return _ownedTipSet[Math.Clamp(tipIndex, 0, _ownedTipSet.Count - 1)];
            return _brush.Tip;
        }

        public int[] TipIndicesFor(int stampTipIndex)
        {
            if (_ownedTipSet is { Count: > 1 } && _brush.TipSelectionMode != BrushTipSelectionMode.Single)
                return _cachedTipIndices ??= Enumerable.Range(0, _ownedTipSet.Count).ToArray();
            return [stampTipIndex];
        }

        public bool IsImageTip(int tipIndex) => TipFor(tipIndex) is ImageBrushTip or NodeBrushTip { IsDirectImageSampler: true };

        public SKBitmap MaskFor(StampSample stamp)
            => MaskFor(stamp.TipIndex, stamp.Hardness, stamp.Size);

        public SKBitmap MaskFor(int tipIndex, StampSample stamp)
            => MaskFor(tipIndex, stamp.Hardness, stamp.Size);

        public SKBitmap MaskFor(float hardness)
            => MaskFor(0, hardness, BaseMaskSize);

        private SKBitmap MaskFor(int tipIndex, float hardness, float stampDiameter)
        {
            tipIndex = _ownedTipSet is { Count: > 0 } ? Math.Clamp(tipIndex, 0, _ownedTipSet.Count - 1) : 0;
            var hardnessKey = QuantizeHardness(hardness);
            var resolution = BrushTipMaskRasterization.MaskResolutionForStamp(stampDiameter, _brush);
            var key = (tipIndex, hardnessKey, resolution);
            if (_maskCache.TryGetValue(key, out var cached))
                return cached;

            var normalizedHardness = hardnessKey / 255f;
            var tip = TipFor(tipIndex);
            var tipMask = tipIndex == 0
                && hardnessKey == QuantizeHardness((float)_brush.Hardness)
                && resolution == BaseMaskSize
                ? Mask
                : tip.GenerateMask(resolution, normalizedHardness);

            // All tips cache internally; the stroke never owns tip masks.

            if (_brush.Shape == null)
                return CacheOrReturnTemporary(key, tipMask);

            var shapeMask = _brush.Shape.GenerateMask(resolution, normalizedHardness);
            var combined = MultiplyMasks(tipMask, shapeMask);
            _ownedMasks.Add(combined);
            // shapeMask is ProceduralBrushTip-owned; tip holds it internally, never dispose
            DisposeTemporary(tipMask);
            return CacheOrReturnTemporary(key, combined);
        }

        public void ReleaseMask(SKBitmap mask)
        {
            if (_maskCache.ContainsValue(mask)) return;
            if (_ownedMasks.Remove(mask))
                mask.Dispose();
        }

        public bool TryGetCachedDab(StampSample stamp, out CachedDab dab)
        {
            dab = null!;

            var key = CachedDabKey.From(_brush, stamp);
            if (_dabCache.TryGetValue(key, out dab!))
                return true;

            if (TryBakeLargeCircleDab(key, out dab)
                || TryBakeLargeMaskDab(key, out dab))
            {
                _dabCache[key] = dab;
                _dabCacheOrder.Enqueue(key);
                return true;
            }

            var mask = MaskFor(key.TipIndex, key.Hardness / 255f, key.Size);
            var layout = ComputeDabLayout(key, mask.Width, mask.Height);
            var bitmap = new SKBitmap(new SKImageInfo(layout.BitmapWidth, layout.BitmapHeight, SKColorType.Alpha8, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint
            {
                Color = SKColors.White,
                BlendMode = SKBlendMode.Src
            })
            {
                ApplyBrushQualityToPaint(paint, _brush.Quality);
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(layout.BitmapWidth * 0.5f, layout.BitmapHeight * 0.5f);
                if (MathF.Abs(layout.AngleDegrees) > 0.001f)
                    canvas.RotateDegrees(layout.AngleDegrees);
                canvas.Scale(layout.RenderScaleX, layout.RenderScaleY);
                canvas.DrawBitmap(mask, -mask.Width * 0.5f, -mask.Height * 0.5f, paint);
            }

            dab = new CachedDab(bitmap, layout.OffsetX, layout.OffsetY, layout.LogicalWidth, layout.LogicalHeight);
            _dabCache[key] = dab;
            _dabCacheOrder.Enqueue(key);
            return true;
        }

        public bool TryGetCachedColorDab(StampSample stamp, out CachedColorDab dab)
        {
            dab = null!;

            var key = CachedDabKey.From(_brush, stamp);
            if (_colorDabCache.TryGetValue(key, out dab!))
                return true;

            var tip = TipFor(key.TipIndex);
            var colorResolution = BrushTipMaskRasterization.MaskResolutionForStamp(key.Size, _brush);
            if (!tip.HasColor || tip.GenerateColorStamp(colorResolution) is not { } colorStamp)
                return false;

            var layout = ComputeDabLayout(key, colorStamp.Width, colorStamp.Height);
            var bitmap = new SKBitmap(new SKImageInfo(layout.BitmapWidth, layout.BitmapHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                ApplyBrushQualityToPaint(paint, _brush.Quality);
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(layout.BitmapWidth * 0.5f, layout.BitmapHeight * 0.5f);
                if (MathF.Abs(layout.AngleDegrees) > 0.001f)
                    canvas.RotateDegrees(layout.AngleDegrees);
                canvas.Scale(layout.RenderScaleX, layout.RenderScaleY);
                canvas.DrawBitmap(colorStamp, -colorStamp.Width * 0.5f, -colorStamp.Height * 0.5f, paint);
            }

            dab = new CachedColorDab(bitmap, layout.OffsetX, layout.OffsetY, layout.LogicalWidth, layout.LogicalHeight);
            _colorDabCache[key] = dab;
            _colorDabCacheOrder.Enqueue(key);
            return true;
        }

        private readonly record struct DabLayout(
            int LogicalWidth,
            int LogicalHeight,
            int BitmapWidth,
            int BitmapHeight,
            float RenderScaleX,
            float RenderScaleY,
            int OffsetX,
            int OffsetY,
            float AngleDegrees);

        private bool TryBakeLargeCircleDab(CachedDabKey key, out CachedDab dab)
        {
            dab = null!;
            if (key.Size < LargeStampCachedRasterMinDiameterPx)
                return false;
            if (_brush.Shape != null || key.Angle != 0 || key.FlipBits != 0 || key.TipIndex != 0)
                return false;
            if (TipFor(0) is not ProceduralBrushTip { Shape: BrushTipShape.Circle or BrushTipShape.SoftRound })
                return false;
            if (Math.Abs(_brush.TipThickness - 1.0) > 0.001f
                && Math.Abs(key.Thickness / 256f - (float)_brush.TipThickness) > 0.05f)
                return false;

            var hardness = Math.Clamp(key.Hardness * 100 / 255, 0, 100);
            var diamUpper = Math.Min(4096, key.Size + 8);
            var buffer = new byte[diamUpper * diamUpper];
            var stamp = ClassicBrushLut.GetStamp(key.Size, hardness, buffer, buffer);
            var d = stamp.Diameter;
            if (d <= 0 || d * d > buffer.Length)
                return false;

            var bitmap = new SKBitmap(new SKImageInfo(d, d, SKColorType.Alpha8, SKAlphaType.Unpremul));
            unsafe
            {
                var dst = (byte*)bitmap.GetPixels().ToPointer();
                var stride = bitmap.RowBytes;
                fixed (byte* src = stamp.Data)
                {
                    for (var y = 0; y < d; y++)
                        Buffer.MemoryCopy(src + y * d, dst + y * stride, d, d);
                }
            }

            dab = new CachedDab(bitmap, stamp.Left, stamp.Top, d, d);
            return true;
        }

        private bool TryBakeLargeMaskDab(CachedDabKey key, out CachedDab dab)
        {
            dab = null!;
            if (key.Size < LargeStampCachedRasterMinDiameterPx)
                return false;

            var mask = MaskFor(key.TipIndex, key.Hardness / 255f, key.Size);
            if (mask.IsEmpty || mask.ColorType != SKColorType.Alpha8)
                return false;

            var layout = ComputeDabLayout(key, mask.Width, mask.Height);
            var allowLod = key.Angle != 0 || key.FlipBits != 0;
            return TryBakeLargeMaskDabCpu(key, mask, layout, allowLod, out dab);
        }

        private bool TryBakeLargeMaskDabCpu(CachedDabKey key, SKBitmap mask, DabLayout layout, bool allowLod, out CachedDab dab)
        {
            dab = null!;
            var bakeW = layout.BitmapWidth;
            var bakeH = layout.BitmapHeight;
            if (bakeW <= 0 || bakeH <= 0)
                return false;

            if (allowLod)
            {
                var cap = LargeDabCpuBakeMaxDimension;
                if (bakeW > cap || bakeH > cap)
                {
                    var shrink = cap / (float)Math.Max(bakeW, bakeH);
                    bakeW = Math.Max(1, (int)MathF.Round(bakeW * shrink));
                    bakeH = Math.Max(1, (int)MathF.Round(bakeH * shrink));
                }
            }

            var effScaleX = layout.RenderScaleX * (layout.BitmapWidth / (float)bakeW);
            var effScaleY = layout.RenderScaleY * (layout.BitmapHeight / (float)bakeH);
            var bitmap = new SKBitmap(new SKImageInfo(bakeW, bakeH, SKColorType.Alpha8, SKAlphaType.Unpremul));
            BakeDabMaskCpu(mask, bakeW, bakeH, effScaleX, effScaleY, layout.AngleDegrees, bitmap);
            dab = new CachedDab(bitmap, layout.OffsetX, layout.OffsetY, layout.LogicalWidth, layout.LogicalHeight);
            return true;
        }

        private static unsafe void BakeDabMaskCpu(
            SKBitmap mask,
            int bakeW,
            int bakeH,
            float scaleX,
            float scaleY,
            float angleDegrees,
            SKBitmap output)
        {
            var maskPtr = (byte*)mask.GetPixels().ToPointer();
            var maskW = mask.Width;
            var maskH = mask.Height;
            var maskStride = mask.RowBytes;
            var outPtr = (byte*)output.GetPixels().ToPointer();
            var outStride = output.RowBytes;
            var cx = bakeW * 0.5f;
            var cy = bakeH * 0.5f;
            var radians = angleDegrees * MathF.PI / 180f;
            var cosA = MathF.Cos(radians);
            var sinA = MathF.Sin(radians);
            var maskOriginX = maskW * 0.5f;
            var maskOriginY = maskH * 0.5f;

            for (var oy = 0; oy < bakeH; oy++)
            {
                var dstRow = outPtr + oy * outStride;
                var py = oy - cy;
                for (var ox = 0; ox < bakeW; ox++)
                {
                    var px = ox - cx;
                    var rx = px * cosA + py * sinA;
                    var ry = -px * sinA + py * cosA;
                    var mx = rx / scaleX + maskOriginX;
                    var my = ry / scaleY + maskOriginY;
                    dstRow[ox] = (byte)SampleMaskBilinear(maskPtr, maskW, maskH, maskStride, mx, my);
                }
            }
        }

        private static unsafe int SampleMaskBilinear(byte* maskPtr, int maskW, int maskH, int maskStride, float mx, float my)
        {
            if (mx < 0f || my < 0f || mx >= maskW || my >= maskH)
                return 0;

            var x0 = (int)mx;
            var y0 = (int)my;
            var x1 = Math.Min(x0 + 1, maskW - 1);
            var y1 = Math.Min(y0 + 1, maskH - 1);
            var tx = mx - x0;
            var ty = my - y0;
            var a00 = maskPtr[y0 * maskStride + x0];
            var a10 = maskPtr[y0 * maskStride + x1];
            var a01 = maskPtr[y1 * maskStride + x0];
            var a11 = maskPtr[y1 * maskStride + x1];
            var top = a00 + (a10 - a00) * tx;
            var bottom = a01 + (a11 - a01) * tx;
            return (int)(top + (bottom - top) * ty + 0.5f);
        }

        private DabLayout ComputeDabLayout(CachedDabKey key, int maskWidth, int maskHeight)
        {
            var baseW = Math.Max(1, maskWidth);
            var baseH = Math.Max(1, maskHeight);
            var scaleX = key.Size / (float)baseW;
            var scaleY = key.Size / (float)baseH;
            var thickness = key.Thickness / 256f;
            if (_brush.TipDirection == BrushTipDirection.Horizontal)
                scaleY *= thickness;
            else
                scaleX *= thickness;
            if (_brush.FlipHorizontal) scaleX = -scaleX;
            if (_brush.FlipVertical) scaleY = -scaleY;

            var halfW = baseW * 0.5f * MathF.Abs(scaleX);
            var halfH = baseH * 0.5f * MathF.Abs(scaleY);
            var angle = key.Angle;
            var radians = angle * MathF.PI / 180f;
            var cosA = MathF.Cos(radians);
            var sinA = MathF.Sin(radians);
            var boxHX = MathF.Abs(angle) > 0.001f
                ? halfW * MathF.Abs(cosA) + halfH * MathF.Abs(sinA)
                : halfW;
            var boxHY = MathF.Abs(angle) > 0.001f
                ? halfW * MathF.Abs(sinA) + halfH * MathF.Abs(cosA)
                : halfH;
            const float margin = 2.0f;
            var logicalW = Math.Max(1, (int)MathF.Ceiling(boxHX * 2f + margin * 2f));
            var logicalH = Math.Max(1, (int)MathF.Ceiling(boxHY * 2f + margin * 2f));

            var bitmapW = logicalW;
            var bitmapH = logicalH;
            if ((long)logicalW * logicalH > MaxCachedDabPixels)
            {
                var shrink = MathF.Sqrt(MaxCachedDabPixels / (float)((long)logicalW * logicalH));
                bitmapW = Math.Max(1, (int)MathF.Round(logicalW * shrink));
                bitmapH = Math.Max(1, (int)MathF.Round(logicalH * shrink));
            }

            var shrinkX = (float)bitmapW / logicalW;
            var shrinkY = (float)bitmapH / logicalH;
            return new DabLayout(
                logicalW,
                logicalH,
                bitmapW,
                bitmapH,
                scaleX * shrinkX,
                scaleY * shrinkY,
                -logicalW / 2,
                -logicalH / 2,
                angle);
        }

        internal void EnterDabCacheUse() { }

        internal void ExitDabCacheUse() { }

        internal void EnterColorDabCacheUse() { }

        internal void ExitColorDabCacheUse() { }

        internal void TrimDabCache() => TrimDabCacheCore();

        internal void TrimColorDabCache() => TrimColorDabCacheCore();

        private void TrimDabCacheCore()
        {
            while (_dabCache.Count > MaxCachedDabs && _dabCacheOrder.Count > 0)
            {
                var key = _dabCacheOrder.Dequeue();
                if (_dabCache.Remove(key, out var old))
                    old.Mask.Dispose();
            }
        }

        private void TrimColorDabCacheCore()
        {
            while (_colorDabCache.Count > MaxCachedColorDabs && _colorDabCacheOrder.Count > 0)
            {
                var key = _colorDabCacheOrder.Dequeue();
                if (_colorDabCache.Remove(key, out var old))
                    old.Bitmap.Dispose();
            }
        }

        private void DisposeTemporary(SKBitmap mask)
        {
            if (ReferenceEquals(mask, Mask)) return;
            if (_maskCache.ContainsValue(mask)) return;
            if (_ownedMasks.Remove(mask))
                mask.Dispose();
        }

        private SKBitmap CacheOrReturnTemporary((int TipIndex, int Hardness, int Resolution) key, SKBitmap mask)
        {
            if (_maskCache.Count >= MaxCachedMasks)
                return mask;

            _maskCache[key] = mask;
            return mask;
        }

        private static int QuantizeHardness(float hardness)
            => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

        private static unsafe SKBitmap MultiplyMasks(SKBitmap tip, SKBitmap shape)
        {
            var size = Math.Max(Math.Max(tip.Width, tip.Height), Math.Max(shape.Width, shape.Height));
            var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
            var a = (byte*)tip.GetPixels().ToPointer();
            var b = (byte*)shape.GetPixels().ToPointer();
            var dst = (byte*)bmp.GetPixels().ToPointer();
            var aStride = tip.RowBytes;
            var bStride = shape.RowBytes;
            var dStride = bmp.RowBytes;
            var tw = Math.Min(tip.Width, size);
            var th = Math.Min(tip.Height, size);
            var sw = Math.Min(shape.Width, size);
            var sh = Math.Min(shape.Height, size);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var ta = y < th && x < tw ? a[y * aStride + x] : (byte)0;
                    var sa = y < sh && x < sw ? b[y * bStride + x] : (byte)0;
                    dst[y * dStride + x] = (byte)(ta * sa / 255);
                }
            return bmp;
        }

        public void Dispose()
        {
            foreach (var mask in _maskCache.Values)
                if (_ownedMasks.Remove(mask))
                    mask.Dispose();
            foreach (var mask in _ownedMasks)
                mask.Dispose();
            foreach (var dab in _dabCache.Values)
                dab.Mask.Dispose();
            foreach (var dab in _colorDabCache.Values)
                dab.Bitmap.Dispose();
            if (_ownedTipSet != null)
            {
                foreach (var tip in _ownedTipSet)
                    if (tip is IDisposable disposable)
                        disposable.Dispose();
                _ownedTipSet.Clear();
            }
            _maskCache.Clear();
            _dabCache.Clear();
            _dabCacheOrder.Clear();
            _colorDabCache.Clear();
            _colorDabCacheOrder.Clear();
            _ownedMasks.Clear();
            StrokeMaskTiles.Clear();
            BeforeTiles = null;
            Paint.Dispose();
        }

        private static SKColor ToSkColor(Color c) => new(c.R, c.G, c.B, c.A);

        public sealed class CachedDab(SKBitmap mask, int offsetX, int offsetY, int logicalWidth, int logicalHeight)
        {
            public SKBitmap Mask { get; } = mask;
            public int OffsetX { get; } = offsetX;
            public int OffsetY { get; } = offsetY;
            public int LogicalWidth { get; } = logicalWidth;
            public int LogicalHeight { get; } = logicalHeight;
            public float MaskScaleX => (float)LogicalWidth / Mask.Width;
            public float MaskScaleY => (float)LogicalHeight / Mask.Height;
            public bool IsScaled => LogicalWidth != Mask.Width || LogicalHeight != Mask.Height;
        }

        public sealed class CachedColorDab(SKBitmap bitmap, int offsetX, int offsetY, int logicalWidth, int logicalHeight)
        {
            public SKBitmap Bitmap { get; } = bitmap;
            public int OffsetX { get; } = offsetX;
            public int OffsetY { get; } = offsetY;
            public int LogicalWidth { get; } = logicalWidth;
            public int LogicalHeight { get; } = logicalHeight;
            public float MaskScaleX => (float)LogicalWidth / Bitmap.Width;
            public float MaskScaleY => (float)LogicalHeight / Bitmap.Height;
            public bool IsScaled => LogicalWidth != Bitmap.Width || LogicalHeight != Bitmap.Height;
        }

        private readonly record struct CachedDabKey(int Size, int Hardness, int Thickness, int Angle, int FlipBits, int TipIndex)
        {
            public static CachedDabKey From(BrushPreset brush, StampSample stamp)
            {
                var size = Math.Clamp((int)MathF.Round(stamp.Size), 1, 4096);
                if (brush.Size >= LargeStampCachedRasterMinDiameterPx)
                    size = QuantizeLargeStampCacheSize(size);
                var hardness = Math.Clamp((int)MathF.Round(Math.Clamp(stamp.Hardness, 0.001f, 1f) * 255f), 0, 255);
                var thickness = Math.Clamp((int)MathF.Round(Math.Clamp((float)brush.TipThickness * stamp.TipThicknessMultiplier, 0.01f, 4f) * 256f), 3, 1024);
                var angle = QuantizeAngle(stamp.Angle, brush);
                var flipBits = (brush.FlipHorizontal ? 1 : 0) | (brush.FlipVertical ? 2 : 0);
                return new CachedDabKey(size, hardness, thickness, angle, flipBits, Math.Max(0, stamp.TipIndex));
            }

            private static int QuantizeAngle(float angle, BrushPreset brush)
            {
                var tip = brush.Tip as ProceduralBrushTip;
                var isCircle = tip?.Shape is BrushTipShape.Circle or BrushTipShape.SoftRound;
                if (isCircle && Math.Abs(brush.TipThickness - 1.0) < 0.001)
                    return 0;

                var normalized = angle % 360f;
                if (normalized < 0) normalized += 360f;
                return Math.Clamp((int)MathF.Round(normalized / 2f) * 2, 0, 358);
            }
        }
    }

    // Value noise at canvas pixel coordinates for grain/paper texture.
    // Uses 4-pixel cells with bilinear interpolation so grain has a natural scale.
    private static float GrainNoise(int cx, int cy)
    {
        int gx = cx >> 2, gy = cy >> 2;
        float fx = (cx & 3) * 0.25f, fy = (cy & 3) * 0.25f;
        float h00 = HashF(gx, gy), h10 = HashF(gx + 1, gy);
        float h01 = HashF(gx, gy + 1), h11 = HashF(gx + 1, gy + 1);
        return h00 + (h10 - h00) * fx + (h01 - h00) * fy + (h00 - h10 - h01 + h11) * fx * fy;
    }

    private static float HashF(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17; h *= 0xbf324c81u;
            h ^= h >> 13; h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0f;
        }
    }

    private static bool PreferCachedTileMajorRaster(BrushPreset brush)
        => brush.Size >= LargeStampCachedRasterMinDiameterPx;

    private static int QuantizeLargeStampCacheSize(int size)
    {
        if (size >= 1024) return ((size + 127) / 128) * 128;
        if (size >= 512) return ((size + 63) / 64) * 64;
        return ((size + 31) / 32) * 32;
    }

    public static bool UsesProceduralStampEvaluation(BrushPreset brush, IBrushTip primaryTip, int stampColorCount)
    {
        if (stampColorCount != 0) return false;
        if (brush.ColorMix) return false;
        if (!SupportsCpuRasterBlendMode(brush.BlendMode)) return false;
        if (brush.Shape != null) return false;
        if (HasMultiTipSelection(brush)) return false;
        if (primaryTip.HasColor) return false;

        if (BrushTipMaskRasterization.PrefersMaskRasterization(primaryTip))
            return false;

        return primaryTip switch
        {
            ImageBrushTip => true,
            NodeBrushTip { IsDirectImageSampler: true } => true,
            _ => false
        };
    }

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
