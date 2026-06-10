using Avalonia;

namespace Floss.App.Tools;

public interface IViewportController
{
    double Zoom { get; }
    double PanOffsetX { get; }
    double PanOffsetY { get; }
    void PanBy(double dx, double dy);
    void ZoomBy(double factor, Point viewportCenter);
    void RotateBy(double degrees);
}
