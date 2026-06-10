using System;
using Avalonia;
using Floss.App.Document;

namespace Floss.App.Features;

/// <summary>
/// Canvas view state for dockers that track or navigate the viewport (mini map, navigator, etc.).
/// Implemented by MainWindow; feature modules use this instead of reaching into viewport partials.
/// </summary>
public interface ICanvasViewHost
{
    bool HasDocument { get; }

    int DocumentWidth { get; }

    int DocumentHeight { get; }

    double Zoom { get; }

    double PanOffsetX { get; }

    double PanOffsetY { get; }

    double Rotation { get; }

    int FlipX { get; }

    int FlipY { get; }

    double ViewportWidth { get; }

    double ViewportHeight { get; }

    /// <summary>Visible document area in canvas pixel coordinates, if computable.</summary>
    PixelRegion? VisibleDocumentRegion { get; }

    /// <summary>Fired when pan, zoom, rotation, or document dimensions affecting the view change.</summary>
    event Action? ViewTransformChanged;

    /// <summary>Fired when document pixels, layers, or selection change (navigator / overview refresh).</summary>
    event Action? DocumentVisualChanged;

    void PanBy(double dx, double dy);

    void ZoomBy(double factor, Point viewportCenter);

    void ResetView();
}
