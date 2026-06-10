namespace Floss.App.Tests;

public class PixelRegionTests
{
    [Fact]
    public void Intersect_ReturnsOverlap()
        => TestAssertions.Equal(new PixelRegion(5, 6, 5, 2), new PixelRegion(0, 0, 10, 10).Intersect(new PixelRegion(5, 6, 20, 2)));

    [Fact]
    public void Intersect_ReturnsEmptyForTouchingEdges()
        => TestAssertions.Equal(PixelRegion.Empty, new PixelRegion(0, 0, 10, 10).Intersect(new PixelRegion(10, 0, 5, 5)));

    [Fact]
    public void Union_HandlesEmptyRegions()
    {
        var region = new PixelRegion(-2, 3, 5, 7);
        TestAssertions.Equal(region, PixelRegion.Empty.Union(region));
        TestAssertions.Equal(region, region.Union(PixelRegion.Empty));
        TestAssertions.Equal(new PixelRegion(-2, -1, 12, 11), region.Union(new PixelRegion(4, -1, 6, 3)));
    }

    [Fact]
    public void Transforms_WorkTogether()
    {
        var region = new PixelRegion(2, 3, 4, 5).Inflate(2).Translate(-1, 1).ClipTo(6, 8);
        TestAssertions.Equal(new PixelRegion(0, 2, 6, 6), region);
    }
}

