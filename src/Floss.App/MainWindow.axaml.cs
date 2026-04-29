using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App;

public partial class MainWindow : Window
{
    private readonly Color[] _swatches =
    [
        Color.Parse("#111111"), Color.Parse("#ffffff"), Color.Parse("#e53935"),
        Color.Parse("#ff8f00"), Color.Parse("#ffeb3b"), Color.Parse("#43a047"),
        Color.Parse("#00acc1"), Color.Parse("#1e88e5"), Color.Parse("#5e35b1"),
        Color.Parse("#d81b60"), Color.Parse("#795548"), Color.Parse("#78909c")
    ];

    private double _zoom = 1.0;
    private bool _spacePanning;
    private bool _isPanning;
    private bool _syncingLayerUi;
    private Point _lastPanPoint;
    private ScaleTransform _workspaceScale = null!;
    private TranslateTransform _workspacePan = null!;

    public MainWindow()
    {
        InitializeComponent();
        var workspaceTransform = (TransformGroup)Workspace.RenderTransform!;
        _workspaceScale = (ScaleTransform)workspaceTransform.Children[0];
        _workspacePan = (TranslateTransform)workspaceTransform.Children[1];
        BuildSwatches();
        BuildPresets();
        WireControls();
        ApplyPreset(BrushPreset.Defaults[0]);
        SetColor(_swatches[0]);
        BuildLayerList();
        UpdateStatus();
    }

