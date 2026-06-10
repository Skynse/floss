namespace Floss.App.Tests;

public class LayerPickQueriesTests
{
    [Fact]
    public void FindLayersAtPoint_ReturnsTopmostPaintLayersWithPixel()
    {
        var document = new DrawingDocument(4, 4);
        document.AddLayer();
        document.ActiveLayer!.Pixels.SetPixel(1, 1, 10, 20, 30, 255);
        document.AddLayer();
        document.ActiveLayer.Pixels.SetPixel(1, 1, 40, 50, 60, 255);

        var found = LayerPickQueries.FindLayersAtPoint(document, 1, 1);

        TestAssertions.Equal(2, found.Count);
        TestAssertions.Equal(1, found[0]);
        TestAssertions.Equal(0, found[1]);
    }

    [Fact]
    public void FindLayersInRect_ReturnsLayersWithContentInArea()
    {
        var document = new DrawingDocument(8, 8);
        document.AddLayer();
        document.ActiveLayer!.Pixels.SetPixel(1, 1, 255, 0, 0, 255);
        document.AddLayer();
        document.ActiveLayer.Pixels.SetPixel(6, 6, 0, 255, 0, 255);

        var found = LayerPickQueries.FindLayersInRect(document, 0, 0, 3, 3);

        TestAssertions.Equal(1, found.Count);
        TestAssertions.Equal(0, found[0]);
    }
}
