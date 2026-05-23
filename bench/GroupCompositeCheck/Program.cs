using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Kra;

if (Application.Current == null)
    AppBuilder.Configure<Floss.App.App>()
        .UseSkia()
        .UsePlatformDetect()
        .SetupWithoutStarting();

Console.WriteLine("=== Synthetic group tile test ===");
RunSyntheticGroupTileTest();
Console.WriteLine();

if (Application.Current == null)
    throw new InvalidOperationException("Avalonia failed to initialize.");

const string path = "/home/neckles/Downloads/electrichearts_20250824A_kiki.kra";
if (!File.Exists(path))
{
    Console.WriteLine("KRA file not found, skipping.");
    return;
}

using var stream = File.OpenRead(path);
var document = KraImporter.Load(stream);
Console.WriteLine($"Loaded {document.Width}x{document.Height}, root layers={document.Layers.Count(l => l.Parent == null)}");

var sample = FindInBoundsSample(document, "Character | 人物") ?? FindInBoundsSample(document);
if (sample is null)
{
    Console.WriteLine("No sample pixel found.");
    return;
}

var (layer, docX, docY, b, g, r, a) = sample.Value;
Console.WriteLine($"Sample from '{layer.Name}' at ({docX},{docY}) rgba=({r},{g},{b},{a})");

Console.WriteLine("Root stack:");
foreach (var rootLayer in document.Layers.Where(l => l.Parent == null))
    PrintLayer(rootLayer, 0);

static void PrintLayer(DrawingLayer layer, int indent)
{
    var pad = new string(' ', indent * 2);
    Console.WriteLine($"{pad}{layer.Name} group={layer.IsGroup} visible={layer.IsVisible} opacity={layer.Opacity:F2} blend={layer.BlendMode} offset=({layer.OffsetX},{layer.OffsetY})");
    if (layer.IsGroup)
        foreach (var child in layer.Children)
            PrintLayer(child, indent + 1);
}

var viewport = new PixelRegion(Math.Max(0, docX - 256), Math.Max(0, docY - 256), 512, 512);
using var compositor = new LayerCompositor();

foreach (var zoom in new[] { 1.0, 0.2, 0.05 })
{
    compositor.SetSize(document.Width, document.Height);
    compositor.Invalidate(null);
    var passes = 0;
    while (compositor.Composite(document.Layers, document.Width, document.Height, 0, viewport, zoom) && passes++ < 64) { }

    var sampled = compositor.SampleCompositePixel(document.Layers, document.Width, document.Height, docX, docY, 0);
    var direct = compositor.CompositeToBgra(document.Layers, document.Width, document.Height, 0);
    var off = (docY * document.Width + docX) * 4;
    compositor.TryReadDisplayPixel(docX, docY, out var tb, out var tg, out var tr, out var ta);
    Console.WriteLine($"zoom={zoom:P0} lod={compositor.LastLod} tile={tr},{tg},{tb},{ta} sample={(sampled.HasValue ? $"{sampled.Value.R},{sampled.Value.G},{sampled.Value.B},{sampled.Value.A}" : "null")} direct={direct[off + 2]},{direct[off + 1]},{direct[off]},{direct[off + 3]} src=({r},{g},{b},{a})");
}

static (DrawingLayer Layer, int X, int Y, byte B, byte G, byte R, byte A)? FindInBoundsSample(DrawingDocument document, string? preferredName = null)
{
    var layer = preferredName != null ? FindLayerByName(document.Layers, preferredName) : null;
    if (layer != null)
    {
        Console.WriteLine($"Layer '{layer.Name}' offset=({layer.OffsetX},{layer.OffsetY}) bounds={layer.DocumentContentBounds}");
        var hit = FindInBoundsSampleInLayer(document, layer);
        if (hit != null) return hit;
    }

    foreach (var root in document.Layers)
    {
        if (root.IsGroup)
        {
            foreach (var child in root.Children)
            {
                var hit = FindInBoundsSampleInLayer(document, child);
                if (hit != null) return hit;
            }
        }
        else
        {
            var hit = FindInBoundsSampleInLayer(document, root);
            if (hit != null) return hit;
        }
    }

    return null;
}

static (DrawingLayer Layer, int X, int Y, byte B, byte G, byte R, byte A)? FindInBoundsSampleInLayer(DrawingDocument document, DrawingLayer layer)
{
    if (layer.IsGroup || !layer.IsVisible || layer.Opacity <= 0) return null;
    for (var y = 0; y < document.Height; y += 32)
    for (var x = 0; x < document.Width; x += 32)
    {
        layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY, out var b, out var g, out var r, out var a);
        if (a > 0)
            return (layer, x, y, b, g, r, a);
    }

    return null;
}

