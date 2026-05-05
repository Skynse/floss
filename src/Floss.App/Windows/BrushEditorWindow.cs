using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using SkiaSharp;

namespace Floss.App;

public sealed class BrushEditorWindow : Window
{
    private const string Bg0 = "#0d0f14";
    private const string Bg1 = "#13151a";
    private const string Bg2 = "#1a1c22";
    private const string BgSidebar = "#0f1117";
    private const string Stroke = "#2b303b";
    private const string TextPrimary = "#d7dde8";
    private const string TextSecondary = "#A0AAB4";
    private const string TextMuted = "#6f7888";
    private const string Accent = "#3d6fd8";
    private const string AccentSoft = "#22355f";

    // ── Categories ────────────────────────────────────────────────────────────
    private static readonly string[] Categories =
        ["Brush Size", "Ink", "Anti-aliasing", "Brush Tip", "Stroke", "Texture"];

    private int _activeCategory;
    private readonly Button[] _catButtons;
    private readonly Border _contentHost = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private BrushPreset _preset;
    private readonly Action<BrushPreset> _onChange;
    private readonly BrushStrokePreview _preview = new() { Height = 64 };
    private bool _syncing;

    // ── Stamp layers (Texture category) ──────────────────────────────────────
    private readonly List<StampLayer> _stampLayers = [];
    private StackPanel _stampPanel = null!;

    // ── Sliders ───────────────────────────────────────────────────────────────
    private readonly Slider _sizeSlider = MkSlider(0.5, 300, 8, "Brush size in pixels");
    private readonly Slider _opacitySlider = MkSlider(0.01, 1, 1.0, "Maximum opacity per stamp");
    private readonly Slider _flowSlider = MkSlider(0.01, 1, 1.0, "Paint buildup per dab");
    private readonly Slider _colorMixSlider = MkSlider(0, 1, 0.0, "Canvas color pickup per dab (0=pure brush, 1=full mix)");
    private readonly Slider _colorLoadSlider = MkSlider(0, 1, 1.0, "Paint reload rate (1=always fresh, 0=color accumulates)");
    private readonly Slider _colorStretchSlider = MkSlider(0, 1, 0.5, "Color stretch intensity (0=gentle, 1=aggressive)");
    private readonly Slider _blurAmountSlider = MkSlider(0, 1, 0.0, "Blur during mixing (0=none, 1=full)");
    private readonly Slider _amountOfPaintSlider = MkSlider(0, 1, 1.0, "Amount of paint deposited (0=none, 1=full)");
    private readonly Slider _densityOfPaintSlider = MkSlider(0, 1, 1.0, "Paint density (0=thin, 1=thick)");
    private readonly Slider _hardnessSlider = MkSlider(0, 1, 0.9, "Edge softness (anti-aliasing)");
    private readonly Slider _spacingSlider = MkSlider(0.01, 1, 0.1, "Stamp interval as fraction of size");
    private readonly Slider _smoothingSlider = MkSlider(0, 0.95, 0.3, "Input stabilization");
    private readonly Slider _angleSlider = MkSlider(0, 360, 0, "Base angle in degrees");
    private readonly Slider _grainSlider = MkSlider(0, 1, 0.0, "Noise texture strength");
    private readonly Slider _tipDensitySlider = MkSlider(0, 1, 1.0, "Brush tip density (0=none, 1=full)");

    // ── Open dynamics popups ──────────────────────────────────────────────────
    private DynamicsPopupWindow? _sizeDynPopup;
    private DynamicsPopupWindow? _opacDynPopup;
    private DynamicsPopupWindow? _flowDynPopup;
    private DynamicsPopupWindow? _hardnessDynPopup;
    private DynamicsPopupWindow? _spacingDynPopup;
    private DynamicsPopupWindow? _scatterDynPopup;
    private DynamicsPopupWindow? _rotationDynPopup;
    private AngleDynamicsPopupWindow? _angleDynPopup;

