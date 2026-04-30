using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Document;

public sealed class DrawingDocument
{
    public static readonly Color PaperColor = Color.Parse("#f7f4ed");

    private readonly List<DrawingLayer> _layers = [];
    private readonly Stack<IHistoryState> _undo = new();
    private readonly Stack<IHistoryState> _redo = new();

    public DrawingDocument(int width = 2048, int height = 2048)
    {
        Width = width;
        Height = height;
        _layers.Add(new DrawingLayer("Layer 1", width, height));
        ActiveLayerIndex = 0;
    }

    public event EventHandler<DocumentChangedEventArgs>? Changed;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;
    public event EventHandler<LayerMetadataChangedEventArgs>? LayerMetadataChanged;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public IReadOnlyList<DrawingLayer> Layers => _layers;
    public int ActiveLayerIndex { get; private set; }
    public DrawingLayer ActiveLayer => _layers[ActiveLayerIndex];
    public int CommittedStrokeCount { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool CanDeleteLayer => _layers.Count > 1;

    public void ResizeForImport(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void BeginDocumentMutation()
    {
        _undo.Push(new SnapshotHistoryState(CaptureSnapshot()));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CommitLayerRegionMutation(int layerIndex, IReadOnlyList<LayerRegionPatch> patches, PixelRegion dirtyRegion)
    {
        if (patches.Count == 0 || dirtyRegion.IsEmpty) return;
        _undo.Push(new LayerRegionHistoryState(layerIndex, patches.ToArray(), dirtyRegion));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        NotifyChanged(dirtyRegion, layerIndex);
    }

    public void ClearForImport()
    {
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        _undo.Clear();
        _redo.Clear();
        CommittedStrokeCount = 0;
    }

    public DrawingLayer AddLayerForImport(
        string name,
        bool isGroup = false,
        int? bitmapWidth = null,
        int? bitmapHeight = null)
    {
        var layer = CreateLayerForImport(name, isGroup, bitmapWidth, bitmapHeight);
        _layers.Add(layer);
        return layer;
    }

    public DrawingLayer CreateLayerForImport(
        string name,
        bool isGroup = false,
        int? bitmapWidth = null,
        int? bitmapHeight = null)
        => new(name, bitmapWidth ?? Width, bitmapHeight ?? Height)
        {
            IsGroup = isGroup
        };

    public void AppendLayerForImport(DrawingLayer layer) => _layers.Add(layer);

    public void FinalizeImport()
    {
        ActiveLayerIndex = _layers.Count > 0 ? 0 : -1;
        NotifyLayersChanged();
    }

    public bool CanPaintActiveLayer => !ActiveLayer.IsLocked && ActiveLayer.IsVisible && !ActiveLayer.IsGroup;

    public void CommitStroke()
    {
        CommittedStrokeCount += 1;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearActiveLayer(bool pushHistory = true)
    {
        if (pushHistory)
        {
            BeginDocumentMutation();
        }

        ActiveLayer.Clear();
        CommittedStrokeCount = 0;
        NotifyChanged(null, ActiveLayerIndex);
    }

    public void AddLayer()
    {
        BeginDocumentMutation();
        var insertIndex = ActiveLayerIndex + 1;
        _layers.Insert(insertIndex, new DrawingLayer($"Layer {_layers.Count + 1}", Width, Height));
        ActiveLayerIndex = insertIndex;
        NotifyLayersChanged();
    }

    public void DuplicateActiveLayer()
    {
        BeginDocumentMutation();
        var source = ActiveLayer;
        var copy = new DrawingLayer(
            $"{source.Name} Copy",
            source.Width,
            source.Height)
        {
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY
        };
        copy.RestorePixels(source.CapturePixels());
        _layers.Insert(ActiveLayerIndex + 1, copy);
        ActiveLayerIndex += 1;
        NotifyLayersChanged();
    }

    public void DeleteActiveLayer()
    {
        if (_layers.Count <= 1) return;
        BeginDocumentMutation(); // captures pixels as bytes before we dispose
        var removed = _layers[ActiveLayerIndex];
        _layers.RemoveAt(ActiveLayerIndex);
        removed.Dispose();
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
        var oldValue = _layers[index].IsVisible;
        var newValue = !oldValue;
        var dirtyRegion = LayerDirtyRegion(index);
        _undo.Push(new LayerPropertyHistoryState<bool>(
            index,
            oldValue,
            newValue,
            (layer, value) => layer.IsVisible = value,
            true,
            dirtyRegion));
        _redo.Clear();
        _layers[index].IsVisible = newValue;
        NotifyLayerMetadataChanged(dirtyRegion, index);
    }

    public void ToggleLayerLock(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsLocked;
        var newValue = !oldValue;
        _undo.Push(new LayerPropertyHistoryState<bool>(
            index,
            oldValue,
            newValue,
            (layer, value) => layer.IsLocked = value,
            false,
            PixelRegion.Empty));
        _redo.Clear();
        _layers[index].IsLocked = newValue;
        NotifyLayerMetadataChanged(null, index);
    }

    public void SetActiveLayerOpacity(double opacity)
    {
        var clamped = Math.Clamp(opacity, 0, 1);
        if (Math.Abs(ActiveLayer.Opacity - clamped) < 0.001) return;
        ActiveLayer.Opacity = clamped;
        NotifyLayerMetadataChanged(LayerDirtyRegion(ActiveLayerIndex), ActiveLayerIndex);
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
        var state = _undo.Pop();
        _redo.Push(state.CaptureRedo(this));
        state.Restore(this);
        CommittedStrokeCount = Math.Max(0, CommittedStrokeCount - 1);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var state = _redo.Pop();
        _undo.Push(state.CaptureRedo(this));
        state.Restore(this);
        CommittedStrokeCount += 1;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyChanged(PixelRegion? dirtyRegion = null, int? layerIndex = null)
    {
        Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex));
    }

    private void NotifyLayersChanged()
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, new DocumentChangedEventArgs(null, null));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyLayerMetadataChanged(PixelRegion? dirtyRegion, int? layerIndex)
    {
        if (layerIndex is { } index)
            LayerMetadataChanged?.Invoke(this, new LayerMetadataChangedEventArgs(index));
        if (dirtyRegion is { IsEmpty: false })
            Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private PixelRegion LayerDirtyRegion(int index)
    {
        if (index < 0 || index >= _layers.Count) return PixelRegion.Empty;
        var bounds = _layers[index].DocumentContentBounds.ClipTo(Width, Height);
        return bounds;
    }

    private DocumentSnapshot CaptureSnapshot()
    {
        return new DocumentSnapshot(
            Width,
            Height,
            ActiveLayerIndex,
            _layers.Select(layer => new LayerSnapshot(
                layer.Name,
                layer.IsVisible,
                layer.IsLocked,
                layer.Opacity,
                layer.BlendMode,
                layer.OffsetX,
                layer.OffsetY,
                layer.IsGroup,
                layer.IsOpen,
                layer.IsClipping,
                layer.IndentLevel,
                layer.Parent is null ? -1 : _layers.IndexOf(layer.Parent),
                layer.Width,
                layer.Height,
                layer.CapturePixels())).ToArray());
    }

    private void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        Width = snapshot.Width;
        Height = snapshot.Height;
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        foreach (var layerSnapshot in snapshot.Layers)
        {
            var layer = new DrawingLayer(
                layerSnapshot.Name,
                layerSnapshot.BitmapWidth,
                layerSnapshot.BitmapHeight)
            {
                IsVisible = layerSnapshot.IsVisible,
                IsLocked = layerSnapshot.IsLocked,
                Opacity = layerSnapshot.Opacity,
                BlendMode = layerSnapshot.BlendMode,
                OffsetX = layerSnapshot.OffsetX,
                OffsetY = layerSnapshot.OffsetY,
                IsGroup = layerSnapshot.IsGroup,
                IsOpen = layerSnapshot.IsOpen,
                IsClipping = layerSnapshot.IsClipping,
                IndentLevel = layerSnapshot.IndentLevel
            };
            layer.RestorePixels(layerSnapshot.Pixels);
            _layers.Add(layer);
        }

        for (var i = 0; i < snapshot.Layers.Length; i++)
        {
            var parentIndex = snapshot.Layers[i].ParentIndex;
            if (parentIndex < 0 || parentIndex >= _layers.Count) continue;

            var layer = _layers[i];
            var parent = _layers[parentIndex];
            layer.Parent = parent;
            parent.Children.Add(layer);
        }

        ActiveLayerIndex = Math.Clamp(snapshot.ActiveLayerIndex, 0, _layers.Count - 1);
        LayersChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, new DocumentChangedEventArgs(null, null));
    }

    private sealed record DocumentSnapshot(int Width, int Height, int ActiveLayerIndex, LayerSnapshot[] Layers);

    private sealed record LayerSnapshot(
        string Name,
        bool IsVisible,
        bool IsLocked,
        double Opacity,
        string BlendMode,
        int OffsetX,
        int OffsetY,
        bool IsGroup,
        bool IsOpen,
        bool IsClipping,
        int IndentLevel,
        int ParentIndex,
        int BitmapWidth,
        int BitmapHeight,
        byte[] Pixels);

    private interface IHistoryState
    {
        IHistoryState CaptureRedo(DrawingDocument document);
        void Restore(DrawingDocument document);
    }

    private sealed record SnapshotHistoryState(DocumentSnapshot Snapshot) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new SnapshotHistoryState(document.CaptureSnapshot());
        public void Restore(DrawingDocument document) => document.RestoreSnapshot(Snapshot);
    }

