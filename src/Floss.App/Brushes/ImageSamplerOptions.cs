using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Brushes;

public sealed class ImageSamplerOption(string label, BrushTipData tip)
{
    public string Label { get; } = label;
    public BrushTipData Tip { get; } = tip;

    public override string ToString() => Label;
}

public static class ImageSamplerOptions
{
    public static List<ImageSamplerOption> FromTips(IReadOnlyList<BrushTipData>? tips)
    {
        if (tips == null || tips.Count == 0)
            return [];

        var options = new List<ImageSamplerOption>();
        foreach (var tip in tips.Where(t => t.Kind == BrushTipStorageKind.EmbeddedPng && t.PngBytes.Length > 0))
        {
            if (options.Any(o => SameBytes(o.Tip.PngBytes, tip.PngBytes)))
                continue;
            options.Add(new ImageSamplerOption($"Image {options.Count + 1}", tip.DeepClone()));
        }
        return options;
    }

    public static bool SameBytes(byte[] a, byte[] b)
        => a.Length == b.Length && a.AsSpan().SequenceEqual(b);

    public static ImageSamplerOption? Match(IReadOnlyList<ImageSamplerOption> options, byte[] pngBytes)
        => options.FirstOrDefault(o => SameBytes(o.Tip.PngBytes, pngBytes));
}
