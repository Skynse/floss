using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Config;
using Floss.App.Controls;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Floss.App.Processes;
using Floss.App.Tools;
using Floss.App.Windows;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed partial class ToolPropertiesDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    private Control? _toolPropertiesContent;
    private TextBlock _toolPropertyTitle = null!;
    private StackPanel _toolPropertyPanel = null!;
    private DrawingCanvas? _wiredAssistantsCanvas;

    public ToolPropertiesDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);
        Content = GetToolPropertiesContent();
        session.ActiveCanvasChanged += OnActiveCanvasChanged;
        WireAssistants(session.ActiveCanvas);
    }

    private void OnActiveCanvasChanged() => WireAssistants(_ps.Canvas);

    private void WireAssistants(DrawingCanvas canvas)
    {
        if (_wiredAssistantsCanvas != null)
            _wiredAssistantsCanvas.Document.Assistants.Changed -= OnAssistantsChanged;
        _wiredAssistantsCanvas = canvas;
        canvas.Document.Assistants.Changed += OnAssistantsChanged;
    }

    private void OnAssistantsChanged(object? sender, EventArgs e)
    {
        _lastToolPropertyKey = "";
        Dispatcher.UIThread.Post(Refresh);
    }
    public void Refresh()=>RefreshToolProperties();
    public void OnVisibilityChanged()=>OnToolPropertyVisibilityChanged();
    public Control GetToolPropertiesContent(){if(_toolPropertiesContent!=null)return _toolPropertiesContent;_toolPropertiesContent=BuildToolPropertySection();return _toolPropertiesContent;}
    private void OnToolPropertyVisibilityChanged()
        => Dispatcher.UIThread.Post(RefreshToolProperties);

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

        return root;
    }

    // ── Tool property docker ─────────────────────────────────────────────────
    private sealed record ToolPropertyDescriptor(
        string Id,
        string Label,
        bool DefaultVisible,
        Func<Control> BuildControl,
        Action? RefreshValue = null,
        Action? OpenDynamics = null);

    private DynamicsPopupWindow? _dockerSizeDynPopup;
    private DynamicsPopupWindow? _dockerOpacDynPopup;
    private DynamicsPopupWindow? _dockerFlowDynPopup;
    private DynamicsPopupWindow? _dockerHardnessDynPopup;
    private DynamicsPopupWindow? _dockerSpacingDynPopup;
    private AngleDynamicsPopupWindow? _dockerAngleDynPopup;
    private DynamicsPopupWindow? _dockerTipDensityDynPopup;
    private DynamicsPopupWindow? _dockerTipThicknessDynPopup;

    private string _lastToolPropertyKey = "";

    private IReadOnlyList<ToolPropertyDescriptor> CurrentToolProperties()
    {
        var selected = SelectedAssistant();
        if (selected != null)
            return AssistantContextProperties(selected);

        var preset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (preset == null) return [];

        var props = new List<ToolPropertyDescriptor>();
        var kind = preset.Kind;

        // ── Common paint properties (opacity, blend mode) for all paint tools ──
        bool isPaint = kind.IsPaintTool();

        if (isPaint)
        {
            var opacityId = kind.IsBrushFamily() ? "brush.opacity" : "paint.opacity";
            props.Add(SliderProp(opacityId, "Opacity", true, _ps.BrushPanel.OpacitySlider, "%", OpenDockerOpacityDynamics));
            props.Add(EnumProp("paint.blendMode", "Blend", false,
                () => _ps.Canvas.Brush.BlendMode, v => UpdateCurrentBrushInternal(p => p with { BlendMode = v })));
        }

        // ── Brush-family tools ──
        if (kind.IsBrushFamily())
        {
            props.AddRange([
                SliderProp("brush.size", "Brush Size", true, _ps.BrushPanel.SizeSlider, "px", OpenDockerSizeDynamics),
                SliderProp("brush.maxSizePercent", "Max Size", false, _ps.BrushPanel.MaxSizePercentSlider, "%"),
                SliderProp("brush.flow", "Flow", false, _ps.BrushPanel.FlowSlider, "%", OpenDockerFlowDynamics),
                SliderProp("brush.hardness", "Hardness", true, _ps.BrushPanel.HardnessSlider, "%", OpenDockerHardnessDynamics),
                SliderProp("brush.spacing", "Spacing", false, _ps.BrushPanel.SpacingSlider, "%", OpenDockerSpacingDynamics),
                BoolProp("brush.autoSpacing", "Auto", false,
                    () => _ps.Brush.ActivePreset?.AutoSpacingActive ?? true, v => UpdateCurrentBrushInternal(p => p with { AutoSpacingActive = v })),
                SliderProp("brush.smoothing", "Stabilization", true, _ps.BrushPanel.SmoothingSlider, "%"),
                BoolProp("brush.speedAdaptive", "Adjust by speed", false,
                    () => _ps.Brush.ActivePreset?.SpeedAdaptiveStabilizer ?? true,
                    v => UpdateCurrentBrushInternal(p => p with { SpeedAdaptiveStabilizer = v })),
                SliderProp("brush.grain", "Grain", false, _ps.BrushPanel.GrainSlider, "%"),
                BrushQualityProp("brush.quality", "Antialiasing", false,
                    () => _ps.Brush.ActivePreset?.Quality ?? BrushQuality.High,
                    v => UpdateCurrentBrushInternal(p => p with { Quality = v })),
                BoolProp("brush.colorMix", "Color Mix", false,
                    () => _ps.Brush.ActivePreset?.ColorMix ?? false, v => UpdateCurrentBrushInternal(p => p with { ColorMix = v })),
                EnumProp("brush.smudgeMode", "Mix Mode", false,
                    () => _ps.Brush.ActivePreset?.SmudgeMode ?? SmudgeMode.Blend, v => UpdateCurrentBrushInternal(p => p with { SmudgeMode = v })),
                SliderProp("brush.amountOfPaint", "Amount", false,
                    () => _ps.Brush.ActivePreset?.AmountOfPaint ?? 1, v => UpdateCurrentBrushInternal(p => p with { AmountOfPaint = v }), 0, 1, "%"),
                SliderProp("brush.densityOfPaint", "Density", false,
                    () => _ps.Brush.ActivePreset?.DensityOfPaint ?? 1, v => UpdateCurrentBrushInternal(p => p with { DensityOfPaint = v }), 0, 1, "%"),
                SliderProp("brush.colorStretch", "Stretch", false,
                    () => _ps.Brush.ActivePreset?.ColorStretch ?? 0.5, v => UpdateCurrentBrushInternal(p => p with { ColorStretch = v }), 0, 1, "%"),
                SliderProp("brush.blurAmount", "Blur Mix", false,
                    () => _ps.Brush.ActivePreset?.BlurAmount ?? 0, v => UpdateCurrentBrushInternal(p => p with { BlurAmount = v }), 0, 1, "%"),
                SliderProp("brush.angle", "Angle", false,
                    () => _ps.Brush.ActivePreset?.Angle ?? 0, v => UpdateCurrentBrushInternal(p => p with { Angle = v }), 0, 360, "°",
                    OpenDockerAngleDynamics),
                SliderProp("brush.tipDensity", "Tip Density", false,
                    () => _ps.Brush.ActivePreset?.TipDensity ?? 1, v => UpdateCurrentBrushInternal(p => p with { TipDensity = v }), 0, 1, "%",
                    OpenDockerTipDensityDynamics),
                SliderProp("brush.tipThickness", "Tip Thickness", false,
                    () => _ps.Brush.ActivePreset?.TipThickness ?? 1, v => UpdateCurrentBrushInternal(p => p with { TipThickness = v }), 0.01, 1, "%",
                    OpenDockerTipThicknessDynamics),
                EnumProp("brush.tipDirection", "Tip Direction", false,
                    () => _ps.Brush.ActivePreset?.TipDirection ?? BrushTipDirection.Horizontal, v => UpdateCurrentBrushInternal(p => p with { TipDirection = v })),
            ]);
        }

        if (preset.Kind == ToolKind.Assistant)
        {
            var assistantType = preset.AssistantType ?? PaintingAssistant.RulerType;
            var assistantSelection = SelectedAssistant();

            props.Add(BoolProp("assistant.createAtEditingLayer", "Create at editing layer", true,
                () => preset.AssistantCreateAtEditingLayer,
                v => UpdateActiveToolPreset(p => p.AssistantCreateAtEditingLayer = v)));
            props.Add(BoolProp("assistant.snap", "Snap", true,
                () => assistantSelection?.SnapEnabled ?? preset.AssistantSnapEnabled,
                v => CommitAssistantValue(preset, assistantSelection, a => a.SnapEnabled = v, p => p.AssistantSnapEnabled = v)));

            if (assistantType is PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType)
            {
                props.Add(PerspectiveModeProp("assistant.perspectiveMode", "Perspective", true,
                    () => assistantSelection?.PerspectiveMode ?? preset.AssistantPerspectiveMode,
                    v => CommitAssistantPerspectiveMode(preset, assistantSelection, v)));
                props.Add(BoolProp("assistant.fisheye", "Fisheye", true,
                    () => assistantSelection?.FisheyeEnabled ?? preset.AssistantFisheyeEnabled,
                    v => CommitAssistantValue(preset, assistantSelection,
                        a => a.FisheyeEnabled = v,
                        p => p.AssistantFisheyeEnabled = v)));
                if (assistantSelection?.FisheyeEnabled == true || preset.AssistantFisheyeEnabled)
                {
                    props.Add(SliderProp("assistant.fov", "FOV", true,
                        () => assistantSelection?.FovDegrees ?? preset.AssistantFovDegrees,
                        v => CommitAssistantValue(preset, assistantSelection, a => a.FovDegrees = v, p => p.AssistantFovDegrees = v),
                        10, 360, "°"));
                }

                props.Add(SliderProp("assistant.grid", "Grid", true,
                    () => assistantSelection?.GridSubdivisions ?? preset.AssistantGridSubdivisions,
                    v => CommitAssistantValue(preset, assistantSelection,
                        a => a.GridSubdivisions = (int)Math.Round(v),
                        p => p.AssistantGridSubdivisions = (int)Math.Round(v)),
                    2, 12, ""));
            }
        }

        // ── Input process properties ──
        if (preset.Kind.IsBrushFamily() ||
            preset.Kind is ToolKind.Select or ToolKind.LassoFill)
        {
            if (!kind.IsBrushFamily()) // brush family already shows smoothing above
                props.Add(SliderProp("input.stabilization", "Stabilization", true,
                    () => preset.Stabilization, v => UpdateActiveToolPreset(p => p.Stabilization = v), 0, 1, "%"));
        }

        if (kind is ToolKind.Select or ToolKind.LassoFill)
        {
            props.Add(EnumProp("input.antialiasing", "Antialiasing", true,
                () => preset.AntialiasingQuality, v => UpdateActiveToolPreset(p => p.AntialiasingQuality = v)));
        }

        // ── Output-specific properties ──
        switch (kind)
        {
            case ToolKind.Fill:
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

            case ToolKind.LassoFill:
                props.AddRange([
                    SliderProp("fill.tolerance", "Tolerance", false,
                        () => preset.Tolerance, v => UpdateActiveToolPreset(p => p.Tolerance = v), 0, 1, "%"),
                    SliderProp("fill.areaScaling", "Area Scaling", false,
                        () => preset.AreaScaling, v => UpdateActiveToolPreset(p => p.AreaScaling = v), -20, 20, "px")
                ]);
                break;

            case ToolKind.Select:
                props.AddRange([
                    EnumProp("select.mode", "Selection Mode", true,
                        () => preset.SelectMode, v => UpdateActiveToolPreset(p =>
                        {
                            p.SelectMode = v;
                            p.SyncSelectModeFromKind();
                            _ps.Tools.InvalidatePresetToolCache(p.Id);
                        })),
                    EnumProp("select.op", "Operation", true,
                        () => preset.SelectOp, v => UpdateActiveToolPreset(p => p.SelectOp = v))
                ]);
                break;

            case ToolKind.Gradient:
                props.Add(EnumProp("gradient.type", "Gradient Type", true,
                    () => preset.GradientType, v => UpdateActiveToolPreset(p => p.GradientType = v)));
                break;

            case ToolKind.Shape:
                props.AddRange([
                    SliderProp("stroke.width", "Stroke Width", true,
                        () => preset.PolylineStrokeWidth,
                        v => UpdateActiveToolPreset(p => p.PolylineStrokeWidth = (float)v), 1, 200, "px"),
                    BoolProp("stroke.closePath", "Close Path", true,
                        () => preset.PolylineClosePath, v => UpdateActiveToolPreset(p => p.PolylineClosePath = v))
                ]);
                break;

            case ToolKind.MagicWand:
                props.AddRange([
                    SliderProp("wand.tolerance", "Tolerance", true,
                        () => preset.Tolerance, v => UpdateActiveToolPreset(p => p.Tolerance = v), 0, 1, "%"),
                    EnumProp("wand.reference", "Reference", true,
                        () => preset.FillReference, v => UpdateActiveToolPreset(p => p.FillReference = v)),
                    EnumProp("wand.op", "Operation", true,
                        () => preset.SelectOp, v => UpdateActiveToolPreset(p => p.SelectOp = v)),
                    SliderProp("wand.areaScaling", "Area Scaling", false,
                        () => preset.AreaScaling, v => UpdateActiveToolPreset(p => p.AreaScaling = v), -20, 20, "px"),
                    BoolProp("wand.contiguous", "Contiguous", false,
                        () => preset.ContiguousFill, v => UpdateActiveToolPreset(p => p.ContiguousFill = v))
                ]);
                break;

            case ToolKind.Liquify:
                props.AddRange([
                    SliderProp("liquify.size", "Size", true,
                        () => preset.LiquifySize, v => UpdateActiveToolPreset(p => p.LiquifySize = v), 10, 500, "px"),
                    SliderProp("liquify.strength", "Strength", true,
                        () => preset.LiquifyStrength, v => UpdateActiveToolPreset(p => p.LiquifyStrength = v), 0, 1, "%"),
                    EnumProp("liquify.mode", "Mode", true,
                        () => preset.LiquifyMode, v => UpdateActiveToolPreset(p => p.LiquifyMode = v)),
                ]);
                break;

            case ToolKind.Eyedropper:
                props.AddRange([
                    EnumProp("eyedropper.sampleMode", "Sample", true,
                        () => preset.EyedropperSampleMode, v => UpdateActiveToolPreset(p => p.EyedropperSampleMode = v)),
                    BoolProp("eyedropper.excludeLocked", "Exclude Locked", false,
                        () => preset.EyedropperExcludeLockedLayers, v => UpdateActiveToolPreset(p => p.EyedropperExcludeLockedLayers = v)),
                    BoolProp("eyedropper.excludeReference", "Exclude Reference", false,
                        () => preset.EyedropperExcludeReferenceLayers, v => UpdateActiveToolPreset(p => p.EyedropperExcludeReferenceLayers = v))
                ]);
                break;

            case ToolKind.MoveLayer:
                // Move layer has no adjustable properties.
                break;
        }

        return props;
    }

    private static IReadOnlyList<ToolPropertyDescriptor> CurrentLegacyToolProperties() => [];

    private void UpdateActiveToolPreset(Action<ToolPreset> update)
    {
        var preset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (preset == null) return;

        update(preset);
        if (preset.Kind == ToolKind.Assistant)
            _ps.Tools.InvalidatePresetToolCache(preset.Id);

        if (!preset.Kind.IsBrushFamily() && preset.Kind != ToolKind.Liquify)
            _ps.Canvas.SetActiveTool(_ps.Tools.ToolForPreset(preset), preset);
        else
            _ps.Canvas.InvalidateVisual();
        Floss.App.App.ToolGroups.Save();
        Refresh();
    }

    private ToolPropertyDescriptor SliderProp(string id, string label, bool visible, ScrubSlider source, string fmt, Action? openDynamics = null)
        => SliderProp(id, label, visible, () => source.Value, v => source.Value = v, source.Minimum, source.Maximum, fmt, openDynamics);

    private ToolPropertyDescriptor SliderProp(
        string id,
        string label,
        bool visible,
        Func<double> get,
        Action<double> set,
        double min,
        double max,
        string fmt,
        Action? openDynamics = null)
    {
        ScrubSlider? slider = null;
        return new(id, label, visible,
            () =>
            {
                slider = DockPanelUiHelpers.MkSlider(min, max, Math.Clamp(get(), min, max), label);
                slider.PropertyChanged += (_, e) =>
                {
                    if (e.Property == RangeBase.ValueProperty && !slider.IsScrubbing)
                        set(slider.Value);
                };
                slider.ScrubCompleted += (_, v) => set(v);
                return LabelSliderContent(label, slider, fmt);
            },
            () =>
            {
                if (slider != null)
                    slider.Value = Math.Clamp(get(), min, max);
            },
            openDynamics);
    }

    private ToolPropertyDescriptor BrushQualityProp(
        string id,
        string label,
        bool visible,
        Func<BrushQuality> get,
        Action<BrushQuality> set)
    {
        ComboBox? combo = null;
        return new(id, label, visible,
            () =>
            {
                combo = new ComboBox
                {
                    ItemsSource = BrushQualityPolicy.AllLevels,
                    SelectedItem = get(),
                    FontSize = 10,
                    MinHeight = 22,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                combo.ItemTemplate = new FuncDataTemplate<BrushQuality>((quality, _) =>
                    new TextBlock { Text = BrushQualityPolicy.DisplayName(quality), FontSize = 10 });
                combo.SelectionChanged += (_, _) =>
                {
                    if (_ps.Sync.SyncingToolPropertyPanel) return;
                    if (combo.SelectedItem is BrushQuality value)
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
                    if (_ps.Sync.SyncingToolPropertyPanel) return;
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

    private static Control LabelSliderContent(string label, ScrubSlider slider, string fmt)
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
            if (e.Property == RangeBase.ValueProperty)
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
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(valueLabel, Avalonia.Controls.Dock.Right);
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
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(control);
        return row;
    }

    private List<ToolPropertyDescriptor>? _builtToolPropertyDescriptors;

    private void RefreshToolProperties()
    {
        if (_toolPropertyPanel == null || _toolPropertyTitle == null) return;

        if (_ps.Canvas.IsTransformActive)
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
                _ps.Sync.SyncingToolPropertyPanel = true;
                try
                {
                    foreach (var prop in _builtToolPropertyDescriptors ?? transformProps)
                        prop.RefreshValue?.Invoke();
                }
                finally
                {
                    _ps.Sync.SyncingToolPropertyPanel = false;
                }
            }
            return;
        }

        var activePreset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        var selectedAssistant = SelectedAssistant();
        _toolPropertyTitle.Text = selectedAssistant != null
            ? RulerDisplayNames.For(selectedAssistant)
            : activePreset?.Kind == ToolKind.Assistant ? "Assistant"
            : activePreset?.Kind.IsBrushFamily() == true ? "Brush Settings"
            : "Tool Properties";

        var props = CurrentToolProperties().Where(p => IsToolPropertyVisible(p)).ToList();

        var selectionSuffix = "";
        if (selectedAssistant != null)
        {
            var fisheyeOn = selectedAssistant.FisheyeEnabled;
            selectionSuffix = "|ctx:" + (_ps.Canvas.Document.Assistants.SelectedId ?? "")
                + "|fe:" + (fisheyeOn ? "1" : "0");
        }
        else if (activePreset?.Kind == ToolKind.Assistant)
        {
            var fisheyeOn = activePreset.AssistantFisheyeEnabled;
            selectionSuffix = "|sel:" + (_ps.Canvas.Document.Assistants.SelectedId ?? "")
                + "|fe:" + (fisheyeOn ? "1" : "0");
        }
        var propKey = selectionSuffix + string.Join("|", props.Select(p => p.Id));
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
            _ps.Sync.SyncingToolPropertyPanel = true;
            try
            {
                foreach (var prop in _builtToolPropertyDescriptors ?? props)
                    prop.RefreshValue?.Invoke();
            }
            finally
            {
                _ps.Sync.SyncingToolPropertyPanel = false;
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
        if (prop.OpenDynamics != null)
        {
            var tune = DockPanelUiHelpers.SmIconBtn(Icons.TuneVertical, "Edit dynamics curve");
            tune.Width = 22;
            tune.Click += (_, _) => prop.OpenDynamics();
            DockPanel.SetDock(tune, Avalonia.Controls.Dock.Right);
            row.Children.Add(tune);
        }
        var eye = DockPanelUiHelpers.SmIconBtn(Icons.Eye, "Shown in tool property docker");
        eye.Width = 22;
        eye.Click += (_, _) =>
        {
            SetToolPropertyVisible(prop, false);
            _ps.Brush.RefreshToolProperties();
        };
        DockPanel.SetDock(eye, Avalonia.Controls.Dock.Right);
        row.Children.Add(eye);
        row.Children.Add(prop.BuildControl());
        return row;
    }

    private bool IsToolPropertyVisible(ToolPropertyDescriptor prop)
        => _ps.Config.IsToolPropertyDockerVisible(prop.Id);

    private void SetToolPropertyVisible(ToolPropertyDescriptor prop, bool visible)
        => _ps.Config.SetToolPropertyDockerVisible(prop.Id, visible);

    private void PositionDockerDynamicsPopup(Window popup)
    {
        var owner = _ps.Shell.Owner;
        if (owner.Position.X + owner.Width + 280 < owner.Screens.Primary?.WorkingArea.Width)
            popup.Position = new PixelPoint(owner.Position.X + (int)owner.Width + 4, owner.Position.Y);
        else
            popup.Position = new PixelPoint(Math.Max(0, owner.Position.X - 280), owner.Position.Y);
    }

    private void OpenDockerSizeDynamics() => ShowDockerDynamicsPopup(
        _dockerSizeDynPopup, v => _dockerSizeDynPopup = v, "Brush Size",
        () => (_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).SizeDynamics,
        dyn => UpdateCurrentBrushInternal(p => p with { SizeDynamics = dyn }));

    private void OpenDockerOpacityDynamics() => ShowDockerDynamicsPopup(
        _dockerOpacDynPopup, v => _dockerOpacDynPopup = v, "Opacity",
        () => (_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).OpacityDynamics,
        dyn => UpdateCurrentBrushInternal(p => p with { OpacityDynamics = dyn }));

    private void OpenDockerFlowDynamics() => ShowDockerDynamicsPopup(
        _dockerFlowDynPopup, v => _dockerFlowDynPopup = v, "Flow",
        () => BrushDynamics.ToParameterDynamics((_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).Dynamics.Flow),
        dyn => UpdateCurrentBrushInternal(p => p with
        {
            Dynamics = DockerWithDynamics(p.Dynamics, d => d.Flow = BrushDynamics.ToCurveOption(dyn))
        }));

    private void OpenDockerHardnessDynamics() => ShowDockerDynamicsPopup(
        _dockerHardnessDynPopup, v => _dockerHardnessDynPopup = v, "Hardness",
        () => BrushDynamics.ToParameterDynamics((_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).Dynamics.Hardness),
        dyn => UpdateCurrentBrushInternal(p => p with
        {
            Dynamics = DockerWithDynamics(p.Dynamics, d => d.Hardness = BrushDynamics.ToCurveOption(dyn))
        }));

    private void OpenDockerSpacingDynamics() => ShowDockerDynamicsPopup(
        _dockerSpacingDynPopup, v => _dockerSpacingDynPopup = v, "Spacing",
        () => BrushDynamics.ToParameterDynamics((_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).Dynamics.Spacing),
        dyn => UpdateCurrentBrushInternal(p => p with
        {
            Dynamics = DockerWithDynamics(p.Dynamics, d => d.Spacing = BrushDynamics.ToCurveOption(dyn))
        }));

    private void OpenDockerTipDensityDynamics() => ShowDockerDynamicsPopup(
        _dockerTipDensityDynPopup, v => _dockerTipDensityDynPopup = v, "Brush Density",
        () => BrushDynamics.ToParameterDynamics((_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).Dynamics.TipDensity),
        dyn => UpdateCurrentBrushInternal(p => p with
        {
            Dynamics = DockerWithDynamics(p.Dynamics, d => d.TipDensity = BrushDynamics.ToCurveOption(dyn))
        }));

    private void OpenDockerTipThicknessDynamics() => ShowDockerDynamicsPopup(
        _dockerTipThicknessDynPopup, v => _dockerTipThicknessDynPopup = v, "Thickness",
        () => BrushDynamics.ToParameterDynamics((_ps.Brush.ActivePreset ?? _ps.Canvas.Brush).Dynamics.TipThickness),
        dyn => UpdateCurrentBrushInternal(p => p with
        {
            Dynamics = DockerWithDynamics(p.Dynamics, d => d.TipThickness = BrushDynamics.ToCurveOption(dyn))
        }));

    private void OpenDockerAngleDynamics()
    {
        if (_dockerAngleDynPopup != null)
        {
            _dockerAngleDynPopup.Activate();
            return;
        }

        var preset = _ps.Brush.ActivePreset ?? _ps.Canvas.Brush;
        _dockerAngleDynPopup = new AngleDynamicsPopupWindow(
            preset.BaseAngleSource,
            preset.AngleJitter,
            source => UpdateCurrentBrushInternal(p => p with { BaseAngleSource = source }),
            jitter => UpdateCurrentBrushInternal(p => p with { AngleJitter = jitter }));
        _dockerAngleDynPopup.Closed += (_, _) => _dockerAngleDynPopup = null;
        PositionDockerDynamicsPopup(_dockerAngleDynPopup);
        _dockerAngleDynPopup.Show(_ps.Shell.Owner);
    }

    private void ShowDockerDynamicsPopup(
        DynamicsPopupWindow? existing,
        Action<DynamicsPopupWindow?> assign,
        string title,
        Func<ParameterDynamics> getDynamics,
        Action<ParameterDynamics> onChange)
    {
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var popup = new DynamicsPopupWindow(title, getDynamics(), onChange);
        assign(popup);
        popup.Closed += (_, _) => assign(null);
        PositionDockerDynamicsPopup(popup);
        popup.Show(_ps.Shell.Owner);
    }

    private static BrushDynamics DockerWithDynamics(BrushDynamics source, Action<BrushDynamics> update)
    {
        var clone = source.Clone();
        update(clone);
        return clone;
    }

    private void EnsureTransformPropertySubscription()
    {
        if (_transformPropertySubscribed) return;
        _transformPropertySubscribed = true;
        _ps.Canvas.TransformEditChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshToolProperties);
    }

    private bool _transformPropertySubscribed;

    private IReadOnlyList<ToolPropertyDescriptor> CurrentTransformProperties()
    {
        EnsureTransformPropertySubscription();

        return
        [
            EnumProp("transform.mode", "Mode", true,
                () => _ps.Canvas.TransformEdit?.Mode ?? TransformMode.ScaleRotate,
                v => ApplyTransformEdit(s => s with { Mode = v })),
            SliderProp("transform.scaleW", "Scale W", true,
                () => _ps.Canvas.TransformEdit?.ScaleWPercent ?? 100,
                v => ApplyTransformEdit(s => s with { ScaleWPercent = v }), 1, 1000, "%"),
            SliderProp("transform.scaleH", "Scale H", true,
                () => _ps.Canvas.TransformEdit?.ScaleHPercent ?? 100,
                v => ApplyTransformEdit(s => s with { ScaleHPercent = v }), 1, 1000, "%"),
            BoolProp("transform.keepAspect", "Keep aspect ratio", true,
                () => _ps.Canvas.TransformEdit?.KeepAspectRatio ?? true,
                v => ApplyTransformEdit(s => s with { KeepAspectRatio = v })),
            SliderProp("transform.angle", "Rotation", true,
                () => _ps.Canvas.TransformEdit?.Angle ?? 0,
                v => ApplyTransformEdit(s => s with { Angle = v }), -180, 180, "°"),
        ];
    }

    private Control BuildTransformActionBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 4)
        };

        row.Children.Add(MkTransformBtn("Reset", () => _ps.Canvas.ResetTransformEdit()));
        row.Children.Add(MkTransformBtn("Flip H", () => _ps.Canvas.FlipTransformHorizontal()));
        row.Children.Add(MkTransformBtn("Flip V", () => _ps.Canvas.FlipTransformVertical()));
        row.Children.Add(MkTransformBtn("OK", () => _ps.Canvas.CommitActiveTool()));
        row.Children.Add(MkTransformBtn("Cancel", () => _ps.Canvas.CancelActiveTool()));

        return row;
    }

    private static Button MkTransformBtn(string text, Action action)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 10,
            MinHeight = 22,
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false
        };
        btn.Click += (_, _) => action();
        return btn;
    }

    private void ApplyTransformEdit(Func<TransformEditSnapshot, TransformEditSnapshot> mutate)
    {
        if (_ps.Sync.SyncingToolPropertyPanel) return;
        var cur = _ps.Canvas.TransformEdit;
        if (cur == null) return;
        var next = mutate(cur);
        if (next == cur) return;
        _ps.Canvas.UpdateTransformEdit(next);
    }

    private void UpdateCurrentBrushInternal(Func<BrushPreset, BrushPreset> update)
        => _ps.Brush.UpdateCurrentBrush(update);

    private static string FormatSliderValue(double v, string fmt) => fmt switch
    {
        "px" => $"{v:0}px",
        // 0–1 fraction sliders (spacing, opacity, flow); max-size uses 100–400 and skips this branch.
        "%" when v <= 1.0 => $"{v * 100:0}%",
        "%" => $"{v:0}%",
        "°" => $"{v:0}°",
        "" => v.ToString("0"),
        _ => $"{v:0.##}{fmt}"
    };

    private PaintingAssistant? SelectedAssistant()
        => _ps.Canvas.Document.Assistants.FindById(_ps.Canvas.Document.Assistants.SelectedId);

    /// <summary>Properties for a selected ruler/assistant (: tool panel shows selected object settings).</summary>
    private IReadOnlyList<ToolPropertyDescriptor> AssistantContextProperties(PaintingAssistant selected)
    {
        var preset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        var props = new List<ToolPropertyDescriptor>();

        props.Add(BoolProp("assistant.snap", "Snap", true,
            () => selected.SnapEnabled,
            v => CommitAssistantValue(preset!, selected, a => a.SnapEnabled = v, p => p.AssistantSnapEnabled = v)));

        if (selected.TypeId is PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType)
        {
            props.Add(PerspectiveModeProp("assistant.perspectiveMode", "Perspective", true,
                () => selected.PerspectiveMode,
                v => CommitAssistantPerspectiveMode(preset!, selected, v)));
            props.Add(BoolProp("assistant.fisheye", "Fisheye", true,
                () => selected.FisheyeEnabled,
                v => CommitAssistantValue(preset!, selected, a => a.FisheyeEnabled = v, p => p.AssistantFisheyeEnabled = v)));
            if (selected.FisheyeEnabled)
            {
                props.Add(SliderProp("assistant.fov", "FOV", true,
                    () => selected.FovDegrees,
                    v => CommitAssistantValue(preset!, selected, a => a.FovDegrees = v, p => p.AssistantFovDegrees = v),
                    10, 360, "°"));
            }

            props.Add(SliderProp("assistant.grid", "Grid", true,
                () => selected.GridSubdivisions,
                v => CommitAssistantValue(preset!, selected,
                    a => a.GridSubdivisions = (int)Math.Round(v),
                    p => p.AssistantGridSubdivisions = (int)Math.Round(v)),
                2, 12, ""));
        }

        return props;
    }

    private void CommitAssistantValue(
        ToolPreset preset,
        PaintingAssistant? selected,
        Action<PaintingAssistant> updateAssistant,
        Action<ToolPreset> updatePreset)
    {
        if (selected != null)
        {
            var doc = _ps.Canvas.Document;
            var before = doc.Assistants.CaptureSnapshot();
            updateAssistant(selected);
            doc.Assistants.NotifyChanged();
            doc.CommitAssistantsChange(before);
            _ps.Canvas.InvalidateVisual();
            return;
        }

        UpdateActiveToolPreset(updatePreset);
    }

    private void CommitAssistantPerspectiveMode(
        ToolPreset preset,
        PaintingAssistant? selected,
        PerspectiveAssistantMode mode)
    {
        if (selected != null)
        {
            var doc = _ps.Canvas.Document;
            var before = doc.Assistants.CaptureSnapshot();
            var bounds = PerspectiveDragBounds(selected);
            selected.PerspectiveMode = mode;
            selected.RepositionForCurrentMode(bounds.Start, bounds.End);
            doc.Assistants.NotifyChanged();
            doc.CommitAssistantsChange(before);
            _ps.Canvas.InvalidateVisual();
            return;
        }

        UpdateActiveToolPreset(p => p.AssistantPerspectiveMode = mode);
    }

    private static (Point Start, Point End) PerspectiveDragBounds(PaintingAssistant assistant)
    {
        var points = new List<Point> { assistant.HandleA, assistant.HandleB };
        if (assistant.HandleCount > 2) points.Add(assistant.HandleC);
        if (assistant.HandleCount > 3) points.Add(assistant.HandleD);
        return (new Point(points.Min(p => p.X), points.Min(p => p.Y)),
            new Point(points.Max(p => p.X), points.Max(p => p.Y)));
    }

    private ToolPropertyDescriptor PerspectiveModeProp(
        string id,
        string label,
        bool visible,
        Func<PerspectiveAssistantMode> get,
        Action<PerspectiveAssistantMode> set)
    {
        ComboBox? combo = null;
        return new(id, label, visible,
            () =>
            {
                combo = new ComboBox
                {
                    ItemsSource = Enum.GetValues<PerspectiveAssistantMode>(),
                    SelectedItem = get(),
                    FontSize = 10,
                    MinHeight = 22,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                combo.ItemTemplate = new FuncDataTemplate<PerspectiveAssistantMode>((mode, _) =>
                    new TextBlock { Text = mode.DisplayName(), FontSize = 10 });
                combo.SelectionChanged += (_, _) =>
                {
                    if (_ps.Sync.SyncingToolPropertyPanel) return;
                    if (combo.SelectedItem is PerspectiveAssistantMode value)
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
}
