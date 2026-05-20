using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Paints strokes onto the active layer using bounded UI-thread work slices.
// Each pen stroke is a transaction. Finished strokes can continue rendering
// while later pointer events queue new transactions instead of blocking pen-up.
public sealed class DirectDrawOutput : IOutputProcess
{
    private const long PreviewNotifyIntervalMs = 12;
    private const double RenderSliceBudgetMs = 5.0;
    private const int MaxSegmentsPerSlice = 24;
    private const int InitialSegmentsPerSlice = 8;
    private const int SynchronousFinishSegmentLimit = 4;
    private const int TargetMaxDabsPerQueuedSegment = 96;
    private const double MinQueuedSegmentLength = 8.0;
    private const double MaxQueuedSegmentLength = 96.0;

    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;
    private readonly BrushPreparationScheduler _preparationScheduler = new();
    private readonly Queue<StrokeTransaction> _pendingTransactions = new();

    public bool Antialiasing { get; set; } = true;

    private StrokeTransaction? _active;
    private StrokeTransaction? _accepting;
    private bool _processingScheduled;
    private bool _processing;

    public DirectDrawOutput(BrushEngine brushEngine, DrawingDocument _)
    {
        _brushEngine = brushEngine;
    }

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var samples = stroke.SmoothedSamples;
        var tx = _accepting is { FinalizeRequested: false }
            ? _accepting
            : BeginTransaction(ctx, layer, samples[0]);

