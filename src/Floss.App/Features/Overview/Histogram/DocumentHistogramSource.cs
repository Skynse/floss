using System;
using System.Threading;
using Avalonia.Threading;
using Floss.App.Canvas;

namespace Floss.App.Features.Overview.Histogram;

/// <summary>
/// RGB histogram from the shared <see cref="DocumentOverviewCache"/> composite.
/// </summary>
public sealed class DocumentHistogramSource : IDocumentHistogramSource, ICanvasBoundService, IDisposable
{
    private readonly DocumentOverviewCache _cache;
    private int _dispatchGeneration;
    private bool _disposed;

    public DocumentHistogramSource(DocumentOverviewCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cache.Updated += OnCacheUpdated;
    }

    public event Action<DocumentHistogram?>? HistogramReady;

    public void BindCanvas(DrawingCanvas canvas)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DispatchHistogram(Interlocked.Increment(ref _dispatchGeneration), null);
        _cache.BindCanvas(canvas);
    }

    public void RequestUpdate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var canvas = _cache.Canvas;
        if (!canvas.HasDocument)
        {
            DispatchHistogram(Interlocked.Increment(ref _dispatchGeneration), null);
            return;
        }

        if (_cache.TryGet(canvas.Document, out _, out var histogram))
        {
            DispatchHistogram(Interlocked.Increment(ref _dispatchGeneration), histogram);
            return;
        }

        _cache.RequestRebuild(immediate: true);
    }

    public void CancelPending() { }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cache.Updated -= OnCacheUpdated;
    }

    private void OnCacheUpdated()
    {
        if (_disposed)
            return;

        var canvas = _cache.Canvas;
        if (!canvas.HasDocument || !_cache.TryGet(canvas.Document, out _, out var histogram))
        {
            DispatchHistogram(Interlocked.Increment(ref _dispatchGeneration), null);
            return;
        }

        DispatchHistogram(Interlocked.Increment(ref _dispatchGeneration), histogram);
    }

    private void DispatchHistogram(int generation, DocumentHistogram? histogram)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation < _dispatchGeneration)
                return;

            _dispatchGeneration = generation;
            HistogramReady?.Invoke(histogram);
        });
    }
}
