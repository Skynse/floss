using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Floss.App.Document;

namespace Floss.App.Canvas;

public sealed class ProjectionUpdateScheduler
{
    private readonly Action _requestRender;
    private readonly object _lock = new();
    private PixelRegion? _pendingDirty;
    private IReadOnlyList<DrawingLayer>? _pendingLayers;
    private int? _pendingLayerIndex;
    private bool _fullDirty;
    private bool _metadataOnly;
    private bool _queued;

    public ProjectionUpdateScheduler(Action requestRender)
    {
        _requestRender = requestRender;
    }

    public int PendingCount { get; private set; }

    public void Invalidate(PixelRegion? region, IReadOnlyList<DrawingLayer>? layers = null, int? layerIndex = null, bool metadataOnly = false)
    {
        lock (_lock)
        {
            PendingCount++;
            _pendingLayers = layers;
            _pendingLayerIndex = layerIndex;
            if (metadataOnly)
                _metadataOnly = true;
            if (region is null || region.Value.IsEmpty)
            {
                _fullDirty = true;
                _pendingDirty = null;
            }
            else if (!_fullDirty)
            {
                _pendingDirty = _pendingDirty is { } existing ? existing.Union(region.Value) : region.Value;
            }

            if (_queued) return;
            _queued = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_lock) _queued = false;
            _requestRender();
        }, DispatcherPriority.Render);
    }

    public void ApplyPending(LayerCompositor compositor)
    {
        // If a background composite is mid-pass we DON'T want to block the UI
        // thread on its gate. Leave our pending state intact and try again on
        // the next render tick (which the composite's completion will trigger).
        if (compositor.IsCompositeActive)
            return;

        PixelRegion? dirty;
        IReadOnlyList<DrawingLayer>? layers;
        int? layerIndex;
        bool fullDirty;
        bool metadataOnly;

        lock (_lock)
        {
            fullDirty = _fullDirty;
            dirty = _pendingDirty;
            layers = _pendingLayers;
            layerIndex = _pendingLayerIndex;
            metadataOnly = _metadataOnly;
            _fullDirty = false;
            _pendingDirty = null;
            _pendingLayers = null;
            _pendingLayerIndex = null;
            _metadataOnly = false;
            PendingCount = 0;
        }

        if (fullDirty)
            compositor.Invalidate(null, layers, layerIndex, metadataOnly);
        else if (dirty is { } region && !region.IsEmpty)
            compositor.Invalidate(region, layers, layerIndex, metadataOnly);
    }
}