    private void WireControls()
    {
        BrushToolButton.Click += (_, _) => SetTool("brush");
        EraserToolButton.Click += (_, _) => SetTool("eraser");
        ClearButton.Click += (_, _) => DrawingCanvas.Clear();
        UndoButton.Click += (_, _) => DrawingCanvas.Undo();
        RedoButton.Click += (_, _) => DrawingCanvas.Redo();
        AddLayerButton.Click += (_, _) => DrawingCanvas.AddLayer();
        DuplicateLayerButton.Click += (_, _) => DrawingCanvas.DuplicateLayer();
        DeleteLayerButton.Click += (_, _) => DrawingCanvas.DeleteLayer();
        MoveLayerUpButton.Click += (_, _) => DrawingCanvas.MoveActiveLayer(1);
        MoveLayerDownButton.Click += (_, _) => DrawingCanvas.MoveActiveLayer(-1);

        SizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) DrawingCanvas.SetBrushSize(SizeSlider.Value);
        };
        OpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) DrawingCanvas.SetBrushOpacity(OpacitySlider.Value);
        };
        HardnessSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) DrawingCanvas.SetBrushHardness(HardnessSlider.Value);
        };
        SpacingSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) DrawingCanvas.SetBrushSpacing(SpacingSlider.Value);
        };
        LayerOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (_syncingLayerUi || e.Property != Slider.ValueProperty) return;
            DrawingCanvas.SetActiveLayerOpacity(LayerOpacitySlider.Value);
        };

        DrawingCanvas.StatsChanged += (_, _) => UpdateStatus();
        DrawingCanvas.HistoryChanged += (_, _) => UpdateStatus();
        DrawingCanvas.LayersChanged += (_, _) =>
        {
            BuildLayerList();
            UpdateStatus();
        };
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    private void BuildSwatches()
    {
        foreach (var color in _swatches)
        {
            var button = new Button
            {
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(color),
                BorderBrush = Avalonia.Media.Brushes.Transparent,
                CornerRadius = new CornerRadius(16)
            };
            button.Click += (_, _) => SetColor(color);
            SwatchPanel.Children.Add(button);
        }
    }

    private void BuildPresets()
    {
        foreach (var preset in BrushPreset.Defaults)
        {
            var button = new Button
            {
                Content = preset.Name,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(10, 8),
                Tag = preset
            };
            button.Click += (_, _) => ApplyPreset(preset);
            PresetPanel.Children.Add(button);
        }
    }

    private void BuildLayerList()
    {
        LayerPanel.Children.Clear();

        for (var index = DrawingCanvas.Layers.Count - 1; index >= 0; index--)
        {
            var layer = DrawingCanvas.Layers[index];
            var isActive = index == DrawingCanvas.ActiveLayerIndex;
            var row = new Border
            {
                Background = new SolidColorBrush(isActive ? Color.Parse("#2f7df6") : Color.Parse("#22262c")),
                BorderBrush = new SolidColorBrush(isActive ? Color.Parse("#69a2ff") : Color.Parse("#303640")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("28,28,*,Auto")
            };

            var visibilityButton = new Button
            {
                Content = layer.IsVisible ? "◉" : "○",
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Tag = index
            };
            visibilityButton.Click += (_, _) => DrawingCanvas.ToggleLayerVisibility((int)visibilityButton.Tag!);

            var lockButton = new Button
            {
                Content = layer.IsLocked ? "L" : "U",
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Tag = index
            };
            lockButton.Click += (_, _) => DrawingCanvas.ToggleLayerLock((int)lockButton.Tag!);

            var nameButton = new Button
            {
                Content = layer.Name,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderBrush = Avalonia.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(isActive ? Color.Parse("#ffffff") : Color.Parse("#d8dbe0")),
                Padding = new Thickness(8, 2),
                Tag = index
            };
            nameButton.Click += (_, _) => DrawingCanvas.SelectLayer((int)nameButton.Tag!);

            var opacityText = new TextBlock
            {
                Text = $"{Math.Round(layer.Opacity * 100)}%",
                Foreground = new SolidColorBrush(isActive ? Color.Parse("#eaf2ff") : Color.Parse("#8b9099")),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            Grid.SetColumn(visibilityButton, 0);
            Grid.SetColumn(lockButton, 1);
            Grid.SetColumn(nameButton, 2);
            Grid.SetColumn(opacityText, 3);
            grid.Children.Add(visibilityButton);
            grid.Children.Add(lockButton);
            grid.Children.Add(nameButton);
            grid.Children.Add(opacityText);
            row.Child = grid;
            LayerPanel.Children.Add(row);
        }

        _syncingLayerUi = true;
        LayerOpacitySlider.Value = DrawingCanvas.Layers[DrawingCanvas.ActiveLayerIndex].Opacity;
        _syncingLayerUi = false;
    }

    private void ApplyPreset(BrushPreset preset)
    {
        var color = DrawingCanvas.PaintColor;
        DrawingCanvas.SetBrush(preset with { Color = color });
        SizeSlider.Value = preset.Size;
        OpacitySlider.Value = preset.Opacity;
        HardnessSlider.Value = preset.Hardness;
        SpacingSlider.Value = preset.Spacing;
        SetTool(preset.Kind == BrushKind.Eraser ? "eraser" : "brush");
        UpdateStatus();
    }

    private void SetColor(Color color)
    {
        ColorWell.Background = new SolidColorBrush(color);
        DrawingCanvas.SetPaintColor(color);
        SetTool("brush");
    }

    private void SetTool(string tool)
    {
        DrawingCanvas.SetTool(tool);
        ToolStatusText.Text = tool == "eraser" ? "Eraser" : DrawingCanvas.Brush.Name;
        FooterStatusText.Text = tool == "eraser" ? "Eraser active" : "Brush active";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        if (e.Key == Key.Space)
        {
            _spacePanning = true;
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Z)
        {
            DrawingCanvas.Redo();
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z)
        {
            DrawingCanvas.Undo();
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.N)
        {
            DrawingCanvas.AddLayer();
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.J)
        {
            DrawingCanvas.DuplicateLayer();
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Delete)
        {
            DrawingCanvas.DeleteLayer();
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Up)
        {
            DrawingCanvas.MoveActiveLayer(1);
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Down)
        {
            DrawingCanvas.MoveActiveLayer(-1);
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y)
        {
            DrawingCanvas.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.B)
        {
            SetTool("brush");
            e.Handled = true;
        }
        else if (e.Key == Key.E)
        {
            SetTool("eraser");
            e.Handled = true;
        }
        else if (e.Key == Key.OemOpenBrackets)
        {
            SizeSlider.Value = Math.Max(SizeSlider.Minimum, SizeSlider.Value - 2);
            e.Handled = true;
        }
        else if (e.Key == Key.OemCloseBrackets)
        {
            SizeSlider.Value = Math.Min(SizeSlider.Maximum, SizeSlider.Value + 2);
            e.Handled = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.D0)
        {
            SetZoom(1.0);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spacePanning = false;
            _isPanning = false;
            e.Handled = true;
        }
    }

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(WorkspaceViewport);
        var middlePan = currentPoint.Properties.IsMiddleButtonPressed;
        if (!_spacePanning && !middlePan) return;
        _isPanning = true;
        _lastPanPoint = e.GetPosition(WorkspaceViewport);
        e.Pointer.Capture(Workspace);
        e.Handled = true;
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var point = e.GetPosition(WorkspaceViewport);
        var delta = point - _lastPanPoint;
        _workspacePan.X += delta.X;
        _workspacePan.Y += delta.Y;
        _lastPanPoint = point;
        e.Handled = true;
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        SetZoom(_zoom * factor);
        e.Handled = true;
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.2, 6.0);
        _workspaceScale.ScaleX = _zoom;
        _workspaceScale.ScaleY = _zoom;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        CanvasStatusText.Text = $"zoom {Math.Round(_zoom * 100)}%  layer {DrawingCanvas.ActiveLayerIndex + 1}/{DrawingCanvas.Layers.Count}  samples {DrawingCanvas.ActiveSampleCount}";
        UndoButton.IsEnabled = DrawingCanvas.CanUndo;
        RedoButton.IsEnabled = DrawingCanvas.CanRedo;
        DeleteLayerButton.IsEnabled = DrawingCanvas.CanDeleteLayer;
        MoveLayerUpButton.IsEnabled = DrawingCanvas.ActiveLayerIndex < DrawingCanvas.Layers.Count - 1;
        MoveLayerDownButton.IsEnabled = DrawingCanvas.ActiveLayerIndex > 0;
    }
}