static (DrawingLayer Layer, int X, int Y, byte B, byte G, byte R, byte A)? FindSamplePixel(IReadOnlyList<DrawingLayer> layers, string? preferredName = null)
{
    if (preferredName != null)
    {
        var preferred = FindLayerByName(layers, preferredName);
        if (preferred != null)
        {
            var hit = FindSamplePixelInLayer(preferred);
            if (hit != null) return hit;
        }
    }

    foreach (var layer in layers)
    {
        if (layer.IsGroup)
        {
            var nested = FindSamplePixel(layer.Children, preferredName);
            if (nested != null) return nested;
            continue;
        }

        var sample = FindSamplePixelInLayer(layer);
        if (sample != null) return sample;
    }

    return null;
}

static DrawingLayer? FindLayerByName(IReadOnlyList<DrawingLayer> layers, string name)
{
    foreach (var layer in layers)
    {
        if (layer.IsGroup)
        {
            var nested = FindLayerByName(layer.Children, name);
            if (nested != null) return nested;
        }
        else if (layer.Name == name)
            return layer;
    }

    return null;
}

static void RunSyntheticGroupTileTest()
{
    var background = new DrawingLayer("Background", 512, 512);
    background.Pixels.SetPixel(300, 300, 0, 0, 255, 255);
    var child = new DrawingLayer("Ink", 512, 512);
    child.Pixels.SetPixel(300, 300, 10, 20, 30, 255);
    var group = new DrawingLayer("Group", 512, 512) { IsGroup = true };
    child.Parent = group;
    group.Children.Add(child);
    var layers = new[] { background, group };

    using var compositor = new LayerCompositor();
    var expected = compositor.CompositeToBgra(layers, 512, 512, 0);
    var offset = (300 * 512 + 300) * 4;
    Console.WriteLine($"CompositeToBgra r={expected[offset + 2]}");

    compositor.SetSize(512, 512);
    compositor.Invalidate(null);
    compositor.Composite(layers, 512, 512, 0, new PixelRegion(256, 256, 256, 256), 1.0);
    var ok = compositor.TryReadDisplayPixel(300, 300, out var b, out var g, out var r, out var a);
    Console.WriteLine($"Off-origin tile ok={ok} rgba=({r},{g},{b},{a})");

    compositor.Invalidate(null);
    compositor.Composite(layers, 512, 512, 0, new PixelRegion(0, 0, 512, 512), 1.0);
    ok = compositor.TryReadDisplayPixel(300, 300, out b, out g, out r, out a);
    Console.WriteLine($"Full viewport lod0 ok={ok} rgba=({r},{g},{b},{a}) hasTiles={compositor.HasAnyTiles}");

    compositor.Invalidate(null);
    compositor.Composite(layers, 512, 512, 0, new PixelRegion(0, 0, 512, 512), 0.05);
    ok = compositor.TryReadDisplayPixel(300, 300, out b, out g, out r, out a);
    Console.WriteLine($"Low zoom tile lod={compositor.LastLod} ok={ok} rgba=({r},{g},{b},{a}) hasTiles={compositor.HasAnyTiles}");
    var sampled = compositor.SampleCompositePixel(layers, 512, 512, 300, 300, 0);
    Console.WriteLine($"Low zoom sample={(sampled.HasValue ? $"{sampled.Value.R},{sampled.Value.G},{sampled.Value.B},{sampled.Value.A}" : "null")}");
}

static (DrawingLayer Layer, int X, int Y, byte B, byte G, byte R, byte A)? FindSamplePixelInLayer(DrawingLayer layer)
{
    if (layer.IsGroup || !layer.IsVisible || layer.Opacity <= 0) return null;
    var bounds = layer.DocumentContentBounds;
    if (bounds.IsEmpty) return null;

    for (var y = bounds.Y; y < bounds.Bottom; y += 64)
    for (var x = bounds.X; x < bounds.Right; x += 64)
    {
        var lx = x - layer.OffsetX;
        var ly = y - layer.OffsetY;
        layer.Pixels.GetPixel(lx, ly, out var b, out var g, out var r, out var a);
        if (a > 0)
            return (layer, x, y, b, g, r, a);
    }

    return null;
}
