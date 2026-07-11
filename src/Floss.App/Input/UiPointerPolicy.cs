using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Floss.App.Input;

/// <summary>
/// Painting-app input policy: pen/touch press-and-hold must not synthesize
/// right-click / context menus. That gesture fights ordinary taps (e.g. selecting
/// a brush). Real mouse right-click and pen barrel button still open menus.
/// </summary>
public static class UiPointerPolicy
{
    private static bool _penTipContact;

    /// <summary>
    /// Disable Avalonia hold gestures on a control tree root and suppress
    /// OS/Avalonia hold-derived context requests that started as pen tip contact.
    /// </summary>
    public static void ApplyTo(Control root)
    {
        InputElement.SetIsHoldingEnabled(root, false);

        root.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    /// <summary>Call when wiring a control that hosts a ContextMenu.</summary>
    public static void DisableHoldOn(InputElement element)
        => InputElement.SetIsHoldingEnabled(element, false);

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Pen)
            return;

        var props = e.GetCurrentPoint(null).Properties;
        // Tip contact starts as left; barrel/OS-hold often arrives as right from the start.
        _penTipContact = props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsEraser;
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Pen)
            _penTipContact = false;
    }

    private static void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Pen)
            _penTipContact = false;
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Hold-to-right-click after tip contact — block. Barrel button (right from press)
        // never sets _penTipContact, so menus still work.
        if (_penTipContact)
            e.Handled = true;
    }
}
