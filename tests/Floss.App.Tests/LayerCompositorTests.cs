namespace Floss.App.Tests;

using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;
using Floss.App.Kra;
using SkiaSharp;

public class LayerCompositorTests
{
    private static readonly object AvaloniaGate = new();
    private static bool _avaloniaInitialized;

    private static void EnsureAvalonia()
    {
        lock (AvaloniaGate)
        {
            if (_avaloniaInitialized || Application.Current != null)
            {
                _avaloniaInitialized = true;
                return;
            }

            try
            {
                AppBuilder.Configure<Floss.App.App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
            }
            catch (InvalidOperationException)
            {
                // Another test fixture already initialized Avalonia in this process.
            }

            _avaloniaInitialized = true;
        }
    }

    [Fact]
    public void MonochromeExpression_ThresholdsCoverageBeforePaperComposite()
    {
        using var layer = new DrawingLayer("Ink", 4, 1)
        {
            ExpressionColor = ExpressionColorMode.Monochrome
        };

        layer.Pixels.SetPixel(0, 0, 0, 0, 0, 127);
        layer.Pixels.SetPixel(1, 0, 0, 0, 0, 128);
        layer.Pixels.SetPixel(2, 0, 200, 200, 200, 255);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([layer], 4, 1, 0xFFFFFFFF);

        AssertPixel(pixels, 0, 255, 255, 255, 255);
        AssertPixel(pixels, 1, 0, 0, 0, 255);
        AssertPixel(pixels, 2, 255, 255, 255, 255);
    }

    [Fact]
    public void SampleCompositePixel_UsesFinalCompositorResult()
    {
        using var layer = new DrawingLayer("Multiply red", 1, 1)
        {
            BlendMode = BlendMode.Multiply
        };
        layer.Pixels.SetPixel(0, 0, b: 0, g: 0, r: 255, a: 255);

        using var compositor = new LayerCompositor();
        var sampled = compositor.SampleCompositePixel([layer], 1, 1, 0, 0, paperColor: 0xFF0000FF);

        TestAssertions.True(sampled.HasValue, "Sampling image mode should return the final composited pixel.");
        TestAssertions.Equal((byte)0, sampled!.Value.R);
        TestAssertions.Equal((byte)0, sampled.Value.G);
        TestAssertions.Equal((byte)0, sampled.Value.B);
        TestAssertions.Equal((byte)255, sampled.Value.A);
    }

    [Fact]
    public void Composite_BudgetsDirtyTiles()
    {
        var dirtyTileCount = LayerCompositor.CountTilesForRegion(new PixelRegion(0, 0, 4096, 4096), lod: 0);

        TestAssertions.Equal(32, LayerCompositor.DirtyTileBudget);
        TestAssertions.True(dirtyTileCount > LayerCompositor.DirtyTileBudget);
    }

    [Fact]
    public void Composite_DoesNotPublishPartiallyDirtyDisplayFrame()
    {
        EnsureAvalonia();
        const int size = 2048;
        using var layer = new DrawingLayer("Ink", size, size);
        layer.Pixels.SetPixel(0, 0, b: 0, g: 0, r: 255, a: 255);

        using var compositor = new LayerCompositor();
        compositor.SetSize(size, size);
        compositor.Invalidate(null);
        compositor.Composite([layer], size, size, paperColor: 0, viewport: null, zoom: 1.0);

        TestAssertions.True(compositor.TryReadDisplayPixel(0, 0, out var oldB, out var oldG, out var oldR, out var oldA));
        TestAssertions.SequenceEqual(new[] { (byte)0, (byte)0, (byte)255, (byte)255 }, [oldB, oldG, oldR, oldA]);

        layer.Pixels.SetPixel(0, 0, b: 255, g: 0, r: 0, a: 255);
        compositor.Invalidate(new PixelRegion(0, 0, size, size));
        var deferred = compositor.Composite([layer], size, size, paperColor: 0, viewport: null, zoom: 1.0);

        TestAssertions.True(deferred, "The first partial dirty pass should keep work queued.");
        TestAssertions.True(compositor.TryReadDisplayPixel(0, 0, out var b, out var g, out var r, out var a));
        TestAssertions.SequenceEqual(new[] { (byte)0, (byte)0, (byte)255, (byte)255 }, [b, g, r, a]);

        for (var i = 0; i < 40 && compositor.Composite([layer], size, size, paperColor: 0, viewport: null, zoom: 1.0); i++)
        {
        }

        TestAssertions.True(compositor.TryReadDisplayPixel(0, 0, out b, out g, out r, out a));
        TestAssertions.SequenceEqual(new[] { (byte)255, (byte)0, (byte)0, (byte)255 }, [b, g, r, a]);
    }

