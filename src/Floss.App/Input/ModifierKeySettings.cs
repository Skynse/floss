using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;
using Floss.App;
using Floss.App.Config;
using Floss.App.Processes.Internal;

namespace Floss.App.Input;

public enum ModifierAction
{
    None,
    Common,
    /// <summary>
    /// Activate the active tool's built-in alternate (e.g. eyedropper on brush).
    /// Optional <see cref="ModifierKeyAssignment.TemporaryToolPresetId"/> is used as a
    /// fallback when the current tool has no alternate.
    /// </summary>
    AlternateInvocation,
    ChangeToolTemporarily,
    ToolAux,
    ChangeBrushSize,
}

public enum ToolAuxOperationType
{
    None = 0,
    StraightLine,
    AddToSelection,
    RemoveFromSelection,
    SelectFromSelection,
}

public sealed class ModifierKeyAssignment
{
    public Key? Key { get; set; }
    public KeyModifiers Modifiers { get; set; }
    public ModifierAction Action { get; set; }
    public string? TemporaryToolPresetId { get; set; }
    public ToolAuxOperationType ToolAuxOper { get; set; }
}

public sealed class ModifierKeySettings
{
    public List<ModifierKeyAssignment> GeneralAssignments { get; set; } = [];
    public Dictionary<string, List<ModifierKeyAssignment>> ToolSpecificAssignments { get; set; } = [];

    public static ModifierKeySettings CreateDefaults()
    {
        var settings = new ModifierKeySettings
        {
            GeneralAssignments =
            [
                new() { Modifiers = KeyModifiers.Shift, Action = ModifierAction.ToolAux, ToolAuxOper = ToolAuxOperationType.StraightLine },
                new() { Modifiers = KeyModifiers.Control, Action = ModifierAction.None },
                new() { Modifiers = KeyModifiers.Alt, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.EyedropperPresetId },
                new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.ChangeBrushSize },
                new() { Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.SelectLayerPresetId },
                new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },
                new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },

                new() { Key = Key.Space, Modifiers = KeyModifiers.None, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.ViewHandPresetId },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Control, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.ViewZoomInPresetId },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Alt, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.ViewZoomOutPresetId },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Shift, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.ViewRotatePresetId },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.None },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.None },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },
                new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },
            ],
        };

        foreach (var kind in new[] { ToolKind.Pen, ToolKind.Brush, ToolKind.Eraser, ToolKind.Smudge })
        {
            settings.ToolSpecificAssignments[KeyFor(kind)] = CreateBrushFamilyModifierDefaults();
        }

        settings.EnsureSelectionToolModifierDefaults();

        return settings;
    }

    internal void EnsureSelectionToolModifierDefaults()
    {
        if (!ToolSpecificAssignments.ContainsKey(KeyFor(ToolKind.Select)))
            ToolSpecificAssignments[KeyFor(ToolKind.Select)] = CreateSelectionModifierDefaults();

        if (!ToolSpecificAssignments.ContainsKey(KeyFor(ToolKind.MagicWand)))
            ToolSpecificAssignments[KeyFor(ToolKind.MagicWand)] = CreateSelectionModifierDefaults();
    }

    private static List<ModifierKeyAssignment> CreateBrushFamilyModifierDefaults() =>
    [
        new() { Modifiers = KeyModifiers.Control, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.MoveLayerPresetId },
        new() { Modifiers = KeyModifiers.Alt, Action = ModifierAction.AlternateInvocation },
        new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.ChangeBrushSize },
        new() { Modifiers = KeyModifiers.Shift, Action = ModifierAction.ToolAux, ToolAuxOper = ToolAuxOperationType.StraightLine },
        new() { Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.SelectLayerPresetId },
        new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },
        new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },

        new() { Key = Key.Space, Modifiers = KeyModifiers.None, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Alt, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Shift, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.Common },
    ];

    private static List<ModifierKeyAssignment> CreateSelectionModifierDefaults() =>
    [
        new() { Modifiers = KeyModifiers.Shift, Action = ModifierAction.ToolAux, ToolAuxOper = ToolAuxOperationType.AddToSelection },
        new() { Modifiers = KeyModifiers.Alt, Action = ModifierAction.ToolAux, ToolAuxOper = ToolAuxOperationType.RemoveFromSelection },
        new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.ToolAux, ToolAuxOper = ToolAuxOperationType.SelectFromSelection },
        new() { Modifiers = KeyModifiers.Control, Action = ModifierAction.None },
        new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.None },
        new() { Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.SelectLayerPresetId },
        new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },

        new() { Key = Key.Space, Modifiers = KeyModifiers.None, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Alt, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Shift, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.Common },
        new() { Key = Key.Space, Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.Common },
    ];

    public static string KeyFor(ToolKind kind) => kind.ToString();

    private static string LegacyKeyFor(int input, int output) => $"{input}:{output}";

    public ModifierKeyAssignment? Resolve(ToolKind toolKind, Key? key, KeyModifiers mods)
    {
        bool ExactKeyMatch(ModifierKeyAssignment a) =>
            key.HasValue && a.Modifiers == mods && a.Key.HasValue && a.Key.Value == key.Value;
        bool AnyKeyMatch(ModifierKeyAssignment a) =>
            a.Modifiers == mods && !a.Key.HasValue;
        ModifierKeyAssignment? ResolveGeneral()
        {
            var match = GeneralAssignments.FirstOrDefault(ExactKeyMatch)
                     ?? GeneralAssignments.FirstOrDefault(AnyKeyMatch);
            return match?.Action == ModifierAction.None ? null : match;
        }

        var specificKey = KeyFor(toolKind);
        if (ToolSpecificAssignments.TryGetValue(specificKey, out var specific))
        {
            var match = specific.FirstOrDefault(ExactKeyMatch) ?? specific.FirstOrDefault(AnyKeyMatch);
            if (match != null)
            {
                if (match.Action == ModifierAction.None)
                    return ResolveGeneral();
                if (match.Action == ModifierAction.Common)
                    return ResolveGeneral();
                return match;
            }
        }

        return ResolveGeneral();
    }

    internal void MigrateLegacyToolKeys()
    {
        if (ToolSpecificAssignments.Count == 0) return;

        var migrated = new Dictionary<string, List<ModifierKeyAssignment>>(StringComparer.Ordinal);
        foreach (var (key, assignments) in ToolSpecificAssignments)
        {
            if (!key.Contains(':', StringComparison.Ordinal))
            {
                migrated[key] = assignments;
                continue;
            }

            var parts = key.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var input)
                || !int.TryParse(parts[1], out var output))
            {
                migrated[key] = assignments;
                continue;
            }

            var kind = ToolProcessMapping.FromLegacyProcesses((InputProcessType)input, (OutputProcessType)output);
            var newKey = KeyFor(kind);
            if (!migrated.ContainsKey(newKey))
                migrated[newKey] = assignments;
        }

        ToolSpecificAssignments = migrated;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ModifierKeySettings Load()
    {
        try
        {
            var path = AppPaths.ModifierKeySettingsPath;
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<ModifierKeySettings>(File.ReadAllText(path), JsonOpts) ?? CreateDefaults();
                settings.MigrateLegacyToolKeys();
                settings.EnsureSelectionToolModifierDefaults();
                return settings;
            }
        }
        catch (Exception ex) { CrashLog.Write(ex, "ModifierKeySettings.Load"); }
        return CreateDefaults();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.ModifierKeySettingsPath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch (Exception ex) { CrashLog.Write(ex, "ModifierKeySettings.Save"); }
    }
}
