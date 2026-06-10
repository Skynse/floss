using System;
using System.Collections.Generic;
using Floss.App.Tools;

namespace Floss.App.Features.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly List<(int Order, Func<ToolRegistryContext, ITool?> Factory)> _factories = [];

    public void RegisterFactory(Func<ToolRegistryContext, ITool?> factory, int order = 1000)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add((order, factory));
        _factories.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public ITool? TryCreate(ToolRegistryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var (_, factory) in _factories)
        {
            var tool = factory(context);
            if (tool != null)
                return tool;
        }

        return null;
    }
}
