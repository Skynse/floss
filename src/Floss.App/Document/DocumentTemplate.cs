using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace Floss.App.Document;

public sealed class DocumentTemplate
{
    public string Name { get; set; } = "Untitled";
    // Nullable — null means "not included in this preset" (user keeps their current value).
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Dpi { get; set; }
    public string? Background { get; set; }
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
}

public static class DocumentTemplateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static readonly IReadOnlyList<DocumentTemplate> BuiltIn =
    [
        new() { Name = "Square (2048×2048)",       Width = 2048, Height = 2048, Dpi = 72,  Background = "White",        IsBuiltIn = true },
        new() { Name = "HD (1920×1080)",            Width = 1920, Height = 1080, Dpi = 72,  Background = "White",        IsBuiltIn = true },
        new() { Name = "4K (3840×2160)",            Width = 3840, Height = 2160, Dpi = 72,  Background = "White",        IsBuiltIn = true },
        new() { Name = "A4 Portrait (300 DPI)",     Width = 2480, Height = 3508, Dpi = 300, Background = "White",        IsBuiltIn = true },
        new() { Name = "A4 Landscape (300 DPI)",    Width = 3508, Height = 2480, Dpi = 300, Background = "White",        IsBuiltIn = true },
        new() { Name = "A3 Portrait (300 DPI)",     Width = 3508, Height = 4961, Dpi = 300, Background = "White",        IsBuiltIn = true },
        new() { Name = "Letter Portrait (300 DPI)", Width = 2550, Height = 3300, Dpi = 300, Background = "White",        IsBuiltIn = true },
        new() { Name = "Comic Page (350 DPI)",      Width = 2040, Height = 3087, Dpi = 350, Background = "White",        IsBuiltIn = true },
        new() { Name = "Webtoon Strip",             Width = 800,  Height = 1200, Dpi = 72,  Background = "White",        IsBuiltIn = true },
        new() { Name = "Instagram Square",          Width = 1080, Height = 1080, Dpi = 72,  Background = "White",        IsBuiltIn = true },
        new() { Name = "Twitter Header",            Width = 1500, Height = 500,  Dpi = 72,  Background = "White",        IsBuiltIn = true },
    ];

    public static List<DocumentTemplate> LoadCustom()
    {
        try
        {
            var path = AppPaths.DocumentTemplatesPath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<DocumentTemplate>>(json, JsonOpts) ?? [];
            }
        }
        catch (Exception ex) { CrashLog.Write(ex, "DocumentTemplateStore.Load"); }
        return [];
    }

    public static void SaveCustom(IEnumerable<DocumentTemplate> templates)
    {
        try
        {
            File.WriteAllText(AppPaths.DocumentTemplatesPath,
                JsonSerializer.Serialize(templates.ToList(), JsonOpts));
        }
        catch (Exception ex) { CrashLog.Write(ex, "DocumentTemplateStore.Save"); }
    }
}
