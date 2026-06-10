using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Floss.App.Brushes;

namespace Floss.App.Input;

public enum KeyboardInputRegion
{
    Chrome,
    Canvas,
    NodeGraph
}

/// <summary>
/// Routes keyboard shortcuts to canvas vs node graph. Pointer position wins over
/// stale keyboard focus so Space-pan works with the cursor over the graph dock
/// without stealing canvas shortcuts while the pen is on the canvas.
/// </summary>
public sealed class KeyboardInputScope
{
    private readonly List<(InputElement Root, KeyboardInputRegion Region)> _surfaces = [];

    public KeyboardInputRegion ActiveRegion { get; private set; } = KeyboardInputRegion.Canvas;
    private Visual? _pointerVisual;

    public void RegisterSurface(InputElement root, KeyboardInputRegion region)
    {
        _surfaces.RemoveAll(entry => ReferenceEquals(entry.Root, root));
        _surfaces.Add((root, region));
    }

    public void UnregisterSurface(InputElement root)
        => _surfaces.RemoveAll(entry => ReferenceEquals(entry.Root, root));

    public void Activate(KeyboardInputRegion region)
        => ActiveRegion = region;

    public void UpdatePointerVisual(Visual? hit)
        => _pointerVisual = hit;

    public KeyboardInputRegion ResolveRegion(Visual? hit)
    {
        for (var current = hit; current != null; current = current.GetVisualParent())
        {
            foreach (var (root, region) in _surfaces)
            {
                if (!ReferenceEquals(current, root))
                    continue;
                if (!root.IsVisible)
                    continue;
                return region;
            }
        }

        return KeyboardInputRegion.Chrome;
    }

    public static bool IsTextEntryFocused(IInputElement? focused)
        => focused is TextBox or ComboBox;

    public static bool IsPopupNodeGraphFocused(IInputElement? focused)
    {
        if (focused is not Visual visual)
            return false;

        for (var current = visual; current != null; current = current.GetVisualParent())
        {
            if (current is NodeGraphEditorWindow)
                return true;
        }

        return false;
    }

    public bool ShouldRouteToNodeGraph(IInputElement? focused)
    {
        if (IsTextEntryFocused(focused))
            return false;
        if (IsPopupNodeGraphFocused(focused))
            return true;

        return ResolveEffectiveRegion(focused) == KeyboardInputRegion.NodeGraph;
    }

    public bool ShouldRouteToCanvas(IInputElement? focused)
    {
        if (IsTextEntryFocused(focused))
            return false;
        if (IsPopupNodeGraphFocused(focused))
            return false;

        return ResolveEffectiveRegion(focused) == KeyboardInputRegion.Canvas;
    }

    private KeyboardInputRegion ResolveEffectiveRegion(IInputElement? focused)
    {
        if (_pointerVisual != null)
        {
            var pointerRegion = ResolveRegion(_pointerVisual);
            if (pointerRegion is KeyboardInputRegion.Canvas or KeyboardInputRegion.NodeGraph)
                return pointerRegion;
        }

        if (focused is Visual focusedVisual)
        {
            var focusRegion = ResolveRegion(focusedVisual);
            if (focusRegion is KeyboardInputRegion.Canvas or KeyboardInputRegion.NodeGraph)
                return focusRegion;
        }

        return ActiveRegion;
    }
}
