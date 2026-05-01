using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Tools;

namespace Floss.App;

public partial class MainWindow
{
    private StackPanel BuildToolPropertySection()
    {
        _toolPropertyTitle = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var detailBtn = SmBtn("⚙", "Open full tool property detail");
        detailBtn.Click += (_, _) => OpenToolPropertyDetail();
        detailBtn.Margin = new Thickness(4, 0, 0, 0);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(detailBtn, Dock.Right);
        header.Children.Add(detailBtn);
        header.Children.Add(_toolPropertyTitle);

        _toolPropertyPanel = new StackPanel { Spacing = 3 };

        var root = new StackPanel
        {
            Margin = new Thickness(10, 6, 10, 10),
            Children = { header, _toolPropertyPanel }
        };
        RefreshToolProperties();
        return root;
    }

    // ── Tool property docker ─────────────────────────────────────────────────
    private sealed record ToolPropertyDescriptor(
        string Id,
        string Label,
        bool DefaultVisible,
        Func<Control> BuildControl);

    private IReadOnlyList<ToolPropertyDescriptor> CurrentToolProperties()
    {
        var tool = _canvas.ActiveTool;
        if (tool is BrushTool)
        {
            return
            [
                SliderProp("brush.size", "Brush Size", true, _sizeSlider, "px"),
                SliderProp("brush.opacity", "Opacity", true, _opacitySlider, "%"),
                SliderProp("brush.flow", "Flow", false, _flowSlider, "%"),
                SliderProp("brush.hardness", "Anti-aliasing", true, _hardnessSlider, "%"),
                SliderProp("brush.spacing", "Spacing", false, _spacingSlider, "%"),
                SliderProp("brush.smoothing", "Stabilization", true, _smoothingSlider, "%"),
                SliderProp("brush.grain", "Grain", false, _grainSlider, "%")
            ];
        }

        if (tool is SelectTool)
        {
            return
            [
                EnumProp("select.mode", "Selection Mode", true, () => _selectTool.Mode, v => _selectTool.Mode = v),
                EnumProp("select.op", "Operation", true, () => _selectTool.Op, v => _selectTool.Op = v)
            ];
        }

        if (tool is MagicWandTool)
        {
            return
            [
                SliderProp("wand.tolerance", "Tolerance", true, () => _magicWandTool.Tolerance, v => _magicWandTool.Tolerance = v, 0, 1, "%"),
                EnumProp("wand.op", "Operation", true, () => _magicWandTool.Op, v => _magicWandTool.Op = v)
            ];
        }

        if (tool is FillTool)
        {
            return
            [
                SliderProp("fill.tolerance", "Tolerance", true, () => _fillTool.Tolerance, v => _fillTool.Tolerance = v, 0, 1, "%")
            ];
        }

        if (tool is GradientTool)
        {
            return
            [
                EnumProp("gradient.type", "Gradient Type", true, () => _gradientTool.GradientType, v => _gradientTool.GradientType = v)
            ];
        }

        if (tool is ShapeTool)
        {
            return
            [
                EnumProp("shape.kind", "Shape", true, () => _shapeTool.Kind, v => _shapeTool.Kind = v),
                EnumProp("shape.drawMode", "Draw Mode", true, () => _shapeTool.DrawMode, v => _shapeTool.DrawMode = v),
                SliderProp("shape.strokeWidth", "Stroke Width", true, () => _shapeTool.StrokeWidth, v => _shapeTool.StrokeWidth = (float)v, 1, 200, "px")
            ];
        }

        if (tool is PolylineTool)
        {
            return
            [
                SliderProp("polyline.strokeWidth", "Stroke Width", true, () => _polylineTool.StrokeWidth, v => _polylineTool.StrokeWidth = (float)v, 1, 200, "px"),
                BoolProp("polyline.closePath", "Close Path", true, () => _polylineTool.ClosePath, v => _polylineTool.ClosePath = v)
            ];
        }

        return [];
    }

    private ToolPropertyDescriptor SliderProp(string id, string label, bool visible, Slider source, string fmt)
        => SliderProp(id, label, visible, () => source.Value, v => source.Value = v, source.Minimum, source.Maximum, fmt);

