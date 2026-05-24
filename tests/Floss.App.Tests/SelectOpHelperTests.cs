using Avalonia.Input;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Tests;

public class SelectOpHelperTests
{
    [Theory]
    [InlineData(SelectOp.Replace, ToolAuxOperationType.AddToSelection, SelectOp.Add)]
    [InlineData(SelectOp.Replace, ToolAuxOperationType.RemoveFromSelection, SelectOp.Subtract)]
    [InlineData(SelectOp.Replace, ToolAuxOperationType.SelectFromSelection, SelectOp.Intersect)]
    [InlineData(SelectOp.Add, ToolAuxOperationType.None, SelectOp.Add)]
    [InlineData(SelectOp.Subtract, ToolAuxOperationType.StraightLine, SelectOp.Subtract)]
    public void Resolve_MapsModifierAuxOrPreset(SelectOp preset, ToolAuxOperationType aux, SelectOp expected)
        => Assert.Equal(expected, SelectOpHelper.Resolve(preset, aux));

    [Theory]
    [InlineData(SelectOp.Replace, KeyModifiers.Shift, SelectOp.Add)]
    [InlineData(SelectOp.Replace, KeyModifiers.Alt, SelectOp.Subtract)]
    [InlineData(SelectOp.Replace, KeyModifiers.Shift | KeyModifiers.Alt, SelectOp.Intersect)]
    public void ResolveForSelection_UsesHeldModifiersWhenAuxUnset(SelectOp preset, KeyModifiers mods, SelectOp expected)
    {
        var ctx = new ToolContext(new DrawingDocument())
        {
            CurrentModifiers = mods
        };
        Assert.Equal(expected, SelectOpHelper.ResolveForSelection(preset, ctx));
    }

    [Fact]
    public void ResolveForSelection_UsesLockedGestureOp()
    {
        var ctx = new ToolContext(new DrawingDocument())
        {
            ActiveSelectionOp = SelectOp.Add,
            CurrentModifiers = KeyModifiers.None
        };
        Assert.Equal(SelectOp.Add, SelectOpHelper.ResolveForSelection(SelectOp.Replace, ctx));
    }

    [Fact]
    public void SelectionMask_Add_UnionRects()
    {
        var mask = new SelectionMask();
        mask.Resize(20, 20);

        mask.SetFromRect(2, 2, 4, 4, SelectOp.Replace);
        mask.SetFromRect(10, 2, 4, 4, SelectOp.Add);

        Assert.True(mask.IsSelected(3, 3));
        Assert.True(mask.IsSelected(11, 3));
        Assert.False(mask.IsSelected(8, 3));
    }

    [Fact]
    public void SelectionMask_Add_SecondLassoUsesMaskOutlineGeometry()
    {
        var mask = new SelectionMask();
        mask.Resize(100, 100);

        mask.SetFromPolygon(
        [
            new(10, 10), new(40, 10), new(40, 40), new(10, 40)
        ], SelectOp.Replace);
        mask.SetFromPolygon(
        [
            new(60, 60), new(90, 60), new(90, 90), new(60, 90)
        ], SelectOp.Add);

        Assert.True(mask.IsSelected(20, 20));
        Assert.True(mask.IsSelected(75, 75));
        Assert.Equal("Mask", mask.OutlineGeometryKindForTests);
    }

    [Fact]
    public void SelectionMask_ReplaceZeroRect_ClearsSelection()
    {
        var mask = new SelectionMask();
        mask.Resize(20, 20);
        mask.SetFromRect(2, 2, 4, 4, SelectOp.Replace);
        mask.SetFromRect(5, 5, 0, 0, SelectOp.Replace);
        Assert.False(mask.HasSelection);
    }

    [Fact]
    public void SelectionMask_Subtract_RemovesOverlap()
    {
        var mask = new SelectionMask();
        mask.Resize(20, 20);

        mask.SetFromRect(2, 2, 8, 8, SelectOp.Replace);
        mask.SetFromRect(5, 5, 4, 4, SelectOp.Subtract);

        Assert.False(mask.IsSelected(6, 6));
        Assert.True(mask.IsSelected(3, 3));
    }

    [Fact]
    public void SelectionMask_Intersect_KeepsOverlapOnly()
    {
        var mask = new SelectionMask();
        mask.Resize(20, 20);

        mask.SetFromRect(2, 2, 8, 8, SelectOp.Replace);
        mask.SetFromRect(5, 5, 8, 8, SelectOp.Intersect);

        Assert.True(mask.IsSelected(6, 6));
        Assert.False(mask.IsSelected(3, 3));
        Assert.False(mask.IsSelected(12, 12));
    }
}