    [Fact]
    public void SelectLod_AlwaysZero_NoLodSystem()
    {
        using var compositor = new LayerCompositor();
        TestAssertions.Equal(0, compositor.SelectLod(1000, 1000, 0.4));
        TestAssertions.Equal(0, compositor.SelectLod(1000, 1000, 0.2));
        TestAssertions.Equal(0, compositor.SelectLod(1000, 1000, 1.0));
    }

    [Fact]
    public void SelectLod_AlwaysZero_ForAnyCanvas()
    {
        using var compositor = new LayerCompositor();
        TestAssertions.Equal(0, compositor.SelectLod(6000, 4080, 0.1));
        TestAssertions.Equal(0, compositor.SelectLod(6000, 4080, 0.3));
        TestAssertions.Equal(0, compositor.SelectLod(6000, 4080, 1.0));
    }

    [Fact]
    public void Composite_InvalidatesActiveLodOnPartialDirty()
    {
        EnsureAvalonia();
        using var compositor = new LayerCompositor();
        compositor.SetSize(1024, 1024);
        var region = new PixelRegion(512, 512, 128, 128);
        compositor.Composite([], 1024, 1024, viewport: new PixelRegion(0, 0, 1024, 1024), zoom: 1.0);
        TestAssertions.Equal(0, compositor.LastLod);
        compositor.Invalidate(region);
        var expected = LayerCompositor.CountTilesForRegion(region, lod: 0);
        TestAssertions.True(expected > 0);
        TestAssertions.Equal(expected, compositor.PendingDirtyTileCount);
    }

    [Fact]
    public void Composite_UsesZoomLodDuringStrokeSuspend()
    {
        EnsureAvalonia();
        var background = new DrawingLayer("Background", 64, 64);
        background.Pixels.SetPixel(5, 5, 255, 0, 0, 255);

        using var compositor = new LayerCompositor();
        compositor.BeginStrokeSuspend(new PixelRegion(0, 0, 64, 64));
        compositor.Composite([background], 64, 64, viewport: new PixelRegion(0, 0, 64, 64), zoom: 1.0);
        TestAssertions.Equal(0, compositor.LastLod, "Compositor uses LOD 0 during stroke suspend.");
        compositor.EndStrokeSuspend();
    }

    [Fact]
    public void ClipCompositeStartIndex_FindsClipBase()
    {
        var baseLayer = new DrawingLayer("Base", 8, 8);
        var clipA = new DrawingLayer("ClipA", 8, 8) { IsClipping = true };
        var clipB = new DrawingLayer("ClipB", 8, 8) { IsClipping = true };
        var siblings = new List<DrawingLayer> { baseLayer, clipA, clipB };

        TestAssertions.Equal(0, LayerProjectionPlane.ClipCompositeStartIndex(siblings, 0));
        TestAssertions.Equal(0, LayerProjectionPlane.ClipCompositeStartIndex(siblings, 1));
        TestAssertions.Equal(0, LayerProjectionPlane.ClipCompositeStartIndex(siblings, 2));
    }

    [Fact]
    public void StrokeSuspend_OverlayOnMultiply_MatchesFullComposite()
    {
        EnsureAvalonia();
        const int size = 128;
        var multiply = new DrawingLayer("Multiply", size, size) { BlendMode = BlendMode.Multiply };
        multiply.Pixels.SetPixel(32, 32, 0, 0, 255, 255);

        var overlay = new DrawingLayer("Overlay", size, size) { BlendMode = BlendMode.Overlay };
        overlay.Pixels.SetPixel(40, 40, 0, 200, 200, 200);

        var layers = new List<DrawingLayer> { multiply, overlay };
        using var compositor = new LayerCompositor();
        compositor.SetSize(size, size);

        var expected = compositor.SampleCompositePixel(layers, size, size, 40, 40, paperColor: 0xFFFFFFFF);
        TestAssertions.True(expected.HasValue, "Full composite should hit the overlay pixel.");

        compositor.BeginStrokeSuspend(new PixelRegion(0, 0, size, size), layerIndex: 1);
        compositor.Invalidate(new PixelRegion(32, 32, 16, 16));
        compositor.Composite(layers, size, size, paperColor: 0xFFFFFFFF,
            viewport: new PixelRegion(0, 0, size, size), zoom: 1.0);

        TestAssertions.True(compositor.TryReadDisplayPixel(40, 40, out var b, out var g, out var r, out var a));
        TestAssertions.Equal(expected!.Value.R, r, "Overlay stroke preview must match full composite red channel.");
        TestAssertions.Equal(expected.Value.G, g, "Overlay stroke preview must match full composite green channel.");
        TestAssertions.Equal(expected.Value.B, b, "Overlay stroke preview must match full composite blue channel.");
        TestAssertions.Equal(expected.Value.A, a, "Overlay stroke preview must match full composite alpha.");

        compositor.EndStrokeSuspend();
    }

