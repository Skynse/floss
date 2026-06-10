using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Tests;

public class SelectionAlphaTextureTests
{
    [Fact]
    public void TryGetAlphaTexture_AfterRectSelection_BuildsCroppedAlphaImage()
    {
        var mask = new SelectionMask();
        mask.Resize(128, 128);
        mask.SetFromRect(20, 30, 40, 50);

        Assert.True(mask.TryGetAlphaTexture(out var image, out var bounds, out var texScale));
        Assert.NotNull(image);
        Assert.Equal(new SKRectI(20, 30, 60, 80), bounds);
        Assert.Equal(1, texScale);
        Assert.True(bounds.Width <= image!.Width);
        Assert.True(bounds.Height <= image.Height);

        mask.Clear();
    }
}
