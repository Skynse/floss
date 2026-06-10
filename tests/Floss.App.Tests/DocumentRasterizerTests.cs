using Floss.App.ImageFiles;
using SkiaSharp;

namespace Floss.App.Tests;

public class DocumentRasterizerTests
{
    [Fact]
    public void RenderLayerBitmap_UsesOnlySelectedLayer()
    {
        var document = new DrawingDocument(2, 2);
        document.AddLayer();
        var sketch = document.ActiveLayer!;
        sketch.Pixels.SetPixel(0, 0, 10, 20, 30, 255);
        document.AddLayer();
        document.ActiveLayer!.Pixels.SetPixel(1, 1, 40, 50, 60, 255);

        using var bitmap = DocumentRasterizer.RenderLayerBitmap(document, sketch);

        TestAssertions.Equal(2, bitmap.Width);
        TestAssertions.Equal(2, bitmap.Height);
        TestAssertions.Equal(new SKColor(30, 20, 10, 255), bitmap.GetPixel(0, 0));
        TestAssertions.Equal(new SKColor(0, 0, 0, 0), bitmap.GetPixel(1, 1));
    }
}