    [Fact]
    public void TryCreateStrokeSplitPlan_RejectsIsolatedGroupPaintLayer()
    {
        const int size = 64;
        var background = new DrawingLayer("Background", size, size);
        var group = new DrawingLayer("Folder", size, size) { IsGroup = true };
        var paint = new DrawingLayer("Paint", size, size);
        paint.Parent = group;
        group.Children.Add(paint);

        var layers = new List<DrawingLayer> { background, group, paint };
        var rootStack = LayerProjectionPlane.BuildSiblingStack(LayerStackComposition.SelectLayersForComposite(layers));
        var method = typeof(LayerCompositor).GetMethod("TryCreateStrokeSplitPlan", BindingFlags.NonPublic | BindingFlags.Static);
        TestAssertions.True(method != null);

        var args = new object?[] { layers, 2, rootStack, null };
        var accepted = (bool)method!.Invoke(null, args)!;
        TestAssertions.False(accepted, "Stroke split must fall back to full composite for isolated groups.");
    }

    [Fact]
    public void StrokeSuspend_OverlayOnMultiply_InNormalGroup_MatchesFullComposite()
    {
        EnsureAvalonia();
        const int size = 128;
        var background = new DrawingLayer("Background", size, size);
        background.Pixels.SetPixel(40, 40, 255, 200, 100, 255);

        var group = new DrawingLayer("Folder", size, size) { IsGroup = true };

        var multiply = new DrawingLayer("Multiply", size, size) { BlendMode = BlendMode.Multiply };
        multiply.Pixels.SetPixel(40, 40, 0, 0, 255, 255);
        multiply.Parent = group;

        var overlay = new DrawingLayer("Overlay", size, size) { BlendMode = BlendMode.Overlay };
        overlay.Parent = group;

        group.Children.Add(multiply);
        group.Children.Add(overlay);

        var layers = new List<DrawingLayer> { background, group, multiply, overlay };
        using var compositor = new LayerCompositor();
        compositor.SetSize(size, size);

        compositor.BeginStrokeSuspend(new PixelRegion(0, 0, size, size), layerIndex: 3);
        overlay.Pixels.SetPixel(40, 40, 0, 200, 200, 200);
        compositor.Invalidate(new PixelRegion(32, 32, 24, 24));
        compositor.Composite(layers, size, size, paperColor: 0xFFFFFFFF,
            viewport: new PixelRegion(0, 0, size, size), zoom: 1.0);

        var expected = compositor.SampleCompositePixel(layers, size, size, 40, 40, paperColor: 0xFFFFFFFF);
        TestAssertions.True(expected.HasValue, "Full composite should hit the overlay pixel.");
        TestAssertions.True(compositor.TryReadDisplayPixel(40, 40, out var b, out var g, out var r, out var a));
        TestAssertions.Equal(expected!.Value.R, r, "Grouped overlay stroke preview must match full composite red channel.");
        TestAssertions.Equal(expected.Value.G, g, "Overlay stroke preview must match full composite green channel.");
        TestAssertions.Equal(expected.Value.B, b, "Overlay stroke preview must match full composite blue channel.");
        TestAssertions.Equal(expected.Value.A, a, "Overlay stroke preview must match full composite alpha.");

        compositor.EndStrokeSuspend();
    }

    [Fact]
    public void StrokeSuspend_LiveOverlayDab_MatchesFullComposite()
    {
        EnsureAvalonia();
        const int size = 128;
        var multiply = new DrawingLayer("Multiply", size, size) { BlendMode = BlendMode.Multiply };
        multiply.Pixels.SetPixel(32, 32, 0, 0, 255, 255);

        var overlay = new DrawingLayer("Overlay", size, size) { BlendMode = BlendMode.Overlay };

        var layers = new List<DrawingLayer> { multiply, overlay };
        using var compositor = new LayerCompositor();
        compositor.SetSize(size, size);

        compositor.BeginStrokeSuspend(new PixelRegion(0, 0, size, size), layerIndex: 1);
        overlay.Pixels.SetPixel(40, 40, 0, 200, 200, 200);
        compositor.Invalidate(new PixelRegion(32, 32, 16, 16));
        compositor.Composite(layers, size, size, paperColor: 0xFFFFFFFF,
            viewport: new PixelRegion(0, 0, size, size), zoom: 1.0);

        var expected = compositor.SampleCompositePixel(layers, size, size, 40, 40, paperColor: 0xFFFFFFFF);
        TestAssertions.True(expected.HasValue);
        TestAssertions.True(compositor.TryReadDisplayPixel(40, 40, out var b, out var g, out var r, out var a));
        TestAssertions.Equal(expected!.Value.R, r);
        TestAssertions.Equal(expected.Value.G, g);
        TestAssertions.Equal(expected.Value.B, b);
        TestAssertions.Equal(expected.Value.A, a);

        compositor.EndStrokeSuspend();
    }

