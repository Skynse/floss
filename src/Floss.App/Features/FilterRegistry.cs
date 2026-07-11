using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Floss.App.Features;

/// <summary>Concrete implementation of IFilterRegistry.</summary>
internal sealed class FilterRegistry : IFilterRegistry
{
    private readonly Dictionary<string, FilterRegistration> _filters = new(StringComparer.Ordinal);

    public void Register(FilterRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrEmpty(registration.Id);

        if (_filters.TryAdd(registration.Id, registration) == false)
            Trace.WriteLine($"[Floss] Filter id '{registration.Id}' overwritten by new registration");
    }

    public IReadOnlyList<FilterRegistration> GetAll()
        => _filters.Values
            .OrderBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.Order)
            .ThenBy(f => f.DisplayName, StringComparer.Ordinal)
            .ToList();

    public FilterRegistration? Find(string id)
        => _filters.TryGetValue(id, out var f) ? f : null;
}
