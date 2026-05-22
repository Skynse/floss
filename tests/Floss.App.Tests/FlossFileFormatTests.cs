namespace Floss.App.Tests;

public class FlossFileFormatTests
{
    [Fact]
    public void RoundTrip_BasicDocument()
    {
        var doc = new DrawingDocument(100, 80);
        doc.AddLayer();
        doc.ActiveLayer!.Pixels.SetPixel(10, 10, 1, 2, 3, 255);
        doc.ActiveLayer.Pixels.SetPixel(50, 40, 4, 5, 6, 128);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        TestAssertions.Equal(100, loaded.Width);
        TestAssertions.Equal(80, loaded.Height);
        TestAssertions.Equal(1, loaded.Layers.Count);
        TestAssertions.Equal(0, loaded.ActiveLayerIndex);

        loaded.Layers[0].Pixels.GetPixel(10, 10, out var b1, out var g1, out var r1, out var a1);
        TestAssertions.SequenceEqual(new byte[] { 1, 2, 3, 255 }, [b1, g1, r1, a1]);

        loaded.Layers[0].Pixels.GetPixel(50, 40, out var b2, out var g2, out var r2, out var a2);
        TestAssertions.SequenceEqual(new byte[] { 4, 5, 6, 128 }, [b2, g2, r2, a2]);
    }

    [Fact]
    public void RoundTrip_PaperColor()
    {
        var doc = new DrawingDocument(64, 64);
        doc.SetPaperColor(Avalonia.Media.Color.FromArgb(255, 247, 244, 237));

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        TestAssertions.Equal(247, loaded.PaperColor.R);
        TestAssertions.Equal(244, loaded.PaperColor.G);
        TestAssertions.Equal(237, loaded.PaperColor.B);
        TestAssertions.Equal(255, loaded.PaperColor.A);
    }

    [Fact]
    public void RoundTrip_PaperLayer()
    {
        var doc = new DrawingDocument(64, 64);
        doc.SetPaperColor(Avalonia.Media.Colors.White);
        doc.AddBackgroundLayer();

        TestAssertions.True(doc.PaperLayer != null);
        TestAssertions.True(doc.Layers[0].IsPaper);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        TestAssertions.Equal(1, loaded.Layers.Count);
        TestAssertions.True(loaded.Layers[0].IsPaper);
        TestAssertions.Equal("Paper", loaded.Layers[0].Name);
        TestAssertions.True(loaded.PaperLayer != null);
        TestAssertions.True(ReferenceEquals(loaded.PaperLayer, loaded.Layers[0]));
    }

    [Fact]
    public void RoundTrip_OutOfBoundsTiles()
    {
        var doc = new DrawingDocument(64, 64);
        doc.AddLayer();
        // Draw outside the document bounds — creates negative-coordinate tiles
        doc.ActiveLayer!.Pixels.SetPixel(-10, -20, 7, 8, 9, 255);
        doc.ActiveLayer.Pixels.SetPixel(70, 80, 10, 11, 12, 200);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        loaded.Layers[0].Pixels.GetPixel(-10, -20, out var b1, out var g1, out var r1, out var a1);
        TestAssertions.SequenceEqual(new byte[] { 7, 8, 9, 255 }, [b1, g1, r1, a1]);

        loaded.Layers[0].Pixels.GetPixel(70, 80, out var b2, out var g2, out var r2, out var a2);
        TestAssertions.SequenceEqual(new byte[] { 10, 11, 12, 200 }, [b2, g2, r2, a2]);
    }

    [Fact]
    public void RoundTrip_MultipleLayersWithGroup()
    {
        var doc = new DrawingDocument(100, 100);
        doc.AddLayer();
        doc.AddGroupLayer();

        // Group the first two layers into the group
        doc.GroupSelectedLayers([0, 1]);

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        TestAssertions.True(loaded.Layers.Count >= 3);
        TestAssertions.True(loaded.Layers.Any(l => l.IsGroup));

        var group = loaded.Layers.First(l => l.IsGroup);
        TestAssertions.True(group.Children.Count > 0);
    }

    [Fact]
    public void RoundTrip_EmptyLayer()
    {
        var doc = new DrawingDocument(50, 50);
        doc.AddLayer();
        // Layer with no pixel content

        using var stream = new MemoryStream();
        Floss.App.FlossFiles.FlossFileFormat.Save(stream, doc);

        stream.Position = 0;
        var loaded = Floss.App.FlossFiles.FlossFileFormat.Load(stream);

        TestAssertions.Equal(50, loaded.Width);
        TestAssertions.Equal(50, loaded.Height);
        TestAssertions.Equal(1, loaded.Layers.Count);
        TestAssertions.Equal(PixelRegion.Empty, loaded.Layers[0].Pixels.ContentTileBounds);
    }
}
