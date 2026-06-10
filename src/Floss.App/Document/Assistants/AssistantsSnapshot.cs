using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Document.Assistants;

public sealed class AssistantsSnapshot
{
    public required List<LayerRulerSetSnapshot> LayerSets { get; init; }

    public string? SelectedId { get; init; }

    public AssistantsSnapshot Clone()
        => new()
        {
            LayerSets = LayerSets.Select(s => s.Clone()).ToList(),
            SelectedId = SelectedId,
        };
}
