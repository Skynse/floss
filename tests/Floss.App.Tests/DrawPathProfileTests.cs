using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;

namespace Floss.App.Tests;

/// <summary>Micro-benchmarks for draw-path profiling. Run with: dotnet test --filter DrawPathProfile</summary>
public class DrawPathProfileTests
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
            catch (InvalidOperationException) { }

            _avaloniaInitialized = true;
        }
    }

    [Fact]
    public void Profile_SmallCanvas_StrokeDabs()
    {
        EnsureAvalonia();
        RunStrokeSim(docW: 512, docH: 512, viewportW: 1920, viewportH: 1080, dabCount: 200, label: "512x512");
    }

    [Fact]
    public void Profile_LargeCanvas_StrokeDabs()
    {
        EnsureAvalonia();
        RunStrokeSim(docW: 10000, docH: 8000, viewportW: 1920, viewportH: 1080, dabCount: 100, label: "10kx8k");
    }

    private static void RunStrokeSim(int docW, int docH, int viewportW, int viewportH, int dabCount, string label)
    {
        using var layer = new DrawingLayer("Ink", docW, docH);
        var layers = new List<DrawingLayer> { layer };
        using var compositor = new LayerCompositor();

        var viewport = new PixelRegion(0, 0, Math.Min(viewportW, docW), Math.Min(viewportH, docH));
        compositor.SetSize(docW, docH);
        compositor.BeginStrokeSuspend(viewport, 0);

        // Bootstrap visible tiles
        compositor.Invalidate(viewport, layers, 0, viewportClip: viewport);
        for (var i = 0; i < 64 && compositor.Composite(layers, docW, docH, 0, viewport, 1.0); i++) { }

        var compositeMs = 0.0;
        var sw = Stopwatch.StartNew();
        for (var dab = 0; dab < dabCount; dab++)
        {
            var x = 100 + dab * 3;
            var y = 100 + dab * 2;
            if (x >= docW - 32) break;
            var dirty = new PixelRegion(x - 32, y - 32, 64, 64).ClipTo(docW, docH);
            layer.Pixels.SetPixel(x % docW, y % docH, 0, 0, 255, 255);

            compositor.Invalidate(dirty, layers, 0, viewportClip: viewport);
            var t0 = Stopwatch.GetTimestamp();
            compositor.Composite(layers, docW, docH, 0, viewport, 1.0);
            compositeMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        var tileCols = (viewport.Width + 63) / 64;
        var tileRows = (viewport.Height + 63) / 64;
        var visibleTiles = tileCols * tileRows;
        var cellCols = (docW + 16384 - 1) / 16384;
        var cellRows = (docH + 16384 - 1) / 16384;
        var cellBytes = (long)Math.Min(16384, docW) * Math.Min(16384, docH) * 4;

        // Simulate 60fps DrawTiles for 1 second
        var drawMs = 0.0;
        var frames = 60;
        using var bmp = new RenderTargetBitmap(new PixelSize(viewport.Width, viewport.Height), new Vector(96, 96));
        for (var f = 0; f < frames; f++)
        {
            var t0 = Stopwatch.GetTimestamp();
            using (var dc = bmp.CreateDrawingContext())
                compositor.DrawTiles(dc, new Rect(0, 0, viewport.Width, viewport.Height), viewport);
            drawMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Console.WriteLine($"PROFILE {label}: dabs={dabCount} composite_total={compositeMs:F1}ms composite_per_dab={compositeMs / dabCount:F2}ms");
        Console.WriteLine($"  drawtiles_60frames={drawMs:F1}ms drawtiles_per_frame={drawMs / frames:F2}ms");
        Console.WriteLine($"  visible_tiles_per_draw={visibleTiles} cells={cellCols}x{cellRows} cell_bytes={cellBytes / (1024 * 1024)}MB");
        Console.WriteLine($"  pending={compositor.PendingDirtyTileCount}");
        compositor.EndStrokeSuspend();
    }
}
