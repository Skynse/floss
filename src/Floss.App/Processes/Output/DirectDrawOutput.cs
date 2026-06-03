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

// Paints strokes onto the active layer. Active-stroke segments are rendered
// synchronously on the UI thread (pointer event → tiles → Invalidate) so there
// is no dispatcher or thread-pool hop.  Background work is only used when
// finalizing a stroke while a new one has already started.
public sealed class DirectDrawOutput : IOutputProcess
{
    private const long PreviewNotifyIntervalMs = 1;
    private const double RenderSliceBudgetMs = 5.0;
    private const int MaxSegmentsPerSlice = 16;
    private const int InitialSegmentsPerSlice = 4;

    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;
    private readonly Queue<StrokeTransaction> _pendingTransactions = new();

    public bool Antialiasing { get; set; } = true;

    private StrokeTransaction? _active;
    private StrokeTransaction? _accepting;
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

        QueueNewSamples(tx, layer, samples);
        if (tx.NextSegmentIndex == 1 &&
            tx.QueuedSamples.Count == 1 &&
            tx.QueuedSamples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
        {
            tx.QueuedSamples.Add(tx.QueuedSamples[0]);
        }

        EnsureActiveTransaction();

        // Render synchronously right here in the pointer event.
        // No dispatcher post, no Task.Run — pixels go to tiles immediately.
        ProcessQueuedSync();
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
        ProcessQueuedSync();
    }

    private StrokeTransaction BeginTransaction(ToolContext ctx, DrawingLayer layer, CanvasInputSample firstSample)
    {
        var tx = new StrokeTransaction(ctx, layer, ctx.ActiveLayerIndex, ctx.Brush, ToLayerSample(layer, firstSample));
        if (_active == null)
            _active = tx;
        else
            _pendingTransactions.Enqueue(tx);
        _accepting = tx;

        if (ctx.Brush.ColorMix && ctx.Brush.SmudgeMode == SmudgeMode.Blend)
        {
            tx.PickupSampler = (x, y, out b, out g, out r, out a) =>
                ReadBeforeStrokePixelFrom(tx.BeforeTiles, tx.Layer, x, y, out b, out g, out r, out a);
            tx.PickupTiles = (tileX, tileY) => ReadBeforeStrokeTileFrom(tx.BeforeTiles, tx.Layer, tileX, tileY);
        }

        var radius = Math.Max(64, (int)Math.Ceiling(ctx.Brush.Size + 32));
        var docX = (int)Math.Round(firstSample.X) + layer.OffsetX;
        var docY = (int)Math.Round(firstSample.Y) + layer.OffsetY;
        var initialRegion = new PixelRegion(docX - radius, docY - radius, radius * 2, radius * 2)
            .ClipTo(ctx.Document.Width, ctx.Document.Height);
        if (!initialRegion.IsEmpty)
            ctx.Document.NotifyStrokeSuspendBegin(initialRegion, ctx.ActiveLayerIndex);

        layer.Pixels.LiveStroke = true;
        return tx;
    }

    private static void QueueNewSamples(StrokeTransaction tx, DrawingLayer layer, IReadOnlyList<CanvasInputSample> samples)
    {
        var start = Math.Max(0, tx.LastQueuedInputIndex + 1);
        for (var i = start; i < samples.Count; i++)
        {
            tx.QueuedSamples.Add(ToLayerSample(layer, samples[i]));
            tx.LastQueuedInputIndex = i;
        }
    }

    private void EnsureActiveTransaction()
    {
        if (_active != null)
            return;

        if (_pendingTransactions.Count > 0)
            _active = _pendingTransactions.Dequeue();
    }

    // Render synchronously on the caller thread (pointer event handler).
    // No dispatcher post, no Task.Run.  Heavy batches still get sliced so
    // the UI thread doesn't stall for more than ~5 ms.
    private void ProcessQueuedSync()
    {
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
        var scheduleBackground = false;

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
                var segmentCount = Math.Min(remaining, tx.SuggestedSegmentCount(RenderSliceBudgetMs, InitialSegmentsPerSlice, MaxSegmentsPerSlice));

                var startIndex = tx.NextSegmentIndex;
                var snapshot = SnapshotSamples(tx.QueuedSamples, startIndex - 1, segmentCount + 1);
                var snapshotStart = 1;

                var dirty = ProcessSegmentBatch(tx, snapshot, snapshotStart, segmentCount);

                tx.NextSegmentIndex += segmentCount;
                processed += segmentCount;

                if (!dirty.IsEmpty)
                    batchDirty = batchDirty.Union(dirty);

                if (processed >= MaxSegmentsPerSlice)
                    break;
                if (ElapsedMs(started) >= RenderSliceBudgetMs)
                    break;
            }

