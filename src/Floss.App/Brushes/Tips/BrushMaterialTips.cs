using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Brushes.Tips;

/// <summary>
/// Material tips are the preset's PNG library (<see cref="BrushPreset.Tips"/>).
/// Image Sampler nodes hold a <see cref="BrushTipNode.MaterialTipId"/> reference;
/// bytes are resolved from the library at evaluation time.
/// </summary>
public static class BrushMaterialTips
{
    public static IReadOnlyList<BrushTipData> ForPreset(BrushPreset preset)
    {
        var tips = preset.Tips
            .Where(t => t.Kind == BrushTipStorageKind.EmbeddedPng && t.PngBytes.Length > 0)
            .Select(NormalizeTip)
            .ToList();
        if (tips.Count > 0)
            return tips;

        return ActiveEmbedded(preset) is { } active
            ? [NormalizeTip(active)]
            : [];
    }

    public static IReadOnlyList<BrushTipData> PreserveForPreset(BrushPreset preset)
        => ForPreset(preset);

    public static BrushTipData? ActiveEmbedded(BrushPreset preset)
    {
        if (preset.Tip is ImageBrushTip img)
            return BrushTipData.FromTip(img);

        if (preset.Tip is NodeBrushTip node && node.Graph.TryGetDirectImageSampler(out var bytes))
        {
            return new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = bytes
            };
        }

