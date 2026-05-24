using Floss.App.Filters;

namespace Floss.App.Tests;

public class BaseColorMaskEngineTests
{
    [Fact]
    public void GenerateMasks_WithoutModel_ReturnsModelMissing()
    {
        if (BaseColorMaskEngine.ModelFileExists)
            return;

        var bgra = new byte[64 * 64 * 4];
        Array.Fill(bgra, (byte)255);

        var result = BaseColorMaskEngine.GenerateMasks(bgra, 64, 64);

        Assert.Empty(result.Masks);
        Assert.Equal(AnimeSegStatus.ModelMissing, result.AnimeSeg);
    }

    [Fact]
    public void GenerateMasks_WithCachedModel_DetectsCharacterSilhouette()
    {
        if (!BaseColorMaskEngine.ModelFileExists)
            return;

        const int w = 512;
        const int h = 512;
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var p = i * 4;
            bgra[p] = 255;
            bgra[p + 1] = 255;
            bgra[p + 2] = 255;
            bgra[p + 3] = 255;
        }

        for (var y = 100; y < 400; y++)
        {
            for (var x = 150; x < 350; x++)
            {
                var dx = x - 250;
                var dy = y - 250;
                if (dx * dx + dy * dy > 120 * 120) continue;
                var p = (y * w + x) * 4;
                bgra[p] = 30;
                bgra[p + 1] = 30;
                bgra[p + 2] = 30;
            }
        }

        Assert.True(BaseColorMaskEngine.EnsureModelReadyAsync().GetAwaiter().GetResult());

        var result = BaseColorMaskEngine.GenerateMasks(bgra, w, h);

        Assert.NotEqual(AnimeSegStatus.ModelMissing, result.AnimeSeg);
        Assert.NotEqual(AnimeSegStatus.ModelLoadFailed, result.AnimeSeg);
        Assert.NotEqual(AnimeSegStatus.InferenceFailed, result.AnimeSeg);
    }
}
