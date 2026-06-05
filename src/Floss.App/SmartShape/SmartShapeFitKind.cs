namespace Floss.App.SmartShape;

/// <summary>CSP launcher shape types — <see href="https://tips.clip-studio.com/en-us/articles/11934"/>.</summary>
public enum SmartShapeFitKind
{
    Auto,
    StraightLine,
    Polyline,
    Curve,
    ContinuousCurve,
    Triangle,
    EquilateralTriangle,
    Quadrilateral,
    Rectangle,
    Square,
    Ellipse,
    Circle,
    Polygon,
    RegularPolygon
}

public static class SmartShapeFitKindExtensions
{
    public static string Label(this SmartShapeFitKind kind) => kind switch
    {
        SmartShapeFitKind.Auto => "Auto",
        SmartShapeFitKind.StraightLine => "Line",
        SmartShapeFitKind.Polyline => "Polyline",
        SmartShapeFitKind.Curve => "Curve",
        SmartShapeFitKind.ContinuousCurve => "Continuous curve",
        SmartShapeFitKind.Triangle => "Triangle",
        SmartShapeFitKind.EquilateralTriangle => "Equilateral triangle",
        SmartShapeFitKind.Quadrilateral => "Quadrilateral",
        SmartShapeFitKind.Rectangle => "Rectangle",
        SmartShapeFitKind.Square => "Square",
        SmartShapeFitKind.Ellipse => "Ellipse",
        SmartShapeFitKind.Circle => "Circle",
        SmartShapeFitKind.Polygon => "Polygon",
        SmartShapeFitKind.RegularPolygon => "Regular polygon",
        _ => kind.ToString()
    };

    public static string IconPath(this SmartShapeFitKind kind) => kind switch
    {
        SmartShapeFitKind.StraightLine => Icons.LineVariant,
        SmartShapeFitKind.Polyline => Icons.VectorPolyline,
        SmartShapeFitKind.Curve or SmartShapeFitKind.ContinuousCurve => Icons.VectorCurve,
        SmartShapeFitKind.Circle => Icons.Circle,
        SmartShapeFitKind.Ellipse => Icons.EllipseOutline,
        SmartShapeFitKind.Rectangle or SmartShapeFitKind.Square or SmartShapeFitKind.Quadrilateral => Icons.RectangleOutline,
        SmartShapeFitKind.Triangle or SmartShapeFitKind.EquilateralTriangle => Icons.ShapeOutline,
        SmartShapeFitKind.Polygon or SmartShapeFitKind.RegularPolygon => Icons.ShapeOutline,
        _ => Icons.ShapeOutline
    };

    public static bool IsClosedKind(this SmartShapeFitKind kind) => kind switch
    {
        SmartShapeFitKind.Triangle or SmartShapeFitKind.EquilateralTriangle
            or SmartShapeFitKind.Quadrilateral or SmartShapeFitKind.Rectangle
            or SmartShapeFitKind.Square or SmartShapeFitKind.Ellipse
            or SmartShapeFitKind.Circle or SmartShapeFitKind.Polygon
            or SmartShapeFitKind.RegularPolygon => true,
        _ => false
    };

    /// <summary>Primary launcher slots (CSP: up to 3 quick picks beside detected type).</summary>
    public static SmartShapeFitKind[] PrimaryOptions(bool strokeClosed) => strokeClosed
        ?
        [
            SmartShapeFitKind.Circle,
            SmartShapeFitKind.Ellipse,
            SmartShapeFitKind.Rectangle
        ]
        :
        [
            SmartShapeFitKind.Curve,
            SmartShapeFitKind.Polyline,
            SmartShapeFitKind.StraightLine
        ];

    public static SmartShapeFitKind[] OverflowOptions(bool strokeClosed)
    {
        if (strokeClosed)
        {
            return
            [
                SmartShapeFitKind.Square,
                SmartShapeFitKind.Quadrilateral,
                SmartShapeFitKind.Triangle,
                SmartShapeFitKind.EquilateralTriangle,
                SmartShapeFitKind.Polygon,
                SmartShapeFitKind.RegularPolygon,
                SmartShapeFitKind.StraightLine,
                SmartShapeFitKind.Polyline,
                SmartShapeFitKind.Curve,
                SmartShapeFitKind.ContinuousCurve
            ];
        }

        return
        [
            SmartShapeFitKind.ContinuousCurve,
            SmartShapeFitKind.Triangle,
            SmartShapeFitKind.EquilateralTriangle,
            SmartShapeFitKind.Quadrilateral,
            SmartShapeFitKind.Rectangle,
            SmartShapeFitKind.Square,
            SmartShapeFitKind.Ellipse,
            SmartShapeFitKind.Circle,
            SmartShapeFitKind.Polygon,
            SmartShapeFitKind.RegularPolygon
        ];
    }
}
