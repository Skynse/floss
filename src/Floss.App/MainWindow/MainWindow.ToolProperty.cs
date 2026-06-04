using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Processes;
using Floss.App.Tools;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{
    private void OnToolPropertyVisibilityChanged()
        => Dispatcher.UIThread.Post(RefreshToolProperties);

    /// <summary>Single tool-properties panel shared by the docked BRUSH column and popup.</summary>
    private Control GetToolPropertiesContent()
    {
        if (_toolPropertiesContent != null)
            return _toolPropertiesContent;

        _toolPropertiesContent = BuildToolPropertySection();
        return _toolPropertiesContent;
    }

    private StackPanel BuildToolPropertySection()
    {
        _toolPropertyTitle = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(_toolPropertyTitle);

        _toolPropertyPanel = new StackPanel { Spacing = 2 };

        var root = new StackPanel
        {
            Margin = new Thickness(8, 4, 8, 6),
            Children = { header, _toolPropertyPanel }
        };
        _lastToolPropertyKey = "";
        _builtToolPropertyDescriptors = null;
        RefreshToolProperties();

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
        var output = preset.OutputProcess;

        // ── Common paint properties (opacity, blend mode) for all paint tools ──
        bool isPaint = output is OutputProcessType.DirectDraw
            or OutputProcessType.ClosedAreaFill
            or OutputProcessType.FloodFill
            or OutputProcessType.Gradient
            or OutputProcessType.Stroke;

        if (isPaint || output == OutputProcessType.DirectDraw)
        {
            // Use brush.opacity for DirectDraw so the ID matches ToolPropertiesWindow's eye button.
            // Other paint tools use paint.opacity (no ToolPropertiesWindow eye button).
            var opacityId = output == OutputProcessType.DirectDraw ? "brush.opacity" : "paint.opacity";
            props.Add(SliderProp(opacityId, "Opacity", true, _opacitySlider, "%"));
            props.Add(EnumProp("paint.blendMode", "Blend", false,
                () => _canvas.Brush.BlendMode, v => UpdateCurrentBrush(p => p with { BlendMode = v })));
        }

        // ── Brush-specific (DirectDraw) ──
        if (output == OutputProcessType.DirectDraw)
        {
            props.AddRange([
                SliderProp("brush.size", "Brush Size", true, _sizeSlider, "px"),
                SliderProp("brush.maxSizePercent", "Max Size", false, _maxSizePercentSlider, "%"),
                SliderProp("brush.flow", "Flow", false, _flowSlider, "%"),
                SliderProp("brush.hardness", "Hardness", true, _hardnessSlider, "%"),
                SliderProp("brush.spacing", "Spacing", false, _spacingSlider, "%"),
                BoolProp("brush.autoSpacing", "Auto", false,
                    () => _activePreset?.AutoSpacingActive ?? true, v => UpdateCurrentBrush(p => p with { AutoSpacingActive = v })),
                SliderProp("brush.smoothing", "Stabilization", true, _smoothingSlider, "%"),
                BoolProp("brush.speedAdaptive", "Adjust by speed", false,
                    () => _activePreset?.SpeedAdaptiveStabilizer ?? true,
                    v => UpdateCurrentBrush(p => p with { SpeedAdaptiveStabilizer = v })),
                SliderProp("brush.grain", "Grain", false, _grainSlider, "%"),
                EnumProp("brush.quality", "Quality", false,
                    () => _activePreset?.Quality ?? BrushQuality.High, v => UpdateCurrentBrush(p => p with { Quality = v })),
                BoolProp("brush.colorMix", "Color Mix", false,
                    () => _activePreset?.ColorMix ?? false, v => UpdateCurrentBrush(p => p with { ColorMix = v })),
                EnumProp("brush.smudgeMode", "Mix Mode", false,
                    () => _activePreset?.SmudgeMode ?? SmudgeMode.Blend, v => UpdateCurrentBrush(p => p with { SmudgeMode = v })),
                SliderProp("brush.amountOfPaint", "Amount", false,
                    () => _activePreset?.AmountOfPaint ?? 1, v => UpdateCurrentBrush(p => p with { AmountOfPaint = v }), 0, 1, "%"),
                SliderProp("brush.densityOfPaint", "Density", false,
                    () => _activePreset?.DensityOfPaint ?? 1, v => UpdateCurrentBrush(p => p with { DensityOfPaint = v }), 0, 1, "%"),
                SliderProp("brush.colorStretch", "Stretch", false,
                    () => _activePreset?.ColorStretch ?? 0.5, v => UpdateCurrentBrush(p => p with { ColorStretch = v }), 0, 1, "%"),
                SliderProp("brush.blurAmount", "Blur Mix", false,
                    () => _activePreset?.BlurAmount ?? 0, v => UpdateCurrentBrush(p => p with { BlurAmount = v }), 0, 1, "%"),
                SliderProp("brush.angle", "Angle", false,
                    () => _activePreset?.Angle ?? 0, v => UpdateCurrentBrush(p => p with { Angle = v }), 0, 360, "°"),
                SliderProp("brush.tipDensity", "Tip Density", false,
                    () => _activePreset?.TipDensity ?? 1, v => UpdateCurrentBrush(p => p with { TipDensity = v }), 0, 1, "%"),
                SliderProp("brush.tipThickness", "Tip Thickness", false,
                    () => _activePreset?.TipThickness ?? 1, v => UpdateCurrentBrush(p => p with { TipThickness = v }), 0.01, 1, "%"),
                EnumProp("brush.tipDirection", "Tip Direction", false,
                    () => _activePreset?.TipDirection ?? BrushTipDirection.Horizontal, v => UpdateCurrentBrush(p => p with { TipDirection = v })),
            ]);
        }

        // ── Input process properties ──
        if (preset.InputProcess.IsBrushFamily() ||
            preset.InputProcess == InputProcessType.Lasso)
        {
            if (output != OutputProcessType.DirectDraw) // brush already shows smoothing above
                props.Add(SliderProp("input.stabilization", "Stabilization", true,
                    () => preset.Stabilization, v => UpdateActiveToolPreset(p => p.Stabilization = v), 0, 1, "%"));
        }

        if (preset.InputProcess == InputProcessType.Lasso ||
            output is OutputProcessType.ClosedAreaFill or OutputProcessType.SelectionArea)
        {
            props.Add(EnumProp("input.antialiasing", "Antialiasing", true,
                () => preset.AntialiasingQuality, v => UpdateActiveToolPreset(p => p.AntialiasingQuality = v)));
        }

        // ── Output-specific properties ──
        switch (output)
        {
            case OutputProcessType.FloodFill:
                props.AddRange([
                    SliderProp("fill.tolerance", "Tolerance", true,
                        () => preset.Tolerance, v => UpdateActiveToolPreset(p => p.Tolerance = v), 0, 1, "%"),
                    EnumProp("fill.reference", "Reference", true,
                        () => preset.FillReference, v => UpdateActiveToolPreset(p => p.FillReference = v)),
                    SliderProp("fill.areaScaling", "Area Scaling", false,
                        () => preset.AreaScaling, v => UpdateActiveToolPreset(p => p.AreaScaling = v), -20, 20, "px"),
                    BoolProp("fill.contiguous", "Contiguous", false,
                        () => preset.ContiguousFill, v => UpdateActiveToolPreset(p => p.ContiguousFill = v))
                ]);
                break;

            case OutputProcessType.ClosedAreaFill:
                props.AddRange([
                    SliderProp("fill.tolerance", "Tolerance", false,
                        () => preset.Tolerance, v => UpdateActiveToolPreset(p => p.Tolerance = v), 0, 1, "%"),
                    SliderProp("fill.areaScaling", "Area Scaling", false,
                        () => preset.AreaScaling, v => UpdateActiveToolPreset(p => p.AreaScaling = v), -20, 20, "px")
                ]);
                break;

            case OutputProcessType.SelectionArea:
                props.AddRange([
                    EnumProp("select.mode", "Selection Mode", true,
                        () => preset.SelectMode, v => UpdateActiveToolPreset(p =>
                        {
                            p.SelectMode = v;
                            p.SyncSelectInputFromMode();
                            InvalidatePresetToolCache(p.Id);
                        })),
                    EnumProp("select.op", "Operation", true,
                        () => preset.SelectOp, v => UpdateActiveToolPreset(p => p.SelectOp = v))
                ]);
                break;

            case OutputProcessType.Gradient:
                props.Add(EnumProp("gradient.type", "Gradient Type", true,
                    () => preset.GradientType, v => UpdateActiveToolPreset(p => p.GradientType = v)));
                break;

            case OutputProcessType.Stroke:
                props.AddRange([
                    SliderProp("stroke.width", "Stroke Width", true,
                        () => preset.PolylineStrokeWidth,
                        v => UpdateActiveToolPreset(p => p.PolylineStrokeWidth = (float)v), 1, 200, "px"),
                    BoolProp("stroke.closePath", "Close Path", true,
                        () => preset.PolylineClosePath, v => UpdateActiveToolPreset(p => p.PolylineClosePath = v))
                ]);
                break;

            case OutputProcessType.MagicWand:
                props.AddRange([
                    SliderProp("wand.tolerance", "Tolerance", true,
                        () => preset.Tolerance, v => UpdateActiveToolPreset(p => p.Tolerance = v), 0, 1, "%"),
                    EnumProp("wand.op", "Operation", true,
                        () => preset.SelectOp, v => UpdateActiveToolPreset(p => p.SelectOp = v)),
                    BoolProp("wand.contiguous", "Contiguous", false,
                        () => preset.ContiguousFill, v => UpdateActiveToolPreset(p => p.ContiguousFill = v))
                ]);
                break;

            case OutputProcessType.Liquify:
                props.AddRange([
                    SliderProp("liquify.size", "Size", true,
                        () => preset.LiquifySize, v => UpdateActiveToolPreset(p => p.LiquifySize = v), 10, 500, "px"),
                    SliderProp("liquify.strength", "Strength", true,
                        () => preset.LiquifyStrength, v => UpdateActiveToolPreset(p => p.LiquifyStrength = v), 0, 1, "%"),
                    EnumProp("liquify.mode", "Mode", true,
                        () => preset.LiquifyMode, v => UpdateActiveToolPreset(p => p.LiquifyMode = v)),
                ]);
                break;

            case OutputProcessType.Eyedropper:
                props.AddRange([
                    EnumProp("eyedropper.sampleMode", "Sample", true,
                        () => preset.EyedropperSampleMode, v => UpdateActiveToolPreset(p => p.EyedropperSampleMode = v)),
                    BoolProp("eyedropper.excludeLocked", "Exclude Locked", false,
                        () => preset.EyedropperExcludeLockedLayers, v => UpdateActiveToolPreset(p => p.EyedropperExcludeLockedLayers = v)),
                    BoolProp("eyedropper.excludeReference", "Exclude Reference", false,
                        () => preset.EyedropperExcludeReferenceLayers, v => UpdateActiveToolPreset(p => p.EyedropperExcludeReferenceLayers = v))
                ]);
                break;

            case OutputProcessType.MoveLayer:
                // Move layer has no adjustable properties.
                break;
        }

        return props;
    }

    private static IReadOnlyList<ToolPropertyDescriptor> CurrentLegacyToolProperties() => [];

    private void UpdateActiveToolPreset(Action<ToolPreset> update)
    {
        var preset = _activeToolGroup?.ActivePreset;
        if (preset == null) return;

        update(preset);
        if (preset.OutputProcess is not (OutputProcessType.DirectDraw or OutputProcessType.Liquify))
            _canvas.SetActiveTool(ToolForPreset(preset), preset);
        else
            _canvas.InvalidateVisual();
        App.ToolGroups.Save();
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

    private ToolPropertyDescriptor EnumProp<T>(string id, string label, bool visible, Func<T> get, Action<T> set)
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
                    FontSize = 10,
                    MinHeight = 22,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (_syncingToolPropertyPanel) return;
                    if (combo.SelectedItem is T value)
                        set(value);
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
                    FontSize = 10,
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
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Width = 32,
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
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 66,
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
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 80,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(control);
        return row;
    }

    private bool _syncingToolPropertyPanel;
    private List<ToolPropertyDescriptor>? _builtToolPropertyDescriptors;

    private void RefreshToolProperties()
    {
        if (_toolPropertyPanel == null || _toolPropertyTitle == null) return;

        if (_canvas.IsTransformActive)
        {
            _toolPropertyTitle.Text = "Transform";
            var transformProps = CurrentTransformProperties();
            var transformKey = "transform|" + string.Join("|", transformProps.Select(p => p.Id));
            bool transformNeedsRebuild = transformKey != _lastToolPropertyKey;
            _lastToolPropertyKey = transformKey;

            if (transformNeedsRebuild)
            {
                _builtToolPropertyDescriptors = transformProps.ToList();
                _toolPropertyPanel.Children.Clear();
                _toolPropertyPanel.Children.Add(BuildTransformActionBar());
                foreach (var prop in transformProps)
                {
                    var row = new DockPanel { LastChildFill = true };
                    row.Children.Add(prop.BuildControl());
                    _toolPropertyPanel.Children.Add(row);
                }
            }
            else
            {
                _syncingToolPropertyPanel = true;
                try
                {
                    foreach (var prop in _builtToolPropertyDescriptors ?? transformProps)
                        prop.RefreshValue?.Invoke();
                }
                finally
                {
                    _syncingToolPropertyPanel = false;
                }
            }
            return;
        }

        _toolPropertyTitle.Text = "Brush Settings";

        var props = CurrentToolProperties().Where(p => IsToolPropertyVisible(p)).ToList();

        // Build a key from current property IDs to detect structural changes
        var propKey = string.Join("|", props.Select(p => p.Id));
        bool needsRebuild = propKey != _lastToolPropertyKey;
        _lastToolPropertyKey = propKey;

        if (needsRebuild)
        {
            _builtToolPropertyDescriptors = props;
            _toolPropertyPanel.Children.Clear();
            foreach (var prop in props)
            {
                var ctrl = BuildDockerPropertyRow(prop);
                _toolPropertyPanel.Children.Add(ctrl);
            }
        }
        else
        {
            // Use the cached descriptors — they hold the slider instances created during BuildControl().
            // Fresh descriptors from CurrentToolProperties() have slider == null so RefreshValue is a no-op.
            _syncingToolPropertyPanel = true;
            try
            {
                foreach (var prop in _builtToolPropertyDescriptors ?? props)
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
        var eye = SmIconBtn(Icons.Eye, "Shown in tool property docker");
        eye.Width = 22;
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
        => App.Config.IsToolPropertyDockerVisible(prop.Id);

    private static void SetToolPropertyVisible(ToolPropertyDescriptor prop, bool visible)
        => App.Config.SetToolPropertyDockerVisible(prop.Id, visible);
}
