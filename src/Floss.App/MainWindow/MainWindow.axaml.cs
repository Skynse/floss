using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.FlossFiles;
using Floss.App.ImageFiles;
using Floss.App.Input;
using Floss.App.Kra;
using Floss.App.Psd;
using Floss.App.Tools;

namespace Floss.App;

public partial class MainWindow : Window
{
    private const double ResetViewOutset = 80.0;

    private const string Bg0 = "#0f0f10";
    private const string Bg1 = "#161618";
    private const string Bg2 = "#1e1e20";
    private const string Bg3 = "#252527";
    private const string Stroke = "#2e2e32";
    private const string TextPrimary = "#dde1e8";
    private const string TextSecondary = "#9ea8b4";
    private const string TextMuted = "#5e6878";
    private const string Accent = "#4878d8";
    private const string AccentSoft = "#1e2e52";

    // ── Palette ───────────────────────────────────────────────────────────────
    // 10-column × 8-row palette: grays | red | orange | yellow | lime | green | teal | blue | purple | pink
    private static readonly Color[] _swatches =
    [
        // row 0 – near-white / pastels
        Color.Parse("#ffffff"), Color.Parse("#ffe6e6"), Color.Parse("#fff0e6"),
        Color.Parse("#ffffe6"), Color.Parse("#f0ffe6"), Color.Parse("#e6ffe6"),
        Color.Parse("#e6fff0"), Color.Parse("#e6f0ff"), Color.Parse("#eee6ff"),
        Color.Parse("#ffe6f8"),
        // row 1 – light
        Color.Parse("#d4d4d4"), Color.Parse("#ffb3b3"), Color.Parse("#ffd0a0"),
        Color.Parse("#ffff99"), Color.Parse("#ccf099"), Color.Parse("#99ee99"),
        Color.Parse("#99eecc"), Color.Parse("#99ccff"), Color.Parse("#c0aeff"),
        Color.Parse("#ffaadd"),
        // row 2 – medium-light
        Color.Parse("#a0a0a0"), Color.Parse("#ff6666"), Color.Parse("#ffa040"),
        Color.Parse("#ffee44"), Color.Parse("#aadd33"), Color.Parse("#44cc55"),
        Color.Parse("#33ccaa"), Color.Parse("#4499ff"), Color.Parse("#9966ff"),
        Color.Parse("#ff55bb"),
        // row 3 – medium
        Color.Parse("#747474"), Color.Parse("#ff2222"), Color.Parse("#ff7700"),
        Color.Parse("#ffdd00"), Color.Parse("#88cc00"), Color.Parse("#00bb33"),
        Color.Parse("#00aa88"), Color.Parse("#1166ee"), Color.Parse("#7733ff"),
        Color.Parse("#ff22aa"),
        // row 4 – vivid
        Color.Parse("#505050"), Color.Parse("#ee0000"), Color.Parse("#ff5500"),
        Color.Parse("#eebb00"), Color.Parse("#66aa00"), Color.Parse("#009933"),
        Color.Parse("#009977"), Color.Parse("#0044dd"), Color.Parse("#5500ee"),
        Color.Parse("#dd0088"),
        // row 5 – medium-dark
        Color.Parse("#383838"), Color.Parse("#aa0000"), Color.Parse("#cc4400"),
        Color.Parse("#aa8800"), Color.Parse("#448800"), Color.Parse("#006622"),
        Color.Parse("#006655"), Color.Parse("#0033aa"), Color.Parse("#3300aa"),
        Color.Parse("#aa0066"),
        // row 6 – dark
        Color.Parse("#222222"), Color.Parse("#660000"), Color.Parse("#883300"),
        Color.Parse("#665500"), Color.Parse("#224400"), Color.Parse("#003311"),
        Color.Parse("#003333"), Color.Parse("#001177"), Color.Parse("#220066"),
        Color.Parse("#660033"),
        // row 7 – near-black
        Color.Parse("#0d0d0d"), Color.Parse("#2d0000"), Color.Parse("#3d1800"),
        Color.Parse("#2d2600"), Color.Parse("#0f1e00"), Color.Parse("#001508"),
        Color.Parse("#001515"), Color.Parse("#000633"), Color.Parse("#0d0022"),
        Color.Parse("#2d0015"),
    ];

