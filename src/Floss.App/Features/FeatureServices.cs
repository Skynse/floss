#nullable enable
using System;
using System.Collections.Generic;
using Floss.App.Canvas;

namespace Floss.App.Features;

/// <summary>Typed service registry for feature modules (navigator overview, history, future histogram, etc.).</summary>
public sealed class FeatureServices
{
    private readonly Dictionary<Type, object> _services = new();
    private readonly List<ICanvasBoundService> _canvasBound = [];

    public void Register<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _services[typeof(T)] = instance;
        if (instance is ICanvasBoundService bound)
            _canvasBound.Add(bound);
    }

    public T Get<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out var instance))
            return (T)instance;

        throw new InvalidOperationException(
            $"No feature service registered for {typeof(T).Name}. Register it in FeatureSessionBootstrap.");
    }

    public T? TryGet<T>() where T : class
        => _services.TryGetValue(typeof(T), out var instance) ? (T)instance : null;

    /// <summary>Rebind all canvas-bound services after a tab switch.</summary>
    public void NotifyActiveCanvas(DrawingCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        foreach (var service in _canvasBound)
            service.BindCanvas(canvas);
    }
}
