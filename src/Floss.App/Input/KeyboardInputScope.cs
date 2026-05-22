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
/// Tracks which workspace surface owns keyboard shortcuts. Pointer position on
/// registered surfaces is authoritative; keyboard focus is only used to detect
/// text entry and the popup node graph window.
/// </summary>
public sealed class KeyboardInputScope
{
    private readonly List<(InputElement Root, KeyboardInputRegion Region)> _surfaces = [];

    public KeyboardInputRegion ActiveRegion { get; private set; } = KeyboardInputRegion.Canvas;

    public void RegisterSurface(InputElement root, KeyboardInputRegion region)
    {
        _surfaces.RemoveAll(entry => ReferenceEquals(entry.Root, root));
        _surfaces.Add((root, region));
    }

    public void UnregisterSurface(InputElement root)
        => _surfaces.RemoveAll(entry => ReferenceEquals(entry.Root, root));

    public void Activate(KeyboardInputRegion region)
        => ActiveRegion = region;

    public void ActivateFromVisual(Visual? hit)
    {
        if (hit == null) return;
        ActiveRegion = ResolveRegion(hit);
    }

    public KeyboardInputRegion ResolveRegion(Visual? hit)
    {
        for (var current = hit; current != null; current = current.GetVisualParent())
        {
            foreach (var (root, region) in _surfaces)
            {
                if (ReferenceEquals(current, root))
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

    public bool ShouldRouteToCanvas(IInputElement? focused)
        => ActiveRegion == KeyboardInputRegion.Canvas
           && !IsTextEntryFocused(focused)
           && !IsPopupNodeGraphFocused(focused);

    public bool ShouldRouteToNodeGraph(IInputElement? focused)
        => ActiveRegion == KeyboardInputRegion.NodeGraph
           && !IsTextEntryFocused(focused);
}
