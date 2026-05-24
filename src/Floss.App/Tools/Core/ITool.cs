using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using System.Collections.Generic;

namespace Floss.App.Tools;

public readonly record struct EyedropperSampleOptions(
    EyedropperSampleMode Mode,
    bool ExcludeLockedLayers,
    bool ExcludeReferenceLayers)
{
    public static EyedropperSampleOptions Default => new(EyedropperSampleMode.Image, false, false);
}

// Central interface every tool implements.
public interface ITool
{
    void PointerDown(ToolContext ctx, CanvasInputSample s);
    void PointerMove(ToolContext ctx, CanvasInputSample s);
    void PointerUp(ToolContext ctx, CanvasInputSample s);
    void Cancel(ToolContext ctx);
    bool HasPendingOperation => false;
    // Draw real-time UI on top of the composited canvas (selection outline, shape preview, etc.)
    void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom);
    // Called when the tool becomes active or inactive
    void Activate(ToolContext ctx) { }
    void Deactivate(ToolContext ctx) { }
    // Commit any pending operation (e.g. on Enter key or double-click).
    // Default no-op — only tools with modal operations need to implement this.
    void Commit(ToolContext ctx) { }
    // Whether a double-click should commit the current operation.
    bool CanCommitFromClick => false;
    // Whether the tool consumes the given modifier internally (e.g. Shift for constraining shapes).
    // Prevents the modifier from being dispatched to alternate-tool invocation.
    bool ConsumesModifier(KeyModifiers mods) => false;
    // The tool to temporarily swap to when the alternate-invocation key is held. Null means no swap.
    ITool? Alternate => null;
}

// Optional rendering hook for in-progress tool operations.
public interface IToolOperationOverlay
{
    void RenderOverlay(DrawingContext dc, double zoom);
}

// Shared state passed into every tool call — tools must not cache this reference.
public sealed class ToolContext
{
    public DrawingDocument Document { get; }
    public BrushPreset Brush { get; set; } = null!;
    public Color PaintColor { get; set; } = Color.Parse("#111111");
    public SelectionMask Selection => Document.Selection;
    public Action InvalidateRender { get; init; } = () => { };
    public Action InvalidateSelectionOverlay { get; init; } = () => { };
    public Action? TransformEditChanged { get; init; }
    public Action SelectionChanged { get; init; } = () => { };
    public Action<SelectionMask.Snapshot> CommitSelectionMutation { get; init; } = _ => { };
    public Action<Color> OnColorSampled { get; init; } = _ => { };
    public Func<int, int, EyedropperSampleOptions, Color?> SampleDocumentColor { get; init; } = (_, _, _) => null;
    public ToolPreset? ActivePreset { get; set; }
    public KeyModifiers CurrentModifiers { get; set; }
    public ToolAuxOperationType ToolAuxMode { get; set; }
    /// <summary>Locked at selection pointer-down so add/subtract survives the drag.</summary>
    public SelectOp? ActiveSelectionOp { get; set; }
    public IViewportController? Viewport { get; set; }
    public Size ViewportSize { get; set; }
    public DrawingLayer? ActiveLayer =>
        Document.ActiveLayerIndex >= 0 && Document.ActiveLayerIndex < Document.Layers.Count
            ? Document.Layers[Document.ActiveLayerIndex]
            : null;

    public int ActiveLayerIndex => Document.ActiveLayerIndex;

    public Action<int>? OnSelectLayer { get; init; }
    public Action<IReadOnlyList<int>>? OnSelectLayers { get; init; }

    public ToolContext(DrawingDocument document)
    {
        Document = document;
    }

    public void SelectLayer(int index)
    {
        Document.SelectLayer(index);
        OnSelectLayer?.Invoke(index);
    }

    public void SelectLayers(IReadOnlyList<int> indices)
    {
        if (indices.Count == 0) return;
        Document.SelectLayer(indices[0]);
        OnSelectLayer?.Invoke(indices[0]);
        OnSelectLayers?.Invoke(indices);
    }

    // Convenience: commit tile mutation and notify compositor in one call.
    public void CommitMutation(int layerIndex, System.Collections.Generic.Dictionary<(int, int), byte[]?> beforeTiles, PixelRegion dirty)
    {
        Document.CommitLayerTileMutation(layerIndex, beforeTiles, dirty);
        Document.NotifyChanged(dirty, layerIndex);
        ActiveLayer?.MarkThumbnailDirty();
    }
}
