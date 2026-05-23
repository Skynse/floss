namespace Floss.App.Tests;

public class TiledPixelBufferTests
{
    [Fact]
    public void Constructor_ClampsMinimumSize()
    {
        var pixels = new TiledPixelBuffer(0, -20);
        TestAssertions.Equal(1, pixels.Width);
        TestAssertions.Equal(1, pixels.Height);
        TestAssertions.Equal(new PixelRegion(0, 0, 1, 1), pixels.Bounds);
    }

    [Fact]
    public void SetPixel_ReadsAcrossPositiveAndNegativeTiles()
    {
        var pixels = new TiledPixelBuffer(4, 4);
        pixels.SetPixel(-1, -65, 1, 2, 3, 4);
        pixels.SetPixel(64, 64, 5, 6, 7, 8);

        pixels.GetPixel(-1, -65, out var b1, out var g1, out var r1, out var a1);
        pixels.GetPixel(64, 64, out var b2, out var g2, out var r2, out var a2);
        TestAssertions.SequenceEqual(new byte[] { 1, 2, 3, 4 }, [b1, g1, r1, a1]);
        TestAssertions.SequenceEqual(new byte[] { 5, 6, 7, 8 }, [b2, g2, r2, a2]);
        TestAssertions.True(pixels.MinX <= -64 && pixels.MinY <= -128);
        TestAssertions.True(pixels.MaxX >= 128 && pixels.MaxY >= 128);
    }

    [Fact]
    public void CaptureAndRestore_RoundTripPartialRegion()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(2, 2, 10, 20, 30, 40);
        pixels.SetPixel(3, 2, 50, 60, 70, 80);
        var capture = pixels.Capture(new PixelRegion(2, 2, 2, 1));

        pixels.Clear();
        pixels.Restore(new PixelRegion(5, 6, 2, 1), capture);