    private ToolPropertyDescriptor SliderProp(
        string id,
        string label,
        bool visible,
        Func<double> get,
        Action<double> set,
        double min,
        double max,
        string fmt)
        => new(id, label, visible, () =>
        {
            var slider = MkSlider(min, max, Math.Clamp(get(), min, max), label);
            return LabelSlider(label, slider, fmt, set);
        });

    private static ToolPropertyDescriptor EnumProp<T>(string id, string label, bool visible, Func<T> get, Action<T> set)
        where T : struct, Enum
        => new(id, label, visible, () =>
        {
            var combo = new ComboBox
            {
                ItemsSource = Enum.GetValues<T>(),
                SelectedItem = get(),
                FontSize = 11,
                MinHeight = 28,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is T value) set(value);
            };
            return LabeledControl(label, combo);
        });

    private static ToolPropertyDescriptor BoolProp(string id, string label, bool visible, Func<bool> get, Action<bool> set)
        => new(id, label, visible, () =>
        {
            var check = new CheckBox
            {
                IsChecked = get(),
                Content = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
            };
            check.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty)
                    set(check.IsChecked == true);
            };
            return check;
        });

    private static Control LabelSlider(string label, Slider slider, string fmt, Action<double> onChange)
    {
        var row = LabelSlider(label, slider, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) onChange(slider.Value);
        };
        return row;
    }

    private static Control LabeledControl(string label, Control control)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 88,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(control);
        return row;
    }

    private void RefreshToolProperties()
    {
        if (_toolPropertyPanel == null || _toolPropertyTitle == null) return;

        _toolPropertyTitle.Text = ToolDisplayName(_canvas.ActiveTool);
        _toolPropertyPanel.Children.Clear();

        foreach (var prop in CurrentToolProperties())
        {
            if (!IsToolPropertyVisible(prop)) continue;
            _toolPropertyPanel.Children.Add(BuildDockerPropertyRow(prop));
        }

        if (_toolPropertyPanel.Children.Count == 0)
        {
            _toolPropertyPanel.Children.Add(new TextBlock
            {
                Text = "No pinned properties",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(TextMuted))
            });
        }
    }

    private Control BuildDockerPropertyRow(ToolPropertyDescriptor prop)
    {
        var row = new DockPanel { LastChildFill = true };
        var eye = SmBtn("◉", "Shown in tool property docker");
        eye.Width = 24;
        eye.Click += (_, _) =>
        {
            SetToolPropertyVisible(prop, false);
            RefreshToolProperties();
        };
        DockPanel.SetDock(eye, Dock.Right);
        row.Children.Add(eye);
        row.Children.Add(prop.BuildControl());
        return row;
    }

    private void OpenToolPropertyDetail()
    {
        if (_toolPropertyDetailWindow != null)
        {
            _toolPropertyDetailWindow.Activate();
            return;
        }

        var content = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
        var window = new Window
        {
            Title = "Sub Tool Detail",
            Width = 420,
            Height = 520,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Content = new ScrollViewer { Content = content }
        };

        void Rebuild()
        {
            content.Children.Clear();
            content.Children.Add(new TextBlock
            {
                Text = ToolDisplayName(_canvas.ActiveTool),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
                Margin = new Thickness(0, 0, 0, 4)
            });

            foreach (var prop in CurrentToolProperties())
            {
                var visible = IsToolPropertyVisible(prop);
                var eye = SmBtn(visible ? "◉" : "○", visible ? "Hide from tool property docker" : "Show in tool property docker");
                eye.Width = 28;
                eye.Click += (_, _) =>
                {
                    SetToolPropertyVisible(prop, !IsToolPropertyVisible(prop));
                    RefreshToolProperties();
                    Rebuild();
                };

                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(eye, Dock.Right);
                row.Children.Add(eye);
                row.Children.Add(prop.BuildControl());
                content.Children.Add(row);
            }
        }

        Rebuild();
        _toolPropertyDetailWindow = window;
        window.Closed += (_, _) => _toolPropertyDetailWindow = null;
        window.Show(this);
    }

    private static bool IsToolPropertyVisible(ToolPropertyDescriptor prop)
        => App.Config.ToolPropertyDockerVisibility.TryGetValue(prop.Id, out var visible) ? visible : prop.DefaultVisible;

    private static void SetToolPropertyVisible(ToolPropertyDescriptor prop, bool visible)
    {
        App.Config.ToolPropertyDockerVisibility[prop.Id] = visible;
        App.Config.Save();
    }
}
