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
using Avalonia.Platform.Storage;
using Floss.App.Brushes;

namespace Floss.App;

public sealed class BrushEditorWindow : Window
{
    private const string Bg0           = "#0d0f14";
    private const string Bg1           = "#13151a";
    private const string Bg2           = "#1a1c22";
    private const string BgSidebar     = "#0f1117";
    private const string Stroke        = "#2b303b";
    private const string TextPrimary   = "#d7dde8";
    private const string TextSecondary = "#A0AAB4";
    private const string TextMuted     = "#6f7888";
    private const string Accent        = "#3d6fd8";
    private const string AccentSoft    = "#22355f";

    // ── Categories ────────────────────────────────────────────────────────────
    private static readonly string[] Categories =
        ["Brush Size", "Ink", "Anti-aliasing", "Brush Tip", "Stroke", "Texture"];

    private int _activeCategory;
    private readonly Button[]         _catButtons;
    private readonly Border           _contentHost = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private BrushPreset _preset;
    private readonly Action<BrushPreset> _onChange;
    private readonly BrushStrokePreview  _preview = new() { Height = 76 };
    private bool _syncing;

    // ── Stamp layers (Texture category) ──────────────────────────────────────
    private readonly List<StampLayer> _stampLayers  = [];
    private StackPanel                _stampPanel   = null!;

    // ── Sliders ───────────────────────────────────────────────────────────────
    private readonly Slider _sizeSlider      = MkSlider(0.5,  300,  8,    "Brush size in pixels");
    private readonly Slider _opacitySlider   = MkSlider(0.01, 1,    1.0,  "Maximum opacity per stamp");
    private readonly Slider _flowSlider      = MkSlider(0.01, 1,    1.0,  "Paint buildup per dab");
    private readonly Slider _hardnessSlider  = MkSlider(0,    1,    0.9,  "Edge softness (anti-aliasing)");
    private readonly Slider _spacingSlider   = MkSlider(0.01, 1,    0.1,  "Stamp interval as fraction of size");
    private readonly Slider _smoothingSlider = MkSlider(0,    0.95, 0.3,  "Input stabilization");
    private readonly Slider _grainSlider     = MkSlider(0,    1,    0.0,  "Noise texture strength");

    // ── Open dynamics popups ──────────────────────────────────────────────────
    private DynamicsPopupWindow? _sizeDynPopup;
    private DynamicsPopupWindow? _opacDynPopup;

    // ── Constructor ───────────────────────────────────────────────────────────

    public BrushEditorWindow(BrushPreset preset, Action<BrushPreset> onChange)
    {
        _preset   = preset;
        _onChange = onChange;

        Width         = 370;
        Height        = 560;
        CanResize     = true;
        MinWidth      = 300;
        MinHeight     = 420;
        Background    = new SolidColorBrush(Color.Parse(Bg1));
        Title         = $"Edit Brush — {preset.Name}";
        ShowInTaskbar = false;

        _catButtons = Categories.Select((_, i) => MakeCatBtn(i)).ToArray();
        _stampPanel = new StackPanel { Spacing = 4 };

        Content = BuildShell();
        SyncFromPreset(preset);
        WireSliderEvents();
    }

    // ── Shell ─────────────────────────────────────────────────────────────────

