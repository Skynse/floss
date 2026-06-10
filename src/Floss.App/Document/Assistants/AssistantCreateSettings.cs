using Floss.App.Config;

namespace Floss.App.Document.Assistants;

public readonly record struct AssistantCreateSettings(
    string TypeId,
    PerspectiveAssistantMode PerspectiveMode,
    bool FisheyeEnabled,
    double FovDegrees,
    int GridSubdivisions,
    bool SnapEnabled,
    bool CreateAtEditingLayer)
{
    public static AssistantCreateSettings FromPreset(ToolPreset preset) => new(
        preset.AssistantType ?? PaintingAssistant.RulerType,
        preset.AssistantPerspectiveMode,
        preset.AssistantFisheyeEnabled,
        preset.AssistantFovDegrees,
        preset.AssistantGridSubdivisions,
        preset.AssistantSnapEnabled,
        preset.AssistantCreateAtEditingLayer);

    public void ApplyTo(PaintingAssistant assistant)
    {
        assistant.PerspectiveMode = PerspectiveMode;
        assistant.FisheyeEnabled = FisheyeEnabled;
        assistant.FovDegrees = FovDegrees;
        assistant.GridSubdivisions = GridSubdivisions;
        assistant.SnapEnabled = SnapEnabled;
    }
}