    // ── Cached category panels (built once to avoid re-parenting sliders) ────
    private ScrollViewer _brushSizePanel = null!;
    private ScrollViewer _inkPanel = null!;
    private ScrollViewer _aaPanel = null!;
    private ScrollViewer _strokePanel = null!;
    private ScrollViewer _texturePanel = null!;

    // ── Constructor ───────────────────────────────────────────────────────────

    public BrushEditorWindow(BrushPreset preset, Action<BrushPreset> onChange)
    {
        _preset = preset;
        _onChange = onChange;

        Width = 420;
        Height = 560;
        CanResize = true;
        MinWidth = 360;
        MinHeight = 420;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        Title = $"Edit Brush — {preset.Name}";
        ShowInTaskbar = false;

        _catButtons = Categories.Select((_, i) => MakeCatBtn(i)).ToArray();
        _stampPanel = new StackPanel { Spacing = 4 };

        // Build slider-containing panels exactly once so sliders are never
        // re-parented when the user switches categories.
        _brushSizePanel = WrapContent(BuildBrushSizeContent());
        _inkPanel = WrapContent(BuildInkContent());
        _aaPanel = WrapContent(BuildAntiAliasingContent());
        _strokePanel = WrapContent(BuildStrokeContent());
        _texturePanel = WrapContent(BuildTextureContent());

        Content = BuildShell();
        HighlightActiveCategory();
        SelectCategory(0);
        SyncFromPreset(preset);
        WireSliderEvents();
    }

    // ── Shell ─────────────────────────────────────────────────────────────────

