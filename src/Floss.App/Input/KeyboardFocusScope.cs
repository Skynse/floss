using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Floss.App.Brushes;

namespace Floss.App.Input;

/// <summary>
/// Determines whether keyboard shortcuts should route to the canvas/viewport
/// or stay with the focused editor control.
/// </summary>
public static class KeyboardFocusScope
{
    public static bool IsTextEntryFocused(IInputElement? focused)
        => focused is TextBox or ComboBox;

    public static bool IsWithinNodeGraphEditor(IInputElement? focused)
    {
        if (focused is not Visual visual)
            return false;

        for (var current = visual; current != null; current = current.GetVisualParent())
        {
            if (current is NodeGraphView or NodeGraphEditorPanel or NodeGraphEditorWindow)
                return true;
        }

        return false;
    }

    public static bool ShouldRouteToCanvas(IInputElement? focused)
        => !IsTextEntryFocused(focused) && !IsWithinNodeGraphEditor(focused);
}
