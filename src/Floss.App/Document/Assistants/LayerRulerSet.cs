using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Document.Assistants;

/// <summary>Rulers/guides attached to a raster layer (ruler icon on layer row).</summary>
public sealed class LayerRulerSet
{
    public List<PaintingAssistant> Rulers { get; } = [];

    public RulerShowScope ShowScope { get; set; } = RulerShowScope.AllLayers;

    public bool LinkToLayer { get; set; } = true;

    /// <summary>Ruler icon visibility — hide guides without hiding layer pixels.</summary>
    public bool RulersVisible { get; set; } = true;

    public bool HasRulers => Rulers.Count > 0;

    public LayerRulerSet Clone()
    {
        var copy = new LayerRulerSet
        {
            ShowScope = ShowScope,
            LinkToLayer = LinkToLayer,
            RulersVisible = RulersVisible,
        };
        copy.Rulers.AddRange(Rulers.Select(r => r.Clone()));
        return copy;
    }
}
