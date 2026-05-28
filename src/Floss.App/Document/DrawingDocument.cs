using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Floss.App.Tools;

namespace Floss.App.Document;

public enum LayerDropPlacement { Above, Below, Into }
public enum DocumentHistoryChangeKind { Mutation, Undo, Redo }

public sealed class StrokeSuspendEventArgs(PixelRegion region, int layerIndex) : EventArgs
{
    public PixelRegion Region { get; } = region;
    public int LayerIndex { get; } = layerIndex;
}

public sealed class DrawingDocument : IDisposable
{
    public DocumentRenderLock RenderLock { get; } = new();
    public void Dispose()
    {
        foreach (var l in _layers) l.Dispose();
        _layers.Clear();
        _undo.Clear();
        _redo.Clear();
        _undoIds.Clear();
        _redoIds.Clear();
        Selection.Clear();
        RenderLock.Dispose();
    }

    // --- History & State Tracking ---
    private readonly Stack<IHistoryState> _undo = new();
    private readonly Stack<IHistoryState> _redo = new();
    private readonly Stack<long> _undoIds = new();
    private readonly Stack<long> _redoIds = new();

    private long _currentStateId = 0;
    private long _savedStateId = 0;
    private long _nextStateId = 1;
    private DocumentHistoryChangeKind? _activeHistoryReplayKind;

    private readonly List<DrawingLayer> _layers = [];
    private bool _layerOpacityScrubActive;
    private double _layerOpacityScrubStart;
    private int _layerOpacityScrubIndex = -1;

    public DrawingDocument(int width = 2048, int height = 2048)
    {
        Width = width;
        Height = height;
        Selection.Resize(width, height);
        ActiveLayerIndex = -1;
    }

    // --- Events ---
    public event EventHandler<DocumentChangedEventArgs>? Changed;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;
    public event EventHandler<LayerMetadataChangedEventArgs>? LayerMetadataChanged;
    public event EventHandler<DrawingLayer>? LayerRemoved;
    public event EventHandler? DirtyStateChanged;
    public event EventHandler? SelectionChanged;
    // Raised by paint outputs at the start/end of a continuous edit so the
    // compositor can switch into a suspended mode that only updates the stroke
    // region. The PixelRegion is the current stroke bounding box in document
    // coordinates (may be extended via Extend during the stroke).
    public event EventHandler<StrokeSuspendEventArgs>? StrokeSuspendBegan;
    public event EventHandler<PixelRegion>? StrokeSuspendExtended;
    public event EventHandler? StrokeSuspendEnded;

    public void NotifyStrokeSuspendBegin(PixelRegion region, int layerIndex = -1)
        => StrokeSuspendBegan?.Invoke(this, new StrokeSuspendEventArgs(region, layerIndex));
    public void NotifyStrokeSuspendExtend(PixelRegion region)
        => StrokeSuspendExtended?.Invoke(this, region);
    public void NotifyStrokeSuspendEnd()
        => StrokeSuspendEnded?.Invoke(this, EventArgs.Empty);

    // --- Properties ---
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Avalonia.Media.Color PaperColor { get; internal set; } = new(255, 255, 255, 255); // opaque white by default

    public void SetPaperColor(Avalonia.Media.Color color)
    {
        if (color == PaperColor) return;
        var oldColor = PaperColor;
        PushHistoryState(new DocumentPropertyHistoryState<Avalonia.Media.Color>(oldColor, color, v => PaperColor = v, true, PixelRegion.Empty));
        PaperColor = color;
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }
    public DrawingLayer? PaperLayer { get; internal set; }

    /// <summary>True when the compositor/viewport should fill with paper color instead of transparency.</summary>
    public bool IsPaperBackgroundVisible =>
        PaperLayer is { IsVisible: true } && PaperColor.A > 0;

