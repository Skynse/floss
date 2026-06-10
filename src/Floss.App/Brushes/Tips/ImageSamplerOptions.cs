using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Brushes.Tips;

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
            var normalized = BrushMaterialTips.NormalizeTip(tip);
            if (options.Any(o => o.Tip.Id == normalized.Id))
                continue;
            if (options.Any(o => SameBytes(o.Tip.PngBytes, normalized.PngBytes)))
                continue;

            var label = string.IsNullOrWhiteSpace(normalized.Label)
                ? $"Image {options.Count + 1}"
                : normalized.Label;
            options.Add(new ImageSamplerOption(label, normalized));
        }
        return options;
    }

    public static bool SameBytes(byte[] a, byte[] b)
        => a.Length == b.Length && a.AsSpan().SequenceEqual(b);

    public static ImageSamplerOption? MatchByBytes(IReadOnlyList<ImageSamplerOption> options, byte[] pngBytes)
        => pngBytes.Length == 0 ? null : options.FirstOrDefault(o => SameBytes(o.Tip.PngBytes, pngBytes));

    public static ImageSamplerOption? Match(IReadOnlyList<ImageSamplerOption> options, BrushTipNode node)
        => BrushMaterialTips.MatchSamplerOption(options, node);

    [Obsolete("Match by node MaterialTipId instead.")]
    public static ImageSamplerOption? Match(IReadOnlyList<ImageSamplerOption> options, byte[] pngBytes)
        => MatchByBytes(options, pngBytes);
}
