using System;

namespace Floss.App.Tools;

public enum SelectMode { Rect, Lasso, PolylineLasso }
public enum GradientType { Linear, Radial }
public enum ShapeKind { Rectangle, Ellipse, Line }
public enum ShapeDrawMode { Fill, Stroke, FillAndStroke }

/// <summary>Object tool — which canvas object kinds are hit-testable.</summary>
[Flags]
public enum SelectableObjectFlags
{
    None = 0,
    Ruler = 1 << 0,
    Vector = 1 << 1,
    Model3D = 1 << 2,
    Raster = 1 << 3,
    Text = 1 << 4,
    Frame = 1 << 5,
    Gradient = 1 << 6,
    Fill = 1 << 7,
}

/// <summary>Object tool — how clicks update the object selection.</summary>
public enum ObjectSelectionMode
{
    Replace,
    Add,
    Remove,
}
