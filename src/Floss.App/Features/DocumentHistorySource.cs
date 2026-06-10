using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Floss.App.Canvas;
using Floss.App.Document;

namespace Floss.App.Features;

/// <summary>Undo timeline for the active canvas. Follows tab switches via <see cref="BindCanvas"/>.</summary>
public sealed class DocumentHistorySource : IDocumentHistorySource, ICanvasBoundService
{
    private DrawingCanvas _canvas;
    private DrawingDocument _document;

    public DocumentHistorySource(DrawingCanvas canvas)
    {
        _canvas = canvas;
        _document = canvas.Document;
        Wire();
    }

    public event Action? Changed;

    public bool HasDocument => _canvas.HasDocument;

    public IReadOnlyList<DocumentHistoryEntry> Entries => _document.HistoryEntries;

    public int CurrentIndex => _document.HistoryIndex;

    public bool CanUndo => _canvas.CanUndo;

    public bool CanRedo => _canvas.CanRedo;

    public void Undo() => _canvas.Undo();

    public void Redo() => _canvas.Redo();

    public bool JumpTo(int index) => _canvas.JumpToHistoryIndex(index);

    public void BindCanvas(DrawingCanvas canvas)
    {
        if (ReferenceEquals(_canvas, canvas))
            return;

        Unwire();
        _canvas = canvas;
        _document = canvas.Document;
        Wire();
        RaiseChanged();
    }

    private void Wire() => _canvas.HistoryChanged += OnHistoryChanged;

    private void Unwire() => _canvas.HistoryChanged -= OnHistoryChanged;

    private void OnHistoryChanged(object? sender, EventArgs e) => RaiseChanged();

    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Changed?.Invoke();
        else
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }
}
