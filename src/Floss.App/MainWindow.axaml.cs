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
    private const string Bg0 = "#0d0f14";
    private const string Bg1 = "#13151a";
    private const string Bg2 = "#1a1c22";
    private const string Bg3 = "#20232b";
    private const string Stroke = "#2b303b";
    private const string TextPrimary = "#d7dde8";
    private const string TextSecondary = "#A0AAB4";
    private const string TextMuted = "#6f7888";
    private const string Accent = "#3d6fd8";
    private const string AccentSoft = "#22355f";

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
    private Button             _brushToolButton     = null!;
    private Button             _eraserToolButton    = null!;
    private Button             _deleteLayerButton   = null!;
    private Button             _moveLayerUpButton   = null!;
    private Button             _moveLayerDownButton = null!;
    private TextBlock          _toolStatusText      = null!;
    private TextBlock          _footerStatusText    = null!;
    private TextBlock          _canvasStatusText    = null!;
    private Button             _saveBrushButton     = null!;

    // ── State ─────────────────────────────────────────────────────────────────
    private double     _zoom     = 1.0;
    private double     _rotation;
    private int        _swatchIndex;
    private BrushKind? _selectedBrushKind;
    private BrushPreset? _activePreset;
    private BrushAsset? _activeBrushAsset;
    private BrushLibrary _brushLibrary = null!;
    private IReadOnlyList<BrushAsset> _brushAssets = [];
    private readonly Dictionary<int, LayerRowRefs> _layerRows = new();

    private bool  _spacePanning;
    private bool  _isPanning;
    private Point _lastPanPoint;

    private ScaleTransform     _canvasScale  = null!;
    private RotateTransform    _canvasRotate = null!;
    private TranslateTransform _canvasPan    = null!;

    private bool _syncingLayerUi;
    private bool _syncingBrushUi;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        _brushLibrary = new BrushLibrary(AppPaths.BrushesDirectory);
        BuildUi();
        WireControls();
        RestoreFromConfig();
        BuildSwatches();
        BuildBrushCategories();
        LoadBrushAssets();
        SelectInitialBrush();
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
            Background       = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush      = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness  = new Thickness(0, 0, 0, 1),
            Height           = 26,
            Padding          = new Thickness(12, 0),
            Child            = _canvasStatusText
        };

        var footer = new Border
        {
            Background       = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush      = new SolidColorBrush(Color.Parse(Stroke)),
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
        root.ColumnDefinitions.Add(new ColumnDefinition(56, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
        root.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(290, GridUnitType.Pixel) { MinWidth = 180, MaxWidth = 600 });

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
            Background          = new SolidColorBrush(Color.Parse(Bg2))
        };

        Grid.SetColumn(leftRail,   0);
        Grid.SetColumn(centerArea, 1);
        Grid.SetColumn(splitter,   2);
        Grid.SetColumn(rightPanel, 3);
        root.Children.Add(leftRail);
        root.Children.Add(centerArea);
        root.Children.Add(splitter);
        root.Children.Add(rightPanel);

        var shell = new Grid { RowDefinitions = new RowDefinitions("30,*") };
        var menu = BuildMenuBar();
        Grid.SetRow(menu, 0);
        Grid.SetRow(root, 1);
        shell.Children.Add(menu);
        shell.Children.Add(root);

        Content = shell;
    }

    private static TextBlock MiniText() => new()
    {
        Foreground        = new SolidColorBrush(Color.Parse(TextSecondary)),
        FontSize          = 11,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private Control BuildMenuBar()
    {
        var fileMenu = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_Open PSD...", async () => await OpenPsdAsync()),
                MenuAction("_Save PSD...", async () => await SavePsdAsync()),
                new Separator(),
                MenuAction("_Reset View", () => { SetZoom(1.0, null); SetRotation(0); })
            }
        };

        var brushMenu = new MenuItem
        {
            Header = "_Brush",
            ItemsSource = new object[]
            {
                MenuAction("_Save Brush", SaveActiveBrush),
                MenuAction("_Duplicate Brush", DuplicateActiveBrush),
                MenuAction("_Import Tip PNG...", async () => await ImportBrushTipPngAsync())
            }
        };

        var layerMenu = new MenuItem
        {
            Header = "_Layer",
            ItemsSource = new object[]
            {
                MenuAction("_Add Layer", () => _canvas.AddLayer()),
                MenuAction("_Duplicate Layer", () => _canvas.DuplicateLayer()),
                MenuAction("_Delete Layer", () => _canvas.DeleteLayer()),
                new Separator(),
                MenuAction("Move Layer _Up", () => _canvas.MoveActiveLayer(1)),
                MenuAction("Move Layer _Down", () => _canvas.MoveActiveLayer(-1))
            }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Menu
            {
                Background = Avalonia.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                ItemsSource = new[] { fileMenu, brushMenu, layerMenu }
            }
        };
    }

    private static MenuItem MenuAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Left rail ─────────────────────────────────────────────────────────────
    private Control BuildLeftRail()
    {
        _toolStatusText = new TextBlock
        {
            Text              = "Brush",
            Foreground        = new SolidColorBrush(Color.Parse(TextMuted)),
            FontSize          = 9,
            TextAlignment     = TextAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin            = new Thickness(0, 1, 0, 4)
        };

        _colorWell = new Border
        {
            Width            = 28,
            Height           = 28,
            CornerRadius     = new CornerRadius(14),
            BorderBrush      = new SolidColorBrush(Color.Parse("#4b5260")),
            BorderThickness  = new Thickness(1.5),
            Background       = new SolidColorBrush(Color.Parse("#111111"))
        };

        var colorBtn = new Button
        {
            Content            = _colorWell,
            Width              = 42,
            Height             = 42,
            Background         = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush        = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness    = new Thickness(1),
            CornerRadius       = new CornerRadius(8),
            Padding            = new Thickness(6),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(colorBtn, "Cycle color  (X)");
        colorBtn.Click += (_, _) => CycleColor();

        _brushToolButton  = RailBtn("⬤", "Brush  (B)");
        _eraserToolButton = RailBtn("◎", "Eraser  (E)");
        _brushToolButton.Click  += (_, _) => SetTool("brush");
        _eraserToolButton.Click += (_, _) => SetTool("eraser");

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
            Margin              = new Thickness(0, 12),
            Spacing             = 8
        };
        stack.Children.Add(_brushToolButton);
        stack.Children.Add(_eraserToolButton);
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
            Background      = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
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
            Width    = 42,
            Height   = 42,
            FontSize = 15,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            Background      = Avalonia.Media.Brushes.Transparent,
            BorderBrush     = Avalonia.Media.Brushes.Transparent,
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(0)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Border RailSep() => new()
    {
        Height     = 1,
        Width      = 34,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin     = new Thickness(0, 2)
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
            Background      = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
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
            Foreground        = new SolidColorBrush(Color.Parse(TextMuted))
        };
        var titleText = new TextBlock
        {
            Text          = title.ToUpperInvariant(),
            FontSize      = 9,
            FontWeight    = FontWeight.SemiBold,
            LetterSpacing = 1.2,
            Foreground    = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var headerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 5
        };
        headerRow.Children.Add(arrow);
        headerRow.Children.Add(titleText);

        var contentWrap = new Border { Child = content, IsVisible = startExpanded, Padding = new Thickness(0, 4, 0, 0) };

        var headerBtn = new Button
        {
            Content            = headerRow,
            Padding            = new Thickness(12, 8, 12, 10),
            Background         = new SolidColorBrush(Color.Parse(Bg0)),
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
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
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
            Margin = new Thickness(12, 0, 12, 10)
        };
        _colorPicker.HsvChanged += OnPickerHsvChanged;

        _hexInput = new TextBox
        {
            Width                    = 112,
            Height                   = 30,
            FontSize                 = 12,
            FontFamily               = new FontFamily("Consolas, Courier New, monospace"),
            Background               = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground               = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush              = new SolidColorBrush(Color.Parse("#3a4250")),
            BorderThickness          = new Thickness(1),
            Padding                  = new Thickness(8, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            CaretBrush               = new SolidColorBrush(Color.Parse(TextPrimary)),
            HorizontalAlignment      = Avalonia.Layout.HorizontalAlignment.Left
        };
        _hexInput.KeyDown   += (_, e) => { if (e.Key is Key.Enter or Key.Return) TryApplyHexColor(_hexInput.Text ?? ""); };
        _hexInput.LostFocus += (_, _) => TryApplyHexColor(_hexInput.Text ?? "");

        _swatchPanel = new WrapPanel
        {
            Margin  = new Thickness(12, 6, 12, 10),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };

        return new StackPanel
        {
            Children =
            {
                _colorPicker,
                new Border { Margin = new Thickness(12, 0, 12, 6), Child = _hexInput },
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
            Margin  = new Thickness(12, 0, 12, 10),
            Spacing = 6,
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
        _brushCategoryPanel = new StackPanel { Spacing = 5 };
        _presetPanel        = new StackPanel { Spacing = 5 };
        var importPngBtn = SmBtn("PNG", "Import brush tip PNG");
        _saveBrushButton = SmBtn("S", "Save brush");
        var duplicateBrushBtn = SmBtn("⎘", "Duplicate brush");
        importPngBtn.Width = 54;
        _saveBrushButton.Width = 34;
        duplicateBrushBtn.Width = 34;
        importPngBtn.Click += async (_, _) => await ImportBrushTipPngAsync();
        _saveBrushButton.Click += (_, _) => SaveActiveBrush();
        duplicateBrushBtn.Click += (_, _) => DuplicateActiveBrush();

        var brushTools = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            Children = { importPngBtn, _saveBrushButton, duplicateBrushBtn }
        };

        var presetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            MaxHeight = 240,
            Content   = _presetPanel
        };

        return new StackPanel
        {
            Margin  = new Thickness(12, 0, 12, 10),
            Spacing = 8,
            Children = { _brushCategoryPanel, brushTools, presetScroll }
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
            Spacing     = 6,
            Margin      = new Thickness(12, 0, 12, 8),
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
            Spacing = 6,
            Children =
            {
                ctrlRow,
                new Border { Margin = new Thickness(12, 0, 12, 6), Child = LabelSlider("Opacity", _layerOpacitySlider) },
                new Border { Margin = new Thickness(6, 0, 6, 10),   Child = _layerPanel }
            }
        };
    }

    // ── Widget helpers ────────────────────────────────────────────────────────
    private static Button SmBtn(string glyph, string tip)
    {
        var btn = new Button
        {
            Content         = glyph,
            Width           = 30,
            Height          = 30,
            Padding         = new Thickness(0),
            FontSize        = 12,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            Background      = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush     = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Slider MkSlider(double min, double max, double value, string tip)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Height = 30,
            MinHeight = 30,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(s, tip);
        return s;
    }

    private static Control LabelSlider(string label, Slider slider)
    {
        var lbl = new TextBlock
        {
            Text              = label,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width             = 78,
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

        _canvas.StatsChanged   += (_, _) => UpdateStatus();
        _canvas.HistoryChanged += (_, _) => UpdateStatus();
        _canvas.LayersChanged  += (_, _) => { BuildLayerList(); UpdateStatus(); };
        _canvas.LayerMetadataChanged += (_, e) => { UpdateLayerRow(e.LayerIndex); UpdateStatus(); };

        SliderChanged(_sizeSlider,      v => UpdateCurrentBrush(p => p with { Size = v }));
        SliderChanged(_opacitySlider,   v => UpdateCurrentBrush(p => p with { Opacity = v }));
        SliderChanged(_hardnessSlider,  v => UpdateCurrentBrush(p => p with { Hardness = v }));
        SliderChanged(_spacingSlider,   v => UpdateCurrentBrush(p => p with { Spacing = v }));
        SliderChanged(_smoothingSlider, v => UpdateCurrentBrush(p => p with { Smoothing = v }));
        SliderChanged(_grainSlider,     v => UpdateCurrentBrush(p => p with { Grain = v }));

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
            Width           = 22,
            Height          = 22,
            Margin          = new Thickness(0, 0, 6, 6),
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
                Padding            = new Thickness(12, 9),
                Background         = new SolidColorBrush(selected ? Color.Parse(AccentSoft) : Color.Parse(Bg2)),
                Foreground         = new SolidColorBrush(selected ? Color.Parse(TextPrimary) : Color.Parse(TextSecondary)),
                BorderBrush        = new SolidColorBrush(selected ? Color.Parse(Accent) : Color.Parse(Stroke)),
                BorderThickness    = new Thickness(1),
                CornerRadius       = new CornerRadius(7),
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
        var assets = _selectedBrushKind is null
            ? _brushAssets
            : _brushAssets.Where(p => p.Preset.Kind == _selectedBrushKind).ToArray();

        foreach (var asset in assets)
        {
            var preset = asset.Preset;
            var isActive = _activeBrushAsset?.Id == asset.Id;
            var row = new Button
            {
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background      = new SolidColorBrush(isActive ? Color.Parse(AccentSoft) : Color.Parse(Bg2)),
                BorderBrush     = new SolidColorBrush(isActive ? Color.Parse(Accent) : Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(7),
                Padding         = new Thickness(11, 9),
                Tag             = asset
            };
            var nameText = new TextBlock
            {
                Text       = preset.Name,
                Foreground = new SolidColorBrush(isActive ? Color.Parse("#ffffff") : Color.Parse(TextPrimary)),
                FontWeight = FontWeight.SemiBold,
                FontSize   = 11
            };
            var previewText = new TextBlock
            {
                Text       = PreviewStroke(preset) + "  " + TipLabel(asset.Tip),
                Foreground = new SolidColorBrush(isActive ? Color.Parse("#eef3ff") : Color.Parse(TextSecondary)),
                FontSize   = 14,
                Margin     = new Thickness(0, 2, 0, 0)
            };
            var col = new StackPanel { Children = { nameText, previewText } };
            row.Content = col;
            row.Click  += (_, _) => ApplyBrushAsset(asset);
            _presetPanel.Children.Add(row);
        }
    }

    private void LoadBrushAssets()
    {
        _brushAssets = _brushLibrary.Load();
        BuildPresets();
    }

    private void SelectInitialBrush()
    {
        var initial = _brushAssets.FirstOrDefault(b => b.Preset.Name == App.Config.LastBrushName)
            ?? _brushAssets.FirstOrDefault()
            ?? BrushAsset.FromPreset(BrushPreset.Defaults[0]);
        ApplyBrushAsset(initial);
    }

    private static string PreviewStroke(BrushPreset p) => p.Kind switch
    {
        BrushKind.Pencil   => "╍╍╍╍╍╍╍",
        BrushKind.Marker   => "━━━━━━",
        BrushKind.Airbrush => "░░░░░░░",
        _                  => "━━━━━━"
    };

    private static string TipLabel(BrushTipData tip)
        => tip.Kind == BrushTipStorageKind.EmbeddedPng ? "PNG" : tip.Shape.ToString();

    private void ApplyBrushAsset(BrushAsset asset)
    {
        _activeBrushAsset = asset;
        ApplyPreset(asset.ToPreset(), syncSliders: true);
    }

    private void ApplyPreset(BrushPreset preset, bool syncSliders)
    {
        _activePreset = preset;
        if (syncSliders)
        {
            _syncingBrushUi = true;
            _sizeSlider.Value = Math.Clamp(preset.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
            _opacitySlider.Value = Math.Clamp(preset.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
            _hardnessSlider.Value = Math.Clamp(preset.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
            _spacingSlider.Value = Math.Clamp(preset.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
            _smoothingSlider.Value = Math.Clamp(preset.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
            _grainSlider.Value = Math.Clamp(preset.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
            _syncingBrushUi = false;
        }

        var applied = preset with
        {
            Color    = _canvas.PaintColor,
            Size     = _sizeSlider.Value,
            Opacity  = _opacitySlider.Value,
            Hardness = _hardnessSlider.Value,
            Spacing  = _spacingSlider.Value
        };
        _canvas.SetBrush(applied);
        SetTool(preset.Kind == BrushKind.Eraser ? "eraser" : "brush");
        BuildPresets();
        UpdateStatus();
    }

    private void UpdateCurrentBrush(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncingBrushUi || _activePreset == null) return;
        var updated = update(_activePreset);
        _activePreset = updated;
        if (_activeBrushAsset != null)
        {
            _activeBrushAsset.Preset = updated;
            _activeBrushAsset.Tip = BrushTipData.FromTip(updated.Tip);
        }
        ApplyPreset(updated, syncSliders: false);
    }

    private void SaveActiveBrush()
    {
        if (_activeBrushAsset == null || _activePreset == null) return;
        _activeBrushAsset.WithPreset(CurrentBrushFromUi());
        _brushLibrary.Save(_activeBrushAsset);
        LoadBrushAssets();
        _activeBrushAsset = _brushAssets.FirstOrDefault(b => b.Id == _activeBrushAsset.Id) ?? _activeBrushAsset;
        _footerStatusText.Text = $"Saved brush {_activeBrushAsset.Preset.Name}";
    }

    private void DuplicateActiveBrush()
    {
        if (_activeBrushAsset == null) return;
        var copy = _activeBrushAsset.CloneForSaveAs(_activeBrushAsset.Preset.Name + " Copy");
        copy.WithPreset(CurrentBrushFromUi() with { Name = copy.Preset.Name });
        _brushLibrary.Save(copy);
        LoadBrushAssets();
        var saved = _brushAssets.FirstOrDefault(b => b.Id == copy.Id);
        if (saved != null) ApplyBrushAsset(saved);
    }

    private async System.Threading.Tasks.Task ImportBrushTipPngAsync()
    {
        if (_activeBrushAsset == null || _activePreset == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import brush tip PNG",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        var tip = new BrushTipData
        {
            Kind = BrushTipStorageKind.EmbeddedPng,
            PngBytes = memory.ToArray()
        };
        _activeBrushAsset.Tip = tip;
        _activeBrushAsset.Preset = CurrentBrushFromUi() with { Tip = tip.CreateTip() };
        ApplyPreset(_activeBrushAsset.ToPreset(), syncSliders: false);
        BuildPresets();
        _footerStatusText.Text = $"Embedded PNG tip in {_activeBrushAsset.Preset.Name}";
    }

    private BrushPreset CurrentBrushFromUi()
    {
        var source = _activePreset ?? BrushPreset.Defaults[0];
        return source with
        {
            Size = _sizeSlider.Value,
            Opacity = _opacitySlider.Value,
            Hardness = _hardnessSlider.Value,
            Spacing = _spacingSlider.Value,
            Smoothing = _smoothingSlider.Value,
            Grain = _grainSlider.Value
        };
    }

    // ── Tool selection ────────────────────────────────────────────────────────
    private void SetTool(string tool)
    {
        _canvas.SetTool(tool);
        _toolStatusText.Text   = tool == "eraser" ? "Eraser" : _canvas.Brush.Name;
        _footerStatusText.Text = tool == "eraser" ? "Eraser" : "Brush";
        var eraser = tool == "eraser";
        SetRailActive(_brushToolButton, !eraser);
        SetRailActive(_eraserToolButton, eraser);
    }

    private static void SetRailActive(Button button, bool active)
    {
        button.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : "Transparent"));
        button.BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : "Transparent"));
        button.BorderThickness = new Thickness(active ? 1 : 0);
        button.Padding = new Thickness(active ? 1 : 0);
    }

    // ── Layer panel ───────────────────────────────────────────────────────────
    private void BuildLayerList()
    {
        _layerPanel.Children.Clear();
        _layerRows.Clear();
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
            _layerRows[i] = new LayerRowRefs(row, visBtn, lockBtn, nameBtn, opacityText);
        }

        if (layers.Count > 0)
        {
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = layers[_canvas.ActiveLayerIndex].Opacity;
            _syncingLayerUi = false;
        }
    }

    private void UpdateLayerRow(int index)
    {
        var layers = _canvas.Layers;
        if (index < 0 || index >= layers.Count || !_layerRows.TryGetValue(index, out var refs))
        {
            BuildLayerList();
            return;
        }

        var layer = layers[index];
        var isActive = index == _canvas.ActiveLayerIndex;

        refs.Row.Background = new SolidColorBrush(isActive ? Color.Parse("#1a2a50") : Color.Parse("#1a1c22"));
        refs.Row.BorderBrush = new SolidColorBrush(isActive ? Color.Parse("#2e5fb8") : Color.Parse("#22252e"));
        refs.VisibilityButton.Content = layer.IsVisible ? "●" : "○";
        refs.LockButton.Content = layer.IsLocked ? "L" : "·";
        refs.NameButton.Foreground = new SolidColorBrush(isActive ? Color.Parse("#d8e0f0") : Color.Parse("#909aa8"));
        refs.OpacityText.Text = $"{Math.Round(layer.Opacity * 100)}%";
        refs.OpacityText.Foreground = new SolidColorBrush(isActive ? Color.Parse("#5a80c8") : Color.Parse("#404550"));

        if (isActive && !_syncingLayerUi)
        {
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = layer.Opacity;
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

    private sealed record LayerRowRefs(
        Border Row,
        Button VisibilityButton,
        Button LockButton,
        Button NameButton,
        TextBlock OpacityText);

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
            ClampCanvasPan();
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
        ClampCanvasPan();
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

        ClampCanvasPan();
        UpdateStatus();
    }

    private void SetRotation(double degrees)
    {
        _rotation = degrees % 360;
        _canvasRotate.Angle      = _rotation;
        _canvas.CanvasRotation   = _rotation;
        ClampCanvasPan();
        UpdateStatus();
    }

    private void ClampCanvasPan()
    {
        var vpW = Math.Max(1, _workspaceViewport.Bounds.Width);
        var vpH = Math.Max(1, _workspaceViewport.Bounds.Height);
        if (vpW <= 1 || vpH <= 1) return;

        var angle = Math.Abs(_rotation % 180) * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));
        var docW = Math.Max(1, _canvas.Document.Width) * _zoom;
        var docH = Math.Max(1, _canvas.Document.Height) * _zoom;
        var rotatedW = docW * cos + docH * sin;
        var rotatedH = docW * sin + docH * cos;

        var marginX = Math.Min(vpW * 0.45, 360);
        var marginY = Math.Min(vpH * 0.45, 360);
        var maxX = Math.Max(marginX, (rotatedW - vpW) * 0.5 + marginX);
        var maxY = Math.Max(marginY, (rotatedH - vpH) * 0.5 + marginY);

        _canvasPan.X = Math.Clamp(_canvasPan.X, -maxX, maxX);
        _canvasPan.Y = Math.Clamp(_canvasPan.Y, -maxY, maxY);
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
            _canvas.PaintInputSuspended = true;
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
            _canvas.PaintInputSuspended = false;
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
