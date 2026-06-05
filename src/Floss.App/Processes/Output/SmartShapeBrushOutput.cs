using System;
using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Brushes.Engine;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes.Input;
using Floss.App.SmartShape;
using Floss.App.Tools;
using Avalonia.Media;

namespace Floss.App.Processes.Output;

/// <summary>
/// Live drawing via <see cref="DirectDrawOutput"/>; smart-shape phases use
/// <see cref="SmartShapeStrokePreview"/> (Ctrl+T-style overlay, no layer mutation).
/// </summary>
public sealed class SmartShapeBrushOutput : IOutputProcess
{
    private readonly DirectDrawOutput _direct;
    private readonly BrushEngine _brushEngine;
    private readonly SmartShapeStrokePreview _strokePreview;
    private SmartShapeBrushInputProcess? _input;

    public SmartShapeBrushOutput(BrushEngine brushEngine, DrawingDocument document)
    {
        _brushEngine = brushEngine;
        _direct = new DirectDrawOutput(brushEngine, document);
        _strokePreview = new SmartShapeStrokePreview(brushEngine);
    }

    public void BindInput(SmartShapeBrushInputProcess input) => _input = input;

    public bool Antialiasing
    {
        get => _direct.Antialiasing;
        set => _direct.Antialiasing = value;
    }

    public bool HasPendingWork => _direct.HasPendingWork || _strokePreview.IsActive;

    public bool IsPaintOutput => true;

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is SmartShapeCommitInput shapePreview && _input?.HasPendingSmartShape == true)
        {
            _strokePreview.Update(ctx, shapePreview.Shape, shapePreview.AvgPressure);
            return;
        }

        if (_input?.Phase is SmartShapePhase.Adjusting or SmartShapePhase.Launcher or SmartShapePhase.Gizmo)
            return;

        _direct.Preview(ctx, input);
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is SmartShapeCommitInput commit)
        {
            ClearStrokePreview();
            CommitShape(ctx, commit);
            return;
        }

        _direct.Execute(ctx, input);
    }

    public void Cancel() => _direct.Cancel();

    public void FinalizeAccepting() => _direct.FinalizeAccepting();

    public void DrawStrokePreview(DrawingContext dc) => _strokePreview.Draw(dc);

    public void ClearStrokePreview() => _strokePreview.Clear();

    public void AbortLiveStroke()
    {
        ClearStrokePreview();
        _direct.WaitUntilIdle();
        _direct.Cancel();
    }

    private void CommitShape(ToolContext ctx, SmartShapeCommitInput commit)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked)
            return;

        var samples = SmartShapePolyline.ToDocumentSamples(commit.Shape, layer, commit.AvgPressure);
        if (samples.Count < 2)
            return;

        var region = EstimateDirtyRegion(layer, ctx.Brush, samples);
        if (region.IsEmpty)
            return;

        var beforeTiles = new Dictionary<(int X, int Y), byte[]?>();
        layer.ActivePixels.EnterPixelReadLock();
        try
        {
            layer.CaptureTiles(region, beforeTiles);
        }
        finally
        {
            layer.ActivePixels.ExitPixelReadLock();
        }

        try
        {
            using var mutation = ctx.Document.RenderLock.Write();
            PaintShapeSegments(ctx, layer, ctx.Brush, samples, region, beforeTiles);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "SmartShapeBrushOutput.CommitShape");
        }
        finally
        {
            layer.ActivePixels.LiveStroke = false;
            ctx.Document.NotifyStrokeSuspendEnd();
        }

        ctx.InvalidateRender();
    }

    private void PaintShapeSegments(
        ToolContext ctx,
        DrawingLayer layer,
        BrushPreset brush,
        List<CanvasInputSample> samples,
        PixelRegion region,
        Dictionary<(int X, int Y), byte[]?> beforeTiles)
    {
        layer.ActivePixels.LiveStroke = true;
        ctx.Document.NotifyStrokeSuspendBegin(region.Translate(layer.OffsetX, layer.OffsetY), ctx.ActiveLayerIndex);

        _brushEngine.CanvasZoom = ctx.Viewport?.Zoom ?? 1.0;
        _brushEngine.BeginStroke(brush, samples[0]);
        var dirty = _brushEngine.RasterizeSegments(layer, brush, samples, 1, samples.Count - 1);
        _brushEngine.EndStroke();

        if (dirty.IsEmpty)
            return;

        RestoreUnselectedPixels(layer, dirty, ctx.Selection, beforeTiles);

        var tileDirty = dirty.Translate(layer.OffsetX, layer.OffsetY);
        layer.MarkThumbnailDirty();
        if (layer.IsMaskEditing)
            layer.MarkMaskThumbnailDirty();

        ctx.Document.CommitLayerTileMutation(ctx.ActiveLayerIndex, beforeTiles, tileDirty);
        ctx.Document.CommitStroke();
        ctx.Document.NotifyChanged(tileDirty, ctx.ActiveLayerIndex);
    }

    private PixelRegion EstimateDirtyRegion(DrawingLayer layer, BrushPreset brush, List<CanvasInputSample> samples)
    {
        var region = PixelRegion.Empty;
        for (var i = 1; i < samples.Count; i++)
            region = region.Union(_brushEngine.EstimateSegmentRegion(layer, brush, samples[i - 1], samples[i]));
        return region;
    }

    private static void RestoreUnselectedPixels(
        DrawingLayer layer,
        PixelRegion dirty,
        SelectionMask selection,
        Dictionary<(int X, int Y), byte[]?> beforeTiles)
    {
        if (dirty.IsEmpty)
            return;

        var hasSelection = selection.HasSelection;
        var alphaLocked = layer.IsAlphaLocked;
        if (!hasSelection && !alphaLocked)
            return;

        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(dirty.X, ts);
        var firstTileY = FloorDiv(dirty.Y, ts);
        var lastTileX = FloorDiv(dirty.Right - 1, ts);
        var lastTileY = FloorDiv(dirty.Bottom - 1, ts);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            var tilePixY = ty * ts;
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                if (!beforeTiles.TryGetValue((tx, ty), out var beforeTile))
                    continue;

                var pxMin = Math.Max(dirty.X, tx * ts);
                var pxMax = Math.Min(dirty.Right, tx * ts + ts);
                var pyMin = Math.Max(dirty.Y, ty * ts);
                var pyMax = Math.Min(dirty.Bottom, ty * ts + ts);
                if (pxMin >= pxMax || pyMin >= pyMax)
                    continue;

                byte[]? liveTile = null;
                for (var py = pyMin; py < pyMax; py++)
                {
                    var ly = py - tilePixY;
                    var rowBase = ly * ts * 4;
                    for (var px = pxMin; px < pxMax; px++)
                    {
                        var inSelection = !hasSelection || selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY);
                        var lx = px - tx * ts;
                        var offset = rowBase + lx * 4;
                        var hadAlpha = !alphaLocked || beforeTile is { } bt && bt[offset + 3] > 0;
                        if (inSelection && hadAlpha)
                            continue;

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

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);
}
