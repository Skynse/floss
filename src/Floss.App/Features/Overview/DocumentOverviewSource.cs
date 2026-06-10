using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Floss.App.Canvas;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Features.Overview;

/// <summary>
/// Navigator snapshots — resizes the shared <see cref="DocumentOverviewCache"/> for the panel.
/// </summary>
public sealed class DocumentOverviewSource : IDocumentOverviewSource, ICanvasBoundService, IDisposable
{
    private readonly DocumentOverviewCache _cache;
    private readonly object _gate = new();
    private int _panelMaxWidth = 1;
    private int _panelMaxHeight = 1;
    private int _dispatchGeneration;
    private bool _disposed;

    public DocumentOverviewSource(DocumentOverviewCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cache.Updated += OnCacheUpdated;
    }

    public void BindCanvas(DrawingCanvas canvas)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = DispatchSnapshotAsync(Interlocked.Increment(ref _dispatchGeneration), null);
        _cache.BindCanvas(canvas);
    }

    public event Action<DocumentOverviewSnapshot?>? SnapshotReady;

    public void RequestSnapshot(int maxWidth, int maxHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            _panelMaxWidth = Math.Max(1, maxWidth);
            _panelMaxHeight = Math.Max(1, maxHeight);
        }

        var canvas = _cache.Canvas;
        if (!canvas.HasDocument)
        {
            _ = DispatchSnapshotAsync(Interlocked.Increment(ref _dispatchGeneration), null);
            return;
        }

        if (_cache.TryGet(canvas.Document, out var bitmap, out _))
        {
            _ = DispatchResizedSnapshotAsync(bitmap, Interlocked.Increment(ref _dispatchGeneration));
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
        if (_disposed || !_cache.Canvas.HasDocument)
        {
            _ = DispatchSnapshotAsync(Interlocked.Increment(ref _dispatchGeneration), null);
            return;
        }

        if (_cache.TryGet(_cache.Canvas.Document, out var bitmap, out _))
            _ = DispatchResizedSnapshotAsync(bitmap, Interlocked.Increment(ref _dispatchGeneration));
        else
            _ = DispatchSnapshotAsync(Interlocked.Increment(ref _dispatchGeneration), null);
    }

    private async Task DispatchResizedSnapshotAsync(SKBitmap cache, int generation)
    {
        int maxW;
        int maxH;
        int docW;
        int docH;

        lock (_gate)
        {
            maxW = _panelMaxWidth;
            maxH = _panelMaxHeight;
        }

        docW = _cache.Canvas.Document.Width;
        docH = _cache.Canvas.Document.Height;

        DocumentOverviewSnapshot? snapshot = null;
        try
        {
            await Task.Run(() =>
            {
                snapshot = DocumentOverviewCompositor.CreateSnapshot(cache, docW, docH, maxW, maxH);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DocumentOverviewSource] snapshot resize failed: {ex}");
        }

        await DispatchSnapshotAsync(generation, snapshot).ConfigureAwait(false);
    }

    private async Task DispatchSnapshotAsync(int generation, DocumentOverviewSnapshot? snapshot)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || generation < _dispatchGeneration)
            {
                snapshot?.Dispose();
                return;
            }

            _dispatchGeneration = generation;
            SnapshotReady?.Invoke(snapshot);
        });
    }
}
