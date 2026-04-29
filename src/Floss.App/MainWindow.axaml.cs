using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Psd;

namespace Floss.App;

public partial class MainWindow : Window
{
    // ── Palette ───────────────────────────────────────────────────────────────
    private readonly Color[] _swatches =
    [
        Color.Parse("#111111"), Color.Parse("#ffffff"), Color.Parse("#e53935"),
        Color.Parse("#ff8f00"), Color.Parse("#ffeb3b"), Color.Parse("#43a047"),
        Color.Parse("#00acc1"), Color.Parse("#1e88e5"), Color.Parse("#5e35b1"),
        Color.Parse("#d81b60"), Color.Parse("#795548"), Color.Parse("#78909c")
    ];

    private readonly IReadOnlyList<(string Label, BrushKind? Kind)> _brushCategories =
    [
        ("Recent",   null),
        ("Pencils",  BrushKind.Pencil),
        ("Pens",     BrushKind.Ink),
        ("Markers",  BrushKind.Marker),
        ("Airbrush", BrushKind.Airbrush)
    ];

    // ── Controls ──────────────────────────────────────────────────────────────
    private DrawingCanvas      _canvas              = null!;
    private Grid               _workspaceViewport   = null!;
    private Border             _canvasFrame         = null!;
    private HsvColorPicker     _colorPicker         = null!;
    private TextBox            _hexInput            = null!;
    private Border             _colorWell           = null!;
    private WrapPanel          _swatchPanel         = null!;
    private StackPanel         _brushCategoryPanel  = null!;
    private StackPanel         _presetPanel         = null!;
    private StackPanel         _layerPanel          = null!;
    private Slider             _sizeSlider          = null!;
    private Slider             _opacitySlider       = null!;
    private Slider             _hardnessSlider      = null!;
    private Slider             _spacingSlider       = null!;
    private Slider             _smoothingSlider     = null!;
    private Slider             _grainSlider         = null!;
    private Slider             _layerOpacitySlider  = null!;
    private Button             _undoButton          = null!;
    private Button             _redoButton          = null!;
    private Button             _deleteLayerButton   = null!;
    private Button             _moveLayerUpButton   = null!;
    private Button             _moveLayerDownButton = null!;
    private TextBlock          _toolStatusText      = null!;
    private TextBlock          _footerStatusText    = null!;
    private TextBlock          _canvasStatusText    = null!;

    // ── State ─────────────────────────────────────────────────────────────────
    private double     _zoom     = 1.0;
    private double     _rotation;
    private int        _swatchIndex;
    private BrushKind? _selectedBrushKind;
    private BrushPreset? _activePreset;

    private bool  _spacePanning;
    private bool  _isPanning;
    private Point _lastPanPoint;

    private ScaleTransform     _canvasScale  = null!;
    private RotateTransform    _canvasRotate = null!;
    private TranslateTransform _canvasPan    = null!;

