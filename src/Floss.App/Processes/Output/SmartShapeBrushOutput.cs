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
            _strokePreview.Update(ctx, shapePreview.Shape, shapePreview.RawSamples, shapePreview.StrokeClosed);
            return;
        }

        if (_input?.Phase == SmartShapePhase.Preview)
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

        var shapeSamples = SmartShapePolyline.ToDocumentSamples(
            commit.Shape, layer, commit.RawSamples, commit.StrokeClosed);
        if (shapeSamples.Count < 2)
            return;

        var rawSamples = SmartShapeSampleRemap.ToDocumentSamples(commit.RawSamples, layer);

        var shapeRegion = EstimateDirtyRegion(layer, ctx.Brush, shapeSamples);
        var rawRegion = rawSamples.Count >= 2
            ? EstimateDirtyRegion(layer, ctx.Brush, rawSamples)
            : PixelRegion.Empty;
        var fullRegion = rawRegion.IsEmpty ? shapeRegion : shapeRegion.Union(rawRegion);
        if (fullRegion.IsEmpty)
            return;

        var beforeEmpty = new Dictionary<(int X, int Y), byte[]?>();
        layer.ActivePixels.EnterPixelReadLock();
        try
        {
            layer.CaptureTiles(fullRegion, beforeEmpty);
        }
        finally
        {
            layer.ActivePixels.ExitPixelReadLock();
        }

        var plan = SmartShapeCommitRasterizer.Plan(
            _brushEngine,
            ctx,
            layer,
            ctx.Brush,
            beforeEmpty,
            fullRegion,
            rawSamples,
            shapeSamples);

        if (plan.SmartStepPatches.Count == 0)
            return;

        var maskMutation = layer.IsMaskEditing && layer.HasMask;
        var tileDirty = fullRegion.Translate(layer.OffsetX, layer.OffsetY);

        var historyLabel = HistoryLabels.FromPreset(ctx.ActivePreset);

        try
        {
            using var mutation = ctx.Document.RenderLock.Write();
            layer.ActivePixels.LiveStroke = true;
            ctx.Document.NotifyStrokeSuspendBegin(tileDirty, ctx.ActiveLayerIndex);

            foreach (var patch in plan.SmartStepPatches)
                layer.RestorePaintTile(patch.TileX, patch.TileY, patch.AfterPixels, maskMutation);

            if (plan.RawStepPatches.Count > 0)
            {
                ctx.Document.SetPendingHistoryLabel(historyLabel);
                ctx.Document.PushLayerTileHistoryPatches(
                    ctx.ActiveLayerIndex, plan.RawStepPatches, plan.DirtyRegion);
            }

            ctx.Document.SetPendingHistoryLabel(historyLabel);
            ctx.Document.PushLayerTileHistoryPatches(
                ctx.ActiveLayerIndex, plan.SmartStepPatches, plan.DirtyRegion);

            layer.MarkThumbnailDirty();
            if (maskMutation)
                layer.MarkMaskThumbnailDirty();

            ctx.Document.CommitStroke();
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

    private PixelRegion EstimateDirtyRegion(DrawingLayer layer, BrushPreset brush, List<CanvasInputSample> samples)
    {
        var region = PixelRegion.Empty;
        for (var i = 1; i < samples.Count; i++)
            region = region.Union(_brushEngine.EstimateSegmentRegion(layer, brush, samples[i - 1], samples[i]));
        return region;
    }
}
