using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Floss.App.Brushes;

using static Floss.App.AppColors;

public sealed class NodeGraphEditorPanel : UserControl
{
    private readonly NodeGraphView _view = new();
    private readonly StackPanel _propertyContent = new() { Spacing = 6, Margin = new Thickness(8) };
    private readonly Border _propertyBorder;
    private readonly TextBlock _brushTitleText;
    private readonly Action<BrushTipNodeGraph> _onCommit;
    private readonly Action<BrushTipNodeGraph, string>? _onSaveAsNew;
    private readonly bool _docked;
    private List<ImageSamplerOption> _imageSamplers;

    private BrushTipNodeGraph _graph;
    private BrushTipNode? _selectedNode;
    private bool _isCommitting;
    private int _saveCounter;
    private ContextMenu? _openMenu;
    private Point _pendingAddPosition;

    public NodeGraphEditorPanel(BrushTipNodeGraph graph, Action<BrushTipNodeGraph> onCommit,
        Action<BrushTipNodeGraph, string>? onSaveAsNew = null,
        IReadOnlyList<BrushTipData>? imageSamplers = null,
        bool docked = false,
        Action? onClose = null)
    {
        _graph = graph.DeepClone();
        _onCommit = onCommit;
        _onSaveAsNew = onSaveAsNew;
        _docked = docked;
        _imageSamplers = ImageSamplerOptions.FromTips(imageSamplers);
        _view.SetImageSamplerOptions(imageSamplers);
        _view.ImageSamplerChanged += (_, bytes) => DoCommit();

        var layoutBtn = MakeToolbarButton("Auto Layout");
        layoutBtn.Click += (_, _) => { _view.AutoLayout(); ScheduleFitToView(); };

        var applyBtn = MakeToolbarButton("Apply");
        applyBtn.Click += (_, _) => DoCommit();

        var saveAsBtn = MakeToolbarButton("Save as New");
        saveAsBtn.IsVisible = _onSaveAsNew != null;
        saveAsBtn.Click += (_, _) =>
        {
            if (_onSaveAsNew == null) return;
            _saveCounter++;
            var clone = _graph.DeepClone();
            clone.BuiltInShape = null;
            _onSaveAsNew(clone, $"Custom Node Graph {_saveCounter}");
        };

        var newFromPresetBtn = MakeToolbarButton("New from Preset");
        newFromPresetBtn.Click += (_, _) => ShowPresetMenu(newFromPresetBtn);

        _brushTitleText = new TextBlock
        {
            Text = "Brush Graph",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var toolbar = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            MinHeight = 32
        };

        if (_docked)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    newFromPresetBtn,
                    ToolbarSep(),
                    layoutBtn,
                    ToolbarSep(),
                    saveAsBtn
                }
            };

            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            right.Children.Add(applyBtn);
            if (onClose != null)
            {
                var closeBtn = MakeToolbarButton("✕");
                closeBtn.MinWidth = 26;
                closeBtn.Click += (_, _) => onClose();
                right.Children.Add(closeBtn);
            }

            var bar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                VerticalAlignment = VerticalAlignment.Center
            };
            bar.Children.Add(_brushTitleText);
            Grid.SetColumn(actions, 1);
            actions.HorizontalAlignment = HorizontalAlignment.Center;
            bar.Children.Add(actions);
            Grid.SetColumn(right, 2);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            bar.Children.Add(right);
            toolbar.Child = bar;
        }
        else
        {
            toolbar.Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(2, 0),
                Children =
                {
                    newFromPresetBtn,
                    ToolbarSep(),
                    layoutBtn,
                    ToolbarSep(),
                    saveAsBtn,
                    ToolbarSep(),
                    applyBtn
                }
            };
        }

        Border? statusBorder = null;
        if (!_docked)
        {
            statusBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse(Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 3),
                Child = new TextBlock
                {
                    Text = "Middle/Space-drag pan · Scroll zoom · Drag header move · Drag port connect · Right-click canvas add node · Ctrl+Z undo · Del delete",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse(TextMuted))
                }
            };
        }

        _view.NodeSelected += OnNodeSelected;
        _view.NodeRightClicked += OnNodeRightClicked;
        _view.CanvasRightClicked += OnCanvasRightClicked;
        _view.GraphModified += OnGraphModified;

        _propertyBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Child = new ScrollViewer { Content = _propertyContent },
            Width = _docked ? 200 : 220,
            MinWidth = _docked ? 160 : 200,
            MaxWidth = _docked ? 240 : 280
        };
        ShowNoSelection();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        if (!_docked)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        grid.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        Grid.SetColumnSpan(toolbar, 2);

        grid.Children.Add(_view);
        Grid.SetRow(_view, 1);
        Grid.SetColumn(_view, 0);

        grid.Children.Add(_propertyBorder);
        Grid.SetRow(_propertyBorder, 1);
        Grid.SetColumn(_propertyBorder, 1);

        if (statusBorder != null)
        {
            grid.Children.Add(statusBorder);
            Grid.SetRow(statusBorder, 2);
            Grid.SetColumnSpan(statusBorder, 2);
        }

        Content = grid;
        Background = new SolidColorBrush(Color.Parse(Bg1));

        _view.LoadGraph(_graph, new());
        _view.AutoLayout();

        Loaded += (_, _) =>
        {
            if (_docked)
                ScheduleFitToView();
            else
                _view.SetView(50, 50, 1.2);
        };
        _view.KeyDown += OnPanelKeyDown;
        Focusable = true;
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            !e.GetCurrentPoint(this).Properties.IsRightButtonPressed &&
            !e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            return;

        Focus();
        _view.Focus();
    }

    private static Button MakeToolbarButton(string label) => new()
    {
        Content = label,
        FontSize = 11,
        MinHeight = 24,
        Padding = new Thickness(8, 2),
        CornerRadius = new CornerRadius(3)
    };

    private static Border ToolbarSep() => new()
    {
        Width = 1,
        Height = 18,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(2, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    public void SetBrushTitle(string title)
    {
        _brushTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Brush Graph" : $"{title.Trim()} — Graph";
    }

    private void ScheduleFitToView()
        => Dispatcher.UIThread.Post(() => _view.FitGraphToView(), DispatcherPriority.Loaded);

    public void SetImageSamplerOptions(IReadOnlyList<BrushTipData>? tips)
    {
        _imageSamplers = ImageSamplerOptions.FromTips(tips);
        _view.SetImageSamplerOptions(tips);
        RefreshPropertyPanel();
    }

    public void LoadGraph(BrushTipNodeGraph graph, string? brushTitle = null)
    {
        _graph = graph.DeepClone();
        _view.LoadGraph(_graph, new());
        _view.AutoLayout();
        _selectedNode = null;
        RefreshPropertyPanel();
        if (brushTitle != null)
            SetBrushTitle(brushTitle);
        if (_docked)
            ScheduleFitToView();
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        var sc = App.Shortcuts;
        if (sc.Undo.Matches(e))
        {
            _view.Undo();
            e.Handled = true;
            return;
        }
        if (sc.Redo.Matches(e) || sc.RedoAlt.Matches(e))
        {
            _view.Redo();
            e.Handled = true;
        }
    }

    private void OnNodeSelected(BrushTipNode? node)
    {
        _selectedNode = node;
        RefreshPropertyPanel();
    }

    private void OnNodeRightClicked(BrushTipNode node)
    {
        ShowNodeContextMenu(node);
    }

    private void OnCanvasRightClicked(Point worldPosition)
    {
        _pendingAddPosition = worldPosition;
        ShowAddNodeMenu();
    }

    private void OnGraphModified()
    {
        _graph = _view.Graph;
        RefreshPropertyPanel();
        DoCommit();
    }

    private void ShowPresetMenu(Control placement)
    {
        var menu = OpenMenu();
        var presets = new (string Label, BrushTipShape Shape, float DefaultAspect)[]
        {
            ("Round", BrushTipShape.Circle, 1.0f),
            ("Soft", BrushTipShape.SoftRound, 1.0f),
            ("Flat", BrushTipShape.Flat, 1.0f),
            ("Oval", BrushTipShape.Ellipse, 2.4f),
            ("Square", BrushTipShape.Rectangle, 1.0f),
            ("Chalk", BrushTipShape.Chalk, 1.0f),
            ("Bristle", BrushTipShape.Bristle, 1.0f),
            ("Scatter", BrushTipShape.Scatter, 1.0f),
        };
        foreach (var (label, shape, aspect) in presets)
        {
            var item = new MenuItem { Header = label, FontSize = 11 };
            var capturedShape = shape;
            var capturedAspect = aspect;
            item.Click += (_, _) =>
            {
                _graph = BrushTipNodeGraph.FromProceduralShape(capturedShape, capturedAspect).DeepClone();
                _graph.BuiltInShape = null;
                var positions = new Dictionary<string, Avalonia.Point>();
                _view.LoadGraph(_graph, positions);
                _view.AutoLayout();
                _selectedNode = null;
                RefreshPropertyPanel();
                DoCommit();
            };
            menu.Items.Add(item);
        }
        menu.Open(placement);
    }

    private ContextMenu OpenMenu()
    {
        _openMenu?.Close();
        var menu = new ContextMenu();
        _openMenu = menu;
        return menu;
    }

    private void ShowAddNodeMenu()
    {
        var menu = OpenMenu();
        foreach (var item in BuildGroupedAddNodeItems(kind =>
        {
            var node = _view.AddNode(kind, _pendingAddPosition);
            if (node != null)
            {
                InitializeNewNode(node);
                _graph = _view.Graph;
                _selectedNode = node;
                RefreshPropertyPanel();
                DoCommit();
            }
        }))
            menu.Items.Add(item);
        menu.Open(_view);
    }

    private void ShowNodeContextMenu(BrushTipNode node)
    {
        var menu = OpenMenu();

        // Delete option (only for non-output)
        if (node.Id != _graph.OutputNodeId)
        {
            var deleteItem = new MenuItem { Header = "Delete", FontSize = 11 };
            deleteItem.Click += (_, _) =>
            {
                _view.DeleteNode(node.Id);
                if (_selectedNode?.Id == node.Id) { _selectedNode = null; RefreshPropertyPanel(); }
                _graph = _view.Graph;
                DoCommit();
            };
            menu.Items.Add(deleteItem);
        }

        menu.Items.Add(new Separator());

        // Add node submenu
        var addSub = new MenuItem { Header = "Add Node", FontSize = 11 };
        foreach (var item in BuildGroupedAddNodeItems(kind =>
        {
            var newNode = _view.AddNode(kind);
            if (newNode != null)
            {
                InitializeNewNode(newNode);
                _graph = _view.Graph;
                _selectedNode = newNode;
                RefreshPropertyPanel();
                DoCommit();
            }
        }))
            addSub.Items.Add(item);
        menu.Items.Add(addSub);

        menu.Open(_view);
    }

    private static IEnumerable<MenuItem> BuildGroupedAddNodeItems(Action<BrushTipNodeKind> onAdd)
    {
        foreach (var group in AddableNodeKindGroups())
        {
            var groupItem = new MenuItem { Header = group.Name, FontSize = 11 };
            foreach (var kind in group.Kinds)
            {
                var item = new MenuItem { Header = NodeKindDisplayName(kind), FontSize = 11 };
                var capturedKind = kind;
                item.Click += (_, _) => onAdd(capturedKind);
                groupItem.Items.Add(item);
            }
            yield return groupItem;
        }
    }

    private void RefreshPropertyPanel()
    {
        _propertyContent.Children.Clear();
        if (_selectedNode != null && !_graph.Nodes.Any(n => n.Id == _selectedNode.Id))
            _selectedNode = null;
        if (_selectedNode == null)
        {
            ShowNoSelection();
            return;
        }

        var headerText = new TextBlock
        {
            Text = $"{_selectedNode.Kind}  ·  {_selectedNode.Id}",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        _propertyContent.Children.Add(headerText);

        if (_selectedNode.Kind == BrushTipNodeKind.ImageSampler)
            AddImageSamplerSelector();

        var inputCount = NodeInputCount(_selectedNode.Kind);
        if (inputCount > 0)
        {
            _propertyContent.Children.Add(new TextBlock
            {
                Text = "INPUTS",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                Margin = new Thickness(0, 4, 0, 2)
            });

            var sourceOptions = _graph.Nodes
                .Where(n => n.Id != _graph.OutputNodeId && n.Id != _selectedNode.Id)
                .Select(n => n.Id)
                .Prepend("— none —")
                .ToList();

            for (var i = 0; i < inputCount; i++)
            {
                var idx = i;
                var inputLabel = _selectedNode.Kind switch
                {
                    BrushTipNodeKind.Output => "Input",
                    BrushTipNodeKind.DistanceField or BrushTipNodeKind.BoxDistanceField
                        or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
                        or BrushTipNodeKind.Noise => "Coord",
                    BrushTipNodeKind.RotateCoordinates or BrushTipNodeKind.PolarRadius
                        or BrushTipNodeKind.PolarAngle => "Coord",
                    BrushTipNodeKind.WarpCoordinates => idx == 0 ? "Coord" : "Warp",
                    BrushTipNodeKind.Threshold => "Mask",
                    BrushTipNodeKind.Invert => "Input",
                    _ => idx == 0 ? "A" : "B"
                };

                var current = idx < _selectedNode.Inputs.Count
                    ? _selectedNode.Inputs[idx] ?? ""
                    : "";
                var combo = new ComboBox
                {
                    ItemsSource = sourceOptions,
                    SelectedItem = sourceOptions.Contains(current) ? current : "— none —",
                    FontSize = 10,
                    MinHeight = 22,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var capturedIdx = idx;
                combo.SelectionChanged += (_, _) =>
                {
                    var chosen = combo.SelectedItem as string ?? "— none —";
                    if (!_view.SetNodeInput(_selectedNode.Id, capturedIdx,
                            chosen != "— none —" ? chosen : "", notify: false))
                    {
                        RefreshPropertyPanel();
                        return;
                    }
                    _graph = _view.Graph;
                    _selectedNode = _graph.Nodes.FirstOrDefault(x => x.Id == _selectedNode.Id);
                    DoCommit();
                };

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = inputLabel,
                            FontSize = 10,
                            Width = 36,
                            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        combo
                    }
                };
                _propertyContent.Children.Add(row);
            }
        }

        var nodeParams = GetNodeParams(_selectedNode.Kind);
        if (nodeParams.Length > 0)
        {
            _propertyContent.Children.Add(new TextBlock
            {
                Text = "PARAMETERS",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                Margin = new Thickness(0, 6, 0, 2)
            });

            foreach (var param in nodeParams)
            {
                var slider = new Slider
                {
                    Minimum = param.Min,
                    Maximum = param.Max,
                    Value = Math.Clamp(param.Get(_selectedNode), param.Min, param.Max),
                    Height = 20
                };
                var valueText = new TextBlock
                {
                    Text = param.Get(_selectedNode).ToString("F2", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Width = 36,
                    Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                var capturedParam = param;
                slider.ValueChanged += (_, args) =>
                {
                    var val = (float)args.NewValue;
                    var selectedId = _selectedNode.Id;
                    _view.UpdateNode(selectedId, n => capturedParam.Set(n, val), notify: false);
                    _graph = _view.Graph;
                    _selectedNode = _graph.Nodes.FirstOrDefault(x => x.Id == selectedId) ?? _selectedNode;
                    valueText.Text = val.ToString("F2", CultureInfo.InvariantCulture);
                    DoCommit();
                };

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = param.Name,
                            FontSize = 10,
                            Width = 56,
                            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        slider,
                        valueText
                    }
                };
                _propertyContent.Children.Add(row);
            }
        }

        if (_selectedNode.Id != _graph.OutputNodeId)
        {
            var delBtn = new Button
            {
                Content = "Delete Node",
                FontSize = 10,
                MinHeight = 22,
                Margin = new Thickness(0, 8, 0, 0)
            };
            delBtn.Click += (_, _) =>
            {
                var id = _selectedNode.Id;
                _selectedNode = null;
                _view.DeleteNode(id);
                RefreshPropertyPanel();
                DoCommit();
            };
            _propertyContent.Children.Add(delBtn);
        }
    }

    private void ShowNoSelection()
    {
        _propertyContent.Children.Add(new TextBlock
        {
            Text = "No node selected",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Margin = new Thickness(8, 20, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        _propertyContent.Children.Add(new TextBlock
        {
            Text = "Right-click canvas to add",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
    }

    private void AddImageSamplerSelector()
    {
        _propertyContent.Children.Add(new TextBlock
        {
            Text = "IMAGE",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Margin = new Thickness(0, 4, 0, 2)
        });

        if (_imageSamplers.Count == 0)
        {
            _propertyContent.Children.Add(new TextBlock
            {
                Text = "Add images in Brush Tip first.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var selected = ImageSamplerOptions.Match(_imageSamplers, _selectedNode!.PngBytes)
            ?? _imageSamplers[0];
        if (_selectedNode!.PngBytes.Length == 0)
            SetImageSamplerBytes(_selectedNode.Id, selected.Tip.PngBytes);

        var combo = new ComboBox
        {
            ItemsSource = _imageSamplers,
            SelectedItem = selected,
            FontSize = 10,
            MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ImageSamplerOption option || _selectedNode == null)
                return;
            SetImageSamplerBytes(_selectedNode.Id, option.Tip.PngBytes);
            DoCommit();
        };
        _propertyContent.Children.Add(combo);
    }

    private void InitializeNewNode(BrushTipNode node)
    {
        if (node.Kind != BrushTipNodeKind.ImageSampler || node.PngBytes.Length > 0 || _imageSamplers.Count == 0)
            return;
        SetImageSamplerBytes(node.Id, _imageSamplers[0].Tip.PngBytes);
    }

    private void SetImageSamplerBytes(string nodeId, byte[] pngBytes)
    {
        _view.UpdateNode(nodeId, n => n.PngBytes = pngBytes.ToArray(), notify: false);
        _graph = _view.Graph;
        _selectedNode = _graph.Nodes.FirstOrDefault(x => x.Id == nodeId) ?? _selectedNode;
    }

    private void DoCommit()
    {
        if (_isCommitting) return;
        _isCommitting = true;
        try
        {
            var cloned = _graph.DeepClone();
            if (cloned.Validate().Count == 0)
            {
                cloned.BuiltInShape = null;
                _onCommit(cloned);
            }
        }
        finally
        {
            _isCommitting = false;
        }
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

    private sealed record NodeKindGroup(string Name, BrushTipNodeKind[] Kinds);

    private static IReadOnlyList<NodeKindGroup> AddableNodeKindGroups()
        =>
        [
            new("Input / Coordinates",
            [
                BrushTipNodeKind.Coordinates,
                BrushTipNodeKind.RotateCoordinates,
                BrushTipNodeKind.WarpCoordinates,
                BrushTipNodeKind.PolarRadius,
                BrushTipNodeKind.PolarAngle,
                BrushTipNodeKind.Value
            ]),
            new("Fields / Generators",
            [
                BrushTipNodeKind.DistanceField,
                BrushTipNodeKind.BoxDistanceField,
                BrushTipNodeKind.LinearGradient,
                BrushTipNodeKind.Stripe,
                BrushTipNodeKind.ImageSampler,
                BrushTipNodeKind.Noise,
                BrushTipNodeKind.Bristle
            ]),
            new("Math / Combine",
            [
                BrushTipNodeKind.Add,
                BrushTipNodeKind.Subtract,
                BrushTipNodeKind.Multiply,
                BrushTipNodeKind.Min,
                BrushTipNodeKind.Max,
                BrushTipNodeKind.Mix
            ]),
            new("Mask / Remap",
            [
                BrushTipNodeKind.Threshold,
                BrushTipNodeKind.SmoothStep,
                BrushTipNodeKind.Power,
                BrushTipNodeKind.Sine,
                BrushTipNodeKind.Absolute,
                BrushTipNodeKind.Invert
            ])
        ];

    private static string NodeKindDisplayName(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.DistanceField => "Ellipse Distance",
        BrushTipNodeKind.BoxDistanceField => "Box Distance",
        BrushTipNodeKind.RotateCoordinates => "Rotate Coordinates",
        BrushTipNodeKind.WarpCoordinates => "Warp Coordinates",
        BrushTipNodeKind.PolarRadius => "Polar Radius",
        BrushTipNodeKind.PolarAngle => "Polar Angle",
        BrushTipNodeKind.LinearGradient => "Linear Gradient",
        BrushTipNodeKind.ImageSampler => "Image Sampler",
        BrushTipNodeKind.SmoothStep => "Smooth Step",
        _ => kind.ToString()
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
}
