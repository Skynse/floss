using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Features.Overview.Histogram;
using SkiaSharp;

namespace Floss.App.Features.Overview;

/// <summary>
/// Shared stroke-aware overview composite for navigator, histogram, and future panels.
/// </summary>
public sealed class DocumentOverviewCache : ICanvasBoundService, IDisposable
{
    private const int RebuildDebounceMs = 350;
    private const int MaxBuildRetries = 3;

    private DrawingCanvas _canvas;
    private DrawingDocument? _wiredDocument;
    private readonly object _gate = new();
    private CancellationTokenSource? _rebuildCts;
    private int _dispatchGeneration;
    private SKBitmap? _bitmap;
    private DocumentHistogram? _histogram;
    private long _cacheRevision = -1;
    private int _cacheDocW;
    private int _cacheDocH;
    private bool _cacheCheckerboard;
    private uint _cachePaper;
    private bool _dirty;
    private bool _disposed;
    private int _buildRetries;

    public DocumentOverviewCache(DrawingCanvas canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        WireDocument(_canvas.Document);
    }

    /// <summary>Fired on the UI thread after rebuild or clear. Borrow cache via <see cref="TryGet"/>.</summary>
    public event Action? Updated;

    public DrawingCanvas Canvas => _canvas;

    public void BindCanvas(DrawingCanvas canvas)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        if (ReferenceEquals(_canvas, canvas))
            return;

        CancelPending();
        UnwireDocument();
        ClearCache();
        _canvas = canvas;
        WireDocument(_canvas.Document);
        _dirty = true;
        _buildRetries = 0;

        NotifyUpdated();

        if (!_canvas.HasDocument)
            return;

