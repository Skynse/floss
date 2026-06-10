using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Output;
using Floss.App.Tools;

namespace Floss.App.Tests;

public class MagicWandTests
{
    [Fact]
    public void LayerLocalFloodFill_OffsetLayer_DoesNotSelectOutsideLayerFootprint()
    {
        const int docW = 100, docH = 100;
        var mask = new SelectionMask();
        mask.Resize(docW, docH);

        var pixels = new TiledPixelBuffer(10, 10);
        pixels.SetPixel(0, 0, 40, 120, 200, 255);

        mask.SetFromFloodFillLayerLocal(
            pixels, 0, 0, offsetX: 50, offsetY: 50, layerW: 10, layerH: 10,
            tolerance: 1.0, SelectOp.Replace, contiguousFill: true);

        Assert.True(mask.IsSelected(50, 50));
        Assert.False(mask.IsSelected(0, 0));
        Assert.False(mask.IsSelected(49, 49));
        Assert.False(mask.IsSelected(60, 60));

        for (int y = 0; y < docH; y++)
        {
            for (int x = 0; x < docW; x++)
            {
                if (x < 50 || x >= 60 || y < 50 || y >= 60)
                    Assert.False(mask.IsSelected(x, y));
            }
        }
    }

    [Fact]
    public void LayerLocalFloodFill_LocalClickOutsideLayerSize_IsNoOp()
    {
        var mask = new SelectionMask();
        mask.Resize(100, 100);
        mask.SetFromRect(0, 0, 10, 10, SelectOp.Replace);

        var pixels = new TiledPixelBuffer(10, 10);
        mask.SetFromFloodFillLayerLocal(
            pixels, 15, 15, offsetX: 50, offsetY: 50, layerW: 10, layerH: 10,
            tolerance: 0, SelectOp.Replace);

        Assert.True(mask.IsSelected(0, 0));
        Assert.False(mask.IsSelected(55, 55));
    }

    [Fact]
    public void MagicWandOutput_SamplesMaskWhenMaskEditing()
    {
        var doc = new DrawingDocument(32, 32);
        doc.AddLayer();
        var layer = doc.ActiveLayer!;
        layer.CreateMask();
        doc.SetLayerMaskEditing(0, true);

        layer.Pixels.SetPixel(4, 4, 255, 0, 0, 255);
        layer.MaskPixels!.SetPixel(4, 4, 255, 255, 255, 255);
        layer.MaskPixels.SetPixel(5, 4, 0, 0, 0, 0);

        var ctx = new ToolContext(doc)
        {
            CommitSelectionMutation = doc.CommitSelectionMutation
        };

        var wand = new MagicWandOutput { Tolerance = 0 };
        wand.Execute(ctx, new ClickInput { Point = Click(4, 4) });

        Assert.True(doc.Selection.IsSelected(4, 4));
        Assert.False(doc.Selection.IsSelected(5, 4));
    }

    [Fact]
    public void MagicWandOutput_LockedLayer_DoesNotChangeSelection()
    {
        var doc = new DrawingDocument(16, 16);
        doc.AddLayer();
        var layer = doc.ActiveLayer!;
        layer.IsLocked = true;
        layer.Pixels.SetPixel(4, 4, 255, 0, 0, 255);

        var ctx = new ToolContext(doc)
        {
            CommitSelectionMutation = doc.CommitSelectionMutation
        };

        var wand = new MagicWandOutput { Tolerance = 0 };
        wand.Execute(ctx, new ClickInput { Point = Click(4, 4) });

        Assert.False(doc.Selection.HasSelection);
    }

    [Fact]
    public void MagicWandOutput_OutOfDocumentBounds_IsNoOp()
    {
        var doc = new DrawingDocument(16, 16);
        doc.AddLayer();
        doc.ActiveLayer!.Pixels.SetPixel(4, 4, 255, 0, 0, 255);

        var ctx = new ToolContext(doc)
        {
            CommitSelectionMutation = doc.CommitSelectionMutation
        };

        var wand = new MagicWandOutput();
        wand.Execute(ctx, new ClickInput { Point = Click(20, 20) });

        Assert.False(doc.Selection.HasSelection);
    }

    private static CanvasInputSample Click(double x, double y)
        => new(x, y, 1, 0, 0, 0, 0, 1, CanvasInputSource.Mouse, CanvasInputPhase.Down);
}
