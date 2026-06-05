using System.Collections.Generic;

namespace Floss.App.SmartShape;

public readonly record struct Vec2(double X, double Y);

public abstract record SmartShapeModel(SmartShapeKind Kind);

public sealed record LineShape(Vec2 Start, Vec2 End) : SmartShapeModel(SmartShapeKind.Line);

public sealed record PolylineShape(IReadOnlyList<Vec2> Points) : SmartShapeModel(SmartShapeKind.Polyline);

public sealed record CurveShape(IReadOnlyList<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> Curves)
    : SmartShapeModel(SmartShapeKind.Curve);

public sealed record CircleShape(Vec2 Center, double Radius) : SmartShapeModel(SmartShapeKind.Circle);

public sealed record EllipseShape(Vec2 Center, double Rx, double Ry, double AngleDeg)
    : SmartShapeModel(SmartShapeKind.Ellipse);

public sealed record RectangleShape(Vec2 Center, double Width, double Height, double AngleDeg)
    : SmartShapeModel(SmartShapeKind.Rectangle);

public sealed record TriangleShape(IReadOnlyList<Vec2> Points) : SmartShapeModel(SmartShapeKind.Triangle);

public sealed record PolygonShape(IReadOnlyList<Vec2> Points) : SmartShapeModel(SmartShapeKind.Polygon);

public enum SmartShapePhase
{
    Idle,
    Drawing,
    Adjusting,
    /// <summary>Pointer up after hold — CSP launcher (Edit + shape types), no handles yet.</summary>
    Launcher,
    Gizmo
}
