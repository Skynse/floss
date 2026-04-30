using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Floss.App.Brushes;

/// <summary>
/// User-defined brush palette layout: category names (in order) and
/// which brush lives in which category.
/// </summary>
public sealed class BrushPaletteConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<string> Categories { get; set; } = [];
    public Dictionary<string, string> BrushCategory { get; set; } = [];

    public static BrushPaletteConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<BrushPaletteConfig>(File.ReadAllText(path), JsonOpts)
                    ?? new BrushPaletteConfig();
        }
        catch { }
        return new BrushPaletteConfig();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }

    /// <summary>
    /// Ensure every brush in <paramref name="assets"/> has a category assignment.
    /// Brushes without an assignment are placed according to their <see cref="BrushKind"/>.
    /// Missing categories are auto-created.
    /// </summary>
    public void SyncWithAssets(IReadOnlyList<BrushAsset> assets)
    {
        var kindToDefaultCategory = new Dictionary<BrushKind, string>
        {
            [BrushKind.Pencil] = "Pencils",
            [BrushKind.Ink] = "Pens",
            [BrushKind.Marker] = "Markers",
            [BrushKind.Airbrush] = "Airbrush",
            [BrushKind.Eraser] = "Erasers",
        };

        if (Categories.Count == 0)
        {
            Categories.AddRange(["Recent", "Pencils", "Pens", "Markers", "Airbrush"]);
        }

        foreach (var asset in assets)
        {
            if (BrushCategory.ContainsKey(asset.Id)) continue;

            var defaultCat = kindToDefaultCategory.TryGetValue(asset.Preset.Kind, out var cat)
                ? cat : "Pens";

            BrushCategory[asset.Id] = defaultCat;

            if (!Categories.Contains(defaultCat))
                Categories.Add(defaultCat);
        }
    }
}
