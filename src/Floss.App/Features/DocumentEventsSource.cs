using System;
using Avalonia.Threading;
using Floss.App.Canvas;

namespace Floss.App.Features;

/// <summary>Canvas-bound document events. Follows tab switches via <see cref="BindCanvas"/>.</summary>
public sealed class DocumentEventsSource : IDocumentEvents, ICanvasBoundService
{
    private DrawingCanvas _canvas;
    private readonly ICanvasViewHost? _view;

    public DocumentEventsSource(DrawingCanvas canvas, ICanvasViewHost? viewHost = null)
    {
        _canvas = canvas;
        _view = viewHost;
        WireCanvas();
        if (_view != null)
            _view.ViewTransformChanged += OnViewportChanged;
    }

    public event Action? StructureChanged;

    public event Action? SelectionChanged;

    public event Action? HistoryChanged;

    public event Action? ViewportChanged;

    public event Action? AssistantsChanged;

    public void BindCanvas(DrawingCanvas canvas)
    {
        if (ReferenceEquals(_canvas, canvas))
            return;

        UnwireCanvas();
        _canvas = canvas;
        WireCanvas();
    }

    private void WireCanvas()
    {
        _canvas.LayersChanged += OnStructureChanged;
        _canvas.SelectionChanged += OnSelectionChanged;
        _canvas.HistoryChanged += OnHistoryChanged;
        _canvas.AssistantsChanged += OnAssistantsChanged;
    }

    private void UnwireCanvas()
    {
        _canvas.LayersChanged -= OnStructureChanged;
        _canvas.SelectionChanged -= OnSelectionChanged;
        _canvas.HistoryChanged -= OnHistoryChanged;
        _canvas.AssistantsChanged -= OnAssistantsChanged;
    }

    private void OnStructureChanged(object? sender, EventArgs e) => Raise(StructureChanged);

    private void OnSelectionChanged(object? sender, EventArgs e) => Raise(SelectionChanged);

    private void OnHistoryChanged(object? sender, EventArgs e) => Raise(HistoryChanged);

    private void OnAssistantsChanged(object? sender, EventArgs e) => Raise(AssistantsChanged);

    private void OnViewportChanged() => Raise(ViewportChanged);

    private static void Raise(Action? handler)
    {
        if (handler == null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            handler();
        else
            Dispatcher.UIThread.Invoke(handler);
    }
}
