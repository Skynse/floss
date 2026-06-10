using System;
using System.Collections.Generic;

namespace Floss.App.Document.Assistants;

public sealed class AssistantTypeDescriptor
{
    public required string TypeId { get; init; }

    public required string Name { get; init; }

    public required int HandleCount { get; init; }
}

/// <summary>Built-in assistant types (extensible via registration).</summary>
public static class AssistantTypeRegistry
{
    private static readonly List<AssistantTypeDescriptor> Types =
    [
        new() { TypeId = PaintingAssistant.RulerType, Name = "Ruler", HandleCount = 2 },
        new() { TypeId = PaintingAssistant.PerspectiveType, Name = "Perspective", HandleCount = 4 },
    ];

    public static IReadOnlyList<AssistantTypeDescriptor> All => Types;

    public static AssistantTypeDescriptor? TryGet(string? typeId)
        => Types.Find(t => string.Equals(t.TypeId, typeId, StringComparison.Ordinal));

    public static void Register(AssistantTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var index = Types.FindIndex(t => t.TypeId == descriptor.TypeId);
        if (index >= 0)
            Types[index] = descriptor;
        else
            Types.Add(descriptor);
    }
}
