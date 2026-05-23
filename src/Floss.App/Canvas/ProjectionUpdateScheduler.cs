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
    private PixelRegion? _viewportClip;

    public ProjectionUpdateScheduler(Action requestRender)
    {
        _requestRender = requestRender;
    }

    public int PendingCount { get; private set; }

    /// <summary>True when the last applied invalidation was a layer-property preview (opacity scrub, etc.).</summary>
    public bool LastApplyWasMetadataOnly { get; private set; }

    public void Invalidate(
        PixelRegion? region,
        IReadOnlyList<DrawingLayer>? layers = null,
        int? layerIndex = null,
        bool metadataOnly = false,
        PixelRegion? viewportClip = null)
    {
        lock (_lock)
        {
            PendingCount++;
            _pendingLayers = layers;
            _pendingLayerIndex = layerIndex;
            if (metadataOnly)
                _metadataOnly = true;
            if (viewportClip is { IsEmpty: false })
            {
                _viewportClip = _viewportClip is { } existing
                    ? existing.Union(viewportClip.Value)
                    : viewportClip.Value;
            }
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

        var priority = metadataOnly ? DispatcherPriority.Input : DispatcherPriority.Render;
        Dispatcher.UIThread.Post(() =>
        {
            lock (_lock) _queued = false;
            _requestRender();
        }, priority);
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

        PixelRegion? viewportClip;
        lock (_lock)
        {
            fullDirty = _fullDirty;
            dirty = _pendingDirty;
            layers = _pendingLayers;
            layerIndex = _pendingLayerIndex;
            metadataOnly = _metadataOnly;
            viewportClip = _viewportClip;
            _fullDirty = false;
            _pendingDirty = null;
            _pendingLayers = null;
            _pendingLayerIndex = null;
            _metadataOnly = false;
            _viewportClip = null;
            PendingCount = 0;
        }

        LastApplyWasMetadataOnly = metadataOnly;

        if (fullDirty)
            compositor.Invalidate(null, layers, layerIndex, metadataOnly, viewportClip);
        else if (dirty is { } region && !region.IsEmpty)
            compositor.Invalidate(region, layers, layerIndex, metadataOnly, viewportClip);
    }
}