    private bool _syncingLayerUi;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        BuildUi();
        WireControls();
        RestoreFromConfig();
        BuildSwatches();
        BuildBrushCategories();
        ApplyPreset(BrushPreset.Defaults[0]);
        SetColor(Color.Parse(App.Config.LastColor));
        SyncCanvasFrameToDocument(fitToViewport: false);
        BuildLayerList();
        UpdateStatus();
        Closing += (_, _) => SaveToConfig();
    }

    // ── Root layout ───────────────────────────────────────────────────────────
    private void BuildUi()
    {
        _canvas = new DrawingCanvas();

        var tg = new TransformGroup();
        _canvasScale  = new ScaleTransform(1, 1);
        _canvasRotate = new RotateTransform(0);
        _canvasPan    = new TranslateTransform(0, 0);
        tg.Children.Add(_canvasScale);
        tg.Children.Add(_canvasRotate);
        tg.Children.Add(_canvasPan);

        _canvasFrame = new Border
        {
            RenderTransform = tg,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Child = _canvas,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center
        };

        _workspaceViewport = new Grid
        {
            ClipToBounds = true,
            Background   = new SolidColorBrush(Color.Parse("#101113"))
        };
        _workspaceViewport.Children.Add(_canvasFrame);

        _canvasStatusText = MiniText();
        _footerStatusText = MiniText();

        var statusBar = new Border
        {
            Background       = new SolidColorBrush(Color.Parse("#13151a")),
            BorderBrush      = new SolidColorBrush(Color.Parse("#22252e")),
            BorderThickness  = new Thickness(0, 0, 0, 1),
            Height           = 26,
            Padding          = new Thickness(12, 0),
            Child            = _canvasStatusText
        };

        var footer = new Border
        {
            Background       = new SolidColorBrush(Color.Parse("#13151a")),
            BorderBrush      = new SolidColorBrush(Color.Parse("#22252e")),
            BorderThickness  = new Thickness(0, 1, 0, 0),
            Height           = 22,
            Padding          = new Thickness(12, 0),
            Child            = _footerStatusText
        };

        var centerArea = new Grid { RowDefinitions = new RowDefinitions("26,*,22") };
        Grid.SetRow(statusBar, 0);
        Grid.SetRow(_workspaceViewport, 1);
        Grid.SetRow(footer, 2);
        centerArea.Children.Add(statusBar);
        centerArea.Children.Add(_workspaceViewport);
        centerArea.Children.Add(footer);

        var leftRail   = BuildLeftRail();
        var rightPanel = BuildRightPanel();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(48, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
        root.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(290, GridUnitType.Pixel) { MinWidth = 180, MaxWidth = 600 });

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
            Background          = new SolidColorBrush(Color.Parse("#1a1c22"))
        };

        Grid.SetColumn(leftRail,   0);
        Grid.SetColumn(centerArea, 1);
        Grid.SetColumn(splitter,   2);
        Grid.SetColumn(rightPanel, 3);
        root.Children.Add(leftRail);
        root.Children.Add(centerArea);
        root.Children.Add(splitter);
        root.Children.Add(rightPanel);

        Content = root;
    }

    private static TextBlock MiniText() => new()
    {
        Foreground        = new SolidColorBrush(Color.Parse("#505570")),
        FontSize          = 11,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    // ── Left rail ─────────────────────────────────────────────────────────────
    private Control BuildLeftRail()
    {
        _toolStatusText = new TextBlock
        {
            Text              = "Brush",
            Foreground        = new SolidColorBrush(Color.Parse("#505570")),
            FontSize          = 9,
            TextAlignment     = TextAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin            = new Thickness(0, 1, 0, 4)
        };

        _colorWell = new Border
        {
            Width            = 26,
            Height           = 26,
            CornerRadius     = new CornerRadius(13),
            BorderBrush      = new SolidColorBrush(Color.Parse("#3a3d46")),
            BorderThickness  = new Thickness(1.5),
            Background       = new SolidColorBrush(Color.Parse("#111111"))
        };

        var colorBtn = new Button
        {
            Content            = _colorWell,
            Width              = 36,
            Height             = 36,
            Background         = Avalonia.Media.Brushes.Transparent,
            BorderBrush        = Avalonia.Media.Brushes.Transparent,
            Padding            = new Thickness(4),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(colorBtn, "Cycle color  (X)");
        colorBtn.Click += (_, _) => CycleColor();

        var brushBtn  = RailBtn("⬤", "Brush  (B)");
        var eraserBtn = RailBtn("◎", "Eraser  (E)");
        brushBtn.Click  += (_, _) => SetTool("brush");
        eraserBtn.Click += (_, _) => SetTool("eraser");

        _undoButton = RailBtn("↩", "Undo  (Ctrl+Z)");
        _redoButton = RailBtn("↪", "Redo  (Ctrl+Shift+Z)");
        _undoButton.Click += (_, _) => _canvas.Undo();
        _redoButton.Click += (_, _) => _canvas.Redo();

        var clearBtn     = RailBtn("⊡", "Clear layer");
        var openBtn      = RailBtn("⊞", "Open PSD  (Ctrl+O)");
        var saveBtn      = RailBtn("⊟", "Save PSD  (Ctrl+S)");
        var zoomResetBtn = RailBtn("⊙", "Reset view  (Ctrl+0)");
        clearBtn.Click     += (_, _) => _canvas.Clear();
        openBtn.Click      += async (_, _) => await OpenPsdAsync();
        saveBtn.Click      += async (_, _) => await SavePsdAsync();
        zoomResetBtn.Click += (_, _) => { SetZoom(1.0, null); SetRotation(0); };

        var stack = new StackPanel
        {
            Orientation         = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin              = new Thickness(0, 10),
            Spacing             = 2
        };
        stack.Children.Add(brushBtn);
        stack.Children.Add(eraserBtn);
        stack.Children.Add(_toolStatusText);
        stack.Children.Add(RailSep());
        stack.Children.Add(colorBtn);
        stack.Children.Add(RailSep());
        stack.Children.Add(_undoButton);
        stack.Children.Add(_redoButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(clearBtn);
        stack.Children.Add(openBtn);
        stack.Children.Add(saveBtn);
        stack.Children.Add(RailSep());
        stack.Children.Add(zoomResetBtn);

        return new Border
        {
            Background      = new SolidColorBrush(Color.Parse("#13151a")),
            BorderBrush     = new SolidColorBrush(Color.Parse("#22252e")),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child           = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Hidden,
                Content                       = stack
            }
        };
    }

    private static Button RailBtn(string glyph, string tip)
    {
        var btn = new Button
        {
            Content  = glyph,
            Width    = 36,
            Height   = 36,
            FontSize = 15,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            Background      = Avalonia.Media.Brushes.Transparent,
            BorderBrush     = Avalonia.Media.Brushes.Transparent,
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(0)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Border RailSep() => new()
    {
        Height     = 1,
        Width      = 28,
        Background = new SolidColorBrush(Color.Parse("#22252e")),
        Margin     = new Thickness(0, 5)
    };

    // ── Right panel ───────────────────────────────────────────────────────────
    private Control BuildRightPanel()
    {
        var stack = new StackPanel();
        stack.Children.Add(DockerSection("Color",   BuildColorSection(), startExpanded: true));
        stack.Children.Add(DockerSection("Brush",   BuildBrushSection(), startExpanded: true));
        stack.Children.Add(DockerSection("Library", BuildLibrarySection(), startExpanded: false));
        stack.Children.Add(DockerSection("Layers",  BuildLayersSection(), startExpanded: true));

        return new Border
        {
            Background      = new SolidColorBrush(Color.Parse("#13151a")),
            BorderBrush     = new SolidColorBrush(Color.Parse("#22252e")),
            BorderThickness = new Thickness(0, 0, 0, 0),
            Child           = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                Content                       = stack
            }
        };
    }

    // Collapsible docker section with a clickable header bar.
    private static Border DockerSection(string title, Control content, bool startExpanded)
    {
        var arrow = new TextBlock
        {
            Text              = startExpanded ? "▾" : "▸",
            FontSize          = 10,
            Width             = 14,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground        = new SolidColorBrush(Color.Parse("#50557a"))
        };
        var titleText = new TextBlock
        {
            Text          = title.ToUpperInvariant(),
            FontSize      = 9,
            FontWeight    = FontWeight.SemiBold,
            LetterSpacing = 1.2,
            Foreground    = new SolidColorBrush(Color.Parse("#50557a")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var headerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 5
        };
        headerRow.Children.Add(arrow);
        headerRow.Children.Add(titleText);

        var contentWrap = new Border { Child = content, IsVisible = startExpanded };

        var headerBtn = new Button
        {
            Content            = headerRow,
            Padding            = new Thickness(10, 7),
            Background         = new SolidColorBrush(Color.Parse("#0d0f14")),
            BorderBrush        = Avalonia.Media.Brushes.Transparent,
            HorizontalAlignment            = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment     = Avalonia.Layout.HorizontalAlignment.Left,
            CornerRadius       = new CornerRadius(0)
        };
        headerBtn.Click += (_, _) =>
        {
            var open = !contentWrap.IsVisible;
            contentWrap.IsVisible = open;
            arrow.Text = open ? "▾" : "▸";
        };

        var outer = new StackPanel();
        outer.Children.Add(headerBtn);
        outer.Children.Add(contentWrap);

        return new Border
        {
            BorderBrush     = new SolidColorBrush(Color.Parse("#1c1e24")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child           = outer
        };
    }

    // ── Color section ─────────────────────────────────────────────────────────
    private Control BuildColorSection()
    {
        _colorPicker = new HsvColorPicker
        {
            Height = 168,
            Margin = new Thickness(10, 0, 10, 8)
        };
        _colorPicker.HsvChanged += OnPickerHsvChanged;

        _hexInput = new TextBox
        {
            Width                    = 90,
            Height                   = 26,
            FontSize                 = 12,
            FontFamily               = new FontFamily("Consolas, Courier New, monospace"),
            Background               = new SolidColorBrush(Color.Parse("#0d0f14")),
            Foreground               = new SolidColorBrush(Color.Parse("#b8bcc8")),
            BorderBrush              = new SolidColorBrush(Color.Parse("#2a2d34")),
            Padding                  = new Thickness(6, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            CaretBrush               = new SolidColorBrush(Color.Parse("#b8bcc8")),
            HorizontalAlignment      = Avalonia.Layout.HorizontalAlignment.Left
        };
        _hexInput.KeyDown   += (_, e) => { if (e.Key is Key.Enter or Key.Return) TryApplyHexColor(_hexInput.Text ?? ""); };
        _hexInput.LostFocus += (_, _) => TryApplyHexColor(_hexInput.Text ?? "");

        _swatchPanel = new WrapPanel
        {
            Margin  = new Thickness(10, 4, 10, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };

        return new StackPanel
        {
            Children =
            {
                _colorPicker,
                new Border { Margin = new Thickness(10, 0, 10, 6), Child = _hexInput },
                _swatchPanel
            }
        };
    }

    // ── Brush section ─────────────────────────────────────────────────────────
    private Control BuildBrushSection()
    {
        _sizeSlider      = MkSlider(1,    256,  20,   "Size");
        _opacitySlider   = MkSlider(0.01, 1,    1.0,  "Opacity");
        _hardnessSlider  = MkSlider(0,    1,    0.9,  "Hardness");
        _spacingSlider   = MkSlider(0.02, 1,    0.1,  "Spacing");
        _smoothingSlider = MkSlider(0,    0.95, 0.3,  "Smoothing");
        _grainSlider     = MkSlider(0,    1,    0.0,  "Grain");

        return new StackPanel
        {
            Margin  = new Thickness(10, 0, 10, 8),
            Spacing = 4,
            Children =
            {
                LabelSlider("Size",      _sizeSlider),
                LabelSlider("Opacity",   _opacitySlider),
                LabelSlider("Hardness",  _hardnessSlider),
                LabelSlider("Spacing",   _spacingSlider),
                LabelSlider("Smoothing", _smoothingSlider),
                LabelSlider("Grain",     _grainSlider)
            }
        };
    }

    // ── Library section ───────────────────────────────────────────────────────
    private Control BuildLibrarySection()
    {
        _brushCategoryPanel = new StackPanel { Spacing = 3 };
        _presetPanel        = new StackPanel { Spacing = 3 };

        var presetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            MaxHeight = 240,
            Content   = _presetPanel
        };

        return new StackPanel
        {
            Margin  = new Thickness(10, 0, 10, 8),
            Spacing = 6,
            Children = { _brushCategoryPanel, presetScroll }
        };
    }

    // ── Layers section ────────────────────────────────────────────────────────
    private Control BuildLayersSection()
    {
        var addBtn  = SmBtn("+", "Add layer  (Ctrl+Shift+N)");
        var dupBtn  = SmBtn("⎘", "Duplicate  (Ctrl+J)");
        _deleteLayerButton   = SmBtn("✕", "Delete  (Ctrl+Delete)");
        _moveLayerUpButton   = SmBtn("↑", "Move up  (Ctrl+Up)");
        _moveLayerDownButton = SmBtn("↓", "Move down  (Ctrl+Down)");

        addBtn.Click                 += (_, _) => _canvas.AddLayer();
        dupBtn.Click                 += (_, _) => _canvas.DuplicateLayer();
        _deleteLayerButton.Click    += (_, _) => _canvas.DeleteLayer();
        _moveLayerUpButton.Click    += (_, _) => _canvas.MoveActiveLayer(1);
        _moveLayerDownButton.Click  += (_, _) => _canvas.MoveActiveLayer(-1);

        var ctrlRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 3,
            Margin      = new Thickness(10, 0, 10, 4),
            Children    = { addBtn, dupBtn, _deleteLayerButton, _moveLayerUpButton, _moveLayerDownButton }
        };

        _layerOpacitySlider = MkSlider(0, 1, 1, "Layer opacity");
        _layerOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (_syncingLayerUi || e.Property != Slider.ValueProperty) return;
            _canvas.SetActiveLayerOpacity(_layerOpacitySlider.Value);
        };

        _layerPanel = new StackPanel { Spacing = 2 };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                ctrlRow,
                new Border { Margin = new Thickness(10, 0, 10, 4), Child = LabelSlider("Opacity", _layerOpacitySlider) },
                new Border { Margin = new Thickness(4, 0, 4, 8),   Child = _layerPanel }
            }
        };
    }

    // ── Widget helpers ────────────────────────────────────────────────────────
    private static Button SmBtn(string glyph, string tip)
    {
        var btn = new Button
        {
            Content         = glyph,
            Width           = 24,
            Height          = 24,
            Padding         = new Thickness(0),
            FontSize        = 12,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            Background      = new SolidColorBrush(Color.Parse("#1c1e24")),
            BorderBrush     = new SolidColorBrush(Color.Parse("#2a2d34")),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Slider MkSlider(double min, double max, double value, string tip)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Height = 20 };
        ToolTip.SetTip(s, tip);
        return s;
    }

    private static Control LabelSlider(string label, Slider slider)
    {
        var lbl = new TextBlock
        {
            Text              = label,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.Parse("#606470")),
            Width             = 66,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(slider);
        return row;
    }

    // ── Config persistence ────────────────────────────────────────────────────
    private void RestoreFromConfig()
    {
        var cfg = App.Config;
        _sizeSlider.Value     = Math.Clamp(cfg.LastBrushSize,     _sizeSlider.Minimum,     _sizeSlider.Maximum);
        _opacitySlider.Value  = Math.Clamp(cfg.LastBrushOpacity,  _opacitySlider.Minimum,  _opacitySlider.Maximum);
        _hardnessSlider.Value = Math.Clamp(cfg.LastBrushHardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
        _spacingSlider.Value  = Math.Clamp(cfg.LastBrushSpacing,  _spacingSlider.Minimum,  _spacingSlider.Maximum);
    }

    private void SaveToConfig()
    {
        var cfg = App.Config;
        cfg.LastBrushSize     = _sizeSlider.Value;
        cfg.LastBrushOpacity  = _opacitySlider.Value;
        cfg.LastBrushHardness = _hardnessSlider.Value;
        cfg.LastBrushSpacing  = _spacingSlider.Value;
        var c = _canvas.PaintColor;
        cfg.LastColor     = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        cfg.LastBrushName = _activePreset?.Name ?? "";
        cfg.Save();
    }

    // ── Wire-up ───────────────────────────────────────────────────────────────
    private void WireControls()
    {
        _workspaceViewport.PointerWheelChanged += Workspace_OnPointerWheelChanged;
        _workspaceViewport.PointerPressed      += Workspace_OnPointerPressed;
        _workspaceViewport.PointerMoved        += Workspace_OnPointerMoved;
        _workspaceViewport.PointerReleased     += Workspace_OnPointerReleased;

        _canvas.StatsChanged   += (_, _) => { _layerPanel.InvalidateVisual(); UpdateStatus(); };
        _canvas.HistoryChanged += (_, _) => UpdateStatus();
        _canvas.LayersChanged  += (_, _) => { BuildLayerList(); UpdateStatus(); };

        SliderChanged(_sizeSlider,      v => _canvas.SetBrushSize(v));
        SliderChanged(_opacitySlider,   v => _canvas.SetBrushOpacity(v));
        SliderChanged(_hardnessSlider,  v => _canvas.SetBrushHardness(v));
        SliderChanged(_spacingSlider,   v => _canvas.SetBrushSpacing(v));
        SliderChanged(_smoothingSlider, v => _canvas.SetBrushSmoothing(v));
        SliderChanged(_grainSlider,     v => _canvas.SetBrushGrain(v));

        KeyDown += OnKeyDown;
        KeyUp   += OnKeyUp;
    }

    private static void SliderChanged(Slider slider, Action<double> action)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) action(slider.Value);
        };
    }

    // ── Color picker ──────────────────────────────────────────────────────────
    private void OnPickerHsvChanged(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SetColor(color, syncPicker: false);
    }

    private void SyncPickerFromColor(Color color)
    {
        var (h, s, v) = RgbToHsv(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        _colorPicker.SetHsv(h, s, v);
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void TryApplyHexColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, null, out var rgb))
            SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
    }

    // ── Color application ─────────────────────────────────────────────────────
    private void SetColor(Color color, bool syncPicker = true)
    {
        _colorWell.Background = new SolidColorBrush(color);
        _canvas.SetPaintColor(color);
        SetTool("brush");
        if (syncPicker) SyncPickerFromColor(color);
    }

    private void CycleColor()
    {
        _swatchIndex = (_swatchIndex + 1) % _swatches.Length;
        SetColor(_swatches[_swatchIndex]);
    }

    // ── Swatch panel ──────────────────────────────────────────────────────────
    private void BuildSwatches()
    {
        for (var i = 0; i < _swatches.Length; i++)
        {
            var idx   = i;
            var color = _swatches[i];
            var btn = new Button
            {
                Width           = 18,
                Height          = 18,
                Margin          = new Thickness(0, 0, 4, 4),
                Background      = new SolidColorBrush(color),
                BorderBrush     = new SolidColorBrush(Color.Parse("#333")),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(9),
                Padding         = new Thickness(0)
            };
            ToolTip.SetTip(btn, color.ToString());
            btn.Click += (_, _) => { _swatchIndex = idx; SetColor(color); };
            _swatchPanel.Children.Add(btn);
        }
    }

    // ── Brush library ─────────────────────────────────────────────────────────
    private void BuildBrushCategories()
    {
        _brushCategoryPanel.Children.Clear();
        foreach (var cat in _brushCategories)
        {
            var selected = cat.Kind == _selectedBrushKind;
            var btn = new Button
            {
                Content            = cat.Label,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding            = new Thickness(10, 7),
                Background         = new SolidColorBrush(selected ? Color.Parse("#2a6ef5") : Color.Parse("#1c1e24")),
                Foreground         = new SolidColorBrush(selected ? Color.Parse("#ffffff") : Color.Parse("#888d9a")),
                BorderBrush        = Avalonia.Media.Brushes.Transparent,
                CornerRadius       = new CornerRadius(6),
                FontSize           = 11,
                Tag                = cat.Kind
            };
            btn.Click += (_, _) =>
            {
                _selectedBrushKind = btn.Tag is BrushKind k ? k : null;
                BuildBrushCategories();
                BuildPresets();
            };
            _brushCategoryPanel.Children.Add(btn);
        }
    }

    private void BuildPresets()
    {
        _presetPanel.Children.Clear();
        var presets = _selectedBrushKind is null
            ? BrushPreset.Defaults
            : BrushPreset.Defaults.Where(p => p.Kind == _selectedBrushKind).ToArray();

        foreach (var preset in presets)
        {
            var isActive = _activePreset?.Name == preset.Name;
            var row = new Button
            {
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background      = new SolidColorBrush(isActive ? Color.Parse("#2a6ef5") : Color.Parse("#1a1c22")),
                BorderBrush     = Avalonia.Media.Brushes.Transparent,
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(9, 7),
                Tag             = preset
            };
            var nameText = new TextBlock
            {
                Text       = preset.Name,
                Foreground = new SolidColorBrush(isActive ? Color.Parse("#ffffff") : Color.Parse("#c8cdd8")),
                FontWeight = FontWeight.SemiBold,
                FontSize   = 11
            };
            var previewText = new TextBlock
            {
                Text       = PreviewStroke(preset),
                Foreground = new SolidColorBrush(isActive ? Color.Parse("#eef3ff") : Color.Parse("#505570")),
                FontSize   = 14,
                Margin     = new Thickness(0, 2, 0, 0)
            };
            var col = new StackPanel { Children = { nameText, previewText } };
            row.Content = col;
            row.Click  += (_, _) => ApplyPreset(preset);
            _presetPanel.Children.Add(row);
        }
    }

    private static string PreviewStroke(BrushPreset p) => p.Kind switch
    {
        BrushKind.Pencil   => "╍╍╍╍╍╍╍",
        BrushKind.Marker   => "━━━━━━",
        BrushKind.Airbrush => "░░░░░░░",
        _                  => "━━━━━━"
    };

    private void ApplyPreset(BrushPreset preset)
    {
        _activePreset = preset;
        var applied = preset with
        {
            Color    = _canvas.PaintColor,
            Size     = _sizeSlider.Value,
            Opacity  = _opacitySlider.Value,
            Hardness = _hardnessSlider.Value,
            Spacing  = _spacingSlider.Value
        };
        _canvas.SetBrush(applied);
        _smoothingSlider.Value = preset.Smoothing;
        _grainSlider.Value     = preset.Grain;
        SetTool(preset.Kind == BrushKind.Eraser ? "eraser" : "brush");
        BuildPresets();
        UpdateStatus();
    }

    // ── Tool selection ────────────────────────────────────────────────────────
    private void SetTool(string tool)
    {
        _canvas.SetTool(tool);
        _toolStatusText.Text   = tool == "eraser" ? "Eraser" : _canvas.Brush.Name;
        _footerStatusText.Text = tool == "eraser" ? "Eraser" : "Brush";
    }

    // ── Layer panel ───────────────────────────────────────────────────────────
    private void BuildLayerList()
    {
        _layerPanel.Children.Clear();
        var layers = _canvas.Layers;

        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer    = layers[i];
            var isActive = i == _canvas.ActiveLayerIndex;

            var row = new Border
            {
                Background      = new SolidColorBrush(isActive ? Color.Parse("#1a2a50") : Color.Parse("#1a1c22")),
                BorderBrush     = new SolidColorBrush(isActive ? Color.Parse("#2e5fb8") : Color.Parse("#22252e")),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(5, 4),
                Margin          = new Thickness(layer.IndentLevel * 12, 0, 0, 0)
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,34,22,*,Auto") };

            var visBtn = new Button
            {
                Content  = layer.IsVisible ? "●" : "○",
                Width    = 18, Height = 18,
                Padding  = new Thickness(0),
                FontSize = 10,
                Tag      = i,
                Background  = Avalonia.Media.Brushes.Transparent,
                BorderBrush = Avalonia.Media.Brushes.Transparent
            };
            ToolTip.SetTip(visBtn, "Toggle visibility");
            visBtn.Click += (_, _) => _canvas.ToggleLayerVisibility((int)visBtn.Tag!);

            var preview = BuildLayerPreview(layer);

            var lockBtn = new Button
            {
                Content  = layer.IsLocked ? "L" : "·",
                Width    = 18, Height = 18,
                Padding  = new Thickness(0),
                FontSize = 11,
                Tag      = i,
                Background  = Avalonia.Media.Brushes.Transparent,
                BorderBrush = Avalonia.Media.Brushes.Transparent
            };
            ToolTip.SetTip(lockBtn, "Toggle lock");
            lockBtn.Click += (_, _) => _canvas.ToggleLayerLock((int)lockBtn.Tag!);

            var prefix = (layer.IsGroup ? (layer.IsOpen ? "▾ " : "▸ ") : "") + (layer.IsClipping ? "⤷ " : "");
            var nameBtn = new Button
            {
                Content            = prefix + layer.Name,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Background         = Avalonia.Media.Brushes.Transparent,
                BorderBrush        = Avalonia.Media.Brushes.Transparent,
                Foreground         = new SolidColorBrush(isActive ? Color.Parse("#d8e0f0") : Color.Parse("#909aa8")),
                Padding            = new Thickness(5, 1),
                FontSize           = 11,
                Tag                = i
            };
            nameBtn.Click += (_, _) => _canvas.SelectLayer((int)nameBtn.Tag!);

            var opacityText = new TextBlock
            {
                Text              = $"{Math.Round(layer.Opacity * 100)}%",
                Foreground        = new SolidColorBrush(isActive ? Color.Parse("#5a80c8") : Color.Parse("#404550")),
                FontSize          = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin            = new Thickness(3, 0, 2, 0)
            };

            Grid.SetColumn(visBtn,      0);
            Grid.SetColumn(preview,     1);
            Grid.SetColumn(lockBtn,     2);
            Grid.SetColumn(nameBtn,     3);
            Grid.SetColumn(opacityText, 4);
            grid.Children.Add(visBtn);
            grid.Children.Add(preview);
            grid.Children.Add(lockBtn);
            grid.Children.Add(nameBtn);
            grid.Children.Add(opacityText);
            row.Child = grid;
            _layerPanel.Children.Add(row);
        }

        if (layers.Count > 0)
        {
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = layers[_canvas.ActiveLayerIndex].Opacity;
            _syncingLayerUi = false;
        }
    }

    private static Control BuildLayerPreview(DrawingLayer layer)
    {
        var frame = new Border
        {
            Width           = 28,
            Height          = 28,
            Margin          = new Thickness(2, 0, 4, 0),
            Background      = new SolidColorBrush(Color.Parse("#252832")),
            BorderBrush     = new SolidColorBrush(Color.Parse("#3a3f4d")),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            ClipToBounds    = true
        };

        if (layer.IsGroup)
        {
            frame.Child = new TextBlock
            {
                Text                = layer.IsOpen ? "▾" : "▸",
                Foreground          = new SolidColorBrush(Color.Parse("#9aa6c1")),
                FontSize            = 13,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center
            };
            return frame;
        }

        var image = new Image
        {
            Source              = layer.GetThumbnail(28),
            Stretch             = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        frame.Child = image;
        return frame;
    }

    // ── Viewport ──────────────────────────────────────────────────────────────
    private void SyncCanvasFrameToDocument(bool fitToViewport)
    {
        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        _canvasFrame.Width  = w;
        _canvasFrame.Height = h;

        if (fitToViewport)
        {
            var vw = Math.Max(1, _workspaceViewport.Bounds.Width  - 80);
            var vh = Math.Max(1, _workspaceViewport.Bounds.Height - 80);
            _zoom = Math.Clamp(Math.Min(vw / w, vh / h), 0.05, 1.0);
            _canvasScale.ScaleX = _zoom;
            _canvasScale.ScaleY = _zoom;
            _canvasPan.X = 0;
            _canvasPan.Y = 0;
            UpdateStatus();
        }
    }

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
        SetZoom(_zoom * factor, e.GetPosition(_workspaceViewport));
        e.Handled = true;
    }

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_workspaceViewport);
        if (!_spacePanning && !pt.Properties.IsMiddleButtonPressed) return;
        _isPanning    = true;
        _lastPanPoint = e.GetPosition(_workspaceViewport);
        e.Pointer.Capture(_workspaceViewport);
        e.Handled = true;
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var pt = e.GetPosition(_workspaceViewport);
        var d  = pt - _lastPanPoint;
        _canvasPan.X  += d.X;
        _canvasPan.Y  += d.Y;
        _lastPanPoint  = pt;
        e.Handled = true;
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // pan_new = (cursor - vpCenter) * (1 - ratio) + pan_old * ratio
    private void SetZoom(double newZoom, Point? cursor)
    {
        var oldZoom = _zoom;
        _zoom = Math.Clamp(newZoom, 0.05, 16.0);
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;

        if (cursor.HasValue && oldZoom > 0)
        {
            var ratio = _zoom / oldZoom;
            var vpW   = _workspaceViewport.Bounds.Width;
            var vpH   = _workspaceViewport.Bounds.Height;
            var c     = cursor.Value;
            _canvasPan.X = (c.X - vpW * 0.5) * (1 - ratio) + _canvasPan.X * ratio;
            _canvasPan.Y = (c.Y - vpH * 0.5) * (1 - ratio) + _canvasPan.Y * ratio;
        }

        UpdateStatus();
    }

    private void SetRotation(double degrees)
    {
        _rotation = degrees % 360;
        _canvasRotate.Angle      = _rotation;
        _canvas.CanvasRotation   = _rotation;
        UpdateStatus();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mod   = e.KeyModifiers;
        var ctrl  = mod.HasFlag(KeyModifiers.Control);
        var shift = mod.HasFlag(KeyModifiers.Shift);
        var none  = mod == KeyModifiers.None;

        if (none && e.Key == Key.Space)
        {
            _spacePanning = true;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }

        if      (ctrl && shift && e.Key == Key.Z)         { _canvas.Redo();                    e.Handled = true; }
        else if (ctrl && e.Key == Key.Z)                  { _canvas.Undo();                    e.Handled = true; }
        else if (ctrl && e.Key == Key.Y)                  { _canvas.Redo();                    e.Handled = true; }
        else if (ctrl && e.Key == Key.S)                  { _ = SavePsdAsync();                e.Handled = true; }
        else if (ctrl && e.Key == Key.O)                  { _ = OpenPsdAsync();                e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.N)         { _canvas.AddLayer();                e.Handled = true; }
        else if (ctrl && e.Key == Key.J)                  { _canvas.DuplicateLayer();          e.Handled = true; }
        else if (ctrl && e.Key == Key.Delete)             { _canvas.DeleteLayer();             e.Handled = true; }
        else if (ctrl && e.Key == Key.Up)                 { _canvas.MoveActiveLayer(1);        e.Handled = true; }
        else if (ctrl && e.Key == Key.Down)               { _canvas.MoveActiveLayer(-1);       e.Handled = true; }
        else if (ctrl && e.Key == Key.D0)                 { SetZoom(1.0, null); SetRotation(0); e.Handled = true; }
        else if (ctrl && e.Key == Key.Add)                { SetZoom(_zoom * 1.2, null);        e.Handled = true; }
        else if (ctrl && e.Key == Key.Subtract)           { SetZoom(_zoom / 1.2, null);        e.Handled = true; }
        else if (none && e.Key == Key.B)                  { SetTool("brush");                  e.Handled = true; }
        else if (none && e.Key == Key.E)                  { SetTool("eraser");                 e.Handled = true; }
        else if (none && e.Key == Key.X)                  { CycleColor();                      e.Handled = true; }
        else if (none && e.Key == Key.D)                  { SetColor(Color.Parse("#111111"));  e.Handled = true; }
        else if (none && e.Key == Key.OemOpenBrackets)
        {
            _sizeSlider.Value = Math.Max(_sizeSlider.Minimum, _sizeSlider.Value - 2);
            e.Handled = true;
        }
        else if (none && e.Key == Key.OemCloseBrackets)
        {
            _sizeSlider.Value = Math.Min(_sizeSlider.Maximum, _sizeSlider.Value + 2);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.OemOpenBrackets)  { SetRotation(_rotation - 15);       e.Handled = true; }
        else if (shift && e.Key == Key.OemCloseBrackets) { SetRotation(_rotation + 15);       e.Handled = true; }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spacePanning = false;
            _isPanning    = false;
            Cursor        = Cursor.Default;
            e.Handled     = true;
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────
    private void UpdateStatus()
    {
        var layers = _canvas.Layers;
        if (layers.Count == 0) return;
        var layer = layers[_canvas.ActiveLayerIndex];
        _canvasStatusText.Text =
            $"{Math.Round(_zoom * 100)}%  {Math.Round(_rotation)}°  " +
            $"layer {_canvas.ActiveLayerIndex + 1}/{layers.Count}  " +
            $"{layer.BlendMode}";
        _undoButton.IsEnabled         = _canvas.CanUndo;
        _redoButton.IsEnabled         = _canvas.CanRedo;
        _deleteLayerButton.IsEnabled  = _canvas.CanDeleteLayer;
        _moveLayerUpButton.IsEnabled  = _canvas.ActiveLayerIndex < layers.Count - 1;
        _moveLayerDownButton.IsEnabled = _canvas.ActiveLayerIndex > 0;
    }

    // ── File I/O ──────────────────────────────────────────────────────────────
    private async System.Threading.Tasks.Task OpenPsdAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open PSD",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("Photoshop Document") { Patterns = ["*.psd"] }]
        });
        if (files.Count == 0) return;

        try
        {
            var path = files[0].Path.LocalPath;
            await using var stream = await files[0].OpenReadAsync();
            PsdImporter.Import(stream, _canvas.Document);
            App.Config.AddRecentFile(path);
            SyncCanvasFrameToDocument(fitToViewport: true);
            BuildLayerList();
            UpdateStatus();
            _footerStatusText.Text =
                $"Opened {_canvas.Document.Width}×{_canvas.Document.Height}  {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task SavePsdAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title           = "Save PSD",
            FileTypeChoices = [new FilePickerFileType("Photoshop Document") { Patterns = ["*.psd"] }]
        });
        if (file == null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            PsdExporter.Export(stream, _canvas.Document);
            App.Config.AddRecentFile(file.Path.LocalPath);
            _footerStatusText.Text = $"Saved {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    // ── HSV ↔ RGB helpers ─────────────────────────────────────────────────────
    private static (double h, double s, double v) RgbToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d   = max - min;
        double h = 0;
        if (d > 0)
        {
            if      (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else               h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double r, double g, double b) HsvToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        var hi = (int)(h / 60) % 6;
        var f  = h / 60 - Math.Floor(h / 60);
        var p  = v * (1 - s);
        var q  = v * (1 - f * s);
        var t  = v * (1 - (1 - f) * s);
        return hi switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
    }
}
