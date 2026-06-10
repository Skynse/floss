using System;
using System.Collections.Generic;
using Floss.App.Document;

namespace Floss.App.Features;

/// <summary>
/// Undo timeline for the active document. Implemented by <see cref="DocumentHistorySource"/>.
/// </summary>
public interface IDocumentHistorySource
{
    event Action? Changed;

    bool HasDocument { get; }

    IReadOnlyList<DocumentHistoryEntry> Entries { get; }

    int CurrentIndex { get; }

    bool CanUndo { get; }

    bool CanRedo { get; }

    void Undo();

    void Redo();

    /// <summary>Jump to a timeline index (0 = document origin). Returns false when blocked (e.g. active stroke).</summary>
    bool JumpTo(int index);
}
