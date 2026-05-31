using System;
using System.Buffers;
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

// Paints strokes onto the active layer from a dedicated background rasterizer
// thread. The UI thread only ingests pointer samples and invalidates the
// viewport when the rasterizer signals that new tiles are ready.
public sealed class DirectDrawOutput : IOutputProcess
{
    private const long PreviewNotifyIntervalMs = 8;
    private const double RenderSliceBudgetMs = 5.0;
    private const int MaxSegmentsPerSlice = 256;
    private const int InitialSegmentsPerSlice = 4;

    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;
    private readonly BrushPreparationScheduler _preparationScheduler = new();
    private readonly Queue<StrokeTransaction> _pendingTransactions = new();

    public bool Antialiasing { get; set; } = true;

    private StrokeTransaction? _active;
    private StrokeTransaction? _accepting;

    // Background rasterizer thread state
    private readonly object _rasterizerStateLock = new();
    private readonly SemaphoreSlim _rasterizerSignal = new(0);
    private Task? _rasterizerTask;
    private CancellationTokenSource? _rasterizerCts;

    public DirectDrawOutput(BrushEngine brushEngine, DrawingDocument _)
    {
        _brushEngine = brushEngine;
    }

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup) return;
        if (!layer.IsMaskEditing && layer.IsLocked) return;
        if (layer.IsMaskEditing && layer.MaskPixels == null)
            layer.CreateMask();

        var samples = stroke.SmoothedSamples;
        var tx = _accepting is { FinalizeRequested: false }
            ? _accepting
            : BeginTransaction(ctx, layer, samples[0]);

        QueueNewSamples(tx, layer, ctx.Brush, samples);
        if (tx.NextSegmentIndex == 1 &&
            tx.QueuedSamples.Count == 1 &&
            tx.QueuedSamples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
        {
            lock (tx.QueueLock)
                tx.QueuedSamples.Add(tx.QueuedSamples[0]);
        }

        EnsureActiveTransaction();
        EnsureRasterizerRunning();
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0)
        {
            _accepting = null;
            return;
        }

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup)
        {
            _accepting = null;
            return;
        }
        if (!layer.IsMaskEditing && layer.IsLocked)
        {
            _accepting = null;
            return;
        }
        if (layer.IsMaskEditing && layer.MaskPixels == null)
            layer.CreateMask();

        Preview(ctx, input);
        if (_accepting != null)
        {
            lock (_accepting.QueueLock)
                _accepting.FinalizeRequested = true;
            _accepting = null;
        }

        EnsureActiveTransaction();
        EnsureRasterizerRunning();
    }

    private StrokeTransaction BeginTransaction(ToolContext ctx, DrawingLayer layer, CanvasInputSample firstSample)
    {
        _preparationScheduler.QueuePrepare(ctx.Brush, firstSample);
        var tx = new StrokeTransaction(ctx, layer, ctx.ActiveLayerIndex, ctx.Brush, ToLayerSample(layer, firstSample));
        lock (_rasterizerStateLock)
        {
            if (_active == null)
                _active = tx;
            else
                _pendingTransactions.Enqueue(tx);
        }
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

        layer.ActivePixels.LiveStroke = true;
        return tx;
    }

    private static void QueueNewSamples(StrokeTransaction tx, DrawingLayer layer, BrushPreset brush, IReadOnlyList<CanvasInputSample> samples)
    {
        lock (tx.QueueLock)
        {
            var start = Math.Max(0, tx.LastQueuedInputIndex + 1);
            for (var i = start; i < samples.Count; i++)
            {
                var sample = ToLayerSample(layer, samples[i]);
                AppendBoundedSample(tx, brush, sample);
                tx.LastQueuedInputIndex = i;
            }
        }
    }

    private static void AppendBoundedSample(StrokeTransaction tx, BrushPreset brush, CanvasInputSample sample)
    {
        if (tx.QueuedSamples.Count > 0)
        {
            var lastIndex = tx.QueuedSamples.Count - 1;
            if (lastIndex >= tx.NextSegmentIndex
                && ShouldCoalesceQueuedSample(tx.QueuedSamples[lastIndex], sample, brush))
            {
                tx.QueuedSamples[lastIndex] = sample;
                return;
            }
        }

        tx.QueuedSamples.Add(sample);
    }

    private static bool ShouldCoalesceQueuedSample(CanvasInputSample previous, CanvasInputSample next, BrushPreset brush)
    {
        if (previous.Phase != CanvasInputPhase.Move || next.Phase != CanvasInputPhase.Move)
            return false;

        if (Math.Abs(next.Pressure - previous.Pressure) > 0.03) return false;
        if (Math.Abs(next.TiltX - previous.TiltX) > 0.08) return false;
        if (Math.Abs(next.TiltY - previous.TiltY) > 0.08) return false;
        if (Math.Abs(next.Twist - previous.Twist) > 4.0) return false;

        var spacing = BrushSpacing.EstimateDistance(brush);
        var size = Math.Max(BrushSpacing.MinStampSizePx, (float)brush.Size);
        var threshold = Math.Clamp(Math.Min(spacing * 0.20f, size * 0.02f), 1f, 24f);
        return previous.DistanceTo(next) < threshold;
    }

    private void EnsureActiveTransaction()
    {
        if (_active != null)
            return;

        if (_pendingTransactions.Count > 0)
            _active = _pendingTransactions.Dequeue();
    }

    // ═══ Background rasterizer ═════════════════════════════════════════════════

    private void EnsureRasterizerRunning()
    {
        lock (_rasterizerStateLock)
        {
            if (_rasterizerTask != null && !_rasterizerTask.IsCompleted)
            {
                _rasterizerSignal.Release();
                return;
            }
            _rasterizerCts = new CancellationTokenSource();
            _rasterizerTask = Task.Run(() => RasterizerLoop(_rasterizerCts.Token));
        }
    }

    private void RasterizerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            StrokeTransaction? tx;
            lock (_rasterizerStateLock)
            {
                EnsureActiveTransaction();
                tx = _active;
            }

            if (tx == null)
                break;

            try
            {
                var processed = ProcessTransaction(tx, ct);
                if (!processed)
                    break;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "DirectDrawOutput.RasterizerLoop", flushToDisk: true);
                _brushEngine.EndStroke();
                RestoreTransaction(tx);

                lock (_rasterizerStateLock)
                {
                    _active = null;
                    _accepting = ReferenceEquals(_accepting, tx) ? null : _accepting;
                }
            }
        }
    }

    /// <summary>
    /// Processes all available segments for the given transaction.
    /// Returns true if the transaction was finalized or completed, false if
    /// there is no work yet and the rasterizer should sleep.
    /// </summary>
    private bool ProcessTransaction(StrokeTransaction tx, CancellationToken ct)
    {
        if (!tx.StrokeStarted)
        {
            _brushEngine.BeginStroke(tx.Brush, tx.FirstSample);
            tx.StrokeStarted = true;
        }

        var batchDirty = PixelRegion.Empty;
        var started = Stopwatch.GetTimestamp();
        var processed = 0;

        while (!ct.IsCancellationRequested)
        {
            int remaining;
            int nextSegmentIndex;
            lock (tx.QueueLock)
            {
                remaining = tx.QueuedSamples.Count - tx.NextSegmentIndex;
                nextSegmentIndex = tx.NextSegmentIndex;
            }

            if (remaining <= 0)
                break;

            var segmentCount = Math.Min(remaining,
                tx.SuggestedSegmentCount(RenderSliceBudgetMs, InitialSegmentsPerSlice, MaxSegmentsPerSlice));
            var isLastBatch = segmentCount >= remaining;
            var ensureLastEndpoint = isLastBatch && tx.FinalizeRequested;

            // Snapshot samples under the transaction lock
            CanvasInputSample[] snapshot;
            int snapshotCount;
            lock (tx.QueueLock)
            {
                var startIndex = tx.NextSegmentIndex;
                var count = segmentCount + 1;
                var available = Math.Max(0, tx.QueuedSamples.Count - Math.Max(0, startIndex - 1));
                var actual = Math.Min(count, available);
                snapshot = ArrayPool<CanvasInputSample>.Shared.Rent(actual);
                for (int i = 0; i < actual; i++)
                    snapshot[i] = tx.QueuedSamples[startIndex - 1 + i];
                snapshotCount = actual;
            }

            var segStarted = Stopwatch.GetTimestamp();
            var dirty = ProcessSegmentBatch(tx, snapshot, 1, segmentCount, ensureLastEndpoint);
            ArrayPool<CanvasInputSample>.Shared.Return(snapshot);

            lock (tx.QueueLock)
            {
                tx.NextSegmentIndex += segmentCount;
            }

            processed += segmentCount;
            tx.RecordSegmentTime(ElapsedMs(segStarted), segmentCount);

            if (!dirty.IsEmpty)
                batchDirty = batchDirty.Union(dirty);

            // Time-budget check removed — this is a background thread.
            // We still cap total segments per iteration to avoid holding
            // the BrushEngine._gate for unbounded time.
            if (processed >= MaxSegmentsPerSlice)
                break;
        }

        if (!batchDirty.IsEmpty)
        {
            lock (tx.QueueLock)
                tx.PendingPreviewDirty = tx.PendingPreviewDirty.Union(batchDirty);
            FlushPreviewDirty(tx, force: false);
        }

        bool shouldFinalize;
        lock (tx.QueueLock)
            shouldFinalize = tx.FinalizeRequested && tx.NextSegmentIndex >= tx.QueuedSamples.Count;

        if (!shouldFinalize)
        {
            // More samples may arrive later; signal will wake us.
            return false;
        }

        // ── Finalize transaction ────────────────────────────────────────────
        FlushPreviewDirty(tx, force: true);
        _brushEngine.EndStroke();
        Commit(tx);

        // Notify UI thread that this transaction is done
        var dirtyRegion = tx.DirtyRegion;
        var layerIndex = tx.LayerIndex;
        var ctx = tx.Ctx;
        Dispatcher.UIThread.Post(() =>
        {
            ctx.Document.NotifyStrokeSuspendEnd();
            if (!dirtyRegion.IsEmpty)
            {
                ctx.Document.NotifyChanged(dirtyRegion, layerIndex);
                ctx.InvalidateRender();
            }
        });

        lock (_rasterizerStateLock)
            _active = null;

        return true;
    }

    private void FlushPreviewDirty(StrokeTransaction tx, bool force)
    {
        PixelRegion pending;
        long lastNotify;
        lock (tx.QueueLock)
        {
            pending = tx.PendingPreviewDirty;
            lastNotify = tx.LastPreviewNotifyTimestamp;
        }

        if (pending.IsEmpty) return;

        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - lastNotify) * 1000.0 / Stopwatch.Frequency;
        if (!force && elapsedMs < PreviewNotifyIntervalMs)
            return;

        // Document notifications are thread-safe (they post to UI internally).
        tx.Ctx.Document.NotifyStrokeSuspendExtend(pending);
        tx.Ctx.Document.NotifyChanged(pending, tx.LayerIndex);

        // InvalidateRender MUST run on the UI thread.
        var ctx = tx.Ctx;
        Dispatcher.UIThread.Post(() => ctx.InvalidateRender());

        lock (tx.QueueLock)
        {
            tx.PendingPreviewDirty = PixelRegion.Empty;
            tx.LastPreviewNotifyTimestamp = now;
        }
    }

    private PixelRegion ProcessSegmentBatch(StrokeTransaction tx, CanvasInputSample[] samples, int startSegmentIndex, int segmentCount, bool ensureLastEndpoint = false)
    {
        using var telemetry = RenderTelemetry.ScopeNow();

        var savedAlphaLocked = tx.Layer.IsAlphaLocked;
        var isMaskEditing = tx.Layer.IsMaskEditing && tx.Layer.MaskPixels != null;
        if (isMaskEditing)
            tx.Layer.IsAlphaLocked = false;

        try
        {
            var region = EstimateSegmentBatchRegion(tx, samples, startSegmentIndex, segmentCount);
            if (!region.IsEmpty)
            {
                tx.Layer.ActivePixels.EnterPixelReadLock();
                try
                {
                    tx.Layer.ActivePixels.CaptureTiles(region, tx.BeforeTiles);
                }
                finally
                {
                    tx.Layer.ActivePixels.ExitPixelReadLock();
                }
            }

            var started = Stopwatch.GetTimestamp();
            var pickupSampler = tx.PickupSampler;
            var pickupTiles = tx.PickupTiles;

            _brushEngine.CanvasZoom = tx.Ctx.Viewport?.Zoom ?? 1.0;
            var dirty = _brushEngine.RasterizeSegments(tx.Layer, tx.Brush, samples, startSegmentIndex, segmentCount,
                pickupSampler, pickupTiles, ensureLastEndpoint);
            RenderTelemetry.RecordBrush(ElapsedMs(started), _brushEngine.LastStats.Path, _brushEngine.LastStats.StampCount, _brushEngine.LastStats.CachedDabCount);

            if (!dirty.IsEmpty)
            {
                tx.Layer.ActivePixels.EnterPixelWriteLock();
                try
                {
                    RestoreUnselectedPixels(tx.Layer, dirty, tx.Ctx.Selection, tx.BeforeTiles);
                }
                finally
                {
                    tx.Layer.ActivePixels.ExitPixelWriteLock();
                }

                var translatedDirty = dirty.Translate(tx.Layer.OffsetX, tx.Layer.OffsetY);
                tx.DirtyRegion = tx.DirtyRegion.Union(translatedDirty);
                return translatedDirty;
            }

            return PixelRegion.Empty;
        }
        finally
        {
            if (isMaskEditing)
                tx.Layer.IsAlphaLocked = savedAlphaLocked;
        }
    }

    private PixelRegion EstimateSegmentBatchRegion(StrokeTransaction tx, CanvasInputSample[] samples, int startSegmentIndex, int segmentCount)
    {
        var endSegmentIndex = Math.Min(samples.Length - 1, startSegmentIndex + segmentCount - 1);
        var region = PixelRegion.Empty;
        for (var i = startSegmentIndex; i <= endSegmentIndex; i++)
            region = region.Union(_brushEngine.EstimateSegmentRegion(tx.Layer, tx.Brush, samples[i - 1], samples[i]));
        return region;
    }

    private static (CanvasInputSample[] Snapshot, CanvasInputSample[] Rent) SnapshotSamples(List<CanvasInputSample> source, int startIndex, int count)
    {
        var available = Math.Max(0, source.Count - Math.Max(0, startIndex));
        var actual = Math.Min(count, available);
        var rent = ArrayPool<CanvasInputSample>.Shared.Rent(actual);
        for (var i = 0; i < actual; i++)
            rent[i] = source[startIndex + i];
        return (rent, rent);
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
            tx.Layer.ActivePixels.LiveStroke = false;
            tx.Ctx.Document.NotifyStrokeSuspendEnd();
        }
    }

    public void Cancel()
    {
        _rasterizerCts?.Cancel();
        WaitForProcessingToFinish();

        _brushEngine.EndStroke();

        if (_active != null)
            RestoreTransaction(_active);

        while (_pendingTransactions.Count > 0)
            RestoreTransaction(_pendingTransactions.Dequeue());

        _active = null;
        _accepting = null;
    }

    public bool HasPendingWork
    {
        get
        {
            lock (_rasterizerStateLock)
                return _rasterizerTask != null && !_rasterizerTask.IsCompleted;
        }
    }

    // Marks the active/accepting transaction as finalize-requested so the
    // background task will commit it normally, then detaches _accepting.
    // Call this when switching away from the tool while drawing — do NOT
    // call Cancel(), which would block the UI thread and revert the stroke.
    public void FinalizeAccepting()
    {
        if (_accepting != null)
        {
            lock (_accepting.QueueLock)
                _accepting.FinalizeRequested = true;
            _accepting = null;
        }
        if (_active != null)
        {
            lock (_active.QueueLock)
                _active.FinalizeRequested = true;
        }
        EnsureRasterizerRunning();
    }

    private void WaitForProcessingToFinish()
    {
        var deadline = Environment.TickCount64 + 2000;
        Task? task;
        lock (_rasterizerStateLock)
            task = _rasterizerTask;

        while (task != null && !task.IsCompleted && Environment.TickCount64 < deadline)
        {
            // Pump the dispatcher so any UI-thread callbacks posted by the
            // background rasterizer can execute.
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }

    public void WaitUntilIdle()
    {
        WaitForProcessingToFinish();
        FlushPending();
        WaitForProcessingToFinish();
    }

    public void FlushPending()
    {
        lock (_rasterizerStateLock)
        {
            if (_rasterizerTask != null && !_rasterizerTask.IsCompleted)
                return; // Background thread will finish everything
        }

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

            while (true)
            {
                int remaining;
                lock (tx.QueueLock)
                    remaining = tx.QueuedSamples.Count - tx.NextSegmentIndex;

                if (remaining <= 0)
                    break;

                var segmentCount = remaining;
                var isLastBatch = true;
                var ensureLastEndpoint = isLastBatch && tx.FinalizeRequested;

                CanvasInputSample[] snapshot;
                int snapshotCount;
                lock (tx.QueueLock)
                {
                    var startIndex = tx.NextSegmentIndex;
                    var count = segmentCount + 1;
                    var available = Math.Max(0, tx.QueuedSamples.Count - Math.Max(0, startIndex - 1));
                    var actual = Math.Min(count, available);
                    snapshot = ArrayPool<CanvasInputSample>.Shared.Rent(actual);
                    for (int i = 0; i < actual; i++)
                        snapshot[i] = tx.QueuedSamples[startIndex - 1 + i];
                    snapshotCount = actual;
                }

                batchDirty = batchDirty.Union(
                    ProcessSegmentBatch(tx, snapshot, 1, segmentCount, ensureLastEndpoint));
                ArrayPool<CanvasInputSample>.Shared.Return(snapshot);

                lock (tx.QueueLock)
                    tx.NextSegmentIndex += segmentCount;
                processed += segmentCount;
            }

            if (!batchDirty.IsEmpty)
            {
                lock (tx.QueueLock)
                    tx.PendingPreviewDirty = tx.PendingPreviewDirty.Union(batchDirty);
                FlushPreviewDirty(tx, force: tx.FinalizeRequested);
            }

            if (tx.FinalizeRequested)
            {
                _brushEngine.EndStroke();
                Commit(tx);
                _active = null;
                EnsureActiveTransaction();
                if (_active != null)
                    continue;
            }

            break;
        }
    }

    private static void RestoreTransaction(StrokeTransaction tx)
    {
        tx.Layer.ActivePixels.LiveStroke = false;
        try
        {
            if (tx.BeforeTiles.Count == 0)
                return;

            using var mutation = tx.Ctx.Document.RenderLock.Write();
            foreach (var ((tileX, tileY), tile) in tx.BeforeTiles)
                tx.Layer.ActivePixels.RestoreTile(tileX, tileY, tile);

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
            currentLayer.ActivePixels.GetPixel(x, y, out b, out g, out r, out a);
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

        return currentLayer.ActivePixels.GetTileOrNull(tileX, tileY);
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
                            liveTile ??= layer.ActivePixels.GetOrCreateRawTile(tx, ty);
                            liveTile[offset] = beforeTile[offset];
                            liveTile[offset + 1] = beforeTile[offset + 1];
                            liveTile[offset + 2] = beforeTile[offset + 2];
                            liveTile[offset + 3] = beforeTile[offset + 3];
                        }
                        else
                        {
                            liveTile ??= layer.ActivePixels.GetOrCreateRawTile(tx, ty);
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
        public readonly object QueueLock = new();
        public int LastQueuedInputIndex { get; set; } = -1;
        public int NextSegmentIndex { get; set; } = 1;
        public bool StrokeStarted { get; set; }
        public bool FinalizeRequested { get; set; }
        public PixelRegion DirtyRegion { get; set; } = PixelRegion.Empty;
        public PixelRegion PendingPreviewDirty { get; set; } = PixelRegion.Empty;
        public long LastPreviewNotifyTimestamp { get; set; }
        public double AverageSegmentMs { get; private set; }
        public int RemainingSegments
        {
            get
            {
                lock (QueueLock)
                    return Math.Max(0, QueuedSamples.Count - NextSegmentIndex);
            }
        }

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
