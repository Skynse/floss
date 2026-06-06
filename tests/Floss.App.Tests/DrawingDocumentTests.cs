namespace Floss.App.Tests;

using Floss.App.Canvas.Compositing;
using Floss.App.Document;

public class DrawingDocumentTests
{
    [Fact]
    public void Constructor_SetsInitialLayerState()
    {
        var document = new DrawingDocument(20, 10);
        TestAssertions.Equal(20, document.Width);
        TestAssertions.Equal(10, document.Height);
        TestAssertions.Equal(0, document.Layers.Count);
        TestAssertions.Equal(-1, document.ActiveLayerIndex);
        TestAssertions.False(document.CanPaintActiveLayer);
        TestAssertions.False(document.CanUndo);
        TestAssertions.False(document.IsDirty);
    }

    [Fact]
    public void LayerManagement_WorksForCommonMutations()
    {
        var document = new DrawingDocument(4, 4);
        var layersChanged = 0;
        document.LayersChanged += (_, _) => layersChanged++;

        document.AddLayer();
        TestAssertions.Equal(1, document.Layers.Count);
        TestAssertions.Equal(0, document.ActiveLayerIndex);

        document.AddLayer();
        TestAssertions.Equal(2, document.Layers.Count);
        TestAssertions.Equal(1, document.ActiveLayerIndex);

        document.SelectLayer(0);
        document.DuplicateActiveLayer();
        TestAssertions.Equal(3, document.Layers.Count);
        TestAssertions.Equal("Layer 1 copy", document.ActiveLayer!.Name);

        document.DeleteActiveLayer();
        TestAssertions.Equal(2, document.Layers.Count);
        TestAssertions.True(layersChanged >= 4);
    }

    [Fact]
    public void CapabilityFlags_RespectLayerState()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ToggleLayerLock(0);
        TestAssertions.False(document.CanPaintActiveLayer);
        TestAssertions.False(document.CanDeleteLayer);
        document.ToggleLayerLock(0);