        QueueNewSamples(tx, layer, ctx.Brush, samples);
        if (tx.NextSegmentIndex == 1 &&
            tx.QueuedSamples.Count == 1 &&
            tx.QueuedSamples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
        {
            tx.QueuedSamples.Add(tx.QueuedSamples[0]);
        }

        EnsureActiveTransaction();
        ScheduleProcessQueued();
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0)
        {
            _accepting = null;
            return;
        }

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked)
        {
            _accepting = null;
            return;
        }

        Preview(ctx, input);
        if (_accepting != null)
        {
            _accepting.FinalizeRequested = true;
            _accepting = null;
        }

        EnsureActiveTransaction();
        if (_active is { } active && active.RemainingSegments <= SynchronousFinishSegmentLimit)
            ProcessQueuedSegments(force: true);
        else
            ScheduleProcessQueued();
    }

    private StrokeTransaction BeginTransaction(ToolContext ctx, DrawingLayer layer, CanvasInputSample firstSample)
    {
        _preparationScheduler.QueuePrepare(ctx.Brush, firstSample);
        var tx = new StrokeTransaction(ctx, layer, ctx.ActiveLayerIndex, ctx.Brush, ToLayerSample(layer, firstSample));
        if (_active == null)
            _active = tx;
        else
            _pendingTransactions.Enqueue(tx);
        _accepting = tx;
        return tx;
    }

    private static void QueueNewSamples(StrokeTransaction tx, DrawingLayer layer, BrushPreset brush, IReadOnlyList<CanvasInputSample> samples)
    {
        var start = Math.Max(0, tx.LastQueuedInputIndex + 1);
        for (var i = start; i < samples.Count; i++)
        {
            var sample = ToLayerSample(layer, samples[i]);
            AppendBoundedSample(tx, brush, sample);
            tx.LastQueuedInputIndex = i;
        }
    }

    private static void AppendBoundedSample(StrokeTransaction tx, BrushPreset brush, CanvasInputSample sample)
    {
        if (tx.QueuedSamples.Count == 0)
        {
            tx.QueuedSamples.Add(sample);
            return;
        }

        var previous = tx.QueuedSamples[^1];
        var distance = previous.DistanceTo(sample);
        var maxLength = QueuedSegmentLength(brush);
        if (distance <= maxLength)
        {
            tx.QueuedSamples.Add(sample);
            return;
        }

        var pieces = Math.Clamp((int)Math.Ceiling(distance / maxLength), 1, 4096);
        for (var i = 1; i <= pieces; i++)
        {
            var t = i / (double)pieces;
            tx.QueuedSamples.Add(LerpSample(previous, sample, t));
        }
    }

    private static double QueuedSegmentLength(BrushPreset brush)
    {
        var flow = Math.Clamp(brush.Flow, 0.01, 1.0);
        var spacing = Math.Clamp(brush.Spacing, 0.005, 4.0);
        var dabSpacing = Math.Max(0.5, brush.Size * spacing * Math.Sqrt(flow));
        return Math.Clamp(dabSpacing * TargetMaxDabsPerQueuedSegment, MinQueuedSegmentLength, MaxQueuedSegmentLength);
    }

    private static CanvasInputSample LerpSample(CanvasInputSample from, CanvasInputSample to, double t)
    {
        return new CanvasInputSample(
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
    }

    private void EnsureActiveTransaction()
    {
        if (_active != null)
            return;

        if (_pendingTransactions.Count > 0)
            _active = _pendingTransactions.Dequeue();
    }

    private void ScheduleProcessQueued()
    {
        if (_processingScheduled || _processing)
            return;

        _processingScheduled = true;
        Dispatcher.UIThread.Post(() => ProcessQueuedSegments(force: false), DispatcherPriority.Background);
    }

    private async void ProcessQueuedSegments(bool force)
    {
        _processingScheduled = false;
        if (_processing)
            return;

        EnsureActiveTransaction();
        var tx = _active;
        if (tx == null)
            return;

        _processing = true;
        var started = Stopwatch.GetTimestamp();
        var processed = 0;
        var batchDirty = PixelRegion.Empty;
        var scheduleAfterProcessing = false;

        try
        {
            if (!tx.StrokeStarted)
            {
                _brushEngine.BeginStroke(tx.Brush, tx.FirstSample);
                tx.StrokeStarted = true;
            }

            // Fast path: small remaining segments run synchronously to avoid
            // async overhead for the final 1-4 segments.
            if (force && tx.RemainingSegments <= SynchronousFinishSegmentLimit)
            {
                ProcessSegmentsSync(tx, force, ref started, ref processed, ref batchDirty, ref scheduleAfterProcessing);
                return;
            }

            while (tx.NextSegmentIndex < tx.QueuedSamples.Count)
            {
                var remaining = tx.QueuedSamples.Count - tx.NextSegmentIndex;
                var segmentCount = force
                    ? remaining
                    : Math.Min(remaining, tx.SuggestedSegmentCount(RenderSliceBudgetMs, InitialSegmentsPerSlice, MaxSegmentsPerSlice));

                // Offload heavy rasterization to thread pool. The DocumentRenderLock
                // and TiledPixelBuffer internal locks provide cross-thread safety.
                // The continuation runs on the captured SynchronizationContext (UI thread).
                var result = await Task.Run(() =>
                {
                    var segStarted = Stopwatch.GetTimestamp();
                    var dirty = ProcessSegmentBatch(tx, tx.NextSegmentIndex, segmentCount);
                    return (dirty, segmentCount, ElapsedMs(segStarted));
                });

                tx.NextSegmentIndex += result.segmentCount;
                processed += result.segmentCount;
                tx.RecordSegmentTime(result.Item3, result.segmentCount);

                if (!result.dirty.IsEmpty)
                    batchDirty = batchDirty.Union(result.dirty);

                if (!force && processed >= MaxSegmentsPerSlice)
                    break;
                if (!force && ElapsedMs(started) >= RenderSliceBudgetMs)
                    break;
            }

            if (!batchDirty.IsEmpty)
            {
                tx.PendingPreviewDirty = tx.PendingPreviewDirty.Union(batchDirty);
                FlushPreviewDirty(tx, force: force || tx.FinalizeRequested);
            }

            if (tx.NextSegmentIndex < tx.QueuedSamples.Count)
            {
                scheduleAfterProcessing = true;
                return;
            }

            if (tx.FinalizeRequested)
            {
                FlushPreviewDirty(tx, force: true);
                _brushEngine.EndStroke();
                Commit(tx);
                _active = null;
                EnsureActiveTransaction();
                if (_active != null)
                    scheduleAfterProcessing = true;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "DirectDrawOutput.ProcessQueuedSegments", flushToDisk: true);
            _brushEngine.EndStroke();
            RestoreTransaction(tx);
            _active = null;
            _accepting = ReferenceEquals(_accepting, tx) ? null : _accepting;
            EnsureActiveTransaction();
            if (_active != null)
                scheduleAfterProcessing = true;
        }
        finally
        {
            _processing = false;
            if (scheduleAfterProcessing)
                ScheduleProcessQueued();
        }
    }

    private void ProcessSegmentsSync(StrokeTransaction tx, bool force,
        ref long started, ref int processed, ref PixelRegion batchDirty,
        ref bool scheduleAfterProcessing)
    {
        while (tx.NextSegmentIndex < tx.QueuedSamples.Count)
        {
            var remaining = tx.QueuedSamples.Count - tx.NextSegmentIndex;
            var segmentCount = force
                ? remaining
                : Math.Min(remaining, tx.SuggestedSegmentCount(RenderSliceBudgetMs, InitialSegmentsPerSlice, MaxSegmentsPerSlice));

            var segmentStarted = Stopwatch.GetTimestamp();
            batchDirty = batchDirty.Union(ProcessSegmentBatch(tx, tx.NextSegmentIndex, segmentCount));
            tx.NextSegmentIndex += segmentCount;
            processed += segmentCount;
            tx.RecordSegmentTime(ElapsedMs(segmentStarted), segmentCount);

            if (!force && processed >= MaxSegmentsPerSlice)
                break;
            if (!force && ElapsedMs(started) >= RenderSliceBudgetMs)
                break;
        }

        if (!batchDirty.IsEmpty)
        {
            tx.PendingPreviewDirty = tx.PendingPreviewDirty.Union(batchDirty);
            FlushPreviewDirty(tx, force: force || tx.FinalizeRequested);
        }

        if (tx.NextSegmentIndex < tx.QueuedSamples.Count)
        {
            scheduleAfterProcessing = true;
            return;
        }

        if (tx.FinalizeRequested)
        {
            FlushPreviewDirty(tx, force: true);
            _brushEngine.EndStroke();
            Commit(tx);
            _active = null;
            EnsureActiveTransaction();
            if (_active != null)
                scheduleAfterProcessing = true;
        }
    }

    private void FlushPreviewDirty(StrokeTransaction tx, bool force)
    {
        if (tx.PendingPreviewDirty.IsEmpty) return;

        var now = Environment.TickCount64;
        if (!force && now - tx.LastPreviewNotifyMs < PreviewNotifyIntervalMs)
            return;

        tx.Ctx.Document.NotifyChanged(tx.PendingPreviewDirty, tx.LayerIndex);
        tx.Ctx.InvalidateRender();
        tx.PendingPreviewDirty = PixelRegion.Empty;
        tx.LastPreviewNotifyMs = now;
    }

    private PixelRegion ProcessSegmentBatch(StrokeTransaction tx, int startSegmentIndex, int segmentCount)
    {
        using var mutation = tx.Ctx.Document.RenderLock.Write();
        using var telemetry = RenderTelemetry.Scope("Brush");

        var region = EstimateSegmentBatchRegion(tx, startSegmentIndex, segmentCount);
        if (!region.IsEmpty)
            tx.Layer.CaptureTiles(region, tx.BeforeTiles);

        var started = Stopwatch.GetTimestamp();
        var dirty = _brushEngine.RasterizeSegments(tx.Layer, tx.Brush, tx.QueuedSamples, startSegmentIndex, segmentCount,
            (x, y, out b, out g, out r, out a) => ReadBeforeStrokePixelFrom(tx.BeforeTiles, tx.Layer, x, y, out b, out g, out r, out a));
        RenderTelemetry.RecordBrush(ElapsedMs(started), _brushEngine.LastStats.Path, _brushEngine.LastStats.StampCount, _brushEngine.LastStats.CachedDabCount);

        if (!dirty.IsEmpty)
        {
            RestoreUnselectedPixels(tx.Layer, dirty, tx.Ctx.Selection, tx.BeforeTiles);

            var translatedDirty = dirty.Translate(tx.Layer.OffsetX, tx.Layer.OffsetY);
            tx.DirtyRegion = tx.DirtyRegion.Union(translatedDirty);
            return translatedDirty;
        }

        return PixelRegion.Empty;
    }

    private PixelRegion EstimateSegmentBatchRegion(StrokeTransaction tx, int startSegmentIndex, int segmentCount)
    {
        var endSegmentIndex = Math.Min(tx.QueuedSamples.Count - 1, startSegmentIndex + segmentCount - 1);
        var region = PixelRegion.Empty;
        for (var i = startSegmentIndex; i <= endSegmentIndex; i++)
            region = region.Union(_brushEngine.EstimateSegmentRegion(tx.Layer, tx.Brush, tx.QueuedSamples[i - 1], tx.QueuedSamples[i]));
        return region;
    }

    private static void Commit(StrokeTransaction tx)
    {
        try
        {
            using var mutation = tx.Ctx.Document.RenderLock.Write();
            if (tx.BeforeTiles.Count > 0)
            {
                var tileDirty = ComputeTileDirtyRegion(tx.BeforeTiles).Translate(tx.Layer.OffsetX, tx.Layer.OffsetY);
                if (!tileDirty.IsEmpty)
                {
                    tx.Layer.MarkThumbnailDirty();
                    tx.Ctx.Document.CommitLayerTileMutation(tx.LayerIndex, tx.BeforeTiles, tileDirty);
                }
            }

            tx.Ctx.Document.CommitStroke();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "DirectDrawOutput.Commit");
        }
    }

    public void Cancel()
    {
        _brushEngine.EndStroke();

        if (_active != null)
            RestoreTransaction(_active);

        while (_pendingTransactions.Count > 0)
            RestoreTransaction(_pendingTransactions.Dequeue());

        _active = null;
        _accepting = null;
        _processingScheduled = false;
    }

    private static void RestoreTransaction(StrokeTransaction tx)
    {
        if (tx.BeforeTiles.Count == 0)
            return;

        using var mutation = tx.Ctx.Document.RenderLock.Write();
        foreach (var ((tileX, tileY), tile) in tx.BeforeTiles)
            tx.Layer.RestoreTile(tileX, tileY, tile);

        var tileDirty = ComputeTileDirtyRegion(tx.BeforeTiles).Translate(tx.Layer.OffsetX, tx.Layer.OffsetY);
        if (!tileDirty.IsEmpty)
        {
            tx.Layer.MarkThumbnailDirty();
            tx.Ctx.Document.NotifyChanged(tileDirty, tx.LayerIndex);
            tx.Ctx.InvalidateRender();
        }
    }

    private static PixelRegion ComputeTileDirtyRegion(Dictionary<(int, int), byte[]?> tiles)
    {
        if (tiles.Count == 0) return PixelRegion.Empty;
        const int ts = TiledPixelBuffer.TileSize;
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        foreach (var ((tx, ty), _) in tiles)
        {
            minX = Math.Min(minX, tx * ts);
            minY = Math.Min(minY, ty * ts);
            maxX = Math.Max(maxX, tx * ts + ts);
            maxY = Math.Max(maxY, ty * ts + ts);
        }
        return new PixelRegion(minX, minY, maxX - minX, maxY - minY);
    }

    private static CanvasInputSample ToLayerSample(DrawingLayer layer, CanvasInputSample s)
        => s.WithPosition(s.X - layer.OffsetX, s.Y - layer.OffsetY, s.Pressure, s.TimeMicros);

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);

    private static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static void ReadBeforeStrokePixelFrom(Dictionary<(int, int), byte[]?> beforeTiles, DrawingLayer currentLayer, int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        const int ts = TiledPixelBuffer.TileSize;
        var tx = FloorDiv(x, ts);
        var ty = FloorDiv(y, ts);
        if (!beforeTiles.TryGetValue((tx, ty), out var tile) || tile == null)
        {
            currentLayer.Pixels.GetPixel(x, y, out b, out g, out r, out a);
            return;
        }

        var lx = x - tx * ts;
        var ly = y - ty * ts;
        var offset = (ly * ts + lx) * 4;
        b = tile[offset];
        g = tile[offset + 1];
        r = tile[offset + 2];
        a = tile[offset + 3];
    }

    private static void RestoreUnselectedPixels(DrawingLayer layer, PixelRegion dirty, SelectionMask selection, Dictionary<(int, int), byte[]?> beforeTiles)
    {
        if (dirty.IsEmpty) return;

        bool hasSelection = selection.HasSelection;
        bool alphaLocked = layer.IsAlphaLocked;
        if (!hasSelection && !alphaLocked) return;

        const int ts = TiledPixelBuffer.TileSize;
        int firstTileX = FloorDiv(dirty.X, ts);
        int firstTileY = FloorDiv(dirty.Y, ts);
        int lastTileX = FloorDiv(dirty.Right - 1, ts);
        int lastTileY = FloorDiv(dirty.Bottom - 1, ts);

        for (int ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (int tx = firstTileX; tx <= lastTileX; tx++)
            {
                beforeTiles.TryGetValue((tx, ty), out var beforeTile);
                if (alphaLocked && beforeTile != null && IsTileAllZero(beforeTile))
                    continue;

                int pxMin = Math.Max(dirty.X, tx * ts);
                int pxMax = Math.Min(dirty.Right, tx * ts + ts);
                int pyMin = Math.Max(dirty.Y, ty * ts);
                int pyMax = Math.Min(dirty.Bottom, ty * ts + ts);

                for (int py = pyMin; py < pyMax; py++)
                {
                    int ly = py - ty * ts;
                    for (int px = pxMin; px < pxMax; px++)
                    {
                        bool inSelection = !hasSelection || selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY);
                        int lx = px - tx * ts;
                        int offset = (ly * ts + lx) * 4;
                        bool hadAlpha = !alphaLocked || (beforeTile != null && beforeTile[offset + 3] > 0);

                        if (inSelection && hadAlpha) continue;

                        if (beforeTile != null)
                            layer.Pixels.SetPixel(px, py, beforeTile[offset], beforeTile[offset + 1], beforeTile[offset + 2], beforeTile[offset + 3]);
                        else
                            layer.Pixels.SetPixel(px, py, 0, 0, 0, 0);
                    }
                }
            }
        }
    }

    private static unsafe bool IsTileAllZero(byte[] tile)
    {
        fixed (byte* p = tile)
        {
            var len = tile.Length / 4;
            var w = (uint*)p;
            for (var i = 0; i < len; i++)
                if (w[i] != 0) return false;
        }
        return true;
    }

    private sealed class StrokeTransaction
    {
        public StrokeTransaction(ToolContext ctx, DrawingLayer layer, int layerIndex, BrushPreset brush, CanvasInputSample firstSample)
        {
            Ctx = ctx;
            Layer = layer;
            LayerIndex = layerIndex;
            Brush = brush;
            FirstSample = firstSample;
        }

        public ToolContext Ctx { get; }
        public DrawingLayer Layer { get; }
        public int LayerIndex { get; }
        public BrushPreset Brush { get; }
        public CanvasInputSample FirstSample { get; }
        public Dictionary<(int, int), byte[]?> BeforeTiles { get; } = new();
        public List<CanvasInputSample> QueuedSamples { get; } = [];
        public int LastQueuedInputIndex { get; set; } = -1;
        public int NextSegmentIndex { get; set; } = 1;
        public bool StrokeStarted { get; set; }
        public bool FinalizeRequested { get; set; }
        public PixelRegion DirtyRegion { get; set; } = PixelRegion.Empty;
        public PixelRegion PendingPreviewDirty { get; set; } = PixelRegion.Empty;
        public long LastPreviewNotifyMs { get; set; }
        public double AverageSegmentMs { get; private set; }
        public int RemainingSegments => Math.Max(0, QueuedSamples.Count - NextSegmentIndex);

        public int SuggestedSegmentCount(double targetMs, int initial, int max)
        {
            if (AverageSegmentMs <= 0.001)
                return Math.Clamp(initial, 1, max);

            return Math.Clamp((int)Math.Floor(targetMs / AverageSegmentMs), 1, max);
        }

        public void RecordSegmentTime(double elapsedMs, int segmentCount)
        {
            if (segmentCount <= 0 || elapsedMs <= 0) return;
            var perSegment = elapsedMs / segmentCount;
            AverageSegmentMs = AverageSegmentMs <= 0.001
                ? perSegment
                : AverageSegmentMs * 0.75 + perSegment * 0.25;
        }
    }
}