    private sealed record LayerRegionHistoryState(int LayerIndex, LayerRegionPatch[] Patches, PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count)
                return new LayerRegionHistoryState(LayerIndex, [], DirtyRegion);

            var layer = document._layers[LayerIndex];
            var redoPatches = new LayerRegionPatch[Patches.Length];
            for (var i = 0; i < Patches.Length; i++)
            {
                var patch = Patches[i];
                redoPatches[i] = new LayerRegionPatch(patch.Region, layer.CapturePixels(patch.Region));
            }

            return new LayerRegionHistoryState(LayerIndex, redoPatches, DirtyRegion);
        }

        public void Restore(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return;

            var layer = document._layers[LayerIndex];
            for (var i = Patches.Length - 1; i >= 0; i--)
            {
                var patch = Patches[i];
                layer.RestorePixels(patch.Region, patch.BeforePixels);
            }

            document.LayersChanged?.Invoke(document, EventArgs.Empty);
            document.Changed?.Invoke(document, new DocumentChangedEventArgs(DirtyRegion, LayerIndex));
        }
    }

    private sealed record LayerPropertyHistoryState<T>(
        int LayerIndex,
        T OldValue,
        T NewValue,
        Action<DrawingLayer, T> Apply,
        bool AffectsComposite,
        PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
            => new LayerPropertyHistoryState<T>(LayerIndex, NewValue, OldValue, Apply, AffectsComposite, DirtyRegion);

        public void Restore(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return;
            Apply(document._layers[LayerIndex], OldValue);
            document.NotifyLayerMetadataChanged(AffectsComposite ? DirtyRegion : null, LayerIndex);
        }
    }
}

public sealed class DocumentChangedEventArgs(PixelRegion? dirtyRegion, int? layerIndex) : EventArgs
{
    public PixelRegion? DirtyRegion { get; } = dirtyRegion;
    public int? LayerIndex { get; } = layerIndex;
}

public sealed class LayerMetadataChangedEventArgs(int layerIndex) : EventArgs
{
    public int LayerIndex { get; } = layerIndex;
}

public readonly record struct LayerRegionPatch(PixelRegion Region, byte[] BeforePixels);
