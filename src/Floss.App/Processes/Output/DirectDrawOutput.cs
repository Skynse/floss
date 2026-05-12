using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Paints a stroke onto the active layer using the brush engine.
// Rasterization runs on a background thread so the UI stays responsive during strokes.
public sealed class DirectDrawOutput : IOutputProcess
{
    public bool IsPaintOutput => true;
    private readonly BrushEngine _brushEngine;

    public bool Antialiasing { get; set; } = true;

    // Incremental stroke state
    private int _lastProcessedIndex = -1;
    private Dictionary<(int, int), byte[]?>? _beforeTiles;
    private PixelRegion _dirtyRegion;
    private DrawingLayer? _currentLayer;
    private bool _strokeActive;

    // Background worker
    private readonly object _queueLock = new();
    private readonly List<SegmentWork> _segmentQueue = [];
    private Task? _workerTask;
    private CancellationTokenSource? _workerCts;
    private ToolContext? _currentCtx;

    private sealed class SegmentWork
    {
        public required DrawingLayer Layer { get; init; }
        public required BrushPreset Brush { get; init; }
        public required SelectionMask Selection { get; init; }
        public required CanvasInputSample From { get; init; }
        public required CanvasInputSample To { get; init; }
    }

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
        var brush = ctx.Brush;
        var selection = ctx.Selection;

        // Initialize stroke on first preview call
        if (!_strokeActive)
        {
            _strokeActive = true;
            _lastProcessedIndex = -1;
            _beforeTiles = new Dictionary<(int, int), byte[]?>();
            _dirtyRegion = PixelRegion.Empty;
            _currentLayer = layer;
            _currentCtx = ctx;
            _brushEngine.BeginStroke(brush, ToLayerSample(layer, samples[0]));

            // Initial dab for mouse/touch — queue it for background processing
            if (samples[0].Source is CanvasInputSource.Mouse or CanvasInputSource.Unknown)
            {
                QueueSegment(layer, brush, selection, ToLayerSample(layer, samples[0]), ToLayerSample(layer, samples[0]));
                _lastProcessedIndex = 0;
            }
        }

        // Queue any new segments since last preview
        var startIdx = Math.Max(1, _lastProcessedIndex + 1);
        for (int i = startIdx; i < samples.Count; i++)
        {
            var from = ToLayerSample(layer, samples[i - 1]);
            var to = ToLayerSample(layer, samples[i]);
            QueueSegment(layer, brush, selection, from, to);
            _lastProcessedIndex = i;
        }

