using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Floss.App.Document;

namespace Floss.App.Document.Assistants;

/// <summary>Rulers attached to layers; selection drives tool-property context (Object tool).</summary>
public sealed class DocumentAssistants
{
    private readonly DrawingDocument _document;

    public DocumentAssistants(DrawingDocument document)
    {
        _document = document;
    }

    public string? SelectedId { get; set; }

    public event EventHandler? Changed;

    public IReadOnlyList<PaintingAssistant> All
        => EnumerateRulers(includeHiddenLayers: true).Select(e => e.Ruler).ToList();

    public IReadOnlyList<PaintingAssistant> Rulers => All;

    public PaintingAssistant? HitTest(double x, double y, double tolerance)
        => HitTestBody(new Point(x, y), tolerance);

    public IEnumerable<(DrawingLayer Layer, PaintingAssistant Ruler)> EnumerateForRender()
        => EnumerateRulers();

    public AssistantsSnapshot CaptureSnapshot()
        => new()
        {
            LayerSets = _document.Layers
                .Select((layer, index) => (index, layer))
                .Where(pair => pair.layer.RulerSet is { HasRulers: true })
                .Select(pair => new LayerRulerSetSnapshot
                {
                    LayerIndex = pair.index,
                    Set = pair.layer.RulerSet!.Clone(),
                })
                .ToList(),
            SelectedId = SelectedId,
        };

    public void RestoreSnapshot(AssistantsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var layer in _document.Layers)
            layer.RulerSet = null;

        foreach (var entry in snapshot.LayerSets)
        {
            if (entry.LayerIndex < 0 || entry.LayerIndex >= _document.Layers.Count)
                continue;

            _document.Layers[entry.LayerIndex].RulerSet = entry.Set.Clone();
        }

        SelectedId = snapshot.SelectedId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (SelectedId == null)
            return;

        SelectedId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(PaintingAssistant assistant, bool createAtEditingLayer = true)
    {
        ArgumentNullException.ThrowIfNull(assistant);

        var layer = createAtEditingLayer
            ? _document.ResolveRulerHostLayer()
            : _document.AddRulerHostLayer();

        EnsureRulerSet(layer).Rulers.Add(assistant);
        SelectedId = assistant.Id;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        foreach (var layer in _document.Layers)
        {
            if (layer.RulerSet == null)
                continue;

            var index = layer.RulerSet.Rulers.FindIndex(r => r.Id == id);
            if (index < 0)
                continue;

            layer.RulerSet.Rulers.RemoveAt(index);
            if (layer.RulerSet.Rulers.Count == 0)
                layer.RulerSet = null;

            if (SelectedId == id)
                SelectedId = null;

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public PaintingAssistant? FindById(string? id)
    {
        if (id == null)
            return null;

        foreach (var (_, ruler) in EnumerateRulers(includeHiddenLayers: true))
        {
            if (ruler.Id == id)
                return ruler;
        }

        return null;
    }

    public DrawingLayer? FindLayerByRulerId(string? id)
    {
        if (id == null)
            return null;

        foreach (var (layer, ruler) in EnumerateRulers(includeHiddenLayers: true))
        {
            if (ruler.Id == id)
                return layer;
        }

        return null;
    }

    public void ReplaceAll(IEnumerable<PaintingAssistant> assistants, int hostLayerIndex)
    {
        foreach (var layer in _document.Layers)
            layer.RulerSet = null;

        var list = assistants.Select(a => a.Clone()).ToList();
        if (list.Count > 0 && hostLayerIndex >= 0 && hostLayerIndex < _document.Layers.Count)
        {
            var set = EnsureRulerSet(_document.Layers[hostLayerIndex]);
            set.Rulers.AddRange(list);
        }

        SelectedId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AttachLegacyRulers(IEnumerable<PaintingAssistant> rulers, int hostLayerIndex)
    {
        if (hostLayerIndex < 0 || hostLayerIndex >= _document.Layers.Count)
            return;

        var set = EnsureRulerSet(_document.Layers[hostLayerIndex]);
        set.Rulers.AddRange(rulers.Select(r => r.Clone()));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public (PaintingAssistant Assistant, int HandleIndex)? HitTestHandle(Point documentPoint, double toleranceDoc)
    {
        var tol2 = toleranceDoc * toleranceDoc;
        foreach (var (_, assistant) in EnumerateRulers())
        {
            for (var i = 1; i <= assistant.HandleCount; i++)
            {
                if (DistanceSquared(documentPoint, assistant.GetHandle(i)) <= tol2)
                    return (assistant, i);
            }
        }

        return null;
    }

    public PaintingAssistant? HitTestLine(Point documentPoint, double toleranceDoc)
        => HitTestBody(documentPoint, toleranceDoc);

    public PaintingAssistant? HitTestBody(Point documentPoint, double toleranceDoc)
    {
        var tol2 = toleranceDoc * toleranceDoc;
        foreach (var (_, assistant) in EnumerateRulers())
        {
            var hit = assistant.TypeId switch
            {
                PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType
                    => assistant.UsesFisheyeGrid
                        ? HitTestFisheye(assistant, documentPoint, tol2)
                        : HitTestPerspective(assistant, documentPoint, tol2),
                _ => DistanceToSegmentSquared(documentPoint, assistant.HandleA, assistant.HandleB) <= tol2,
            };

            if (hit)
                return assistant;
        }

        return null;
    }

    public static LayerRulerSet EnsureRulerSet(DrawingLayer layer)
    {
        layer.RulerSet ??= new LayerRulerSet();
        return layer.RulerSet;
    }

    private IEnumerable<(DrawingLayer Layer, PaintingAssistant Ruler)> EnumerateRulers(bool includeHiddenLayers = false)
    {
        foreach (var layer in _document.Layers)
        {
            if (layer.RulerSet is not { HasRulers: true, RulersVisible: true })
                continue;
            if (!includeHiddenLayers && !layer.IsVisible)
                continue;

            foreach (var ruler in layer.RulerSet.Rulers)
            {
                if (!ruler.IsVisible)
                    continue;
                yield return (layer, ruler);
            }
        }
    }

    private static bool HitTestPerspective(PaintingAssistant assistant, Point p, double tol2)
        => PerspectiveAssistantGeometry.SnapSegments(assistant)
            .Any(edge => DistanceToSegmentSquared(p, edge.A, edge.B) <= tol2);

    private static bool HitTestFisheye(PaintingAssistant assistant, Point p, double tol2)
        => FisheyeAssistantGeometry.SnapSegments(assistant)
            .Any(edge => DistanceToSegmentSquared(p, edge.A, edge.B) <= tol2);

    private static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double DistanceToSegmentSquared(Point p, Point a, Point b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        if (len2 < 1e-6)
            return DistanceSquared(p, a);

        var t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0, 1);
        var proj = new Point(a.X + abx * t, a.Y + aby * t);
        return DistanceSquared(p, proj);
    }
}
