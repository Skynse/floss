using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

/// <summary>
/// walks the node graph from image roots — never a flat layer list that
/// also contains nested group children.
/// </summary>
internal static class LayerStackComposition
{
    public static List<DrawingLayer> GetRootLayers(IReadOnlyList<DrawingLayer> layers)
    {
        var roots = new List<DrawingLayer>(layers.Count);
        foreach (var layer in layers)
            if (layer.Parent == null && !layer.IsPaper)
                roots.Add(layer);
        return roots;
    }

    /// <summary>
    /// Roots for a full document composite, or explicit layers when compositing
    /// a subtree (e.g. one group in thumbnail preview).
    /// </summary>
    public static List<DrawingLayer> SelectLayersForComposite(IReadOnlyList<DrawingLayer> layers)
    {
        var roots = GetRootLayers(layers);
        if (roots.Count > 0)
            return roots;

        var explicitLayers = new List<DrawingLayer>(layers.Count);
        foreach (var layer in layers)
            if (!layer.IsPaper)
                explicitLayers.Add(layer);
        return explicitLayers;
    }
}