    private Control BuildShell()
    {
        // Sidebar
        var sidebar = new Border
        {
            Width = 90,
            Background = new SolidColorBrush(Color.Parse(BgSidebar)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = BuildSidebar()
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(_contentHost, 1);
        body.Children.Add(sidebar);
        body.Children.Add(_contentHost);

        // Preview at top
        DockPanel.SetDock(_preview, Dock.Top);
        var dp = new DockPanel { LastChildFill = true };
        dp.Children.Add(_preview);
        dp.Children.Add(body);
        return dp;
    }

    private Control BuildSidebar()
    {
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var btn in _catButtons) stack.Children.Add(btn);
        return stack;
    }

    private Button MakeCatBtn(int index)
    {
        var btn = new Button
        {
            Content = Categories[index],
            Height = 24,
            Padding = new Thickness(8, 0),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
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
                ? new SolidColorBrush(Color.Parse(AccentSoft))
                : new SolidColorBrush(Colors.Transparent);
            _catButtons[i].Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
        }
    }

    private void SelectCategory(int index)
    {
        _activeCategory = index;
        HighlightActiveCategory();
        // Close any open dynamics popups before switching — prevents crashes
        // from stale references to re-parented sliders.
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
        _angleDynPopup?.Close();
        _angleDynPopup = null;

        // BrushTip is rebuilt fresh (preset-dependent, no shared sliders).
        // All others are pre-built cached panels to avoid re-parenting sliders.
        _contentHost.Child = index switch
        {
            0 => _brushSizePanel,
            1 => _inkPanel,
            2 => _aaPanel,
            3 => WrapContent(BuildBrushTipContent()),
            4 => _strokePanel,
            5 => _texturePanel,
            _ => new Border()
        };
    }

    private static ScrollViewer WrapContent(Control content) => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(8, 6, 8, 8),
        Content = content
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
            PlainSliderRow("Mix",  _colorMixSlider,  "%", "brush.colorMix"),
            PlainSliderRow("Load", _colorLoadSlider, "%", "brush.colorLoad"),
            PlainSliderRow("Stretch", _colorStretchSlider, "%", "brush.colorStretch"),
            PlainSliderRow("Blur", _blurAmountSlider, "%", "brush.blurAmount"),
            BuildMixingModeRow(),
            PlainSliderRow("Amount", _amountOfPaintSlider, "%", "brush.amountOfPaint"),
            PlainSliderRow("Density", _densityOfPaintSlider, "%", "brush.densityOfPaint"),
        }
    };

    private Control BuildAntiAliasingContent() => new StackPanel
    {
        Spacing = 0,
        Children = { BuildAntialiasingLevelRow() }
    };

    private Control BuildBrushTipContent()
    {
        var mainTip = _stampLayers.Count > 0 ? _stampLayers[0].Tip : _preset.Tip;
        var isProc = mainTip is ProceduralBrushTip;
        var procTip = mainTip as ProceduralBrushTip ?? new ProceduralBrushTip();

        var result = new StackPanel { Spacing = 0 };

        // ── SHAPE section ────────────────────────────────────────────────────────
        // When tip is procedural: shape grid controls Tip directly (no separate clip).
        // When tip is image-based: shape grid controls preset.Shape (the clip mask).

        var gridShapes = new (BrushTipShape shape, string label)[]
        {
            (BrushTipShape.Circle,    "Round"),
            (BrushTipShape.SoftRound, "Soft"),
            (BrushTipShape.Flat,      "Flat"),
            (BrushTipShape.Ellipse,   "Oval"),
            (BrushTipShape.Rectangle, "Square"),
            (BrushTipShape.Chalk,     "Chalk"),
            (BrushTipShape.Bristle,   "Bristle"),
            (BrushTipShape.Scatter,   "Scatter"),
        };

        var shapeGrid = new WrapPanel { Orientation = Orientation.Horizontal };

        // "Off" cell — only shown when tip is image-based (shape clip is optional then)
        if (!isProc)
        {
            var offActive = _preset.Shape == null;
            var offBtn = MkShapeCell("Off", null, offActive, () => Commit(p => p with { Shape = null }));
            shapeGrid.Children.Add(offBtn);
        }

        BrushTipShape? activeShape = isProc ? procTip.Shape : _preset.Shape?.Shape;

        foreach (var (shape, label) in gridShapes)
        {
            var s = shape;
            var active = activeShape == s;
            Action onClick = isProc
                ? () => CommitMainTip(new ProceduralBrushTip(s, procTip.AspectRatio))
                : () => Commit(p => p with { Shape = new ProceduralBrushTip(s) });
            shapeGrid.Children.Add(MkShapeCell(label, s, active, onClick));
        }

        result.Children.Add(SectionHeader("SHAPE"));
        result.Children.Add(shapeGrid);

        if (isProc && procTip.Shape == BrushTipShape.Ellipse)
        {
            var aspectSlider = MkSlider(0.1, 1.0, Math.Clamp(procTip.AspectRatio, 0.1, 1.0), "Aspect ratio (width/height)");
            WireSlider(aspectSlider, v => CommitMainTip(new ProceduralBrushTip(procTip.Shape, (float)v)));
            result.Children.Add(SectionHeader("ASPECT RATIO"));
            result.Children.Add(PlainSliderRowRaw(aspectSlider, "f2"));
        }

        // ── TIP TEXTURE section ──────────────────────────────────────────────────
        // Shows the raw texture bitmap. For procedural tips this is just the shape
        // itself (no separate texture). For image tips, shows the imported image.

        var previewBmp = BuildTipPreview(mainTip);
        var previewImg = new Image
        {
            Source = previewBmp,
            Width = 48, Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previewBtn = new Button
        {
            Width = 54, Height = 54,
            Padding = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = previewImg
        };
        previewBtn.Click += (_, _) => OpenTipBrowser();

        var browseBtn = SmBtn("Browse...");
        browseBtn.Click += (_, _) => OpenTipBrowser();

        var tipBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tipBtnRow.Children.Add(browseBtn);
        if (!isProc)
        {
            var clearBtn = SmBtn("Clear");
            clearBtn.Click += (_, _) =>
            {
                var shape = _preset.Shape?.Shape ?? BrushTipShape.Circle;
                CommitMainTip(new ProceduralBrushTip(shape));
                Commit(p => p with { Shape = null });
            };
            tipBtnRow.Children.Add(clearBtn);
        }

        var tipRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 2, 0, 4),
            Children = { previewBtn, tipBtnRow }
        };

        result.Children.Add(SectionHeader("TIP TEXTURE"));
        result.Children.Add(tipRow);

        return result;
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
                Text = label, FontSize = 8,
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
            Width = 54, Height = 58,
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
        var browser = new BrushTipBrowserWindow(this, tip => CommitMainTip(tip));
        browser.Show(this);
    }

    private static Bitmap? BuildTipPreview(IBrushTip tip)
    {
        const int size = 48;
        if (tip is ImageBrushTip img)
        {
            try { using var ms = new MemoryStream(img.GetPngBytes()); return new Bitmap(ms); }
            catch { return null; }
        }
        return RenderMaskThumbnail(tip, size);
    }

    private static Bitmap? RenderTipThumbnail(BrushTipShape shape)
    {
        var tip = new ProceduralBrushTip(shape, shape == BrushTipShape.Ellipse ? 0.45f : 1.0f);
        return RenderMaskThumbnail(tip, 40);
    }

    private static Bitmap? RenderMaskThumbnail(IBrushTip tip, int size)
    {
        try
        {
            const float hardness = 0.85f;
            using var mask = tip.GenerateMask(size - 6, hardness);
            var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(new SKColor(0x28, 0x24, 0x28));
            using var colorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn);
            using var paint = new SKPaint { ColorFilter = colorFilter, IsAntialias = true };
            canvas.Save();
            canvas.Translate(3f, 3f);
            canvas.DrawBitmap(mask, 0, 0, paint);
            canvas.Restore();
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
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
        catch { }
        return list;
    }

    private static Button MkToggleBtn(string label, bool active, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Height = 20,
            Padding = new Thickness(8, 0),
            FontSize = 10,
            Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted)),
            CornerRadius = new CornerRadius(3)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Control BuildStrokeContent() => new StackPanel
    {
        Spacing = 0,
        Children =
        {
            DynSliderRow("Spacing",   _spacingSlider,   "%", () => OpenSpacingDynamics(), "brush.spacing"),
            PlainSliderRow("Smoothing", _smoothingSlider, "%", "brush.smoothing"),
            DynSliderRow("Angle", _angleSlider, "°", () => openAngleDynamics(), "brush.angle")
        }
    };

    private Control BuildTextureContent()
    {
        var addShapeBtn = SmBtn("+ Shape");
        var addPngBtn = SmBtn("+ PNG");
        addShapeBtn.Click += (_, _) => AddShapeLayer();
        addPngBtn.Click += async (_, _) => await AddPngLayerAsync();

        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(addPngBtn, Dock.Right);
        DockPanel.SetDock(addShapeBtn, Dock.Right);
        headerRow.Children.Add(SectionHeader("STAMP LAYERS"));
        headerRow.Children.Add(addPngBtn);
        headerRow.Children.Add(new Border { Width = 4 });
        headerRow.Children.Add(addShapeBtn);

        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                PlainSliderRow("Grain", _grainSlider, "%", "brush.grain"),
                PlainSliderRow("Tip Density", _tipDensitySlider, "%", "brush.tipDensity"),
                new Border { Height = 4 },
                headerRow,
                _stampPanel
            }
        };
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private Control DynSliderRow(string label, Slider slider, string fmt, Action openDyn, string? toolPropId = null)
    {
        var dynBtn = new Button
        {
            Content = MaterialIcon(Icons.TuneVertical, 14),
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(dynBtn, "Edit dynamics curve");
        dynBtn.Click += (_, _) => openDyn();

        return BuildSliderRow(label, slider, fmt, dynBtn, toolPropId);
    }

    private Control PlainSliderRow(string label, Slider slider, string fmt, string? toolPropId = null)
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
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
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

    private static Control PlainSliderRowRaw(Slider slider, string fmt)
    {
        var val = MkValLabel(slider.Value, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) val.Text = FormatV(slider.Value, fmt);
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);
        row.Children.Add(slider);
        return row;
    }

    private static Control BuildSliderRow(string label, Slider slider, string fmt, Control? extra, string? toolPropId = null)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Width = 60,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var val = MkValLabel(slider.Value, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) val.Text = FormatV(slider.Value, fmt);
        };

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(val, Dock.Right);

        // Eye toggle for tool property docker visibility
        if (toolPropId != null)
        {
            bool visible = App.Config.ToolPropertyDockerVisibility.TryGetValue(toolPropId, out var v) ? v : false;
            var eyeBtn = new Button
            {
                Content = visible ? "◉" : "○",
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                FontSize = 10,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(Bg2)),
                Foreground = new SolidColorBrush(Color.Parse(visible ? Accent : TextMuted)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(eyeBtn, visible ? "Hide from tool property docker" : "Show in tool property docker");
            eyeBtn.Click += (_, _) =>
            {
                var newVisible = !App.Config.ToolPropertyDockerVisibility.TryGetValue(toolPropId, out var cur) || !cur;
                App.Config.ToolPropertyDockerVisibility[toolPropId] = newVisible;
                App.Config.Save();
                AppConfig.NotifyToolPropertyVisibilityChanged();
                eyeBtn.Content = newVisible ? "◉" : "○";
                eyeBtn.Foreground = new SolidColorBrush(Color.Parse(newVisible ? Accent : TextMuted));
                ToolTip.SetTip(eyeBtn, newVisible ? "Hide from tool property docker" : "Show in tool property docker");
            };
            DockPanel.SetDock(eyeBtn, Dock.Right);
            row.Children.Add(lbl);
            row.Children.Add(eyeBtn);
            row.Children.Add(new Border { Width = 4 });
        }
        else
        {
            row.Children.Add(lbl);
        }

        if (extra != null)
        {
            DockPanel.SetDock(extra, Dock.Right);
            row.Children.Add(extra);
            row.Children.Add(new Border { Width = 4 });
        }
        row.Children.Add(val);
        row.Children.Add(slider);
        return row;
    }

    private static TextBlock MkValLabel(double value, string fmt) => new()
    {
        Text = FormatV(value, fmt),
        FontSize = 9,
        Width = 32,
        TextAlignment = Avalonia.Media.TextAlignment.Right,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Consolas, Courier New, monospace")
    };

    // ── Dynamics popups ───────────────────────────────────────────────────────

    private void OpenSizeDynamics()
    {
        if (_sizeDynPopup != null) { _sizeDynPopup.Activate(); return; }
        _sizeDynPopup = new DynamicsPopupWindow("Brush Size", _preset.SizeDynamics, dyn =>
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
        _opacDynPopup = new DynamicsPopupWindow("Opacity", _preset.OpacityDynamics, dyn =>
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
        _flowDynPopup = new DynamicsPopupWindow("Flow", BrushDynamics.ToParameterDynamics(_preset.Dynamics.Flow), dyn =>
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
        _hardnessDynPopup = new DynamicsPopupWindow("Hardness", BrushDynamics.ToParameterDynamics(_preset.Dynamics.Hardness), dyn =>
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
            _preset.BaseAngleSource,
            _preset.AngleJitter,
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
        _spacingDynPopup = new DynamicsPopupWindow("Spacing", BrushDynamics.ToParameterDynamics(_preset.Dynamics.Spacing), dyn =>
        {
            Commit(p => p with { Dynamics = WithDynamics(p.Dynamics, d => d.Spacing = BrushDynamics.ToCurveOption(dyn)) });
        });
        _spacingDynPopup.Closed += (_, _) => _spacingDynPopup = null;
        PositionPopup(_spacingDynPopup);
        _spacingDynPopup.Show(this);
    }

    private void OpenScatterDynamics()
    {
        if (_scatterDynPopup != null) { _scatterDynPopup.Activate(); return; }
        _scatterDynPopup = new DynamicsPopupWindow("Scatter", BrushDynamics.ToParameterDynamics(_preset.Dynamics.Scatter), dyn =>
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
        _rotationDynPopup = new DynamicsPopupWindow("Rotation", BrushDynamics.ToParameterDynamics(_preset.Dynamics.Rotation), dyn =>
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

    // ── Stamp layers ──────────────────────────────────────────────────────────

    private void AddShapeLayer()
    {
        _stampLayers.Add(new StampLayer
        {
            Tip = new ProceduralBrushTip(),
            Blend = _stampLayers.Count == 0 ? StampLayerBlend.Replace : StampLayerBlend.Multiply
        });
        RebuildStampPanel();
        CommitLayers();
    }

    private async Task AddPngLayerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PNG texture",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var mem = new MemoryStream();
        await stream.CopyToAsync(mem);
        _stampLayers.Add(new StampLayer
        {
            Tip = new ImageBrushTip(mem.ToArray()),
            Blend = _stampLayers.Count == 0 ? StampLayerBlend.Replace : StampLayerBlend.Multiply
        });
        RebuildStampPanel();
        CommitLayers();
    }

    private void RebuildStampPanel()
    {
        _stampPanel.Children.Clear();
        for (var i = 0; i < _stampLayers.Count; i++)
            _stampPanel.Children.Add(BuildLayerRow(i));
    }

    private Control BuildLayerRow(int index)
    {
        var layer = _stampLayers[index];

        var icon = MaterialIcon(layer.Tip is ImageBrushTip ? Icons.ImageOutline : Icons.ShapeOutline, 16);
        icon.Width = 18;

        var typeLabel = new TextBlock
        {
            Text = layer.Tip is ImageBrushTip ? "PNG" : "Shape",
            FontSize = 10,
            Width = 34,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var blendBox = new ComboBox
        {
            ItemsSource = Enum.GetNames<StampLayerBlend>(),
            SelectedIndex = (int)layer.Blend,
            FontSize = 10,
            Width = 72,
            Height = 20,
            Padding = new Thickness(4, 0)
        };
        blendBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || blendBox.SelectedIndex < 0) return;
            _stampLayers[index] = _stampLayers[index] with { Blend = (StampLayerBlend)blendBox.SelectedIndex };
            CommitLayers();
        };

        var opacSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = layer.Opacity,
            Width = 48,
            Height = 26,
            MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center
        };
        opacSlider.PropertyChanged += (_, e) =>
        {
            if (_syncing || e.Property != Slider.ValueProperty) return;
            _stampLayers[index] = _stampLayers[index] with { Opacity = (float)opacSlider.Value };
            CommitLayers();
        };

        var deleteBtn = new Button
        {
            Content = MaterialIcon(Icons.Close, 13),
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            IsEnabled = _stampLayers.Count > 1
        };
        deleteBtn.Click += (_, _) =>
        {
            _stampLayers.RemoveAt(index);
            RebuildStampPanel();
            CommitLayers();
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, 0, 0, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { icon, typeLabel, blendBox, opacSlider, deleteBtn }
            }
        };
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
        WireSlider(_smoothingSlider, v => Commit(p => p with { Smoothing = v }));
        WireSlider(_grainSlider, v => Commit(p => p with { Grain = v }));
        WireSlider(_angleSlider, v => Commit(p => p with { Angle = v }));
        WireSlider(_colorMixSlider,  v => Commit(p => p with { ColorMix  = v }));
        WireSlider(_colorLoadSlider, v => Commit(p => p with { ColorLoad = v }));
        WireSlider(_colorStretchSlider, v => Commit(p => p with { ColorStretch = v }));
        WireSlider(_blurAmountSlider, v => Commit(p => p with { BlurAmount = v }));
        WireSlider(_amountOfPaintSlider, v => Commit(p => p with { AmountOfPaint = v }));
        WireSlider(_densityOfPaintSlider, v => Commit(p => p with { DensityOfPaint = v }));
        WireSlider(_tipDensitySlider, v => Commit(p => p with { TipDensity = v }));
    }

    private void Commit(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncing) return;
        _preset = update(_preset);
        _preview.Brush = _preset;
        _preview.InvalidateBitmap();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _onChange(_preset), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void CommitMainTip(IBrushTip tip)
    {
        if (_stampLayers.Count == 0)
        {
            _stampLayers.Add(new StampLayer { Tip = tip, Blend = StampLayerBlend.Replace });
        }
        else
        {
            _stampLayers[0] = _stampLayers[0] with { Tip = tip };
        }
        CommitLayers();
        if (_activeCategory == 3)
            _contentHost.Child = WrapContent(BuildBrushTipContent());
    }

    private void CommitLayers()
    {
        var tip = _stampLayers.Count == 1 && _stampLayers[0].Blend == StampLayerBlend.Replace
            ? _stampLayers[0].Tip
            : (IBrushTip)new CompoundBrushTip(_stampLayers.ToList());
        Commit(p => p with { Tip = tip });
    }

    public void SyncFromPreset(BrushPreset preset)
    {
        _syncing = true;
        _preset = preset;

        _sizeSlider.Value = Math.Clamp(preset.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
        _opacitySlider.Value = Math.Clamp(preset.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
        _flowSlider.Value = Math.Clamp(preset.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
        _hardnessSlider.Value = Math.Clamp(preset.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
        _spacingSlider.Value = Math.Clamp(preset.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
        _smoothingSlider.Value = Math.Clamp(preset.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
        _grainSlider.Value = Math.Clamp(preset.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
        _angleSlider.Value = Math.Clamp(preset.Angle, _angleSlider.Minimum, _angleSlider.Maximum);
        _colorMixSlider.Value  = Math.Clamp(preset.ColorMix,  _colorMixSlider.Minimum,  _colorMixSlider.Maximum);
        _colorLoadSlider.Value = Math.Clamp(preset.ColorLoad, _colorLoadSlider.Minimum, _colorLoadSlider.Maximum);
        _colorStretchSlider.Value = Math.Clamp(preset.ColorStretch, _colorStretchSlider.Minimum, _colorStretchSlider.Maximum);
        _blurAmountSlider.Value = Math.Clamp(preset.BlurAmount, _blurAmountSlider.Minimum, _blurAmountSlider.Maximum);
        _amountOfPaintSlider.Value = Math.Clamp(preset.AmountOfPaint, _amountOfPaintSlider.Minimum, _amountOfPaintSlider.Maximum);
        _densityOfPaintSlider.Value = Math.Clamp(preset.DensityOfPaint, _densityOfPaintSlider.Minimum, _densityOfPaintSlider.Maximum);
        _tipDensitySlider.Value = Math.Clamp(preset.TipDensity, _tipDensitySlider.Minimum, _tipDensitySlider.Maximum);
        if (_blendModeCombo != null) _blendModeCombo.SelectedItem = preset.BlendMode;
        if (_mixingModeCombo != null) _mixingModeCombo.SelectedItem = preset.MixingMode;
        if (_aaLevelCombo != null) _aaLevelCombo.SelectedIndex = HardnessToLevel(preset.Hardness);

        _stampLayers.Clear();
        if (preset.Tip is CompoundBrushTip compound)
            _stampLayers.AddRange(compound.Layers);
        else
            _stampLayers.Add(new StampLayer { Tip = preset.Tip, Blend = StampLayerBlend.Replace });

        _syncing = false;

        // Sync open popup windows
        _sizeDynPopup?.SyncFromDynamics(preset.SizeDynamics);
        _opacDynPopup?.SyncFromDynamics(preset.OpacityDynamics);
        _flowDynPopup?.SyncFromDynamics(BrushDynamics.ToParameterDynamics(preset.Dynamics.Flow));
        _hardnessDynPopup?.SyncFromDynamics(BrushDynamics.ToParameterDynamics(preset.Dynamics.Hardness));
        _spacingDynPopup?.SyncFromDynamics(BrushDynamics.ToParameterDynamics(preset.Dynamics.Spacing));
        _scatterDynPopup?.SyncFromDynamics(BrushDynamics.ToParameterDynamics(preset.Dynamics.Scatter));
        _rotationDynPopup?.SyncFromDynamics(BrushDynamics.ToParameterDynamics(preset.Dynamics.Rotation));
        _angleDynPopup?.SyncState(preset.BaseAngleSource, preset.AngleJitter);

        RebuildStampPanel();
        // Refresh sidebar highlight without rebuilding cached slider panels.
        // Only the Brush Tip panel needs a live rebuild (it reflects preset state).
        HighlightActiveCategory();
        if (_activeCategory == 3)
            _contentHost.Child = WrapContent(BuildBrushTipContent());
        _preview.Brush = preset;
        _preview.InvalidateBitmap();
        Title = $"Edit Brush — {preset.Name}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Slider MkSlider(double min, double max, double value, string tip)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Height = 26,
            MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(s, tip);
        return s;
    }

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
        Margin = new Thickness(0, 3, 0, 2),
        LetterSpacing = 1.2
    };

    private static Button SmBtn(string label)
    {
        var b = new Button
        {
            Content = label,
            Height = 20,
            Padding = new Thickness(6, 0),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
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

    private static void WireSlider(Slider slider, Action<double> onChange)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) onChange(slider.Value);
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
            SelectedItem = _preset.BlendMode,
            FontSize = 11,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _blendModeCombo.SelectionChanged += (_, _) =>
        {
            if (_blendModeCombo.SelectedItem is SKBlendMode mode)
                Commit(p => p with { BlendMode = mode });
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        var lbl = new TextBlock
        {
            Text = "Blend",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(_blendModeCombo);
        return row;
    }

    // ── Mixing mode row ───────────────────────────────────────────────────────
    private ComboBox? _mixingModeCombo;

    private Control BuildMixingModeRow()
    {
        _mixingModeCombo = new ComboBox
        {
            ItemsSource = new[] { MixingMode.Standard, MixingMode.Perceptual },
            SelectedItem = _preset.MixingMode,
            FontSize = 11,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _mixingModeCombo.SelectionChanged += (_, _) =>
        {
            if (_mixingModeCombo.SelectedItem is MixingMode mode)
                Commit(p => p with { MixingMode = mode });
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        var lbl = new TextBlock
        {
            Text = "Mixing",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(_mixingModeCombo);
        return row;
    }

    // ── Antialiasing level row ────────────────────────────────────────────────
    private ComboBox? _aaLevelCombo;

    private Control BuildAntialiasingLevelRow()
    {
        var levels = new[] { "Pixel Art", "Low", "Medium", "High" };
        _aaLevelCombo = new ComboBox
        {
            ItemsSource = levels,
            SelectedIndex = HardnessToLevel(_preset.Hardness),
            FontSize = 11,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _aaLevelCombo.SelectionChanged += (_, _) =>
        {
            var level = _aaLevelCombo.SelectedIndex;
            var hardness = LevelToHardness(level);
            Commit(p => p with { Hardness = hardness });
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        var lbl = new TextBlock
        {
            Text = "Quality",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(_aaLevelCombo);
        return row;
    }

    private static int HardnessToLevel(double hardness) => hardness switch
    {
        >= 0.95 => 0, // Pixel Art
        >= 0.6  => 1, // Low
        >= 0.3  => 2, // Medium
        _       => 3, // High
    };

    private static double LevelToHardness(int level) => level switch
    {
        0 => 1.0,   // Pixel Art
        1 => 0.65,  // Low
        2 => 0.35,  // Medium
        _ => 0.05,  // High
    };
}
