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
    private CheckBox _referenceLayerCheck = null!;
    private Button _layerColorSquare = null!;
    private ComboBox _expressionColorCombo = null!;

    // The last layer color used; independent of the brush color.
    // Used when the user toggles layer color on and the current layer has none.
    private Avalonia.Media.Color _lastLayerColor = Avalonia.Media.Color.Parse("#3d6fd8");

    private StackPanel BuildLayerPropertiesSection()
    {
        _layerPropertiesPanel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 8, 6) };

        // ── Layer Color ──
        var layerColorRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2) };

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
            Width = 24,
            Height = 16,
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

        _referenceLayerCheck = new CheckBox
        {
            Content = "Reference layer",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _referenceLayerCheck.PropertyChanged += (_, e) =>
        {
            if (_syncingToolPropertyPanel || e.Property != ToggleButton.IsCheckedProperty) return;
            var canvas = _canvas;
            if (canvas == null) return;
            var doc = canvas.Document;
            if (doc.ActiveLayerIndex < 0) return;
            if (doc.ActiveLayer.IsReference != (_referenceLayerCheck.IsChecked == true))
                canvas.ToggleLayerReference(doc.ActiveLayerIndex);
        };

        // ── Expression Color ──
        var exprRow = new DockPanel { LastChildFill = true };
        var exprLabel = new TextBlock
        {
            Text = "Expression color",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 80,
            VerticalAlignment = VerticalAlignment.Center
        };
        _expressionColorCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<ExpressionColorMode>(),
            SelectedItem = ExpressionColorMode.Color,
            FontSize = 11,
            MinHeight = 22,
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
        _layerPropertiesPanel.Children.Add(_referenceLayerCheck);
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
            _referenceLayerCheck.IsChecked = layer.IsReference;
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
            // Use the layer's own stored color, or the last used layer color.
            // Never use the brush color.
            var color = layer.LayerColor ?? _lastLayerColor;
            canvas.SetActiveLayerColor(color);
        }
        else
        {
            // Remember the color we are turning off so we can restore it later.
            if (layer.LayerColor is { } c)
                _lastLayerColor = c;
            canvas.SetActiveLayerColor(null);
        }

        RefreshLayerProperties();
    }

    private void ToggleActiveLayerColor()
    {
        var layer = _canvas?.Document?.ActiveLayer;
        if (layer == null) return;

        UpdateLayerColorState(!layer.LayerColor.HasValue);
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

        // Layer color has its own independent color — never falls back to brush.
        var currentColor = layer.LayerColor ?? _lastLayerColor;
        var picker = new ColorPickerWindow(currentColor, color =>
        {
            _lastLayerColor = color;
            canvas.SetActiveLayerColor(color);
            RefreshLayerProperties();
        });
        picker.Show(this);
    }
}