    private readonly BrushPaletteConfig _brushPaletteConfig = new();
    private string? _selectedBrushCategory;

    // ── Blend modes ───────────────────────────────────────────────────────────
    private static readonly string[] BlendModes =
    [
        "Normal", "Dissolve",
        "Multiply", "Screen", "Overlay", "SoftLight", "HardLight",
        "ColorDodge", "ColorBurn", "LinearDodge", "LinearBurn",
        "Darken", "Lighten", "DarkerColor", "LighterColor",
        "Difference", "Exclusion", "Subtract", "Divide",
        "Hue", "Saturation", "Color", "Luminosity",
        "VividLight", "LinearLight", "PinLight", "HardMix",
    ];

    private static string BlendAbbr(string mode) => mode switch
    {
        "Normal" => "Nrm",
        "Dissolve" => "Dis",
        "Multiply" => "Mul",
        "Screen" => "Scr",
        "Overlay" => "Ovl",
        "SoftLight" => "SL",
        "HardLight" => "HL",
        "ColorDodge" => "CDg",
        "ColorBurn" => "CBn",
        "LinearDodge" => "LDg",
        "LinearBurn" => "LBn",
        "Darken" => "Drk",
        "Lighten" => "Lgt",
        "DarkerColor" => "DC",
        "LighterColor" => "LC",
        "Difference" => "Dif",
        "Exclusion" => "Exc",
        "Subtract" => "Sub",
        "Divide" => "Div",
        "Hue" => "Hue",
        "Saturation" => "Sat",
        "Color" => "Col",
        "Luminosity" => "Lum",
        "VividLight" => "VL",
        "LinearLight" => "LL",
        "PinLight" => "PL",
        "HardMix" => "HM",
        "PassThrough" => "PT",
        _ => mode[..Math.Min(3, mode.Length)]
    };

    // ── Controls ──────────────────────────────────────────────────────────────
    private DrawingCanvas _canvas = null!;
    private Grid _workspaceViewport = null!;
    private Border _canvasFrame = null!;
    private HsvColorPicker _colorPicker = null!;
    private TextBox _hexInput = null!;
    private Border _colorWell = null!;
    private WrapPanel _swatchPanel = null!;
    private StackPanel _brushCategoryPanel = null!;
    private StackPanel _presetPanel = null!;
    private StackPanel _toolPropertyPanel = null!;
    private TextBlock _toolPropertyTitle = null!;
    private StackPanel _layerPanel = null!;
    private Slider _sizeSlider = null!;
    private Slider _opacitySlider = null!;
    private Slider _hardnessSlider = null!;
    private Slider _spacingSlider = null!;
    private Slider _smoothingSlider = null!;
    private Slider _grainSlider = null!;
    private Slider _flowSlider = null!;
    private Slider _layerOpacitySlider = null!;
    private TextBlock _activeBrushLabel = null!;
    private ComboBox _blendModeComboBox = null!;
    private TextBox _layerNameBox = null!;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Button _brushToolButton = null!;
    private Button _eraserToolButton = null!;
    private Button _smudgeToolButton = null!;
    private Button _moveToolButton = null!;
    private Button _selectToolButton = null!;
    private Button _wandToolButton = null!;
    private Button _fillToolButton = null!;
    private Button _lassoFillToolButton = null!;
    private Button _eyedropToolButton = null!;
    private Button _gradientToolButton = null!;
    private Button _shapeToolButton = null!;
    private Button _polylineToolButton = null!;
    private Button _deleteLayerButton = null!;
    private Button _moveLayerUpButton = null!;
    private Button _moveLayerDownButton = null!;
    private TextBlock _toolStatusText = null!;
    private TextBlock _footerStatusText = null!;
    private TextBlock _canvasStatusText = null!;
    private TextBlock _zoomDisplay = null!;
    private TextBlock _rotDisplay = null!;
    private Button _saveBrushButton = null!;
    private BrushStrokePreview _strokePreview = null!;
    private Border? _paperSwatch;
    private Button? _paperVisBtn;

