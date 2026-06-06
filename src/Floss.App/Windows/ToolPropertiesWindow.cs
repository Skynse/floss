using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using Floss.App.Controls;
using Floss.App.Processes;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

public sealed class ToolPropertiesWindow : Window
{
    // ── Categories (dynamic based on tool type) ───────────────────────────────
    private readonly string[] _categories;
    private readonly bool _isBrushTool;
    private int _activeCategory;
    private readonly Button[] _catButtons;
    private readonly Border _contentHost = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private ToolPreset _toolPreset;
    private BrushPreset _brushPreset;
    private BrushTipData? _lastProceduralTip;
    private readonly Action<ToolPreset, Func<BrushPreset, BrushPreset>?> _onChange;
    private readonly Action<BrushTipNodeGraph, string>? _onSaveBrushTipGraphAsNew;
    private readonly Action? _onOpenNodeGraphEditor;
    private readonly BrushStrokePreview _preview = new() { Height = 64 };
    private bool _syncing;
    private int _presetSyncGeneration;
    private Avalonia.Media.Color _activePaintColor;

    // ── Texture / grain ───────────────────────────────────────────────────────
    private TextBlock _textureFileLabel = null!;

    // ── Sliders ───────────────────────────────────────────────────────────────
    private readonly ScrubSlider _sizeSlider = MkSlider(0.5, 1000, 8, "Brush size in pixels");
    private readonly ScrubSlider _opacitySlider = MkSlider(0.01, 1, 1.0, "Maximum opacity per stamp");
    private readonly ScrubSlider _flowSlider = MkSlider(0.01, 1, 1.0, "Paint buildup per dab");
    private Button? _mixToggle;
    private readonly ScrubSlider _colorLoadSlider = MkSlider(0, 1, 1.0, "Paint reload rate (1=always fresh, 0=color accumulates)");
    private readonly ScrubSlider _colorStretchSlider = MkSlider(0, 1, 0.5, "Color stretch intensity (0=gentle, 1=aggressive)");
    private readonly ScrubSlider _blurAmountSlider = MkSlider(0, 1, 0.0, "Blur during mixing (0=none, 1=full)");
    private readonly ScrubSlider _amountOfPaintSlider = MkSlider(0, 1, 1.0, "Amount of paint deposited (0=none, 1=full)");
    private readonly ScrubSlider _densityOfPaintSlider = MkSlider(0, 1, 1.0, "Paint density (0=thin, 1=thick)");
    private readonly ScrubSlider _hardnessSlider = MkSlider(0, 1, 0.9, "Edge softness (anti-aliasing)");
    private readonly ScrubSlider _spacingSlider = MkSlider(0.01, 1, 0.1, "Stamp interval as fraction of size");
    private readonly CheckBox _autoSpacingCheck = new()
    {
        Content = new TextBlock { Text = "Auto", FontSize = 11 },
        Margin = new Thickness(0, -2, 0, 0),
        IsChecked = true,
    };
    private readonly ScrubSlider _smoothingSlider = MkSlider(0, 0.95, 0.3, "Input stabilization");
    private CheckBox? _speedAdaptiveCheck;
    private readonly ScrubSlider _angleSlider = MkSlider(0, 360, 0, "Base angle in degrees");
    private readonly ScrubSlider _grainSlider = MkSlider(0, 1, 0.0, "Noise texture strength");
    private readonly ScrubSlider _tipDensitySlider = MkSlider(0, 1, 1.0, "Brush tip density (0=none, 1=full)");

    // ── Open dynamics popups ──────────────────────────────────────────────────
    private DynamicsPopupWindow? _sizeDynPopup;
    private DynamicsPopupWindow? _opacDynPopup;
    private DynamicsPopupWindow? _flowDynPopup;
    private DynamicsPopupWindow? _hardnessDynPopup;
    private DynamicsPopupWindow? _spacingDynPopup;
    private DynamicsPopupWindow? _scatterDynPopup;
    private DynamicsPopupWindow? _rotationDynPopup;
    private DynamicsPopupWindow? _tipDensityDynPopup;
    private DynamicsPopupWindow? _tipThicknessDynPopup;
    private AngleDynamicsPopupWindow? _angleDynPopup;

    // ── Cached category panels (built once to avoid re-parenting sliders) ────
    private ScrollViewer _brushSizePanel = null!;
    private ScrollViewer _inkPanel = null!;
    private ScrollViewer _aaPanel = null!;
    private ScrollViewer _strokePanel = null!;
    private ScrollViewer _texturePanel = null!;
    private ScrollViewer[]? _genericPanels;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ToolPropertiesWindow(ToolPreset toolPreset, BrushPreset? brushPreset,
        Action<ToolPreset, Func<BrushPreset, BrushPreset>?> onChange,
        Action<BrushTipNodeGraph, string>? onSaveBrushTipGraphAsNew = null,
        Action? onOpenNodeGraphEditor = null)
    {
        _toolPreset = toolPreset;
        _brushPreset = brushPreset ?? new BrushPreset(toolPreset.Name, 8, 1.0, 0.9, 0.1, Colors.White, 0);
        RememberProceduralTip(_brushPreset.Tip);
        _onChange = onChange;
        _onSaveBrushTipGraphAsNew = onSaveBrushTipGraphAsNew;
        _onOpenNodeGraphEditor = onOpenNodeGraphEditor;
        _isBrushTool = toolPreset.OutputProcess == OutputProcessType.DirectDraw;

        Width = 440;
        Height = 560;
        CanResize = true;
        MinWidth = 400;
        MinHeight = 440;
        Background = new SolidColorBrush(Color.Parse(Bg0));
        Title = _isBrushTool ? "Brush Settings" : $"Tool Properties — {toolPreset.Name}";
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Build dynamic categories based on tool type
        _categories = BuildCategories();
        _catButtons = _categories.Select((_, i) => MakeCatBtn(i)).ToArray();

        // Build slider-containing panels exactly once so sliders are never
        // re-parented when the user switches categories.
        if (_isBrushTool)
        {
            _brushSizePanel = WrapContent(BuildBrushSizeContent());
            _inkPanel = WrapContent(BuildInkContent());
            _aaPanel = WrapContent(BuildAntiAliasingContent());
            _strokePanel = WrapContent(BuildStrokeContent());
            _texturePanel = WrapContent(BuildTextureContent());
        }
        else
        {
            // For non-brush tools, build generic category panels
            _genericPanels = new ScrollViewer[_categories.Length];
            for (int i = 0; i < _categories.Length; i++)
                _genericPanels[i] = WrapContent(BuildGenericCategoryContent(i));
        }

        Content = BuildShell();
        HighlightActiveCategory();
        SelectCategory(0);
        if (_isBrushTool)
            SyncFromPreset(_brushPreset);
        WireSliderEvents();
    }

    private string[] BuildCategories()
    {
        if (_isBrushTool)
            return ["Brush Size", "Ink", "Anti-aliasing", "Brush shape", "Brush tip", "Stroke", "Texture"];

        return _toolPreset.OutputProcess switch
        {
            OutputProcessType.FloodFill => ["Fill Settings", "Paint Settings"],
            OutputProcessType.ClosedAreaFill => ["Lasso Fill Settings", "Paint Settings", "Lasso Input"],
            OutputProcessType.SelectionArea => ["Selection Settings", "Lasso Input"],
            OutputProcessType.Gradient => ["Gradient Settings", "Paint Settings"],
            OutputProcessType.Stroke => ["Stroke Settings", "Paint Settings"],
            OutputProcessType.MagicWand => ["Magic Wand Settings"],
            OutputProcessType.Liquify => ["Liquify Settings"],
            OutputProcessType.Eyedropper => ["Eyedropper Settings"],
            OutputProcessType.MoveLayer => ["Tool Info"],
            _ => ["Properties"]
        };
    }

    // ── Shell ─────────────────────────────────────────────────────────────────

    private Control BuildShell()
    {
        var title = new TextBlock
        {
            Text = _isBrushTool ? "Brush Settings" : "Tool Properties",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeBtn = HeaderButton(Icons.Close, "Close");
        closeBtn.Click += (_, _) => Close();

        var header = new DockPanel
        {
            LastChildFill = true,
            Height = 32,
            Margin = new Thickness(8, 0)
        };
        CustomWindowChrome.WireTitleBarDrag(header, this);
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(title);

        var navStack = new StackPanel { Spacing = 0 };
        foreach (var btn in _catButtons)
            navStack.Children.Add(btn);

        var nav = new Border
        {
            Width = 108,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = navStack
            }
        };

        _contentHost.Background = new SolidColorBrush(Color.Parse(Bg1));
        _contentHost.Padding = new Thickness(8, 6);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("108,*")
        };
        Grid.SetColumn(nav, 0);
        Grid.SetColumn(_contentHost, 1);
        body.Children.Add(nav);
        body.Children.Add(_contentHost);

        _preview.Height = 88;
        _preview.Margin = new Thickness(0);
        _preview.HorizontalAlignment = HorizontalAlignment.Stretch;

