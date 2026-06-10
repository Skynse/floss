using Floss.App.Canvas.FloodFill;

namespace Floss.App.Tests;

public class FloodFillEngineTests
{
    [Fact]
    public void ColorDifference_ManhattanMatchesWandMetric()
    {
        int threshold = ColorDifference.Tolerance01ToThreshold(0.1); // 102
        TestAssertions.True(ColorDifference.IsSimilarBgra(100, 100, 100, 255, 100, 100, 100, 255, threshold));
        TestAssertions.True(ColorDifference.IsSimilarBgra(110, 100, 100, 255, 100, 100, 100, 255, threshold));
        TestAssertions.False(ColorDifference.IsSimilarBgra(203, 100, 100, 255, 100, 100, 100, 255, threshold));
    }

    [Fact]
    public void ContiguousFill_OnlyConnectedRegion()
    {
        const int w = 5, h = 3;
        var grid = new byte[w * h];
        // Two separate red regions
        SetPixel(grid, w, 1, 1, 255, 0, 0);
        SetPixel(grid, w, 3, 1, 255, 0, 0);

        var visit = new VisitEpoch();
        visit.BeginPass(w * h);
        var filled = new List<(int x, int y)>();

        bool Similar(int x, int y)
        {
            int idx = y * w + x;
            return grid[idx] == 255;
        }

        FloodFillScanline.FillContiguous(w, h, 1, 1, Similar, visit.Stamp, visit.Epoch,
            (x, y) => filled.Add((x, y)));

        TestAssertions.Equal(1, filled.Count);
        TestAssertions.Equal((1, 1), filled[0]);
    }

    [Fact]
    public void NonContiguousFill_AllMatchingPixels()
    {
        const int w = 5, h = 3;
        var grid = new byte[w * h];
        SetPixel(grid, w, 1, 1, 255, 0, 0);
        SetPixel(grid, w, 3, 1, 255, 0, 0);

        bool Similar(int x, int y) => grid[y * w + x] == 255;
        var filled = new List<(int x, int y)>();
        FloodFillNonContiguous.FillInBounds(0, 0, w - 1, h - 1, Similar, (x, y) => filled.Add((x, y)));

        TestAssertions.Equal(2, filled.Count);
    }

    [Fact]
    public void AreaScaling_PositiveExpandsMask()
    {
        const int w = 5, h = 5;
        var mask = new byte[w * h];
        mask[2 * w + 2] = 255;

        MaskMorphology.ApplyAreaScaling(mask, w, h, 1);

        TestAssertions.Equal((byte)255, mask[2 * w + 2]);
        TestAssertions.Equal((byte)255, mask[2 * w + 1]);
        TestAssertions.Equal((byte)255, mask[2 * w + 3]);
        TestAssertions.Equal((byte)255, mask[1 * w + 2]);
        TestAssertions.Equal((byte)255, mask[3 * w + 2]);
        TestAssertions.Equal((byte)0, mask[0]);
    }

    [Fact]
    public void ToleranceZero_ExactMatchOnly()
    {
        int threshold = ColorDifference.Tolerance01ToThreshold(0);
        TestAssertions.True(ColorDifference.IsSimilarBgra(10, 20, 30, 40, 10, 20, 30, 40, threshold));
        TestAssertions.False(ColorDifference.IsSimilarBgra(11, 20, 30, 40, 10, 20, 30, 40, threshold));
    }

    private static void SetPixel(byte[] grid, int w, int x, int y, byte r, byte g, byte b)
    {
        int idx = y * w + x;
        grid[idx] = r;
    }
}
