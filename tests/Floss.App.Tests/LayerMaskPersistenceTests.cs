using Floss.App.Document;

namespace Floss.App.Tests;

public sealed class LayerMaskPersistenceTests
{
    [Fact]
    public void FlossRoundTrip_PreservesLayerMaskTiles()
    {
        var doc = new DrawingDocument(64, 64);
        doc.AddAdjustmentLayer(AdjustmentKind.HueSaturationLuminosity);
        doc.CreateLayerMask(0);
        doc.Layers[0].MaskPixels!.SetPixel(20, 20, 0, 0, 0, 200);
        doc.Layers[0].MaskPixels.SetPixel(30, 25, 0, 0, 0, 64);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        TestAssertions.True(loaded.Layers[0].HasMask);
        loaded.Layers[0].MaskPixels!.GetPixel(20, 20, out _, out _, out _, out var a1);
        loaded.Layers[0].MaskPixels.GetPixel(30, 25, out _, out _, out _, out var a2);
        TestAssertions.Equal(200, a1);
        TestAssertions.Equal(64, a2);
    }

    [Fact]
    public void MaskPaint_UndoRedo_RestoresMaskTiles()
    {
        var doc = new DrawingDocument(64, 64);
        doc.AddLayer();
        doc.CreateLayerMask(0);
        doc.SetLayerMaskEditing(0, true);

        var before = doc.Layers[0].MaskPixels!.CaptureTile(0, 0);
        doc.Layers[0].MaskPixels.SetPixel(5, 5, 0, 0, 0, 128);
        var after = doc.Layers[0].MaskPixels.CaptureTile(0, 0);
        var dirty = new PixelRegion(0, 0, 64, 64);
        doc.CommitLayerTileMutation(0, new Dictionary<(int X, int Y), byte[]?> { [(0, 0)] = before }, dirty);

        doc.Undo();
        doc.Layers[0].MaskPixels!.GetPixel(5, 5, out _, out _, out _, out var undone);
        TestAssertions.Equal(255, undone);

        doc.Redo();
        doc.Layers[0].MaskPixels!.GetPixel(5, 5, out _, out _, out _, out var redone);
        TestAssertions.Equal(128, redone);
        _ = after;
    }

    [Fact]
    public void CreateDeleteMask_UndoRedo()
    {
        var doc = new DrawingDocument(64, 64);
        doc.AddLayer();
        doc.CreateLayerMask(0);
        TestAssertions.True(doc.Layers[0].HasMask);

        doc.Undo();
        TestAssertions.False(doc.Layers[0].HasMask);

        doc.Redo();
        TestAssertions.True(doc.Layers[0].HasMask);
    }
}