    private Control BuildShell()
    {
        // Sidebar
        var sidebar = new Border
        {
            Width           = 110,
            Background      = new SolidColorBrush(Color.Parse(BgSidebar)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child           = BuildSidebar()
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(sidebar,      0);
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
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var btn in _catButtons) stack.Children.Add(btn);
        return stack;
    }

    private Button MakeCatBtn(int index)
    {
        var btn = new Button
        {
            Content         = Categories[index],
            Height          = 32,
            Padding         = new Thickness(10, 0),
            FontSize        = 10,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment   = VerticalAlignment.Center,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            Background      = new SolidColorBrush(Colors.Transparent),
            Foreground      = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderThickness = new Thickness(0),
            CornerRadius    = new CornerRadius(0)
        };
        btn.Click += (_, _) => SelectCategory(index);
        return btn;
    }

    private void SelectCategory(int index)
    {
        _activeCategory = index;
        for (var i = 0; i < _catButtons.Length; i++)
        {
            var active = i == index;
            _catButtons[i].Background = active
                ? new SolidColorBrush(Color.Parse(AccentSoft))
                : new SolidColorBrush(Colors.Transparent);
            _catButtons[i].Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
        }
        _contentHost.Child = WrapContent(index switch
        {
            0 => BuildBrushSizeContent(),
            1 => BuildInkContent(),
            2 => BuildAntiAliasingContent(),
            3 => BuildBrushTipContent(),
            4 => BuildStrokeContent(),
            5 => BuildTextureContent(),
            _ => new Border()
        });
    }

    private static ScrollViewer WrapContent(Control content) => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        Padding                       = new Thickness(10, 8, 10, 12),
        Content                       = content
    };

    // ── Category content ──────────────────────────────────────────────────────

    private Control BuildBrushSizeContent() => new StackPanel
    {
        Spacing = 0,
        Children =
        {
            DynSliderRow("Size", _sizeSlider, "px", () => OpenSizeDynamics())
        }
    };

    private Control BuildInkContent() => new StackPanel
    {
        Spacing = 0,
        Children =
        {
            DynSliderRow("Opacity", _opacitySlider, "%", () => OpenOpacityDynamics()),
            PlainSliderRow("Flow",  _flowSlider,    "%")
        }
    };

    private Control BuildAntiAliasingContent() => new StackPanel
    {
        Spacing  = 0,
        Children = { PlainSliderRow("Hardness", _hardnessSlider, "%") }
    };

    private Control BuildBrushTipContent()
    {
        var tip = _preset.Tip as ProceduralBrushTip ?? new ProceduralBrushTip();

        // Shape selector
        var shapeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 4,
            Margin      = new Thickness(0, 0, 0, 8)
        };
        foreach (var shape in Enum.GetValues<BrushTipShape>())
        {
            var s     = shape;
            var isAct = tip.Shape == s;
            var btn   = new Button
            {
                Content         = shape.ToString(),
                Height          = 24,
                Padding         = new Thickness(8, 0),
                FontSize        = 10,
                Background      = new SolidColorBrush(Color.Parse(isAct ? AccentSoft : Bg2)),
                BorderBrush     = new SolidColorBrush(Color.Parse(isAct ? Accent     : Stroke)),
                BorderThickness = new Thickness(1),
                Foreground      = new SolidColorBrush(Color.Parse(isAct ? TextPrimary : TextMuted)),
                CornerRadius    = new CornerRadius(3),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center
            };
            btn.Click += (_, _) => Commit(p => p with { Tip = new ProceduralBrushTip(s, (p.Tip as ProceduralBrushTip)?.AspectRatio ?? 1f) });
            shapeRow.Children.Add(btn);
        }

        // Aspect ratio
        var aspectSlider = MkSlider(0.1, 1.0, Math.Clamp(tip.AspectRatio, 0.1, 1.0), "Aspect ratio (width/height)");
        WireSlider(aspectSlider, v =>
        {
            if (_preset.Tip is ProceduralBrushTip pt)
                Commit(p => p with { Tip = new ProceduralBrushTip(pt.Shape, (float)v) });
        });

