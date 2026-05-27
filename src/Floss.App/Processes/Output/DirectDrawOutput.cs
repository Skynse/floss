using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
    private const double RenderSliceBudgetMs = 3.0;
    private const int MaxSegmentsPerSlice = 8;
    private const int InitialSegmentsPerSlice = 1;

    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;
    private readonly BrushPreparationScheduler _preparationScheduler = new();
    private readonly Queue<StrokeTransaction> _pendingTransactions = new();
    private readonly Action _processQueuedAction;

    public bool Antialiasing { get; set; } = true;

    private StrokeTransaction? _active;
    private StrokeTransaction? _accepting;
    private bool _processingScheduled;
    private bool _processing;

    public DirectDrawOutput(BrushEngine brushEngine, DrawingDocument _)
    {
        _brushEngine = brushEngine;
        _processQueuedAction = () => ProcessQueuedSegments(force: false);
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

        // Cache Blend-smudge pickup delegates once per stroke to avoid
        // two heap-allocated closure objects per ProcessSegmentBatch.
        if (ctx.Brush.ColorMix && ctx.Brush.SmudgeMode == SmudgeMode.Blend)
        {
            tx.PickupSampler = (x, y, out b, out g, out r, out a) =>
                ReadBeforeStrokePixelFrom(tx.BeforeTiles, tx.Layer, x, y, out b, out g, out r, out a);
            tx.PickupTiles = (tileX, tileY) => ReadBeforeStrokeTileFrom(tx.BeforeTiles, tx.Layer, tileX, tileY);
        }

        // Krita-style stroke suspend: hint a generous initial bounding box
        // around the first stamp. The compositor will only process invalidations
        // inside this region (extended per-stamp below) until the stroke ends —
        // unrelated pending dirties are deferred.
        var radius = Math.Max(64, (int)Math.Ceiling(ctx.Brush.Size + 32));
        var docX = (int)Math.Round(firstSample.X) + layer.OffsetX;
        var docY = (int)Math.Round(firstSample.Y) + layer.OffsetY;
        var initialRegion = new PixelRegion(docX - radius, docY - radius, radius * 2, radius * 2)
            .ClipTo(ctx.Document.Width, ctx.Document.Height);
        if (!initialRegion.IsEmpty)
            ctx.Document.NotifyStrokeSuspendBegin(initialRegion, ctx.ActiveLayerIndex);

        return tx;
    }

    private static void QueueNewSamples(StrokeTransaction tx, DrawingLayer layer, BrushPreset brush, IReadOnlyList<CanvasInputSample> samples)
    {
        var start = Math.Max(0, tx.LastQueuedInputIndex + 1);
        for (var i = start; i < samples.Count; i++)
        {
            var sample = ToLayerSample(layer, samples[i]);
            AppendBoundedSample(tx, sample);
            tx.LastQueuedInputIndex = i;
        }
    }

    private static void AppendBoundedSample(StrokeTransaction tx, CanvasInputSample sample)
    {
        tx.QueuedSamples.Add(sample);
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
        Dispatcher.UIThread.Post(_processQueuedAction, DispatcherPriority.Normal);
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

            while (tx.NextSegmentIndex < tx.QueuedSamples.Count)
            {
                var remaining = tx.QueuedSamples.Count - tx.NextSegmentIndex;
                var segmentCount = force
                    ? remaining
                    : Math.Min(remaining, tx.SuggestedSegmentCount(RenderSliceBudgetMs, InitialSegmentsPerSlice, MaxSegmentsPerSlice));

                // CRITICAL: snapshot the sample slice BEFORE Task.Run. tx.QueuedSamples
                // is mutated on the UI thread (Preview adds samples while we render).
                // List<>.Add can reallocate its backing array — a reader on the background
                // thread could observe a torn reference and crash with a native AV with
                // no managed stack. The snapshot is a stable read-only copy.
                var startIndex = tx.NextSegmentIndex;
                var snapshot = SnapshotSamples(tx.QueuedSamples, startIndex - 1, segmentCount + 1);
                var snapshotStart = 1; // first sample is segment-start anchor

                var result = await Task.Run(() =>
                {
                    var segStarted = Stopwatch.GetTimestamp();
                    var dirty = ProcessSegmentBatch(tx, snapshot, snapshotStart, segmentCount);
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
            var startIndex = tx.NextSegmentIndex;
            var snapshot = SnapshotSamples(tx.QueuedSamples, startIndex - 1, segmentCount + 1);
            batchDirty = batchDirty.Union(ProcessSegmentBatch(tx, snapshot, 1, segmentCount));
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

        // Extend the stroke suspend region so the compositor knows the brush
        // wandered. Generously inflated so the brush radius fits comfortably.
        tx.Ctx.Document.NotifyStrokeSuspendExtend(tx.PendingPreviewDirty);
        tx.Ctx.Document.NotifyChanged(tx.PendingPreviewDirty, tx.LayerIndex);
        tx.Ctx.InvalidateRender();
        tx.PendingPreviewDirty = PixelRegion.Empty;
        tx.LastPreviewNotifyMs = now;
    }

    private PixelRegion ProcessSegmentBatch(StrokeTransaction tx, CanvasInputSample[] samples, int startSegmentIndex, int segmentCount)
    {
        // Do NOT take DocumentRenderLock.Write here. This method runs on a
        // background thread (Task.Run). Holding the document write lock for the
        // entire rasterize blocks DrawingCanvas.Render on the UI thread at
        // RenderLock.Read — the app freezes with no crash log. Tile mutations
        // are already serialized via TiledPixelBuffer's pixel read/write locks.
        using var telemetry = RenderTelemetry.ScopeNow();

        var region = EstimateSegmentBatchRegion(tx, samples, startSegmentIndex, segmentCount);
        if (!region.IsEmpty)
        {
            tx.Layer.Pixels.EnterPixelReadLock();
            try
            {
                tx.Layer.CaptureTiles(region, tx.BeforeTiles);
            }
            finally
            {
                tx.Layer.Pixels.ExitPixelReadLock();
            }
        }

        var started = Stopwatch.GetTimestamp();
        var pickupSampler = tx.PickupSampler;
        var pickupTiles = tx.PickupTiles;

        _brushEngine.CanvasZoom = tx.Ctx.Viewport?.Zoom ?? 1.0;
        var dirty = _brushEngine.RasterizeSegments(tx.Layer, tx.Brush, samples, startSegmentIndex, segmentCount,
            pickupSampler, pickupTiles);
        RenderTelemetry.RecordBrush(ElapsedMs(started), _brushEngine.LastStats.Path, _brushEngine.LastStats.StampCount, _brushEngine.LastStats.CachedDabCount);

        if (!dirty.IsEmpty)
        {
            tx.Layer.Pixels.EnterPixelWriteLock();
            try
            {
                RestoreUnselectedPixels(tx.Layer, dirty, tx.Ctx.Selection, tx.BeforeTiles);
            }
            finally
            {
                tx.Layer.Pixels.ExitPixelWriteLock();
            }

            var translatedDirty = dirty.Translate(tx.Layer.OffsetX, tx.Layer.OffsetY);
            tx.DirtyRegion = tx.DirtyRegion.Union(translatedDirty);
            return translatedDirty;
        }

        return PixelRegion.Empty;
    }

    private PixelRegion EstimateSegmentBatchRegion(StrokeTransaction tx, CanvasInputSample[] samples, int startSegmentIndex, int segmentCount)
    {
        var endSegmentIndex = Math.Min(samples.Length - 1, startSegmentIndex + segmentCount - 1);
        var region = PixelRegion.Empty;
        for (var i = startSegmentIndex; i <= endSegmentIndex; i++)
            region = region.Union(_brushEngine.EstimateSegmentRegion(tx.Layer, tx.Brush, samples[i - 1], samples[i]));
        return region;
    }

    private static CanvasInputSample[] SnapshotSamples(List<CanvasInputSample> source, int startIndex, int count)
    {
        var available = Math.Max(0, source.Count - Math.Max(0, startIndex));
        var actual = Math.Min(count, available);
        var snapshot = new CanvasInputSample[actual];
        for (var i = 0; i < actual; i++)
            snapshot[i] = source[startIndex + i];
        return snapshot;
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
        finally
        {
            // Always release the stroke-suspend on the compositor so deferred
            // invalidations from elsewhere get flushed even if Commit threw.
            tx.Ctx.Document.NotifyStrokeSuspendEnd();
        }
    }

    public void Cancel()
    {
        WaitForProcessingToFinish();
        _brushEngine.EndStroke();

        if (_active != null)
            RestoreTransaction(_active);

        while (_pendingTransactions.Count > 0)
            RestoreTransaction(_pendingTransactions.Dequeue());

        _active = null;
        _accepting = null;
        _processingScheduled = false;
    }

    public bool HasPendingWork =>
        _processing || _processingScheduled || _active != null || _pendingTransactions.Count > 0;

    // Marks the active/accepting transaction as finalize-requested so the
    // background task will commit it normally, then detaches _accepting.
    // Call this when switching away from the tool while drawing — do NOT
    // call Cancel(), which would block the UI thread and revert the stroke.
    public void FinalizeAccepting()
    {
        if (_accepting != null)
        {
            _accepting.FinalizeRequested = true;
            _accepting = null;
        }
        if (_active != null && !_active.FinalizeRequested)
            _active.FinalizeRequested = true;
        ScheduleProcessQueued();
    }

    private void WaitForProcessingToFinish()
    {
        // Wait up to 2 seconds for the background rasterize Task.Run to complete.
        // The previous bounded spin (~1 ms) could bail before processing finished,
        // letting the UI thread mutate tx.BeforeTiles / tx.QueuedSamples concurrently
        // with the background reader — a major source of native crashes with no log.
        var deadline = Environment.TickCount64 + 2000;
        var spin = new SpinWait();
        while (_processing && Environment.TickCount64 < deadline)
            spin.SpinOnce();
    }

    public void WaitUntilIdle()
    {
        WaitForProcessingToFinish();
        FlushPending();
        WaitForProcessingToFinish();
    }

    public void FlushPending()
    {
        if (_processing)
            return;

        _processingScheduled = false;
        EnsureActiveTransaction();
        while (_active is { } tx)
        {
            if (!tx.StrokeStarted)
            {
                _brushEngine.BeginStroke(tx.Brush, tx.FirstSample);
                tx.StrokeStarted = true;
            }

            var started = Stopwatch.GetTimestamp();
            var processed = 0;
            var batchDirty = PixelRegion.Empty;
            var scheduleAfterProcessing = false;
            ProcessSegmentsSync(tx, force: true, ref started, ref processed, ref batchDirty, ref scheduleAfterProcessing);
            if (ReferenceEquals(_active, tx))
                break;
        }
    }

    private static void RestoreTransaction(StrokeTransaction tx)
    {
        try
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
        finally
        {
            tx.Ctx.Document.NotifyStrokeSuspendEnd();
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

    private static byte[]? ReadBeforeStrokeTileFrom(Dictionary<(int, int), byte[]?> beforeTiles, DrawingLayer currentLayer, int tileX, int tileY)
    {
        // beforeTiles holds the layer's pre-stroke snapshot for tiles already
        // touched by this stroke. For tiles outside that set, fall back to the
        // live layer state (which hasn't been written yet for that tile).
        if (beforeTiles.TryGetValue((tileX, tileY), out var captured))
            return captured;

        return currentLayer.Pixels.GetTileOrNull(tileX, tileY);
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

        // Caller (ProcessSegmentBatch) already holds the pixel write lock.
        for (int ty = firstTileY; ty <= lastTileY; ty++)
        {
            var tilePixY = ty * ts;
            for (int tx = firstTileX; tx <= lastTileX; tx++)
            {
                if (!beforeTiles.TryGetValue((tx, ty), out var beforeTile))
                    continue;

                int pxMin = Math.Max(dirty.X, tx * ts);
                int pxMax = Math.Min(dirty.Right, tx * ts + ts);
                int pyMin = Math.Max(dirty.Y, ty * ts);
                int pyMax = Math.Min(dirty.Bottom, ty * ts + ts);
                if (pxMin >= pxMax || pyMin >= pyMax) continue;

                byte[]? liveTile = null;
                for (int py = pyMin; py < pyMax; py++)
                {
                    int ly = py - tilePixY;
                    int rowBase = ly * ts * 4;
                    for (int px = pxMin; px < pxMax; px++)
                    {
                        bool inSelection = !hasSelection || selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY);
                        int lx = px - tx * ts;
                        int offset = rowBase + lx * 4;
                        bool hadAlpha = !alphaLocked || (beforeTile != null && beforeTile[offset + 3] > 0);

                        if (inSelection && hadAlpha) continue;

                        if (beforeTile != null)
                        {
                            liveTile ??= layer.Pixels.GetOrCreateRawTile(tx, ty);
                            liveTile[offset] = beforeTile[offset];
                            liveTile[offset + 1] = beforeTile[offset + 1];
                            liveTile[offset + 2] = beforeTile[offset + 2];
                            liveTile[offset + 3] = beforeTile[offset + 3];
                        }
                        else
                        {
                            liveTile ??= layer.Pixels.GetOrCreateRawTile(tx, ty);
                            liveTile[offset] = 0;
                            liveTile[offset + 1] = 0;
                            liveTile[offset + 2] = 0;
                            liveTile[offset + 3] = 0;
                        }
                    }
                }
            }
        }
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
        public Dictionary<(int, int), byte[]?> BeforeTiles { get; } = new(capacity: 64);
        public List<CanvasInputSample> QueuedSamples { get; } = new(capacity: 1024);
        public int LastQueuedInputIndex { get; set; } = -1;
        public int NextSegmentIndex { get; set; } = 1;
        public bool StrokeStarted { get; set; }
        public bool FinalizeRequested { get; set; }
        public PixelRegion DirtyRegion { get; set; } = PixelRegion.Empty;
        public PixelRegion PendingPreviewDirty { get; set; } = PixelRegion.Empty;
        public long LastPreviewNotifyMs { get; set; }
        public double AverageSegmentMs { get; private set; }
        public int RemainingSegments => Math.Max(0, QueuedSamples.Count - NextSegmentIndex);

        // Cached per-transaction to avoid delegate allocations per segment batch.
        public BrushEngine.PixelSampler? PickupSampler { get; set; }
        public BrushEngine.TileReader? PickupTiles { get; set; }

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
