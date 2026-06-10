using System;

namespace Floss.App.Features;

/// <summary>Coarse document/canvas change notifications for plugins and dockers.</summary>
public interface IDocumentEvents
{
    /// <summary>Layers added, removed, or reordered.</summary>
    event Action? StructureChanged;

    event Action? SelectionChanged;

    /// <summary>Undo timeline changed (new edit, undo, redo, jump).</summary>
    event Action? HistoryChanged;

    /// <summary>Viewport pan, zoom, rotation, or flip changed.</summary>
    event Action? ViewportChanged;

    /// <summary>Painting assistants added, removed, or edited.</summary>
    event Action? AssistantsChanged;
}
