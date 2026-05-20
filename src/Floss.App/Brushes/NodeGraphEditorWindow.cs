using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App;

using static AppColors;

public sealed class NodeGraphEditorWindow : Window
{
    private readonly NodeGraphView _view = new();
    private readonly StackPanel _propertyContent = new() { Spacing = 6, Margin = new Thickness(8) };
    private readonly Border _propertyBorder;
    private readonly Action<BrushTipNodeGraph> _onCommit;
    private readonly Action<BrushTipNodeGraph, string>? _onSaveAsNew;

    private BrushTipNodeGraph _graph;
    private BrushTipNode? _selectedNode;
    private bool _isCommitting;
    private int _saveCounter;
    private ContextMenu? _openMenu;

    public NodeGraphEditorWindow(BrushTipNodeGraph graph, Action<BrushTipNodeGraph> onCommit,
        Action<BrushTipNodeGraph, string>? onSaveAsNew = null)
    {
        _graph = graph.DeepClone();
        _onCommit = onCommit;
        _onSaveAsNew = onSaveAsNew;

        Title = "Node Graph Editor";
        Width = 960;
        Height = 680;
        MinWidth = 700;
        MinHeight = 400;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        ShowInTaskbar = false;

        var layoutBtn = new Button { Content = "Auto Layout", FontSize = 11, MinHeight = 24 };
        layoutBtn.Click += (_, _) => _view.AutoLayout();

        var applyBtn = new Button { Content = "✓ Apply", FontSize = 11, MinHeight = 24 };
        applyBtn.Click += (_, _) => DoCommit();

        var saveAsBtn = new Button
        {
            Content = "Save as New",
            FontSize = 11,
            MinHeight = 24,
            IsVisible = _onSaveAsNew != null
        };
        saveAsBtn.Click += (_, _) =>
        {
            if (_onSaveAsNew == null) return;
            _saveCounter++;
            var clone = _graph.DeepClone();
            clone.BuiltInShape = null;
            _onSaveAsNew(clone, $"Custom Node Graph {_saveCounter}");
        };

        var newFromPresetBtn = new Button { Content = "New from Preset", FontSize = 11, MinHeight = 24 };
        newFromPresetBtn.Click += (_, _) => ShowPresetMenu(newFromPresetBtn);

        var toolbarPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6),
            Spacing = 6,
            Children =
            {
                newFromPresetBtn,
                new Border { Width = 1, Height = 20, Background = new SolidColorBrush(Color.Parse(Stroke)), Margin = new Thickness(4, 0) },
                layoutBtn,
                new Border { Width = 1, Height = 20, Background = new SolidColorBrush(Color.Parse(Stroke)), Margin = new Thickness(4, 0) },
                saveAsBtn,
                new Border { Width = 1, Height = 20, Background = new SolidColorBrush(Color.Parse(Stroke)), Margin = new Thickness(4, 0) },
                applyBtn
            }
        };

        var toolbar = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Child = toolbarPanel
        };

        var statusText = new TextBlock
        {
            Text = "Middle-click pan · Scroll zoom · Drag header move · Drag port connect · Right-click canvas add node · Ctrl+Z undo · Del delete",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted))
        };

        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3),
            Child = statusText
        };

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
            Width = 220
        };
        ShowNoSelection();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
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

        grid.Children.Add(statusBorder);
        Grid.SetRow(statusBorder, 2);
        Grid.SetColumnSpan(statusBorder, 2);

        Content = grid;

        _view.LoadGraph(_graph, new());
        _view.AutoLayout();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (ctrl && e.Key == Key.Z && !shift)
        {
            _view.Undo();
            _graph = _view.Graph;
            _selectedNode = null;
            RefreshPropertyPanel();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Y || (ctrl && shift && e.Key == Key.Z))
        {
            _view.Redo();
            _graph = _view.Graph;
            _selectedNode = null;
            RefreshPropertyPanel();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _view.SetView(50, 50, 1.2);
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

    private void OnCanvasRightClicked()
    {
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
        foreach (var kind in Enum.GetValues<BrushTipNodeKind>().Where(k => k != BrushTipNodeKind.Output))
        {
            var item = new MenuItem { Header = kind.ToString(), FontSize = 11 };
            var capturedKind = kind;
            item.Click += (_, _) =>
            {
                var node = _view.AddNode(capturedKind);
                if (node != null)
                {
                    _graph = _view.Graph;
                    _selectedNode = node;
                    RefreshPropertyPanel();
                    DoCommit();
                }
            };
            menu.Items.Add(item);
        }
        menu.Open(this);
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
        foreach (var kind in Enum.GetValues<BrushTipNodeKind>().Where(k => k != BrushTipNodeKind.Output))
        {
            var addItem = new MenuItem { Header = kind.ToString(), FontSize = 11 };
            var capturedKind = kind;
            addItem.Click += (_, _) =>
            {
                var newNode = _view.AddNode(capturedKind);
                if (newNode != null)
                {
                    _graph = _view.Graph;
                    _selectedNode = newNode;
                    RefreshPropertyPanel();
                    DoCommit();
                }
            };
            addSub.Items.Add(addItem);
        }
        menu.Items.Add(addSub);

        menu.Open(this);
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
                    var n = _graph.Nodes.FirstOrDefault(x => x.Id == _selectedNode.Id);
                    if (n == null) return;
                    while (n.Inputs.Count <= capturedIdx)
                        n.Inputs.Add("");
                    n.Inputs[capturedIdx] = chosen != "— none —" ? chosen : "";
                    while (n.Inputs.Count > 0 && string.IsNullOrEmpty(n.Inputs[^1]))
                        n.Inputs.RemoveAt(n.Inputs.Count - 1);
                    _view.InvalidateVisual();
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
                    capturedParam.Set(_selectedNode, val);
                    valueText.Text = val.ToString("F2", CultureInfo.InvariantCulture);
                    _view.InvalidateVisual();
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

    private void DoCommit()
    {
        if (_isCommitting) return;
        _isCommitting = true;
        try
        {
            var cloned = _graph.DeepClone();
            if (cloned.Validate().Count == 0)
                _onCommit(cloned);
        }
        finally
        {
            _isCommitting = false;
        }
    }

    private static int NodeInputCount(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Output => 1,
        BrushTipNodeKind.Threshold or BrushTipNodeKind.Invert => 1,
        BrushTipNodeKind.Add or BrushTipNodeKind.Multiply or BrushTipNodeKind.Max
            or BrushTipNodeKind.Min or BrushTipNodeKind.Subtract or BrushTipNodeKind.Mix => 2,
        _ => 0
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
}
