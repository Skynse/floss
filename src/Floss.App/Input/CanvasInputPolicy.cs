using Floss.App.Config;

namespace Floss.App.Input;

/// <summary>
/// Defines how keyboard modifiers and temporary tool swaps interact with the active canvas mode.
/// Normal painting uses the active preset's modifier table; transient edit sessions (transform,
/// smart-shape preview, future modal tools) use general modifiers only and allow viewport nav overlays.
/// </summary>
public readonly struct CanvasInputPolicy
{
    public bool IsTransientEdit { get; init; }
    public bool BlocksPrimaryToolPointer { get; init; }
    public ToolKind ModifierToolKind { get; init; }

    public static CanvasInputPolicy ForActivePreset(ToolKind kind) => new()
    {
        IsTransientEdit = false,
        ModifierToolKind = kind,
    };

    public static CanvasInputPolicy TransientEdit { get; } = new()
    {
        IsTransientEdit = true,
        ModifierToolKind = default,
    };

    /// <summary>Live filter preview: navigate viewport, no brush/transform pointer dispatch.</summary>
    public static CanvasInputPolicy FilterPreview { get; } = new()
    {
        IsTransientEdit = true,
        BlocksPrimaryToolPointer = true,
        ModifierToolKind = default,
    };

    public bool AllowsModifierAction(ModifierKeyAssignment assignment)
    {
        if (!IsTransientEdit)
            return true;

        return assignment.Action == ModifierAction.ChangeToolTemporarily
            && ToolGroupConfig.IsViewportNavigationPreset(assignment.TemporaryToolPresetId);
    }

    public bool AllowsTemporaryPreset(string presetId)
    {
        if (!IsTransientEdit)
            return true;

        return ToolGroupConfig.IsViewportNavigationPreset(presetId);
    }

    /// <summary>Commit modal tool before swapping to a temporary preset (eyedropper, move layer, etc.).</summary>
    public bool ShouldCommitPendingOnTemporaryPresetPush => !IsTransientEdit;

    /// <summary>Commit temporary tool before restoring the base preset (eyedropper pick without pointer-up).</summary>
    public bool ShouldCommitPendingOnTemporaryPresetPop => !IsTransientEdit;

    /// <summary>Commit modal tool when releasing alternate-invocation modifier.</summary>
    public bool ShouldCommitPendingOnAlternateDeactivate => !IsTransientEdit;
}