        return null;
    }

    public static BrushTipData NormalizeTip(BrushTipData tip)
    {
        var clone = tip.DeepClone();
        clone.EnsureId();
        if (string.IsNullOrWhiteSpace(clone.Label))
            clone.Label = "";
        return clone;
    }

    public static List<BrushTipData> NormalizeLibrary(IEnumerable<BrushTipData> tips)
        => tips.Select(NormalizeTip).ToList();

    public static bool ReferencesSame(BrushTipData a, BrushTipData b)
    {
        if (!string.IsNullOrEmpty(a.Id) && !string.IsNullOrEmpty(b.Id))
            return string.Equals(a.Id, b.Id, StringComparison.Ordinal);
        return a.Kind == BrushTipStorageKind.EmbeddedPng
            && b.Kind == BrushTipStorageKind.EmbeddedPng
            && ImageSamplerOptions.SameBytes(a.PngBytes, b.PngBytes);
    }

    public static byte[] ResolveSamplerPng(BrushTipNode node, IReadOnlyList<BrushTipData>? materialTips)
    {
        if (node.Kind != BrushTipNodeKind.ImageSampler)
            return [];

        if (materialTips != null && !string.IsNullOrEmpty(node.MaterialTipId))
        {
            var tip = materialTips.FirstOrDefault(t => t.Id == node.MaterialTipId);
            return tip is { PngBytes.Length: > 0 } ? tip.PngBytes : [];
        }

        // Legacy graphs may still carry embedded bytes until bound.
        return node.PngBytes.Length > 0 ? node.PngBytes : [];
    }

    public static bool HasResolvedSampler(BrushTipNode node, IReadOnlyList<BrushTipData>? materialTips)
        => node.Kind == BrushTipNodeKind.ImageSampler && ResolveSamplerPng(node, materialTips).Length > 0;

    public static ImageSamplerOption? MatchSamplerOption(
        IReadOnlyList<ImageSamplerOption> options,
        BrushTipNode node)
    {
        if (!string.IsNullOrEmpty(node.MaterialTipId))
            return options.FirstOrDefault(o => o.Tip.Id == node.MaterialTipId);

        var legacy = ResolveSamplerPng(node, null);
        return legacy.Length > 0 ? ImageSamplerOptions.MatchByBytes(options, legacy) : null;
    }

    public static string SamplerDisplayLabel(BrushTipNode node, IReadOnlyList<ImageSamplerOption> options)
    {
        if (node.Kind != BrushTipNodeKind.ImageSampler)
            return "";

        if (!string.IsNullOrEmpty(node.MaterialTipId))
        {
            var matched = options.FirstOrDefault(o => o.Tip.Id == node.MaterialTipId);
            if (matched != null)
                return matched.Label;
            return "Missing image";
        }

        if (ResolveSamplerPng(node, null).Length > 0)
            return options.Count > 0 ? "Unlinked image" : "No images";

        return options.Count > 0 ? "Select image…" : "No images";
    }

    /// <summary>
    /// Binds Image Sampler nodes to library ids, strips embedded bytes, and clears orphaned refs.
    /// May append newly discovered embedded images to <paramref name="materialTips"/>.
    /// </summary>
    public static BrushTipNodeGraph BindGraphToLibrary(
        BrushTipNodeGraph graph,
        IList<BrushTipData> materialTips)
    {
        var clone = graph.DeepClone();
        foreach (var node in clone.Nodes)
        {
            if (node.Kind != BrushTipNodeKind.ImageSampler)
                continue;

            if (!string.IsNullOrEmpty(node.MaterialTipId))
            {
                var tip = materialTips.FirstOrDefault(t => t.Id == node.MaterialTipId);
                if (tip == null || tip.PngBytes.Length == 0)
                {
                    node.MaterialTipId = null;
                    node.PngBytes = [];
                }
                else
                    node.PngBytes = [];

                continue;
            }

            if (node.PngBytes.Length == 0)
                continue;

            var match = materialTips.FirstOrDefault(t =>
                t.Kind == BrushTipStorageKind.EmbeddedPng
                && ImageSamplerOptions.SameBytes(t.PngBytes, node.PngBytes));
            if (match != null)
            {
                match = NormalizeTip(match);
                node.MaterialTipId = match.Id;
                node.PngBytes = [];
                continue;
            }

            var imported = NormalizeTip(new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = node.PngBytes.ToArray(),
                Label = NextImageLabel(materialTips)
            });
            materialTips.Add(imported);
            node.MaterialTipId = imported.Id;
            node.PngBytes = [];
        }

        return clone;
    }

    public static BrushTipNodeGraph ClearRemovedTipReferences(
        BrushTipNodeGraph graph,
        BrushTipData removed,
        IReadOnlyList<BrushTipData> remainingTips)
    {
        var clone = graph.DeepClone();
        foreach (var node in clone.Nodes)
        {
            if (node.Kind != BrushTipNodeKind.ImageSampler)
                continue;

            var orphaned = !string.IsNullOrEmpty(node.MaterialTipId)
                && string.Equals(node.MaterialTipId, removed.Id, StringComparison.Ordinal);
            var legacyMatch = node.PngBytes.Length > 0
                && removed.PngBytes.Length > 0
                && ImageSamplerOptions.SameBytes(node.PngBytes, removed.PngBytes);

            if (orphaned || legacyMatch)
            {
                node.MaterialTipId = null;
                node.PngBytes = [];
            }
        }

        return BindGraphToLibrary(clone, remainingTips.ToList());
    }

    public static string LibraryCacheKey(IReadOnlyList<BrushTipData>? materialTips)
    {
        if (materialTips == null || materialTips.Count == 0)
            return "";

        return string.Join('|', materialTips
            .Where(t => t.Kind == BrushTipStorageKind.EmbeddedPng && t.PngBytes.Length > 0)
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => $"{t.Id}:{t.PngBytes.Length}"));
    }

    public static void BindToPreset(BrushPreset preset)
    {
        var tips = ForPreset(preset);
        switch (preset.Tip)
        {
            case NodeBrushTip node:
                node.BindMaterialTips(tips);
                break;
            case ProceduralBrushTip proc:
                proc.BindMaterialTips(tips);
                break;
        }
    }

    public static (List<BrushTipData> Tips, IBrushTip Tip) ApplyLibraryChange(
        BrushPreset preset,
        List<BrushTipData> newTips,
        BrushTipData? removed = null)
    {
        var normalized = NormalizeLibrary(newTips);
        IBrushTip tip = preset.Tip;
        if (tip is NodeBrushTip nodeTip)
        {
            var graph = removed != null
                ? ClearRemovedTipReferences(nodeTip.Graph, removed, normalized)
                : BindGraphToLibrary(nodeTip.Graph, normalized);
            tip = new NodeBrushTip(graph);
            ((NodeBrushTip)tip).BindMaterialTips(normalized);
        }
        else if (tip is ProceduralBrushTip procTip)
        {
            var bound = BindGraphToLibrary(procTip.Graph, normalized);
            tip = new ProceduralBrushTip(bound);
            ((ProceduralBrushTip)tip).BindMaterialTips(normalized);
        }

        return (normalized, tip);
    }

    private static string NextImageLabel(IEnumerable<BrushTipData> materialTips)
    {
        var n = materialTips.Count(t => t.Kind == BrushTipStorageKind.EmbeddedPng) + 1;
        return $"Image {n}";
    }
}