            if (!batchDirty.IsEmpty)
            {
                tx.PendingPreviewDirty = tx.PendingPreviewDirty.Union(batchDirty);
                FlushPreviewDirty(tx, force: tx.FinalizeRequested);
            }

            if (tx.NextSegmentIndex < tx.QueuedSamples.Count)
            {
                // UI-thread budget exceeded; offload remainder to background.
                scheduleBackground = true;
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
                    scheduleBackground = true;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "DirectDrawOutput.ProcessQueuedSync", flushToDisk: true);
            _brushEngine.EndStroke();
            RestoreTransaction(tx);
            _active = null;
            _accepting = ReferenceEquals(_accepting, tx) ? null : _accepting;
            EnsureActiveTransaction();
            if (_active != null)
                scheduleBackground = true;
        }
        finally
        {
            _processing = false;
            if (scheduleBackground)
                Dispatcher.UIThread.Post(() => ProcessQueuedAsync(), DispatcherPriority.Background);
        }
    }

    // Fallback background path for when the UI-thread budget is exceeded
    // or when we need to finalize a transaction while not in a pointer event.
    private async void ProcessQueuedAsync()
    {
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
                var segmentCount = Math.Min(remaining, tx.SuggestedSegmentCount(RenderSliceBudgetMs, InitialSegmentsPerSlice, MaxSegmentsPerSlice));

                var startIndex = tx.NextSegmentIndex;
                var snapshot = SnapshotSamples(tx.QueuedSamples, startIndex - 1, segmentCount + 1);
                var snapshotStart = 1;

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

                if (processed >= MaxSegmentsPerSlice)
                    break;
                if (ElapsedMs(started) >= RenderSliceBudgetMs)
                    break;
            }

            if (!batchDirty.IsEmpty)
            {
                tx.PendingPreviewDirty = tx.PendingPreviewDirty.Union(batchDirty);
                FlushPreviewDirty(tx, force: tx.FinalizeRequested);
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
            CrashLog.Write(ex, "DirectDrawOutput.ProcessQueuedAsync", flushToDisk: true);
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
                Dispatcher.UIThread.Post(() => ProcessQueuedAsync(), DispatcherPriority.Background);
        }
    }

    private void FlushPreviewDirty(StrokeTransaction tx, bool force)
    {
        if (tx.PendingPreviewDirty.IsEmpty) return;

        var now = Environment.TickCount64;
        if (!force && now - tx.LastPreviewNotifyMs < PreviewNotifyIntervalMs)
            return;

        tx.Ctx.Document.NotifyStrokeSuspendExtend(tx.PendingPreviewDirty);
        tx.Ctx.Document.NotifyChanged(tx.PendingPreviewDirty, tx.LayerIndex);
        tx.Ctx.InvalidateRender();
        tx.PendingPreviewDirty = PixelRegion.Empty;
        tx.LastPreviewNotifyMs = now;
    }

    private PixelRegion ProcessSegmentBatch(StrokeTransaction tx, CanvasInputSample[] samples, int startSegmentIndex, int segmentCount)
    {
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
            tx.Layer.Pixels.LiveStroke = false;
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
    }

    public bool HasPendingWork =>
        _processing || _active != null || _pendingTransactions.Count > 0;

    public void FinalizeAccepting()
    {
        if (_accepting != null)
        {
            _accepting.FinalizeRequested = true;
            _accepting = null;
        }
        if (_active != null && !_active.FinalizeRequested)
            _active.FinalizeRequested = true;
        ProcessQueuedSync();
    }

    private void WaitForProcessingToFinish()
    {
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

    private static void RestoreTransaction(StrokeTransaction tx)
    {
        tx.Layer.Pixels.LiveStroke = false;
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
