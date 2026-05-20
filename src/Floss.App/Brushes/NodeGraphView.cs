using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Floss.App.Brushes;

public sealed class NodeGraphView : Control
{
    private const float NodeWidth = 160f;
    private const float HeaderHeight = 24f;
    private const float PortRowHeight = 22f;
    private const float PortRadius = 5f;
    private const float PortHitRadius = 8f;
    private const float SliderRowHeight = 22f;
    private const float PreviewBarHeight = 38f;
    private const float BodyPadding = 4f;
    private const float BottomPadding = 4f;
    private const float CornerRadius = 6f;

    private static readonly Color BgColor = Color.Parse("#1a1a1c");
    private static readonly Color GridColor = Color.Parse("#252528");
    private static readonly Color NodeBg = Color.Parse("#2d2d30");
    private static readonly Color NodeBorder = Color.Parse("#3a3a3e");
    private static readonly Color HeaderText = Color.Parse("#ffffff");
    private static readonly Color BodyText = Color.Parse("#c0c8d4");
    private static readonly Color MutedText = Color.Parse("#5e6878");
    private static readonly Color PortDefault = Color.Parse("#505864");
    private static readonly Color PortConnected = Color.Parse("#60a0e0");
    private static readonly Color WireColor = Color.Parse("#6090d0");
    private static readonly Color WireTemp = Color.Parse("#80b0f0");
    private static readonly Color SelectionBorder = Color.Parse("#70a0e0");
    private static readonly Color GenColor = Color.Parse("#388e7a");
    private static readonly Color CombColor = Color.Parse("#3b6ec8");
    private static readonly Color ModColor = Color.Parse("#8e6bc8");
    private static readonly Color OutColor = Color.Parse("#c86b3b");
    private static readonly Color SliderBg = Color.Parse("#202024");
    private static readonly Color SliderFill = Color.Parse("#7eb8e0");
    private static readonly Color PreviewBg = Color.Parse("#1e1e22");

