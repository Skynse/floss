using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes;

// Marker interface for the result of an input process.
public interface IProcessedInput { }

// Freehand stroke with raw and smoothed samples.
public sealed class StrokeInput : IProcessedInput
{
    public List<CanvasInputSample> RawSamples { get; set; } = [];
    public List<CanvasInputSample> SmoothedSamples { get; set; } = [];
}

// Polygon (closed or open) with raw and smoothed vertices.
public sealed class PolygonInput : IProcessedInput
{
    public List<CanvasInputSample> RawPoints { get; set; } = [];
    public List<CanvasInputSample> SmoothedPoints { get; set; } = [];
    public bool IsClosed { get; set; }
}

// Rectangle defined by drag start/end.
public sealed class RectInput : IProcessedInput
{
    public CanvasInputSample Start { get; set; }
    public CanvasInputSample End { get; set; }
}

// Single click/tap.
public sealed class ClickInput : IProcessedInput
{
    public CanvasInputSample Point { get; set; }
}

// Drag with start and current position.
public sealed class DragInput : IProcessedInput
{
    public CanvasInputSample Start { get; set; }
    public CanvasInputSample Current { get; set; }
}
