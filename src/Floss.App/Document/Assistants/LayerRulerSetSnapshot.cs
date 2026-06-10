namespace Floss.App.Document.Assistants;

public sealed class LayerRulerSetSnapshot
{
    public required int LayerIndex { get; init; }

    public required LayerRulerSet Set { get; init; }

    public LayerRulerSetSnapshot Clone()
        => new()
        {
            LayerIndex = LayerIndex,
            Set = Set.Clone(),
        };
}
