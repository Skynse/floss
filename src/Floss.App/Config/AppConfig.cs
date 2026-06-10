using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Floss.App.Brushes;
using Floss.App.Diagnostics;
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

    /// <summary>Discord Rich Presence (desktop client must be running).</summary>
    public bool DiscordRichPresenceEnabled { get; set; } = true;

    /// <summary>Discord Developer Portal application ID. Falls back to FLOSS_DISCORD_APP_ID env var.</summary>
    public string? DiscordApplicationId { get; set; }
    public BrushCursorMode BrushCursorMode { get; set; } = BrushCursorMode.Outline;
    public BrushCursorMode PenCursorMode { get; set; } = BrushCursorMode.Outline;
    public BrushCursorMode EraserCursorMode { get; set; } = BrushCursorMode.Outline;
    public BrushCursorMode SmudgeCursorMode { get; set; } = BrushCursorMode.Outline;
    public bool ShowRulers { get; set; }

    // Smart Shape — global, applies to all DirectDraw tools.
    public bool SmartShapeEnabled { get; set; } = true;
    public double SmartShapeHoldSeconds { get; set; } = 0.70;
    public double SmartShapeHoldRadiusPx { get; set; } = 8;

    /// <summary>Shift+click point-to-point straight lines on brush tools.</summary>
    public bool ShiftStraightLineEnabled { get; set; } = true;

    public Dictionary<string, bool> ToolPropertyDockerVisibility { get; set; } = new();

    /// <summary>Plugin id → enabled. Missing key means enabled (default on).</summary>
    public Dictionary<string, bool> PluginEnabled { get; set; } = new();

    /// <summary>Shared default visibility for tool properties in the docker. Used by both the docker panel and ToolPropertiesWindow.</summary>
    public static bool GetToolPropertyDockerDefault(string propertyId) => propertyId switch
    {
        "brush.size" => true,
        "brush.hardness" => true,
        "brush.smoothing" => true,
        "paint.opacity" or "brush.opacity" => true,
        "assistant.snap" or "assistant.perspectiveMode" or "assistant.fisheye" or "assistant.fov" or "assistant.grid" => true,
        _ => false
    };

    /// <summary>Check whether a tool property should be visible in the docker. Considers both user override and defaults.</summary>
    public bool IsToolPropertyDockerVisible(string propertyId)
        => ToolPropertyDockerVisibility.TryGetValue(propertyId, out var v) ? v : GetToolPropertyDockerDefault(propertyId);

    public void SetToolPropertyDockerVisible(string propertyId, bool visible)
    {
        ToolPropertyDockerVisibility[propertyId] = visible;
        Save();
        NotifyToolPropertyVisibilityChanged();
    }

    public void ToggleToolPropertyDockerVisible(string propertyId)
        => SetToolPropertyDockerVisible(propertyId, !IsToolPropertyDockerVisible(propertyId));

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
                cfg.PurgeObsoleteWorkspacePresets();
                cfg.RepairToolPropertyVisibility();
                cfg.PruneRecentFiles();
                return cfg;
            }
        }
        catch (Exception ex) { CrashLog.Write(ex, "AppConfig.Load"); }
        return new AppConfig();
    }

    /// <summary>
    /// Drop stale auto-seeded presets that collide with factory reset and ship outdated layouts.
    /// </summary>
    public void PurgeObsoleteWorkspacePresets()
    {
        WorkspacePresets.Remove("default");
        WorkspacePresets.Remove("main");
    }

    /// <summary>
    /// Clean up stale tool property visibility entries that were written by the old buggy eye button.
    /// Also remove entries that match the default — let the default logic handle them.
    /// </summary>
    private void RepairToolPropertyVisibility()
    {
        if (ToolPropertyDockerVisibility == null || ToolPropertyDockerVisibility.Count == 0) return;
        var toRemove = new List<string>();
        foreach (var (key, val) in ToolPropertyDockerVisibility)
        {
            // If the stored value matches the default, remove it — let the default logic apply.
            // This fixes stale "true" entries that were no-ops under the old logic, and
            // stale "false" entries that were silently ignored.
            var def = GetToolPropertyDockerDefault(key);
            if (val == def)
                toRemove.Add(key);
        }
        foreach (var k in toRemove)
            ToolPropertyDockerVisibility.Remove(k);
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
            LogWorkspaceLayout("load/before-repair", cfg.WorkspaceLayout);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                cfg.WorkspaceLayout.RepairLayoutIntegrity();
                var orphan = cfg.WorkspaceLayout.FindOrphanedPanel();
                if (orphan == null)
                {
                    LogWorkspaceLayout("load/after-repair", cfg.WorkspaceLayout);
                    return;
                }

                CrashLog.WriteRaw($"[Layout] Orphan '{orphan}' on attempt {attempt + 1}; repairing");
            }

            var remaining = cfg.WorkspaceLayout.FindOrphanedPanel();
            if (remaining != null)
            {
                var msg =
                    $"[Layout] Still orphaned panel '{remaining}' after repair; keeping saved layout (not resetting).";
                CrashLog.WriteRaw(msg);
                Console.Error.WriteLine($"[Floss] {msg}");
            }

            LogWorkspaceLayout("load/after-repair", cfg.WorkspaceLayout);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "AppConfig.ValidateOrResetLayout");
        }
    }

    private static void LogWorkspaceLayout(string phase, WorkspaceLayout layout)
    {
        static string SummarizeColumn(DockColumnLayout col)
        {
            if (col.Rows is { Count: > 0 })
                return $"{col.Id}:rows={col.Rows.Count}";
            return $"{col.Id}:panels={col.PanelIds.Count},tabs={col.TabGroups.Count}";
        }

        var left = string.Join(", ", layout.LeftColumns.Select(SummarizeColumn));
        var right = string.Join(", ", layout.RightColumns.Select(SummarizeColumn));
        var bottom = string.Join(", ", layout.BottomColumns.Select(SummarizeColumn));
        CrashLog.WriteRaw(
            $"[Layout] {phase} v{layout.LayoutVersion} left=[{left}] right=[{right}] bottom=[{bottom}] hidden={layout.HiddenPanelIds.Count}");
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
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        var list = new List<string>(RecentFiles);
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 10)
            list.RemoveRange(10, list.Count - 10);
        RecentFiles = [.. list];
        Save();
    }

    /// <summary>Drop missing paths and de-duplicate (case-insensitive).</summary>
    public void PruneRecentFiles()
    {
        if (RecentFiles.Length == 0)
            return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(RecentFiles.Length);
        foreach (var path in RecentFiles)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(full))
                continue;
            if (!seen.Add(full))
                continue;
            kept.Add(full);
        }

        if (kept.Count != RecentFiles.Length)
        {
            RecentFiles = [.. kept];
            Save();
        }
    }
}