    [Fact]
    public void StrokeSuspend_LargeCanvasOverlayDab_MatchesFullComposite()
    {
        EnsureAvalonia();
        const int size = 3072;
        var multiply = new DrawingLayer("Multiply", size, size) { BlendMode = BlendMode.Multiply };
        multiply.Pixels.SetPixel(512, 512, 0, 0, 255, 255);

        var overlay = new DrawingLayer("Overlay", size, size) { BlendMode = BlendMode.Overlay };

        var layers = new List<DrawingLayer> { multiply, overlay };
        using var compositor = new LayerCompositor();
        compositor.SetSize(size, size);

        compositor.BeginStrokeSuspend(new PixelRegion(0, 0, size, size), layerIndex: 1);
        overlay.Pixels.SetPixel(520, 520, 0, 200, 200, 200);
        compositor.Invalidate(new PixelRegion(480, 480, 96, 96));
        compositor.Composite(layers, size, size, paperColor: 0xFFFFFFFF,
            viewport: new PixelRegion(480, 480, 96, 96), zoom: 1.0);

        var expected = compositor.SampleCompositePixel(layers, size, size, 520, 520, paperColor: 0xFFFFFFFF);
        TestAssertions.True(expected.HasValue);
        TestAssertions.True(compositor.TryReadDisplayPixel(520, 520, out var b, out var g, out var r, out var a));
        TestAssertions.Equal(expected!.Value.R, r);
        TestAssertions.Equal(expected.Value.G, g);
        TestAssertions.Equal(expected.Value.B, b);
        TestAssertions.Equal(expected.Value.A, a);

        compositor.EndStrokeSuspend();
    }

    [Fact]
    public void StrokeSuspend_OnClippedLayer_MatchesFullCompositeOutsideClipBase()
    {
        EnsureAvalonia();
        const int size = 64;
        var baseLayer = new DrawingLayer("Base", size, size);
        baseLayer.Pixels.SetPixel(32, 32, 0, 0, 0, 255);

        var clipLayer = new DrawingLayer("Clip", size, size) { IsClipping = true };
        clipLayer.Pixels.SetPixel(8, 8, 0, 255, 0, 255);

        var layers = new List<DrawingLayer> { baseLayer, clipLayer };
        using var compositor = new LayerCompositor();
        compositor.SetSize(size, size);

        void ReadAt(int x, int y, out byte b, out byte g, out byte r, out byte a)
        {
            if (!compositor.TryReadDisplayPixel(x, y, out b, out g, out r, out a))
                b = g = r = a = 0;
        }

        compositor.Composite(layers, size, size, paperColor: 0xFFFFFFFF);
        ReadAt(8, 8, out _, out var refG, out _, out var refA);

        compositor.BeginStrokeSuspend(new PixelRegion(0, 0, size, size), layerIndex: 1);
        compositor.Invalidate(new PixelRegion(0, 0, size, size));
        compositor.Composite(layers, size, size, paperColor: 0xFFFFFFFF,
            viewport: new PixelRegion(0, 0, size, size), zoom: 1.0);
        ReadAt(8, 8, out _, out var liveG, out _, out var liveA);

        TestAssertions.Equal(refG, liveG, "Live stroke must match full composite green channel outside clip base.");
        TestAssertions.Equal(refA, liveA, "Live stroke must match full composite alpha outside clip base.");

        compositor.EndStrokeSuspend();
    }

    [Fact]
    public void Composite_LodKeepsSubpixelInkCoverage()
    {
        EnsureAvalonia();
        using var ink = new DrawingLayer("Ink", 64, 64);
        ink.Pixels.SetPixel(0, 0, b: 0, g: 0, r: 0, a: 255);

        using var compositor = new LayerCompositor();
        compositor.Composite([ink], 64, 64, paperColor: 0,
            viewport: new PixelRegion(0, 0, 64, 64), zoom: 1.0);

        using var bitmap = compositor.AssembleSkBitmap(64, 64, lod: 0);
        var pixel = bitmap.GetPixel(0, 0);

        TestAssertions.True(pixel.Alpha == 255, "At native resolution, a single pixel should be fully opaque.");
    }