        ScheduleRebuild(immediate: true);
    }

    public void RequestRebuild(bool immediate = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_canvas.HasDocument)
        {
            CancelPending();
            ClearCache();
            NotifyUpdated();
            return;
        }

        if (!_dirty && Matches(_canvas.Document))
            return;

        _dirty = true;
        if (_canvas.IsStrokeInProgress)
            return;

        ScheduleRebuild(immediate: immediate || _bitmap == null);
    }

    public void CancelPending() => ClearRebuildCts();

    public bool TryGet(DrawingDocument document, out SKBitmap bitmap, out DocumentHistogram? histogram)
    {
        lock (_gate)
        {
            if (_bitmap != null && Matches(document))
            {
                bitmap = _bitmap;
                histogram = _histogram;
                return true;
            }
        }

        bitmap = null!;
        histogram = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ClearRebuildCts();
        UnwireDocument();
        ClearCache();
    }

    private void ClearRebuildCts()
    {
        var cts = Interlocked.Exchange(ref _rebuildCts, null);
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private void WireDocument(DrawingDocument doc)
    {
        if (ReferenceEquals(_wiredDocument, doc))
            return;

        UnwireDocument();
        _wiredDocument = doc;
        doc.StrokeSuspendEnded += OnStrokeEnded;
        doc.LayersChanged += OnStructureChanged;
        doc.Changed += OnDocumentChanged;
    }

    private void UnwireDocument()
    {
        if (_wiredDocument == null)
            return;

        _wiredDocument.StrokeSuspendEnded -= OnStrokeEnded;
        _wiredDocument.LayersChanged -= OnStructureChanged;
        _wiredDocument.Changed -= OnDocumentChanged;
        _wiredDocument = null;
    }

    private void OnStrokeEnded(object? sender, EventArgs e)
    {
        if (!_dirty && Matches(_canvas.Document))
            return;

        ScheduleRebuild(immediate: true);
    }

    private void OnStructureChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        if (_canvas.IsStrokeInProgress)
            return;

        ScheduleRebuild(immediate: false);
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (_canvas.IsStrokeInProgress)
        {
            _dirty = true;
            return;
        }

        _dirty = true;
        ScheduleRebuild(immediate: false);
    }

    private void ScheduleRebuild(bool immediate)
    {
        if (_disposed || _canvas.IsStrokeInProgress)
            return;

        ClearRebuildCts();

        if (immediate)
        {
            _ = RunRebuildAsync(Interlocked.Increment(ref _dispatchGeneration));
            return;
        }

        var cts = new CancellationTokenSource();
        _rebuildCts = cts;
        _ = RunDebouncedRebuildAsync(cts);
    }

    private async Task RunDebouncedRebuildAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(RebuildDebounceMs, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || !ReferenceEquals(_rebuildCts, cts) || _canvas.IsStrokeInProgress)
            return;

        await RunRebuildAsync(Interlocked.Increment(ref _dispatchGeneration)).ConfigureAwait(false);
    }

    private async Task RunRebuildAsync(int generation)
    {
        if (!_canvas.HasDocument)
        {
            ClearCache();
            await NotifyUpdatedAsync(generation).ConfigureAwait(false);
            return;
        }

        var document = _canvas.Document;
        SKBitmap? built = null;
        DocumentHistogram? histogram = null;

        try
        {
            await Task.Run(() =>
            {
                using (document.RenderLock.Read())
                {
                    built = DocumentOverviewCompositor.BuildCache(document);
                    if (built != null)
                        histogram = DocumentHistogramComputer.Compute(built, document.Width, document.Height);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DocumentOverviewCache] build failed: {ex}");
            built?.Dispose();
            built = null;
            histogram = null;
        }

        if (_disposed || generation < Volatile.Read(ref _dispatchGeneration))
        {
            built?.Dispose();
            return;
        }

        if (built == null)
        {
            ClearCache();
            await NotifyUpdatedAsync(generation).ConfigureAwait(false);
            if (_disposed || generation < Volatile.Read(ref _dispatchGeneration))
                return;

            if (_buildRetries < MaxBuildRetries)
            {
                _buildRetries++;
                try
                {
                    await Task.Delay(250 * _buildRetries).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!_disposed && generation <= Volatile.Read(ref _dispatchGeneration))
                    ScheduleRebuild(immediate: true);
            }

            return;
        }

        _buildRetries = 0;
        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = built;
            _histogram = histogram;
            _cacheRevision = document.VisualRevision;
            _cacheDocW = document.Width;
            _cacheDocH = document.Height;
            _cacheCheckerboard = !document.IsPaperBackgroundVisible || document.PaperColor.A < 255;
            _cachePaper = _cacheCheckerboard
                ? 0u
                : (uint)(document.PaperColor.B
                         | (document.PaperColor.G << 8)
                         | (document.PaperColor.R << 16)
                         | (document.PaperColor.A << 24));
            _dirty = false;
        }

        await NotifyUpdatedAsync(generation).ConfigureAwait(false);
    }

    private async Task NotifyUpdatedAsync(int generation)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || generation < _dispatchGeneration)
                return;

            _dispatchGeneration = generation;
            Updated?.Invoke();
        });
    }

    private void NotifyUpdated()
    {
        if (_disposed)
            return;

        _ = Dispatcher.UIThread.InvokeAsync(() => Updated?.Invoke());
    }

    private bool Matches(DrawingDocument document)
    {
        var checkerboard = !document.IsPaperBackgroundVisible || document.PaperColor.A < 255;
        var paper = checkerboard
            ? 0u
            : (uint)(document.PaperColor.B
                     | (document.PaperColor.G << 8)
                     | (document.PaperColor.R << 16)
                     | (document.PaperColor.A << 24));

        lock (_gate)
        {
            return _bitmap != null
                   && !_dirty
                   && _cacheRevision == document.VisualRevision
                   && _cacheDocW == document.Width
                   && _cacheDocH == document.Height
                   && _cacheCheckerboard == checkerboard
                   && _cachePaper == paper;
        }
    }

    private void ClearCache()
    {
        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _histogram = null;
            _dirty = false;
        }
    }
}