    private static readonly Pen GridPen = new(new SolidColorBrush(GridColor), 1);
    private static readonly Pen WirePen = new(new SolidColorBrush(WireColor), 2);
    private static readonly Pen WireTempPen = new(new SolidColorBrush(WireTemp), 2)
        { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) };
    private static readonly Pen NodeBorderPen = new(new SolidColorBrush(NodeBorder), 1);
    private static readonly Pen SelectionBorderPen = new(new SolidColorBrush(SelectionBorder), 2);
    private static readonly SolidColorBrush NodeBgBrush = new(NodeBg);
    private static readonly SolidColorBrush PortDefaultBrush = new(PortDefault);
    private static readonly SolidColorBrush PortConnectedBrush = new(PortConnected);
    private static readonly SolidColorBrush HeaderTextBrush = new(HeaderText);
    private static readonly SolidColorBrush BodyTextBrush = new(BodyText);
    private static readonly SolidColorBrush MutedTextBrush = new(MutedText);
    private static readonly SolidColorBrush GenBrush = new(GenColor);
    private static readonly SolidColorBrush CombBrush = new(CombColor);
    private static readonly SolidColorBrush ModBrush = new(ModColor);
    private static readonly SolidColorBrush OutBrush = new(OutColor);
    private static readonly SolidColorBrush BgBrush = new(BgColor);
    private static readonly SolidColorBrush SliderBgBrush = new(SliderBg);
    private static readonly SolidColorBrush SliderFillBrush = new(SliderFill);
    private static readonly SolidColorBrush PreviewBgBrush = new(PreviewBg);

    private BrushTipNodeGraph _graph = null!;
    private Dictionary<string, Point> _positions = null!;

    private double _panX;
    private double _panY;
    private double _zoom = 1.2;

    private enum DragAction { None, Pan, MoveNode, Connect, ConnectFromInput, ScrubParam }
    private DragAction _dragAction;
    private Point _dragStartScreen;
    private Point _dragStartWorld;
    private string _dragNodeId = null!;
    private string _connectSrcId = null!;
    private string _connectTgtId = null!;
    private int _connectTgtIdx;
    private Point _connectTempEnd;
    private string _scrubNodeId = null!;
    private int _scrubParamIdx;
    private float _scrubStartValue;

    private string? _selectedNodeId;
    private static readonly Typeface SemiBoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface DefaultTypeface = Typeface.Default;

    // Undo/redo history
    private sealed record HistoryEntry(BrushTipNodeGraph Graph, Dictionary<string, Point> Positions);
    private readonly List<HistoryEntry> _history = new();
    private int _historyIndex = -1;

    // Preview cache
    private readonly Dictionary<string, IImage> _previewCache = new();

    public event Action<BrushTipNode?>? NodeSelected;
    public event Action<BrushTipNode>? NodeRightClicked;
    public event Action? GraphModified;
    public event Action? CanvasRightClicked;

    public string? SelectedNodeId => _selectedNodeId;
    public BrushTipNodeGraph Graph => _graph;

    public void LoadGraph(BrushTipNodeGraph graph, Dictionary<string, Point> positions)
    {
        _graph = graph;
        _positions = positions;
        _selectedNodeId = null;
        _history.Clear();
        _history.Add(new HistoryEntry(graph.DeepClone(), new(positions)));
        _historyIndex = 0;
        InvalidatePreviews();
        InvalidateVisual();
    }

    private void PushHistory()
    {
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(new HistoryEntry(_graph.DeepClone(), new(_positions)));
        _historyIndex = _history.Count - 1;
        if (_history.Count > 300)
        {
            _history.RemoveAt(0);
            _historyIndex--;
        }
        InvalidatePreviews();
    }

    private void InvalidatePreviews()
    {
        foreach (var img in _previewCache.Values)
        {
            if (img is IDisposable d) d.Dispose();
        }
        _previewCache.Clear();
    }

    private IImage GetNodePreview(BrushTipNode node)
    {
        if (_previewCache.TryGetValue(node.Id, out var cached))
            return cached;

        const int prevSize = 32;
        var tempGraph = _graph.DeepClone();
        tempGraph.OutputNodeId = node.Id;
        var bitmap = BrushTipNodeGraphEvaluator.Evaluate(tempGraph, prevSize, 1.0f);

        using var skImg = SKImage.FromBitmap(bitmap);
        var png = skImg.Encode(SKEncodedImageFormat.Png, 60);
        var stream = new MemoryStream(png.ToArray());
        var avaloniaBmp = new Bitmap(stream);
        _previewCache[node.Id] = avaloniaBmp;
        return avaloniaBmp;
    }

    public void Undo()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        RestoreEntry(_history[_historyIndex]);
    }

    public void Redo()
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        RestoreEntry(_history[_historyIndex]);
    }

    private void RestoreEntry(HistoryEntry entry)
    {
        _graph = entry.Graph.DeepClone();
        _positions = new Dictionary<string, Point>(entry.Positions);
        _selectedNodeId = null;
        NodeSelected?.Invoke(null);
        InvalidateVisual();
        GraphModified?.Invoke();
    }

    public BrushTipNode? AddNode(BrushTipNodeKind kind)
    {
        var id = $"{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid().ToString("N").AsSpan(0, 6)}";
        var node = new BrushTipNode { Id = id, Kind = kind };
        var centerWorld = ScreenToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2));
        var offset = new Point(_positions.Count * 20.0, _positions.Count * 20.0);
        _positions[id] = centerWorld + offset;

        var outputIdx = _graph.Nodes.FindIndex(n => n.Id == _graph.OutputNodeId);
        if (outputIdx >= 0)
            _graph.Nodes.Insert(outputIdx, node);
        else
            _graph.Nodes.Add(node);

        _selectedNodeId = id;
        PushHistory();
        InvalidateVisual();
        NodeSelected?.Invoke(node);
        GraphModified?.Invoke();
        return node;
    }

    public void DeleteNode(string nodeId)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null || nodeId == _graph.OutputNodeId) return;
        PushHistory();
        _graph.Nodes.Remove(node);
        _positions.Remove(nodeId);
        foreach (var other in _graph.Nodes)
            other.Inputs.RemoveAll(id => id == nodeId);
        if (_selectedNodeId == nodeId)
        {
            _selectedNodeId = null;
            NodeSelected?.Invoke(null);
        }
        InvalidateVisual();
        GraphModified?.Invoke();
    }

    public void SetView(double panX, double panY, double zoom)
    {
        _panX = panX;
        _panY = panY;
        _zoom = Math.Clamp(zoom, 0.1, 10.0);
        InvalidateVisual();
    }

    private Point ScreenToWorld(Point screen) =>
        new((screen.X - _panX) / _zoom, (screen.Y - _panY) / _zoom);

    private static int NodeInputCount(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Output => 1,
        BrushTipNodeKind.Threshold or BrushTipNodeKind.Invert => 1,
        BrushTipNodeKind.Add or BrushTipNodeKind.Multiply or BrushTipNodeKind.Max
            or BrushTipNodeKind.Min or BrushTipNodeKind.Subtract or BrushTipNodeKind.Mix => 2,
        _ => 0
    };

    private float GetNodeHeight(BrushTipNode node)
    {
        var inputCount = NodeInputCount(node.Kind);
        var paramCount = GetNodeParams(node.Kind).Length;
        var bodyHeight = BodyPadding;
        if (inputCount > 0)
            bodyHeight += inputCount * PortRowHeight;
        if (paramCount > 0)
        {
            bodyHeight += BodyPadding;
            bodyHeight += paramCount * SliderRowHeight;
            bodyHeight += PreviewBarHeight + 4;
        }
        else if (inputCount > 0)
        {
            bodyHeight += PreviewBarHeight + 4;
        }
        return HeaderHeight + bodyHeight + BottomPadding;
    }

    private static SolidColorBrush HeaderBrush(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Circle or BrushTipNodeKind.Rectangle
            or BrushTipNodeKind.RoundedRectangle
            or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
            or BrushTipNodeKind.Noise or BrushTipNodeKind.Bristle => GenBrush,
        BrushTipNodeKind.Add or BrushTipNodeKind.Multiply
            or BrushTipNodeKind.Max or BrushTipNodeKind.Min
            or BrushTipNodeKind.Subtract or BrushTipNodeKind.Mix => CombBrush,
        BrushTipNodeKind.Threshold or BrushTipNodeKind.Invert => ModBrush,
        BrushTipNodeKind.Output => OutBrush,
        _ => GenBrush
    };

    private sealed record NodeParam(
        string Name, float Min, float Max,
        Func<BrushTipNode, float> Get,
        Action<BrushTipNode, float> Set);

    private static NodeParam[] GetNodeParams(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Circle => new NodeParam[] {
            new("Radius", 0f, 1f, n => n.Radius, (n, v) => n.Radius = v),
            new("Hardness", 0f, 1f, n => n.Hardness, (n, v) => n.Hardness = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
            new("Rotation", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        BrushTipNodeKind.Rectangle => new NodeParam[] {
            new("Width", 0f, 1f, n => n.Width, (n, v) => n.Width = v),
            new("Height", 0f, 1f, n => n.Height, (n, v) => n.Height = v),
            new("Hardness", 0f, 1f, n => n.Hardness, (n, v) => n.Hardness = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
            new("Rotation", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        BrushTipNodeKind.Noise => new NodeParam[] {
            new("Density", 0f, 1f, n => n.Density, (n, v) => n.Density = v),
            new("Scale", 0.05f, 64f, n => n.Scale, (n, v) => n.Scale = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Bristle => new NodeParam[] {
            new("Density", 0f, 1f, n => n.Density, (n, v) => n.Density = v),
            new("Width", 0f, 1f, n => n.Width, (n, v) => n.Width = v),
            new("Height", 0f, 1f, n => n.Height, (n, v) => n.Height = v),
            new("Hardness", 0f, 1f, n => n.Hardness, (n, v) => n.Hardness = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.LinearGradient => new NodeParam[] {
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Stripe => new NodeParam[] {
            new("Scale", 1f, 128f, n => n.Scale, (n, v) => n.Scale = v),
            new("Density", 0f, 1f, n => n.Density, (n, v) => n.Density = v),
            new("Hardness", 0f, 1f, n => n.Hardness, (n, v) => n.Hardness = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
            new("Rotation", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        BrushTipNodeKind.Threshold => new NodeParam[] {
            new("Threshold", 0f, 1f, n => n.Threshold, (n, v) => n.Threshold = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Mix => new NodeParam[] {
            new("Factor", 0f, 1f, n => n.Density, (n, v) => n.Density = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Invert => new NodeParam[] {
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.RoundedRectangle => new NodeParam[] {
            new("Width", 0f, 1f, n => n.Width, (n, v) => n.Width = v),
            new("Height", 0f, 1f, n => n.Height, (n, v) => n.Height = v),
            new("Radius", 0f, 1f, n => n.Radius, (n, v) => n.Radius = v),
            new("Hardness", 0f, 1f, n => n.Hardness, (n, v) => n.Hardness = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
            new("Rotation", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        _ => Array.Empty<NodeParam>(),
    };

    private static string NodeKindDisplayName(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.LinearGradient => "Linear Grad",
        BrushTipNodeKind.RoundedRectangle => "Round Rect",
        _ => kind.ToString()
    };

    private Point GetOutputPortPos(BrushTipNode node, Point nodePos) =>
        new(nodePos.X + NodeWidth, nodePos.Y + HeaderHeight / 2);

    private Point GetInputPortPos(BrushTipNode node, Point nodePos, int index) =>
        new(nodePos.X, nodePos.Y + HeaderHeight + BodyPadding + index * PortRowHeight + PortRowHeight / 2);

    // ── Slider bar bounds (world-space, relative to node position) ─────────────
    private static Rect SliderBarRect(Point pos, int paramIndex, int inputCount)
    {
        var y = pos.Y + HeaderHeight + BodyPadding;
        if (inputCount > 0) y += inputCount * PortRowHeight + BodyPadding;
        y += paramIndex * SliderRowHeight + 4;
        return new Rect(pos.X + 58, y, NodeWidth - 98, 14);
    }

    // ── Preview bar bounds ─────────────────────────────────────────────────────
    private static Rect PreviewBarRect(Point pos, BrushTipNode node)
    {
        var paramCount = GetNodeParams(node.Kind).Length;
        var inputCount = NodeInputCount(node.Kind);
        var y = pos.Y + HeaderHeight + BodyPadding;
        if (inputCount > 0) y += inputCount * PortRowHeight + BodyPadding;
        if (paramCount > 0) y += paramCount * SliderRowHeight + 4;
        else if (inputCount > 0) { /* just after inputs */ }
        return new Rect(pos.X + 4, y, NodeWidth - 8, PreviewBarHeight);
    }

    public override void Render(DrawingContext context)
    {
        if (_graph == null || _positions == null) return;

        context.DrawRectangle(BgBrush, null, new Rect(Bounds.Size));
        DrawGrid(context);

        var transform = Matrix.CreateTranslation(_panX, _panY) * Matrix.CreateScale(_zoom, _zoom);
        using (context.PushTransform(transform))
        {
            DrawWires(context);
            DrawNodes(context);
            DrawPendingWire(context);
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        var spacing = Math.Max(2, 30 * _zoom);
        var startX = ((_panX % spacing) + spacing) % spacing;
        var startY = ((_panY % spacing) + spacing) % spacing;

        for (var x = startX; x < Bounds.Width; x += spacing)
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        for (var y = startY; y < Bounds.Height; y += spacing)
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
    }

    private void DrawWires(DrawingContext context)
    {
        foreach (var node in _graph.Nodes)
        {
            if (!_positions.TryGetValue(node.Id, out var nodePos)) continue;
            for (var i = 0; i < node.Inputs.Count; i++)
            {
                var srcId = node.Inputs[i];
                if (string.IsNullOrEmpty(srcId)) continue;
                if (!_positions.TryGetValue(srcId, out var srcPos)) continue;
                var srcNode = _graph.Nodes.FirstOrDefault(n => n.Id == srcId);
                if (srcNode == null) continue;

                var start = GetOutputPortPos(srcNode, srcPos);
                var end = GetInputPortPos(node, nodePos, i);
                DrawBezier(context, start, end, WirePen);
            }
        }
    }

    private static void DrawBezier(DrawingContext context, Point start, Point end, Pen pen)
    {
        var dx = Math.Max(Math.Abs(end.X - start.X) * 0.5f, 30f);
        var cp1 = new Point(start.X + dx, start.Y);
        var cp2 = new Point(end.X - dx, end.Y);

        var geo = new StreamGeometry();
        using (var gctx = geo.Open())
        {
            gctx.BeginFigure(start, false);
            gctx.CubicBezierTo(cp1, cp2, end);
            gctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geo);
    }

    private void DrawPendingWire(DrawingContext context)
    {
        if (_graph == null || _positions == null) return;

        if (_dragAction == DragAction.Connect && _connectSrcId != null)
        {
            if (_graph.Nodes.FirstOrDefault(n => n.Id == _connectSrcId) is { } srcNode
                && _positions.TryGetValue(_connectSrcId, out var srcPos))
                DrawBezier(context, GetOutputPortPos(srcNode, srcPos), _connectTempEnd, WireTempPen);
        }
        else if (_dragAction == DragAction.ConnectFromInput && _connectTgtId != null)
        {
            if (_graph.Nodes.FirstOrDefault(n => n.Id == _connectTgtId) is { } tgtNode
                && _positions.TryGetValue(_connectTgtId, out var tgtPos))
                DrawBezier(context, _connectTempEnd, GetInputPortPos(tgtNode, tgtPos, _connectTgtIdx), WireTempPen);
        }
    }

    private void DrawNodes(DrawingContext context)
    {
        foreach (var node in _graph.Nodes)
        {
            if (_positions.TryGetValue(node.Id, out var pos))
                DrawNode(context, node, pos);
        }
    }

    private void DrawNode(DrawingContext context, BrushTipNode node, Point pos)
    {
        var height = GetNodeHeight(node);
        var headerColor = HeaderBrush(node.Kind);
        var isSelected = node.Id == _selectedNodeId;

        var shadowBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        context.DrawRectangle(shadowBrush, null,
            new Rect(pos.X + 3, pos.Y + 3, NodeWidth, height), CornerRadius, CornerRadius);

        context.DrawRectangle(
            NodeBgBrush,
            isSelected ? SelectionBorderPen : NodeBorderPen,
            new Rect(pos.X, pos.Y, NodeWidth, height), CornerRadius, CornerRadius);

        DrawRoundedTopRect(context, headerColor, pos.X, pos.Y, NodeWidth, HeaderHeight, CornerRadius);

        var kindName = NodeKindDisplayName(node.Kind).ToUpperInvariant();
        var ft = new FormattedText(kindName, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, SemiBoldTypeface, 11, HeaderTextBrush);
        context.DrawText(ft,
            new Point(pos.X + 8, pos.Y + (HeaderHeight - ft.Height) / 2));

        // Output port
        var outPos = GetOutputPortPos(node, pos);
        if (node.Kind != BrushTipNodeKind.Output)
            context.DrawEllipse(PortConnectedBrush, null, outPos, PortRadius, PortRadius);

        // Input ports
        var y = (float)pos.Y + HeaderHeight + BodyPadding;
        var inputCount = NodeInputCount(node.Kind);

        for (var i = 0; i < inputCount; i++)
        {
            var ip = GetInputPortPos(node, pos, i);
            var connected = i < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[i]);
            context.DrawEllipse(
                connected ? PortConnectedBrush : PortDefaultBrush, null,
                ip, PortRadius, PortRadius);

            var inputLabel = node.Kind switch
            {
                BrushTipNodeKind.Output => "output",
                BrushTipNodeKind.Threshold => "mask",
                BrushTipNodeKind.Invert => "input",
                _ => i == 0 ? "A" : "B"
            };
            var lft = new FormattedText(inputLabel, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 10, MutedTextBrush);
            context.DrawText(lft,
                new Point(pos.X + PortRadius + 6, ip.Y - lft.Height / 2));

            y += PortRowHeight;
        }

        // Slider rows
        if (inputCount > 0) y += BodyPadding;
        var nodeParams = GetNodeParams(node.Kind);
        foreach (var param in nodeParams)
        {
            var value = param.Get(node);
            var fraction = (value - param.Min) / Math.Max(0.0001f, param.Max - param.Min);

            // Label
            var labelText = new FormattedText(param.Name, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 9, MutedTextBrush);
            context.DrawText(labelText, new Point(pos.X + 4, y + (SliderRowHeight - labelText.Height) / 2));

            // Slider background + fill
            var barRect = SliderBarRect(pos, Array.IndexOf(nodeParams, param), inputCount);
            context.DrawRectangle(SliderBgBrush, null, barRect, 3, 3);
            if (fraction > 0.001f)
            {
                var fillRect = new Rect(barRect.X, barRect.Y, barRect.Width * fraction, barRect.Height);
                context.DrawRectangle(SliderFillBrush, null, fillRect, 3, 3);
            }

            // Value text
            var valText = new FormattedText(value.ToString("F2"), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 9, BodyTextBrush);
            context.DrawText(valText, new Point(pos.X + NodeWidth - 36, y + (SliderRowHeight - valText.Height) / 2));

            y += SliderRowHeight;
        }

        // Preview bar at bottom
        if (nodeParams.Length > 0 || inputCount > 0)
        {
            var prev = PreviewBarRect(pos, node);
            context.DrawRectangle(PreviewBgBrush, null, prev, 3, 3);

            try
            {
                var previewImage = GetNodePreview(node);
                var padding = 3.0;
                var imgRect = new Rect(prev.X + padding, prev.Y + padding,
                    prev.Width - padding * 2, prev.Height - padding * 2);
                context.DrawImage(previewImage, new Rect(previewImage.Size), imgRect);
            }
            catch
            {
                // Fallback: simple opacity fill if evaluation fails
                var opacity = Math.Clamp(node.Opacity, 0f, 1f);
                if (opacity > 0.01f)
                {
                    var fillColor = node.Kind switch
                    {
                        BrushTipNodeKind.Noise => Color.FromArgb(180, 180, 180, 180),
                        _ => Color.FromArgb(180, 120, 168, 240)
                    };
                    var fill = new Rect(prev.X + 4, prev.Y + 4, prev.Width - 8, prev.Height - 8);
                    context.DrawRectangle(new SolidColorBrush(fillColor), null, fill, 3, 3);
                }
            }
        }
    }

    private static void DrawRoundedTopRect(DrawingContext context, IBrush brush,
        double x, double y, double width, double height, double radius)
    {
        using (context.PushClip(new Rect(x, y, width, height + radius)))
        {
            context.DrawRectangle(brush, null,
                new RoundedRect(new Rect(x, y, width, height), new CornerRadius(radius, radius, 0, 0)));
        }
    }

    // ── Auto-layout ────────────────────────────────────────────────────────────

    public void AutoLayout()
    {
        if (_graph == null) return;

        var depths = new Dictionary<string, int>();
        var outputNode = _graph.Nodes.FirstOrDefault(n => n.Id == _graph.OutputNodeId);
        if (outputNode != null)
            AssignDepth(outputNode.Id, 0, new HashSet<string>(), depths);

        var unvisited = _graph.Nodes
            .Where(n => !depths.ContainsKey(n.Id))
            .Select(n => n.Id)
            .ToList();

        var maxDepth = depths.Count > 0 ? depths.Values.Max() : 0;
        var normalized = depths.ToDictionary(kv => kv.Key, kv => maxDepth - kv.Value);

        foreach (var uid in unvisited)
            normalized[uid] = -1;

        var byDepth = normalized.GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

        var xSpacing = NodeWidth + 60f;
        var ySpacing = 100f;

        _positions.Clear();
        foreach (var (depth, nodeIds) in byDepth.OrderBy(kv => kv.Key))
        {
            var yStart = -(nodeIds.Count - 1) * ySpacing / 2f;
            for (var i = 0; i < nodeIds.Count; i++)
            {
                _positions[nodeIds[i]] = new Point(
                    50 + (depth + 1) * xSpacing,
                    50 + yStart + i * ySpacing);
            }
        }

        InvalidateVisual();
    }

    private void AssignDepth(string nodeId, int depth, HashSet<string> visited, Dictionary<string, int> depths)
    {
        if (!visited.Add(nodeId)) return;
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return;

        if (!depths.ContainsKey(nodeId) || depth > depths[nodeId])
            depths[nodeId] = depth;

        foreach (var inputId in node.Inputs)
        {
            if (string.IsNullOrEmpty(inputId)) continue;
            AssignDepth(inputId, depth + 1, visited, depths);
        }
    }

    // ── Pointer events ─────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_graph == null || _positions == null) return;

        var point = e.GetCurrentPoint(this);
        var screenPos = point.Position;
        var worldPos = ScreenToWorld(screenPos);

        if (point.Properties.IsMiddleButtonPressed)
        {
            _dragAction = DragAction.Pan;
            _dragStartScreen = screenPos;
            _dragStartWorld = new Point(_panX, _panY);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            // Hit-test output ports (reverse order).
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (node.Kind == BrushTipNodeKind.Output) continue;
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var op = GetOutputPortPos(node, np);
                if (Dist(worldPos, op) < PortHitRadius)
                {
                    _dragAction = DragAction.Connect;
                    _connectSrcId = node.Id;
                    _connectTempEnd = worldPos;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }

            // Hit-test input ports for bidirectional connect.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var inputCount = NodeInputCount(node.Kind);
                for (var i = 0; i < inputCount; i++)
                {
                    var ip = GetInputPortPos(node, np, i);
                    if (Dist(worldPos, ip) < PortHitRadius)
                    {
                        _dragAction = DragAction.ConnectFromInput;
                        _connectTgtId = node.Id;
                        _connectTgtIdx = i;
                        _connectTempEnd = worldPos;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Hit-test slider bars.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var nodeParams = GetNodeParams(node.Kind);
                var inputCount = NodeInputCount(node.Kind);
                for (var pi = 0; pi < nodeParams.Length; pi++)
                {
                    var bar = SliderBarRect(np, pi, inputCount);
                    if (bar.Contains(worldPos))
                    {
                        _dragAction = DragAction.ScrubParam;
                        _scrubNodeId = node.Id;
                        _scrubParamIdx = pi;
                        _scrubStartValue = nodeParams[pi].Get(node);
                        _dragStartScreen = screenPos;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Hit-test node bodies (reverse order).
            BrushTipNode? hitNode = null;
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var h = GetNodeHeight(node);
                if (new Rect(np.X, np.Y, NodeWidth, h).Contains(worldPos))
                {
                    hitNode = node;
                    break;
                }
            }

            if (hitNode != null)
            {
                var headerRect = new Rect(_positions[hitNode.Id].X, _positions[hitNode.Id].Y,
                    NodeWidth, HeaderHeight);
                if (headerRect.Contains(worldPos))
                {
                    _dragAction = DragAction.MoveNode;
                    _dragNodeId = hitNode.Id;
                    _dragStartWorld = worldPos;
                    e.Pointer.Capture(this);
                }

                _selectedNodeId = hitNode.Id;
                NodeSelected?.Invoke(hitNode);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Empty space: deselect then pan.
            if (_selectedNodeId != null)
            {
                _selectedNodeId = null;
                NodeSelected?.Invoke(null);
                InvalidateVisual();
            }
            _dragAction = DragAction.Pan;
            _dragStartScreen = screenPos;
            _dragStartWorld = new Point(_panX, _panY);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Right-click input port → disconnect.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var inputCount = NodeInputCount(node.Kind);
                for (var i = 0; i < inputCount; i++)
                {
                    var ip = GetInputPortPos(node, np, i);
                    if (Dist(worldPos, ip) < PortHitRadius)
                    {
                        if (i < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[i]))
                        {
                            PushHistory();
                            node.Inputs[i] = "";
                            GraphModified?.Invoke();
                            InvalidateVisual();
                        }
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Right-click on node body → context menu event.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var h = GetNodeHeight(node);
                if (new Rect(np.X, np.Y, NodeWidth, h).Contains(worldPos))
                {
                    NodeRightClicked?.Invoke(node);
                    e.Handled = true;
                    return;
                }
            }

            // Right-click on empty canvas → add-node context menu.
            CanvasRightClicked?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_graph == null || _positions == null) return;

        var screenPos = e.GetCurrentPoint(this).Position;
        var worldPos = ScreenToWorld(screenPos);

        switch (_dragAction)
        {
            case DragAction.Pan:
                _panX = _dragStartWorld.X + (screenPos.X - _dragStartScreen.X);
                _panY = _dragStartWorld.Y + (screenPos.Y - _dragStartScreen.Y);
                InvalidateVisual();
                break;

            case DragAction.MoveNode:
                if (_dragNodeId != null && _positions.ContainsKey(_dragNodeId))
                {
                    var delta = worldPos - _dragStartWorld;
                    _positions[_dragNodeId] = new Point(
                        _positions[_dragNodeId].X + delta.X,
                        _positions[_dragNodeId].Y + delta.Y);
                    _dragStartWorld = worldPos;
                    InvalidateVisual();
                }
                break;

            case DragAction.Connect:
            case DragAction.ConnectFromInput:
                _connectTempEnd = worldPos;
                InvalidateVisual();
                break;

            case DragAction.ScrubParam:
                if (_scrubNodeId != null)
                {
                    var node = _graph.Nodes.FirstOrDefault(n => n.Id == _scrubNodeId);
                    if (node == null) break;
                    var nodeParams = GetNodeParams(node.Kind);
                    if (_scrubParamIdx >= nodeParams.Length) break;
                    var param = nodeParams[_scrubParamIdx];
                    var dx = (screenPos.X - _dragStartScreen.X) / Math.Max(1.0, _zoom * 150.0);
                    var range = param.Max - param.Min;
                    var newValue = (float)Math.Clamp(_scrubStartValue + dx * range, param.Min, param.Max);
                    if (Math.Abs(newValue - param.Get(node)) > 0.0001f)
                    {
                        param.Set(node, newValue);
                        InvalidateVisual();
                    }
                }
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var worldPos = ScreenToWorld(e.GetCurrentPoint(this).Position);

        if (_dragAction == DragAction.Connect && _connectSrcId != null)
        {
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (node.Id == _connectSrcId) continue;
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var inputCount = NodeInputCount(node.Kind);
                for (var i = 0; i < inputCount; i++)
                {
                    if (Dist(worldPos, GetInputPortPos(node, np, i)) < PortHitRadius)
                    {
                        PushHistory();
                        while (node.Inputs.Count <= i) node.Inputs.Add("");
                        node.Inputs[i] = _connectSrcId;
                        GraphModified?.Invoke();
                        InvalidateVisual();
                        goto done;
                    }
                }
            }
        }
        else if (_dragAction == DragAction.ConnectFromInput && _connectTgtId != null)
        {
            // Input → output: find output port to connect from.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (node.Id == _connectTgtId || node.Kind == BrushTipNodeKind.Output) continue;
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                if (Dist(worldPos, GetOutputPortPos(node, np)) < PortHitRadius)
                {
                    PushHistory();
                    var tgtNode = _graph.Nodes.FirstOrDefault(n => n.Id == _connectTgtId);
                    if (tgtNode != null)
                    {
                        while (tgtNode.Inputs.Count <= _connectTgtIdx) tgtNode.Inputs.Add("");
                        tgtNode.Inputs[_connectTgtIdx] = node.Id;
                        GraphModified?.Invoke();
                        InvalidateVisual();
                    }
                    goto done;
                }
            }
            // Released on empty space: disconnect the input's existing wire.
            var tgtN = _graph.Nodes.FirstOrDefault(n => n.Id == _connectTgtId);
            if (tgtN != null && _connectTgtIdx < tgtN.Inputs.Count
                && !string.IsNullOrEmpty(tgtN.Inputs[_connectTgtIdx]))
            {
                PushHistory();
                tgtN.Inputs[_connectTgtIdx] = "";
                GraphModified?.Invoke();
                InvalidateVisual();
            }
        }
        else if (_dragAction == DragAction.ScrubParam && _scrubNodeId != null)
        {
            var node = _graph.Nodes.FirstOrDefault(n => n.Id == _scrubNodeId);
            if (node != null)
            {
                var nodeParams = GetNodeParams(node.Kind);
                if (_scrubParamIdx < nodeParams.Length)
                {
                    PushHistory();
                    InvalidateVisual();
                    GraphModified?.Invoke();
                }
            }
        }

        done:
        if (_dragAction != DragAction.None)
            e.Pointer.Capture(null);
        _dragAction = DragAction.None;
        _dragNodeId = null!;
        _connectSrcId = null!;
        _connectTgtId = null!;
        _scrubNodeId = null!;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_graph == null) return;

        var pos = e.GetPosition(this);
        var worldBefore = ScreenToWorld(pos);

        var zoomDelta = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        _zoom = Math.Clamp(_zoom * zoomDelta, 0.1, 10.0);

        _panX = pos.X - worldBefore.X * _zoom;
        _panY = pos.Y - worldBefore.Y * _zoom;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (_selectedNodeId != null && _selectedNodeId != _graph?.OutputNodeId)
            {
                DeleteNode(_selectedNodeId);
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    private static double Dist(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