        return new StackPanel
        {
            Spacing  = 0,
            Children =
            {
                SectionHeader("SHAPE"),
                shapeRow,
                SectionHeader("ASPECT RATIO"),
                PlainSliderRowRaw(aspectSlider, "f2")
            }
        };
    }

    private Control BuildStrokeContent() => new StackPanel
    {
        Spacing = 0,
        Children =
        {
            PlainSliderRow("Spacing",   _spacingSlider,   "%"),
            PlainSliderRow("Smoothing", _smoothingSlider, "%")
        }
    };

    private Control BuildTextureContent()
    {
        var addShapeBtn = SmBtn("+ Shape");
        var addPngBtn   = SmBtn("+ PNG");
        addShapeBtn.Click += (_, _) => AddShapeLayer();
        addPngBtn.Click   += async (_, _) => await AddPngLayerAsync();

        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(addPngBtn,   Dock.Right);
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
                PlainSliderRow("Grain", _grainSlider, "%"),
                new Border { Height = 8 },
                headerRow,
                _stampPanel
            }
        };
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    // Slider row with dynamics button (≋)
    private Control DynSliderRow(string label, Slider slider, string fmt, Action openDyn)
    {
        var dynBtn = new Button
        {
            Content         = "≋",
            Width           = 22,
            Height          = 22,
            Padding         = new Thickness(0),
            FontSize        = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Background      = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground      = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(dynBtn, "Edit dynamics curve");
        dynBtn.Click += (_, _) => openDyn();

        return BuildSliderRow(label, slider, fmt, dynBtn);
    }

    private Control PlainSliderRow(string label, Slider slider, string fmt)
        => BuildSliderRow(label, slider, fmt, null);

    private static Control PlainSliderRowRaw(Slider slider, string fmt)
    {
        var val = MkValLabel(slider.Value, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) val.Text = FormatV(slider.Value, fmt);
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);
        row.Children.Add(slider);
        return row;
    }

    private static Control BuildSliderRow(string label, Slider slider, string fmt, Control? extra)
    {
        var lbl = new TextBlock
        {
            Text              = label,
            FontSize          = 11,
            Width             = 68,
            Foreground        = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var val = MkValLabel(slider.Value, fmt);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) val.Text = FormatV(slider.Value, fmt);
        };

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(val, Dock.Right);
        if (extra != null)
        {
            DockPanel.SetDock(extra, Dock.Right);
            row.Children.Add(lbl);
            row.Children.Add(extra);
            row.Children.Add(new Border { Width = 4 });
            row.Children.Add(val);
        }
        else
        {
            row.Children.Add(lbl);
            row.Children.Add(val);
        }
        row.Children.Add(slider);
        return row;
    }

    private static TextBlock MkValLabel(double value, string fmt) => new()
    {
        Text              = FormatV(value, fmt),
        FontSize          = 10,
        Width             = 38,
        TextAlignment     = Avalonia.Media.TextAlignment.Right,
        Foreground        = new SolidColorBrush(Color.Parse(TextMuted)),
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily        = new FontFamily("Consolas, Courier New, monospace")
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
            Tip   = new ProceduralBrushTip(),
            Blend = _stampLayers.Count == 0 ? StampLayerBlend.Replace : StampLayerBlend.Multiply
        });
        RebuildStampPanel();
        CommitLayers();
    }

    private async Task AddPngLayerAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import PNG texture",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var mem = new MemoryStream();
        await stream.CopyToAsync(mem);
        _stampLayers.Add(new StampLayer
        {
            Tip   = new ImageBrushTip(mem.ToArray()),
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

        var icon = new TextBlock
        {
            Text              = layer.Tip is ImageBrushTip ? "⊞" : "○",
            FontSize          = 12,
            Width             = 18,
            Foreground        = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment     = Avalonia.Media.TextAlignment.Center
        };

        var typeLabel = new TextBlock
        {
            Text              = layer.Tip is ImageBrushTip ? "PNG" : "Shape",
            FontSize          = 10,
            Width             = 34,
            Foreground        = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var blendBox = new ComboBox
        {
            ItemsSource   = Enum.GetNames<StampLayerBlend>(),
            SelectedIndex = (int)layer.Blend,
            FontSize      = 10,
            Width         = 78,
            Height        = 24,
            Padding       = new Thickness(4, 0)
        };
        blendBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || blendBox.SelectedIndex < 0) return;
            _stampLayers[index] = _stampLayers[index] with { Blend = (StampLayerBlend)blendBox.SelectedIndex };
            CommitLayers();
        };

        var opacSlider = new Slider
        {
            Minimum           = 0,
            Maximum           = 1,
            Value             = layer.Opacity,
            Width             = 54,
            Height            = 24,
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
            Content         = "✕",
            Width           = 22,
            Height          = 22,
            Padding         = new Thickness(0),
            FontSize        = 9,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Background      = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground      = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            IsEnabled       = _stampLayers.Count > 1
        };
        deleteBtn.Click += (_, _) =>
        {
            _stampLayers.RemoveAt(index);
            RebuildStampPanel();
            CommitLayers();
        };

        return new Border
        {
            Background      = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6, 3),
            Margin          = new Thickness(0, 0, 0, 3),
            Child           = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 5,
                Children    = { icon, typeLabel, blendBox, opacSlider, deleteBtn }
            }
        };
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    private void WireSliderEvents()
    {
        WireSlider(_sizeSlider,      v => Commit(p => p with { Size      = v }));
        WireSlider(_opacitySlider,   v => Commit(p => p with { Opacity   = v }));
        WireSlider(_flowSlider,      v => Commit(p => p with { Flow      = v }));
        WireSlider(_hardnessSlider,  v => Commit(p => p with { Hardness  = v }));
        WireSlider(_spacingSlider,   v => Commit(p => p with { Spacing   = v }));
        WireSlider(_smoothingSlider, v => Commit(p => p with { Smoothing = v }));
        WireSlider(_grainSlider,     v => Commit(p => p with { Grain     = v }));
    }

    private void Commit(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncing) return;
        _preset = update(_preset);
        _preview.Brush = _preset;
        _preview.InvalidateBitmap();
        _onChange(_preset);
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
        _preset  = preset;

        _sizeSlider.Value      = Math.Clamp(preset.Size,      _sizeSlider.Minimum,      _sizeSlider.Maximum);
        _opacitySlider.Value   = Math.Clamp(preset.Opacity,   _opacitySlider.Minimum,   _opacitySlider.Maximum);
        _flowSlider.Value      = Math.Clamp(preset.Flow,      _flowSlider.Minimum,      _flowSlider.Maximum);
        _hardnessSlider.Value  = Math.Clamp(preset.Hardness,  _hardnessSlider.Minimum,  _hardnessSlider.Maximum);
        _spacingSlider.Value   = Math.Clamp(preset.Spacing,   _spacingSlider.Minimum,   _spacingSlider.Maximum);
        _smoothingSlider.Value = Math.Clamp(preset.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
        _grainSlider.Value     = Math.Clamp(preset.Grain,     _grainSlider.Minimum,     _grainSlider.Maximum);

        _stampLayers.Clear();
        if (preset.Tip is CompoundBrushTip compound)
            _stampLayers.AddRange(compound.Layers);
        else
            _stampLayers.Add(new StampLayer { Tip = preset.Tip, Blend = StampLayerBlend.Replace });

        _syncing = false;

        // Sync open popup windows
        _sizeDynPopup?.SyncFromDynamics(preset.SizeDynamics);
        _opacDynPopup?.SyncFromDynamics(preset.OpacityDynamics);

        RebuildStampPanel();
        SelectCategory(_activeCategory);
        _preview.Brush = preset;
        _preview.InvalidateBitmap();
        Title = $"Edit Brush — {preset.Name}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Slider MkSlider(double min, double max, double value, string tip)
    {
        var s = new Slider
        {
            Minimum           = min,
            Maximum           = max,
            Value             = value,
            Height            = 26,
            MinHeight         = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(s, tip);
        return s;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text          = text,
        FontSize      = 9,
        FontWeight    = FontWeight.SemiBold,
        Foreground    = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin        = new Thickness(0, 4, 0, 4),
        LetterSpacing = 1.2
    };

    private static Button SmBtn(string label)
    {
        var b = new Button
        {
            Content         = label,
            Height          = 22,
            Padding         = new Thickness(6, 0),
            FontSize        = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Background      = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground      = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3)
        };
        return b;
    }

    private static string FormatV(double v, string fmt) => fmt switch
    {
        "px" => $"{v:0}px",
        "%"  => $"{v * 100:0}%",
        "f1" => $"{v:0.0}",
        "f2" => $"{v:0.00}",
        _    => $"{v:0.##}"
    };

    private static void WireSlider(Slider slider, Action<double> onChange)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) onChange(slider.Value);
        };
    }
}
