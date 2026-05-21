using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Brushes;

public static class BrushMaterialTips
{
    public static IReadOnlyList<BrushTipData> ForPreset(BrushPreset preset)
    {
        var tips = preset.Tips
            .Where(t => t.Kind == BrushTipStorageKind.EmbeddedPng && t.PngBytes.Length > 0)
            .Select(t => t.DeepClone())
            .ToList();
        if (tips.Count > 0)
            return tips;

        return ActiveEmbedded(preset) is { } active
            ? [active.DeepClone()]
            : [];
    }

    public static IReadOnlyList<BrushTipData> PreserveForPreset(BrushPreset preset)
        => ForPreset(preset);

    public static BrushTipData? ActiveEmbedded(BrushPreset preset)
    {
        if (preset.Tip is ImageBrushTip)
            return BrushTipData.FromTip(preset.Tip);

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
}
