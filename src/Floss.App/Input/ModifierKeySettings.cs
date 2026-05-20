using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;
using Floss.App;

namespace Floss.App.Input;

public enum ModifierAction
{
    None,
    Common,
    ChangeToolTemporarily,
    ToolAux,
    ChangeBrushSize,
}

public enum ToolAuxOperationType
{
    None = 0,
    StraightLine,
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
                // ── Modifier-only combos ──────────────────────────────────────
                new() { Modifiers = KeyModifiers.Shift, Action = ModifierAction.ToolAux, ToolAuxOper = ToolAuxOperationType.StraightLine },
                new() { Modifiers = KeyModifiers.Control, Action = ModifierAction.None },
                new() { Modifiers = KeyModifiers.Alt, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.EyedropperPresetId },
                new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Action = ModifierAction.ChangeBrushSize },
                new() { Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.SelectLayerPresetId },
                new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },
                new() { Modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, Action = ModifierAction.None },

                // ── Space combos — view tools (like CSP) ──────────────────────
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

        // ── Tool-specific defaults for brush-family tools ───────────────────
        // These match CSP's defaults for pen/brush/eraser/smudge tools.
        foreach (var input in new[] { InputProcessType.Pen, InputProcessType.Brush, InputProcessType.Eraser, InputProcessType.Smudge })
        {
            var key = KeyFor((int)input, (int)OutputProcessType.DirectDraw);
            settings.ToolSpecificAssignments[key] =
            [
                new() { Modifiers = KeyModifiers.Control, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.MoveLayerPresetId },
                new() { Modifiers = KeyModifiers.Alt, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = ToolGroupConfig.EyedropperPresetId },
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
        }

        return settings;
    }

    private static string KeyFor(int input, int output) => $"{input}:{output}";

    public ModifierKeyAssignment? Resolve(int inputProcessType, int outputProcessType, Key? key, KeyModifiers mods)
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

        var specificKey = KeyFor(inputProcessType, outputProcessType);
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
                return JsonSerializer.Deserialize<ModifierKeySettings>(File.ReadAllText(path), JsonOpts) ?? CreateDefaults();
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