    internal void SwapDimensions()
    {
        (Width, Height) = (Height, Width);
    }
    public IReadOnlyList<DrawingLayer> Layers => _layers;
    public int ActiveLayerIndex { get; private set; }
    public DrawingLayer? ActiveLayer => ActiveLayerIndex >= 0 && ActiveLayerIndex < _layers.Count ? _layers[ActiveLayerIndex] : null;
    public SelectionMask Selection { get; } = new();
    public int CommittedStrokeCount { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool CanDeleteLayer => CanDeleteLayers([ActiveLayerIndex]);

    public bool CanDeleteLayers(IReadOnlyList<int> indices)
    {
        if (_layers.Count <= 1 || indices.Count == 0)
            return false;

        var toRemove = CollectDeletableLayers(indices);
        return toRemove.Count > 0 && _layers.Count - toRemove.Count >= 1;
    }

    private static bool IsLayerDeletable(DrawingLayer layer) =>
        !layer.IsLocked || layer.IsPaper;

    private HashSet<DrawingLayer> CollectDeletableLayers(IReadOnlyList<int> indices)
    {
        var toRemove = new HashSet<DrawingLayer>();
        foreach (var i in indices)
        {
            if (i < 0 || i >= _layers.Count) continue;
            var layer = _layers[i];
            if (!IsLayerDeletable(layer)) continue;
            foreach (var l in EnumerateLayerTree(layer))
                toRemove.Add(l);
        }
        return toRemove;
    }
    public bool CanPaintActiveLayer => ActiveLayer is { IsLocked: false, IsVisible: true, IsGroup: false };
    public bool CanModifyActiveLayer => ActiveLayer is { IsLocked: false, IsGroup: false };
    public bool IsDirty => _currentStateId != _savedStateId;
    public DocumentHistoryChangeKind LastHistoryChangeKind { get; private set; } = DocumentHistoryChangeKind.Mutation;
    public bool LastHistoryAffectsVisual { get; private set; } = true;
    public PixelRegion LastHistoryVisualDirtyRegion { get; private set; } = PixelRegion.Empty;
    public bool LastHistoryRequiresFullVisualRefresh =>
        LastHistoryAffectsVisual && LastHistoryVisualDirtyRegion.IsEmpty;

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

        MarkHistoryMutation();
        ApplyHistoryVisualState(state);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyHistoryVisualState(IHistoryState state)
    {
        if (_activeHistoryReplayKind.HasValue)
            return;

        LastHistoryAffectsVisual = state.AffectsVisual;
        LastHistoryVisualDirtyRegion = state.VisualDirtyRegion;
    }

    private void MarkVisualHistoryChanged(bool affectsVisual, PixelRegion visualDirtyRegion)
    {
        LastHistoryAffectsVisual = affectsVisual;
        LastHistoryVisualDirtyRegion = visualDirtyRegion;
    }

    private void MarkHistoryMutation()
    {
        if (_activeHistoryReplayKind.HasValue)
        {
            LastHistoryChangeKind = _activeHistoryReplayKind.Value;
            return;
        }

        LastHistoryChangeKind = DocumentHistoryChangeKind.Mutation;
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
                IsVisible = layer.IsVisible,
                IsLocked = layer.IsLocked,
                Opacity = layer.Opacity,
                BlendMode = layer.BlendMode,
                OffsetX = layer.OffsetX,
                OffsetY = layer.OffsetY,
                IsGroup = layer.IsGroup,
                IsOpen = layer.IsOpen,
                IsClipping = layer.IsClipping,
                IndentLevel = layer.IndentLevel,
                IsAlphaLocked = layer.IsAlphaLocked,
                IsReference = layer.IsReference,
                IsPaper = layer.IsPaper,
                Adjustment = layer.Adjustment?.Clone()
            };
            if (!layer.IsGroup)
            {
                if (layer.IsPaper)
                {
                    nl.FillSolid(nl.Pixels.Bounds, PaperColor);
                }
                else
                {
                    var pixels = layer.CapturePixels();
                    var region = new PixelRegion(contentOffsetX, contentOffsetY, layer.Width, layer.Height);
                    nl.Pixels.CopyFromBgra(region, pixels, layer.Width * 4);
                }
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
        Selection.Resize(newW, newH);

        PaperLayer = _layers.FirstOrDefault(l => l.IsPaper);

        ActiveLayerIndex = _layers.Count > 0 ? Math.Clamp(ActiveLayerIndex, 0, _layers.Count - 1) : -1;
        NotifyLayersChanged();
        NotifySelectionChanged();
        NotifyChanged();
    }

    // --- Import / Setup ---
    public void ResizeForImport(int width, int height)
    {
        Width = width;
        Height = height;
        Selection.Resize(width, height);
    }

    public void ClearForImport()
    {
        NotifyLayerRemovedRecursive(_layers);
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
        ActiveLayerIndex = -1;
        PaperLayer = null;
        PaperColor = new Avalonia.Media.Color(255, 255, 255, 255);
        Selection.Resize(Width, Height);
        Selection.Clear();
    }

    private void NotifyLayerRemovedRecursive(List<DrawingLayer> layers)
    {
        foreach (var l in layers)
            NotifyLayerRemovedRecursive(l);
    }

    private void NotifyLayerRemovedRecursive(DrawingLayer layer)
    {
        LayerRemoved?.Invoke(this, layer);
        if (layer.Children.Count > 0)
            NotifyLayerRemovedRecursive(layer.Children);
    }

    public void ReplaceWith(DrawingDocument source)
    {
        if (ReferenceEquals(this, source)) return;

        ClearForImport();

        Width = source.Width;
        Height = source.Height;
        Selection.Resize(Width, Height);
        ActiveLayerIndex = source.ActiveLayerIndex;
        CommittedStrokeCount = source.CommittedStrokeCount;
        PaperColor = source.PaperColor;
        PaperLayer = source.PaperLayer;

        _layers.AddRange(source._layers);
        source._layers.Clear();
        source.ActiveLayerIndex = -1;
        source.PaperLayer = null;

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
        PaperLayer = _layers.FirstOrDefault(l => l.IsPaper);
        if (PaperLayer != null && PaperColor.A == 0)
            PaperColor = new Avalonia.Media.Color(255, 255, 255, 255);

        var defaultActive = _layers.FindIndex(l => !l.IsPaper && !l.IsGroup);
        if (defaultActive < 0)
            defaultActive = _layers.FindIndex(l => !l.IsPaper);
        if (defaultActive < 0)
            defaultActive = 0;

        ActiveLayerIndex = _layers.Count > 0
            ? Math.Clamp(activeLayerIndex ?? defaultActive, 0, _layers.Count - 1)
            : -1;
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

    public void CommitLayerTileMutations(IReadOnlyList<LayerTileMutation> mutations)
    {
        if (mutations.Count == 0) return;

        var states = new List<IHistoryState>(mutations.Count);
        foreach (var mutation in mutations)
        {
            if (mutation.BeforeTiles.Count == 0 || mutation.DirtyRegion.IsEmpty ||
                mutation.LayerIndex < 0 || mutation.LayerIndex >= _layers.Count)
                continue;

            var layer = _layers[mutation.LayerIndex];
            var patches = new List<LayerTilePatch>(mutation.BeforeTiles.Count);
            foreach (var (key, before) in mutation.BeforeTiles)
            {
                var after = layer.CaptureTile(key.X, key.Y);
                if (TileBytesEqual(before, after)) continue;
                patches.Add(new LayerTilePatch(key.X, key.Y, before, after));
            }

            if (patches.Count == 0) continue;
            states.Add(new LayerTileHistoryState(mutation.LayerIndex, patches.ToArray(), mutation.DirtyRegion));
        }

        if (states.Count == 0) return;
        PushHistoryState(states.Count == 1 ? states[0] : new CompositeHistoryState(states.ToArray()));
        foreach (var mutation in mutations)
            NotifyChanged(mutation.DirtyRegion, mutation.LayerIndex);
    }

    public void CommitSelectionMutation(SelectionMask.Snapshot before)
    {
        var after = Selection.CaptureSnapshot();
        if (SelectionSnapshotsEqual(before, after)) return;
        PushHistoryState(new SelectionHistoryState(before, after));
        NotifySelectionChanged();
    }

    public void ApplyFilterToLayers(IReadOnlyList<int> indices, Action<DrawingLayer> apply)
    {
        var valid = indices.Where(i => i >= 0 && i < _layers.Count && !_layers[i].IsGroup && !_layers[i].IsLocked).ToList();
        if (valid.Count == 0) return;
        BeginDocumentMutation();
        foreach (var idx in valid)
            apply(_layers[idx]);
        NotifyChanged(GetLayersDirtyRegion(valid), valid.Count == 1 ? valid[0] : null);
    }

    public void CommitStroke()
    {
        CommittedStrokeCount += 1;
        MarkHistoryMutation();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearActiveLayer(bool pushHistory = true)
    {
        if (!CanPaintActiveLayer) return;
        var layer = ActiveLayer;
        if (layer == null) return;

        if (pushHistory)
        {
            var layerIndex = ActiveLayerIndex;
            var dirtyRegion = LayerDirtyRegion(layerIndex);
            var beforeTiles = new Dictionary<(int X, int Y), byte[]?>();
            foreach (var (key, bytes) in layer.CaptureTiles()) beforeTiles[key] = bytes;

            layer.Clear();
            CommittedStrokeCount = 0;
            CommitLayerTileMutation(layerIndex, beforeTiles, dirtyRegion);
            return;
        }

        layer.Clear();
        CommittedStrokeCount = 0;
        NotifyChanged(null, ActiveLayerIndex);
    }

    // --- Layer Management ---
    public void AddLayer()
    {
        BeginDocumentMutation();

        var layer = new DrawingLayer($"Layer {_layers.Count + 1}", Width, Height);
        if (ActiveLayerIndex >= 0 && ActiveLayerIndex < _layers.Count)
        {
            var active = ActiveLayer;
            InsertLayerNear(layer, active!, LayerDropPlacement.Above);
        }
        else
        {
            _layers.Add(layer);
        }
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(layer);
        NotifyLayersChanged();
    }

    public void AddAdjustmentLayer(AdjustmentKind kind, string? name = null)
    {
        BeginDocumentMutation();

        var layer = new DrawingLayer(name ?? AdjustmentLayerData.DisplayName(kind), Width, Height)
        {
            Adjustment = new AdjustmentLayerData { Kind = kind }
        };
        if (ActiveLayerIndex >= 0 && ActiveLayerIndex < _layers.Count)
            InsertLayerNear(layer, ActiveLayer!, LayerDropPlacement.Above);
        else
            _layers.Add(layer);
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(layer);
        NotifyLayersChanged();
    }

    public void SetLayerAdjustmentParams(int layerIndex, AdjustmentLayerData newParams)
    {
        if (layerIndex < 0 || layerIndex >= _layers.Count) return;
        var layer = _layers[layerIndex];
        if (layer.Adjustment == null) return;
        var oldParams = layer.Adjustment.Clone();
        layer.Adjustment = newParams.Clone();
        PushHistoryState(new AdjustmentParamsHistoryState(layerIndex, oldParams, newParams.Clone(), new PixelRegion(0, 0, Width, Height)));
        NotifyLayerMetadataChanged(new PixelRegion(0, 0, Width, Height), layerIndex);
    }

    public void PreviewLayerAdjustmentParams(int layerIndex, AdjustmentLayerData newParams)
    {
        if (layerIndex < 0 || layerIndex >= _layers.Count) return;
        var layer = _layers[layerIndex];
        if (layer.Adjustment == null) return;
        layer.Adjustment = newParams.Clone();
        PreviewLayerMetadataChanged(new PixelRegion(0, 0, Width, Height), layerIndex);
    }

    public void AddGroupLayer()
    {
        BeginDocumentMutation();

        var group = new DrawingLayer($"Folder {_layers.Count(l => l.IsGroup) + 1}", Width, Height) { IsGroup = true, IsOpen = true };
        if (ActiveLayerIndex >= 0 && ActiveLayerIndex < _layers.Count)
        {
            var active = ActiveLayer;
            InsertLayerNear(group, active!, LayerDropPlacement.Above);
        }
        else
        {
            _layers.Add(group);
        }
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(group);
        NotifyLayersChanged();
    }

    public void DuplicateActiveLayer()
    {
        var source = ActiveLayer;
        if (source == null) return;
        BeginDocumentMutation();
        var copy = CloneLayerTree(source, source.Name + " copy");
        InsertLayerNear(copy, source, LayerDropPlacement.Above);
        ActiveLayerIndex = _layers.IndexOf(copy);
        NotifyLayersChanged();
    }

    public void AddBackgroundLayer()
    {
        if (PaperLayer != null) return;

        BeginDocumentMutation();

        // PaperColor is zeroed out when a paper layer is deleted. Reset to white
        // so the re-added layer is visible and IsPaperBackgroundVisible returns true.
        if (PaperColor.A == 0)
            PaperColor = new Avalonia.Media.Color(255, 255, 255, 255);

        var bg = new DrawingLayer("Paper", Width, Height);
        bg.IsLocked = true;
        bg.IsPaper = true;
        // No pixel fill needed — compositor handles paper fill via PaperColor.

        var roots = RootLayers();
        roots.Add(bg);
        RebuildFlatLayerOrder(roots);

        PaperLayer = bg;
        NotifyLayersChanged();
    }

    public void GroupSelectedLayers(IReadOnlyList<int> indices)
    {
        if (indices.Count < 1) return;
        var sorted = indices.OrderBy(i => i).ToList();
        if (sorted.Any(i => i < 0 || i >= _layers.Count)) return;

        BeginDocumentMutation();

        var topIndex = sorted[^1];
        var topLayer = _layers[topIndex];
        var group = new DrawingLayer($"Folder {_layers.Count(l => l.IsGroup) + 1}", Width, Height)
        {
            IsGroup = true,
            IsOpen = true
        };
        InsertLayerNear(group, topLayer, LayerDropPlacement.Above);
        RebuildFlatLayerOrder();

        // Collect only the top-level selected layers — skip any layer that is
        // already a descendant of another selected layer (e.g. child of a
        // selected group). The group brings its children along implicitly.
        var selectedSet = sorted.Select(i => _layers[i]).ToHashSet();
        var layersToMove = sorted
            .Select(i => _layers[i])
            .Where(l => l != group)
            .Where(l => selectedSet.Contains(l) &&
                        !HasAncestorInSet(l, selectedSet))
            .ToList();
        foreach (var layer in layersToMove)
        {
            InsertLayerNear(layer, group, LayerDropPlacement.Into);
        }
        group.IsOpen = true;

        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(group);
        NotifyLayersChanged();
    }

    public void PasteLayer(DrawingLayer clipboard, int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= _layers.Count) return;
        BeginDocumentMutation();
        var copy = CloneLayerTree(clipboard);
        InsertLayerNear(copy, _layers[targetIndex], LayerDropPlacement.Above);
        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.IndexOf(copy);
        NotifyLayersChanged();
    }

    public void InsertAndSelectLayer(DrawingLayer layer, int index)
    {
        if (index < 0) index = 0;
        if (index > _layers.Count) index = _layers.Count;
        BeginDocumentMutation();
        _layers.Insert(index, layer);
        ActiveLayerIndex = index;
        NotifyLayersChanged();
    }

    public void DeleteActiveLayer() => DeleteLayers([ActiveLayerIndex]);

    public void DeleteLayers(IReadOnlyList<int> indices)
    {
        var toRemove = CollectDeletableLayers(indices);
        if (toRemove.Count == 0 || _layers.Count - toRemove.Count < 1)
            return;

        var removedPaper = toRemove.Any(l => l.IsPaper);
        var activeWasRemoved = ActiveLayerIndex >= 0 && ActiveLayerIndex < _layers.Count
            && toRemove.Contains(_layers[ActiveLayerIndex]);
        var fallbackIndex = ActiveLayerIndex;

        BeginDocumentMutation();
        foreach (var layer in toRemove.OrderByDescending(l => _layers.IndexOf(l)).ToList())
        {
            DetachLayer(layer);
            _layers.Remove(layer);
            LayerRemoved?.Invoke(this, layer);
            layer.Dispose();
        }

        if (removedPaper)
        {
            PaperLayer = null;
            PaperColor = new Avalonia.Media.Color(0, 0, 0, 0);
        }

        RebuildFlatLayerOrder();
        if (_layers.Count == 0)
        {
            ActiveLayerIndex = -1;
        }
        else if (activeWasRemoved)
        {
            ActiveLayerIndex = Math.Clamp(fallbackIndex, 0, _layers.Count - 1);
        }
        else
        {
            ActiveLayerIndex = Math.Clamp(ActiveLayerIndex, 0, _layers.Count - 1);
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
        var layer = _layers[index];
        var oldValue = layer.IsVisible;
        var newValue = !oldValue;
        var dirtyRegion = layer.IsPaper
            ? new PixelRegion(0, 0, Width, Height)
            : LayerDirtyRegion(index);
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (l, value) => l.IsVisible = value, true, dirtyRegion));
        layer.IsVisible = newValue;
        if (layer.IsPaper)
            NotifyPaperBackdropChanged(index);
        else
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

    internal void NotifyPaperBackdropChanged(int layerIndex)
    {
        LayerMetadataChanged?.Invoke(this, new LayerMetadataChangedEventArgs(layerIndex));
        // Paper color is the compositor backdrop for every tile — partial dirty is not enough.
        Changed?.Invoke(this, new DocumentChangedEventArgs(null, layerIndex));
        MarkHistoryMutation();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
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

    public void ToggleLayerReference(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsReference;
        var newValue = !oldValue;
        var dirtyRegion = LayerDirtyRegion(index);
        PushHistoryState(new LayerPropertyHistoryState<bool>(index, oldValue, newValue, (layer, value) => layer.IsReference = value, false, dirtyRegion));
        _layers[index].IsReference = newValue;
        NotifyLayerMetadataChanged(null, index);
    }

    public void ToggleLayerClipping(int index)
    {
        if (index < 0 || index >= _layers.Count) return;
        var oldValue = _layers[index].IsClipping;
        var newValue = !oldValue;
        var dirtyRegion = LayerDirtyRegion(index);
        // Consecutive clipping layers above also change their effective base — include their bounds.
        for (int j = index + 1; j < _layers.Count; j++)
        {
            if (!_layers[j].IsClipping) break;
            dirtyRegion = dirtyRegion.Union(LayerDirtyRegion(j));
        }
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

    public void BeginActiveLayerOpacityScrub()
    {
        var layer = ActiveLayer;
        if (layer == null) return;
        _layerOpacityScrubActive = true;
        _layerOpacityScrubStart = layer.Opacity;
        _layerOpacityScrubIndex = ActiveLayerIndex;
    }

    public void PreviewActiveLayerOpacity(double opacity)
    {
        if (!_layerOpacityScrubActive)
        {
            SetActiveLayerOpacity(opacity);
            return;
        }

        if (_layerOpacityScrubIndex < 0 || _layerOpacityScrubIndex >= _layers.Count)
            return;

        var layer = _layers[_layerOpacityScrubIndex];
        var clamped = Math.Clamp(opacity, 0, 1);
        if (Math.Abs(layer.Opacity - clamped) < 0.001) return;

        layer.Opacity = clamped;
        var dirtyRegion = LayerDirtyRegion(_layerOpacityScrubIndex);
        PreviewLayerMetadataChanged(dirtyRegion, _layerOpacityScrubIndex);
    }

    public void CommitActiveLayerOpacityScrub()
    {
        if (!_layerOpacityScrubActive) return;
        _layerOpacityScrubActive = false;

        var index = _layerOpacityScrubIndex;
        _layerOpacityScrubIndex = -1;
        if (index < 0 || index >= _layers.Count) return;

        var layer = _layers[index];
        var start = _layerOpacityScrubStart;
        var end = Math.Clamp(layer.Opacity, 0, 1);
        layer.Opacity = end;
        if (Math.Abs(start - end) < 0.001) return;

        var dirtyRegion = LayerDirtyRegion(index);
        PushHistoryState(new LayerPropertyHistoryState<double>(index, start, end, (l, v) => l.Opacity = v, true, dirtyRegion));
        NotifyLayerMetadataChanged(dirtyRegion, index);
    }

    public void SetActiveLayerOpacity(double opacity)
    {
        var layer = ActiveLayer;
        if (layer == null) return;
        var clamped = Math.Clamp(opacity, 0, 1);
        if (Math.Abs(layer.Opacity - clamped) < 0.001) return;
        var oldOpacity = layer.Opacity;
        var dirtyRegion = LayerDirtyRegion(ActiveLayerIndex);
        PushHistoryState(new LayerPropertyHistoryState<double>(ActiveLayerIndex, oldOpacity, clamped, (l, v) => l.Opacity = v, true, dirtyRegion));
        layer.Opacity = clamped;
        NotifyLayerMetadataChanged(dirtyRegion, ActiveLayerIndex);
    }

    public void SetActiveLayerBlendMode(string blendMode)
    {
        var layer = ActiveLayer;
        if (layer == null) return;
        if (layer.BlendMode == blendMode) return;
        var oldMode = layer.BlendMode;
        var dirtyRegion = LayerDirtyRegion(ActiveLayerIndex);
        PushHistoryState(new LayerPropertyHistoryState<string>(ActiveLayerIndex, oldMode, blendMode, (l, value) => l.BlendMode = value, true, dirtyRegion));
        layer.BlendMode = blendMode;
        NotifyLayerMetadataChanged(dirtyRegion, ActiveLayerIndex);
    }

    public void SetActiveLayerColor(Avalonia.Media.Color? color)
    {
        var layer = ActiveLayer;
        if (layer == null) return;
        if (layer.LayerColor == color) return;
        var old = layer.LayerColor;
        var dirtyRegion = LayerDirtyRegion(ActiveLayerIndex);
        PushHistoryState(new LayerPropertyHistoryState<Avalonia.Media.Color?>(ActiveLayerIndex, old, color, (l, value) => l.LayerColor = value, true, dirtyRegion));
        layer.LayerColor = color;
        NotifyLayerMetadataChanged(dirtyRegion, ActiveLayerIndex);
    }

    public void SetActiveLayerName(string name)
    {
        var layer = ActiveLayer;
        if (layer == null) return;
        if (string.IsNullOrWhiteSpace(name) || layer.Name == name) return;
        var oldName = layer.Name;
        PushHistoryState(new LayerPropertyHistoryState<string>(ActiveLayerIndex, oldName, name, (l, value) => l.Name = value, false, PixelRegion.Empty));
        layer.Name = name;
        NotifyLayerMetadataChanged(null, ActiveLayerIndex);
    }

    public void SetExpressionColor(int layerIndex, ExpressionColorMode mode)
    {
        if (layerIndex < 0 || layerIndex >= _layers.Count) return;
        var layer = _layers[layerIndex];
        var old = layer.ExpressionColor;
        if (old == mode) return;

        var dirtyRegion = LayerDirtyRegion(layerIndex);
        PushHistoryState(new LayerPropertyHistoryState<ExpressionColorMode>(layerIndex, old, mode, (l, v) => l.ExpressionColor = v, true, dirtyRegion));
        layer.ExpressionColor = mode;
        NotifyLayerMetadataChanged(dirtyRegion, layerIndex);
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
        if (ActiveLayerIndex < 0 || ActiveLayer == null) return;
        var visible = VisibleLayerOrder().ToArray();
        var current = Array.IndexOf(visible, ActiveLayer);
        var targetVisibleIndex = current - delta;
        if (current < 0 || targetVisibleIndex < 0 || targetVisibleIndex >= visible.Length) return;

        var target = visible[targetVisibleIndex];
        // delta > 0 means "move up" (toward top of panel, earlier in visible order).
        // If the source and target are at different tree levels (e.g. moving a root
        // layer above a child of a group), use the flat index directly since
        // Above/Below placement resolves the tree structure correctly.
        MoveLayer(ActiveLayerIndex, _layers.IndexOf(target),
            delta > 0 ? LayerDropPlacement.Above : LayerDropPlacement.Below);
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

        var source = _layers[sourceIndex];
        var target = _layers[targetIndex];

        var oldParent = source.Parent;
        var oldSiblings = oldParent?.Children ?? RootLayers();
        var oldSiblingIndex = oldSiblings.IndexOf(source);

        PushHistoryState(new MoveLayerHistoryState(source, oldParent, oldSiblingIndex, ActiveLayerIndex));

        InsertLayerNear(source, target, placement);
        if (placement == LayerDropPlacement.Into) target.IsOpen = true;

        // InsertLayerNear rebuilds the flat list for root-level moves.
        // Group moves only mutate parent.Children, so rebuild here as well.
        if (source.Parent != null || placement == LayerDropPlacement.Into)
            RebuildFlatLayerOrder();

        ActiveLayerIndex = _layers.IndexOf(source);
        NotifyLayersChanged();
    }

    public void MergeSelectedLayers(IReadOnlyList<int> indices, LayerCompositor compositor)
    {
        if (indices.Count < 2) return;
        var sorted = indices.OrderBy(i => i).ToList();
        if (sorted.Any(i => i < 0 || i >= _layers.Count || _layers[i].IsLocked)) return;

        BeginDocumentMutation();

        var toMerge = sorted.Select(i => _layers[i]).ToList();
        var merged = compositor.CompositeToBgra(toMerge, Width, Height);

        var topmostLayer = _layers[sorted[^1]];

        var mergedLayer = new DrawingLayer(topmostLayer.Name, Width, Height);
        mergedLayer.Pixels.CopyFromBgra(merged, Width, Height);
        InsertLayerNear(mergedLayer, topmostLayer, LayerDropPlacement.Above);

        foreach (var layer in toMerge)
        {
            DetachLayer(layer);
            _layers.Remove(layer);
            LayerRemoved?.Invoke(this, layer);
            layer.Dispose();
        }

        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.Count > 0 ? Math.Clamp(_layers.IndexOf(mergedLayer), 0, _layers.Count - 1) : -1;
        NotifyLayersChanged();
    }

    public void FlattenGroup(int groupIndex, LayerCompositor compositor)
    {
        if (groupIndex < 0 || groupIndex >= _layers.Count) return;
        var group = _layers[groupIndex];
        if (!group.IsGroup || group.Children.Count == 0 || group.IsLocked) return;

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
            LayerRemoved?.Invoke(this, layer);
            layer.Dispose();
        }

        RebuildFlatLayerOrder();
        ActiveLayerIndex = _layers.Count > 0 ? Math.Clamp(_layers.IndexOf(flatLayer), 0, _layers.Count - 1) : -1;
        NotifyLayersChanged();
    }

    // --- Undo / Redo Execution ---
    public void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop();
        _redo.Push(state.CaptureRedo(this));
        LastHistoryChangeKind = DocumentHistoryChangeKind.Undo;
        _activeHistoryReplayKind = DocumentHistoryChangeKind.Undo;
        try
        {
            state.Restore(this);
        }
        finally
        {
            _activeHistoryReplayKind = null;
        }
        CommittedStrokeCount = Math.Max(0, CommittedStrokeCount - 1);

        _redoIds.Push(_currentStateId);
        _currentStateId = _undoIds.Pop();

        LastHistoryChangeKind = DocumentHistoryChangeKind.Undo;
        LastHistoryAffectsVisual = state.AffectsVisual;
        LastHistoryVisualDirtyRegion = state.VisualDirtyRegion;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var state = _redo.Pop();
        _undo.Push(state.CaptureRedo(this));
        LastHistoryChangeKind = DocumentHistoryChangeKind.Redo;
        _activeHistoryReplayKind = DocumentHistoryChangeKind.Redo;
        try
        {
            state.Restore(this);
        }
        finally
        {
            _activeHistoryReplayKind = null;
        }
        CommittedStrokeCount += 1;

        _undoIds.Push(_currentStateId);
        _currentStateId = _redoIds.Pop();

        LastHistoryChangeKind = DocumentHistoryChangeKind.Redo;
        LastHistoryAffectsVisual = state.AffectsVisual;
        LastHistoryVisualDirtyRegion = state.VisualDirtyRegion;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- Helpers / Notifications ---
    public void NotifyChanged(PixelRegion? dirtyRegion = null, int? layerIndex = null, bool metadataOnly = false)
    {
        if (!metadataOnly && layerIndex is { } index && index >= 0 && index < _layers.Count)
            _layers[index].MarkThumbnailDirty();
        Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex, metadataOnly));
    }

    public PixelRegion GetLayerDirtyRegion(int index) => LayerDirtyRegion(index);

    public PixelRegion GetLayersDirtyRegion(IReadOnlyList<int> indices)
    {
        var region = PixelRegion.Empty;
        foreach (var index in indices)
            region = region.Union(LayerDirtyRegion(index));
        return region.ClipTo(Width, Height);
    }
    public void NotifySelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);
    private void NotifyLayersChanged()
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, new DocumentChangedEventArgs(null, null));
        MarkHistoryMutation();
        MarkVisualHistoryChanged(affectsVisual: true, visualDirtyRegion: PixelRegion.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void PreviewLayerMetadataChanged(PixelRegion dirtyRegion, int layerIndex)
    {
        LayerMetadataChanged?.Invoke(this, new LayerMetadataChangedEventArgs(layerIndex));
        if (!dirtyRegion.IsEmpty)
            Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex, metadataOnly: true));
    }

    private void NotifyLayerMetadataChanged(PixelRegion? dirtyRegion, int? layerIndex)
    {
        if (layerIndex is { } index) LayerMetadataChanged?.Invoke(this, new LayerMetadataChangedEventArgs(index));
        if (dirtyRegion is { IsEmpty: false }) Changed?.Invoke(this, new DocumentChangedEventArgs(dirtyRegion, layerIndex));
        MarkHistoryMutation();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        DirtyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private PixelRegion LayerDirtyRegion(int index)
    {
        if (index < 0 || index >= _layers.Count) return PixelRegion.Empty;
        var layer = _layers[index];
        if (layer.Adjustment != null) return new PixelRegion(0, 0, Width, Height);
        return layer.DocumentContentBounds.ClipTo(Width, Height);
    }
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

    private static bool HasAncestorInSet(DrawingLayer layer, HashSet<DrawingLayer> set)
    {
        for (var parent = layer.Parent; parent != null; parent = parent.Parent)
            if (set.Contains(parent)) return true;
        return false;
    }

    internal void InsertLayerNear(DrawingLayer layer, DrawingLayer target, LayerDropPlacement placement)
    {
        DetachLayer(layer);

        if (placement == LayerDropPlacement.Into)
        {
            // Move into a group — append to its Children list.
            layer.Parent = target;
            target.Children.Add(layer);
            UpdateIndentTree(layer, target.IndentLevel + 1);
            return;
        }

        // Above / Below — make sibling of the target.
        var newParent = target.Parent;
        layer.Parent = newParent;

        if (newParent != null)
        {
            // Sibling insertion inside a group.
            var idx = newParent.Children.IndexOf(target);
            var insertIdx = placement == LayerDropPlacement.Above ? idx + 1 : idx;
            newParent.Children.Insert(Math.Clamp(insertIdx, 0, newParent.Children.Count), layer);
            UpdateIndentTree(layer, newParent.IndentLevel + 1);
        }
        else
        {
            // Sibling insertion at root level. Build a clean root list,
            // insert at the correct sibling position, then pass it to
            // RebuildFlatLayerOrder so root order sticks.
            var roots = RootLayers();
            roots.Remove(layer); // safety
            var idx = roots.IndexOf(target);
            var insertIdx = placement == LayerDropPlacement.Above ? idx + 1 : idx;
            roots.Insert(Math.Clamp(insertIdx, 0, roots.Count), layer);
            RebuildFlatLayerOrder(roots);
            UpdateIndentTree(layer, 0);
        }
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

    internal static DrawingLayer CloneLayerTree(DrawingLayer source, string? nameOverride = null)
    {
        var copy = new DrawingLayer(nameOverride ?? source.Name, source.Width, source.Height)
        {
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            IsAlphaLocked = source.IsAlphaLocked,
            IsReference = source.IsReference,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            IsGroup = source.IsGroup,
            IsOpen = source.IsOpen,
            IsClipping = source.IsClipping,
            IsPaper = source.IsPaper,
            IndentLevel = source.IndentLevel,
            Adjustment = source.Adjustment?.Clone()
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

    private static bool SelectionSnapshotsEqual(SelectionMask.Snapshot a, SelectionMask.Snapshot b)
    {
        if (a.Width != b.Width || a.Height != b.Height ||
            a.GeometryType != b.GeometryType ||
            a.GeometryRect != b.GeometryRect ||
            a.GeometryPolygon.Length != b.GeometryPolygon.Length)
            return false;

        for (var i = 0; i < a.GeometryPolygon.Length; i++)
        {
            if (a.GeometryPolygon[i] != b.GeometryPolygon[i])
                return false;
        }

        if (ReferenceEquals(a.Mask, b.Mask)) return true;
        if (a.Mask == null || b.Mask == null || a.Mask.Length != b.Mask.Length) return false;
        return a.Mask.AsSpan().SequenceEqual(b.Mask);
    }

    private LayerSnapshot CaptureLayerSnapshot(DrawingLayer layer) => new(layer.Name, layer.IsVisible, layer.IsLocked, layer.IsAlphaLocked, layer.IsReference, layer.IsPaper, layer.Opacity, layer.BlendMode, layer.OffsetX, layer.OffsetY, layer.IsGroup, layer.IsOpen, layer.IsClipping, layer.IndentLevel, layer.Parent is null ? -1 : _layers.IndexOf(layer.Parent), layer.Width, layer.Height, layer.CaptureTiles(), layer.Adjustment?.Clone());

    private DrawingLayer CreateLayerFromSnapshot(LayerSnapshot snap)
    {
        var layer = new DrawingLayer(snap.Name, snap.BitmapWidth, snap.BitmapHeight) { IsVisible = snap.IsVisible, IsLocked = snap.IsLocked, IsAlphaLocked = snap.IsAlphaLocked, IsReference = snap.IsReference, IsPaper = snap.IsPaper, Opacity = snap.Opacity, BlendMode = snap.BlendMode, OffsetX = snap.OffsetX, OffsetY = snap.OffsetY, IsGroup = snap.IsGroup, IsOpen = snap.IsOpen, IsClipping = snap.IsClipping, IndentLevel = snap.IndentLevel, Adjustment = snap.Adjustment?.Clone() };
        layer.RestoreTiles(snap.Tiles);
        return layer;
    }

    private DocumentSnapshot CaptureSnapshot() => new(Width, Height, ActiveLayerIndex, _layers.Select(CaptureLayerSnapshot).ToArray(), Selection.CaptureSnapshot());

    private void RestoreSnapshot(DocumentSnapshot snapshot)
    {
        Width = snapshot.Width; Height = snapshot.Height;
        Selection.RestoreSnapshot(snapshot.Selection);
        NotifyLayerRemovedRecursive(_layers);
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

        ActiveLayerIndex = _layers.Count > 0 ? Math.Clamp(snapshot.ActiveLayerIndex, 0, _layers.Count - 1) : -1;
        NotifyLayersChanged();
        NotifySelectionChanged();
    }

    // --- Records and State Classes ---
    private sealed record DocumentSnapshot(int Width, int Height, int ActiveLayerIndex, LayerSnapshot[] Layers, SelectionMask.Snapshot Selection);
    private sealed record LayerSnapshot(string Name, bool IsVisible, bool IsLocked, bool IsAlphaLocked, bool IsReference, bool IsPaper, double Opacity, string BlendMode, int OffsetX, int OffsetY, bool IsGroup, bool IsOpen, bool IsClipping, int IndentLevel, int ParentIndex, int BitmapWidth, int BitmapHeight, Dictionary<(int X, int Y), byte[]> Tiles, AdjustmentLayerData? Adjustment = null);

    private interface IHistoryState
    {
        IHistoryState CaptureRedo(DrawingDocument document);
        void Restore(DrawingDocument document);
        bool AffectsVisual { get; }
        PixelRegion VisualDirtyRegion { get; }
    }

    private sealed record CompositeHistoryState(IHistoryState[] States) : IHistoryState
    {
        public bool AffectsVisual => States.Any(static s => s.AffectsVisual);

        public PixelRegion VisualDirtyRegion
        {
            get
            {
                if (!AffectsVisual)
                    return PixelRegion.Empty;

                var region = PixelRegion.Empty;
                foreach (var state in States)
                {
                    if (!state.AffectsVisual)
                        continue;
                    if (state.VisualDirtyRegion.IsEmpty)
                        return PixelRegion.Empty;
                    region = region.IsEmpty ? state.VisualDirtyRegion : region.Union(state.VisualDirtyRegion);
                }
                return region;
            }
        }
        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            var redo = new IHistoryState[States.Length];
            for (var i = States.Length - 1; i >= 0; i--)
                redo[i] = States[i].CaptureRedo(document);
            return new CompositeHistoryState(redo);
        }

        public void Restore(DrawingDocument document)
        {
            for (var i = States.Length - 1; i >= 0; i--)
                States[i].Restore(document);
        }
    }

    private sealed record SnapshotHistoryState(DocumentSnapshot Snapshot) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => new(0, 0, Snapshot.Width, Snapshot.Height);

        public IHistoryState CaptureRedo(DrawingDocument document) => new SnapshotHistoryState(document.CaptureSnapshot());
        public void Restore(DrawingDocument document) => document.RestoreSnapshot(Snapshot);
    }

    private sealed record InsertLayerHistoryState(int InsertedIndex, int PreviousActiveIndex) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => PixelRegion.Empty;

        public IHistoryState CaptureRedo(DrawingDocument document) => new RemoveLayerHistoryState(InsertedIndex, document.ActiveLayerIndex, document.CaptureLayerSnapshot(document._layers[InsertedIndex]));
        public void Restore(DrawingDocument document) { var removed = document._layers[InsertedIndex]; document._layers.RemoveAt(InsertedIndex); document.NotifyLayerRemovedRecursive(removed); removed.Dispose(); document.ActiveLayerIndex = document._layers.Count > 0 ? Math.Clamp(PreviousActiveIndex, 0, document._layers.Count - 1) : -1; document.NotifyLayersChanged(); }
    }

    private sealed record RemoveLayerHistoryState(int RemovedIndex, int PreviousActiveIndex, LayerSnapshot RemovedSnap) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => PixelRegion.Empty;

        public IHistoryState CaptureRedo(DrawingDocument document) => new InsertLayerHistoryState(RemovedIndex, document.ActiveLayerIndex);
        public void Restore(DrawingDocument document) { document._layers.Insert(RemovedIndex, document.CreateLayerFromSnapshot(RemovedSnap)); document.ActiveLayerIndex = document._layers.Count > 0 ? Math.Clamp(PreviousActiveIndex, 0, document._layers.Count - 1) : -1; document.NotifyLayersChanged(); }
    }

    // Stores the structural position of a layer before a move — no pixels copied.
    private sealed record MoveLayerHistoryState(
        DrawingLayer Layer,
        DrawingLayer? OldParent,
        int OldSiblingIndex,
        int OldActiveIndex) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => PixelRegion.Empty;

        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            var oldParent = Layer.Parent;
            var oldSiblings = oldParent?.Children ?? document.RootLayers();
            var oldSiblingIndex = oldSiblings.IndexOf(Layer);
            return new MoveLayerHistoryState(Layer, oldParent, oldSiblingIndex, document.ActiveLayerIndex);
        }

        public void Restore(DrawingDocument document)
        {
            // Remove from current parent's children list (leave _layers alone — Rebuild will fix it).
            if (Layer.Parent != null)
                Layer.Parent.Children.Remove(Layer);
            Layer.Parent = null;

            if (OldParent != null)
            {
                // Re-insert into the group's children at the exact sibling slot.
                OldParent.Children.Remove(Layer); // safety
                Layer.Parent = OldParent;
                var idx = Math.Clamp(OldSiblingIndex, 0, OldParent.Children.Count);
                OldParent.Children.Insert(idx, Layer);
                document.RebuildFlatLayerOrder(); // no override — traverses tree from roots
            }
            else
            {
                // RootLayers() returns a fresh .ToList() copy ordered by _layers.
                // Remove the layer from wherever it currently sits in that order,
                // then insert it at the saved slot, and rebuild from that list.
                var roots = document.RootLayers();
                roots.Remove(Layer);
                var idx = Math.Clamp(OldSiblingIndex, 0, roots.Count);
                roots.Insert(idx, Layer);
                document.RebuildFlatLayerOrder(roots); // pass the modified root order
            }

            document.ActiveLayerIndex = Math.Clamp(OldActiveIndex, 0, document._layers.Count - 1);
            document.NotifyLayersChanged();
        }
    }

    private sealed record LayerOffsetHistoryState(int LayerIndex, int OldOffsetX, int OldOffsetY, int NewOffsetX, int NewOffsetY, PixelRegion DirtyRegion) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => DirtyRegion;

        public IHistoryState CaptureRedo(DrawingDocument document) => new LayerOffsetHistoryState(LayerIndex, NewOffsetX, NewOffsetY, OldOffsetX, OldOffsetY, DirtyRegion);
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; var layer = document._layers[LayerIndex]; layer.OffsetX = OldOffsetX; layer.OffsetY = OldOffsetY; document.NotifyLayerMetadataChanged(DirtyRegion, LayerIndex); }
    }

    private sealed record LayerRegionHistoryState(int LayerIndex, LayerRegionPatch[] Patches, PixelRegion DirtyRegion) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => DirtyRegion;

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
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => DirtyRegion;

        public IHistoryState CaptureRedo(DrawingDocument document)
        {
            var redoPatches = new LayerTilePatch[Patches.Length];
            for (var i = 0; i < Patches.Length; i++) { var patch = Patches[i]; redoPatches[i] = new LayerTilePatch(patch.TileX, patch.TileY, patch.AfterPixels, patch.BeforePixels); }
            return new LayerTileHistoryState(LayerIndex, redoPatches, DirtyRegion);
        }
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; var layer = document._layers[LayerIndex]; foreach (var patch in Patches) { layer.RestoreTile(patch.TileX, patch.TileY, patch.BeforePixels); } document.NotifyChanged(DirtyRegion, LayerIndex); }
    }

    private sealed record SelectionHistoryState(SelectionMask.Snapshot Before, SelectionMask.Snapshot After) : IHistoryState
    {
        public bool AffectsVisual => false;
        public PixelRegion VisualDirtyRegion => PixelRegion.Empty;

        public IHistoryState CaptureRedo(DrawingDocument document)
            => new SelectionHistoryState(After, Before);

        public void Restore(DrawingDocument document)
        {
            document.Selection.RestoreSnapshot(Before);
            document.NotifySelectionChanged();
        }
    }

    private sealed record LayerPropertyHistoryState<T>(int LayerIndex, T OldValue, T NewValue, Action<DrawingLayer, T> Apply, bool AffectsComposite, PixelRegion DirtyRegion) : IHistoryState
    {
        public bool AffectsVisual => AffectsComposite && !DirtyRegion.IsEmpty;
        public PixelRegion VisualDirtyRegion => DirtyRegion;

        public IHistoryState CaptureRedo(DrawingDocument document) => new LayerPropertyHistoryState<T>(LayerIndex, NewValue, OldValue, Apply, AffectsComposite, DirtyRegion);
        public void Restore(DrawingDocument document) { if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return; var layer = document._layers[LayerIndex]; Apply(layer, OldValue); if (AffectsComposite && layer.IsPaper) document.NotifyPaperBackdropChanged(LayerIndex); else document.NotifyLayerMetadataChanged(AffectsComposite ? DirtyRegion : null, LayerIndex); }
    }

    private sealed record DocumentPropertyHistoryState<T>(T OldValue, T NewValue, Action<T> Apply, bool AffectsVisual, PixelRegion VisualDirtyRegion) : IHistoryState
    {
        public IHistoryState CaptureRedo(DrawingDocument document) => new DocumentPropertyHistoryState<T>(NewValue, OldValue, Apply, AffectsVisual, VisualDirtyRegion);
        public void Restore(DrawingDocument document) { Apply(OldValue); document.DirtyStateChanged?.Invoke(document, EventArgs.Empty); }
    }

    private sealed record AdjustmentParamsHistoryState(int LayerIndex, AdjustmentLayerData OldParams, AdjustmentLayerData NewParams, PixelRegion DirtyRegion) : IHistoryState
    {
        public bool AffectsVisual => true;
        public PixelRegion VisualDirtyRegion => DirtyRegion;

        public IHistoryState CaptureRedo(DrawingDocument document) => new AdjustmentParamsHistoryState(LayerIndex, NewParams, OldParams, DirtyRegion);
        public void Restore(DrawingDocument document)
        {
            if (LayerIndex < 0 || LayerIndex >= document._layers.Count) return;
            var layer = document._layers[LayerIndex];
            if (layer.Adjustment == null) return;
            layer.Adjustment = OldParams.Clone();
            document.NotifyLayerMetadataChanged(DirtyRegion, LayerIndex);
        }
    }
}

public sealed class DocumentChangedEventArgs(PixelRegion? dirtyRegion, int? layerIndex, bool metadataOnly = false) : EventArgs
{
    public PixelRegion? DirtyRegion { get; } = dirtyRegion;
    public int? LayerIndex { get; } = layerIndex;
    public bool MetadataOnly { get; } = metadataOnly;
}
public sealed class LayerMetadataChangedEventArgs(int layerIndex) : EventArgs { public int LayerIndex { get; } = layerIndex; }
public readonly record struct LayerRegionPatch(PixelRegion Region, byte[] BeforePixels);
public readonly record struct LayerTileMutation(int LayerIndex, IReadOnlyDictionary<(int X, int Y), byte[]?> BeforeTiles, PixelRegion DirtyRegion);
