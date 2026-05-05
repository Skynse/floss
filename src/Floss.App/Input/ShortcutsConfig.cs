using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace Floss.App.Input;

public enum GestureAxis { Vertical, Horizontal }

/// <summary>
/// All configurable shortcuts and canvas behaviour tuning.
/// Serialised to %AppData%/Floss/shortcuts.json as a human-readable JSON file
/// that can be hand-edited.
/// </summary>
public sealed class ShortcutsConfig
{
    // ── Sensitivity / behaviour tuning ────────────────────────────────────────

    /// Zoom multiplier per scroll-wheel tick (default 1.12 ≈ 12 % per click).
    public double ZoomScrollFactor { get; set; } = 1.12;

    /// Zoom multiplier for Zoom In / Zoom Out keys.
    public double ZoomKeyFactor { get; set; } = 1.20;

    /// Canvas rotation per key press in degrees.
    public double RotateKeyStep { get; set; } = 15.0;

    /// Brush size change per small nudge key press (px).
    public double BrushSizeStep { get; set; } = 2.0;

    /// Brush size change per large nudge key press (px).
    public double BrushSizeStepLarge { get; set; } = 10.0;

    /// Brush opacity change per key press (0-1).
    public double BrushOpacityStep { get; set; } = 0.05;

    /// Zoom change per pixel of pen drag (gesture zoom).
    /// Formula: zoom *= pow(GestureZoomSensitivity, axisDelta)
    public double GestureZoomSensitivity { get; set; } = 1.012;

    /// Which axis of pen movement controls zoom during the Zoom gesture.
    /// Vertical = drag up/down (up = zoom in), Horizontal = drag left/right (right = zoom in).
    public GestureAxis GestureZoomAxis { get; set; } = GestureAxis.Vertical;

    /// Canvas rotation per pixel of horizontal pen drag (gesture rotate, degrees/px).
    public double GestureRotateSensitivity { get; set; } = 0.40;

    /// Brush size change per pixel of horizontal pen drag (gesture size, px/px).
    public double GestureSizeSensitivity { get; set; } = 0.50;

    // ── File ──────────────────────────────────────────────────────────────────

    public KeyBinding FileNew { get; set; } = new(Key.N, KeyModifiers.Control);
    public KeyBinding FileOpen { get; set; } = new(Key.O, KeyModifiers.Control);
    public KeyBinding FileSave { get; set; } = new(Key.S, KeyModifiers.Control);
    public KeyBinding FileSaveAs { get; set; } = new(Key.S, KeyModifiers.Control | KeyModifiers.Shift);

    // ── Edit ──────────────────────────────────────────────────────────────────