        var previewHost = new Border
        {
            Height = 88,
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Child = _preview
        };

        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = new SolidColorBrush(Color.Parse(Bg1))
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(previewHost, 2);
        shell.Children.Add(header);
        shell.Children.Add(body);
        shell.Children.Add(previewHost);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = shell
        };
    }

    private static Button HeaderButton(string icon, string tip)
    {
        var button = new Button
        {
            Content = MaterialIcon(icon, 16),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0)
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static string CategoryDisplayName(string category) => category switch
    {
        "Brush Size" => "Stamp",
        "Anti-aliasing" => "Edge",
        "Brush shape" => "Shape",
        "Brush tip" => "Tip",
        _ => category
    };

    private Button MakeCatBtn(int index)
    {
        var btn = new Button
        {
            Content = CategoryDisplayName(_categories[index]),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = 28,
            Padding = new Thickness(10, 0),
            FontSize = 11,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0)
        };
        btn.Click += (_, _) => SelectCategory(index);
        return btn;
    }

    private void HighlightActiveCategory()
    {
        for (var i = 0; i < _catButtons.Length; i++)
        {
            var active = i == _activeCategory;
            _catButtons[i].Background = active
                ? new SolidColorBrush(Color.Parse(SelectionBg))
                : new SolidColorBrush(Colors.Transparent);
            _catButtons[i].Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextSecondary));
            _catButtons[i].BorderBrush = active
                ? new SolidColorBrush(Color.Parse(Accent))
                : new SolidColorBrush(Colors.Transparent);
            _catButtons[i].BorderThickness = new Thickness(active ? 2 : 0, 0, 0, 0);
        }
    }

    private void CloseDynamicsPopups()
    {
        _sizeDynPopup?.Close();
        _sizeDynPopup = null;
        _opacDynPopup?.Close();
        _opacDynPopup = null;
        _flowDynPopup?.Close();
        _flowDynPopup = null;
        _hardnessDynPopup?.Close();
        _hardnessDynPopup = null;
        _spacingDynPopup?.Close();
        _spacingDynPopup = null;
        _scatterDynPopup?.Close();
        _scatterDynPopup = null;
        _rotationDynPopup?.Close();
        _rotationDynPopup = null;
        _tipDensityDynPopup?.Close();
        _tipDensityDynPopup = null;
        _tipThicknessDynPopup?.Close();
        _tipThicknessDynPopup = null;
        _angleDynPopup?.Close();
        _angleDynPopup = null;
    }

    private void SelectCategory(int index)
    {
        _activeCategory = index;
        HighlightActiveCategory();
        CloseDynamicsPopups();
        if (!_isBrushTool && _genericPanels != null)
        {
            _contentHost.Child = _genericPanels[index];
            return;
        }

        // BrushShape and BrushTip are rebuilt fresh (preset-dependent, no shared sliders).
        // All others are pre-built cached panels to avoid re-parenting sliders.
        _contentHost.Child = index switch
        {
            0 => _brushSizePanel,
            1 => _inkPanel,
            2 => _aaPanel,
            3 => WrapContent(BuildBrushShapeContent()),
            4 => WrapContent(BuildBrushTipContent()),
            5 => _strokePanel,
            6 => _texturePanel,
            _ => new Border()
        };
    }

    public void SelectBrushTipCategory()
    {
        var index = Array.IndexOf(_categories, "Brush tip");
        if (index >= 0)
            SelectCategory(index);
    }

    private static ScrollViewer WrapContent(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(0, 0, 4, 0)
    };

    // ── Category content ──────────────────────────────────────────────────────

    private Control BuildBrushSizeContent() => new StackPanel
    {
        Spacing = 0,
        Children =
        {
            DynSliderRow("Size", _sizeSlider, "px", () => OpenSizeDynamics(), "brush.size")
        }
    };

    private Control BuildInkContent() => new StackPanel
    {
        Spacing = 0,
        Children =
        {
            DynSliderRow("Opacity", _opacitySlider, "%", () => OpenOpacityDynamics(), "brush.opacity"),
            DynSliderRow("Flow",    _flowSlider,    "%", () => OpenFlowDynamics(), "brush.flow"),
            BuildBlendModeRow(),
            SectionHeader("COLOR MIXING"),
            BuildMixToggleRow("brush.colorMix"),
            BuildSmudgeModeRow("brush.smudgeMode"),
            PlainSliderRow("Amount of paint", _amountOfPaintSlider, "%", "brush.amountOfPaint"),
            PlainSliderRow("Density of paint", _densityOfPaintSlider, "%", "brush.densityOfPaint"),
            PlainSliderRow("Color stretch", _colorStretchSlider, "%", "brush.colorStretch"),
            PlainSliderRow("Intensity of blur", _blurAmountSlider, "%", "brush.blurAmount"),
            BuildMixingModeRow(),
        }
    };

    private Control BuildAntiAliasingContent() => new StackPanel
    {
        Spacing = 0,
        Children = { BuildAntialiasingLevelRow("brush.quality") }
    };

    private Control BuildBrushTipContent()
    {
        var mainTip = _brushPreset.Tip;
        RememberProceduralTip(mainTip);
        var procTip = BuiltInProceduralTipFor(mainTip);

        var result = new StackPanel { Spacing = 0 };

        // ── Tip image library (feeds Image Sampler nodes in the graph) ───────
        var galleryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var materialTips = MaterialTipsFor(_brushPreset);
        var activeTipData = ActiveMaterialTipData(_brushPreset);

        foreach (var tipData in materialTips)
        {
            var td = tipData;
            var isActiveMaterial = activeTipData != null && TipDataEquals(activeTipData, td);
            var tipObj = td.CreateTip();
            var bmp = BuildTipPreview(tipObj);
            if (tipObj is IDisposable disp) disp.Dispose();

            var img = new Image { Source = bmp, Width = 40, Height = 40, Stretch = Stretch.Uniform };
            var trashBtn = new Button
            {
                Content = "✕",
                Width = 14, Height = 14, Padding = new Thickness(0), FontSize = 8,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(Bg1)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0)
            };
            trashBtn.PointerPressed += (_, e) => e.Handled = true;
            trashBtn.Click += (_, _) =>
            {
                var newList = materialTips
                    .Where(t => !BrushMaterialTips.ReferencesSame(t, td))
                    .Select(t => t.DeepClone())
                    .ToList();
                Commit(p =>
                {
                    if (newList.Count == 0 && IsMaterialTip(p.Tip)
                        && ActiveMaterialTipData(p) is { } active
                        && BrushMaterialTips.ReferencesSame(active, td))
                        return p with { Tip = RestoreProceduralTip(), Tips = [] };

                    var (tips, tip) = BrushMaterialTips.ApplyLibraryChange(p, newList, removed: td);
                    return p with { Tips = tips, Tip = tip };
                });
                _contentHost.Child = WrapContent(BuildBrushTipContent());
            };

            var cell = new Grid { Width = 54, Height = 58, Margin = new Thickness(2) };
            cell.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(isActiveMaterial ? AccentSoft : Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(isActiveMaterial ? Accent : Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = img,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            });
            cell.Children.Add(trashBtn);
            cell.PointerPressed += (_, _) =>
            {
                if (isActiveMaterial) return;
                var newList = materialTips.Where(t => !TipDataEquals(t, td)).Select(t => t.DeepClone()).ToList();
                newList.Insert(0, td.DeepClone());
                Commit(p => p with { Tips = newList });
                _contentHost.Child = WrapContent(BuildBrushTipContent());
            };
            galleryPanel.Children.Add(cell);
        }

        var newCell = MkGalleryNewBtn(() =>
        {
            var browser = new BrushTipBrowserWindow(this, tip =>
            {
                RememberProceduralTip(_brushPreset.Tip);
                var td = BrushMaterialTips.NormalizeTip(BrushTipData.FromTip(tip));
                var newList = MaterialTipsFor(_brushPreset)
                    .Where(t => !BrushMaterialTips.ReferencesSame(t, td))
                    .Select(BrushMaterialTips.NormalizeTip)
                    .Append(td)
                    .ToList();
                Commit(p =>
                {
                    if (p.Tip is NodeBrushTip { IsDirectImageSampler: false })
                    {
                        var (tips, updatedTip) = BrushMaterialTips.ApplyLibraryChange(p, newList);
                        return p with { Tips = tips, Tip = updatedTip };
                    }
                    return p with { Tip = CreateGraphTipFromTipData(td), Tips = newList };
                });
                if (_activeCategory == 4)
                    _contentHost.Child = WrapContent(BuildBrushTipContent());
            });
            browser.Show(this);
        });
        galleryPanel.Children.Add(newCell);

        var galleryScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = galleryPanel,
            Margin = new Thickness(0, 0, 0, 2)
        };
        result.Children.Add(SectionHeader("TIP IMAGES"));
        result.Children.Add(new TextBlock
        {
            Text = "PNG library for Image Sampler nodes in the graph editor.",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });
        result.Children.Add(galleryScroll);
        if (materialTips.Count > 1)
            result.Children.Add(BuildTipSelectionModeRow());

        if (IsGraphTip(_brushPreset.Tip))
            result.Children.Add(BuildTipGraphControls());

        if (procTip?.Shape == BrushTipShape.Ellipse)
        {
            var aspectSlider = MkSlider(0.1, 8.0, Math.Clamp(procTip.AspectRatio, 0.1, 8.0), "Oval width/height ratio");
            WireSlider(aspectSlider, v => CommitMainTip(new ProceduralBrushTip(procTip.Shape, (float)v)));
            result.Children.Add(PlainSliderRowRaw(aspectSlider, "f2"));
        }

        // ── Brush tip properties ──────────────────────────────────────────────
        result.Children.Add(SectionHeader("BRUSH TIP"));

        var localHardness = MkSlider(0, 1, _brushPreset.Hardness, "Brush tip hardness");
        WireSlider(localHardness, v => Commit(p => p with { Hardness = v }));
        result.Children.Add(PlainSliderRow("Hardness", localHardness, "%", "brush.hardness"));

        var thicknessSlider = MkSlider(0.01, 1, Math.Clamp(_brushPreset.TipThickness, 0.01, 1.0), "Brush tip thickness");
        WireSlider(thicknessSlider, v => Commit(p => p with { TipThickness = v }));
        result.Children.Add(DynSliderRow("Thickness", thicknessSlider, "%", () => OpenTipThicknessDynamics(), "brush.tipThickness"));

        result.Children.Add(BuildTipDirectionRow("brush.tipDirection"));
        result.Children.Add(BuildFlipRow());

        var localAngle = MkSlider(0, 360, Math.Clamp(_brushPreset.Angle, 0, 360), "Brush tip angle");
        WireSlider(localAngle, v => Commit(p => p with { Angle = v }));
        result.Children.Add(DynSliderRow("Angle", localAngle, "°", () => openAngleDynamics(), "brush.angle"));

        var localDensity = MkSlider(0, 1, Math.Clamp(_brushPreset.TipDensity, 0, 1), "Brush tip density");
        WireSlider(localDensity, v => Commit(p => p with { TipDensity = v }));
        result.Children.Add(DynSliderRow("Brush density", localDensity, "%", () => OpenTipDensityDynamics(), "brush.tipDensity"));

        return result;
    }

    private Control BuildTipGraphControls()
    {
        var graph = GraphForTip(_brushPreset.Tip);
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(SectionHeader("TIP GRAPH"));

        var openEditorBtn = new Button
        {
            Content = "Open Graph Editor",
            FontSize = 11,
            MinHeight = 26,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Classes = { "primary" },
            Margin = new Thickness(0, 0, 0, 4)
        };
        openEditorBtn.Click += (_, _) =>
        {
            if (_onOpenNodeGraphEditor != null)
            {
                _onOpenNodeGraphEditor();
                return;
            }

            var materialTips = MaterialTipsFor(_brushPreset).Select(BrushMaterialTips.NormalizeTip).ToList();
            var boundGraph = BrushMaterialTips.BindGraphToLibrary(graph.DeepClone(), materialTips);
            var editor = new NodeGraphEditorWindow(boundGraph, g =>
            {
                if (g.Validate().Count > 0) return;
                var tips = MaterialTipsFor(_brushPreset).Select(BrushMaterialTips.NormalizeTip).ToList();
                var clone = BrushMaterialTips.BindGraphToLibrary(g.DeepClone(), tips);
                clone.BuiltInShape = null;
                var tip = (IBrushTip)new NodeBrushTip(clone);
                if (tip is NodeBrushTip nodeTip)
                    nodeTip.BindMaterialTips(tips);
                Commit(p => p with { Tip = tip, Tips = tips });
            }, (g, name) =>
            {
                if (g.Validate().Count > 0) return;
                var clone = g.DeepClone();
                clone.BuiltInShape = null;
                clone.BuiltInAspectRatio = 1.0f;
                if (_onSaveBrushTipGraphAsNew != null)
                    _onSaveBrushTipGraphAsNew(clone, name);
                else
                {
                    var tip = (IBrushTip)new NodeBrushTip(clone);
                    Commit(p => p with { Name = name, Tip = tip, Tips = PreserveMaterialTipsWithActiveFirst(p) });
                }
            }, MaterialTipsFor(_brushPreset));
            editor.Show(this);
        };
        panel.Children.Add(openEditorBtn);

        var errors = graph.Validate();
        if (errors.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", errors.Take(3)),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#ff8f8f")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 4)
            });
        }

        var nodeCount = graph.Nodes.Count;
        panel.Children.Add(new TextBlock
        {
            Text = $"{nodeCount} node{(nodeCount == 1 ? "" : "s")}. Wire Image Sampler nodes to Tip Images above.",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        });
        return panel;
    }

    private Control BuildNodeCard(BrushTipNodeGraph graph, BrushTipNode node)
    {
        var isOutput = node.Id == graph.OutputNodeId;
        var inner = new StackPanel { Spacing = 1 };

        // Header: kind · id  [×]
        var kindLabel = new TextBlock
        {
            Text = $"{node.Kind}  ·  {node.Id}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(isOutput ? TextPrimary : TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var deleteBtn = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = !isOutput
        };
        deleteBtn.Click += (_, _) =>
        {
            var g = graph.DeepClone();
            g.Nodes.RemoveAll(n => n.Id == node.Id);
            foreach (var n in g.Nodes)
                n.Inputs.RemoveAll(id => id == node.Id);
            CommitGraphTip(g);
            _contentHost.Child = WrapContent(BuildBrushTipContent());
        };

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(deleteBtn, Dock.Right);
        header.Children.Add(deleteBtn);
        header.Children.Add(kindLabel);
        inner.Children.Add(header);

        // Input connection ComboBoxes
        var inputCount = NodeInputCount(node.Kind);
        var otherIds = graph.Nodes.Where(n => n.Id != node.Id).Select(n => n.Id).ToList();
        const string NoneOption = "— none —";
        for (var i = 0; i < inputCount; i++)
        {
            var idx = i;
            var items = new List<string> { NoneOption };
            items.AddRange(otherIds);
            var current = idx < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[idx])
                ? node.Inputs[idx] : NoneOption;
            var combo = new ComboBox
            {
                ItemsSource = items,
                SelectedItem = otherIds.Contains(current) ? current : NoneOption,
                FontSize = 10,
                MinHeight = 22,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            combo.SelectionChanged += (_, _) =>
            {
                var g = graph.DeepClone();
                var n = g.Nodes.FirstOrDefault(x => x.Id == node.Id);
                if (n == null) return;
                var chosen = combo.SelectedItem as string ?? NoneOption;
                while (n.Inputs.Count <= idx) n.Inputs.Add("");
                n.Inputs[idx] = chosen != NoneOption ? chosen : "";
                while (n.Inputs.Count > 0 && string.IsNullOrEmpty(n.Inputs[^1]))
                    n.Inputs.RemoveAt(n.Inputs.Count - 1);
                CommitGraphTip(g);
            };
            var inputLbl = new TextBlock
            {
                Text = NodeInputLabel(node.Kind, idx),
                FontSize = 10,
                Width = 52,
                Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var inputRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
            DockPanel.SetDock(inputLbl, Dock.Left);
            inputRow.Children.Add(inputLbl);
            inputRow.Children.Add(combo);
            inner.Children.Add(inputRow);
        }

        // Parameter sliders
        if (node.Kind != BrushTipNodeKind.Output)
            AddNodeSlider(inner, graph, node, "Opacity", 0, 1, node.Opacity, (n, v) => n.Opacity = (float)v, "f2");
        switch (node.Kind)
        {
            case BrushTipNodeKind.Circle:
                AddNodeSlider(inner, graph, node, "Radius", 0.02, 0.75, node.Radius, (n, v) => n.Radius = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Width", 0.05, 2, node.Width, (n, v) => n.Width = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Height", 0.05, 2, node.Height, (n, v) => n.Height = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Edge", 0.001, 1, node.Hardness, (n, v) => n.Hardness = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Rotation", 0, 360, node.RotationDegrees, (n, v) => n.RotationDegrees = (float)v, "f1");
                break;
            case BrushTipNodeKind.Rectangle:
                AddNodeSlider(inner, graph, node, "Width", 0.05, 1.5, node.Width, (n, v) => n.Width = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Height", 0.02, 1.5, node.Height, (n, v) => n.Height = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Edge", 0.001, 1, node.Hardness, (n, v) => n.Hardness = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Rotation", 0, 360, node.RotationDegrees, (n, v) => n.RotationDegrees = (float)v, "f1");
                break;
            case BrushTipNodeKind.Noise:
                AddNodeSlider(inner, graph, node, "Density", 0, 1, node.Density, (n, v) => n.Density = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Scale", 0.05, 8, node.Scale, (n, v) => n.Scale = (float)v, "f2");
                break;
            case BrushTipNodeKind.Bristle:
                AddNodeSlider(inner, graph, node, "Density", 0.05, 1, node.Density, (n, v) => n.Density = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Width", 0.1, 1.5, node.Width, (n, v) => n.Width = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Height", 0.1, 1.5, node.Height, (n, v) => n.Height = (float)v, "f2");
                break;
            case BrushTipNodeKind.Threshold:
                AddNodeSlider(inner, graph, node, "Threshold", 0, 1, node.Threshold, (n, v) => n.Threshold = (float)v, "f2");
                break;
            case BrushTipNodeKind.LinearGradient:
                AddNodeSlider(inner, graph, node, "Scale", 0.05, 16, node.Scale, (n, v) => n.Scale = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Density", 0.02, 0.98, node.Density, (n, v) => n.Density = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Rotation", 0, 360, node.RotationDegrees, (n, v) => n.RotationDegrees = (float)v, "f1");
                break;
            case BrushTipNodeKind.Stripe:
                AddNodeSlider(inner, graph, node, "Scale", 0.05, 16, node.Scale, (n, v) => n.Scale = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Density", 0.02, 0.98, node.Density, (n, v) => n.Density = (float)v, "f2");
                AddNodeSlider(inner, graph, node, "Rotation", 0, 360, node.RotationDegrees, (n, v) => n.RotationDegrees = (float)v, "f1");
                break;
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isOutput ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Margin = new Thickness(0, 0, 0, 4),
            Child = inner
        };
    }

    private Control BuildAddNodeRow(BrushTipNodeGraph graph)
    {
        var kinds = Enum.GetValues<BrushTipNodeKind>()
            .Where(k => k != BrushTipNodeKind.Output)
            .ToList();
        var kindCombo = new ComboBox
        {
            ItemsSource = kinds,
            SelectedItem = BrushTipNodeKind.Circle,
            FontSize = 10,
            MinHeight = 22,
            Width = 120
        };
        var addBtn = SmBtn("Add node");
        addBtn.Click += (_, _) =>
        {
            if (kindCombo.SelectedItem is not BrushTipNodeKind kind) return;
            var g = graph.DeepClone();
            var id = $"{kind.ToString().ToLowerInvariant()}-{g.Nodes.Count + 1}";
            var newNode = new BrushTipNode { Id = id, Kind = kind };
            var outputIdx = g.Nodes.FindIndex(n => n.Id == g.OutputNodeId);
            g.Nodes.Insert(outputIdx >= 0 ? outputIdx : g.Nodes.Count, newNode);
            CommitGraphTip(g);
            _contentHost.Child = WrapContent(BuildBrushTipContent());
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { kindCombo, addBtn }
        };
    }

    private static int NodeInputCount(BrushTipNodeKind kind)
        => BrushTipNodeRegistry.InputCount(kind);

    private static string NodeInputLabel(BrushTipNodeKind kind, int index)
        => BrushTipNodeRegistry.InputLabel(kind, index);

    private void AddNodeSlider(StackPanel panel, BrushTipNodeGraph sourceGraph, BrushTipNode sourceNode, string label,
        double min, double max, float value, Action<BrushTipNode, double> setValue, string fmt)
    {
        var slider = MkSlider(min, max, Math.Clamp(value, (float)min, (float)max), label);
        WireSlider(slider, v =>
        {
            var graph = sourceGraph.DeepClone();
            var node = graph.Nodes.FirstOrDefault(n => n.Id == sourceNode.Id);
            if (node == null) return;
            setValue(node, v);
            CommitGraphTip(graph);
        });
        panel.Children.Add(BuildSliderRow(label, slider, fmt, extra: null));
    }

    private void CommitGraphTip(BrushTipNodeGraph graph)
    {
        if (graph.Validate().Count > 0)
            return;
        var clone = graph.DeepClone();
        clone.BuiltInShape = null;
        var tip = (IBrushTip)new NodeBrushTip(clone);
        Commit(p => p with { Tip = tip, Tips = PreserveMaterialTipsWithActiveFirst(p) });
    }

    private static BrushTipNodeGraph GraphForTip(IBrushTip tip)
        => tip switch
        {
            ProceduralBrushTip proc => proc.Graph.DeepClone(),
            NodeBrushTip node => node.Graph.DeepClone(),
            ImageBrushTip img => BrushTipNodeGraph.FromImageTip(img.GetPngBytes()),
            _ => BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Circle)
        };

    private Button MkImageCell(Bitmap? bmp, string label, bool active, Action onClick)
    {
        Control inner;
        if (bmp != null)
        {
            var img = new Image { Source = bmp, Width = 40, Height = 40, Stretch = Stretch.Uniform };
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 54
            };
            inner = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { img, lbl } };
        }
        else
        {
            inner = new TextBlock { Text = label, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) };
        }
        var btn = new Button
        {
            Width = 54, Height = 58, Margin = new Thickness(2), Padding = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = inner
        };
        ToolTip.SetTip(btn, label);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Control BuildTipDirectionRow(string? toolPropId = null)
    {
        Button horizontal = null!;
        Button vertical = null!;
        void SetDirection(BrushTipDirection direction)
        {
            if (_syncing) return;
            Commit(p => p with { TipDirection = direction });
            StylizeToggle(horizontal, direction == BrushTipDirection.Horizontal);
            StylizeToggle(vertical, direction == BrushTipDirection.Vertical);
        }

        horizontal = MkToggleBtn("Horizontal", _brushPreset.TipDirection == BrushTipDirection.Horizontal, () => SetDirection(BrushTipDirection.Horizontal));
        vertical = MkToggleBtn("Vertical", _brushPreset.TipDirection == BrushTipDirection.Vertical, () => SetDirection(BrushTipDirection.Vertical));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(horizontal);
        panel.Children.Add(vertical);

        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 2, 0, 2),
            Children =
            {
                new TextBlock { Text = "Direction", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), Width = 72, VerticalAlignment = VerticalAlignment.Center },
                panel
            }
        };
        if (toolPropId != null) return AddEyeButton(row, toolPropId);
        return row;
    }

    private Control BuildTipSelectionModeRow()
    {
        Button single = null!;
        Button sequential = null!;
        Button random = null!;

        void SetMode(BrushTipSelectionMode mode)
        {
            if (_syncing) return;
            Commit(p => p with { TipSelectionMode = mode });
            StylizeToggle(single, mode == BrushTipSelectionMode.Single);
            StylizeToggle(sequential, mode == BrushTipSelectionMode.Sequential);
            StylizeToggle(random, mode == BrushTipSelectionMode.Random);
        }

        single = MkToggleBtn("Single", _brushPreset.TipSelectionMode == BrushTipSelectionMode.Single, () => SetMode(BrushTipSelectionMode.Single));
        sequential = MkToggleBtn("Cycle", _brushPreset.TipSelectionMode == BrushTipSelectionMode.Sequential, () => SetMode(BrushTipSelectionMode.Sequential));
        random = MkToggleBtn("Random", _brushPreset.TipSelectionMode == BrushTipSelectionMode.Random, () => SetMode(BrushTipSelectionMode.Random));

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(single);
        panel.Children.Add(sequential);
        panel.Children.Add(random);

        return new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 2, 0, 2),
            Children =
            {
                new TextBlock { Text = "Tip order", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), Width = 72, VerticalAlignment = VerticalAlignment.Center },
                panel
            }
        };
    }

    private Button MkGalleryNewBtn(Action onClick)
    {
        var btn = new Button
        {
            Width = 54, Height = 58, Margin = new Thickness(2), Padding = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = "+", FontSize = 20, Foreground = new SolidColorBrush(Color.Parse(TextMuted)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        ToolTip.SetTip(btn, "Add tip");
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Control BuildFlipRow()
    {
        Button flipH = null!;
        Button flipV = null!;

        flipH = MkToggleBtn("Flip H", _brushPreset.FlipHorizontal, () =>
        {
            if (_syncing) return;
            var next = !_brushPreset.FlipHorizontal;
            Commit(p => p with { FlipHorizontal = next });
            StylizeToggle(flipH, next);
        });
        flipV = MkToggleBtn("Flip V", _brushPreset.FlipVertical, () =>
        {
            if (_syncing) return;
            var next = !_brushPreset.FlipVertical;
            Commit(p => p with { FlipVertical = next });
            StylizeToggle(flipV, next);
        });

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(flipH);
        panel.Children.Add(flipV);

        return new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 2, 0, 2),
            Children =
            {
                new TextBlock { Text = "Flip", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), Width = 72, VerticalAlignment = VerticalAlignment.Center },
                panel
            }
        };
    }

    private Button MkShapeCell(string label, BrushTipShape? shape, bool active, Action onClick)
    {
        var thumb = shape.HasValue ? RenderTipThumbnail(shape.Value) : null;
        Control inner;
        if (thumb != null)
        {
            var img = new Image { Source = thumb, Width = 36, Height = 36, Stretch = Stretch.Uniform };
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted))
            };
            inner = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { img, lbl } };
        }
        else
        {
            inner = new TextBlock { Text = label, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted)) };
        }
        var btn = new Button
        {
            Width = 54,
            Height = 58,
            Margin = new Thickness(2),
            Padding = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = inner
        };
        ToolTip.SetTip(btn, label);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void OpenTipBrowser()
    {
        var browser = new BrushTipBrowserWindow(this, tip =>
        {
            RememberProceduralTip(_brushPreset.Tip);
            CommitMainTip(tip);
        });
        browser.Show(this);
    }

    private static IImage? BuildTipPreview(IBrushTip tip)
    {
        const int size = 48;
        if (tip is ImageBrushTip img)
        {
            try { using var ms = new MemoryStream(img.GetPngBytes()); return new Bitmap(ms); }
            catch { return null; }
        }
        if (tip is NodeBrushTip node && node.Graph.TryGetDirectImageSampler(out var bytes))
        {
            try { using var ms = new MemoryStream(bytes); return new Bitmap(ms); }
            catch { return null; }
        }
        return RenderMaskThumbnail(tip, size);
    }

    private static IImage? RenderTipThumbnail(BrushTipShape shape)
    {
        var tip = new ProceduralBrushTip(shape, shape == BrushTipShape.Ellipse ? 2.4f : 1.0f);
        return RenderMaskThumbnail(tip, 40);
    }

    // Renders a tip mask directly into a WriteableBitmap via SKSurface.
    // Avoids the SKBitmap → SKImage → PNG → Bitmap roundtrip that could race
    // with GC finalizers disposing cached SKBitmaps while the render thread
    // is still holding references to them.
    private static WriteableBitmap? RenderMaskThumbnail(IBrushTip tip, int size)
    {
        try
        {
            var mask = tip.GenerateMask(size - 6, 0.85f);
            var wb = new WriteableBitmap(
                new PixelSize(size, size),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            using var fb = wb.Lock();
            var info = new SKImageInfo(fb.Size.Width, fb.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
            if (surface == null) return null;
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0x28, 0x24, 0x28));
            using var colorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn);
            using var paint = new SKPaint { ColorFilter = colorFilter, IsAntialias = true };
            canvas.Translate(3f, 3f);
            canvas.DrawBitmap(mask, 0, 0, paint);
            canvas.Flush();
            return wb;
        }
        catch { return null; }
    }

    private static List<string> GetPngFiles()
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(AppPaths.BrushTipsDirectory))
                list.AddRange(Directory.EnumerateFiles(AppPaths.BrushTipsDirectory, "*.png"));
        }
        catch (Exception ex) { CrashLog.Write(ex, "ToolPropertiesWindow.GetBrushTipFiles"); }
        return list;
    }

    private static Button MkToggleBtn(string label, bool active, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Height = 22,
            Padding = new Thickness(8, 0),
            FontSize = 10,
            Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(active ? SelectionBorder : Stroke)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextSecondary)),
            CornerRadius = new CornerRadius(4)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Control BuildBrushShapeContent()
    {
        var stack = new StackPanel { Spacing = 0 };

        // Current tip preview + save row
        var previewBmp = BuildTipPreview(_brushPreset.Tip);
        var previewImg = new Image { Source = previewBmp, Width = 56, Height = 56, Stretch = Stretch.Uniform };
        var previewBox = new Border
        {
            Width = 64, Height = 64, Padding = new Thickness(4),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = previewImg
        };

        var nameBox = new TextBox
        {
            FontSize = 12,
            MinHeight = 30,
            Height = 30,
            PlaceholderText = "Preset name",
            Text = _brushPreset.Name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 0)
        };

        var addBtn = SmBtn("Add to presets");
        addBtn.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var p = BrushShapePreset.FromPreset(_brushPreset, name);
            App.Config.BrushShapePresets.RemoveAll(x => x.Name == name);
            App.Config.BrushShapePresets.Add(p);
            App.Config.Save();
            _contentHost.Child = WrapContent(BuildBrushShapeContent());
        };

        var nameRow = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(nameBox);
        nameRow.Children.Add(addBtn);

        var topRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 12) };
        DockPanel.SetDock(previewBox, Dock.Left);
        topRow.Children.Add(previewBox);
        topRow.Children.Add(new Border { Width = 12 });
        topRow.Children.Add(nameRow);

        stack.Children.Add(SectionHeader("CURRENT SHAPE"));
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = topRow
        });
        stack.Children.Add(SectionHeader("SAVED SHAPES"));

        var presets = App.Config.BrushShapePresets;
        if (presets.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No saved shapes yet.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                Margin = new Thickness(0, 6)
            });
        }
        else
        {
            foreach (var preset in presets.ToList())
                stack.Children.Add(BuildShapePresetRow(preset));
        }

        return stack;
    }

    private Control BuildShapePresetRow(BrushShapePreset shapePreset)
    {
        var tipObj = shapePreset.Tip.CreateTip();
        var bmp = BuildTipPreview(tipObj);
        if (tipObj is IDisposable disp) disp.Dispose();

        var img = new Image { Source = bmp, Width = 30, Height = 30, Stretch = Stretch.Uniform };
        var thumb = new Border
        {
            Width = 36, Height = 36, Padding = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = img
        };

        var nameLabel = new TextBlock
        {
            Text = shapePreset.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var applyBtn = SmBtn("Apply");
        applyBtn.Click += (_, _) =>
        {
            var applied = shapePreset.Apply(_brushPreset);
            Commit(_ => applied);
            if (_activeCategory == 4)
                _contentHost.Child = WrapContent(BuildBrushTipContent());
        };

        var deleteBtn = new Button
        {
            Content = "✕",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            FontSize = 9,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        deleteBtn.Click += (_, _) =>
        {
            App.Config.BrushShapePresets.Remove(shapePreset);
            App.Config.Save();
            _contentHost.Child = WrapContent(BuildBrushShapeContent());
        };

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2) };
        DockPanel.SetDock(thumb, Dock.Left);
        DockPanel.SetDock(deleteBtn, Dock.Right);
        DockPanel.SetDock(applyBtn, Dock.Right);
        row.Children.Add(thumb);
        row.Children.Add(deleteBtn);
        row.Children.Add(new Border { Width = 4 });
        row.Children.Add(applyBtn);
        row.Children.Add(new Border { Width = 8 });
        row.Children.Add(nameLabel);
        return row;
    }

    private Control BuildStrokeContent()
    {
        _speedAdaptiveCheck = new CheckBox
        {
            Content = new TextBlock { Text = "Adjust by speed", FontSize = 11 },
            Margin = new Thickness(0, 2, 0, 0),
            IsChecked = _brushPreset.SpeedAdaptiveStabilizer,
        };
        _speedAdaptiveCheck.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            Commit(p => p with { SpeedAdaptiveStabilizer = _speedAdaptiveCheck?.IsChecked ?? true });
        };
        var speedAdaptiveRow = AddEyeButton(_speedAdaptiveCheck, "brush.speedAdaptive");
        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                DynSliderRow("Spacing",   _spacingSlider,   "%", () => OpenSpacingDynamics(), "brush.spacing"),
                AddEyeButton(_autoSpacingCheck, "brush.autoSpacing"),
                PlainSliderRow("Stabilization", _smoothingSlider, "%", "brush.smoothing"),
                speedAdaptiveRow
            }
        };
    }

    private Control BuildTextureContent()
    {
        // Grain texture picker
        _textureFileLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 160,
            Text = _brushPreset.Texture != null ? Path.GetFileName(_brushPreset.Texture) : "None"
        };
        var browseTexBtn = SmBtn("Browse...");
        browseTexBtn.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select grain texture",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path == null) return;
            Commit(p => p with { Texture = path });
            _textureFileLabel.Text = Path.GetFileName(path);
        };
        var clearTexBtn = SmBtn("Clear");
        clearTexBtn.Click += (_, _) =>
        {
            Commit(p => p with { Texture = null });
            _textureFileLabel.Text = "None";
        };
        var texBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        texBtnRow.Children.Add(browseTexBtn);
        texBtnRow.Children.Add(clearTexBtn);

        var texRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(new TextBlock
        {
            Text = "Texture",
            FontSize = 10,
            Width = 60,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        }, Dock.Left);
        texRow.Children.Add(new TextBlock
        {
            Text = "Texture",
            FontSize = 10,
            Width = 60,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        });
        texRow.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _textureFileLabel, texBtnRow }
        });

        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                PlainSliderRow("Grain", _grainSlider, "%", "brush.grain"),
                texRow,
            }
        };
    }

    // ── Generic category content for non-brush tools ─────────────────────────

    private Control BuildGenericCategoryContent(int categoryIndex)
    {
        var panel = new StackPanel { Spacing = 6 };
        var output = _toolPreset.OutputProcess;
        var isPaint = output is OutputProcessType.DirectDraw
            or OutputProcessType.ClosedAreaFill
            or OutputProcessType.FloodFill
            or OutputProcessType.Gradient
            or OutputProcessType.Stroke;

        var cat = _categories[categoryIndex];

        if (cat == "Paint Settings")
        {
            if (_isBrushTool)
            {
                panel.Children.Add(BuildGenericSliderRow("Opacity", 0.01, 1.0,
                    _brushPreset.Opacity,
                    v => Commit(p => p with { Opacity = v }), "%", mult: 100, toolPropId: "paint.opacity"));
                panel.Children.Add(BuildGenericComboRow<SkiaSharp.SKBlendMode>("Blend Mode",
                    _brushPreset.BlendMode,
                    v => Commit(p => p with { BlendMode = v }), toolPropId: "paint.blendMode"));
            }
            else
            {
                panel.Children.Add(BuildGenericSliderRow("Opacity", 0.01, 1.0,
                    _toolPreset.BrushOverride?.Opacity ?? 1.0,
                    v => CommitTool(p => (p.BrushOverride ??= new()).Opacity = v), "%", mult: 100, toolPropId: "paint.opacity"));
                panel.Children.Add(BuildGenericComboRow<SkiaSharp.SKBlendMode>("Blend Mode",
                    _toolPreset.BrushOverride?.BlendMode ?? SkiaSharp.SKBlendMode.SrcOver,
                    v => CommitTool(p => (p.BrushOverride ??= new()).BlendMode = v), toolPropId: "paint.blendMode"));
            }
            return panel;
        }

        switch (output)
        {
            case OutputProcessType.FloodFill when cat == "Fill Settings":
                panel.Children.Add(BuildGenericSliderRow("Tolerance", 0, 1.0,
                    _toolPreset.Tolerance, v => CommitTool(p => p.Tolerance = v), toolPropId: "fill.tolerance"));
                panel.Children.Add(BuildGenericSliderRow("Area Scaling", -20, 20,
                    _toolPreset.AreaScaling, v => CommitTool(p => p.AreaScaling = v), "px", step: 1, toolPropId: "fill.areaScaling"));
                panel.Children.Add(BuildGenericToggleRow("Contiguous Fill",
                    _toolPreset.ContiguousFill, v => CommitTool(p => p.ContiguousFill = v), toolPropId: "fill.contiguous"));
                break;

            case OutputProcessType.ClosedAreaFill when cat == "Lasso Fill Settings":
                panel.Children.Add(BuildGenericSliderRow("Tolerance", 0, 1.0,
                    _toolPreset.Tolerance, v => CommitTool(p => p.Tolerance = v), toolPropId: "lasso.tolerance"));
                panel.Children.Add(BuildGenericSliderRow("Area Scaling", -20, 20,
                    _toolPreset.AreaScaling, v => CommitTool(p => p.AreaScaling = v), "px", step: 1, toolPropId: "lasso.areaScaling"));
                break;

            case OutputProcessType.ClosedAreaFill when cat == "Lasso Input":
            case OutputProcessType.SelectionArea when cat == "Lasso Input":
                panel.Children.Add(BuildGenericComboRow<AntialiasingQuality>("Antialiasing",
                    _toolPreset.AntialiasingQuality, v => CommitTool(p => p.AntialiasingQuality = v), toolPropId: "input.antialiasing"));
                panel.Children.Add(BuildGenericSliderRow("Stabilization", 0, 1.0,
                    _toolPreset.Stabilization, v => CommitTool(p => p.Stabilization = v), toolPropId: "input.stabilization"));
                break;

            case OutputProcessType.SelectionArea when cat == "Selection Settings":
                panel.Children.Add(BuildGenericComboRow<SelectMode>("Selection Mode",
                    _toolPreset.SelectMode, v => CommitTool(p => { p.SelectMode = v; p.SyncSelectInputFromMode(); }), toolPropId: "select.mode"));
                panel.Children.Add(BuildGenericComboRow<SelectOp>("Operation",
                    _toolPreset.SelectOp, v => CommitTool(p => p.SelectOp = v), toolPropId: "select.op"));
                break;

            case OutputProcessType.Gradient when cat == "Gradient Settings":
                panel.Children.Add(BuildGenericComboRow<GradientType>("Gradient Type",
                    _toolPreset.GradientType, v => CommitTool(p => p.GradientType = v), toolPropId: "gradient.type"));
                break;

            case OutputProcessType.Stroke when cat == "Stroke Settings":
                panel.Children.Add(BuildGenericSliderRow("Stroke Width", 1, 200,
                    _toolPreset.PolylineStrokeWidth, v => CommitTool(p => p.PolylineStrokeWidth = (float)v), "px", step: 0.5, toolPropId: "stroke.width"));
                panel.Children.Add(BuildGenericToggleRow("Close Path",
                    _toolPreset.PolylineClosePath, v => CommitTool(p => p.PolylineClosePath = v), toolPropId: "stroke.closePath"));
                break;

            case OutputProcessType.MagicWand when cat == "Magic Wand Settings":
                panel.Children.Add(BuildGenericSliderRow("Tolerance", 0, 1.0,
                    _toolPreset.Tolerance, v => CommitTool(p => p.Tolerance = v), toolPropId: "wand.tolerance"));
                panel.Children.Add(BuildGenericComboRow<SelectOp>("Operation",
                    _toolPreset.SelectOp, v => CommitTool(p => p.SelectOp = v), toolPropId: "wand.op"));
                panel.Children.Add(BuildGenericToggleRow("Contiguous",
                    _toolPreset.ContiguousFill, v => CommitTool(p => p.ContiguousFill = v), toolPropId: "wand.contiguous"));
                break;

            case OutputProcessType.Liquify when cat == "Liquify Settings":
                panel.Children.Add(BuildGenericSliderRow("Size", 10, 500,
                    _toolPreset.LiquifySize, v => CommitTool(p => p.LiquifySize = v), "px", step: 1, toolPropId: "liquify.size"));
                panel.Children.Add(BuildGenericSliderRow("Strength", 0, 1.0,
                    _toolPreset.LiquifyStrength, v => CommitTool(p => p.LiquifyStrength = v), "%", mult: 100, toolPropId: "liquify.strength"));
                panel.Children.Add(BuildGenericComboRow<LiquifyMode>("Mode",
                    _toolPreset.LiquifyMode, v => CommitTool(p => p.LiquifyMode = v), toolPropId: "liquify.mode"));
                break;

            case OutputProcessType.Eyedropper when cat == "Eyedropper Settings":
                panel.Children.Add(BuildGenericComboRow<EyedropperSampleMode>("Sample",
                    _toolPreset.EyedropperSampleMode, v => CommitTool(p => p.EyedropperSampleMode = v), toolPropId: "eyedropper.sampleMode"));
                panel.Children.Add(BuildGenericToggleRow("Exclude locked layers",
                    _toolPreset.EyedropperExcludeLockedLayers, v => CommitTool(p => p.EyedropperExcludeLockedLayers = v), toolPropId: "eyedropper.excludeLocked"));
                panel.Children.Add(BuildGenericToggleRow("Exclude reference layers",
                    _toolPreset.EyedropperExcludeReferenceLayers, v => CommitTool(p => p.EyedropperExcludeReferenceLayers = v), toolPropId: "eyedropper.excludeReference"));
                break;

            case OutputProcessType.MoveLayer when cat == "Tool Info":
                panel.Children.Add(new TextBlock
                {
                    Text = "This tool has no adjustable properties.",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                    Margin = new Thickness(0, 12, 0, 0)
                });
                break;
        }

        return panel;
    }

    private void CommitTool(Action<ToolPreset> update)
    {
        update(_toolPreset);
        _onChange(_toolPreset, null);
    }

    // ── Generic widget builders ───────────────────────────────────────────────

    private static Control BuildGenericSliderRow(string label, double min, double max, double value,
        Action<double> setter, string fmt = "%", double mult = 1.0, double step = 0.01, string? toolPropId = null)
    {
        var slider = ScrubSliderFactory.Create(min, max, value);

        var valueLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 40,
            TextAlignment = TextAlignment.Right,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };

        void UpdateLabel() => valueLabel.Text = mult == 1.0
            ? $"{slider.Value:F2}"
            : $"{slider.Value * mult:F0}{fmt}";
        UpdateLabel();

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) UpdateLabel();
        };

        slider.AddHandler(PointerReleasedEvent, (_, _) => setter(slider.Value), handledEventsToo: true);
        slider.LostFocus += (_, _) => setter(slider.Value);

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2) };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(valueLabel, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(valueLabel);
        row.Children.Add(slider);

        if (toolPropId != null)
            return AddEyeButton(row, toolPropId);
        return row;
    }

    private static Control BuildGenericComboRow<T>(string label, T value, Action<T> setter, string? toolPropId = null)
        where T : struct, Enum
    {
        var combo = new ComboBox
        {
            ItemsSource = Enum.GetValues<T>(),
            SelectedItem = value,
            FontSize = 11,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is T v) setter(v);
        };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2) };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(combo);

        if (toolPropId != null)
            return AddEyeButton(row, toolPropId);
        return row;
    }

    private static Control BuildGenericToggleRow(string label, bool value, Action<bool> setter, string? toolPropId = null)
    {
        var check = new CheckBox
        {
            IsChecked = value,
            Content = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
        };
        check.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
                setter(check.IsChecked == true);
        };

        if (toolPropId != null)
            return AddEyeButton(check, toolPropId);
        return check;
    }

    private static Control AddEyeButton(Control content, string toolPropId)
    {
        var eyeBtn = new Button
        {
            Content = "◉",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        bool visible = App.Config.IsToolPropertyDockerVisible(toolPropId);
        eyeBtn.Foreground = new SolidColorBrush(Color.Parse(visible ? Accent : TextMuted));

        eyeBtn.Click += (_, _) =>
        {
            App.Config.ToggleToolPropertyDockerVisible(toolPropId);
            var newVisible = App.Config.IsToolPropertyDockerVisible(toolPropId);
            eyeBtn.Foreground = new SolidColorBrush(Color.Parse(newVisible ? Accent : TextMuted));
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(eyeBtn, Dock.Right);
        row.Children.Add(eyeBtn);
        row.Children.Add(content);
        return row;
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private Control DynSliderRow(string label, ScrubSlider slider, string fmt, Action openDyn, string? toolPropId = null)
    {
        var dynBtn = new Button
        {
            Content = MaterialIcon(Icons.TuneVertical, 12),
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(dynBtn, "Edit dynamics curve");
        dynBtn.Click += (_, _) => openDyn();

        return BuildSliderRow(label, slider, fmt, dynBtn, toolPropId);
    }

    private Control PlainSliderRow(string label, ScrubSlider slider, string fmt, string? toolPropId = null)
        => BuildSliderRow(label, slider, fmt, null, toolPropId);

    private Control DynButtonRow(string label, Action openDyn)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Width = 60,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var dynBtn = new Button
        {
            Content = MaterialIcon(Icons.TuneVertical, 14),
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(dynBtn, "Edit dynamics curve");
        dynBtn.Click += (_, _) => openDyn();

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(dynBtn, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(dynBtn);
        return row;
    }

    private static Control PlainSliderRowRaw(ScrubSlider slider, string fmt)
    {
        var val = MkValLabel(slider.Value, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) val.Text = FormatV(slider.Value, fmt);
        };
        slider.Margin = new Thickness(4, 0);
        slider.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1), MinHeight = 24 };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);
        row.Children.Add(slider);
        return row;
    }

    private static Control BuildSliderRow(string label, ScrubSlider slider, string fmt, Control? extra, string? toolPropId = null)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Width = 88,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var val = MkValLabel(slider.Value, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) val.Text = FormatV(slider.Value, fmt);
        };
        slider.Margin = new Thickness(4, 0);
        slider.VerticalAlignment = VerticalAlignment.Center;

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1), MinHeight = 24 };
        DockPanel.SetDock(lbl, Dock.Left);

        Button? eyeBtn = null;
        if (toolPropId != null)
        {
            var visible = App.Config.IsToolPropertyDockerVisible(toolPropId);
            eyeBtn = new Button
            {
                Content = visible ? "◉" : "○",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                FontSize = 9,
                Margin = new Thickness(2, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(visible ? Accent : TextMuted)),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(eyeBtn, visible ? "Hide from tool property docker" : "Show in tool property docker");
            eyeBtn.Click += (_, _) =>
            {
                App.Config.ToggleToolPropertyDockerVisible(toolPropId);
                var newVisible = App.Config.IsToolPropertyDockerVisible(toolPropId);
                eyeBtn.Content = newVisible ? "◉" : "○";
                eyeBtn.Foreground = new SolidColorBrush(Color.Parse(newVisible ? Accent : TextMuted));
                ToolTip.SetTip(eyeBtn, newVisible ? "Hide from tool property docker" : "Show in tool property docker");
            };
            DockPanel.SetDock(eyeBtn, Dock.Right);
        }

        if (extra != null)
        {
            extra.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(extra, Dock.Right);
        }

        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(val);
        if (extra != null)
            row.Children.Add(extra);
        if (eyeBtn != null)
            row.Children.Add(eyeBtn);
        row.Children.Add(slider);
        return row;
    }

    private static TextBlock MkValLabel(double value, string fmt) => new()
    {
        Text = FormatV(value, fmt),
        FontSize = 10,
        Width = 44,
        TextAlignment = Avalonia.Media.TextAlignment.Right,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Consolas, Courier New, monospace")
    };

    // ── Dynamics popups ───────────────────────────────────────────────────────

    private void OpenSizeDynamics()
    {
        if (_sizeDynPopup != null) { _sizeDynPopup.Activate(); return; }
        _sizeDynPopup = new DynamicsPopupWindow("Brush Size", _brushPreset.SizeDynamics, dyn =>
        {
            Commit(p => p with { SizeDynamics = dyn });
        });
        _sizeDynPopup.Closed += (_, _) => _sizeDynPopup = null;
        PositionPopup(_sizeDynPopup);
        _sizeDynPopup.Show(this);
    }

    private void OpenOpacityDynamics()
    {
        if (_opacDynPopup != null) { _opacDynPopup.Activate(); return; }
        _opacDynPopup = new DynamicsPopupWindow("Opacity", _brushPreset.OpacityDynamics, dyn =>
        {
            Commit(p => p with { OpacityDynamics = dyn });
        });
        _opacDynPopup.Closed += (_, _) => _opacDynPopup = null;
        PositionPopup(_opacDynPopup);
        _opacDynPopup.Show(this);
    }

    private void OpenFlowDynamics()
    {
        if (_flowDynPopup != null) { _flowDynPopup.Activate(); return; }
        _flowDynPopup = new DynamicsPopupWindow("Flow", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.Flow), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.Flow = BrushDynamics.ToCurveOption(dyn)) });
        });
        _flowDynPopup.Closed += (_, _) => _flowDynPopup = null;
        PositionPopup(_flowDynPopup);
        _flowDynPopup.Show(this);
    }

    private void OpenHardnessDynamics()
    {
        if (_hardnessDynPopup != null) { _hardnessDynPopup.Activate(); return; }
        _hardnessDynPopup = new DynamicsPopupWindow("Hardness", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.Hardness), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.Hardness = BrushDynamics.ToCurveOption(dyn)) });
        });
        _hardnessDynPopup.Closed += (_, _) => _hardnessDynPopup = null;
        PositionPopup(_hardnessDynPopup);
        _hardnessDynPopup.Show(this);
    }



    private void openAngleDynamics()
    {
        if (_angleDynPopup != null)
        {
            _angleDynPopup.Activate();
            return;
        }

        _angleDynPopup = new AngleDynamicsPopupWindow(
            _brushPreset.BaseAngleSource,
            _brushPreset.AngleJitter,
            source => Commit(p => p with { BaseAngleSource = source }),
            jitter => Commit(p => p with { AngleJitter = jitter })
        );

        _angleDynPopup.Closed += (_, _) => _angleDynPopup = null;
        PositionPopup(_angleDynPopup);
        _angleDynPopup.Show(this);
    }

    private void OpenSpacingDynamics()
    {
        if (_spacingDynPopup != null) { _spacingDynPopup.Activate(); return; }
        _spacingDynPopup = new DynamicsPopupWindow("Spacing", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.Spacing), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.Spacing = BrushDynamics.ToCurveOption(dyn)) });
        });
        _spacingDynPopup.Closed += (_, _) => _spacingDynPopup = null;
        PositionPopup(_spacingDynPopup);
        _spacingDynPopup.Show(this);
    }

    private void OpenTipDensityDynamics()
    {
        if (_tipDensityDynPopup != null) { _tipDensityDynPopup.Activate(); return; }
        _tipDensityDynPopup = new DynamicsPopupWindow("Brush Density", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.TipDensity), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.TipDensity = BrushDynamics.ToCurveOption(dyn)) });
        });
        _tipDensityDynPopup.Closed += (_, _) => _tipDensityDynPopup = null;
        PositionPopup(_tipDensityDynPopup);
        _tipDensityDynPopup.Show(this);
    }

    private void OpenTipThicknessDynamics()
    {
        if (_tipThicknessDynPopup != null) { _tipThicknessDynPopup.Activate(); return; }
        _tipThicknessDynPopup = new DynamicsPopupWindow("Thickness", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.TipThickness), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.TipThickness = BrushDynamics.ToCurveOption(dyn)) });
        });
        _tipThicknessDynPopup.Closed += (_, _) => _tipThicknessDynPopup = null;
        PositionPopup(_tipThicknessDynPopup);
        _tipThicknessDynPopup.Show(this);
    }

    private void OpenScatterDynamics()
    {
        if (_scatterDynPopup != null) { _scatterDynPopup.Activate(); return; }
        _scatterDynPopup = new DynamicsPopupWindow("Scatter", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.Scatter), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.Scatter = BrushDynamics.ToCurveOption(dyn)) });
        });
        _scatterDynPopup.Closed += (_, _) => _scatterDynPopup = null;
        PositionPopup(_scatterDynPopup);
        _scatterDynPopup.Show(this);
    }

    private void OpenRotationDynamics()
    {
        if (_rotationDynPopup != null) { _rotationDynPopup.Activate(); return; }
        _rotationDynPopup = new DynamicsPopupWindow("Rotation", BrushDynamics.ToParameterDynamics(_brushPreset.Dynamics.Rotation), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.Rotation = BrushDynamics.ToCurveOption(dyn)) });
        });
        _rotationDynPopup.Closed += (_, _) => _rotationDynPopup = null;
        PositionPopup(_rotationDynPopup);
        _rotationDynPopup.Show(this);
    }

    private void PositionPopup(Window popup)
    {
        if (Position.X + Width + 280 < Screens.Primary?.WorkingArea.Width)
            popup.Position = new PixelPoint(Position.X + (int)Width + 4, Position.Y);
        else
            popup.Position = new PixelPoint(Math.Max(0, Position.X - 280), Position.Y);
    }

    private void CommitMainTip(IBrushTip tip)
    {
        var tipData = BrushTipData.FromTip(tip);
        if (tipData.Kind != BrushTipStorageKind.EmbeddedPng)
            _lastProceduralTip = tipData.DeepClone();
        Commit(p =>
        {
            if (tipData.Kind != BrushTipStorageKind.EmbeddedPng)
                return p with { Tip = tip, Tips = PreserveMaterialTipsWithActiveFirst(p) };

            var tips = MaterialTipsFor(p)
                .Where(t => !TipDataEquals(t, tipData))
                .Select(t => t.DeepClone())
                .Append(tipData.DeepClone())
                .ToList();
            return p with { Tip = CreateGraphTipFromTipData(tipData), Tips = tips };
        });
        if (_activeCategory == 4)
            _contentHost.Child = WrapContent(BuildBrushTipContent());
        else if (_activeCategory == 3)
            _contentHost.Child = WrapContent(BuildBrushShapeContent());
    }

    private void RememberProceduralTip(IBrushTip tip)
    {
        if (IsMaterialTip(tip)) return;
        if (!IsGraphTip(tip)) return;
        _lastProceduralTip = BrushTipData.FromTip(tip).DeepClone();
    }

    private IBrushTip RestoreProceduralTip()
    {
        if (_lastProceduralTip is { Kind: BrushTipStorageKind.NodeGraph } remembered)
            return remembered.CreateTip();
        if (_brushPreset.Shape != null)
            return new ProceduralBrushTip(_brushPreset.Shape.Shape, _brushPreset.Shape.AspectRatio);
        return new ProceduralBrushTip();
    }

    private static IReadOnlyList<BrushTipData> PreserveMaterialTipsWithActiveFirst(BrushPreset preset)
    {
        var tips = preset.Tips.Select(t => t.DeepClone()).ToList();
        if (ActiveMaterialTipData(preset) is { Kind: BrushTipStorageKind.EmbeddedPng } active)
        {
            tips = tips.Where(t => !TipDataEquals(t, active)).ToList();
            tips.Insert(0, active.DeepClone());
        }
        return tips;
    }

    private static IReadOnlyList<BrushTipData> MaterialTipsFor(BrushPreset preset)
        => BrushMaterialTips.ForPreset(preset);

    private static bool IsGraphTip(IBrushTip tip)
        => tip is ProceduralBrushTip or NodeBrushTip;

    private static bool IsMaterialTip(IBrushTip tip)
        => tip is ImageBrushTip || tip is NodeBrushTip node && node.Graph.TryGetDirectImageSampler(out _);

    private static BrushTipData? ActiveMaterialTipData(BrushPreset preset)
        => BrushMaterialTips.ActiveEmbedded(preset);

    private static IBrushTip CreateGraphTipFromTipData(BrushTipData data)
    {
        data = BrushMaterialTips.NormalizeTip(data);
        return data.Kind == BrushTipStorageKind.EmbeddedPng && data.PngBytes.Length > 0
            ? new NodeBrushTip(BrushTipNodeGraph.FromImageTip(data.PngBytes, data.Id))
            : data.CreateTip();
    }

    private static ProceduralBrushTip? BuiltInProceduralTipFor(IBrushTip tip)
        => tip switch
        {
            ProceduralBrushTip proc => proc,
            NodeBrushTip => null,
            _ => null
        };

    private static bool TipDataEquals(BrushTipData a, BrushTipData b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.Kind == BrushTipStorageKind.Procedural)
            return a.Shape == b.Shape && Math.Abs(a.AspectRatio - b.AspectRatio) < 0.0001f;
        if (a.Kind == BrushTipStorageKind.NodeGraph)
            return a.NodeGraph?.CacheKey() == b.NodeGraph?.CacheKey();
        return a.PngBytes.AsSpan().SequenceEqual(b.PngBytes);
    }

    private static PathIcon MaterialIcon(string pathData, double size) =>
        Icons.Make(pathData, size, new SolidColorBrush(Color.Parse(TextMuted)));

    // ── Sync ──────────────────────────────────────────────────────────────────

    private void WireSliderEvents()
    {
        WireSlider(_sizeSlider, v => Commit(p => p with { Size = v }));
        WireSlider(_opacitySlider, v => Commit(p => p with { Opacity = v }));
        WireSlider(_flowSlider, v => Commit(p => p with { Flow = v }));
        WireSlider(_hardnessSlider, v => Commit(p => p with { Hardness = v }));
        WireSlider(_spacingSlider, v => Commit(p => p with { Spacing = v }));
        _autoSpacingCheck.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            Commit(p => p with { AutoSpacingActive = _autoSpacingCheck.IsChecked ?? true });
        };
        WireSlider(_smoothingSlider, v => Commit(p => p with { Smoothing = v }));
        WireSlider(_grainSlider, v => Commit(p => p with { Grain = v }));
        WireSlider(_angleSlider, v => Commit(p => p with { Angle = v }));
        WireSlider(_colorStretchSlider, v => Commit(p => p with { ColorStretch = v }));
        WireSlider(_blurAmountSlider, v => Commit(p => p with { BlurAmount = v }));
        WireSlider(_amountOfPaintSlider, v => Commit(p => p with { AmountOfPaint = v }));
        WireSlider(_densityOfPaintSlider, v => Commit(p => p with { DensityOfPaint = v }));
        WireSlider(_tipDensitySlider, v => Commit(p => p with { TipDensity = v }));
    }

    private void Commit(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncing) return;
        var generation = _presetSyncGeneration;
        _brushPreset = update(_brushPreset);
        _preview.Brush = _brushPreset with { Color = _activePaintColor };
        _preview.InvalidateBitmap();
        _onChange(_toolPreset, _isBrushTool && generation == _presetSyncGeneration ? update : null);
    }

    public void SyncFromPreset(BrushPreset preset)
    {
        CloseDynamicsPopups();
        _syncing = true;
        _presetSyncGeneration++;
        _brushPreset = preset;
        RememberProceduralTip(preset.Tip);

        _sizeSlider.Value = Math.Clamp(preset.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
        _opacitySlider.Value = Math.Clamp(preset.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
        _flowSlider.Value = Math.Clamp(preset.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
        _hardnessSlider.Value = Math.Clamp(preset.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
        _spacingSlider.Value = Math.Clamp(preset.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
        _autoSpacingCheck.IsChecked = preset.AutoSpacingActive;
        _smoothingSlider.Value = Math.Clamp(preset.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
        if (_speedAdaptiveCheck != null)
            _speedAdaptiveCheck.IsChecked = preset.SpeedAdaptiveStabilizer;
        _grainSlider.Value = Math.Clamp(preset.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
        _angleSlider.Value = Math.Clamp(preset.Angle, _angleSlider.Minimum, _angleSlider.Maximum);
        StylizeToggle(_mixToggle, preset.ColorMix);
        _colorStretchSlider.Value = Math.Clamp(preset.ColorStretch, _colorStretchSlider.Minimum, _colorStretchSlider.Maximum);
        _blurAmountSlider.Value = Math.Clamp(preset.BlurAmount, _blurAmountSlider.Minimum, _blurAmountSlider.Maximum);
        _amountOfPaintSlider.Value = Math.Clamp(preset.AmountOfPaint, _amountOfPaintSlider.Minimum, _amountOfPaintSlider.Maximum);
        _densityOfPaintSlider.Value = Math.Clamp(preset.DensityOfPaint, _densityOfPaintSlider.Minimum, _densityOfPaintSlider.Maximum);
        _tipDensitySlider.Value = Math.Clamp(preset.TipDensity, _tipDensitySlider.Minimum, _tipDensitySlider.Maximum);
        if (_blendModeCombo != null) _blendModeCombo.SelectedItem = preset.BlendMode;
        if (_mixingModeCombo != null) _mixingModeCombo.SelectedItem = preset.MixingMode;
        UpdateSmudgeModeButtons(preset.SmudgeMode);
        if (_aaLevelCombo != null) _aaLevelCombo.SelectedIndex = HardnessToLevel(preset.Hardness);

        if (_textureFileLabel != null)
            _textureFileLabel.Text = preset.Texture != null ? Path.GetFileName(preset.Texture) : "None";

        HighlightActiveCategory();
        if (_activeCategory == 4)
            _contentHost.Child = WrapContent(BuildBrushTipContent());
        else if (_activeCategory == 3)
            _contentHost.Child = WrapContent(BuildBrushShapeContent());

        _activePaintColor = preset.Color;
        _preview.Brush = preset;
        _preview.InvalidateBitmap();
        Title = $"Edit Brush — {preset.Name}";

        _syncing = false;
    }

    public void UpdatePreviewColor(Color color)
    {
        _activePaintColor = color;
        if (_preview.Brush != null)
        {
            _preview.Brush = _preview.Brush with { Color = color };
            _preview.InvalidateBitmap();
        }
    }

    public void SyncFromToolPreset(ToolPreset preset)
    {
        _syncing = true;
        _toolPreset = preset;
        // For non-brush tools, rebuild the generic panels with updated values
        if (!_isBrushTool && _genericPanels != null)
        {
            for (int i = 0; i < _categories.Length; i++)
                _genericPanels[i] = WrapContent(BuildGenericCategoryContent(i));
            // Refresh current category view
            if (_activeCategory < _genericPanels.Length)
                _contentHost.Child = _genericPanels[_activeCategory];
        }
        Title = $"Tool Properties — {preset.Name}";
        _syncing = false;
    }

    public bool CanSyncToolPreset(ToolPreset preset)
    {
        var isBrushTool = preset.OutputProcess == OutputProcessType.DirectDraw;
        if (isBrushTool != _isBrushTool) return false;
        if (_isBrushTool) return true;
        return preset.InputProcess == _toolPreset.InputProcess
            && preset.OutputProcess == _toolPreset.OutputProcess;
    }


    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScrubSlider MkSlider(double min, double max, double value, string tip)
        => ScrubSliderFactory.Create(min, max, value, tip);

    private static BrushDynamics WithDynamics(BrushDynamics source, Action<BrushDynamics> update)
    {
        var dynamics = source.Clone();
        update(dynamics);
        return dynamics;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin = new Thickness(0, 6, 0, 2),
        LetterSpacing = 0.5
    };

    private static void StyleCombo(ComboBox combo)
    {
        combo.Background = new SolidColorBrush(Color.Parse(Bg2));
        combo.Foreground = new SolidColorBrush(Color.Parse(TextPrimary));
        combo.BorderBrush = new SolidColorBrush(Color.Parse(Stroke));
        combo.BorderThickness = new Thickness(1);
        combo.CornerRadius = new CornerRadius(5);
        combo.Padding = new Thickness(8, 0);
    }

    private static Button SmBtn(string label)
    {
        var b = new Button
        {
            Content = label,
            Height = 24,
            Padding = new Thickness(8, 0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "outline" }
        };
        return b;
    }

    private static string FormatV(double v, string fmt) => fmt switch
    {
        "px" => $"{v:0}px",
        "%" => $"{v * 100:0}%",
        "f1" => $"{v:0.0}",
        "f2" => $"{v:0.00}",
        _ => $"{v:0.##}"
    };

    private void WireSlider(ScrubSlider slider, Action<double> onChange)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (_syncing || e.Property != RangeBase.ValueProperty || slider.IsScrubbing)
                return;
            onChange(slider.Value);
        };
        slider.ScrubCompleted += (_, v) =>
        {
            if (!_syncing)
                onChange(v);
        };
    }

    // ── Blend mode row ────────────────────────────────────────────────────────
    private ComboBox? _blendModeCombo;

    private Control BuildBlendModeRow()
    {
        _blendModeCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                SKBlendMode.SrcOver, SKBlendMode.Multiply, SKBlendMode.Screen, SKBlendMode.Overlay,
                SKBlendMode.Darken, SKBlendMode.Lighten, SKBlendMode.ColorDodge, SKBlendMode.ColorBurn,
                SKBlendMode.HardLight, SKBlendMode.SoftLight, SKBlendMode.Difference, SKBlendMode.Exclusion,
                SKBlendMode.DstOut, SKBlendMode.Clear
            },
            SelectedItem = _brushPreset.BlendMode,
            FontSize = 11,
            MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        StyleCombo(_blendModeCombo);
        _blendModeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncing || _blendModeCombo.SelectedItem is not SKBlendMode mode)
                return;
            Commit(p => p with { BlendMode = mode });
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1), MinHeight = 24 };
        var lbl = new TextBlock
        {
            Text = "Blend",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(_blendModeCombo);
        return row;
    }

    // ── Mixing mode row ───────────────────────────────────────────────────────
    private ComboBox? _mixingModeCombo;
    private Button? _blendBtn, _smearBtn, _smudgeBtn;

    private Control BuildMixingModeRow()
    {
        _mixingModeCombo = new ComboBox
        {
            ItemsSource = new[] { MixingMode.Standard, MixingMode.Perceptual },
            SelectedItem = _brushPreset.MixingMode,
            FontSize = 11,
            MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        StyleCombo(_mixingModeCombo);
        _mixingModeCombo.SelectionChanged += (_, _) =>
        {
            if (_syncing || _mixingModeCombo.SelectedItem is not MixingMode mode)
                return;
            Commit(p => p with { MixingMode = mode });
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1), MinHeight = 24 };
        var lbl = new TextBlock
        {
            Text = "Mixing",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(_mixingModeCombo);
        return row;
    }

    private Control BuildMixToggleRow(string? toolPropId = null)
    {
        _mixToggle = MkToggleBtn("Color mixing", _brushPreset.ColorMix, () =>
        {
            if (_syncing) return;
            var enabled = !_brushPreset.ColorMix;
            Commit(p => p with { ColorMix = enabled });
            StylizeToggle(_mixToggle, enabled);
        });
        var row = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 1),
            MinHeight = 24,
            Children =
            {
                new TextBlock { Text = "Color mixing", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), Width = 88, VerticalAlignment = VerticalAlignment.Center },
                _mixToggle
            }
        };
        if (toolPropId != null) return AddEyeButton(row, toolPropId);
        return row;
    }

    private Control BuildSmudgeModeRow(string? toolPropId = null)
    {
        _blendBtn = MkToggleBtn("Blend", _brushPreset.SmudgeMode == SmudgeMode.Blend, () => SetSmudgeMode(SmudgeMode.Blend));
        _smearBtn = MkToggleBtn("Smear", _brushPreset.SmudgeMode == SmudgeMode.Smear, () => SetSmudgeMode(SmudgeMode.Smear));
        _smudgeBtn = MkToggleBtn("Running color", _brushPreset.SmudgeMode == SmudgeMode.Smudge, () => SetSmudgeMode(SmudgeMode.Smudge));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(_blendBtn);
        panel.Children.Add(_smearBtn);
        panel.Children.Add(_smudgeBtn);
        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 1),
            MinHeight = 24,
            Children = {
                new TextBlock { Text = "Mode", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), Width = 88, VerticalAlignment = VerticalAlignment.Center },
                panel
            }
        };
        if (toolPropId != null) return AddEyeButton(row, toolPropId);
        return row;
    }

    private void SetSmudgeMode(SmudgeMode mode)
    {
        if (_syncing) return;
        Commit(p => p with { SmudgeMode = mode });
        UpdateSmudgeModeButtons(mode);
    }

    private void UpdateSmudgeModeButtons(SmudgeMode mode)
    {
        StylizeToggle(_blendBtn, mode == SmudgeMode.Blend);
        StylizeToggle(_smearBtn, mode == SmudgeMode.Smear);
        StylizeToggle(_smudgeBtn, mode == SmudgeMode.Smudge);
    }

    private static void StylizeToggle(Button? btn, bool active)
    {
        if (btn == null) return;
        btn.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2));
        btn.BorderBrush = new SolidColorBrush(Color.Parse(active ? SelectionBorder : Stroke));
        btn.Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextSecondary));
    }

    // ── Antialiasing level row ────────────────────────────────────────────────
    private ComboBox? _aaLevelCombo;

    private Control BuildAntialiasingLevelRow(string? toolPropId = null)
    {
        var levels = new[] { "Pixel Art", "Low", "Medium", "High" };
        _aaLevelCombo = new ComboBox
        {
            ItemsSource = levels,
            SelectedIndex = HardnessToLevel(_brushPreset.Hardness),
            FontSize = 11,
            MinHeight = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        StyleCombo(_aaLevelCombo);
        _aaLevelCombo.SelectionChanged += (_, _) =>
        {
            if (_syncing)
                return;
            var level = _aaLevelCombo.SelectedIndex;
            var hardness = LevelToHardness(level);
            Commit(p => p with { Hardness = hardness });
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1), MinHeight = 24 };
        var lbl = new TextBlock
        {
            Text = "Quality",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(_aaLevelCombo);
        if (toolPropId != null) return AddEyeButton(row, toolPropId);
        return row;
    }

    private static int HardnessToLevel(double hardness) => hardness switch
    {
        >= 0.95 => 0, // Pixel Art
        >= 0.6 => 1, // Low
        >= 0.3 => 2, // Medium
        _ => 3, // High
    };

    private static double LevelToHardness(int level) => level switch
    {
        0 => 1.0,   // Pixel Art
        1 => 0.65,  // Low
        2 => 0.35,  // Medium
        _ => 0.05,  // High
    };
}
