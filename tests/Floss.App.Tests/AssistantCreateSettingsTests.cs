using Floss.App.Config;
using Floss.App.Document.Assistants;
using Xunit;

namespace Floss.App.Tests;

public class AssistantCreateSettingsTests
{
    [Fact]
    public void PerspectivePreset_DefaultsCreateAtEditingLayerTrue()
    {
        var assistants = ToolGroupConfig.Defaults().First(g => g.Name == "Assistants");
        var preset = assistants.Presets.First(p => p.Id == ToolGroupConfig.AssistantPerspectivePresetId);

        Assert.True(preset.AssistantCreateAtEditingLayer);
    }

    [Fact]
    public void FromPreset_IncludesCreateAtEditingLayer()
    {
        var preset = new ToolPreset
        {
            AssistantType = PaintingAssistant.PerspectiveType,
            AssistantCreateAtEditingLayer = false,
        };

        var settings = AssistantCreateSettings.FromPreset(preset);

        Assert.False(settings.CreateAtEditingLayer);
    }
}
