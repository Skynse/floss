using System;
using Floss.App.Document;
using Floss.App.Processes;
using Floss.App.Tools;

namespace Floss.App.Features.Tools;

/// <summary>Optional tool factories tried before the default <see cref="ToolFactory"/>.</summary>
public interface IToolRegistry
{
    void RegisterFactory(Func<ToolRegistryContext, ITool?> factory, int order = 1000);

    ITool? TryCreate(ToolRegistryContext context);
}

public sealed class ToolRegistryContext
{
    public required ToolPreset Preset { get; init; }

    public required ToolFactory Factory { get; init; }

    public required DrawingDocument Document { get; init; }
}
