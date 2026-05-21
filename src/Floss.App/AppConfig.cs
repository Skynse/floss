using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Floss.App.Brushes;

namespace Floss.App;

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
    public string LastBrushName { get; set; } = "Technical Pen";
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
    public bool ShowNodeGraphDock { get; set; }
    public double NodeGraphDockHeight { get; set; } = 320;
    public Dictionary<string, bool> ToolPropertyDockerVisibility { get; set; } = new();
    public WorkspaceLayoutConfig WorkspaceLayout { get; set; } = WorkspaceLayoutConfig.Default();
    public Dictionary<string, WorkspaceLayoutConfig> WorkspacePresets { get; set; } = new();
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
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch (Exception ex) { CrashLog.Write(ex, "AppConfig.Load"); }
        return new AppConfig();
    }

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

public sealed class WorkspaceLayoutConfig
{
    public double LeftRailWidth { get; set; } = 48;
    public double RightPanelWidth { get; set; } = 520;
    public double RightDockSplit { get; set; } = 0.5;
    public DockColumnConfig LeftColumn { get; set; } = new() { Id = "left" };
    public List<DockColumnConfig> RightColumns { get; set; } =
    [
        new() { Id = "right-0", Panels = ["brush", "tool-properties", "layer-properties", "layers"] },
        new() { Id = "right-1", Panels = ["color", "color-slider", "brush-size"] }
    ];
    public Dictionary<string, double> PanelHeights { get; set; } = new();
    public Dictionary<string, FloatingDockerConfig> FloatingPanels { get; set; } = new();
    public HashSet<string> HiddenDockers { get; set; } = [];

    public static WorkspaceLayoutConfig Default() => new();
    public WorkspaceLayoutConfig Clone()
        => JsonSerializer.Deserialize<WorkspaceLayoutConfig>(
            JsonSerializer.Serialize(this)) ?? Default();
}

public sealed class DockColumnConfig
{
    public string Id { get; set; } = "";
    public List<string> Panels { get; set; } = [];
}

public sealed class FloatingDockerConfig
{
    public bool IsFloating { get; set; }
    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 420;
}
