using Avalonia.Input;
using Floss.App.Input;

namespace Floss.App.Tools;

internal static class SelectOpHelper
{
    public static SelectOp Resolve(SelectOp presetOp, ToolAuxOperationType auxMode)
        => auxMode switch
        {
            ToolAuxOperationType.AddToSelection => SelectOp.Add,
            ToolAuxOperationType.RemoveFromSelection => SelectOp.Subtract,
            ToolAuxOperationType.SelectFromSelection => SelectOp.Intersect,
            _ => presetOp
        };

    /// <summary>
    /// Resolves the effective selection op for the current gesture.
    /// Modifier-key settings take priority; Shift/Alt fall back when aux mode is unset.
    /// </summary>
    public static SelectOp ResolveForSelection(SelectOp presetOp, ToolContext ctx)
    {
        if (ctx.ActiveSelectionOp is { } locked)
            return locked;

        if (ctx.ToolAuxMode is ToolAuxOperationType.AddToSelection
            or ToolAuxOperationType.RemoveFromSelection
            or ToolAuxOperationType.SelectFromSelection)
            return Resolve(presetOp, ctx.ToolAuxMode);

        var mods = ctx.CurrentModifiers;
        if ((mods & (KeyModifiers.Shift | KeyModifiers.Alt)) == (KeyModifiers.Shift | KeyModifiers.Alt))
            return SelectOp.Intersect;
        if ((mods & KeyModifiers.Alt) != 0)
            return SelectOp.Subtract;
        if ((mods & KeyModifiers.Shift) != 0)
            return SelectOp.Add;

        return presetOp;
    }
}
