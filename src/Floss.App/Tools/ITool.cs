using System;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

// Central interface every tool implements.
public interface ITool
{
    void PointerDown(ToolContext ctx, CanvasInputSample s);
    void PointerMove(ToolContext ctx, CanvasInputSample s);
    void PointerUp(ToolContext ctx, CanvasInputSample s);
    void Cancel(ToolContext ctx);
    // Draw real-time UI on top of the composited canvas (selection outline, shape preview, etc.)
    void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom);
    // Called when the tool becomes active or inactive
    void Activate(ToolContext ctx) { }
    void Deactivate(ToolContext ctx) { }
}

// Shared state passed into every tool call — tools must not cache this reference.
public sealed class ToolContext
{
    public DrawingDocument Document { get; }
    public BrushPreset Brush { get; set; } = null!;
    public Color PaintColor { get; set; } = Color.Parse("#111111");
    public SelectionMask Selection { get; } = new();
    public Action InvalidateRender { get; init; } = () => { };
    public Action<Color> OnColorSampled { get; init; } = _ => { };

    public DrawingLayer? ActiveLayer =>
        Document.ActiveLayerIndex >= 0 && Document.ActiveLayerIndex < Document.Layers.Count
            ? Document.Layers[Document.ActiveLayerIndex]
            : null;

    public int ActiveLayerIndex => Document.ActiveLayerIndex;

    public ToolContext(DrawingDocument document)
    {
        Document = document;
    }

    // Convenience: commit tile mutation and notify compositor in one call.
    public void CommitMutation(int layerIndex, System.Collections.Generic.Dictionary<(int, int), byte[]?> beforeTiles, PixelRegion dirty)
    {
        Document.CommitLayerTileMutation(layerIndex, beforeTiles, dirty);
        Document.NotifyChanged(dirty, layerIndex);
        ActiveLayer?.MarkThumbnailDirty();
    }
}
