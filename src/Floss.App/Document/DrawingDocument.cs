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

    public void CommitLayerTileMutation(int layerIndex, IReadOnlyDictionary<(int X, int Y), byte[]?> beforeTiles, PixelRegion dirtyRegion)
    {
        if (beforeTiles.Count == 0 || dirtyRegion.IsEmpty) return;
        if (layerIndex < 0 || layerIndex >= _layers.Count) return;

        var layer = _layers[layerIndex];
        var patches = new List<LayerTilePatch>(beforeTiles.Count);
        foreach (var (key, before) in beforeTiles)
        {
            var after = layer.CaptureTile(key.X, key.Y);
            if (TileBytesEqual(before, after)) continue;
            patches.Add(new LayerTilePatch(key.X, key.Y, before, after));
        }

        if (patches.Count == 0) return;

        _undo.Push(new LayerTileHistoryState(layerIndex, patches.ToArray(), dirtyRegion));
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

    public void ReplaceWith(DrawingDocument source)
    {
        if (ReferenceEquals(this, source)) return;

        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        _undo.Clear();
        _redo.Clear();

        Width = source.Width;
        Height = source.Height;
        ActiveLayerIndex = source.ActiveLayerIndex;
        CommittedStrokeCount = source.CommittedStrokeCount;

        _layers.AddRange(source._layers);
        source._layers.Clear();
        source.ActiveLayerIndex = -1;

        NotifyLayersChanged();
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

    public void FinalizeImport(int? activeLayerIndex = null)
    {
        ActiveLayerIndex = _layers.Count > 0
            ? Math.Clamp(activeLayerIndex ?? 0, 0, _layers.Count - 1)
            : -1;
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
            var layerIndex = ActiveLayerIndex;
            var dirtyRegion = LayerDirtyRegion(layerIndex);
            var beforeTiles = new Dictionary<(int X, int Y), byte[]?>();
            foreach (var (key, bytes) in ActiveLayer.CaptureTiles())
            {
                beforeTiles[key] = bytes;
            }

            ActiveLayer.Clear();
            CommittedStrokeCount = 0;
            CommitLayerTileMutation(layerIndex, beforeTiles, dirtyRegion);
            return;
        }

        ActiveLayer.Clear();
        CommittedStrokeCount = 0;
        NotifyChanged(null, ActiveLayerIndex);
    }

    public void AddLayer()
    {
        var prevActiveIndex = ActiveLayerIndex;
        var insertIndex = ActiveLayerIndex + 1;
        _layers.Insert(insertIndex, new DrawingLayer($"Layer {_layers.Count + 1}", Width, Height));
        ActiveLayerIndex = insertIndex;
        _undo.Push(new InsertLayerHistoryState(insertIndex, prevActiveIndex));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        NotifyLayersChanged();
    }

    public void DuplicateActiveLayer()
    {
        var prevActiveIndex = ActiveLayerIndex;
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
        copy.RestoreTiles(source.CaptureTiles());
        var insertIndex = ActiveLayerIndex + 1;
        _layers.Insert(insertIndex, copy);
        ActiveLayerIndex = insertIndex;
        _undo.Push(new InsertLayerHistoryState(insertIndex, prevActiveIndex));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        NotifyLayersChanged();
    }

    public void DeleteActiveLayer()
    {
        if (_layers.Count <= 1) return;
        var removedIndex = ActiveLayerIndex;
        var prevActiveIndex = ActiveLayerIndex;
        var removed = _layers[ActiveLayerIndex];
        var snap = CaptureLayerSnapshot(removed);
        _layers.RemoveAt(ActiveLayerIndex);
        removed.Dispose();
        ActiveLayerIndex = Math.Clamp(ActiveLayerIndex, 0, _layers.Count - 1);
        _undo.Push(new RemoveLayerHistoryState(removedIndex, prevActiveIndex, snap));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
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

    public void SetActiveLayerBlendMode(string blendMode)
    {
        if (ActiveLayer.BlendMode == blendMode) return;
        var oldMode = ActiveLayer.BlendMode;
        var dirtyRegion = LayerDirtyRegion(ActiveLayerIndex);
        _undo.Push(new LayerPropertyHistoryState<string>(
            ActiveLayerIndex, oldMode, blendMode,
            (layer, value) => layer.BlendMode = value,
            true, dirtyRegion));
        _redo.Clear();
        ActiveLayer.BlendMode = blendMode;
        NotifyLayerMetadataChanged(dirtyRegion, ActiveLayerIndex);
    }

    public void SetActiveLayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || ActiveLayer.Name == name) return;
        var oldName = ActiveLayer.Name;
        _undo.Push(new LayerPropertyHistoryState<string>(
            ActiveLayerIndex, oldName, name,
            (layer, value) => layer.Name = value,
            false, PixelRegion.Empty));
        _redo.Clear();
        ActiveLayer.Name = name;
        NotifyLayerMetadataChanged(null, ActiveLayerIndex);
    }

    public void CommitLayerOffsetMutation(int layerIndex, int oldOffsetX, int oldOffsetY, int newOffsetX, int newOffsetY)
    {
        if (layerIndex < 0 || layerIndex >= _layers.Count) return;
        if (oldOffsetX == newOffsetX && oldOffsetY == newOffsetY) return;

        var layer = _layers[layerIndex];
        var currentOffsetX = layer.OffsetX;
        var currentOffsetY = layer.OffsetY;
        layer.OffsetX = oldOffsetX;
        layer.OffsetY = oldOffsetY;
        var oldRegion = LayerDirtyRegion(layerIndex);
        layer.OffsetX = newOffsetX;
        layer.OffsetY = newOffsetY;
        var newRegion = LayerDirtyRegion(layerIndex);
        var dirtyRegion = oldRegion.Union(newRegion);
        layer.OffsetX = currentOffsetX;
        layer.OffsetY = currentOffsetY;

        _undo.Push(new LayerOffsetHistoryState(layerIndex, oldOffsetX, oldOffsetY, newOffsetX, newOffsetY, dirtyRegion));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        NotifyLayerMetadataChanged(dirtyRegion, layerIndex);
    }

    public void MoveActiveLayer(int delta)
    {
        var from = ActiveLayerIndex;
        var to = from + delta;
        if (to < 0 || to >= _layers.Count) return;
        var layer = ActiveLayer;
        _layers.RemoveAt(from);
        _layers.Insert(to, layer);
        ActiveLayerIndex = to;
        _undo.Push(new MoveLayerHistoryState(to, from));
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
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

    private static bool TileBytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private LayerSnapshot CaptureLayerSnapshot(DrawingLayer layer)
        => new(layer.Name, layer.IsVisible, layer.IsLocked, layer.Opacity, layer.BlendMode,
               layer.OffsetX, layer.OffsetY, layer.IsGroup, layer.IsOpen, layer.IsClipping,
               layer.IndentLevel, layer.Parent is null ? -1 : _layers.IndexOf(layer.Parent),
               layer.Width, layer.Height, layer.CaptureTiles());

    private DrawingLayer CreateLayerFromSnapshot(LayerSnapshot snap)
    {
        var layer = new DrawingLayer(snap.Name, snap.BitmapWidth, snap.BitmapHeight)
        {
            IsVisible = snap.IsVisible, IsLocked = snap.IsLocked,
            Opacity = snap.Opacity, BlendMode = snap.BlendMode,
            OffsetX = snap.OffsetX, OffsetY = snap.OffsetY,
            IsGroup = snap.IsGroup, IsOpen = snap.IsOpen,
            IsClipping = snap.IsClipping, IndentLevel = snap.IndentLevel
        };
        layer.RestoreTiles(snap.Tiles);
        return layer;
    }

    private DocumentSnapshot CaptureSnapshot()
    {
        return new DocumentSnapshot(
            Width, Height, ActiveLayerIndex,
            _layers.Select(CaptureLayerSnapshot).ToArray());
    }

    private void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        Width = snapshot.Width;
        Height = snapshot.Height;
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        foreach (var snap in snapshot.Layers)
            _layers.Add(CreateLayerFromSnapshot(snap));

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
        string Name, bool IsVisible, bool IsLocked, double Opacity, string BlendMode,
        int OffsetX, int OffsetY, bool IsGroup, bool IsOpen, bool IsClipping,
        int IndentLevel, int ParentIndex, int BitmapWidth, int BitmapHeight,
        Dictionary<(int X, int Y), byte[]> Tiles);

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

    // Undo an insert (add/duplicate): remove the layer that was inserted.
    private sealed record InsertLayerHistoryState(int InsertedIndex, int PreviousActiveIndex) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            var snap = document.CaptureLayerSnapshot(document._layers[InsertedIndex]);
            return new RemoveLayerHistoryState(InsertedIndex, document.ActiveLayerIndex, snap);
        }

        public void Restore(DrawingDocument document)
        {
            var removed = document._layers[InsertedIndex];
            document._layers.RemoveAt(InsertedIndex);
            removed.Dispose();
            document.ActiveLayerIndex = Math.Clamp(PreviousActiveIndex, 0, document._layers.Count - 1);
            document.NotifyLayersChanged();
        }
    }

    // Undo a delete: reinsert the layer that was removed.
    private sealed record RemoveLayerHistoryState(int RemovedIndex, int PreviousActiveIndex, LayerSnapshot RemovedSnap) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            return new InsertLayerHistoryState(RemovedIndex, document.ActiveLayerIndex);
        }

        public void Restore(DrawingDocument document)
        {
            var layer = document.CreateLayerFromSnapshot(RemovedSnap);
            document._layers.Insert(RemovedIndex, layer);
            document.ActiveLayerIndex = Math.Clamp(PreviousActiveIndex, 0, document._layers.Count - 1);
            document.NotifyLayersChanged();
        }
    }

    // Undo a move: swap from and to indices.
    private sealed record MoveLayerHistoryState(int FromIndex, int ToIndex) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new MoveLayerHistoryState(ToIndex, FromIndex);

        public void Restore(DrawingDocument document)
        {
            var layer = document._layers[FromIndex];
            document._layers.RemoveAt(FromIndex);
            document._layers.Insert(ToIndex, layer);
            document.ActiveLayerIndex = ToIndex;
            document.NotifyLayersChanged();
        }
    }

    private sealed record LayerOffsetHistoryState(
        int LayerIndex,
        int OldOffsetX,
        int OldOffsetY,
        int NewOffsetX,
        int NewOffsetY,
        PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
            => new LayerOffsetHistoryState(LayerIndex, NewOffsetX, NewOffsetY, OldOffsetX, OldOffsetY, DirtyRegion);

        public void Restore(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return;
            var layer = document._layers[LayerIndex];
            layer.OffsetX = OldOffsetX;
            layer.OffsetY = OldOffsetY;
            document.NotifyLayerMetadataChanged(DirtyRegion, LayerIndex);
        }
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

    private readonly record struct LayerTilePatch(int TileX, int TileY, byte[]? BeforePixels, byte[]? AfterPixels);

    private sealed record LayerTileHistoryState(int LayerIndex, LayerTilePatch[] Patches, PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            var redoPatches = new LayerTilePatch[Patches.Length];
            for (var i = 0; i < Patches.Length; i++)
            {
                var patch = Patches[i];
                redoPatches[i] = new LayerTilePatch(patch.TileX, patch.TileY, patch.AfterPixels, patch.BeforePixels);
            }

            return new LayerTileHistoryState(LayerIndex, redoPatches, DirtyRegion);
        }

        public void Restore(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return;

            var layer = document._layers[LayerIndex];
            foreach (var patch in Patches)
            {
                layer.RestoreTile(patch.TileX, patch.TileY, patch.BeforePixels);
            }

            document.LayersChanged?.Invoke(document, EventArgs.Empty);
            document.Changed?.Invoke(document, new DocumentChangedEventArgs(DirtyRegion, LayerIndex));
        }
    }

    private sealed record LayerPropertyHistoryState<T>(
        int LayerIndex, T OldValue, T NewValue,
        Action<DrawingLayer, T> Apply, bool AffectsComposite, PixelRegion DirtyRegion) : IHistoryState
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
