using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Floss.App.Brushes;

namespace Floss.App.Config;

public static class PresetPackageFormat
{
    public const string SubToolExtension = ".flbr";
    public const string SubToolGroupExtension = ".flbrg";

    public static void ExportSubTool(string path, ToolGroup sourceGroup, ToolPreset preset, IReadOnlyList<BrushAsset> brushAssets)
    {
        ResetPackageFile(path);
        var exportGroup = new ToolGroup
        {
            Id = sourceGroup.Id,
            Name = sourceGroup.Name,
            Shortcut = sourceGroup.Shortcut,
            DefaultKind = sourceGroup.DefaultKind,
            CustomIcon = sourceGroup.CustomIcon,
            LastActivePresetId = preset.Id,
            Presets = [preset],
            Categories = sourceGroup.Categories
                .Where(c => c.PresetIds.Contains(preset.Id))
                .Select(c => new ToolCategory { Name = c.Name, PresetIds = [preset.Id] })
                .ToList()
        };

        var store = PresetStore.OpenPackage(path);
        store.SaveToolGroups([exportGroup]);
        store.SaveBrushAssets(ReferencedBrushAssets([preset], brushAssets));
    }

    public static void ExportSubToolGroup(string path, ToolGroup group, IReadOnlyList<BrushAsset> brushAssets)
    {
        ResetPackageFile(path);
        var store = PresetStore.OpenPackage(path);
        store.SaveToolGroups([group]);
        store.SaveBrushAssets(ReferencedBrushAssets(group.Presets, brushAssets));
    }

    public static void ExportSubToolGroup(string path, ToolGroup sourceGroup, ToolCategory category, IReadOnlyList<BrushAsset> brushAssets)
    {
        ResetPackageFile(path);
        var presetIds = category.PresetIds.ToHashSet(StringComparer.Ordinal);
        var presets = sourceGroup.Presets.Where(p => presetIds.Contains(p.Id)).ToList();
        var exportGroup = new ToolGroup
        {
            Id = sourceGroup.Id,
            Name = sourceGroup.Name,
            Shortcut = sourceGroup.Shortcut,
            DefaultKind = sourceGroup.DefaultKind,
            CustomIcon = sourceGroup.CustomIcon,
            LastActivePresetId = presets.Any(p => p.Id == sourceGroup.LastActivePresetId)
                ? sourceGroup.LastActivePresetId
                : presets.FirstOrDefault()?.Id,
            Presets = presets,
            Categories = [new ToolCategory { Name = category.Name, PresetIds = category.PresetIds.Where(presetIds.Contains).ToList() }]
        };

        var store = PresetStore.OpenPackage(path);
        store.SaveToolGroups([exportGroup]);
        store.SaveBrushAssets(ReferencedBrushAssets(presets, brushAssets));
    }

    private static IEnumerable<BrushAsset> ReferencedBrushAssets(IEnumerable<ToolPreset> presets, IReadOnlyList<BrushAsset> brushAssets)
    {
        var brushIds = presets
            .Select(p => p.BrushId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        return brushAssets.Where(a => brushIds.Contains(a.Id));
    }

    public static (ToolGroup Group, ToolPreset Preset, IReadOnlyList<BrushAsset> BrushAssets) ImportSubTool(string path)
    {
        var store = PresetStore.OpenPackage(path);
        var groups = store.LoadToolGroups();
        var brushAssets = store.LoadBrushAssets();
        var group = groups.FirstOrDefault() ?? throw new InvalidOperationException("No tool group in package");
        var preset = group.Presets.FirstOrDefault() ?? throw new InvalidOperationException("No preset in package");
        return (group, preset, brushAssets);
    }

    public static (IReadOnlyList<ToolGroup> Groups, IReadOnlyList<BrushAsset> BrushAssets) ImportSubToolGroup(string path)
    {
        var store = PresetStore.OpenPackage(path);
        return (store.LoadToolGroups(), store.LoadBrushAssets());
    }

    private static void ResetPackageFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
