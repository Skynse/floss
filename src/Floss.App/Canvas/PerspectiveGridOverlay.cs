using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Floss.App.Tools.Assistants;

namespace Floss.App.Canvas;

/// <summary>
/// Draws perspective/fisheye grids across the full workspace viewport (including
/// margins outside the canvas), matching CSP-style "infinite" guide lines.
/// </summary>
internal sealed class PerspectiveGridOverlay : Control
{
    private DrawingCanvas? _canvas;
    private DrawingDocument? _subscribedDocument;

    internal DrawingCanvas? Canvas
    {
        get => _canvas;
        set
        {
            if (ReferenceEquals(_canvas, value)) return;
            Unsubscribe();
            _canvas = value;
            Subscribe();
            InvalidateVisual();
        }
    }

    public PerspectiveGridOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void Subscribe()
    {
        if (_canvas == null) return;
        _subscribedDocument = _canvas.Document;
        _subscribedDocument.Changed += OnDocumentChanged;
        _canvas.AssistantsOverlayInvalidated += OnOverlayInvalidated;
        _canvas.AssistantsChanged += OnAssistantsChanged;
    }

    private void Unsubscribe()
    {
        if (_canvas != null)
        {
            _canvas.AssistantsOverlayInvalidated -= OnOverlayInvalidated;
            _canvas.AssistantsChanged -= OnAssistantsChanged;
        }

        if (_subscribedDocument != null)
        {
            _subscribedDocument.Changed -= OnDocumentChanged;
            _subscribedDocument = null;
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) => InvalidateVisual();
    private void OnOverlayInvalidated() => InvalidateVisual();
    private void OnAssistantsChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_canvas == null || !_canvas.IsVisible)
            return;

        if (!ReferenceEquals(_subscribedDocument, _canvas.Document))
        {
            Unsubscribe();
            Subscribe();
        }

        var visible = _canvas.ComputeUnclampedVisibleDocumentRect();
        if (visible is not { } clip || clip.Width < 1 || clip.Height < 1)
            return;

        var zoom = Math.Max(_canvas.CanvasZoom, 0.001);
        PaintingAssistant? preview = _canvas.AssistantCreatePreview;

        Point? DocToViewport(Point doc)
        {
            if (_canvas.TryDocumentPointToVisual(this, doc, out var vp))
                return vp;
            return null;
        }

        AssistantsRenderer.RenderPerspectiveGridsInViewport(
            context,
            _canvas.Document.Assistants,
            clip,
            zoom,
            DocToViewport,
            preview);
    }
}