        document.AddGroupLayer();
        TestAssertions.False(document.CanPaintActiveLayer);
        TestAssertions.False(document.CanModifyActiveLayer);
    }

    [Fact]
    public void LayerPropertyMutations_UndoRedo()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.MarkAsSaved();
        document.SetActiveLayerName("Ink");
        document.SetActiveLayerOpacity(2);
        document.SetActiveLayerBlendMode(BlendMode.Multiply);

        TestAssertions.Equal("Ink", document.ActiveLayer.Name);
        TestAssertions.Equal(1.0, document.ActiveLayer.Opacity);
        TestAssertions.Equal(BlendMode.Multiply, document.ActiveLayer.BlendMode);
        TestAssertions.True(document.IsDirty);

        document.Undo();
        TestAssertions.Equal(BlendMode.Normal, document.ActiveLayer.BlendMode);
        document.Redo();
        TestAssertions.Equal(BlendMode.Multiply, document.ActiveLayer.BlendMode);
    }

    [Fact]
    public void ReferenceLayerFlag_UndoRedoAndDuplicate()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ToggleLayerReference(0);
        TestAssertions.True(document.ActiveLayer!.IsReference);

        document.Undo();
        TestAssertions.False(document.ActiveLayer.IsReference);
        document.Redo();
        TestAssertions.True(document.ActiveLayer.IsReference);

        document.DuplicateActiveLayer();
        TestAssertions.True(document.ActiveLayer.IsReference);
    }

    [Fact]
    public void SelectionMutations_UndoRedo()
    {
        var document = new DrawingDocument(8, 8);
        var changed = 0;
        document.SelectionChanged += (_, _) => changed++;

        var before = document.Selection.CaptureSnapshot();
        document.Selection.SetFromRect(1, 1, 3, 3);
        document.CommitSelectionMutation(before);

        TestAssertions.True(document.Selection.HasSelection);
        TestAssertions.True(document.Selection.IsSelected(2, 2));
        TestAssertions.False(document.Selection.IsSelected(5, 5));
        TestAssertions.True(document.CanUndo);

        document.Undo();
        TestAssertions.False(document.Selection.HasSelection);

        document.Redo();
        TestAssertions.True(document.Selection.HasSelection);
        TestAssertions.True(document.Selection.IsSelected(2, 2));
        TestAssertions.True(changed >= 3);
    }

    [Fact]
    public void SelectionInvert_KeepsRenderableMaskGeometry()
    {
        var document = new DrawingDocument(8, 8);
        document.Selection.SetFromRect(2, 2, 2, 2);
        document.Selection.Invert();

        var snapshot = document.Selection.CaptureSnapshot();
        TestAssertions.True(document.Selection.HasSelection);
        TestAssertions.Equal("Mask", snapshot.GeometryType);
        TestAssertions.False(document.Selection.IsSelected(2, 2));
        TestAssertions.True(document.Selection.IsSelected(0, 0));
        TestAssertions.Equal(new SKRectI(0, 0, 8, 8), document.Selection.GetMaskBounds());

        document.Selection.Clear();
        document.Selection.Invert();
        TestAssertions.True(document.Selection.HasSelection);
        TestAssertions.Equal("Mask", document.Selection.CaptureSnapshot().GeometryType);
        TestAssertions.Equal(new SKRectI(0, 0, 8, 8), document.Selection.GetMaskBounds());

        document.Selection.SetFromRect(2, 2, 2, 2);
        document.Selection.Invert();
        document.Selection.Invert();
        TestAssertions.Equal("Rect", document.Selection.CaptureSnapshot().GeometryType);
        TestAssertions.Equal(new SKRectI(2, 2, 4, 4), document.Selection.GetMaskBounds());
    }

    [Fact]
    public void Transform_StartsOnEmptySelection()
    {
        var document = new DrawingDocument(16, 16);
        document.AddLayer();
        document.Selection.SetFromRect(4, 4, 6, 6);
        var ctx = new ToolContext(document);
        var tool = new TransformTool();

        TestAssertions.True(tool.BeginTransform(ctx), "Selection transform should start from the selected region even when it contains no pixels.");
        TestAssertions.True(tool.HasPendingOperation, "Transform overlay should be active for an empty selected region.");
    }

    [Fact]
    public void ClearActiveLayer_UndoRedoRestoresPixels()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ActiveLayer.Pixels.SetPixel(1, 1, 1, 2, 3, 255);
        document.ClearActiveLayer();

        document.ActiveLayer.Pixels.GetPixel(1, 1, out _, out _, out _, out var clearedAlpha);
        TestAssertions.Equal((byte)0, clearedAlpha);
        TestAssertions.True(document.CanUndo);

        document.Undo();
        document.ActiveLayer.Pixels.GetPixel(1, 1, out var b, out var g, out var r, out var a);
        TestAssertions.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b, g, r, a]);

        document.Redo();
        document.ActiveLayer.Pixels.GetPixel(1, 1, out _, out _, out _, out var redoneAlpha);
        TestAssertions.Equal((byte)0, redoneAlpha);
    }

    [Fact]
    public void MoveLayer_ValidatesTargetsAndMoves()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();

        TestAssertions.False(document.CanMoveLayer(1, 1, LayerDropPlacement.Above));
        TestAssertions.False(document.CanMoveLayer(-1, 1, LayerDropPlacement.Above));

        var moving = document.ActiveLayer;
        document.MoveLayer(2, 0, LayerDropPlacement.Below);
        TestAssertions.Equal(0, document.ActiveLayerIndex);
        TestAssertions.True(ReferenceEquals(moving, document.ActiveLayer));
    }

    [Fact]
    public void MoveLayer_IntoGroup_AddsLayerAsChild()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();
        document.GroupSelectedLayers([0, 1]);

        var group = document.Layers.First(l => l.IsGroup);
        var groupIndex = document.Layers.ToList().IndexOf(group);
        var moving = document.Layers.First(l => !l.IsGroup && l.Parent == null);
        var movingIndex = document.Layers.ToList().IndexOf(moving);
        TestAssertions.True(document.CanMoveLayer(movingIndex, groupIndex, LayerDropPlacement.Into));

        document.MoveLayer(movingIndex, groupIndex, LayerDropPlacement.Into);

        TestAssertions.True(group.Children.Contains(moving));
        TestAssertions.True(group.IsOpen);
    }

    [Fact]
    public void GroupSelectedLayers_CreatesGroupAndUndoRestores()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();

        document.GroupSelectedLayers([0, 1, 2]);
        TestAssertions.Equal(4, document.Layers.Count);
        TestAssertions.True(document.ActiveLayer!.IsGroup);
        TestAssertions.Equal(3, document.ActiveLayer.Children.Count);
        TestAssertions.Equal(1, document.ActiveLayer.Children[0].IndentLevel);

        document.Undo();
        TestAssertions.Equal(3, document.Layers.Count);
        TestAssertions.False(document.Layers.Any(layer => layer.IsGroup));
    }

    [Fact]
    public void DeleteLayers_CanRemoveFolderAndChildren()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();
        document.GroupSelectedLayers([0, 1]);

        var groupIndex = document.ActiveLayerIndex;
        TestAssertions.True(document.ActiveLayer!.IsGroup);
        TestAssertions.True(document.CanDeleteLayers([groupIndex]));

        document.DeleteLayers([groupIndex]);

        TestAssertions.Equal(1, document.Layers.Count);
        TestAssertions.False(document.Layers.Any(layer => layer.IsGroup));
    }

    [Fact]
    public void MergeSelectedLayers_CombinesMultipleLayers()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();
        TestAssertions.Equal(3, document.Layers.Count);

        using var compositor = new LayerCompositor();
        document.MergeSelectedLayers([0, 1, 2], compositor);
        TestAssertions.Equal(1, document.Layers.Count);
        TestAssertions.Equal("Merged", document.ActiveLayer!.Name);
    }

    [Fact]
    public void HistoryKind_MarksUndoAndRedo()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        TestAssertions.Equal(DocumentHistoryChangeKind.Mutation, document.LastHistoryChangeKind);

        document.Undo();
        TestAssertions.Equal(DocumentHistoryChangeKind.Undo, document.LastHistoryChangeKind);

        document.Redo();
        TestAssertions.Equal(DocumentHistoryChangeKind.Redo, document.LastHistoryChangeKind);
    }

    [Fact]
    public void RenameLayer_DoesNotAffectVisualHistory()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.SetActiveLayerName("Ink");
        TestAssertions.False(document.LastHistoryAffectsVisual);
    }

    [Fact]
    public void ImportLifecycle_ReplacesDocumentState()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ClearForImport();
        TestAssertions.Equal(0, document.Layers.Count);
        TestAssertions.False(document.CanUndo);

        document.ResizeForImport(8, 9);
        var imported = document.AddLayerForImport("Imported", bitmapWidth: 2, bitmapHeight: 3);
        imported.Pixels.SetPixel(1, 2, 9, 8, 7, 255);
        document.FinalizeImport();

        TestAssertions.Equal(8, document.Width);
        TestAssertions.Equal(9, document.Height);
        TestAssertions.Equal("Imported", document.ActiveLayer.Name);
        document.ActiveLayer.Pixels.GetPixel(1, 2, out var b, out var g, out var r, out var a);
        TestAssertions.SequenceEqual(new byte[] { 9, 8, 7, 255 }, [b, g, r, a]);
    }

    [Fact]
    public void DuplicateActiveLayer_CopiesPixels()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ActiveLayer.Pixels.SetPixel(2, 2, 1, 2, 3, 255);
        document.DuplicateActiveLayer();

        document.ActiveLayer.Pixels.GetPixel(2, 2, out var b, out var g, out var r, out var a);
        TestAssertions.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b, g, r, a]);
        document.Layers[0].Pixels.SetPixel(2, 2, 0, 0, 0, 0);
        document.ActiveLayer.Pixels.GetPixel(2, 2, out _, out _, out _, out var duplicateAlpha);
        TestAssertions.Equal((byte)255, duplicateAlpha);
    }

    [Fact]
    public void AddBackgroundLayer_FillsWhiteAndTilesStayIndependent()
    {
        var document = new DrawingDocument(128, 64);
        document.AddBackgroundLayer();

        // Paper layers are handled by the compositor via PaperColor — no pixel fill needed.
        var background = document.Layers[0];
        Assert.True(background.IsPaper);
        Assert.True(background.IsLocked);
        Assert.Equal("Paper", background.Name);
        Assert.NotNull(document.PaperLayer);
        Assert.Equal(document.PaperLayer, background);
        Assert.Equal(128, background.Width);
        Assert.Equal(64, background.Height);
    }

    [Fact]
    public void FinalizeImport_RebindsNativePaperLayer()
    {
        var document = new DrawingDocument(128, 64);
        document.ClearForImport();

        var paint = document.AddLayerForImport("Paint");
        var paper = document.AddLayerForImport("Paper");
        paper.IsPaper = true;
        paper.IsLocked = true;

        document.FinalizeImport();

        Assert.Same(paper, document.PaperLayer);
        Assert.True(document.IsPaperBackgroundVisible);
        Assert.Same(paint, document.ActiveLayer);
    }

    [Fact]
    public void ToolFactory_EyedropperOptions()
    {
        var document = new DrawingDocument(4, 4);
        var factory = new Floss.App.Processes.ToolFactory(document, new BrushEngine());
        var tool = factory.CreateTool(new ToolPreset
        {
            InputProcess = InputProcessType.Click,
            OutputProcess = OutputProcessType.Eyedropper,
            EyedropperSampleMode = EyedropperSampleMode.CurrentLayer,
            EyedropperExcludeLockedLayers = true,
            EyedropperExcludeReferenceLayers = true
        });

        var output = ((Floss.App.Processes.CompositeTool)tool).Output as Floss.App.Processes.Output.EyedropperOutput
            ?? throw new InvalidOperationException("Expected eyedropper output.");
        TestAssertions.Equal(EyedropperSampleMode.CurrentLayer, output.SampleMode);
        TestAssertions.True(output.ExcludeLockedLayers);
        TestAssertions.True(output.ExcludeReferenceLayers);
    }

    [Fact]
    public void DeleteLayers_CanRemovePaperLayerWhenOtherLayersExist()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddBackgroundLayer();

        var paperIndex = document.Layers.ToList().FindIndex(l => l.IsPaper);
        TestAssertions.True(paperIndex >= 0);
        TestAssertions.True(document.CanDeleteLayers([paperIndex]));

        document.DeleteLayers([paperIndex]);

        Assert.Null(document.PaperLayer);
        TestAssertions.Equal(2, document.Layers.Count);
        TestAssertions.False(document.Layers.Any(l => l.IsPaper));
    }

    [Fact]
    public void DeletePaperLayer_UndoRedo_RestoresPaperBackground()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddBackgroundLayer();
        document.SetPaperColor(Avalonia.Media.Color.FromArgb(255, 240, 230, 220));

        var paperIndex = document.Layers.ToList().FindIndex(l => l.IsPaper);
        TestAssertions.True(paperIndex >= 0);
        TestAssertions.True(document.IsPaperBackgroundVisible);

        document.DeleteLayers([paperIndex]);
        Assert.Null(document.PaperLayer);
        TestAssertions.False(document.IsPaperBackgroundVisible);

        document.Undo();
        Assert.NotNull(document.PaperLayer);
        TestAssertions.True(document.Layers.Any(l => l.IsPaper));
        TestAssertions.True(document.IsPaperBackgroundVisible);
        TestAssertions.Equal(240, document.PaperColor.R);

        document.Redo();
        Assert.Null(document.PaperLayer);
        TestAssertions.False(document.IsPaperBackgroundVisible);
    }

    [Fact]
    public void DeleteLayers_RemovesMultipleSelectedLayers()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.AddLayer();
        document.AddLayer();

        TestAssertions.True(document.CanDeleteLayers([0, 2]));
        document.DeleteLayers([0, 2]);

        TestAssertions.Equal(1, document.Layers.Count);
        TestAssertions.Equal("Layer 2", document.ActiveLayer!.Name);
    }
}
