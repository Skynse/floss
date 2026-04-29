using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Document;

public sealed class DrawingDocument
{
    public const int CanvasWidth = 2048;
    public const int CanvasHeight = 2048;
    public static readonly Color PaperColor = Color.Parse("#f7f4ed");

    private readonly List<DrawingLayer> _layers = [];
    private readonly Stack<DocumentSnapshot> _undo = new();
    private readonly Stack<DocumentSnapshot> _redo = new();

    public DrawingDocument()
    {
        _layers.Add(new DrawingLayer("Layer 1", CanvasWidth, CanvasHeight));
        ActiveLayerIndex = 0;
    }

    public event EventHandler? Changed;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;

    public IReadOnlyList<DrawingLayer> Layers => _layers;
    public int ActiveLayerIndex { get; private set; }
    public DrawingLayer ActiveLayer => _layers[ActiveLayerIndex];
    public int CommittedStrokeCount { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool CanDeleteLayer => _layers.Count > 1;

    public void BeginDocumentMutation()
    {
        _undo.Push(CaptureSnapshot());
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanPaintActiveLayer => !ActiveLayer.IsLocked && ActiveLayer.IsVisible;

    public void CommitStroke()
    {
        CommittedStrokeCount += 1;
        NotifyChanged();
    }

    public void ClearActiveLayer(bool pushHistory = true)
    {
        if (pushHistory)
        {
            BeginDocumentMutation();
        }

        ActiveLayer.Clear();
        CommittedStrokeCount = 0;
        NotifyChanged();
    }

    public void AddLayer()
    {
        BeginDocumentMutation();
        var insertIndex = ActiveLayerIndex + 1;
        _layers.Insert(insertIndex, new DrawingLayer($"Layer {_layers.Count + 1}", CanvasWidth, CanvasHeight));
        ActiveLayerIndex = insertIndex;
        NotifyLayersChanged();
    }

    public void DuplicateActiveLayer()
    {
        BeginDocumentMutation();
        var source = ActiveLayer;
        var copy = new DrawingLayer($"{source.Name} Copy", CanvasWidth, CanvasHeight)
        {
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            Opacity = source.Opacity
        };
        copy.RestorePixels(source.CapturePixels());
        _layers.Insert(ActiveLayerIndex + 1, copy);
        ActiveLayerIndex += 1;
        NotifyLayersChanged();
    }

    public void DeleteActiveLayer()
    {
        if (_layers.Count <= 1) return;
        BeginDocumentMutation();
        _layers.RemoveAt(ActiveLayerIndex);
        ActiveLayerIndex = Math.Clamp(ActiveLayerIndex, 0, _layers.Count - 1);
        NotifyLayersChanged();
    }

    public void SelectLayer(int index)
    {
        if (index < 0 || index >= _layers.Count || index == ActiveLayerIndex) return;
        ActiveLayerIndex = index;
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleLayerVisibility(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        BeginDocumentMutation();
        _layers[index].IsVisible = !_layers[index].IsVisible;
        NotifyLayersChanged();
    }

    public void ToggleLayerLock(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        BeginDocumentMutation();
        _layers[index].IsLocked = !_layers[index].IsLocked;
        NotifyLayersChanged();
    }

    public void SetActiveLayerOpacity(double opacity)
    {
        var clamped = Math.Clamp(opacity, 0, 1);
        if (Math.Abs(ActiveLayer.Opacity - clamped) < 0.001) return;
        ActiveLayer.Opacity = clamped;
        NotifyLayersChanged();
    }

    public void MoveActiveLayer(int delta)
    {
        var next = ActiveLayerIndex + delta;
        if (next < 0 || next >= _layers.Count) return;
        BeginDocumentMutation();
        var layer = ActiveLayer;
        _layers.RemoveAt(ActiveLayerIndex);
        _layers.Insert(next, layer);
        ActiveLayerIndex = next;
        NotifyLayersChanged();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CaptureSnapshot());
        RestoreSnapshot(_undo.Pop());
        CommittedStrokeCount = Math.Max(0, CommittedStrokeCount - 1);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CaptureSnapshot());
        RestoreSnapshot(_redo.Pop());
        CommittedStrokeCount += 1;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyLayersChanged()
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private DocumentSnapshot CaptureSnapshot()
    {
        return new DocumentSnapshot(
            ActiveLayerIndex,
            _layers.Select(layer => new LayerSnapshot(
                layer.Name,
                layer.IsVisible,
                layer.IsLocked,
                layer.Opacity,
                layer.CapturePixels())).ToArray());
    }

    private void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        _layers.Clear();
        foreach (var layerSnapshot in snapshot.Layers)
        {
            var layer = new DrawingLayer(layerSnapshot.Name, CanvasWidth, CanvasHeight)
            {
                IsVisible = layerSnapshot.IsVisible,
                IsLocked = layerSnapshot.IsLocked,
                Opacity = layerSnapshot.Opacity
            };
            layer.RestorePixels(layerSnapshot.Pixels);
            _layers.Add(layer);
        }

        ActiveLayerIndex = Math.Clamp(snapshot.ActiveLayerIndex, 0, _layers.Count - 1);
        LayersChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record DocumentSnapshot(int ActiveLayerIndex, LayerSnapshot[] Layers);

    private sealed record LayerSnapshot(
        string Name,
        bool IsVisible,
        bool IsLocked,
        double Opacity,
        byte[] Pixels);
}