    [Fact]
    public void SampleCompositePixel_MatchesCompositeToBgra_ForRealCharacterLayerOnly()
    {
        var path = TestPaths.KraTestFile;
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        var document = KraImporter.Load(stream);
        var character = FindLayerByName(document.Layers, "Character | 人物");
        TestAssertions.True(character != null);

        const int x = 2816;
        const int y = 0;
        character!.Pixels.GetPixel(x, y, out _, out _, out _, out var srcA);
        TestAssertions.True(srcA > 0, "Expected character source alpha at test pixel.");
        TestAssertions.True(character.MaxX > x, $"Character MaxX={character.MaxX} must include x={x}");
        character.Pixels.EnterPixelReadLock();
        try
        {
            var tile = character.Pixels.GetTileOrNull(x / 64, y / 64);
            TestAssertions.True(tile != null, "Expected raw tile for character pixel.");
            TestAssertions.Equal(srcA, tile![(y % 64) * 64 * 4 + (x % 64) * 4 + 3]);
        }
        finally
        {
            character.Pixels.ExitPixelReadLock();
        }

        var sampledOnly = new LayerCompositor().SampleCompositePixel([character], document.Width, document.Height, x, y, 0);
        TestAssertions.True(sampledOnly.HasValue, "SampleCompositePixel alone should hit the character pixel.");

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([character], document.Width, document.Height, 0);
        var offset = (y * document.Width + x) * 4;
        TestAssertions.True(pixels[offset + 3] > 0, "CompositeToBgra should produce character pixel.");
        var sampled = compositor.SampleCompositePixel([character], document.Width, document.Height, x, y, 0);

        TestAssertions.True(sampled.HasValue, "SampleCompositePixel should hit the character pixel.");
        TestAssertions.Equal(pixels[offset + 2], sampled!.Value.R);
        TestAssertions.Equal(pixels[offset + 1], sampled.Value.G);
        TestAssertions.Equal(pixels[offset], sampled.Value.B);
        TestAssertions.Equal(pixels[offset + 3], sampled.Value.A);
    }

    [Fact]
    public void SampleCompositePixel_MatchesCompositeToBgra_OnLargeCanvasWithLowAlphaTopGroup()
    {
        const int width = 10000;
        const int height = 5000;
        const int x = 2816;
        const int y = 0;

        var background = new DrawingLayer("Background", width, height);
        background.Pixels.SetPixel(x, y, 0, 0, 255, 255);

        var layers = new List<DrawingLayer> { background };
        for (var g = 0; g < 4; g++)
        {
            var group = new DrawingLayer($"Group{g}", width, height) { IsGroup = true };
            var child = new DrawingLayer($"Ink{g}", width, height);
            var alpha = (byte)(g == 3 ? 32 : 255);
            child.Pixels.SetPixel(x, y, (byte)(10 + g), (byte)(20 + g), (byte)(30 + g), alpha);
            child.Parent = group;
            group.Children.Add(child);
            layers.Add(group);
        }

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra(layers, width, height, 0);
        var sampled = compositor.SampleCompositePixel(layers, width, height, x, y, 0);

        TestAssertions.True(sampled.HasValue);
        var offset = (y * width + x) * 4;
        TestAssertions.Equal(pixels[offset + 2], sampled!.Value.R);
        TestAssertions.Equal(pixels[offset + 1], sampled.Value.G);
        TestAssertions.Equal(pixels[offset], sampled.Value.B);
        TestAssertions.Equal(pixels[offset + 3], sampled.Value.A);
    }

    [Fact]
    public void SampleCompositePixel_MatchesCompositeToBgra_ForDeepStackedNormalGroupsOffOrigin()
    {
        var background = new DrawingLayer("Background", 512, 512);
        background.Pixels.SetPixel(300, 300, 0, 0, 255, 255);

        var layers = new List<DrawingLayer> { background };
        for (var g = 0; g < 4; g++)
        {
            var group = new DrawingLayer($"Group{g}", 512, 512) { IsGroup = true };
            for (var c = 0; c < 7; c++)
            {
                var child = new DrawingLayer($"Ink{g}_{c}", 512, 512);
                var alpha = (byte)(c == 6 ? 32 : (255 - c * 20));
                child.Pixels.SetPixel(300, 300, (byte)(10 + g), (byte)(20 + g), (byte)(30 + g), alpha);
                child.Parent = group;
                group.Children.Add(child);
            }

            layers.Add(group);
        }

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra(layers, 512, 512, 0);
        var sampled = compositor.SampleCompositePixel(layers, 512, 512, 300, 300, 0);

        TestAssertions.True(sampled.HasValue);
        var offset = (300 * 512 + 300) * 4;
        TestAssertions.Equal(pixels[offset + 2], sampled!.Value.R);
        TestAssertions.Equal(pixels[offset + 1], sampled.Value.G);
        TestAssertions.Equal(pixels[offset], sampled.Value.B);
        TestAssertions.Equal(pixels[offset + 3], sampled.Value.A);
    }

    [Fact]
    public void CompositeToBgra_RendersKikiGroupCharacterPixel()
    {
        var path = TestPaths.KraTestFile;
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        var document = KraImporter.Load(stream);
        var kiki = FindLayerByName(document.Layers, "Kiki | 琪琪");
        TestAssertions.True(kiki != null && kiki.IsGroup);

        var sample = FindFirstInBoundsOpaquePixel(kiki!, document);
        TestAssertions.True(sample.HasValue, "Expected opaque pixel inside Kiki group.");
        var (x, y) = sample.Value;

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([kiki], document.Width, document.Height, 0);
        var offset = (y * document.Width + x) * 4;
        TestAssertions.True(pixels[offset + 3] > 0, "Kiki group should produce visible pixels.");
    }