    // ── State ─────────────────────────────────────────────────────────────────
    private BrushEditorWindow? _brushEditorWindow;
    private Window? _toolPropertyDetailWindow;
    private double _zoom = 1.0;
    private double _rotation;
    private int _swatchIndex;

    private BrushPreset? _activePreset;
    private BrushAsset? _activeBrushAsset;
    private BrushLibrary _brushLibrary = null!;
    private IReadOnlyList<BrushAsset> _brushAssets = [];
    private readonly Dictionary<int, LayerRowRefs> _layerRows = new();
    private readonly HashSet<int> _selectedLayerIndices = new();
    private readonly HashSet<string> _collapsedSections = ["Tool Property"];
    private string? _currentFlossPath;
    private int _layerDragSourceIndex = -1;
    private int _renamingLayerIndex = -1;
    private TextBox? _activeLayerNameEdit;
    private Action<bool>? _finishLayerRename;

    // ── Tool instances ────────────────────────────────────────────────────────
    private readonly SelectTool _selectTool = new();
    private readonly MagicWandTool _magicWandTool = new();
    private readonly FillTool _fillTool = new();
    private readonly LassoFillTool _lassoFillTool = new();
    private readonly EyedropperTool _eyedropperTool = new();
    private readonly MoveTool _moveTool = new();
    private readonly GradientTool _gradientTool = new();
    private readonly ShapeTool _shapeTool = new();
    private readonly PolylineTool _polylineTool = new();
    private readonly List<Button> _toolButtons = [];

    private enum GestureMode { None, Pan, Zoom, Rotate, BrushSize }
    private GestureMode _activeGesture;
    private Key _gestureKey = Key.None;
    private KeyModifiers _gestureModifiers;
    private bool _isPanning;
    private Point _lastPanPoint;
    private Point _gestureStartPoint;
    private SettingsWindow? _settingsWindow;

    private ScaleTransform _canvasScale = null!;
    private RotateTransform _canvasRotate = null!;
    private TranslateTransform _canvasPan = null!;

