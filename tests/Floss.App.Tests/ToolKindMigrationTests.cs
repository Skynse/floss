using System.Text.Json;
using Floss.App.Config;
using Floss.App.Processes;
using Floss.App.Processes.Internal;
using Xunit;

namespace Floss.App.Tests;

public class ToolKindMigrationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("Brush", ToolKind.Brush)]
    [InlineData("Select", ToolKind.Select)]
    [InlineData("Move", ToolKind.Hand)]
    public void MigrateFromLegacyProcesses_MapsEngineField(string engine, ToolKind expected)
    {
        var preset = JsonSerializer.Deserialize<ToolPreset>($$"""{"engine":"{{engine}}"}""", JsonOpts)!;
        preset.MigrateFromLegacyProcesses();
        Assert.Equal(expected, preset.Kind);
    }

    [Fact]
    public void MigrateFromLegacyProcesses_MapsInputOutputPair()
    {
        var preset = JsonSerializer.Deserialize<ToolPreset>("""
            {"inputProcess":"Click","outputProcess":"MagicWand"}
            """, JsonOpts)!;
        preset.MigrateFromLegacyProcesses();
        Assert.Equal(ToolKind.MagicWand, preset.Kind);
    }

    [Fact]
    public void ToolFactory_CreatesFromKindOnly()
    {
        var doc = new DrawingDocument(8, 8);
        using var engine = new BrushEngine();
        var factory = new ToolFactory(doc, engine);
        var tool = factory.CreateTool(new ToolPreset { Kind = ToolKind.Select, SelectMode = SelectMode.Lasso });
        Assert.IsType<CompositeTool>(tool);
    }

    [Fact]
    public void LegacyModifierKeys_MigrateToKindKeys()
    {
        var settings = new ModifierKeySettings
        {
            ToolSpecificAssignments =
            {
                ["2:1"] = [new() { Modifiers = Avalonia.Input.KeyModifiers.Shift, Action = ModifierAction.ToolAux }]
            }
        };
        settings.MigrateLegacyToolKeys();
        Assert.True(settings.ToolSpecificAssignments.ContainsKey(ModifierKeySettings.KeyFor(ToolKind.Brush)));
    }
}
