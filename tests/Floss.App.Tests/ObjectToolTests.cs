using System.Text.Json;
using Floss.App.Config;
using Floss.App.Tools;
using Xunit;

namespace Floss.App.Tests;

public class ObjectToolTests
{
    [Fact]
    public void Defaults_IncludesObjectPresetInOperationGroup()
    {
        var operation = ToolGroupConfig.Defaults().First(g => g.Name == "Operation");
        var preset = operation.Presets.First(p => p.Id == ToolGroupConfig.ObjectPresetId);

        Assert.Equal(ToolKind.Object, preset.Kind);
        Assert.True(preset.SelectableObjectFlags.HasFlag(SelectableObjectFlags.Ruler));
        Assert.Equal(ObjectSelectionMode.Replace, preset.ObjectSelectionMode);
    }

    [Fact]
    public void MigrateFromLegacyProcesses_MapsObjectInputOutputPair()
    {
        var preset = JsonSerializer.Deserialize<ToolPreset>("""
            {
              "inputProcess": "Object",
              "outputProcess": "Object"
            }
            """, ToolGroupConfigTestJson.Options)!;
        preset.MigrateFromLegacyProcesses();
        Assert.Equal(ToolKind.Object, preset.Kind);
    }
}

internal static class ToolGroupConfigTestJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