        pixels.GetPixel(5, 6, out var b1, out _, out _, out var a1);
        pixels.GetPixel(6, 6, out var b2, out _, out _, out var a2);
        TestAssertions.Equal((byte)10, b1);
        TestAssertions.Equal((byte)40, a1);
        TestAssertions.Equal((byte)50, b2);
        TestAssertions.Equal((byte)80, a2);
    }

    [Fact]
    public void Clear_RemovesOnlyRequestedPixels()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(1, 1, 1, 1, 1, 255);
        pixels.SetPixel(4, 4, 2, 2, 2, 255);
        pixels.Clear(new PixelRegion(0, 0, 2, 2));

        pixels.GetPixel(1, 1, out _, out _, out _, out var clearedAlpha);
        pixels.GetPixel(4, 4, out var remainingBlue, out _, out _, out var remainingAlpha);
        TestAssertions.Equal((byte)0, clearedAlpha);
        TestAssertions.Equal((byte)2, remainingBlue);
        TestAssertions.Equal((byte)255, remainingAlpha);
    }

    [Fact]
    public void Clear_RemovesTransparentTiles()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(10, 10, 1, 2, 3, 255);
        TestAssertions.True(pixels.HasContentTiles(new PixelRegion(10, 10, 1, 1)));
        pixels.Clear(new PixelRegion(0, 0, TiledPixelBuffer.TileSize, TiledPixelBuffer.TileSize));
        TestAssertions.False(pixels.HasContentTiles(new PixelRegion(10, 10, 1, 1)));
        TestAssertions.Equal(PixelRegion.Empty, pixels.ContentTileBounds);
    }

    [Fact]
    public void CopyFromBgra_SkipsTransparentPixels()
    {
        var pixels = new TiledPixelBuffer(4, 4);
        var source = new byte[]
        {
            1, 2, 3, 0,
            4, 5, 6, 255,
            7, 8, 9, 128,
            10, 11, 12, 0
        };
        pixels.CopyFromBgra(new PixelRegion(2, 3, 2, 2), source, 8);

        pixels.GetPixel(2, 3, out _, out _, out _, out var transparentAlpha);
        pixels.GetPixel(3, 3, out var b, out var g, out var r, out var a);
        pixels.GetPixel(2, 4, out var b2, out _, out _, out var a2);
        TestAssertions.Equal((byte)0, transparentAlpha);
        TestAssertions.SequenceEqual(new byte[] { 4, 5, 6, 255 }, [b, g, r, a]);
        TestAssertions.Equal((byte)7, b2);
        TestAssertions.Equal((byte)128, a2);
    }

    [Fact]
    public void CopyFromBgra_RegionWithByteStride_DoesNotReadPastBuffer()
    {
        var pixels = new TiledPixelBuffer(128, 128);
        var dirty = new PixelRegion(10, 20, 32, 32);
        var temp = new byte[dirty.Width * dirty.Height * 4];
        var px = (16 * dirty.Width + 16) * 4;
        temp[px + 0] = 11;
        temp[px + 1] = 22;
        temp[px + 2] = 33;
        temp[px + 3] = 255;

        pixels.CopyFromBgra(dirty, temp, dirty.Width * 4);

        pixels.GetPixel(26, 36, out var b, out var g, out var r, out var a);
        TestAssertions.Equal((byte)11, b);
        TestAssertions.Equal((byte)22, g);
        TestAssertions.Equal((byte)33, r);
        TestAssertions.Equal((byte)255, a);
    }

    [Fact]
    public void CaptureTiles_ReturnsDefensiveCopies()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(0, 0, 1, 2, 3, 255);
        var captured = pixels.CaptureTiles();
        captured[(0, 0)][3] = 0;
        pixels.GetPixel(0, 0, out _, out _, out _, out var alpha);
        TestAssertions.Equal((byte)255, alpha);
    }

    [Fact]
    public void ComputeContentBounds_ReturnsNonTransparentExtents()
    {
        var pixels = new TiledPixelBuffer(8, 8);
        pixels.SetPixel(-2, 5, 1, 1, 1, 255);
        pixels.SetPixel(65, 1, 1, 1, 1, 255);
        TestAssertions.Equal(new PixelRegion(-2, 1, 68, 5), pixels.ComputeContentBounds());
    }

    [Fact]
    public void Restore_TruncatedBytesDoesNotThrowOrOverread()
    {
        var pixels = new TiledPixelBuffer(4, 4);
        pixels.Restore(new PixelRegion(0, 0, 2, 2), [9, 8, 7, 255, 6, 5, 4, 255]);
        pixels.GetPixel(0, 0, out var b1, out _, out _, out var a1);
        pixels.GetPixel(1, 0, out var b2, out _, out _, out var a2);
        pixels.GetPixel(0, 1, out _, out _, out _, out var missingAlpha);
        TestAssertions.Equal((byte)9, b1);
        TestAssertions.Equal((byte)255, a1);
        TestAssertions.Equal((byte)6, b2);
        TestAssertions.Equal((byte)255, a2);
        TestAssertions.Equal((byte)0, missingAlpha);
    }

    [Fact]
    public void FillSolid_SharedTemplatesDoNotLeakMutations()
    {
        var pixels = new TiledPixelBuffer(128, 64);
        pixels.FillSolid(new PixelRegion(0, 0, 128, 64), 10, 20, 30, 255);

        pixels.Clear(new PixelRegion(0, 0, 1, 1));

        pixels.GetPixel(0, 0, out _, out _, out _, out var clearedAlpha);
        pixels.GetPixel(64, 0, out var b, out var g, out var r, out var a);
        TestAssertions.Equal((byte)0, clearedAlpha);
        TestAssertions.SequenceEqual(new byte[] { 10, 20, 30, 255 }, [b, g, r, a]);
    }

    [Fact]
    public void ScratchDisk_RoundTripsTilesThroughDisk()
    {
        var originalThreshold = TileSwapManager.MemoryThreshold;
        try
        {
            // Set a very low threshold so any compressed tile gets evicted.
            TileSwapManager.MemoryThreshold = 1;

            var pixels = new TiledPixelBuffer(256, 256);
            pixels.SetPixel(0, 0, 11, 22, 33, 255);
            pixels.SetPixel(128, 128, 44, 55, 66, 255);

            // Compress — this should trigger eviction to disk because threshold is 1 byte.
            pixels.CompressTiles();

            // At this point tiles are either in _compressed or on disk.
            // Force a clear of raw tiles so we must read from disk.
            pixels.CompressTiles();

            // Read back — EnsureRaw should fetch from disk.
            pixels.GetPixel(0, 0, out var b1, out var g1, out var r1, out var a1);
            pixels.GetPixel(128, 128, out var b2, out var g2, out var r2, out var a2);

            TestAssertions.SequenceEqual(new byte[] { 11, 22, 33, 255 }, [b1, g1, r1, a1]);
            TestAssertions.SequenceEqual(new byte[] { 44, 55, 66, 255 }, [b2, g2, r2, a2]);

            pixels.Dispose();
        }
        finally
        {
            TileSwapManager.MemoryThreshold = originalThreshold;
        }
    }
}