    public KeyBinding Undo { get; set; } = new(Key.Z, KeyModifiers.Control);
    public KeyBinding Redo { get; set; } = new(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding RedoAlt { get; set; } = new(Key.Y, KeyModifiers.Control);
    public KeyBinding Copy { get; set; } = new(Key.C, KeyModifiers.Control);
    public KeyBinding Paste { get; set; } = new(Key.V, KeyModifiers.Control);
    public KeyBinding DeleteSelection { get; set; } = new(Key.Delete, KeyModifiers.None);

    // ── View — flip ───────────────────────────────────────────────────────────

    public KeyBinding FlipHorizontal { get; set; } = new(Key.H, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding FlipVertical { get; set; } = new(Key.V, KeyModifiers.Control | KeyModifiers.Shift);

    // ── View — mirror ─────────────────────────────────────────────────────────

    public KeyBinding MirrorHorizontal { get; set; } = new(Key.None);
    public KeyBinding MirrorVertical { get; set; } = new(Key.None);

    // ── View — zoom ───────────────────────────────────────────────────────────

    public KeyBinding ZoomIn { get; set; } = new(Key.Add, KeyModifiers.Control);
    public KeyBinding ZoomInAlt { get; set; } = new(Key.OemPlus, KeyModifiers.Control);
    public KeyBinding ZoomOut { get; set; } = new(Key.Subtract, KeyModifiers.Control);
    public KeyBinding ZoomReset { get; set; } = new(Key.D0, KeyModifiers.Control);
    public KeyBinding ZoomFit { get; set; } = new(Key.D0, KeyModifiers.Control | KeyModifiers.Shift);

    // ── View — rotate ─────────────────────────────────────────────────────────

    public KeyBinding RotateLeft { get; set; } = new(Key.OemOpenBrackets, KeyModifiers.Shift);
    public KeyBinding RotateRight { get; set; } = new(Key.OemCloseBrackets, KeyModifiers.Shift);
    public KeyBinding RotateReset { get; set; } = new(Key.R, KeyModifiers.Control | KeyModifiers.Shift);

    // ── Image — rotate canvas ─────────────────────────────────────────────────

    public KeyBinding RotateCanvas90Cw { get; set; } = new(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding RotateCanvas90Ccw { get; set; } = new(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding RotateCanvas180 { get; set; } = new(Key.D1, KeyModifiers.Control | KeyModifiers.Shift);

    // ── Selection ─────────────────────────────────────────────────────────────

    public KeyBinding SelectAll { get; set; } = new(Key.A, KeyModifiers.Control);
    public KeyBinding Deselect { get; set; } = new(Key.D, KeyModifiers.Control);
    public KeyBinding InvertSelect { get; set; } = new(Key.I, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding Transform { get; set; } = new(Key.T, KeyModifiers.Control);

    // ── Brush — size ──────────────────────────────────────────────────────────

    public KeyBinding BrushSizeDecrease { get; set; } = new(Key.OemOpenBrackets);
    public KeyBinding BrushSizeIncrease { get; set; } = new(Key.OemCloseBrackets);
    public KeyBinding BrushSizeDecreaseLarge { get; set; } = new(Key.OemOpenBrackets, KeyModifiers.Alt);
    public KeyBinding BrushSizeIncreaseLarge { get; set; } = new(Key.OemCloseBrackets, KeyModifiers.Alt);

    // ── Brush — opacity ───────────────────────────────────────────────────────

    public KeyBinding BrushOpacityDecrease { get; set; } = new(Key.OemComma);
    public KeyBinding BrushOpacityIncrease { get; set; } = new(Key.OemPeriod);

    // ── Color ─────────────────────────────────────────────────────────────────

    public KeyBinding ColorCycle { get; set; } = new(Key.X);
    public KeyBinding ColorDefault { get; set; } = new(Key.D);

    // ── Layers ────────────────────────────────────────────────────────────────

    public KeyBinding LayerNew { get; set; } = new(Key.N, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding LayerDuplicate { get; set; } = new(Key.J, KeyModifiers.Control);
    public KeyBinding LayerDelete { get; set; } = new(Key.Delete, KeyModifiers.Control);
    public KeyBinding LayerMoveUp { get; set; } = new(Key.Up, KeyModifiers.Control);
    public KeyBinding LayerMoveDown { get; set; } = new(Key.Down, KeyModifiers.Control);
    public KeyBinding LayerMerge { get; set; } = new(Key.E, KeyModifiers.Control);
    public KeyBinding LayerToggleColor { get; set; } = new(Key.B, KeyModifiers.Control);

    // ── Pen gestures — hold key + drag pen ───────────────────────────────────
    // Pan  : held key + any pen drag → translate canvas
    // Zoom : held key + drag up/down → zoom in or out
    // Rotate : held key + drag left/right → rotate canvas
    // BrushSize : held key + drag left/right → change brush size

    public KeyBinding GesturePan { get; set; } = new(Key.Space);
    public KeyBinding GestureZoom { get; set; } = new(Key.Z, KeyModifiers.Alt);
    public KeyBinding GestureRotate { get; set; } = new(Key.R, KeyModifiers.Alt);
    public KeyBinding GestureBrushSize { get; set; } = new(Key.None, KeyModifiers.Control | KeyModifiers.Alt);

    // ── Misc ──────────────────────────────────────────────────────────────────

    public KeyBinding OpenSettings { get; set; } = new(Key.OemComma, KeyModifiers.Control);
    public KeyBinding OpenBrushEditor { get; set; } = new(Key.B, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyBinding ToggleCanvasOnly { get; set; } = new(Key.Tab, KeyModifiers.None);
    public KeyBinding ToggleRulers { get; set; } = new(Key.R, KeyModifiers.Control);
    public KeyBinding AlternateInvocation { get; set; } = new(Key.LeftAlt, KeyModifiers.Alt);

    // ── Serialisation ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ShortcutsConfig Load()
    {
        try
        {
            var path = AppPaths.ShortcutsConfigPath;
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<ShortcutsConfig>(File.ReadAllText(path), JsonOpts) ?? new();
                config.RepairLegacyGestureBindings();
                return config;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.ShortcutsConfigPath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { }
    }

    private void RepairLegacyGestureBindings()
    {
        if (GestureBrushSize.IsModifierOnly && GestureBrushSize.Modifiers == KeyModifiers.Control)
        {
            GestureBrushSize = new KeyBinding(Key.None, KeyModifiers.Control | KeyModifiers.Alt);
        }
    }
}