        EnsureWorkerRunning();
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0)
        {
            Cleanup();
            return;
        }

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked)
        {
            Cleanup();
            return;
        }

        // Queue remaining segments, then end the brush stroke
        Preview(ctx, input);
        _brushEngine.EndStroke();

        // The worker captures before-tiles as it processes segments.
        // If it's still running, defer the undo commit — we read _beforeTiles
        // after it drains the queue, without blocking the UI thread.
        var pendingWorker = _workerTask;

        if (pendingWorker != null && !pendingWorker.IsCompleted)
        {
            DeferCommit(pendingWorker);
        }
        else
        {
            // Edge case: old worker finished between Preview's queue and here.
            // Preview may have started a new worker — check and defer if so.
            var afterPreview = _workerTask;
            if (afterPreview != null && !afterPreview.IsCompleted)
                DeferCommit(afterPreview);
            else
            {
                CommitFromWorkerState();
                Cleanup();
            }
        }
    }

    private void DeferCommit(Task worker)
    {
        worker.ContinueWith(_ => Dispatcher.UIThread.Post(CommitFromWorkerState), TaskScheduler.Default);
        _strokeActive = false;
        _lastProcessedIndex = -1;
        _workerCts = null;
        _workerTask = null;
        lock (_queueLock) { _segmentQueue.Clear(); }
    }

    private void CommitFromWorkerState()
    {
        if (_beforeTiles != null && _beforeTiles.Count > 0 && _currentLayer != null && _currentCtx != null)
        {
            var tileDirty = ComputeTileDirtyRegion(_beforeTiles).Translate(_currentLayer.OffsetX, _currentLayer.OffsetY);
            if (!tileDirty.IsEmpty)
            {
                _currentLayer.MarkThumbnailDirty();
                _currentCtx.Document.CommitLayerTileMutation(_currentCtx.ActiveLayerIndex, _beforeTiles, tileDirty);
                _currentCtx.Document.NotifyChanged(tileDirty, _currentCtx.ActiveLayerIndex);
            }
        }

        if (_currentCtx != null)
            _currentCtx.Document.CommitStroke();

        Cleanup();
    }

    private void QueueSegment(DrawingLayer layer, BrushPreset brush, SelectionMask selection, CanvasInputSample from, CanvasInputSample to)
    {
        lock (_queueLock)
        {
            _segmentQueue.Add(new SegmentWork
            {
                Layer = layer,
                Brush = brush,
                Selection = selection,
                From = from,
                To = to
            });
        }
    }

    private void EnsureWorkerRunning()
    {
        if (_workerTask != null && !_workerTask.IsCompleted) return;

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_workerCts.Token));
    }

    private void WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SegmentWork? work;
            lock (_queueLock)
            {
                if (_segmentQueue.Count == 0) break;
                work = _segmentQueue[0];
                _segmentQueue.RemoveAt(0);
            }

            if (work == null) continue;

            // Capture shared state locally to avoid races with Cancel()/Cleanup()
            var beforeTiles = _beforeTiles;
            var ctx = _currentCtx;

            // Capture before-tiles for this segment's region
            var region = _brushEngine.EstimateSegmentRegion(work.Layer, work.Brush, work.From, work.To);
            if (!region.IsEmpty && beforeTiles != null)
            {
                work.Layer.CaptureTiles(region, beforeTiles);
            }

            // Rasterize on background thread
            var dirty = _brushEngine.RasterizeSegment(work.Layer, work.Brush, work.From, work.To,
                (x, y, out b, out g, out r, out a) => ReadBeforeStrokePixelFrom(beforeTiles, work.Layer, x, y, out b, out g, out r, out a));

            if (!dirty.IsEmpty)
            {
                if (beforeTiles != null)
                    RestoreUnselectedPixels(work.Layer, dirty, work.Selection, beforeTiles);

                // Accumulate dirty region (thread-safe via lock)
                var translatedDirty = dirty.Translate(work.Layer.OffsetX, work.Layer.OffsetY);
                lock (_queueLock)
                {
                    _dirtyRegion = _dirtyRegion.Union(translatedDirty);
                }

                // Post invalidate to UI thread
                if (ctx != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        work.Layer.MarkThumbnailDirty();
                        ctx.Document.NotifyChanged(translatedDirty, ctx.ActiveLayerIndex);
                        ctx.InvalidateRender();
                    });
                }
            }
        }
    }

    private static void ReadBeforeStrokePixelFrom(Dictionary<(int, int), byte[]?>? beforeTiles, DrawingLayer? currentLayer, int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        if (beforeTiles == null)
        {
            if (currentLayer != null)
                currentLayer.Pixels.GetPixel(x, y, out b, out g, out r, out a);
            else
                b = g = r = a = 0;
            return;
        }

        const int ts = TiledPixelBuffer.TileSize;
        var tx = FloorDiv(x, ts);
        var ty = FloorDiv(y, ts);
        if (!beforeTiles.TryGetValue((tx, ty), out var tile) || tile == null)
        {
            b = g = r = a = 0;
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

    private void Cleanup()
    {
        _workerCts?.Cancel();
        _workerCts = null;
        _workerTask = null;
        _strokeActive = false;
        _lastProcessedIndex = -1;
        _beforeTiles = null;
        _dirtyRegion = PixelRegion.Empty;
        _currentLayer = null;
        _currentCtx = null;
        lock (_queueLock)
        {
            _segmentQueue.Clear();
        }
    }

    public void Cancel()
    {
        _workerCts?.Cancel();
        _brushEngine.EndStroke();
        Cleanup();
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

    private void RestoreUnselectedPixels(DrawingLayer layer, PixelRegion dirty, SelectionMask selection, Dictionary<(int, int), byte[]?> beforeTiles)
    {
        if (beforeTiles == null) return;
        if (dirty.IsEmpty) return;

        bool hasSelection = selection.HasSelection;
        bool alphaLocked = layer.IsAlphaLocked;
        if (!hasSelection && !alphaLocked) return;

        const int ts = TiledPixelBuffer.TileSize;
        int firstTileX = dirty.X / ts;
        int firstTileY = dirty.Y / ts;
        int lastTileX = (dirty.Right - 1) / ts;
        int lastTileY = (dirty.Bottom - 1) / ts;

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
}