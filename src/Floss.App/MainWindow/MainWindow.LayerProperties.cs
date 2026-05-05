using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Document;

namespace Floss.App;

public partial class MainWindow
{
    private StackPanel _layerPropertiesPanel = null!;
    private CheckBox _layerColorCheck = null!;
    private Button _layerColorSquare = null!;
    private ComboBox _expressionColorCombo = null!;

    private StackPanel BuildLayerPropertiesSection()
    {
        _layerPropertiesPanel = new StackPanel { Spacing = 6, Margin = new Thickness(10, 6, 10, 10) };

        // ── Layer Color ──
        var layerColorRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };

        _layerColorCheck = new CheckBox
        {
            Content = "Layer color",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _layerColorCheck.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                var enabled = _layerColorCheck.IsChecked == true;
                UpdateLayerColorState(enabled);
            }
        };

        _layerColorSquare = new Button
        {
            Width = 32,
            Height = 18,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _layerColorSquare.Click += (_, _) => OpenLayerColorPicker();

        DockPanel.SetDock(_layerColorSquare, Dock.Right);
        layerColorRow.Children.Add(_layerColorCheck);
        layerColorRow.Children.Add(_layerColorSquare);

        // ── Expression Color ──
        var exprRow = new DockPanel { LastChildFill = true };
        var exprLabel = new TextBlock
        {
            Text = "Expression color",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center
        };
        _expressionColorCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<ExpressionColorMode>(),
            SelectedItem = ExpressionColorMode.Color,
            FontSize = 11,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _expressionColorCombo.SelectionChanged += (_, _) =>
        {
            if (_expressionColorCombo.SelectedItem is ExpressionColorMode mode)
                SetExpressionColor(mode);
        };
        DockPanel.SetDock(exprLabel, Dock.Left);
        exprRow.Children.Add(exprLabel);
        exprRow.Children.Add(_expressionColorCombo);

        _layerPropertiesPanel.Children.Add(layerColorRow);
        _layerPropertiesPanel.Children.Add(exprRow);

        RefreshLayerProperties();
        return _layerPropertiesPanel;
    }

    private void RefreshLayerProperties()
    {
        if (_layerPropertiesPanel == null) return;
        var layer = _canvas?.Document?.ActiveLayer;
        if (layer == null) return;

        _syncingToolPropertyPanel = true;
        try
        {
            var hasColor = layer.LayerColor.HasValue;
            _layerColorCheck.IsChecked = hasColor;
            _layerColorSquare.IsEnabled = hasColor;

            if (hasColor && layer.LayerColor is { } c)
            {
                _layerColorSquare.Background = new SolidColorBrush(c);
                _layerColorSquare.BorderBrush = new SolidColorBrush(c);
            }
            else
            {
                _layerColorSquare.Background = new SolidColorBrush(Color.Parse(Bg2));
                _layerColorSquare.BorderBrush = new SolidColorBrush(Color.Parse(Stroke));
            }

            _expressionColorCombo.SelectedItem = layer.ExpressionColor;
        }
        finally
        {
            _syncingToolPropertyPanel = false;
        }
    }

    private void UpdateLayerColorState(bool enabled)
    {
        var canvas = _canvas;
        if (canvas == null) return;
        var layer = canvas.Document?.ActiveLayer;
        if (layer == null) return;

        if (enabled)
        {
            // Enable with current foreground color
            var color = canvas.Brush.Color;
            canvas.SetActiveLayerColor(color);
        }
        else
        {
            canvas.SetActiveLayerColor(null);
        }

        RefreshLayerProperties();
    }

    private void SetExpressionColor(ExpressionColorMode mode)
    {
        var doc = _canvas?.Document;
        if (doc == null || _syncingToolPropertyPanel) return;

        doc.SetExpressionColor(doc.ActiveLayerIndex, mode);
    }

    private void OpenLayerColorPicker()
    {
        var canvas = _canvas;
        if (canvas == null) return;
        var layer = canvas.Document?.ActiveLayer;
        if (layer == null) return;

        var currentColor = layer.LayerColor ?? canvas.Brush.Color;
        var picker = new ColorPickerWindow(currentColor, color =>
        {
            canvas.SetActiveLayerColor(color);
            RefreshLayerProperties();
        });
        picker.Show(this);
    }
}
