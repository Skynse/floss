using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Processes;
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

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(_toolPropertyTitle);

        _toolPropertyPanel = new StackPanel { Spacing = 3 };

        var root = new StackPanel
        {
            Margin = new Thickness(10, 6, 10, 10),
            Children = { header, _toolPropertyPanel }
        };
        RefreshToolProperties();

        // Listen for visibility changes from brush editor eye toggles
        AppConfig.ToolPropertyVisibilityChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshToolProperties);

        return root;
    }

    // ── Tool property docker ─────────────────────────────────────────────────
    private sealed record ToolPropertyDescriptor(
        string Id,
        string Label,
        bool DefaultVisible,
        Func<Control> BuildControl,
        Action? RefreshValue = null);

    private string _lastToolPropertyKey = "";

    private IReadOnlyList<ToolPropertyDescriptor> CurrentToolProperties()
    {
        var preset = _activeToolGroup?.ActivePreset;
        if (preset == null) return [];

        var props = new List<ToolPropertyDescriptor>();

        // Input process properties
        if (preset.InputProcess == InputProcessType.BrushStroke ||
            preset.InputProcess == InputProcessType.Lasso)
        {
            props.Add(SliderProp("input.stabilization", "Stabilization", true,
                () => preset.Stabilization, v => preset.Stabilization = v, 0, 1, "%"));
        }

        // Output process properties
        switch (preset.OutputProcess)
        {
            case OutputProcessType.DirectDraw:
                props.AddRange([
                    SliderProp("brush.size", "Brush Size", true, _sizeSlider, "px"),
                    SliderProp("brush.opacity", "Opacity", true, _opacitySlider, "%"),
                    SliderProp("brush.flow", "Flow", false, _flowSlider, "%"),
                    SliderProp("brush.hardness", "Anti-aliasing", true, _hardnessSlider, "%"),
                    SliderProp("brush.spacing", "Spacing", false, _spacingSlider, "%"),
                    SliderProp("brush.smoothing", "Smoothing", true, _smoothingSlider, "%"),
                    SliderProp("brush.grain", "Grain", false, _grainSlider, "%"),
                    EnumProp("brush.blendMode", "Blend", false,
                        () => _canvas.Brush.BlendMode, v => UpdateCurrentBrush(p => p with { BlendMode = v }))
                ]);
                break;

            case OutputProcessType.ClosedAreaFill:
                props.Add(BoolProp("output.antialiasing", "Antialiasing", true,
                    () => preset.Antialiasing, v => preset.Antialiasing = v));
                break;

            case OutputProcessType.SelectionArea:
                props.AddRange([
                    EnumProp("select.mode", "Selection Mode", true,
                        () => preset.SelectMode, v => preset.SelectMode = v),
                    EnumProp("select.op", "Operation", true,
                        () => SelectOp.Replace, _ => { })  // TODO: add SelectOp to preset
                ]);
                break;

            case OutputProcessType.FloodFill:
                props.Add(SliderProp("fill.tolerance", "Tolerance", true,
                    () => preset.Tolerance, v => preset.Tolerance = v, 0, 1, "%"));
                break;

            case OutputProcessType.Gradient:
                props.Add(EnumProp("gradient.type", "Gradient Type", true,
                    () => preset.GradientType, v => preset.GradientType = v));
                break;

            case OutputProcessType.Stroke:
                props.AddRange([
                    SliderProp("stroke.width", "Stroke Width", true,
                        () => preset.PolylineStrokeWidth, v => preset.PolylineStrokeWidth = (float)v, 1, 200, "px"),
                    BoolProp("stroke.closePath", "Close Path", true,
                        () => preset.PolylineClosePath, v => preset.PolylineClosePath = v)
                ]);
                break;
        }

        // Legacy fallback for tools not yet migrated
        if (preset.InputProcess == default || preset.OutputProcess == default)
        {
            return CurrentLegacyToolProperties();
        }

        return props;
    }

    private static IReadOnlyList<ToolPropertyDescriptor> CurrentLegacyToolProperties() => [];

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
    {
        Slider? slider = null;
        return new(id, label, visible,
            () =>
            {
                slider = MkSlider(min, max, Math.Clamp(get(), min, max), label);
                slider.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Slider.ValueProperty) set(slider.Value);
                };
                return LabelSliderContent(label, slider, fmt);
            },
            () =>
            {
                if (slider != null)
                    slider.Value = Math.Clamp(get(), min, max);
            });
    }

    private static ToolPropertyDescriptor EnumProp<T>(string id, string label, bool visible, Func<T> get, Action<T> set)
        where T : struct, Enum
    {
        ComboBox? combo = null;
        return new(id, label, visible,
            () =>
            {
                combo = new ComboBox
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
            },
            () =>
            {
                if (combo != null)
                    combo.SelectedItem = get();
            });
    }

    private static ToolPropertyDescriptor BoolProp(string id, string label, bool visible, Func<bool> get, Action<bool> set)
    {
        CheckBox? check = null;
        return new(id, label, visible,
            () =>
            {
                check = new CheckBox
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
            },
            () =>
            {
                if (check != null)
                    check.IsChecked = get();
            });
    }

    private static Control LabelSliderContent(string label, Slider slider, string fmt)
    {
        var valueLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Width = 36,
            TextAlignment = TextAlignment.Right,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                valueLabel.Text = FormatSliderValue(slider.Value, fmt);
        };
        valueLabel.Text = FormatSliderValue(slider.Value, fmt);

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(valueLabel, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(valueLabel);
        row.Children.Add(slider);
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

    private bool _syncingToolPropertyPanel;

    private void RefreshToolProperties()
    {
        if (_toolPropertyPanel == null || _toolPropertyTitle == null) return;

        _toolPropertyTitle.Text = ToolDisplayName(_canvas.ActiveTool);

        var props = CurrentToolProperties().Where(p => IsToolPropertyVisible(p)).ToList();

        // Build a key from current property IDs to detect structural changes
        var propKey = string.Join("|", props.Select(p => p.Id));
        bool needsRebuild = propKey != _lastToolPropertyKey;
        _lastToolPropertyKey = propKey;

        if (needsRebuild)
        {
            _toolPropertyPanel.Children.Clear();
            foreach (var prop in props)
            {
                var ctrl = BuildDockerPropertyRow(prop);
                _toolPropertyPanel.Children.Add(ctrl);
            }
        }
        else
        {
            _syncingToolPropertyPanel = true;
            try
            {
                foreach (var prop in props)
                    prop.RefreshValue?.Invoke();
            }
            finally
            {
                _syncingToolPropertyPanel = false;
            }
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

    private static bool IsToolPropertyVisible(ToolPropertyDescriptor prop)
        => App.Config.ToolPropertyDockerVisibility.TryGetValue(prop.Id, out var visible) ? visible : prop.DefaultVisible;

    private static void SetToolPropertyVisible(ToolPropertyDescriptor prop, bool visible)
    {
        App.Config.ToolPropertyDockerVisibility[prop.Id] = visible;
        App.Config.Save();
    }
}
