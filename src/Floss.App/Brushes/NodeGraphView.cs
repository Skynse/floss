using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Floss.App.Input;
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
    private const float SliderLabelWidth = 48f;
    private const float SliderValueWidth = 36f;
    private const float SliderBarGap = 4f;
    private const float ImageSelectorRowHeight = 26f;
    private const float PreviewBarHeight = 38f;
    private const float ImageTipPreviewSize = 72f;
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

    private enum DragAction { None, Pan, Zoom, MoveNode, Connect, ConnectFromInput, DisconnectWire, ScrubParam }
    private DragAction _dragAction;
    private bool _spaceHeld;
    private CanvasAction _shortcutAction;
    private double _dragStartZoom;
    private Point _zoomAnchorScreen;
    private Point _lastZoomScreen;
    private int _zoomDirection = 1;
    private Point _dragStartScreen;
    private Point _dragStartWorld;
    private string _dragNodeId = null!;
    private string _connectSrcId = null!;
    private string _connectTgtId = null!;
    private int _connectTgtIdx;
    private Point _connectTempEnd;
    private string _scrubNodeId = null!;
    private int _scrubParamIdx;

    private string? _selectedNodeId;
    private static readonly Typeface SemiBoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface DefaultTypeface = Typeface.Default;

    // Undo/redo history
    private sealed record HistoryEntry(BrushTipNodeGraph Graph, Dictionary<string, Point> Positions);
    private readonly List<HistoryEntry> _history = new();
    private int _historyIndex = -1;

    // Preview cache
    private readonly Dictionary<string, IImage> _previewCache = new();
    private List<ImageSamplerOption> _imageSamplers = [];
    private IReadOnlyList<BrushTipData> _materialTips = [];

    private Popup? _paramEditPopup;
    private TextBox? _paramEditBox;
    private string? _paramEditNodeId;
    private int _paramEditParamIdx = -1;
    private float _paramEditOriginalValue;

    public event Action<BrushTipNode?>? NodeSelected;
    public event Action<BrushTipNode>? NodeRightClicked;
    public event Action? GraphModified;
    public event Action<Point>? CanvasRightClicked;
    public event Action<string, string>? ImageSamplerChanged;

    public string? SelectedNodeId => _selectedNodeId;
    public BrushTipNodeGraph Graph => _graph;

    public NodeGraphView()
    {
        Focusable = true;
        ClipToBounds = true;
        DetachedFromVisualTree += (_, _) => ReleaseTransientCaches();
    }

    public void LoadGraph(BrushTipNodeGraph graph, Dictionary<string, Point> positions)
    {
        _graph = graph.DeepClone();
        _positions = new Dictionary<string, Point>(positions);
        _selectedNodeId = null;
        _history.Clear();
        _history.Add(new HistoryEntry(_graph.DeepClone(), new(_positions)));
        _historyIndex = 0;
        InvalidatePreviews();
        InvalidateVisual();
    }

    public void SetImageSamplerOptions(IReadOnlyList<BrushTipData>? tips)
    {
        _materialTips = tips?.Select(BrushMaterialTips.NormalizeTip).ToList() ?? [];
        _imageSamplers = ImageSamplerOptions.FromTips(_materialTips);
        InvalidatePreviews();
        InvalidateVisual();
    }

    public IReadOnlyList<ImageSamplerOption> AvailableImageSamplers => _imageSamplers;

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

    private void ReleaseTransientCaches()
    {
        CloseParamEdit(revert: true);
        InvalidatePreviews();
        _history.Clear();
        _historyIndex = -1;
    }

    private IImage GetNodePreview(BrushTipNode node)
    {
        if (_previewCache.TryGetValue(node.Id, out var cached))
            return cached;

        const int prevSize = 32;

        if (node.Kind == BrushTipNodeKind.ImageSampler)
        {
            var png = BrushMaterialTips.ResolveSamplerPng(node, _materialTips);
            if (png.Length > 0)
            {
                var thumb = DecodeImagePreview(png);
                _previewCache[node.Id] = thumb;
                return thumb;
            }
        }

        if (node.Kind == BrushTipNodeKind.Coordinates)
        {
            var uv = CreateUvPreview(prevSize);
            _previewCache[node.Id] = uv;
            return uv;
        }

        var tempGraph = _graph.DeepClone();
        tempGraph.OutputNodeId = node.Id;
        using var bitmap = BrushTipNodeGraphEvaluator.EvaluateColor(tempGraph, prevSize, 1.0f, _materialTips)
            ?? BrushTipNodeGraphEvaluator.Evaluate(tempGraph, prevSize, 1.0f, _materialTips);
        using var skImg = SKImage.FromBitmap(bitmap);
        using var encoded = skImg.Encode(SKEncodedImageFormat.Png, 60);
        using var stream = new MemoryStream(encoded.ToArray());
        var avaloniaBmp = new Bitmap(stream);
        _previewCache[node.Id] = avaloniaBmp;
        return avaloniaBmp;
    }

    private static Bitmap DecodeImagePreview(byte[] pngBytes)
    {
        using var source = SKBitmap.Decode(pngBytes)
            ?? throw new InvalidDataException("Brush tip PNG could not be decoded.");
        const int maxDim = 64;
        var scale = Math.Min(maxDim / (float)source.Width, maxDim / (float)source.Height);
        var width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(source.Height * scale));

        using var scaled = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!source.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
            throw new InvalidDataException("Brush tip PNG could not be scaled.");

        using var skImg = SKImage.FromBitmap(scaled);
        using var png = skImg.Encode(SKEncodedImageFormat.Png, 80);
        using var stream = new MemoryStream(png.ToArray());
        return new Bitmap(stream);
    }

    private static Bitmap CreateUvPreview(int size)
    {
        size = Math.Max(8, size);
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (byte)((x / (float)(size - 1)) * 255f + 0.5f);
                var v = (byte)((y / (float)(size - 1)) * 255f + 0.5f);
                bitmap.SetPixel(x, y, new SKColor(u, v, 128));
            }
        }

        using var skImg = SKImage.FromBitmap(bitmap);
        using var png = skImg.Encode(SKEncodedImageFormat.Png, 80);
        using var stream = new MemoryStream(png.ToArray());
        return new Bitmap(stream);
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
        InvalidatePreviews();
        InvalidateVisual();
        GraphModified?.Invoke();
    }

    public BrushTipNode? AddNode(BrushTipNodeKind kind, Point? worldPosition = null)
    {
        var id = $"{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid().ToString("N").AsSpan(0, 6)}";
        var node = new BrushTipNode { Id = id, Kind = kind };
        var centerWorld = ScreenToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2));
        var offset = new Point(_positions.Count * 20.0, _positions.Count * 20.0);
        _positions[id] = worldPosition ?? centerWorld + offset;

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
        _graph.Nodes.Remove(node);
        _positions.Remove(nodeId);
        foreach (var other in _graph.Nodes)
            other.Inputs.RemoveAll(id => id == nodeId);
        if (_selectedNodeId == nodeId)
        {
            _selectedNodeId = null;
            NodeSelected?.Invoke(null);
        }
        PushHistory();
        InvalidateVisual();
        GraphModified?.Invoke();
    }

    public bool SetNodeInput(string nodeId, int inputIndex, string? sourceNodeId, bool notify = true)
    {
        if (inputIndex < 0) return false;
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return false;

        sourceNodeId = string.IsNullOrWhiteSpace(sourceNodeId) ? "" : sourceNodeId;
        if (!string.IsNullOrEmpty(sourceNodeId))
        {
            var source = _graph.Nodes.FirstOrDefault(n => n.Id == sourceNodeId);
            if (source == null || source.Kind == BrushTipNodeKind.Output || source.Id == nodeId)
                return false;
            if (!BrushTipNodePorts.CanConnect(source.Kind, node.Kind, inputIndex))
                return false;
            if (WouldCreateCycle(source.Id, nodeId))
                return false;
        }

        while (node.Inputs.Count <= inputIndex)
            node.Inputs.Add("");
        if (node.Inputs[inputIndex] == sourceNodeId)
            return true;

        node.Inputs[inputIndex] = sourceNodeId;
        TrimTrailingEmptyInputs(node);
        PushHistory();
        InvalidateVisual();
        if (notify)
            GraphModified?.Invoke();
        return true;
    }

    public bool UpdateNode(string nodeId, Action<BrushTipNode> update, bool pushHistory = true, bool notify = true)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return false;
        update(node);
        InvalidatePreviews();
        if (pushHistory)
            PushHistory();
        InvalidateVisual();
        if (notify)
            GraphModified?.Invoke();
        return true;
    }

    private bool WouldCreateCycle(string sourceNodeId, string targetNodeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return DependsOn(sourceNodeId, targetNodeId, seen);
    }

    private bool DependsOn(string nodeId, string targetNodeId, HashSet<string> seen)
    {
        if (nodeId == targetNodeId) return true;
        if (!seen.Add(nodeId)) return false;
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return false;
        foreach (var inputId in node.Inputs)
        {
            if (string.IsNullOrEmpty(inputId)) continue;
            if (DependsOn(inputId, targetNodeId, seen))
                return true;
        }
        return false;
    }

    private static void TrimTrailingEmptyInputs(BrushTipNode node)
    {
        while (node.Inputs.Count > 0 && string.IsNullOrEmpty(node.Inputs[^1]))
            node.Inputs.RemoveAt(node.Inputs.Count - 1);
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

    private void SetZoomAround(double newZoom, Point cursor)
    {
        var oldZoom = _zoom;
        var worldBefore = ScreenToWorld(cursor);
        _zoom = Math.Clamp(newZoom, 0.1, 10.0);
        if (oldZoom > 0)
        {
            _panX = cursor.X - worldBefore.X * _zoom;
            _panY = cursor.Y - worldBefore.Y * _zoom;
        }
        InvalidateVisual();
    }

    private Point ViewCenter() => new(Bounds.Width * 0.5, Bounds.Height * 0.5);

    public void FitGraphToView()
    {
        if (_graph == null || _positions == null || _positions.Count == 0 || Bounds.Width <= 1 || Bounds.Height <= 1)
            return;

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var node in _graph.Nodes)
        {
            if (!_positions.TryGetValue(node.Id, out var p)) continue;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X + NodeWidth);
            maxY = Math.Max(maxY, p.Y + GetNodeHeight(node));
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY))
            return;

        const double margin = 72;
        var graphW = Math.Max(1, maxX - minX);
        var graphH = Math.Max(1, maxY - minY);
        _zoom = Math.Clamp(Math.Min((Bounds.Width - margin) / graphW, (Bounds.Height - margin) / graphH), 0.1, 2.5);
        _panX = Bounds.Width * 0.5 - ((minX + maxX) * 0.5) * _zoom;
        _panY = Bounds.Height * 0.5 - ((minY + maxY) * 0.5) * _zoom;
        InvalidateVisual();
    }

    private void BeginPan(Point screenPos, IPointer pointer)
    {
        _dragAction = DragAction.Pan;
        _dragStartScreen = screenPos;
        _dragStartWorld = new Point(_panX, _panY);
        pointer.Capture(this);
    }

    private void BeginZoom(Point screenPos, IPointer pointer, int direction)
    {
        _dragAction = DragAction.Zoom;
        _dragStartScreen = screenPos;
        _lastZoomScreen = screenPos;
        _zoomAnchorScreen = screenPos;
        _dragStartZoom = _zoom;
        _zoomDirection = direction >= 0 ? 1 : -1;
        pointer.Capture(this);
    }

    private static CanvasAction ResolveButtonAction(CanvasButtonAction action) => action switch
    {
        CanvasButtonAction.PanCanvas => CanvasAction.PanCanvas,
        CanvasButtonAction.ZoomCanvas => CanvasAction.ZoomCanvas,
        _ => CanvasAction.None
    };

    private static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void UpdateViewportShortcut(Key key, KeyModifiers modifiers)
    {
        if (!_spaceHeld)
        {
            _shortcutAction = CanvasAction.None;
            return;
        }

        var triggerKey = key == Key.Space || IsModifierKey(key) ? Key.Space : key;
        var assignment = App.ModifierKeys.Resolve(
            (int)InputProcessType.Pen,
            (int)OutputProcessType.DirectDraw,
            triggerKey,
            modifiers);
        var presetId = assignment?.TemporaryToolPresetId;
        _shortcutAction = presetId switch
        {
            ToolGroupConfig.ViewHandPresetId => CanvasAction.PanCanvas,
            ToolGroupConfig.ViewZoomInPresetId => CanvasAction.ZoomCanvas,
            ToolGroupConfig.ViewZoomOutPresetId => CanvasAction.ZoomCanvas,
            _ => CanvasAction.None
        };
        _zoomDirection = presetId == ToolGroupConfig.ViewZoomOutPresetId ? -1 : 1;
    }

    private static int NodeInputCount(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Output => 1,
        BrushTipNodeKind.RotateCoordinates or BrushTipNodeKind.PolarRadius
            or BrushTipNodeKind.PolarAngle => 1,
        BrushTipNodeKind.WarpCoordinates => 2,
        BrushTipNodeKind.DistanceField or BrushTipNodeKind.BoxDistanceField
            or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
            or BrushTipNodeKind.Noise => 1,
        BrushTipNodeKind.Threshold or BrushTipNodeKind.SmoothStep
            or BrushTipNodeKind.Invert or BrushTipNodeKind.Power
            or BrushTipNodeKind.Sine or BrushTipNodeKind.Absolute => 1,
        BrushTipNodeKind.Add or BrushTipNodeKind.Multiply or BrushTipNodeKind.Max
            or BrushTipNodeKind.Min or BrushTipNodeKind.Subtract or BrushTipNodeKind.Mix => 2,
        _ => 0
    };

    private static bool HasImageSelector(BrushTipNode node)
        => node.Kind == BrushTipNodeKind.ImageSampler;

    private static float PreviewBarHeightFor(BrushTipNode node)
        => HasImageSelector(node) ? ImageTipPreviewSize : PreviewBarHeight;

    private static Rect AspectFitRect(Size sourceSize, Rect bounds)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return bounds;

        var scale = Math.Min(bounds.Width / sourceSize.Width, bounds.Height / sourceSize.Height);
        var w = sourceSize.Width * scale;
        var h = sourceSize.Height * scale;
        return new Rect(
            bounds.X + (bounds.Width - w) * 0.5,
            bounds.Y + (bounds.Height - h) * 0.5,
            w,
            h);
    }

    private static bool HasStandalonePreview(BrushTipNode node)
        => node.Kind == BrushTipNodeKind.Coordinates;

    private static bool ShowsPreviewBar(BrushTipNode node)
    {
        var paramCount = GetNodeParams(node.Kind).Length;
        var inputCount = NodeInputCount(node.Kind);
        return paramCount > 0 || inputCount > 0 || HasImageSelector(node) || HasStandalonePreview(node);
    }

    private float GetNodeHeight(BrushTipNode node)
    {
        var inputCount = NodeInputCount(node.Kind);
        var paramCount = GetNodeParams(node.Kind).Length;
        var hasImageSelector = HasImageSelector(node);
        var bodyHeight = BodyPadding;
        if (inputCount > 0)
            bodyHeight += inputCount * PortRowHeight;
        if (hasImageSelector)
            bodyHeight += ImageSelectorRowHeight + BodyPadding;
        var previewHeight = PreviewBarHeightFor(node);
        if (paramCount > 0)
        {
            bodyHeight += BodyPadding;
            bodyHeight += paramCount * SliderRowHeight;
            bodyHeight += previewHeight + 4;
        }
        else if (ShowsPreviewBar(node))
        {
            bodyHeight += previewHeight + 4;
        }
        return HeaderHeight + bodyHeight + BottomPadding;
    }

    private static float WidgetsStartY(Point pos, BrushTipNode node)
    {
        var y = (float)pos.Y + HeaderHeight + BodyPadding;
        var inputCount = NodeInputCount(node.Kind);
        if (inputCount > 0)
            y += inputCount * PortRowHeight + BodyPadding;
        return y;
    }

    private static Rect ImageSelectorRect(Point pos, BrushTipNode node)
    {
        var y = WidgetsStartY(pos, node);
        return new Rect(pos.X + 4, y, NodeWidth - 8, ImageSelectorRowHeight - 4);
    }

    private static SolidColorBrush HeaderBrush(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Coordinates or BrushTipNodeKind.RotateCoordinates
            or BrushTipNodeKind.WarpCoordinates
            or BrushTipNodeKind.Value or BrushTipNodeKind.DistanceField
            or BrushTipNodeKind.BoxDistanceField
            or BrushTipNodeKind.Circle or BrushTipNodeKind.Rectangle
            or BrushTipNodeKind.RoundedRectangle
            or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
            or BrushTipNodeKind.ImageSampler
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
        BrushTipNodeKind.RotateCoordinates => new NodeParam[] {
            new("Center X", 0f, 1f, n => n.X, (n, v) => n.X = v),
            new("Center Y", 0f, 1f, n => n.Y, (n, v) => n.Y = v),
            new("Scale", 0.05f, 8f, n => n.Scale, (n, v) => n.Scale = v),
            new("Rotation", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        BrushTipNodeKind.WarpCoordinates => new NodeParam[] {
            new("Amount", 0f, 1f, n => n.Density, (n, v) => n.Density = v),
            new("X Amp", 0f, 1f, n => n.Width, (n, v) => n.Width = v),
            new("Y Amp", 0f, 1f, n => n.Height, (n, v) => n.Height = v),
            new("Direction", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        BrushTipNodeKind.PolarRadius => new NodeParam[] {
            new("Center X", 0f, 1f, n => n.X, (n, v) => n.X = v),
            new("Center Y", 0f, 1f, n => n.Y, (n, v) => n.Y = v),
            new("Width", 0f, 1f, n => n.Width, (n, v) => n.Width = v),
            new("Height", 0f, 1f, n => n.Height, (n, v) => n.Height = v),
            new("Scale", 0.05f, 16f, n => n.Scale, (n, v) => n.Scale = v),
        },
        BrushTipNodeKind.PolarAngle => new NodeParam[] {
            new("Center X", 0f, 1f, n => n.X, (n, v) => n.X = v),
            new("Center Y", 0f, 1f, n => n.Y, (n, v) => n.Y = v),
            new("Repeats", 0.05f, 64f, n => n.Scale, (n, v) => n.Scale = v),
            new("Phase", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
        BrushTipNodeKind.Value => new NodeParam[] {
            new("Value", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.DistanceField or BrushTipNodeKind.BoxDistanceField => new NodeParam[] {
            new("Center X", 0f, 1f, n => n.X, (n, v) => n.X = v),
            new("Center Y", 0f, 1f, n => n.Y, (n, v) => n.Y = v),
            new("Width", 0f, 1f, n => n.Width, (n, v) => n.Width = v),
            new("Height", 0f, 1f, n => n.Height, (n, v) => n.Height = v),
            new("Rotation", -180f, 180f, n => n.RotationDegrees, (n, v) => n.RotationDegrees = v),
        },
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
        BrushTipNodeKind.ImageSampler => new NodeParam[] {
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Threshold => new NodeParam[] {
            new("Threshold", 0f, 1f, n => n.Threshold, (n, v) => n.Threshold = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.SmoothStep => new NodeParam[] {
            new("Edge", 0f, 1f, n => n.Threshold, (n, v) => n.Threshold = v),
            new("Softness", 0.001f, 1f, n => n.Hardness, (n, v) => n.Hardness = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Power => new NodeParam[] {
            new("Exponent", 0.05f, 16f, n => n.Scale, (n, v) => n.Scale = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Sine => new NodeParam[] {
            new("Frequency", 0.05f, 64f, n => n.Scale, (n, v) => n.Scale = v),
            new("Phase", 0f, 1f, n => n.X, (n, v) => n.X = v),
            new("Opacity", 0f, 1f, n => n.Opacity, (n, v) => n.Opacity = v),
        },
        BrushTipNodeKind.Absolute => new NodeParam[] {
            new("Center", 0f, 1f, n => n.X, (n, v) => n.X = v),
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
        BrushTipNodeKind.Coordinates => "UV Map",
        BrushTipNodeKind.LinearGradient => "Linear Grad",
        BrushTipNodeKind.ImageSampler => "Image Sampler",
        BrushTipNodeKind.RoundedRectangle => "Round Rect",
        BrushTipNodeKind.RotateCoordinates => "Rotate Coord",
        BrushTipNodeKind.WarpCoordinates => "Warp Coord",
        BrushTipNodeKind.PolarRadius => "Polar Radius",
        BrushTipNodeKind.PolarAngle => "Polar Angle",
        BrushTipNodeKind.DistanceField => "Ellipse Field",
        BrushTipNodeKind.BoxDistanceField => "Box Field",
        BrushTipNodeKind.SmoothStep => "Smooth Step",
        _ => kind.ToString()
    };

    private Point GetOutputPortPos(BrushTipNode node, Point nodePos) =>
        new(nodePos.X + NodeWidth, nodePos.Y + HeaderHeight / 2);

    private Point GetInputPortPos(BrushTipNode node, Point nodePos, int index) =>
        new(nodePos.X, nodePos.Y + HeaderHeight + BodyPadding + index * PortRowHeight + PortRowHeight / 2);

    // ── Slider bar bounds (world-space, relative to node position) ─────────────
    private static Rect SliderBarRect(Point pos, BrushTipNode node, int paramIndex)
    {
        var y = WidgetsStartY(pos, node);
        if (HasImageSelector(node))
            y += ImageSelectorRowHeight + BodyPadding;
        y += paramIndex * SliderRowHeight + 4;
        var x = pos.X + SliderLabelWidth + SliderBarGap;
        var width = NodeWidth - SliderLabelWidth - SliderValueWidth - SliderBarGap * 2;
        return new Rect(x, y, width, 14);
    }

    private static Rect SliderValueRect(Point pos, BrushTipNode node, int paramIndex)
    {
        var bar = SliderBarRect(pos, node, paramIndex);
        return new Rect(pos.X + NodeWidth - SliderValueWidth - 2, bar.Y - 3, SliderValueWidth, SliderRowHeight);
    }

    private Point WorldToLocal(Point world)
        => new(world.X * _zoom + _panX, world.Y * _zoom + _panY);

    private Rect WorldToLocalRect(Rect worldRect)
    {
        var topLeft = WorldToLocal(new Point(worldRect.X, worldRect.Y));
        return new Rect(topLeft, new Size(worldRect.Width * _zoom, worldRect.Height * _zoom));
    }

    private void EnsureParamEditPopup()
    {
        if (_paramEditPopup != null) return;

        _paramEditBox = new TextBox
        {
            FontSize = 10,
            MinWidth = 52,
            Padding = new Thickness(4, 2),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Background = NodeBgBrush,
            Foreground = BodyTextBrush,
            BorderBrush = SelectionBorderPen.Brush,
            BorderThickness = new Thickness(1),
        };

        _paramEditPopup = new Popup
        {
            Child = _paramEditBox,
            IsLightDismissEnabled = false,
            Placement = PlacementMode.Bottom,
        };

        _paramEditBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                CommitParamEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseParamEdit(revert: true);
                e.Handled = true;
            }
        };

        _paramEditBox.LostFocus += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_paramEditPopup?.IsOpen == true)
                    CommitParamEdit();
            }, Avalonia.Threading.DispatcherPriority.Input);
        };
    }

    private void OpenParamEditor(string nodeId, int paramIdx, Rect valueWorldRect)
    {
        if (_graph == null) return;
        EnsureParamEditPopup();
        if (_paramEditBox == null || _paramEditPopup == null)
            return;

        var editBox = _paramEditBox;
        var editPopup = _paramEditPopup;

        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null) return;
        var nodeParams = GetNodeParams(node.Kind);
        if (paramIdx >= nodeParams.Length) return;
        var param = nodeParams[paramIdx];

        _paramEditNodeId = nodeId;
        _paramEditParamIdx = paramIdx;
        _paramEditOriginalValue = param.Get(node);
        editBox.Text = BrushTipNodePorts.FormatDisplayValue(_paramEditOriginalValue);

        var localRect = WorldToLocalRect(valueWorldRect);
        editBox.Width = Math.Max(52, localRect.Width);
        editPopup.PlacementTarget = this;
        editPopup.HorizontalOffset = localRect.X;
        editPopup.VerticalOffset = localRect.Y;
        editPopup.IsOpen = true;
        editBox.Focus();
        editBox.SelectAll();
    }

    private static string FormatParamValue(float value)
        => BrushTipNodePorts.FormatDisplayValue(value);

    private void CommitParamEdit()
    {
        if (_graph == null || _paramEditNodeId == null)
            return;

        var editBox = _paramEditBox;
        var editPopup = _paramEditPopup;
        if (editBox == null || editPopup == null || !editPopup.IsOpen)
            return;

        var node = _graph.Nodes.FirstOrDefault(n => n.Id == _paramEditNodeId);
        if (node == null)
        {
            CloseParamEdit(revert: true);
            return;
        }

        var nodeParams = GetNodeParams(node.Kind);
        if (_paramEditParamIdx >= nodeParams.Length)
        {
            CloseParamEdit(revert: true);
            return;
        }

        var param = nodeParams[_paramEditParamIdx];
        if (!float.TryParse((editBox.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            parsed = _paramEditOriginalValue;
        parsed = Math.Clamp(parsed, param.Min, param.Max);

        var changed = Math.Abs(parsed - param.Get(node)) > 0.0001f;
        if (changed)
        {
            param.Set(node, parsed);
            PushHistory();
            InvalidatePreviews();
            InvalidateVisual();
            GraphModified?.Invoke();
        }

        CloseParamEdit(revert: false);
    }

    private void CloseParamEdit(bool revert)
    {
        if (_paramEditPopup != null)
            _paramEditPopup.IsOpen = false;
        if (revert && _paramEditBox != null && _paramEditNodeId != null)
            _paramEditBox.Text = BrushTipNodePorts.FormatDisplayValue(_paramEditOriginalValue);
        _paramEditNodeId = null;
        _paramEditParamIdx = -1;
    }

    // ── Preview bar bounds ─────────────────────────────────────────────────────
    private static Rect PreviewBarRect(Point pos, BrushTipNode node)
    {
        var paramCount = GetNodeParams(node.Kind).Length;
        var y = WidgetsStartY(pos, node);
        if (HasImageSelector(node))
            y += ImageSelectorRowHeight + BodyPadding;
        if (paramCount > 0)
            y += paramCount * SliderRowHeight + 4;
        else if (HasStandalonePreview(node))
            y = (float)pos.Y + HeaderHeight + BodyPadding;
        return new Rect(pos.X + 4, y, NodeWidth - 8, PreviewBarHeightFor(node));
    }

    private static float ScrubValueFromWorldX(Rect barRect, NodeParam param, double worldX)
    {
        var fraction = Math.Clamp((worldX - barRect.Left) / Math.Max(1.0, barRect.Width), 0.0, 1.0);
        return (float)(param.Min + fraction * (param.Max - param.Min));
    }

    public override void Render(DrawingContext context)
    {
        if (_graph == null || _positions == null) return;

        context.DrawRectangle(BgBrush, null, new Rect(Bounds.Size));
        DrawGrid(context);

        var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panX, _panY);
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
                if (_dragAction == DragAction.DisconnectWire
                    && node.Id == _connectTgtId
                    && i == _connectTgtIdx)
                    continue;

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
        else if (_dragAction == DragAction.DisconnectWire && _connectSrcId != null)
        {
            if (_graph.Nodes.FirstOrDefault(n => n.Id == _connectSrcId) is { } srcNode
                && _positions.TryGetValue(_connectSrcId, out var srcPos))
                DrawBezier(context, GetOutputPortPos(srcNode, srcPos), _connectTempEnd, WirePen);
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

            var inputLabel = BrushTipNodePorts.InputLabel(node.Kind, i);
            if (node.Kind is BrushTipNodeKind.Threshold or BrushTipNodeKind.Invert)
            {
                inputLabel = node.Kind switch
                {
                    BrushTipNodeKind.Threshold => "mask",
                    BrushTipNodeKind.Invert => "input",
                    _ => inputLabel
                };
            }
            else if (node.Kind == BrushTipNodeKind.Output)
                inputLabel = "output";
            var lft = new FormattedText(inputLabel, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 10, MutedTextBrush);
            context.DrawText(lft,
                new Point(pos.X + PortRadius + 6, ip.Y - lft.Height / 2));

            y += PortRowHeight;
        }

        // Image selector widget (Blender-style on-node dropdown)
        if (HasImageSelector(node))
        {
            if (inputCount > 0) y += BodyPadding;
            var selectorRect = ImageSelectorRect(pos, node);
            context.DrawRectangle(SliderBgBrush, null, selectorRect, 3, 3);
            var label = BrushMaterialTips.SamplerDisplayLabel(node, _imageSamplers);
            var labelFt = new FormattedText(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 9, BodyTextBrush);
            var maxLabelW = selectorRect.Width - 18;
            if (labelFt.Width > maxLabelW)
                label = label.Length > 12 ? label[..9] + "…" : label;
            labelFt = new FormattedText(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 9, BodyTextBrush);
            context.DrawText(labelFt,
                new Point(selectorRect.X + 6, selectorRect.Y + (selectorRect.Height - labelFt.Height) / 2));
            var arrow = new FormattedText("▾", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 10, MutedTextBrush);
            context.DrawText(arrow,
                new Point(selectorRect.Right - 14, selectorRect.Y + (selectorRect.Height - arrow.Height) / 2));
            y += ImageSelectorRowHeight + BodyPadding;
        }

        // Slider rows
        if (inputCount > 0 && !HasImageSelector(node)) y += BodyPadding;
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
            var barRect = SliderBarRect(pos, node, Array.IndexOf(nodeParams, param));
            context.DrawRectangle(SliderBgBrush, null, barRect, 3, 3);
            if (fraction > 0.001f)
            {
                var fillRect = new Rect(barRect.X, barRect.Y, barRect.Width * fraction, barRect.Height);
                context.DrawRectangle(SliderFillBrush, null, fillRect, 3, 3);
            }

            // Value (click to edit)
            var valueRect = SliderValueRect(pos, node, Array.IndexOf(nodeParams, param));
            var valText = new FormattedText(FormatParamValue(value), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 9, BodyTextBrush);
            context.DrawText(valText, new Point(
                valueRect.X + (valueRect.Width - valText.Width) / 2,
                valueRect.Y + (valueRect.Height - valText.Height) / 2));

            y += SliderRowHeight;
        }

        // Preview bar at bottom
        if (ShowsPreviewBar(node))
        {
            var prev = PreviewBarRect(pos, node);
            context.DrawRectangle(PreviewBgBrush, null, prev, 3, 3);

            if (HasStandalonePreview(node))
            {
                var hint = new FormattedText("Per-pixel UV 0–1", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, DefaultTypeface, 8, MutedTextBrush);
                context.DrawText(hint, new Point(prev.X + 4, prev.Y + 2));
            }

            try
            {
                var previewImage = GetNodePreview(node);
                const double padding = 3.0;
                var bounds = new Rect(prev.X + padding, prev.Y + padding,
                    prev.Width - padding * 2, prev.Height - padding * 2);
                var srcSize = previewImage.Size;
                var drawRect = AspectFitRect(srcSize, bounds);
                context.DrawImage(previewImage, new Rect(srcSize), drawRect);
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
        Focus(NavigationMethod.Pointer);

        var point = e.GetCurrentPoint(this);
        var screenPos = point.Position;
        var worldPos = ScreenToWorld(screenPos);

        if (point.Properties.IsMiddleButtonPressed)
        {
            var middleAction = ResolveButtonAction(App.Shortcuts.MiddleButtonAction);
            if (middleAction == CanvasAction.ZoomCanvas)
                BeginZoom(screenPos, e.Pointer, 1);
            else
                BeginPan(screenPos, e.Pointer);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            if (_shortcutAction == CanvasAction.PanCanvas)
            {
                BeginPan(screenPos, e.Pointer);
                e.Handled = true;
                return;
            }
            if (_shortcutAction == CanvasAction.ZoomCanvas)
            {
                BeginZoom(screenPos, e.Pointer, _zoomDirection);
                e.Handled = true;
                return;
            }

            // Hit-test output ports (reverse order).
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (node.Kind == BrushTipNodeKind.Output) continue;
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var op = GetOutputPortPos(node, np);
                if (HitPort(worldPos, op))
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
                    if (HitPort(worldPos, ip))
                    {
                        var existing = i < node.Inputs.Count ? node.Inputs[i] : "";
                        if (!string.IsNullOrEmpty(existing))
                        {
                            _dragAction = DragAction.DisconnectWire;
                            _connectSrcId = existing;
                            _connectTgtId = node.Id;
                            _connectTgtIdx = i;
                        }
                        else
                        {
                            _dragAction = DragAction.ConnectFromInput;
                            _connectTgtId = node.Id;
                            _connectTgtIdx = i;
                        }
                        _connectTempEnd = worldPos;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Hit-test image selector on ImageSampler nodes.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!HasImageSelector(node) || !_positions.TryGetValue(node.Id, out var np))
                    continue;
                if (ImageSelectorRect(np, node).Contains(worldPos))
                {
                    ShowImageSelectorMenu(node, np);
                    e.Handled = true;
                    return;
                }
            }

            // Hit-test slider bars.
            for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
            {
                var node = _graph.Nodes[ni];
                if (!_positions.TryGetValue(node.Id, out var np)) continue;
                var nodeParams = GetNodeParams(node.Kind);
                for (var pi = 0; pi < nodeParams.Length; pi++)
                {
                    var valueRect = SliderValueRect(np, node, pi);
                    if (valueRect.Contains(worldPos))
                    {
                        OpenParamEditor(node.Id, pi, valueRect);
                        e.Handled = true;
                        return;
                    }

                    var bar = SliderBarRect(np, node, pi);
                    if (bar.Contains(worldPos))
                    {
                        _dragAction = DragAction.ScrubParam;
                        _scrubNodeId = node.Id;
                        _scrubParamIdx = pi;
                        var param = nodeParams[pi];
                        param.Set(node, ScrubValueFromWorldX(bar, param, worldPos.X));
                        InvalidateVisual();
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

            // Empty space: deselect. Pan with middle button or Space+drag so node selection stays stable.
            if (_selectedNodeId != null)
            {
                _selectedNodeId = null;
                NodeSelected?.Invoke(null);
                InvalidateVisual();
            }
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
                    if (HitPort(worldPos, ip))
                    {
                        if (i < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[i]))
                        {
                            SetNodeInput(node.Id, i, "");
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
            CanvasRightClicked?.Invoke(worldPos);
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

            case DragAction.Zoom:
                var zoomDx = screenPos.X - _lastZoomScreen.X;
                var zoomDy = screenPos.Y - _lastZoomScreen.Y;
                var zoomDelta = Math.Sqrt(zoomDx * zoomDx + zoomDy * zoomDy) * Math.Sign(zoomDy);
                if (Math.Abs(zoomDelta) > 0.001)
                {
                    SetZoomAround(_zoom * Math.Pow(1.012, -zoomDelta * _zoomDirection), _zoomAnchorScreen);
                    _lastZoomScreen = screenPos;
                }
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
            case DragAction.DisconnectWire:
                _connectTempEnd = worldPos;
                InvalidateVisual();
                break;

            case DragAction.ScrubParam:
                if (_scrubNodeId != null)
                {
                    var node = _graph.Nodes.FirstOrDefault(n => n.Id == _scrubNodeId);
                    if (node == null || !_positions.TryGetValue(node.Id, out var np)) break;
                    var nodeParams = GetNodeParams(node.Kind);
                    if (_scrubParamIdx >= nodeParams.Length) break;
                    var param = nodeParams[_scrubParamIdx];
                    var bar = SliderBarRect(np, node, _scrubParamIdx);
                    var newValue = ScrubValueFromWorldX(bar, param, worldPos.X);
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
            if (_graph.Nodes.FirstOrDefault(n => n.Id == _connectSrcId) is { } srcNode
                && TryPickInputPort(worldPos, out var tgtNode, out var inputIdx)
                && BrushTipNodePorts.CanConnect(srcNode.Kind, tgtNode.Kind, inputIdx))
            {
                SetNodeInput(tgtNode.Id, inputIdx, _connectSrcId);
            }
        }
        else if (_dragAction == DragAction.DisconnectWire && _connectTgtId != null)
        {
            var tgtNode = _graph.Nodes.FirstOrDefault(n => n.Id == _connectTgtId);
            var prevSrcNode = _graph.Nodes.FirstOrDefault(n => n.Id == _connectSrcId);
            if (tgtNode != null && _positions.TryGetValue(tgtNode.Id, out var tgtPos))
            {
                var reconnected = false;
                var droppedOnPort = false;

                // Dropped back on the same input — keep the existing wire.
                if (HitPort(worldPos, GetInputPortPos(tgtNode, tgtPos, _connectTgtIdx)))
                {
                    reconnected = true;
                }
                // Dropped on another output — patch a new source into the original input.
                else if (TryPickOutputPort(worldPos, out var newSrc, excludeNodeId: _connectTgtId))
                {
                    droppedOnPort = true;
                    if (BrushTipNodePorts.CanConnect(newSrc.Kind, tgtNode.Kind, _connectTgtIdx))
                        reconnected = SetNodeInput(tgtNode.Id, _connectTgtIdx, newSrc.Id);
                }
                // Dropped on another compatible input — move the wire to that slot.
                else if (prevSrcNode != null
                         && TryPickInputPort(worldPos, out var newTgt, out var newIdx,
                             excludeNodeId: _connectTgtId, excludeInputIndex: _connectTgtIdx)
                         && BrushTipNodePorts.CanConnect(prevSrcNode.Kind, newTgt.Kind, newIdx))
                {
                    droppedOnPort = true;
                    SetNodeInput(tgtNode.Id, _connectTgtIdx, "", notify: false);
                    reconnected = SetNodeInput(newTgt.Id, newIdx, _connectSrcId);
                }

                if (!reconnected && !droppedOnPort
                    && _connectTgtIdx < tgtNode.Inputs.Count
                    && !string.IsNullOrEmpty(tgtNode.Inputs[_connectTgtIdx]))
                {
                    SetNodeInput(tgtNode.Id, _connectTgtIdx, "");
                }
            }
        }
        else if (_dragAction == DragAction.ConnectFromInput && _connectTgtId != null)
        {
            var tgtNode = _graph.Nodes.FirstOrDefault(n => n.Id == _connectTgtId);
            if (tgtNode != null
                && TryPickOutputPort(worldPos, out var srcNode, excludeNodeId: _connectTgtId)
                && BrushTipNodePorts.CanConnect(srcNode.Kind, tgtNode.Kind, _connectTgtIdx))
            {
                SetNodeInput(tgtNode.Id, _connectTgtIdx, srcNode.Id);
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
                    InvalidatePreviews();
                    InvalidateVisual();
                    GraphModified?.Invoke();
                }
            }
        }
        else if (_dragAction == DragAction.MoveNode && _dragNodeId != null)
        {
            PushHistory();
        }

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
        var zoomDelta = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        SetZoomAround(_zoom * zoomDelta, pos);
        e.Handled = true;
    }

    public bool TryHandleKeyDown(KeyEventArgs e)
    {
        var sc = App.Shortcuts;
        if (sc.Undo.Matches(e))
        {
            Undo();
            return true;
        }
        if (sc.Redo.Matches(e) || sc.RedoAlt.Matches(e))
        {
            Redo();
            return true;
        }

        if (sc.ZoomIn.Matches(e) || sc.ZoomInAlt.Matches(e))
        {
            SetZoomAround(_zoom * sc.ZoomKeyFactor, ViewCenter());
            return true;
        }
        if (sc.ZoomOut.Matches(e))
        {
            SetZoomAround(_zoom / sc.ZoomKeyFactor, ViewCenter());
            return true;
        }
        if (sc.ZoomReset.Matches(e))
        {
            SetZoomAround(1.2, ViewCenter());
            return true;
        }
        if (sc.ZoomFit.Matches(e))
        {
            FitGraphToView();
            return true;
        }

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (_selectedNodeId != null && _selectedNodeId != _graph?.OutputNodeId)
            {
                DeleteNode(_selectedNodeId);
                return true;
            }
        }
        if (e.Key == Key.Space)
        {
            _spaceHeld = true;
            UpdateViewportShortcut(e.Key, Floss.App.Input.KeyBinding.ModifiersWithKeyDown(e.Key, e.KeyModifiers));
            return true;
        }
        if (_spaceHeld && IsModifierKey(e.Key))
        {
            UpdateViewportShortcut(e.Key, Floss.App.Input.KeyBinding.ModifiersWithKeyDown(e.Key, e.KeyModifiers));
            return true;
        }

        return false;
    }

    public bool TryHandleKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
            _shortcutAction = CanvasAction.None;
            return true;
        }
        if (_spaceHeld && IsModifierKey(e.Key))
        {
            UpdateViewportShortcut(Key.Space, Floss.App.Input.KeyBinding.ModifiersAfterKeyUp(e.Key, e.KeyModifiers));
            return true;
        }

        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandlesKeyboardDirectly() && TryHandleKeyDown(e))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (HandlesKeyboardDirectly() && TryHandleKeyUp(e))
            e.Handled = true;
        else
            base.OnKeyUp(e);
    }

    private bool HandlesKeyboardDirectly()
    {
        for (var current = this as Visual; current != null; current = current.GetVisualParent())
        {
            if (current is NodeGraphEditorPanel panel)
                return !panel.IsDocked;
            if (current is NodeGraphEditorWindow)
                return true;
        }

        return true;
    }

    private bool HitPort(Point worldPos, Point portWorldPos)
        => Dist(worldPos, portWorldPos) * _zoom < PortHitRadius;

    private bool TryPickOutputPort(Point worldPos, out BrushTipNode pickedNode, string? excludeNodeId = null)
    {
        pickedNode = null!;
        for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
        {
            var node = _graph.Nodes[ni];
            if (node.Kind == BrushTipNodeKind.Output) continue;
            if (excludeNodeId != null && node.Id == excludeNodeId) continue;
            if (!_positions.TryGetValue(node.Id, out var np)) continue;
            if (!HitPort(worldPos, GetOutputPortPos(node, np))) continue;
            pickedNode = node;
            return true;
        }

        return false;
    }

    private bool TryPickInputPort(
        Point worldPos,
        out BrushTipNode pickedNode,
        out int inputIndex,
        string? excludeNodeId = null,
        int? excludeInputIndex = null)
    {
        pickedNode = null!;
        inputIndex = -1;
        for (var ni = _graph.Nodes.Count - 1; ni >= 0; ni--)
        {
            var node = _graph.Nodes[ni];
            if (!_positions.TryGetValue(node.Id, out var np)) continue;
            var inputCount = NodeInputCount(node.Kind);
            for (var i = 0; i < inputCount; i++)
            {
                if (excludeNodeId != null && excludeInputIndex != null
                    && node.Id == excludeNodeId && i == excludeInputIndex.Value)
                    continue;
                if (!HitPort(worldPos, GetInputPortPos(node, np, i))) continue;
                pickedNode = node;
                inputIndex = i;
                return true;
            }
        }

        return false;
    }

    private static double Dist(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void ShowImageSelectorMenu(BrushTipNode node, Point nodePos)
    {
        if (_imageSamplers.Count == 0)
            return;

        var menu = new ContextMenu();
        foreach (var option in _imageSamplers)
        {
            var item = new MenuItem { Header = option.Label, FontSize = 11 };
            var captured = option;
            item.Click += (_, _) =>
            {
                SetImageSamplerMaterialTip(node.Id, captured.Tip.Id);
            };
            menu.Items.Add(item);
        }

        menu.Open(this);
    }

    public void SetImageSamplerMaterialTip(string nodeId, string materialTipId, bool pushHistory = true, bool notify = true)
    {
        var node = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return;
        if (string.Equals(node.MaterialTipId, materialTipId, StringComparison.Ordinal) && node.PngBytes.Length == 0)
            return;

        if (pushHistory)
            PushHistory();
        UpdateNode(nodeId, n =>
        {
            n.MaterialTipId = materialTipId;
            n.PngBytes = [];
        }, pushHistory: false, notify: notify);
        ImageSamplerChanged?.Invoke(nodeId, materialTipId);
    }
}
