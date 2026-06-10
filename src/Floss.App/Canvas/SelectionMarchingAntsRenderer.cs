using Avalonia.Media;
using Floss.App.Tools;

namespace Floss.App.Canvas;

/// <summary>
/// Animated marching-ants selection border (CPU vector dashes).
/// </summary>
internal static class SelectionMarchingAntsRenderer
{
    public const float AntPhaseStepPx = 2f;

    public static void Draw(DrawingContext context, SelectionMask selection, double zoom, float phase)
    {
        if (!selection.HasSelection) return;
        selection.RenderMarchingAnts(context, zoom, phase);
    }
}
