using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Document;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed class LayerPropertiesDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    private StackPanel _layerPropertiesPanel = null!;
    private CheckBox _layerColorCheck = null!;
    private CheckBox _referenceLayerCheck = null!;
    private Button _layerColorSquare = null!;
    private ComboBox _expressionColorCombo = null!;
    private StackPanel _maskSection = null!;
    private CheckBox _maskEnabledCheck = null!;

    private Color _lastLayerColor = Color.Parse("#3d6fd8");

    public LayerPropertiesDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);
        Content = BuildLayerPropertiesSection();
    }

    public void Refresh() => RefreshLayerProperties();

    public void ToggleActiveLayerColor()
    {
        var layer = _ps.Canvas.Document?.ActiveLayer;
        if (layer == null) return;
        UpdateLayerColorState(!layer.LayerColor.HasValue);
    }

    private StackPanel BuildLayerPropertiesSection()
    {
        _layerPropertiesPanel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 8, 6) };

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

        DockPanel.SetDock(_layerColorSquare, Avalonia.Controls.Dock.Right);
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
            if (_ps.Sync.SyncingToolPropertyPanel || e.Property != ToggleButton.IsCheckedProperty) return;
            var canvas = _ps.Canvas;
            var doc = canvas.Document;
            if (doc.ActiveLayer is not { } al) return;
            if (al.IsReference != (_referenceLayerCheck.IsChecked == true))
                canvas.ToggleLayerReference(doc.ActiveLayerIndex);
        };

        var exprRow = new StackPanel { Spacing = 4 };
        var exprLabel = new TextBlock
        {
            Text = "Expression color",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
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
        exprRow.Children.Add(exprLabel);
        exprRow.Children.Add(_expressionColorCombo);

        _maskSection = new StackPanel { Spacing = 4, IsVisible = false };
        var maskLabel = new TextBlock
        {
            Text = "Layer mask",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Margin = new Thickness(0, 4, 0, 0)
        };
        _maskEnabledCheck = new CheckBox
        {
            Content = "Mask enabled",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _maskEnabledCheck.PropertyChanged += (_, e) =>
        {
            if (_ps.Sync.SyncingToolPropertyPanel || e.Property != ToggleButton.IsCheckedProperty) return;
            var canvas = _ps.Canvas;
            var doc = canvas.Document;
            if (doc.ActiveLayer is { HasMask: true } al)
            {
                if (al.IsMaskVisible != (_maskEnabledCheck.IsChecked == true))
                    canvas.ToggleLayerMask(doc.ActiveLayerIndex);
            }
        };
        var maskBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var invertBtn = new Button
        {
            Content = "Invert",
            FontSize = 10,
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        invertBtn.Click += (_, _) =>
        {
            var canvas = _ps.Canvas;
            var doc = canvas.Document;
            if (doc.ActiveLayer is { HasMask: true })
                InvertMask(doc.ActiveLayerIndex);
        };
        var applyBtn = new Button
        {
            Content = "Apply",
            FontSize = 10,
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        applyBtn.Click += (_, _) =>
        {
            var canvas = _ps.Canvas;
            var doc = canvas.Document;
            if (doc.ActiveLayer is { HasMask: true })
            {
                canvas.ApplyLayerMask(doc.ActiveLayerIndex);
                RefreshLayerProperties();
            }
        };
        var deleteBtn = new Button
        {
            Content = "Delete",
            FontSize = 10,
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        deleteBtn.Click += (_, _) =>
        {
            var canvas = _ps.Canvas;
            var doc = canvas.Document;
            if (doc.ActiveLayer is { HasMask: true })
            {
                canvas.DeleteLayerMask(doc.ActiveLayerIndex);
                RefreshLayerProperties();
            }
        };
        maskBtnRow.Children.Add(invertBtn);
        maskBtnRow.Children.Add(applyBtn);
        maskBtnRow.Children.Add(deleteBtn);
        _maskSection.Children.Add(maskLabel);
        _maskSection.Children.Add(_maskEnabledCheck);
        _maskSection.Children.Add(maskBtnRow);

        _layerPropertiesPanel.Children.Add(layerColorRow);
        _layerPropertiesPanel.Children.Add(_referenceLayerCheck);
        _layerPropertiesPanel.Children.Add(exprRow);
        _layerPropertiesPanel.Children.Add(_maskSection);

        RefreshLayerProperties();
        return _layerPropertiesPanel;
    }

    private void RefreshLayerProperties()
    {
        if (_layerPropertiesPanel == null) return;
        var layer = _ps.Canvas.Document?.ActiveLayer;
        if (layer == null) return;

        _ps.Sync.SyncingToolPropertyPanel = true;
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

            var hasMask = layer.HasMask && !layer.IsGroup;
            _maskSection.IsVisible = hasMask;
            if (hasMask)
                _maskEnabledCheck.IsChecked = layer.IsMaskVisible;
        }
        finally
        {
            _ps.Sync.SyncingToolPropertyPanel = false;
        }
    }

    private void UpdateLayerColorState(bool enabled)
    {
        var canvas = _ps.Canvas;
        var layer = canvas.Document?.ActiveLayer;
        if (layer == null) return;

        if (enabled)
        {
            var color = layer.LayerColor ?? _lastLayerColor;
            canvas.SetActiveLayerColor(color);
        }
        else
        {
            if (layer.LayerColor is { } c)
                _lastLayerColor = c;
            canvas.SetActiveLayerColor(null);
        }

        RefreshLayerProperties();
    }

    private void SetExpressionColor(ExpressionColorMode mode)
    {
        var doc = _ps.Canvas.Document;
        if (_ps.Sync.SyncingToolPropertyPanel) return;

        doc.SetExpressionColor(doc.ActiveLayerIndex, mode);
    }

    private void OpenLayerColorPicker()
    {
        var canvas = _ps.Canvas;
        var layer = canvas.Document?.ActiveLayer;
        if (layer == null) return;

        var currentColor = layer.LayerColor ?? _lastLayerColor;
        var picker = new ColorPickerWindow(currentColor, color =>
        {
            _lastLayerColor = color;
            canvas.SetActiveLayerColor(color);
            RefreshLayerProperties();
        });
        picker.Show(_ps.Shell.Owner);
    }

    private void InvertMask(int idx)
    {
        var layer = _ps.Canvas.Layers[idx];
        if (layer.MaskPixels == null) return;
        var tiles = layer.MaskPixels.CaptureTiles();
        foreach (var (key, tile) in tiles)
        {
            if (tile == null) continue;
            for (var i = 0; i < tile.Length; i += 4)
            {
                var v = (byte)(255 - tile[i + 3]);
                tile[i] = v;
                tile[i + 1] = v;
                tile[i + 2] = v;
                tile[i + 3] = v;
            }
        }
        layer.MaskPixels.RestoreTiles(tiles);
        layer.MarkMaskThumbnailDirty();
        _ps.Canvas.InvalidateVisual();
    }
}