    [Fact]
    public void Composite_RealGroupedKra_TiledPathMatchesCompositeToBgra_AtLowAlphaCharacterPixel()
    {
        EnsureAvalonia();
        var path = TestPaths.KraTestFile;
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        var document = KraImporter.Load(stream);
        var character = FindLayerByName(document.Layers, "Character | 人物");
        TestAssertions.True(character != null);

        const int x = 2816;
        const int y = 0;

        using var expectedCompositor = new LayerCompositor();
        var expected = expectedCompositor.CompositeToBgra(document.Layers, document.Width, document.Height, 0);
        var offset = (y * document.Width + x) * 4;

        using var compositor = new LayerCompositor();
        compositor.SetSize(document.Width, document.Height);
        compositor.Invalidate(null);
        var viewport = new PixelRegion(Math.Max(0, x - 256), Math.Max(0, y - 256), 512, 512);
        for (var pass = 0; pass < 32; pass++)
        {
            if (!compositor.Composite(document.Layers, document.Width, document.Height, 0, viewport, zoom: 1.0))
                break;
            if (compositor.PendingDirtyTileCount == 0)
                break;
        }

        var sampled = compositor.SampleCompositePixel(document.Layers, document.Width, document.Height, x, y, 0);
        TestAssertions.True(sampled.HasValue, "Grouped KRA pixel should composite.");
        TestAssertions.Equal(expected[offset + 2], sampled!.Value.R);
        TestAssertions.Equal(expected[offset + 1], sampled.Value.G);
        TestAssertions.Equal(expected[offset], sampled.Value.B);
        TestAssertions.Equal(expected[offset + 3], sampled.Value.A);
    }

    private static DrawingLayer? FindLayerByName(IReadOnlyList<DrawingLayer> layers, string name)
    {
        foreach (var layer in layers)
        {
            if (layer.Name == name)
                return layer;

            if (layer.IsGroup)
            {
                var nested = FindLayerByName(layer.Children, name);
                if (nested != null) return nested;
            }
        }

        return null;
    }

    private static (int X, int Y)? FindFirstInBoundsOpaquePixel(DrawingDocument document)
    {
        foreach (var root in document.Layers)
        {
            var hit = FindFirstInBoundsOpaquePixel(root, document);
            if (hit != null) return hit;
        }

        return null;
    }

    private static (int X, int Y)? FindFirstInBoundsOpaquePixel(DrawingLayer layer, DrawingDocument document)
    {
        if (layer.IsGroup)
        {
            foreach (var child in layer.Children)
            {
                var hit = FindFirstInBoundsOpaquePixel(child, document);
                if (hit != null) return hit;
            }

            return null;
        }

        if (!layer.IsVisible || layer.Opacity <= 0) return null;
        for (var y = 0; y < document.Height; y += 32)
        for (var x = 0; x < document.Width; x += 32)
        {
            layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY, out _, out _, out _, out var a);
            if (a > 64)
                return (x, y);
        }

