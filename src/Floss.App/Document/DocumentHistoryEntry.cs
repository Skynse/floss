namespace Floss.App.Document;

/// <summary>One step on the document undo timeline (for the history docker).</summary>
public readonly record struct DocumentHistoryEntry(long StateId, string Label, bool IsSaved);
