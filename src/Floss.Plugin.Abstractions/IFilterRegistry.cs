using System;
using System.Collections.Generic;

namespace Floss.App.Features;

/// <summary>
/// Registry for image filters. Plugins register filters that appear in the Filter menu
/// and can be applied to layers. Built-in filters also use this registry.
/// </summary>
public interface IFilterRegistry
{
    /// <summary>Register a filter. Use order 1000+ for plugins; 0-999 reserved for built-ins.</summary>
    void Register(FilterRegistration registration);

    /// <summary>Get all registered filters, ordered by registration order.</summary>
    IReadOnlyList<FilterRegistration> GetAll();

    /// <summary>Find a filter by id.</summary>
    FilterRegistration? Find(string id);
}

/// <summary>Describes a registered filter.</summary>
public sealed class FilterRegistration
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; } = "Filter";
    public int Order { get; init; } = 1000;
    public required Action<FilterContext> Execute { get; init; }
}

/// <summary>Context passed to a filter when it is executed.</summary>
public sealed class FilterContext
{
    /// <summary>The document being filtered.</summary>
    public required object Document { get; init; }

    /// <summary>The layer index to apply the filter to.</summary>
    public required int LayerIndex { get; init; }

    /// <summary>Whether there is an active selection to constrain the filter.</summary>
    public bool HasSelection { get; init; }
}
