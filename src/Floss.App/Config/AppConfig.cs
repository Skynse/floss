using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Floss.App.Brushes;
using Floss.App.Docking;

namespace Floss.App.Config;

public enum BrushCursorMode
{
    Outline,
    Dot,
    DotAndOutline,
    None,
    BrushShape
}

public sealed class AppConfig
{
    public int NewCanvasWidth { get; set; } = 2048;
    public int NewCanvasHeight { get; set; } = 2048;
    public int NewCanvasDpi { get; set; } = 72;
    public string NewCanvasBackground { get; set; } = "White";
    public string LastColor { get; set; } = "#111111";
    public string LastBrushName { get; set; } = "Round Sable";
    public string? LastToolGroupId { get; set; }
    public string? LastToolCategoryName { get; set; }
    public string? LastToolPresetId { get; set; }
    public double LastBrushSize { get; set; } = 8;
    public double LastBrushOpacity { get; set; } = 1.0;
    public double LastBrushHardness { get; set; } = 0.95;
    public double LastBrushSpacing { get; set; } = 0.10;
    public bool RecordTimelapse { get; set; } = false;
    public bool ShowRenderTelemetry { get; set; } = false;
    public BrushCursorMode BrushCursorMode { get; set; } = BrushCursorMode.Outline;
    public BrushCursorMode PenCursorMode { get; set; } = BrushCursorMode.Outline;
    public BrushCursorMode EraserCursorMode { get; set; } = BrushCursorMode.Outline;
    public BrushCursorMode SmudgeCursorMode { get; set; } = BrushCursorMode.Outline;
    public bool ShowRulers { get; set; }
    public Dictionary<string, bool> ToolPropertyDockerVisibility { get; set; } = new();
    public WorkspaceLayout WorkspaceLayout { get; set; } = WorkspaceLayout.CreateDefault();
    public Dictionary<string, WorkspaceLayout> WorkspacePresets { get; set; } = new();
    public static event Action? ToolPropertyVisibilityChanged;
    public static void NotifyToolPropertyVisibilityChanged() => ToolPropertyVisibilityChanged?.Invoke();
    public string[] RecentFiles { get; set; } = [];
    public List<BrushShapePreset> BrushShapePresets { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
            {
                var json = File.ReadAllText(AppPaths.ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                ValidateOrResetLayout(cfg);
                return cfg;
            }
        }
        catch (Exception ex) { CrashLog.Write(ex, "AppConfig.Load"); }
        return new AppConfig();
    }

    /// <summary>
    /// Checks the workspace layout for orphaned panels (e.g., from a tab-group dissolve bug).
    /// If corrupted, resets to default and sets <see cref="LayoutWasReset"/> so the UI can notify.
    /// </summary>
    private static void ValidateOrResetLayout(AppConfig cfg)
    {
        try
        {
            cfg.WorkspaceLayout ??= Docking.WorkspaceLayout.CreateDefault();
            var orphan = cfg.WorkspaceLayout.FindOrphanedPanel();
            if (orphan != null)
            {
                cfg.WorkspaceLayout = Docking.WorkspaceLayout.CreateDefault();
                cfg.WorkspaceLayout.Normalize(Docking.PanelRegistry.AllIds);
                cfg.LayoutWasReset = true;
                Console.Error.WriteLine($"[Floss] Workspace layout was corrupted (orphaned panel '{orphan}'), reset to defaults.");
            }
        }
        catch { /* If validation itself fails, ignore */ }
    }

    /// <summary>
    /// True if the workspace layout had to be reset due to corruption on load.
    /// Cleared after first check.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LayoutWasReset { get; set; }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) { CrashLog.Write(ex, "AppConfig.Save"); }
    }

    public void AddRecentFile(string path)
    {
        var list = new List<string>(RecentFiles);
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
        RecentFiles = [.. list];
    }
}
