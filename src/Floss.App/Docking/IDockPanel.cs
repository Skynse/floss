using System;
using Avalonia.Controls;

namespace Floss.App.Docking;

/// <summary>
/// A panel that can be docked, floated, hidden, and rearranged within the workspace.
/// Each panel is identified by a unique string id and provides a factory for its content.
/// Sizing uses proportional values (0-1 share of available space, like Dock library).
/// </summary>
public interface IDockPanel
{
    string Id { get; }
    string Title { get; }
    bool AllowFloat { get; }
    bool AllowHide { get; }
    bool AllowClose { get; }

    /// <summary>Proportional share of column space (0–1). Used for star-sizing.</summary>
    double Proportion { get; }

    /// <summary>Minimum height in pixels.</summary>
    double MinHeight { get; }

    /// <summary>
    /// The default docking zone. Used by Normalize to place unplaced panels.
    /// "left" | "bottom" | "right-0" | "right-1" | etc.
    /// </summary>
    string DefaultZone { get; }

    Func<Control> BuildContent { get; }

    /// <summary>Optional: save panel-specific content state (scroll position, selection, etc.).</summary>
    object? SaveContentState { get; }

    /// <summary>Optional: restore panel-specific content state.</summary>
    object? RestoreContentState { get; }
}

/// <summary>
/// Lightweight default implementation of <see cref="IDockPanel"/>.
/// </summary>
public sealed record DockPanelDef(
    string Id,
    string Title,
    Func<Control> BuildContent,
    bool AllowFloat = true,
    bool AllowHide = true,
    bool AllowClose = false,
    double Proportion = 0.25,
    double MinHeight = 64,
    string DefaultZone = "right-0",
    object? SaveContentState = null,
    object? RestoreContentState = null) : IDockPanel;
