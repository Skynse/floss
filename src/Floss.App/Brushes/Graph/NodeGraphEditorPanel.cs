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

namespace Floss.App.Brushes.Graph;

using static Floss.App.Config.AppColors;

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
    private bool _syncingPropertyParams;
    private ContextMenu? _openMenu;
    private Point _pendingAddPosition;

    public bool IsDocked => _docked;

    internal NodeGraphView GraphView => _view;

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

        toolbar.ZIndex = 1;

        var graphHost = new Border
        {
            ClipToBounds = true,
            Child = _view
        };
        _view.HorizontalAlignment = HorizontalAlignment.Stretch;
        _view.VerticalAlignment = VerticalAlignment.Stretch;

        grid.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        Grid.SetColumnSpan(toolbar, 2);

        grid.Children.Add(graphHost);
        Grid.SetRow(graphHost, 1);
        Grid.SetColumn(graphHost, 0);

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
        ClipToBounds = true;
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
        _view.KeyDown += (_, e) =>
        {
            if (!_docked && TryHandleKeyDown(e))
                e.Handled = true;
        };
        _view.KeyUp += (_, e) =>
        {
            if (!_docked && TryHandleKeyUp(e))
                e.Handled = true;
        };
        Focusable = true;
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    public bool TryHandleKeyDown(KeyEventArgs e)
        => IsVisible && _view.TryHandleKeyDown(e);

    public bool TryHandleKeyUp(KeyEventArgs e)
        => IsVisible && _view.TryHandleKeyUp(e);

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            !e.GetCurrentPoint(this).Properties.IsRightButtonPressed &&
            !e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            return;

        Focus();
        _view.Focus(NavigationMethod.Pointer);
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
        if (_selectedNode != null)
            _selectedNode = _graph.Nodes.FirstOrDefault(n => n.Id == _selectedNode.Id);
        RefreshPropertyPanel();
        DoCommit();
    }

    private void ShowPresetMenu(Control placement)
    {
        var menu = OpenMenu();

        foreach (var (label, graph) in new (string Label, BrushTipNodeGraph Graph)[]
        {
            ("Circle", BrushTipNodeGraph.SimpleCircle()),
            ("Rectangle", BrushTipNodeGraph.SimpleRectangle()),
            ("Rounded Rectangle", BrushTipNodeGraph.SimpleRoundedRectangle()),
        })
        {
            var item = new MenuItem { Header = label, FontSize = 11 };
            var captured = graph.DeepClone();
            item.Click += (_, _) => LoadPresetGraph(captured);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());

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
                LoadPresetGraph(BrushTipNodeGraph.FromProceduralShape(capturedShape, capturedAspect).DeepClone());
            };
            menu.Items.Add(item);
        }
        menu.Open(placement);
    }

    private void LoadPresetGraph(BrushTipNodeGraph graph)
    {
        graph.BuiltInShape = null;
        _graph = graph;
        _view.LoadGraph(_graph, new Dictionary<string, Avalonia.Point>());
        _view.AutoLayout();
        _selectedNode = null;
        RefreshPropertyPanel();
        DoCommit();
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

    private bool _refreshingPropertyPanel;

    private void RefreshPropertyPanel()
    {
        if (_refreshingPropertyPanel)
            return;

        _refreshingPropertyPanel = true;
        try
        {
            RefreshPropertyPanelCore();
        }
        finally
        {
            _refreshingPropertyPanel = false;
        }
    }

    private void RefreshPropertyPanelCore()
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

            for (var i = 0; i < inputCount; i++)
            {
                var idx = i;
                var inputLabel = BrushTipNodePorts.InputLabel(_selectedNode.Kind, idx);
                if (_selectedNode.Kind is BrushTipNodeKind.Threshold or BrushTipNodeKind.Invert)
                {
                    inputLabel = _selectedNode.Kind switch
                    {
                        BrushTipNodeKind.Threshold => "Mask",
                        BrushTipNodeKind.Invert => "Input",
                        _ => inputLabel
                    };
                }
                else if (_selectedNode.Kind == BrushTipNodeKind.Output)
                    inputLabel = "Input";

                var compatibleSources = _graph.Nodes
                    .Where(n => n.Id != _graph.OutputNodeId && n.Id != _selectedNode.Id)
                    .Where(n => BrushTipNodePorts.CanConnect(n.Kind, _selectedNode.Kind, idx))
                    .Select(n => n.Id)
                    .Prepend("— none —")
                    .ToList();

                var current = idx < _selectedNode.Inputs.Count
                    ? _selectedNode.Inputs[idx] ?? ""
                    : "";
                var combo = new ComboBox
                {
                    ItemsSource = compatibleSources,
                    SelectedItem = compatibleSources.Contains(current) ? current : "— none —",
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
                var range = param.Max - param.Min;
                var current = Math.Clamp(param.Get(_selectedNode), param.Min, param.Max);
                var slider = new Slider
                {
                    Minimum = param.Min,
                    Maximum = param.Max,
                    Value = current,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    SmallChange = Math.Max(range / 200.0, 0.001),
                    LargeChange = Math.Max(range / 20.0, 0.01),
                };
                var valueBox = new TextBox
                {
                    Text = FormatParamValue(current),
                    Width = 52,
                    MinWidth = 52,
                    FontSize = 10,
                    Padding = new Thickness(4, 1),
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Background = new SolidColorBrush(Color.Parse(Bg0)),
                    Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
                    BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                    BorderThickness = new Thickness(1),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                var capturedParam = param;
                var selectedId = _selectedNode.Id;

                void ApplyParamValue(float val)
                {
                    val = Math.Clamp(val, capturedParam.Min, capturedParam.Max);
                    _syncingPropertyParams = true;
                    try
                    {
                        slider.Value = val;
                        valueBox.Text = FormatParamValue(val);
                    }
                    finally
                    {
                        _syncingPropertyParams = false;
                    }

                    _view.UpdateNode(selectedId, n => capturedParam.Set(n, val), notify: false);
                    _graph = _view.Graph;
                    _selectedNode = _graph.Nodes.FirstOrDefault(x => x.Id == selectedId) ?? _selectedNode;
                    DoCommit();
                }

                slider.ValueChanged += (_, args) =>
                {
                    if (_syncingPropertyParams) return;
                    ApplyParamValue((float)args.NewValue);
                };

                void CommitValueBox()
                {
                    if (_syncingPropertyParams) return;
                    if (!float.TryParse(valueBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        parsed = capturedParam.Get(_selectedNode);
                    ApplyParamValue(parsed);
                }

                valueBox.KeyDown += (_, e) =>
                {
                    if (e.Key is Key.Enter or Key.Return)
                    {
                        CommitValueBox();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Escape)
                    {
                        valueBox.Text = FormatParamValue(capturedParam.Get(_selectedNode));
                        e.Handled = true;
                    }
                };
                valueBox.LostFocus += (_, _) => CommitValueBox();

                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(1, GridUnitType.Star),
                        new ColumnDefinition(GridLength.Auto),
                    },
                    ColumnSpacing = 6,
                };

                var label = new TextBlock
                {
                    Text = param.Name,
                    FontSize = 10,
                    MinWidth = 56,
                    MaxWidth = 72,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(label, 0);
                Grid.SetColumn(slider, 1);
                Grid.SetColumn(valueBox, 2);
                row.Children.Add(label);
                row.Children.Add(slider);
                row.Children.Add(valueBox);
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
                Margin = new Thickness(0, 8, 0, 0),
                Classes = { "destructive" }
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

        var selected = ImageSamplerOptions.Match(_imageSamplers, _selectedNode!)
            ?? _imageSamplers[0];

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
            if (string.Equals(_selectedNode.MaterialTipId, option.Tip.Id, StringComparison.Ordinal))
                return;
            SetMaterialTipReference(_selectedNode.Id, option.Tip, notify: true);
            DoCommit();
        };
        _propertyContent.Children.Add(combo);
    }

    private void InitializeNewNode(BrushTipNode node)
    {
        if (node.Kind != BrushTipNodeKind.ImageSampler || _imageSamplers.Count == 0)
            return;
        if (!string.IsNullOrEmpty(node.MaterialTipId))
            return;
        if (BrushMaterialTips.ResolveSamplerPng(node, _imageSamplers.Select(o => o.Tip).ToList()).Length > 0)
            return;
        SetMaterialTipReference(node.Id, _imageSamplers[0].Tip, notify: false);
    }

    private void SetMaterialTipReference(string nodeId, BrushTipData tip, bool notify = false)
    {
        var normalized = BrushMaterialTips.NormalizeTip(tip);
        _view.SetImageSamplerMaterialTip(nodeId, normalized.Id, pushHistory: false, notify: notify);
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

    private static int NodeInputCount(BrushTipNodeKind kind)
        => BrushTipNodeRegistry.InputCount(kind);

    private static IReadOnlyList<(string Name, BrushTipNodeKind[] Kinds)> AddableNodeKindGroups()
        => BrushTipNodeRegistry.AddableGroups;

    private static string NodeKindDisplayName(BrushTipNodeKind kind)
        => BrushTipNodeRegistry.DisplayName(kind);

    private static string FormatParamValue(float value)
        => BrushTipNodePorts.FormatDisplayValue(value);

    private static NodeParam[] GetNodeParams(BrushTipNodeKind kind)
        => BrushTipNodeRegistry.Params(kind);
}
