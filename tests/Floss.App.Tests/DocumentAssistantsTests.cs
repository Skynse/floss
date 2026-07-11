using Avalonia;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Xunit;

namespace Floss.App.Tests;

public class DocumentAssistantsTests
{
    [Fact]
    public void Add_AttachesRulerToActiveLayer_WhenCreateAtEditingLayer()
    {
        var doc = new DrawingDocument(256, 256);
        doc.SelectLayer(0);
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.PerspectiveType, new Point(10, 10), new Point(200, 200));

        doc.Assistants.Add(ruler, createAtEditingLayer: true);

        Assert.Single(doc.Assistants.Rulers);
        Assert.Equal(ruler.Id, doc.Assistants.Rulers[0].Id);
        Assert.NotNull(doc.Layers[0].RulerSet);
        Assert.True(doc.Layers[0].RulerSet!.HasRulers);
        Assert.False(doc.Layers[0].IsObjectLayer);
    }

    [Fact]
    public void Add_CreatesHostLayerAtBottom_WhenNotCreateAtEditingLayer()
    {
        var doc = new DrawingDocument(256, 256);
        doc.SelectLayer(0);
        var initialCount = doc.Layers.Count;
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.RulerType, new Point(0, 0), new Point(100, 100));

        doc.Assistants.Add(ruler, createAtEditingLayer: false);

        Assert.Equal(initialCount + 1, doc.Layers.Count);
        var host = doc.Layers[^1];
        Assert.NotNull(host.RulerSet);
        Assert.Contains(ruler, host.RulerSet!.Rulers);
        Assert.False(host.IsObjectLayer);
    }

    [Fact]
    public void CaptureSnapshot_RoundTripsLayerRulerSets()
    {
        var doc = new DrawingDocument(256, 256);
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.PerspectiveType, new Point(5, 5), new Point(50, 50));
        doc.Assistants.Add(ruler, createAtEditingLayer: true);
        ruler.SnapEnabled = false;
        ruler.GridSubdivisions = 8;

        var snap = doc.Assistants.CaptureSnapshot();
        doc.Assistants.Remove(ruler.Id);
        Assert.Empty(doc.Assistants.Rulers);

        doc.Assistants.RestoreSnapshot(snap);

        Assert.Single(doc.Assistants.Rulers);
        Assert.Equal(8, doc.Assistants.Rulers[0].GridSubdivisions);
        Assert.False(doc.Assistants.Rulers[0].SnapEnabled);
    }

    [Fact]
    public void HitTest_SelectsRulerOnLayer()
    {
        var doc = new DrawingDocument(256, 256);
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.RulerType, new Point(10, 10), new Point(100, 10));
        doc.Assistants.Add(ruler, createAtEditingLayer: true);

        var hit = doc.Assistants.HitTest(55, 10, 8);

        Assert.NotNull(hit);
        Assert.Equal(ruler.Id, hit.Id);
    }

    [Fact]
    public void All_ExcludesRulersOnHiddenLayers()
    {
        var doc = new DrawingDocument(256, 256);
        doc.SelectLayer(0);
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.RulerType, new Point(10, 10), new Point(100, 10));
        doc.Assistants.Add(ruler, createAtEditingLayer: true);

        Assert.Single(doc.Assistants.All);

        doc.Layers[0].IsVisible = false;

        Assert.Empty(doc.Assistants.All);
    }

    [Fact]
    public void ClearSelection_DeselectsWithoutRemovingRulers()
    {
        var doc = new DrawingDocument(256, 256);
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.RulerType, new Point(10, 10), new Point(100, 10));
        doc.Assistants.Add(ruler, createAtEditingLayer: true);
        doc.Assistants.SelectedId = ruler.Id;

        doc.Assistants.ClearSelection();

        Assert.Null(doc.Assistants.SelectedId);
        Assert.Single(doc.Assistants.Rulers);
    }

    [Fact]
    public void EnumerateForRender_ExcludesRulersOnHiddenLayers()
    {
        var doc = new DrawingDocument(256, 256);
        doc.SelectLayer(0);
        var ruler = PaintingAssistant.FromDrag(PaintingAssistant.RulerType, new Point(10, 10), new Point(100, 10));
        doc.Assistants.Add(ruler, createAtEditingLayer: true);

        Assert.Single(doc.Assistants.EnumerateForRender());

        doc.Layers[0].IsVisible = false;

        Assert.Empty(doc.Assistants.EnumerateForRender());
    }
}
