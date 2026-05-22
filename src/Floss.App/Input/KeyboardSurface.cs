using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Floss.App.Input;

/// <summary>
/// Wires pointer interaction on a visual surface to a <see cref="KeyboardInputScope"/>.
/// </summary>
public static class KeyboardSurface
{
    public static void Wire(InputElement surface, KeyboardInputScope scope, KeyboardInputRegion region)
    {
        scope.RegisterSurface(surface, region);

        void Activate(object? _, PointerEventArgs __) => scope.Activate(region);

        surface.AddHandler(InputElement.PointerEnteredEvent, Activate, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        surface.AddHandler(InputElement.PointerPressedEvent, Activate, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        surface.AddHandler(InputElement.PointerMovedEvent, Activate, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }
}
