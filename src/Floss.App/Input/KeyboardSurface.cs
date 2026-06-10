using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Floss.App.Input;

/// <summary>
/// Wires pointer interaction on a visual surface to a <see cref="KeyboardInputScope"/>.
/// Press activates the region; move updates pointer hit-testing for keyboard routing.
/// </summary>
public static class KeyboardSurface
{
    public static void Wire(InputElement surface, KeyboardInputScope scope, KeyboardInputRegion region)
    {
        scope.RegisterSurface(surface, region);

        void Activate(object? _, PointerEventArgs e)
        {
            scope.Activate(region);
            scope.UpdatePointerVisual(e.Source as Visual ?? surface);
        }

        void TrackPointer(object? _, PointerEventArgs e)
            => scope.UpdatePointerVisual(e.Source as Visual ?? surface);

        surface.AddHandler(InputElement.PointerPressedEvent, Activate, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        surface.AddHandler(InputElement.PointerEnteredEvent, Activate, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        surface.AddHandler(InputElement.PointerMovedEvent, TrackPointer, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }
}