        return null;
    }

    [Fact]
    public void TiledDisplay_MatchesProjectionMerge_ForStackedNormalGroups()
    {
        EnsureAvalonia();
        var background = new DrawingLayer("Background", 512, 512);
        background.Pixels.SetPixel(300, 300, 0, 0, 255, 255);

        var layers = new List<DrawingLayer> { background };
        for (var i = 0; i < 4; i++)
        {
            var child = new DrawingLayer($"Ink{i}", 512, 512);
            child.Pixels.SetPixel(300, 300, (byte)(10 * (i + 1)), (byte)(20 * (i + 1)), (byte)(30 * (i + 1)), (byte)(64 * (i + 1)));
            var group = new DrawingLayer($"Group{i}", 512, 512) { IsGroup = true };
            child.Parent = group;
            group.Children.Add(child);
            layers.Add(group);
        }

        const int x = 300;
        const int y = 300;
        using var compositor = new LayerCompositor();
        var expected = compositor.SampleCompositePixel(layers, 512, 512, x, y, 0);
        TestAssertions.True(expected.HasValue, "Projection merge should hit the stacked group pixel.");

        compositor.SetSize(512, 512);
        compositor.Invalidate(null);
        compositor.Composite(layers, 512, 512, paperColor: 0, viewport: new PixelRegion(256, 256, 256, 256), zoom: 1.0);

        TestAssertions.True(compositor.TryReadDisplayPixel(x, y, out var b, out var g, out var r, out var a));
        TestAssertions.Equal(expected!.Value.R, r);
        TestAssertions.Equal(expected.Value.G, g);
        TestAssertions.Equal(expected.Value.B, b);
        TestAssertions.Equal(expected.Value.A, a);
    }

    [Fact]
    public void TiledDisplay_MatchesProjectionMerge_ForSingleNormalGroup()
    {
        EnsureAvalonia();
        var background = new DrawingLayer("Background", 512, 512);
        background.Pixels.SetPixel(300, 300, 0, 0, 255, 255);

        var child = new DrawingLayer("Ink", 512, 512);
        child.Pixels.SetPixel(300, 300, 10, 20, 30, 255);

        var group = new DrawingLayer("Group", 512, 512) { IsGroup = true };
        child.Parent = group;
        group.Children.Add(child);

        var layers = new[] { background, group };
        const int x = 300;
        const int y = 300;

        using var compositor = new LayerCompositor();
        var expected = compositor.SampleCompositePixel(layers, 512, 512, x, y, 0);
        TestAssertions.True(expected.HasValue, "Projection merge should hit the group ink pixel.");

        compositor.SetSize(512, 512);
        compositor.Invalidate(null);
        compositor.Composite(layers, 512, 512, paperColor: 0, viewport: new PixelRegion(256, 256, 256, 256), zoom: 1.0);

        TestAssertions.True(compositor.TryReadDisplayPixel(x, y, out var b, out var g, out var r, out var a));
        TestAssertions.Equal(expected!.Value.R, r);
        TestAssertions.Equal(expected.Value.G, g);
        TestAssertions.Equal(expected.Value.B, b);
        TestAssertions.Equal(expected.Value.A, a);
    }

    [Fact]
    public void SampleCompositePixel_MatchesCompositeToBgra_ForStackedNormalGroups()
    {
        var background = new DrawingLayer("Background", 64, 64);
        background.Pixels.SetPixel(20, 20, 0, 0, 255, 255);

        var groups = new List<DrawingLayer> { background };
        for (var i = 0; i < 4; i++)
        {
            var child = new DrawingLayer($"Ink{i}", 64, 64);
            child.Pixels.SetPixel(20, 20, (byte)(10 * (i + 1)), (byte)(20 * (i + 1)), (byte)(30 * (i + 1)), (byte)(64 * (i + 1)));
            var group = new DrawingLayer($"Group{i}", 64, 64) { IsGroup = true };
            child.Parent = group;
            group.Children.Add(child);
            groups.Add(group);
        }

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra(groups, 64, 64, 0);
        var sampled = compositor.SampleCompositePixel(groups, 64, 64, 20, 20, 0);

        TestAssertions.True(sampled.HasValue);
        var offset = (20 * 64 + 20) * 4;
        TestAssertions.Equal(pixels[offset + 2], sampled!.Value.R);
        TestAssertions.Equal(pixels[offset + 1], sampled.Value.G);
        TestAssertions.Equal(pixels[offset], sampled.Value.B);
        TestAssertions.Equal(pixels[offset + 3], sampled.Value.A);
    }

    [Fact]
    public void SampleCompositePixel_MatchesCompositeToBgra_ForNormalGroup()
    {
        var background = new DrawingLayer("Background", 64, 64);
        background.Pixels.SetPixel(10, 10, 0, 0, 0, 255);

        var child = new DrawingLayer("Ink", 64, 64);
        child.Pixels.SetPixel(20, 20, 10, 20, 30, 255);

        var group = new DrawingLayer("Group", 64, 64) { IsGroup = true };
        child.Parent = group;
        group.Children.Add(child);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([background, group], 64, 64, 0);
        var sampled = compositor.SampleCompositePixel([background, group], 64, 64, 20, 20, 0);

        TestAssertions.True(sampled.HasValue);
        var offset = (20 * 64 + 20) * 4;
        TestAssertions.Equal(pixels[offset + 2], sampled!.Value.R);
        TestAssertions.Equal(pixels[offset + 1], sampled.Value.G);
        TestAssertions.Equal(pixels[offset], sampled.Value.B);
        TestAssertions.Equal(pixels[offset + 3], sampled.Value.A);
    }

    [Fact]
    public void Composite_RendersNormalGroupChildren()
    {
        var background = new DrawingLayer("Background", 64, 64);
        background.Pixels.SetPixel(10, 10, 0, 0, 0, 255);

        var child = new DrawingLayer("Ink", 64, 64);
        child.Pixels.SetPixel(20, 20, 10, 20, 30, 255);

        var group = new DrawingLayer("Group", 64, 64) { IsGroup = true };
        child.Parent = group;
        group.Children.Add(child);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([background, group], 64, 64, 0);

        var offset = (20 * 64 + 20) * 4;
        TestAssertions.Equal((byte)10, pixels[offset]);
        TestAssertions.Equal((byte)20, pixels[offset + 1]);
        TestAssertions.Equal((byte)30, pixels[offset + 2]);
        TestAssertions.Equal((byte)255, pixels[offset + 3]);
    }

    [Fact]
    public void Composite_RendersNormalGroupAtLowLod()
    {
        EnsureAvalonia();
        var background = new DrawingLayer("Background", 64, 64);
        background.Pixels.SetPixel(5, 5, 1, 2, 3, 255);

        var child = new DrawingLayer("Ink", 64, 64);
        child.Pixels.SetPixel(30, 30, 40, 50, 60, 200);

        var group = new DrawingLayer("Group", 64, 64) { IsGroup = true };
        child.Parent = group;
        group.Children.Add(child);

        using var compositor = new LayerCompositor();
        compositor.Composite([background, group], 64, 64, paperColor: 0, viewport: new PixelRegion(0, 0, 64, 64), zoom: 1.0);

        var sampled = compositor.SampleCompositePixel([background, group], 64, 64, 30, 30, paperColor: 0);
        TestAssertions.True(sampled.HasValue);
        TestAssertions.True(sampled!.Value.A > 20, "Grouped layers should remain visible when composited.");
    }

    [Fact]
    public void Composite_RendersImportedGroupHierarchy()
    {
        using var stream = new MemoryStream(KraImporterTests.BuildGroupedKraPublic());
        var document = KraImporter.Load(stream);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra(document.Layers, document.Width, document.Height, 0);

        var offset = 0;
        TestAssertions.Equal((byte)1, pixels[offset]);
        TestAssertions.Equal((byte)2, pixels[offset + 1]);
        TestAssertions.Equal((byte)3, pixels[offset + 2]);
        TestAssertions.Equal((byte)255, pixels[offset + 3]);
    }

    [Fact]
    public void Composite_ClippingLayerWithMask_AppliesMask()
    {
        var baseLayer = new DrawingLayer("Base", 4, 1);
        baseLayer.Pixels.SetPixel(0, 0, 100, 100, 100, 255);
        baseLayer.Pixels.SetPixel(1, 0, 100, 100, 100, 255);
        baseLayer.Pixels.SetPixel(2, 0, 100, 100, 100, 255);
        baseLayer.Pixels.SetPixel(3, 0, 100, 100, 100, 255);

        var clipLayer = new DrawingLayer("Clip", 4, 1) { IsClipping = true };
        clipLayer.Pixels.SetPixel(0, 0, 255, 0, 0, 255);
        clipLayer.Pixels.SetPixel(1, 0, 255, 0, 0, 255);
        clipLayer.Pixels.SetPixel(2, 0, 255, 0, 0, 255);
        clipLayer.Pixels.SetPixel(3, 0, 255, 0, 0, 255);
        clipLayer.CreateMask();
        clipLayer.MaskPixels!.SetPixel(0, 0, 0, 0, 0, 255);
        clipLayer.MaskPixels.SetPixel(1, 0, 0, 0, 0, 128);
        clipLayer.MaskPixels.SetPixel(2, 0, 0, 0, 0, 0);
        clipLayer.MaskPixels.SetPixel(3, 0, 0, 0, 0, 255);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([baseLayer, clipLayer], 4, 1, 0);

        AssertPixel(pixels, 0, 255, 0, 0, 255);
        AssertPixel(pixels, 1, 178, 50, 50, 255);
        AssertPixel(pixels, 2, 100, 100, 100, 255);
        AssertPixel(pixels, 3, 255, 0, 0, 255);
    }

    [Fact]
    public void Composite_ClippingLayerWithDisabledMask_IgnoresMask()
    {
        var baseLayer = new DrawingLayer("Base", 2, 1);
        baseLayer.Pixels.SetPixel(0, 0, 100, 100, 100, 255);
        baseLayer.Pixels.SetPixel(1, 0, 100, 100, 100, 255);

        var clipLayer = new DrawingLayer("Clip", 2, 1) { IsClipping = true };
        clipLayer.Pixels.SetPixel(0, 0, 255, 0, 0, 255);
        clipLayer.Pixels.SetPixel(1, 0, 255, 0, 0, 255);
        clipLayer.CreateMask();
        clipLayer.MaskPixels!.SetPixel(0, 0, 0, 0, 0, 0);
        clipLayer.MaskPixels.SetPixel(1, 0, 0, 0, 0, 0);
        clipLayer.IsMaskVisible = false;

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([baseLayer, clipLayer], 2, 1, 0);

        AssertPixel(pixels, 0, 255, 0, 0, 255);
        AssertPixel(pixels, 1, 255, 0, 0, 255);
    }

    private static void AssertPixel(byte[] pixels, int x, byte b, byte g, byte r, byte a)
    {
        var offset = x * 4;
        TestAssertions.SequenceEqual(new[] { b, g, r, a },
            new[] { pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3] });
    }
}