    private bool _syncingLayerUi;
    private bool _syncingBrushUi;
    private ITool? _preAltTool;
    private Button? _preAltToolButton;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        _brushLibrary = new BrushLibrary(AppPaths.BrushesDirectory);
        _brushPaletteConfig = BrushPaletteConfig.Load(AppPaths.BrushPaletteConfigPath);
        BuildUi();
        WireControls();
        RestoreFromConfig();
        BuildSwatches();
        BuildBrushCategories();
        LoadBrushAssets();
        SelectInitialBrush();
        SetColor(Color.Parse(App.Config.LastColor));
        SyncCanvasFrameToDocument(fitToViewport: false);
        if (_canvas.Layers.Count > 0) _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);
        BuildLayerList();
        UpdateStatus();
        Closing += (_, _) => SaveToConfig();
    }

    // ── Root layout ───────────────────────────────────────────────────────────
    private void BuildUi()
    {
        _canvas = new DrawingCanvas();

        var tg = new TransformGroup();
        _canvasScale = new ScaleTransform(1, 1);
        _canvasRotate = new RotateTransform(0);
        _canvasPan = new TranslateTransform(0, 0);
        tg.Children.Add(_canvasScale);
        tg.Children.Add(_canvasRotate);
        tg.Children.Add(_canvasPan);

        _canvasFrame = new Border
        {
            RenderTransform = tg,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Child = _canvas,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        _workspaceViewport = new Grid
        {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#111112"))
        };
        _workspaceViewport.Children.Add(_canvasFrame);

        _canvasStatusText = MiniText();
        _footerStatusText = MiniText();

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 26,
            Padding = new Thickness(12, 0),
            Child = _canvasStatusText
        };

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 22,
            Padding = new Thickness(12, 0),
            Child = _footerStatusText
        };

        var centerArea = new Grid { RowDefinitions = new RowDefinitions("26,*,22") };
        Grid.SetRow(statusBar, 0);
        Grid.SetRow(_workspaceViewport, 1);
        Grid.SetRow(footer, 2);
        centerArea.Children.Add(statusBar);
        centerArea.Children.Add(_workspaceViewport);
        centerArea.Children.Add(footer);

        var leftRail = BuildLeftRail();
        var rightPanel = BuildRightPanel();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(48, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
        root.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(320, GridUnitType.Pixel) { MinWidth = 200, MaxWidth = 700 });

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg2))
        };

        Grid.SetColumn(leftRail, 0);
        Grid.SetColumn(centerArea, 1);
        Grid.SetColumn(splitter, 2);
        Grid.SetColumn(rightPanel, 3);
        root.Children.Add(leftRail);
        root.Children.Add(centerArea);
        root.Children.Add(splitter);
        root.Children.Add(rightPanel);

        var shell = new Grid { RowDefinitions = new RowDefinitions("30,34,*") };
        var menu = BuildMenuBar();
        var toolbar = BuildToolbar();
        Grid.SetRow(menu, 0);
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(root, 2);
        shell.Children.Add(menu);
        shell.Children.Add(toolbar);
        shell.Children.Add(root);

        Content = shell;
        AddHandler(PointerPressedEvent, WindowPointerPressed, RoutingStrategies.Tunnel);
    }

    private static TextBlock MiniText() => new()
    {
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        FontSize = 11,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private Control BuildMenuBar()
    {
        var fileMenu = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_Open...", async () => await OpenDocumentAsync()),
                MenuAction("_Save Floss", async () => await SaveFlossAsync()),
                MenuAction("_Save Floss As...", async () => await SaveFlossAsAsync()),
                MenuAction("_Save PSD...", async () => await SavePsdAsync()),
                MenuAction("_Export Image...", async () => await ExportImageAsync()),
                new Separator(),
                MenuAction("_Reset View", ResetView),
                new Separator(),
                MenuAction("_Settings...", OpenSettings)
            }
        };

        var brushMenu = new MenuItem
        {
            Header = "_Brush",
            ItemsSource = new object[]
            {
                MenuAction("_Save Brush", SaveActiveBrush),
                MenuAction("_Duplicate Brush", DuplicateActiveBrush),
                MenuAction("_Import Tip PNG...", async () => await ImportBrushTipPngAsync()),
                MenuAction("_Import .abr Brushes...", async () => await ImportAbrAsync())
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

    private Control BuildToolbar()
    {
        _zoomDisplay = new TextBlock
        {
            Text = "100%",
            Width = 46,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        _rotDisplay = new TextBlock
        {
            Text = "0°",
            Width = 36,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var zoomOut = TbarBtn(Icons.MagnifyMinus, "Zoom out  (Ctrl+−)");
        var zoomIn = TbarBtn(Icons.MagnifyPlus, "Zoom in  (Ctrl++)");
        var zoomFit = TbarBtn(Icons.FitToScreenOutline, "Fit to screen  (Ctrl+0)");
        zoomOut.Click += (_, _) => SetZoom(_zoom / 1.2, null);
        zoomIn.Click += (_, _) => SetZoom(_zoom * 1.2, null);
        zoomFit.Click += (_, _) => ResetView();

        var rotLeft = TbarBtn(Icons.RotateLeftVariant, "Rotate left 15°  (Shift+[)");
        var rotRight = TbarBtn(Icons.RotateRightVariant, "Rotate right 15°  (Shift+])");
        var rotReset = TbarBtn(Icons.Rotate360, "Reset rotation");
        rotLeft.Click += (_, _) => SetRotation(_rotation - 15);
        rotRight.Click += (_, _) => SetRotation(_rotation + 15);
        rotReset.Click += (_, _) => SetRotation(0);

        var undoTb = TbarBtn(Icons.UndoVariant, "Undo  (Ctrl+Z)");
        var redoTb = TbarBtn(Icons.RedoVariant, "Redo  (Ctrl+Shift+Z)");
        undoTb.Click += (_, _) => _canvas.Undo();
        redoTb.Click += (_, _) => _canvas.Redo();

        var openTb = TbarBtn(Icons.FolderOpenOutline, "Open PSD or image  (Ctrl+O)");
        var saveFlossTb = TbarBtn(Icons.ContentSaveOutline, "Save Floss document  (Ctrl+S)");
        var saveTb = TbarBtn(Icons.ContentSaveOutline, "Save PSD");
        var exportTb = TbarBtn(Icons.ContentSaveOutline, "Export image");
        openTb.Click += async (_, _) => await OpenDocumentAsync();
        saveFlossTb.Click += async (_, _) => await SaveFlossAsync();
        saveTb.Click += async (_, _) => await SavePsdAsync();
        exportTb.Click += async (_, _) => await ExportImageAsync();

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(8, 0)
        };
        row.Children.Add(openTb);
        row.Children.Add(saveFlossTb);
        row.Children.Add(saveTb);
        row.Children.Add(exportTb);
        row.Children.Add(TbarSep());
        row.Children.Add(undoTb);
        row.Children.Add(redoTb);
        row.Children.Add(TbarSep());
        row.Children.Add(zoomOut);
        row.Children.Add(_zoomDisplay);
        row.Children.Add(zoomIn);
        row.Children.Add(zoomFit);
        row.Children.Add(TbarSep());
        row.Children.Add(rotLeft);
        row.Children.Add(_rotDisplay);
        row.Children.Add(rotRight);
        row.Children.Add(rotReset);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private static Button TbarBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = MaterialIcon(icon, 16),
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            CornerRadius = new CornerRadius(5)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static PathIcon MaterialIcon(string pathData, double size) =>
        Icons.Make(pathData, size, new SolidColorBrush(Color.Parse(TextSecondary)));

    private static Button LayerIconBtn(string icon, string tip, string color, int tag)
    {
        var btn = new Button
        {
            Content = Icons.Make(icon, 10, new SolidColorBrush(Color.Parse(color))),
            Width = 14,
            Height = 14,
            Padding = new Thickness(0),
            Tag = tag,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static void SetLayerIconBtnIcon(Button btn, string icon, string color) =>
        btn.Content = Icons.Make(icon, 11, new SolidColorBrush(Color.Parse(color)));

    private static Border TbarSep() => new()
    {
        Width = 1,
        Height = 18,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(6, 0)
    };

    private static MenuItem MenuAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Right panel ───────────────────────────────────────────────────────────
    private Border BuildRightPanel()
    {
        var leftStack = new StackPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        leftStack.Children.Add(PanelSection("Brush", BuildBrushSection()));
        leftStack.Children.Add(PanelSection("Tool Property", BuildToolPropertySection()));
        leftStack.Children.Add(PanelSection("Layers", BuildLayersSection()));

        var leftScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ClipToBounds = true,
            Content = leftStack
        };

        var rightStack = new StackPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        rightStack.Children.Add(PanelSection("Color", BuildColorSection()));

        var rightScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ClipToBounds = true,
            Content = rightStack
        };

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Stroke))
        };

        var grid = new Grid { ClipToBounds = true };
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(rightScroll, 2);
        grid.Children.Add(leftScroll);
        grid.Children.Add(splitter);
        grid.Children.Add(rightScroll);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            Child = grid
        };
    }

    private Border PanelSection(string title, Control content)
    {
        var collapsed = _collapsedSections.Contains(title);
        content.IsVisible = !collapsed;

        var arrow = new TextBlock
        {
            Text = collapsed ? "▸" : "▾",
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var headerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { arrow, titleText }
        };
        var header = new Border
        {
            Padding = new Thickness(10, 6, 10, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerRow
        };
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            if (!_collapsedSections.Remove(title))
                _collapsedSections.Add(title);
            var nowCollapsed = _collapsedSections.Contains(title);
            content.IsVisible = !nowCollapsed;
            arrow.Text = nowCollapsed ? "▸" : "▾";
        };

        var outer = new StackPanel { Spacing = 0 };
        outer.Children.Add(header);
        outer.Children.Add(content);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = outer
        };
    }

    // ── Widget helpers ────────────────────────────────────────────────────────

    private static void SetToggleActive(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : "Transparent"));
        btn.BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : "Transparent"));
        btn.Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
    }

    private static Button SmBtn(string glyph, string tip)
    {
        var btn = new Button
        {
            Content = glyph,
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            FontSize = 11,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
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
            Height = 24,
            MinHeight = 20,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(s, tip);
        return s;
    }

    private static Control LabelSlider(string label, Slider slider, string fmt = "")
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var valText = new TextBlock
        {
            Text = FormatSliderValue(slider.Value, fmt),
            FontSize = 10,
            Width = 36,
            TextAlignment = TextAlignment.Right,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                valText.Text = FormatSliderValue(slider.Value, fmt);
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(valText, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(valText);
        row.Children.Add(slider);
        return row;
    }

    private static string FormatSliderValue(double v, string fmt) => fmt switch
    {
        "px" => $"{v:0}px",
        "%" => $"{v * 100:0}%",
        "f1" => $"{v:0.0}",
        "f2" => $"{v:0.00}",
        _ => v >= 10 ? $"{v:0}" : $"{v:0.0}"
    };

    // ── Config persistence ────────────────────────────────────────────────────
    private void RestoreFromConfig()
    {
        var cfg = App.Config;
        _sizeSlider.Value = Math.Clamp(cfg.LastBrushSize, _sizeSlider.Minimum, _sizeSlider.Maximum);
        _opacitySlider.Value = Math.Clamp(cfg.LastBrushOpacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
        _hardnessSlider.Value = Math.Clamp(cfg.LastBrushHardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
        _spacingSlider.Value = Math.Clamp(cfg.LastBrushSpacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
    }

    private void SaveToConfig()
    {
        var cfg = App.Config;
        cfg.LastBrushSize = _sizeSlider.Value;
        cfg.LastBrushOpacity = _opacitySlider.Value;
        cfg.LastBrushHardness = _hardnessSlider.Value;
        cfg.LastBrushSpacing = _spacingSlider.Value;
        var c = _canvas.PaintColor;
        cfg.LastColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        cfg.LastBrushName = _activePreset?.Name ?? "";
        cfg.Save();
    }

    // ── Wire-up ───────────────────────────────────────────────────────────────
    private void WireControls()
    {
        _workspaceViewport.PointerWheelChanged += Workspace_OnPointerWheelChanged;
        _workspaceViewport.PointerPressed += Workspace_OnPointerPressed;
        _workspaceViewport.PointerMoved += Workspace_OnPointerMoved;
        _workspaceViewport.PointerReleased += Workspace_OnPointerReleased;

        _canvas.StatsChanged += (_, _) => UpdateStatus();
        _canvas.HistoryChanged += (_, _) => UpdateStatus();
        _canvas.LayersChanged += (_, _) =>
        {
            _selectedLayerIndices.Clear();
            if (_canvas.Layers.Count > 0)
                _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);
            BuildLayerList();
            UpdateStatus();
        };
        _canvas.LayerMetadataChanged += (_, e) => { UpdateLayerRow(e.LayerIndex); UpdateStatus(); };
        _canvas.ColorSampled += (_, c) => SetColor(c, syncPicker: true, switchToBrush: false);
        _canvas.Document.PaperChanged += (_, _) => RefreshPaperRow();

        SliderChanged(_sizeSlider, v => UpdateCurrentBrush(p => p with { Size = v }));
        SliderChanged(_opacitySlider, v => UpdateCurrentBrush(p => p with { Opacity = v }));
        SliderChanged(_flowSlider, v => UpdateCurrentBrush(p => p with { Flow = v }));
        SliderChanged(_hardnessSlider, v => UpdateCurrentBrush(p => p with { Hardness = v }));
        SliderChanged(_spacingSlider, v => UpdateCurrentBrush(p => p with { Spacing = v }));
        SliderChanged(_smoothingSlider, v => UpdateCurrentBrush(p => p with { Smoothing = v }));
        SliderChanged(_grainSlider, v => UpdateCurrentBrush(p => p with { Grain = v }));

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    private static void SliderChanged(Slider slider, Action<double> action)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) action(slider.Value);
        };
    }

    // ── Tool selection ────────────────────────────────────────────────────────
    private Button? _activeToolButton;

    private void ActivateTool(ITool tool, Button button)
    {
        if (_canvas.IsTransformActive)
        {
            // In CSP-style transform mode, clicking another tool cancels the transform
            _canvas.CancelActiveTool();
            // After cancel, the previous tool is restored; if user clicked a different tool,
            // we still want to honor that switch, so fall through.
            if (_canvas.ActiveTool == tool) return;
        }

        _canvas.SetActiveTool(tool);
        _activeToolButton = button;
        foreach (var b in _toolButtons) SetRailActive(b, b == button);
        _footerStatusText.Text = ToolDisplayName(tool);
        _toolPropertyDetailWindow?.Close();
        RefreshToolProperties();
    }

    private string ToolDisplayName(ITool tool) => tool switch
    {
        BrushTool bt => bt.IsEraser ? "Eraser" : _canvas.Brush.Name,
        MoveTool => "Move",
        SelectTool => $"Select: {_selectTool.Mode}",
        TransformTool => "Transform",
        MagicWandTool => "Magic Wand",
        FillTool => "Fill",
        EyedropperTool => "Eyedropper",
        GradientTool => $"Gradient: {_gradientTool.GradientType}",
        ShapeTool => $"Shape: {_shapeTool.Kind}",
        PolylineTool => _polylineTool.ClosePath ? "Polyline: Closed" : "Polyline: Open",
        _ => "Tool"
    };

    private void CycleSelectMode()
    {
        _selectTool.Mode = _selectTool.Mode switch
        {
            SelectMode.Rect => SelectMode.Lasso,
            SelectMode.Lasso => SelectMode.PolylineLasso,
            _ => SelectMode.Rect
        };
        _footerStatusText.Text = ToolDisplayName(_selectTool);
        RefreshToolProperties();
    }

    private void CycleGradientMode()
    {
        _gradientTool.GradientType = _gradientTool.GradientType == GradientType.Linear
            ? GradientType.Radial
            : GradientType.Linear;
        _footerStatusText.Text = ToolDisplayName(_gradientTool);
        RefreshToolProperties();
    }

    private void CycleShapeMode()
    {
        _shapeTool.Kind = _shapeTool.Kind switch
        {
            ShapeKind.Rectangle => ShapeKind.Ellipse,
            ShapeKind.Ellipse => ShapeKind.Line,
            _ => ShapeKind.Rectangle
        };
        _footerStatusText.Text = ToolDisplayName(_shapeTool);
        RefreshToolProperties();
    }

    private void TogglePolylineClosePath()
    {
        _polylineTool.ClosePath = !_polylineTool.ClosePath;
        _footerStatusText.Text = ToolDisplayName(_polylineTool);
        RefreshToolProperties();
    }

    private void SetTool(string tool)
    {
        var (itool, btn) = tool switch
        {
            "eraser" => ((ITool)_canvas.EraserTool, _eraserToolButton),
            _ => (_canvas.BrushTool, _brushToolButton)
        };
        if (ReferenceEquals(_canvas.ActiveTool, itool)) return;
        ActivateTool(itool, btn);
    }

    private static void SetRailActive(Button button, bool active)
    {
        button.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : "Transparent"));
        button.BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : "Transparent"));
        button.BorderThickness = new Thickness(active ? 1 : 0);
        button.Padding = new Thickness(active ? 1 : 0);
    }

    // ── Status ────────────────────────────────────────────────────────────────
    private void UpdateStatus()
    {
        var layers = _canvas.Layers;
        if (layers.Count == 0) return;
        var activeIdx = _canvas.ActiveLayerIndex;
        var layer = layers[activeIdx];
        _canvasStatusText.Text =
            $"{Math.Round(_zoom * 100)}%  {Math.Round(_rotation)}°  " +
            $"layer {activeIdx + 1}/{layers.Count}  " +
            $"{layer.BlendMode}";
        _undoButton.IsEnabled = _canvas.CanUndo;
        _redoButton.IsEnabled = _canvas.CanRedo;
        _deleteLayerButton.IsEnabled = _canvas.CanDeleteLayer;
        _moveLayerUpButton.IsEnabled = activeIdx < layers.Count - 1;
        _moveLayerDownButton.IsEnabled = activeIdx > 0;

        if (_layerRows.TryGetValue(activeIdx, out var refs) && refs.PreviewImage != null)
        {
            layer.MarkThumbnailDirty();
            layer.RefreshThumbnail();
            refs.PreviewImage.InvalidateVisual();
        }
    }
}
