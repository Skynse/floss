using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Document;

public enum LayerDropPlacement { Above, Below, Into }

public sealed class DrawingDocument
{
    private Color _paperColor = Color.Parse("#f7f4ed");
    private bool _paperVisible = true;

    // --- History & State Tracking ---
    private readonly Stack<IHistoryState> _undo = new();
    private readonly Stack<IHistoryState> _redo = new();
    private readonly Stack<long> _undoIds = new();
    private readonly Stack<long> _redoIds = new();

    private long _currentStateId = 0;
    private long _savedStateId = 0;
    private long _nextStateId = 1;

    private readonly List<DrawingLayer> _layers = [];

    public DrawingDocument(int width = 2048, int height = 2048)
    {
        Width = width;
        Height = height;
        _layers.Add(new DrawingLayer("Layer 1", width, height));
        ActiveLayerIndex = 0;
    }

    // --- Events ---
    public event EventHandler<DocumentChangedEventArgs>? Changed;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;
    public event EventHandler<LayerMetadataChangedEventArgs>? LayerMetadataChanged;
    public event EventHandler? PaperChanged;
    public event EventHandler? DirtyStateChanged;

    // --- Properties ---
    public int Width { get; private set; }
    public int Height { get; private set; }
    public IReadOnlyList<DrawingLayer> Layers => _layers;
    public int ActiveLayerIndex { get; private set; }
    public DrawingLayer ActiveLayer => _layers[ActiveLayerIndex];
    public int CommittedStrokeCount { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool CanDeleteLayer => _layers.Count > 1;
    public bool CanPaintActiveLayer => !ActiveLayer.IsLocked && ActiveLayer.IsVisible && !ActiveLayer.IsGroup;
    public bool IsDirty => _currentStateId != _savedStateId;

    public Color PaperColor
    {
        get => _paperColor;
        set
        {
            if (_paperColor == value) return;
            _paperColor = value;
            PaperChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool PaperVisible
    {
        get => _paperVisible;
        set
        {
            if (_paperVisible == value) return;
            _paperVisible = value;
            PaperChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // --- Save State Management ---
    public void MarkAsSaved()
    {
        _savedStateId = _currentStateId;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PushHistoryState(IHistoryState state)
    {
        _undo.Push(state);
        _redo.Clear();

        _undoIds.Push(_currentStateId);
        _currentStateId = _nextStateId++;
        _redoIds.Clear();

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- Canvas Resize ---
    public void ResizeCanvas(int newW, int newH, int contentOffsetX, int contentOffsetY)
    {
        if (newW <= 0 || newH <= 0) return;
        BeginDocumentMutation();

        var newLayers = new List<DrawingLayer>(_layers.Count);
        var map = new Dictionary<DrawingLayer, DrawingLayer>(_layers.Count);

        foreach (var layer in _layers)
        {
            var nl = new DrawingLayer(layer.Name, newW, newH)
            {
                IsVisible = layer.IsVisible, IsLocked = layer.IsLocked, Opacity = layer.Opacity,
                BlendMode = layer.BlendMode, OffsetX = layer.OffsetX, OffsetY = layer.OffsetY,
                IsGroup = layer.IsGroup, IsOpen = layer.IsOpen, IsClipping = layer.IsClipping,
                IndentLevel = layer.IndentLevel, IsAlphaLocked = layer.IsAlphaLocked
            };
            if (!layer.IsGroup)
            {
                var pixels = layer.CapturePixels();
                var region = new PixelRegion(contentOffsetX, contentOffsetY, layer.Width, layer.Height);
                nl.Pixels.CopyFromBgra(region, pixels, layer.Width * 4);
                nl.MarkThumbnailDirty();
            }
            map[layer] = nl;
            newLayers.Add(nl);
        }

        for (var i = 0; i < _layers.Count; i++)
        {
            var parent = _layers[i].Parent;
            if (parent != null && map.TryGetValue(parent, out var np))
            {
                newLayers[i].Parent = np;
                np.Children.Add(newLayers[i]);
            }
        }

        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        _layers.AddRange(newLayers);

        Width = newW;
        Height = newH;

        ActiveLayerIndex = Math.Clamp(ActiveLayerIndex, 0, _layers.Count - 1);
        NotifyLayersChanged();
        NotifyChanged();
    }

    // --- Import / Setup ---
    public void ResizeForImport(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void ClearForImport()
    {
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        _undo.Clear();
        _redo.Clear();
        _undoIds.Clear();
        _redoIds.Clear();
        _currentStateId = 0;
        _savedStateId = 0;
        _nextStateId = 1;
        CommittedStrokeCount = 0;
    }

    public void ReplaceWith(DrawingDocument source)
    {
        if (ReferenceEquals(this, source)) return;

        ClearForImport();

        Width = source.Width;
        Height = source.Height;
        ActiveLayerIndex = source.ActiveLayerIndex;
        CommittedStrokeCount = source.CommittedStrokeCount;

        _layers.AddRange(source._layers);
        source._layers.Clear();
        source.ActiveLayerIndex = -1;

        NotifyLayersChanged();
    }

    public DrawingLayer AddLayerForImport(string name, bool isGroup = false, int? bitmapWidth = null, int? bitmapHeight = null)
    {
        var layer = CreateLayerForImport(name, isGroup, bitmapWidth, bitmapHeight);
        _layers.Add(layer);
        return layer;
    }

    public DrawingLayer CreateLayerForImport(string name, bool isGroup = false, int? bitmapWidth = null, int? bitmapHeight = null)
        => new(name, bitmapWidth ?? Width, bitmapHeight ?? Height) { IsGroup = isGroup };

    public void AppendLayerForImport(DrawingLayer layer) => _layers.Add(layer);

    public void FinalizeImport(int? activeLayerIndex = null)
    {
        ActiveLayerIndex = _layers.Count > 0 ? Math.Clamp(activeLayerIndex ?? 0, 0, _layers.Count - 1) : -1;
        NotifyLayersChanged();
    }

    // --- Mutations ---
    public void BeginDocumentMutation() => PushHistoryState(new SnapshotHistoryState(CaptureSnapshot()));

    public void CommitLayerRegionMutation(int layerIndex, IReadOnlyList<LayerRegionPatch> patches, PixelRegion dirtyRegion)
    {
        if (patches.Count == 0 || dirtyRegion.IsEmpty) return;
        PushHistoryState(new LayerRegionHistoryState(layerIndex, patches.ToArray(), dirtyRegion));
        NotifyChanged(dirtyRegion, layerIndex);
    }

    public void CommitLayerTileMutation(int layerIndex, IReadOnlyDictionary<(int X, int Y), byte[]?> beforeTiles, PixelRegion dirtyRegion)
    {
        if (beforeTiles.Count == 0 || dirtyRegion.IsEmpty || layerIndex < 0 || layerIndex >= _layers.Count) return;

        var layer = _layers[layerIndex];
        var patches = new List<LayerTilePatch>(beforeTiles.Count);
        foreach (var (key, before) in beforeTiles)
        {
            var after = layer.CaptureTile(key.X, key.Y);
            if (TileBytesEqual(before, after)) continue;
            patches.Add(new LayerTilePatch(key.X, key.Y, before, after));
        }

        if (patches.Count == 0) return;

        PushHistoryState(new LayerTileHistoryState(layerIndex, patches.ToArray(), dirtyRegion));
        NotifyChanged(dirtyRegion, layerIndex);
    }

    public void ApplyFilterToLayers(IReadOnlyList<int> indices, Action<DrawingLayer> apply)
    {
        var valid = indices.Where(i => i >= 0 && i < _layers.Count && !_layers[i].IsGroup && !_layers[i].IsLocked).ToList();
        if (valid.Count == 0) return;
        BeginDocumentMutation();
        foreach (var idx in valid)
            apply(_layers[idx]);
        NotifyChanged();
    }

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
            foreach (var (key, bytes) in ActiveLayer.CaptureTiles()) beforeTiles[key] = bytes;

            ActiveLayer.Clear();
            CommittedStrokeCount = 0;
            CommitLayerTileMutation(layerIndex, beforeTiles, dirtyRegion);
            return;
        }

        ActiveLayer.Clear();
        CommittedStrokeCount = 0;
        NotifyChanged(null, ActiveLayerIndex);
    }

    // --- Layer Management ---
    public void AddLayer()
    {
        if (ActiveLayerIndex < 0) return;
        BeginDocumentMutation();

        var active = ActiveLayer;
        var layer = new DrawingLayer($"Layer {_layers.Count + 1}", Width, Height);
        InsertLayerNear(layer, active, LayerDropPlacement.Above);
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(layer);
        NotifyLayersChanged();
    }

    public void AddGroupLayer()
    {
        if (ActiveLayerIndex < 0) return;
        BeginDocumentMutation();

        var active = ActiveLayer;
        var group = new DrawingLayer($"Folder {_layers.Count(l => l.IsGroup) + 1}", Width, Height) { IsGroup = true, IsOpen = true };
        InsertLayerNear(group, active, LayerDropPlacement.Above);
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(group);
        NotifyLayersChanged();
    }

    public void DuplicateActiveLayer()
    {
        if (ActiveLayerIndex < 0) return;
        BeginDocumentMutation();

        var source = ActiveLayer;
        var copy = CloneLayerTree(source, $"{source.Name} Copy");
        InsertLayerNear(copy, source, LayerDropPlacement.Above);
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(copy);
        NotifyLayersChanged();
    }

    public void DeleteActiveLayer()
    {
        if (_layers.Count <= 1) return;
        var removed = _layers[ActiveLayerIndex];
        var parent = removed.Parent;
        var siblings = parent?.Children ?? RootLayers();
        var siblingIndex = siblings.IndexOf(removed);

        BeginDocumentMutation();
        DetachLayer(removed);
        foreach (var layer in EnumerateLayerTree(removed).ToArray())
        {
            _layers.Remove(layer);
            layer.Dispose();
        }

        RebuildFlatLayerOrder();
        ActiveLayerIndex = Math.Clamp(ActiveLayerIndex, 0, _layers.Count - 1);
        if (_layers.Count > 0)
        {
            var nextSiblings = parent is { } p && _layers.Contains(p) ? p.Children : RootLayers();
            var fallback = nextSiblings.Count > 0 ? nextSiblings[Math.Clamp(siblingIndex, 0, nextSiblings.Count - 1)] : _layers[^1];
            ActiveLayerIndex = _layers.IndexOf(fallback);
        }

        NotifyLayersChanged();
    }

    public void SelectLayer(int index)
    {
        if (index < 0 || index >= _layers.Count || index == ActiveLayerIndex) return;
        ActiveLayerIndex = index;
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- Layer Properties ---
    public void ToggleLayerVisibility(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsVisible;
        var newValue = !oldValue;
        var dirtyRegion = LayerDirtyRegion(index);
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (layer, value) => layer.IsVisible = value, true, dirtyRegion));
        _layers[index].IsVisible = newValue;
        NotifyLayerMetadataChanged(dirtyRegion, index);
    }

    public void ToggleLayerLock(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsLocked;
        var newValue = !oldValue;
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (layer, value) => layer.IsLocked = value, false, PixelRegion.Empty));
        _layers[index].IsLocked = newValue;
        NotifyLayerMetadataChanged(null, index);
    }

    public void ToggleLayerAlphaLock(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsAlphaLocked;
        var newValue = !oldValue;
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (layer, value) => layer.IsAlphaLocked = value, false, PixelRegion.Empty));
        _layers[index].IsAlphaLocked = newValue;
        NotifyLayerMetadataChanged(null, index);
    }

    public void ToggleLayerClipping(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsClipping;
        var newValue = !oldValue;
        var dirtyRegion = LayerDirtyRegion(index);
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (layer, value) => layer.IsClipping = value, true, dirtyRegion));
        _layers[index].IsClipping = newValue;
        NotifyLayerMetadataChanged(dirtyRegion, index);
    }

    public void ToggleLayerOpen(int index)
    {
        if (index < 0 || index >= _layers.Count || !_layers[index].IsGroup) return;
        var oldValue = _layers[index].IsOpen;
        var newValue = !oldValue;
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (layer, value) => layer.IsOpen = value, false, PixelRegion.Empty));
        _layers[index].IsOpen = newValue;
        NotifyLayersChanged();
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
        PushHistoryState(new LayerPropertyHistoryState<string>(ActiveLayerIndex, oldMode, blendMode, (layer, value) => layer.BlendMode = value, true, dirtyRegion));
        ActiveLayer.BlendMode = blendMode;
        NotifyLayerMetadataChanged(dirtyRegion, ActiveLayerIndex);
    }

    public void SetActiveLayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || ActiveLayer.Name == name) return;
        var oldName = ActiveLayer.Name;
        PushHistoryState(new LayerPropertyHistoryState<string>(ActiveLayerIndex, oldName, name, (layer, value) => layer.Name = value, false, PixelRegion.Empty));
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

        layer.OffsetX = oldOffsetX; layer.OffsetY = oldOffsetY;
        var oldRegion = LayerDirtyRegion(layerIndex);

        layer.OffsetX = newOffsetX; layer.OffsetY = newOffsetY;
        var newRegion = LayerDirtyRegion(layerIndex);

        var dirtyRegion = oldRegion.Union(newRegion);
        layer.OffsetX = currentOffsetX; layer.OffsetY = currentOffsetY;

        PushHistoryState(new LayerOffsetHistoryState(layerIndex, oldOffsetX, oldOffsetY, newOffsetX, newOffsetY, dirtyRegion));
        NotifyLayerMetadataChanged(dirtyRegion, layerIndex);
    }

    // --- Movement & Compositing ---
    public void MoveActiveLayer(int delta)
    {
        if (ActiveLayerIndex < 0) return;
        var visible = VisibleLayerOrder().ToArray();
        var current = visible.IndexOf(ActiveLayer);
        var targetVisibleIndex = current - delta;
        if (current < 0 || targetVisibleIndex < 0 || targetVisibleIndex >= visible.Length) return;

        var target = visible[targetVisibleIndex];
        MoveLayer(ActiveLayerIndex, _layers.IndexOf(target), delta > 0 ? LayerDropPlacement.Above : LayerDropPlacement.Below);
    }

    public bool CanMoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement)
    {
        if (sourceIndex < 0 || sourceIndex >= _layers.Count || targetIndex < 0 || targetIndex >= _layers.Count || sourceIndex == targetIndex) return false;
        var source = _layers[sourceIndex];
        var target = _layers[targetIndex];
        if (placement == LayerDropPlacement.Into && !target.IsGroup) return false;
        if (target == source || IsDescendantOf(target, source)) return false;
        return true;
    }

    public void MoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement)
    {
        if (!CanMoveLayer(sourceIndex, targetIndex, placement)) return;

        BeginDocumentMutation();
        var source = _layers[sourceIndex];
        var target = _layers[targetIndex];
        InsertLayerNear(source, target, placement);
        if (placement == LayerDropPlacement.Into) target.IsOpen = true;
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(source);
        NotifyLayersChanged();
    }

    public void MergeSelectedLayers(IReadOnlyList<int> indices, Floss.App.Canvas.LayerCompositor compositor)
    {
        if (indices.Count < 2) return;
        var sorted = indices.OrderBy(i => i).ToList();
        if (sorted.Any(i => i < 0 || i >= _layers.Count)) return;

        BeginDocumentMutation();

        var toMerge = sorted.Select(i => _layers[i]).ToList();
        var merged = compositor.CompositeToBgra(toMerge, Width, Height);

        var topmostIndex = sorted[^1];
        var topmostLayer = _layers[topmostIndex];
        var anchor = topmostLayer.Parent != null
            ? _layers.FirstOrDefault(l => l.Parent == topmostLayer.Parent && !toMerge.Contains(l))
            : _layers.FirstOrDefault(l => l.Parent == null && !toMerge.Contains(l));

        var mergedLayer = new DrawingLayer("Merged", Width, Height);
        mergedLayer.Pixels.CopyFromBgra(merged, Width, Height);

        if (anchor != null) InsertLayerNear(mergedLayer, anchor, LayerDropPlacement.Above);
        else
        {
            mergedLayer.Parent = topmostLayer.Parent;
            (topmostLayer.Parent?.Children ?? RootLayers()).Add(mergedLayer);
        }

        foreach (var layer in toMerge)
        {
            DetachLayer(layer);
            _layers.Remove(layer);
            layer.Dispose();
        }

        RebuildFlatLayerOrder();
        ActiveLayerIndex = Math.Clamp(_layers.IndexOf(mergedLayer), 0, _layers.Count - 1);
        NotifyLayersChanged();
    }

    public void FlattenGroup(int groupIndex, Floss.App.Canvas.LayerCompositor compositor)
    {
        if (groupIndex < 0 || groupIndex >= _layers.Count) return;
        var group = _layers[groupIndex];
        if (!group.IsGroup || group.Children.Count == 0) return;

        BeginDocumentMutation();

        var flat = compositor.CompositeToBgra(group.Children, Width, Height);
        var flatLayer = new DrawingLayer(group.Name, Width, Height)
        {
            Opacity = group.Opacity,
            BlendMode = group.BlendMode,
            IsClipping = group.IsClipping
        };
        flatLayer.Pixels.CopyFromBgra(flat, Width, Height);

        InsertLayerNear(flatLayer, group, LayerDropPlacement.Above);
        foreach (var layer in EnumerateLayerTree(group).ToArray())
        {
            DetachLayer(layer);
            _layers.Remove(layer);
            layer.Dispose();
        }

        RebuildFlatLayerOrder();
        ActiveLayerIndex = Math.Clamp(_layers.IndexOf(flatLayer), 0, _layers.Count - 1);
        NotifyLayersChanged();
    }

    // --- Undo / Redo Execution ---
    public void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop();
        _redo.Push(state.CaptureRedo(this));
        state.Restore(this);
        CommittedStrokeCount = Math.Max(0, CommittedStrokeCount - 1);

        _redoIds.Push(_currentStateId);
        _currentStateId = _undoIds.Pop();

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var state = _redo.Pop();
        _undo.Push(state.CaptureRedo(this));
        state.Restore(this);
        CommittedStrokeCount += 1;

        _undoIds.Push(_currentStateId);
        _currentStateId = _redoIds.Pop();

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- Helpers / Notifications ---
    public void NotifyChanged(PixelRegion? dirtyRegion = null, int? layerIndex = null) => Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex));
    private void NotifyLayersChanged()
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, new DocumentChangedEventArgs(null, null));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void NotifyLayerMetadataChanged(PixelRegion? dirtyRegion, int? layerIndex)
    {
        if (layerIndex is { } index) LayerMetadataChanged?.Invoke(this, new LayerMetadataChangedEventArgs(index));
        if (dirtyRegion is { IsEmpty: false }) Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private PixelRegion LayerDirtyRegion(int index) => index < 0 || index >= _layers.Count ? PixelRegion.Empty : _layers[index].DocumentContentBounds.ClipTo(Width, Height);
    private List<DrawingLayer> RootLayers() => _layers.Where(layer => layer.Parent == null).ToList();

    private IEnumerable<DrawingLayer> VisibleLayerOrder()
    {
        foreach (var root in RootLayers().AsEnumerable().Reverse())
            foreach (var layer in VisibleLayerOrder(root)) yield return layer;
    }
    private static IEnumerable<DrawingLayer> VisibleLayerOrder(DrawingLayer layer)
    {
        yield return layer;
        if (!layer.IsGroup || !layer.IsOpen) yield break;
        for (var i = layer.Children.Count - 1; i >= 0; i--)
            foreach (var child in VisibleLayerOrder(layer.Children[i])) yield return child;
    }

    private static IEnumerable<DrawingLayer> EnumerateLayerTree(DrawingLayer layer)
    {
        yield return layer;
        foreach (var child in layer.Children.ToArray())
            foreach (var descendant in EnumerateLayerTree(child)) yield return descendant;
    }

    private static bool IsDescendantOf(DrawingLayer layer, DrawingLayer possibleAncestor)
    {
        for (var parent = layer.Parent; parent != null; parent = parent.Parent)
            if (parent == possibleAncestor) return true;
        return false;
    }

    private void InsertLayerNear(DrawingLayer layer, DrawingLayer target, LayerDropPlacement placement)
    {
        DetachLayer(layer);
        var newParent = placement == LayerDropPlacement.Into ? target : target.Parent;
        var siblings = newParent?.Children ?? RootLayers();
        siblings.Remove(layer);
        var targetSiblingIndex = placement == LayerDropPlacement.Into ? siblings.Count : siblings.IndexOf(target);
        var insertIndex = placement switch { LayerDropPlacement.Above => targetSiblingIndex + 1, _ => targetSiblingIndex };

        layer.Parent = newParent;
        siblings.Insert(Math.Clamp(insertIndex, 0, siblings.Count), layer);
        if (newParent != null && !newParent.Children.Contains(layer)) newParent.Children.Add(layer);

        if (newParent == null) RebuildFlatLayerOrder(siblings);
        UpdateIndentTree(layer, newParent?.IndentLevel + 1 ?? 0);
    }

    private static void DetachLayer(DrawingLayer layer)
    {
        if (layer.Parent != null) layer.Parent.Children.Remove(layer);
        layer.Parent = null;
    }

    private void RebuildFlatLayerOrder(List<DrawingLayer>? rootsOverride = null)
    {
        var roots = rootsOverride ?? RootLayers();
        _layers.Clear();
        foreach (var root in roots) AppendFlat(root, 0);
    }

    private void AppendFlat(DrawingLayer layer, int indent)
    {
        layer.IndentLevel = indent;
        _layers.Add(layer);
        foreach (var child in layer.Children) { child.Parent = layer; AppendFlat(child, indent + 1); }
    }

    private static void UpdateIndentTree(DrawingLayer layer, int indent)
    {
        layer.IndentLevel = indent;
        foreach (var child in layer.Children) UpdateIndentTree(child, indent + 1);
    }

    private static DrawingLayer CloneLayerTree(DrawingLayer source, string? nameOverride = null)
    {
        var copy = new DrawingLayer(nameOverride ?? source.Name, source.Width, source.Height)
        {
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            IsAlphaLocked = source.IsAlphaLocked,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            IsGroup = source.IsGroup,
            IsOpen = source.IsOpen,
            IsClipping = source.IsClipping,
            IndentLevel = source.IndentLevel
        };
        copy.RestoreTiles(source.CaptureTiles());
        foreach (var child in source.Children) { var childCopy = CloneLayerTree(child); childCopy.Parent = copy; copy.Children.Add(childCopy); }
        return copy;
    }

    private static bool TileBytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private LayerSnapshot CaptureLayerSnapshot(DrawingLayer layer) => new(layer.Name, layer.IsVisible, layer.IsLocked, layer.Opacity, layer.BlendMode, layer.OffsetX, layer.OffsetY, layer.IsGroup, layer.IsOpen, layer.IsClipping, layer.IndentLevel, layer.Parent is null ? -1 : _layers.IndexOf(layer.Parent), layer.Width, layer.Height, layer.CaptureTiles());

    private DrawingLayer CreateLayerFromSnapshot(LayerSnapshot snap)
    {
        var layer = new DrawingLayer(snap.Name, snap.BitmapWidth, snap.BitmapHeight) { IsVisible = snap.IsVisible, IsLocked = snap.IsLocked, Opacity = snap.Opacity, BlendMode = snap.BlendMode, OffsetX = snap.OffsetX, OffsetY = snap.OffsetY, IsGroup = snap.IsGroup, IsOpen = snap.IsOpen, IsClipping = snap.IsClipping, IndentLevel = snap.IndentLevel };
        layer.RestoreTiles(snap.Tiles);
        return layer;
    }

    private DocumentSnapshot CaptureSnapshot() => new(Width, Height, ActiveLayerIndex, _layers.Select(CaptureLayerSnapshot).ToArray());

    private void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        Width = snapshot.Width; Height = snapshot.Height;
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        foreach (var snap in snapshot.Layers) _layers.Add(CreateLayerFromSnapshot(snap));

        for (var i = 0; i < snapshot.Layers.Length; i++)
        {
            var parentIndex = snapshot.Layers[i].ParentIndex;
            if (parentIndex < 0 || parentIndex >= _layers.Count) continue;
            var layer = _layers[i]; var parent = _layers[parentIndex];
            layer.Parent = parent; parent.Children.Add(layer);
        }

        ActiveLayerIndex = Math.Clamp(snapshot.ActiveLayerIndex, 0, _layers.Count - 1);
        NotifyLayersChanged();
    }

    // --- Records and State Classes ---
    private sealed record DocumentSnapshot(int Width, int Height, int ActiveLayerIndex, LayerSnapshot[] Layers);
    private sealed record LayerSnapshot(string Name, bool IsVisible, bool IsLocked, double Opacity, string BlendMode, int OffsetX, int OffsetY, bool IsGroup, bool IsOpen, bool IsClipping, int IndentLevel, int ParentIndex, int BitmapWidth, int BitmapHeight, Dictionary<(int X, int Y), byte[]> Tiles);

    private interface IHistoryState { IHistoryState CaptureRedo(DrawingDocument document); void Restore(DrawingDocument document); }

    private sealed record SnapshotHistoryState(DocumentSnapshot Snapshot) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new SnapshotHistoryState(document.CaptureSnapshot());
        public void Restore(DrawingDocument document) => document.RestoreSnapshot(Snapshot);
    }

    private sealed record InsertLayerHistoryState(int InsertedIndex, int PreviousActiveIndex) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new RemoveLayerHistoryState(InsertedIndex, document.ActiveLayerIndex, document.CaptureLayerSnapshot(document._layers[InsertedIndex]));
        public void Restore(DrawingDocument document) { var removed = document._layers[InsertedIndex]; document._layers.RemoveAt(InsertedIndex); removed.Dispose(); document.ActiveLayerIndex = Math.Clamp(PreviousActiveIndex, 0, document._layers.Count - 1); document.NotifyLayersChanged(); }
    }

    private sealed record RemoveLayerHistoryState(int RemovedIndex, int PreviousActiveIndex, LayerSnapshot RemovedSnap) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new InsertLayerHistoryState(RemovedIndex, document.ActiveLayerIndex);
        public void Restore(DrawingDocument document) { document._layers.Insert(RemovedIndex, document.CreateLayerFromSnapshot(RemovedSnap)); document.ActiveLayerIndex = Math.Clamp(PreviousActiveIndex, 0, document._layers.Count - 1); document.NotifyLayersChanged(); }
    }

    private sealed record MoveLayerHistoryState(int FromIndex, int ToIndex) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new MoveLayerHistoryState(ToIndex, FromIndex);
        public void Restore(DrawingDocument document) { var layer = document._layers[FromIndex]; document._layers.RemoveAt(FromIndex); document._layers.Insert(ToIndex, layer); document.ActiveLayerIndex = ToIndex; document.NotifyLayersChanged(); }
    }

    private sealed record LayerOffsetHistoryState(int LayerIndex, int OldOffsetX, int OldOffsetY, int NewOffsetX, int NewOffsetY, PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new LayerOffsetHistoryState(LayerIndex, NewOffsetX, NewOffsetY, OldOffsetX, OldOffsetY, DirtyRegion);
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; var layer = document._layers[LayerIndex]; layer.OffsetX = OldOffsetX; layer.OffsetY = OldOffsetY; document.NotifyLayerMetadataChanged(DirtyRegion, LayerIndex); }
    }

    private sealed record LayerRegionHistoryState(int LayerIndex, LayerRegionPatch[] Patches, PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return new LayerRegionHistoryState(LayerIndex, [], DirtyRegion);
            var layer = document._layers[LayerIndex]; var redoPatches = new LayerRegionPatch[Patches.Length];
            for (var i = 0; i < Patches.Length; i++) { var patch = Patches[i]; redoPatches[i] = new LayerRegionPatch(patch.Region, layer.CapturePixels(patch.Region)); }
            return new LayerRegionHistoryState(LayerIndex, redoPatches, DirtyRegion);
        }
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; var layer = document._layers[LayerIndex]; for (var i = Patches.Length - 1; i >= 0; i--) { var patch = Patches[i]; layer.RestorePixels(patch.Region, patch.BeforePixels); } document.NotifyChanged(DirtyRegion, LayerIndex); }
    }

    private readonly record struct LayerTilePatch(int TileX, int TileY, byte[]? BeforePixels, byte[]? AfterPixels);
    private sealed record LayerTileHistoryState(int LayerIndex, LayerTilePatch[] Patches, PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            var redoPatches = new LayerTilePatch[Patches.Length];
            for (var i = 0; i < Patches.Length; i++) { var patch = Patches[i]; redoPatches[i] = new LayerTilePatch(patch.TileX, patch.TileY, patch.AfterPixels, patch.BeforePixels); }
            return new LayerTileHistoryState(LayerIndex, redoPatches, DirtyRegion);
        }
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; var layer = document._layers[LayerIndex]; foreach (var patch in Patches) { layer.RestoreTile(patch.TileX, patch.TileY, patch.BeforePixels); } document.NotifyChanged(DirtyRegion, LayerIndex); }
    }

    private sealed record LayerPropertyHistoryState<T>(int LayerIndex, T OldValue, T NewValue, Action<DrawingLayer, T> Apply, bool AffectsComposite, PixelRegion DirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new LayerPropertyHistoryState<T>(LayerIndex, NewValue, OldValue, Apply, AffectsComposite, DirtyRegion);
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; Apply(document._layers[LayerIndex], OldValue); document.NotifyLayerMetadataChanged(AffectsComposite ? DirtyRegion : null, LayerIndex); }
    }
}

public sealed class DocumentChangedEventArgs(PixelRegion? dirtyRegion, int? layerIndex) : EventArgs { public PixelRegion? DirtyRegion { get; } = dirtyRegion; public int? LayerIndex { get; } = layerIndex; }
public sealed class LayerMetadataChangedEventArgs(int layerIndex) : EventArgs { public int LayerIndex { get; } = layerIndex; }
public readonly record struct LayerRegionPatch(PixelRegion Region, byte[] BeforePixels);
