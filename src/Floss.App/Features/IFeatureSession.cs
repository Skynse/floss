using System;
using Floss.App.Canvas;
using Floss.App.Config;
using Floss.App.Document;

namespace Floss.App.Features;

/// <summary>
/// Stable entry point for feature modules — active document/view plus registered services.
/// New dockers depend on this, not on <c>MainWindow</c> partials or growing host properties.
/// </summary>
public interface IFeatureSession
{
    AppConfig Config { get; }

    /// <summary>Canvas for the active document tab.</summary>
    DrawingCanvas ActiveCanvas { get; }

    /// <summary>Shorthand for <see cref="ActiveCanvas"/>.Document.</summary>
    DrawingDocument ActiveDocument { get; }

    /// <summary>Viewport pan/zoom for navigator-style dockers.</summary>
    ICanvasViewHost View { get; }

    /// <summary>Fired after tab switch once canvas-bound services have been rebound.</summary>
    event Action? ActiveCanvasChanged;

    /// <summary>Registered capability (overview snapshots, history adapter, …).</summary>
    T GetService<T>() where T : class;

    /// <summary>Optional capability — returns null when the module or service was not registered.</summary>
    T? TryGetService<T>() where T : class;

    void RequestDockerRebuild();
}
