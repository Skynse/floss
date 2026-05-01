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
        root.ColumnDefinitions.Add(new ColumnDefinition(290, GridUnitType.Pixel) { MinWidth = 180, MaxWidth = 600 });

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

    // ── Left rail ─────────────────────────────────────────────────────────────
    private Control BuildLeftRail()
    {
        _toolStatusText = new TextBlock { IsVisible = false };

        _colorWell = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a3e")),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.Parse("#111112"))
        };
        var colorBtn = new Button
        {
            Content = _colorWell,
            Width = 36,
            Height = 34,
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(5),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(colorBtn, "Cycle color  (X)");
        colorBtn.Click += (_, _) => CycleColor();

        _brushToolButton = RailBtn(Icons.BrushOutline, "Brush  (B)");
        _eraserToolButton = RailBtn(Icons.Eraser, "Eraser  (E)");
        _moveToolButton = RailBtn(Icons.ArrowAll, "Move layer  (V)");
        _selectToolButton = RailBtn(Icons.SelectionRect, "Select  (S). Click again: rect/lasso/polyline");
        _wandToolButton = RailBtn(Icons.AutoFix, "Magic Wand  (W)");
        _fillToolButton = RailBtn(Icons.FormatColorFill, "Fill  (G)");
        _lassoFillToolButton = RailBtn(Icons.Lasso, "Lasso Fill  (L)");
        _eyedropToolButton = RailBtn(Icons.Eyedropper, "Eyedropper  (I)");
        _gradientToolButton = RailBtn(Icons.GradientHorizontal, "Gradient. Click again: linear/radial");
        _shapeToolButton = RailBtn(Icons.RectangleOutline, "Shape. Click again: rectangle/ellipse/line");
        _polylineToolButton = RailBtn(Icons.VectorPolyline, "Polyline. Click again: open/closed");

        _brushToolButton.Click += (_, _) => ActivateTool(_canvas.BrushTool, _brushToolButton);
        _eraserToolButton.Click += (_, _) => ActivateTool(_canvas.EraserTool, _eraserToolButton);
        _moveToolButton.Click += (_, _) => ActivateTool(_moveTool, _moveToolButton);
        _selectToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _selectTool)) CycleSelectMode();
            else ActivateTool(_selectTool, _selectToolButton);
        };
        _wandToolButton.Click += (_, _) => ActivateTool(_magicWandTool, _wandToolButton);
        _fillToolButton.Click += (_, _) => ActivateTool(_fillTool, _fillToolButton);
        _lassoFillToolButton.Click += (_, _) => ActivateTool(_lassoFillTool, _lassoFillToolButton);
        _eyedropToolButton.Click += (_, _) => ActivateTool(_eyedropperTool, _eyedropToolButton);
        _gradientToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _gradientTool)) CycleGradientMode();
            else ActivateTool(_gradientTool, _gradientToolButton);
        };
        _shapeToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _shapeTool)) CycleShapeMode();
            else ActivateTool(_shapeTool, _shapeToolButton);
        };
        _polylineToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _polylineTool)) TogglePolylineClosePath();
            else ActivateTool(_polylineTool, _polylineToolButton);
        };

        _toolButtons.AddRange([
            _brushToolButton, _eraserToolButton, _moveToolButton,
            _selectToolButton, _wandToolButton, _fillToolButton, _lassoFillToolButton,
            _eyedropToolButton, _gradientToolButton, _shapeToolButton, _polylineToolButton
        ]);

        _undoButton = RailBtn(Icons.UndoVariant, "Undo  (Ctrl+Z)");
        _redoButton = RailBtn(Icons.RedoVariant, "Redo  (Ctrl+Shift+Z)");
        _undoButton.Click += (_, _) => _canvas.Undo();
        _redoButton.Click += (_, _) => _canvas.Redo();

        var clearBtn = RailBtn(Icons.DeleteOutline, "Clear layer");
        clearBtn.Click += (_, _) => _canvas.Clear();

        var stack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 6),
            Spacing = 1
        };
        stack.Children.Add(_brushToolButton);
        stack.Children.Add(_eraserToolButton);
        stack.Children.Add(_moveToolButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(_selectToolButton);
        stack.Children.Add(_wandToolButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(_fillToolButton);
        stack.Children.Add(_lassoFillToolButton);
        stack.Children.Add(_gradientToolButton);
        stack.Children.Add(_shapeToolButton);
        stack.Children.Add(_polylineToolButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(_eyedropToolButton);
        stack.Children.Add(colorBtn);
        stack.Children.Add(RailSep());
        stack.Children.Add(_undoButton);
        stack.Children.Add(_redoButton);
        stack.Children.Add(clearBtn);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = stack
            }
        };
    }

    private static Button RailBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = MaterialIcon(icon, 18),
            Width = 36,
            Height = 34,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Border RailSep() => new()
    {
        Height = 1,
        Width = 26,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(0, 4)
    };

    // ── Right panel ───────────────────────────────────────────────────────────
    private Border BuildRightPanel()
    {
        var leftStack = new StackPanel();
        leftStack.Children.Add(PanelSection("Brush", BuildBrushSection()));
        leftStack.Children.Add(PanelSection("Tool Property", BuildToolPropertySection()));
        leftStack.Children.Add(PanelSection("Layers", BuildLayersSection()));

        var leftScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = leftStack
        };

        var rightStack = new StackPanel();
        rightStack.Children.Add(PanelSection("Color", BuildColorSection()));

        var rightScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rightStack
        };

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Stroke))
        };

        var grid = new Grid();
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
            Child = grid
        };
    }

    private static Border PanelSection(string title, Control content)
    {
        var header = new Border
        {
            Padding = new Thickness(10, 6, 10, 3),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse(TextMuted))
            }
        };
        var outer = new StackPanel { Spacing = 2 };
        outer.Children.Add(header);
        outer.Children.Add(content);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = outer
        };
    }

    // ── Color section ─────────────────────────────────────────────────────────
    private StackPanel BuildColorSection()
    {
        _colorPicker = new HsvColorPicker
        {
            Height = 168,
            Margin = new Thickness(12, 4, 12, 10)
        };
        _colorPicker.HsvChanged += OnPickerHsvChanged;

        _hexInput = new TextBox
        {
            Width = 112,
            Height = 26,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a4250")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        _hexInput.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Return) TryApplyHexColor(_hexInput.Text ?? ""); };
        _hexInput.LostFocus += (_, _) => TryApplyHexColor(_hexInput.Text ?? "");

        _swatchPanel = new WrapPanel
        {
            Margin = new Thickness(12, 2, 12, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            ItemWidth = SwatchSize + 2,
            ItemHeight = SwatchSize + 2
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
        // ── Library part ──────────────────────────────────────────────────────
        _brushCategoryPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4
        };
        _presetPanel = new StackPanel { Spacing = 2 };

        _strokePreview = new BrushStrokePreview { Height = 64 };

        var importPngBtn = SmBtn("PNG", "Import brush tip PNG");
        var importAbrBtn = SmBtn("ABR", "Import .abr brush pack");
        _saveBrushButton = SmBtn("⊙", "Save brush");
        var duplicateBrushBtn = SmBtn("⎘", "Duplicate brush");
        var editBrushBtn = SmBtn("✎", "Edit brush dynamics");
        importPngBtn.Width = 44;
        importAbrBtn.Width = 44;
        _saveBrushButton.Width = 30;
        duplicateBrushBtn.Width = 30;
        editBrushBtn.Width = 30;
        importPngBtn.Click += async (_, _) => await ImportBrushTipPngAsync();
        importAbrBtn.Click += async (_, _) => await ImportAbrAsync();
        _saveBrushButton.Click += (_, _) => SaveActiveBrush();
        duplicateBrushBtn.Click += (_, _) => DuplicateActiveBrush();
        editBrushBtn.Click += (_, _) => OpenBrushEditor();

        var presetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 160,
            Content = _presetPanel
        };

        // ── Parameters part ───────────────────────────────────────────────────
        _sizeSlider = MkSlider(1, 2000, 20, "Size");
        _opacitySlider = MkSlider(0.01, 1, 1.0, "Opacity");
        _flowSlider = MkSlider(0.01, 1, 1.0, "Flow — controls paint buildup per dab");
        _hardnessSlider = MkSlider(0, 1, 0.9, "Hardness — edge softness");
        _spacingSlider = MkSlider(0.02, 1, 0.1, "Spacing");
        _smoothingSlider = MkSlider(0, 0.95, 0.3, "Smoothing — input stabilization");
        _grainSlider = MkSlider(0, 1, 0.0, "Grain — noise texture");

        _activeBrushLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            Margin = new Thickness(0, 2, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var brushToolRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 3,
            Children = { importPngBtn, importAbrBtn, _saveBrushButton, duplicateBrushBtn, editBrushBtn }
        };

        var categoryScrollRow = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 30,
            Content = _brushCategoryPanel
        };

        return new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(10, 4, 10, 8),
            Children =
            {
                _strokePreview,
                new Border { Height = 4 },
                _activeBrushLabel,
                new Border { Height = 4 },
                categoryScrollRow,
                new Border { Height = 4 },
                presetScroll,
                new Border { Height = 2 },
                brushToolRow,
            }
        };
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

    // ── Layers section ────────────────────────────────────────────────────────
    private Control BuildLayersSection()
    {
        // Layer name
        _layerNameBox = new TextBox
        {
            PlaceholderText = "Layer name",
            FontSize = 11,
            Height = 26,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a4250")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _layerNameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyLayerName();
            e.Handled = true;
        };
        _layerNameBox.LostFocus += (_, _) => ApplyLayerName();

        // Blend mode
        _blendModeComboBox = new ComboBox
        {
            ItemsSource = BlendModes,
            SelectedItem = "Normal",
            FontSize = 11,
            Padding = new Thickness(6, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _blendModeComboBox.SelectionChanged += (_, _) =>
        {
            if (_syncingLayerUi) return;
            if (_blendModeComboBox.SelectedItem is string mode)
                _canvas.SetActiveLayerBlendMode(mode);
        };

        // Opacity
        _layerOpacitySlider = MkSlider(0, 1, 1, "Layer opacity");
        _layerOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (_syncingLayerUi || e.Property != Slider.ValueProperty) return;
            _canvas.SetActiveLayerOpacity(_layerOpacitySlider.Value);
        };

        // Blend + Opacity row
        var blendOpRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
        Grid.SetColumn(_blendModeComboBox, 0);
        Grid.SetColumn(_layerOpacitySlider, 2);
        blendOpRow.Children.Add(_blendModeComboBox);
        blendOpRow.Children.Add(_layerOpacitySlider);

        // Action buttons
        var addBtn = SmBtn("+", "Add layer  (Ctrl+Shift+N)");
        var folderBtn = SmBtn("▣", "Add layer folder");
        var dupBtn = SmBtn("⎘", "Duplicate  (Ctrl+J)");
        _deleteLayerButton = SmBtn("✕", "Delete  (Ctrl+Delete)");
        _moveLayerUpButton = SmBtn("↑", "Move up  (Ctrl+Up)");
        _moveLayerDownButton = SmBtn("↓", "Move down  (Ctrl+Down)");

        addBtn.Click += (_, _) => _canvas.AddLayer();
        folderBtn.Click += (_, _) => _canvas.AddGroupLayer();
        dupBtn.Click += (_, _) => _canvas.DuplicateLayer();
        _deleteLayerButton.Click += (_, _) => _canvas.DeleteLayer();
        _moveLayerUpButton.Click += (_, _) => _canvas.MoveActiveLayer(1);
        _moveLayerDownButton.Click += (_, _) => _canvas.MoveActiveLayer(-1);

        foreach (var btn in new Button[] { addBtn, folderBtn, dupBtn, _deleteLayerButton, _moveLayerUpButton, _moveLayerDownButton })
            btn.Margin = new Thickness(0, 0, 2, 2);

        var ctrlRow = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { addBtn, folderBtn, dupBtn, _deleteLayerButton, _moveLayerUpButton, _moveLayerDownButton }
        };

        _layerPanel = new StackPanel { Spacing = 2 };

        return new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(8, 4, 8, 8),
            Children =
            {
                _layerNameBox,
                blendOpRow,
                ctrlRow,
                _layerPanel
            }
        };
    }

    private void ApplyLayerName()
    {
        var name = _layerNameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            _canvas.SetActiveLayerName(name);
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
        _canvas.LayersChanged += (_, _) => { BuildLayerList(); UpdateStatus(); };
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

    // ── Color picker ──────────────────────────────────────────────────────────
    private void OnPickerHsvChanged(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SetColor(color, syncPicker: false, switchToBrush: true);
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
            SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)), switchToBrush: true);
    }

    // ── Color application ─────────────────────────────────────────────────────
    private void SetColor(Color color, bool syncPicker = true, bool switchToBrush = false)
    {
        _colorWell.Background = new SolidColorBrush(color);
        _canvas.SetPaintColor(color);
        if (switchToBrush) SetTool("brush");
        if (syncPicker) SyncPickerFromColor(color);
    }

    private void CycleColor()
    {
        _swatchIndex = (_swatchIndex + 1) % _swatches.Length;
        SetColor(_swatches[_swatchIndex], switchToBrush: true);
    }

    // ── Swatch panel ──────────────────────────────────────────────────────────
    private const int SwatchColumns = 10;
    private const int SwatchSize = 20;

    private void BuildSwatches()
    {
        _swatchPanel.Children.Clear();
        for (var i = 0; i < _swatches.Length; i++)
        {
            var idx = i;
            var color = _swatches[i];
            var btn = new Button
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(0, 0, 2, 2),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.Parse("#2a2d35")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(0)
            };
            ToolTip.SetTip(btn, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            btn.Click += (_, _) => { _swatchIndex = idx; SetColor(color, switchToBrush: true); };
            _swatchPanel.Children.Add(btn);
        }
    }

    // ── Brush library ─────────────────────────────────────────────────────────
    private void BuildBrushCategories()
    {
        _brushCategoryPanel.Children.Clear();
        foreach (var cat in _brushPaletteConfig.Categories)
        {
            var selected = cat == _selectedBrushCategory;
            var btn = new Button
            {
                Content = cat,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(8, 4),
                Height = 26,
                Background = new SolidColorBrush(selected ? Color.Parse(AccentSoft) : Color.Parse(Bg2)),
                Foreground = new SolidColorBrush(selected ? Color.Parse(TextPrimary) : Color.Parse(TextSecondary)),
                BorderBrush = new SolidColorBrush(selected ? Color.Parse(Accent) : Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                FontSize = 10,
                Tag = cat
            };
            btn.Click += (_, _) =>
            {
                _selectedBrushCategory = cat;
                BuildBrushCategories();
                BuildPresets();
            };
            EnableCategoryDrop(btn, cat);
            _brushCategoryPanel.Children.Add(btn);
        }

        // "New category" button / drop zone
        var newCatBtn = new Button
        {
            Content = "+",
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(8, 4),
            Height = 26,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            FontSize = 10
        };
        newCatBtn.Click += (_, _) =>
        {
            var name = PromptForNewCategory();
            if (!string.IsNullOrWhiteSpace(name) && !_brushPaletteConfig.Categories.Contains(name))
            {
                _brushPaletteConfig.Categories.Add(name);
                _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
                _selectedBrushCategory = name;
                BuildBrushCategories();
                BuildPresets();
            }
        };
        EnableNewCategoryDrop(newCatBtn);
        _brushCategoryPanel.Children.Add(newCatBtn);
    }

    private void BuildPresets()
    {
        _presetPanel.Children.Clear();
        var assets = _selectedBrushCategory == null || _selectedBrushCategory == "Recent"
            ? _brushAssets
            : _brushAssets.Where(p => _brushPaletteConfig.BrushCategory.GetValueOrDefault(p.Id) == _selectedBrushCategory).ToArray();

        foreach (var asset in assets)
        {
            var preset = asset.Preset;
            var isActive = _activeBrushAsset?.Id == asset.Id;

            var nameText = new TextBlock
            {
                Text = preset.Name,
                Foreground = new SolidColorBrush(Color.Parse(isActive ? "#ffffff" : TextPrimary)),
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var kindTag = new TextBlock
            {
                Text = KindTag(preset.Kind),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse(isActive ? "#8aacee" : TextMuted)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // Stroke-weight preview: tapered bar mimicking the brush width
            var strokeH = Math.Clamp(preset.Size / 256.0 * 12.0 + 1.5, 1.5, 12.0);
            var strokeOp = preset.Kind == BrushKind.Airbrush ? 0.45 : 0.85;
            var strokeBar = new Border
            {
                Height = strokeH,
                CornerRadius = new CornerRadius(strokeH / 2),
                Background = new SolidColorBrush(Color.Parse(isActive ? "#7a9fe0" : "#3a4258")),
                Opacity = strokeOp,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,56,38") };
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(strokeBar, 1);
            Grid.SetColumn(kindTag, 2);
            grid.Children.Add(nameText);
            grid.Children.Add(strokeBar);
            grid.Children.Add(kindTag);

            var row = new Button
            {
                Height = 32,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(isActive ? AccentSoft : Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 0),
                Content = grid,
                Tag = asset
            };
            row.Click += (_, _) => ApplyBrushAsset(asset);
            EnablePresetDrag(row, asset);
            _presetPanel.Children.Add(row);
        }
    }

    // ── Drag-and-drop helpers ────────────────────────────────────────────────

    private static readonly DataFormat<string> BrushIdFormat = DataFormat.CreateInProcessFormat<string>("x-floss-brush");

    private static void EnablePresetDrag(Control source, BrushAsset asset)
    {
        DragDrop.SetAllowDrop(source, true);
        source.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
                e.Handled = true;
        });
        source.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
                e.DragEffects = DragDropEffects.Move;
        });

        source.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
            {
                var item = new DataTransferItem();
                item.Set(BrushIdFormat, asset.Id);
                var data = new DataTransfer();
                data.Add(item);
                _ = DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            }
        };
    }

    private void EnableCategoryDrop(Control target, string category)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(BrushIdFormat)) return;
            var brushId = e.DataTransfer.TryGetValue<string>(BrushIdFormat);
            if (string.IsNullOrWhiteSpace(brushId)) return;

            _brushPaletteConfig.BrushCategory[brushId] = category;
            _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
            BuildPresets();
            e.Handled = true;
        });
    }

    private void EnableNewCategoryDrop(Control target)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(BrushIdFormat)) return;
            var brushId = e.DataTransfer.TryGetValue<string>(BrushIdFormat);
            if (string.IsNullOrWhiteSpace(brushId)) return;

            var name = PromptForNewCategory();
            if (!string.IsNullOrWhiteSpace(name) && !_brushPaletteConfig.Categories.Contains(name))
            {
                _brushPaletteConfig.Categories.Add(name);
                _brushPaletteConfig.BrushCategory[brushId] = name;
                _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
                _selectedBrushCategory = name;
                BuildBrushCategories();
                BuildPresets();
            }
            e.Handled = true;
        });
    }

    private string? PromptForNewCategory()
    {
        var dialog = new Window
        {
            Title = "New Category",
            Width = 280,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        var tb = new TextBox { Margin = new Thickness(12), PlaceholderText = "Category name" };
        var ok = new Button { Content = "Create", Margin = new Thickness(12, 0, 12, 12) };
        var result = (string?)null;
        ok.Click += (_, _) => { result = tb.Text; dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = tb.Text; dialog.Close(); } };
        dialog.Content = new StackPanel { Children = { tb, ok } };
        dialog.ShowDialog(this);
        return result;
    }

    private static string KindTag(BrushKind kind) => kind switch
    {
        BrushKind.Ink => "Ink",
        BrushKind.Pencil => "Pencil",
        BrushKind.Marker => "Marker",
        BrushKind.Airbrush => "Air",
        BrushKind.Eraser => "Eraser",
        _ => ""
    };

    private void LoadBrushAssets()
    {
        _brushAssets = _brushLibrary.Load();
        _brushPaletteConfig.SyncWithAssets(_brushAssets);
        _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
        BuildPresets();
    }

    private void SelectInitialBrush()
    {
        var initial = _brushAssets.FirstOrDefault(b => b.Preset.Name == App.Config.LastBrushName)
            ?? _brushAssets.FirstOrDefault()
            ?? BrushAsset.FromPreset(BrushPreset.Defaults[0]);
        ApplyBrushAsset(initial);
    }

    private void ApplyBrushAsset(BrushAsset asset)
    {
        _activeBrushAsset = asset;
        ApplyPreset(asset.ToPreset(), syncSliders: true);
    }

    private void ApplyPreset(BrushPreset preset, bool syncSliders)
    {
        _activePreset = preset;
        _activeBrushLabel.Text = preset.Name;

        if (syncSliders)
        {
            _syncingBrushUi = true;
            _sizeSlider.Value = Math.Clamp(preset.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
            _opacitySlider.Value = Math.Clamp(preset.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
            _flowSlider.Value = Math.Clamp(preset.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
            _hardnessSlider.Value = Math.Clamp(preset.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
            _spacingSlider.Value = Math.Clamp(preset.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
            _smoothingSlider.Value = Math.Clamp(preset.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
            _grainSlider.Value = Math.Clamp(preset.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
            _syncingBrushUi = false;
        }

        var applied = preset with { Color = _canvas.PaintColor };
        _canvas.SetBrush(applied);
        _strokePreview.Brush = applied;
        SetTool(preset.Kind == BrushKind.Eraser ? "eraser" : "brush");
        BuildPresets();
        UpdateStatus();
        if (syncSliders)
            RefreshToolProperties();
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

    private void OpenBrushEditor()
    {
        if (_activePreset == null) return;
        if (_brushEditorWindow != null)
        {
            _brushEditorWindow.SyncFromPreset(_activePreset);
            _brushEditorWindow.Activate();
            return;
        }

        _brushEditorWindow = new BrushEditorWindow(_activePreset, preset =>
        {
            UpdateCurrentBrush(_ => preset);
        });
        _brushEditorWindow.Closed += (_, _) => _brushEditorWindow = null;
        _brushEditorWindow.Show(this);
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

    private async System.Threading.Tasks.Task ImportAbrAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import .abr brush pack",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Adobe Brush") { Patterns = ["*.abr"] }]
        });
        if (files.Count == 0) return;

        var imported    = 0;
        var lastDiag    = "";
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            List<Brushes.BrushAsset> brushes;
            try { brushes = await System.Threading.Tasks.Task.Run(() => Brushes.AbrImporter.Import(stream, out lastDiag)); }
            catch (Exception ex) { lastDiag = ex.Message; continue; }

            foreach (var asset in brushes)
            {
                _brushLibrary.Save(asset);
                imported++;
            }
        }

        if (imported > 0)
        {
            LoadBrushAssets();
            _footerStatusText.Text = $"Imported {imported} brush{(imported == 1 ? "" : "es")} from .abr [{lastDiag}]";
        }
        else
        {
            _footerStatusText.Text = $"No brushes imported — {lastDiag}";
        }
    }

    private BrushPreset CurrentBrushFromUi()
    {
        var source = _activePreset ?? BrushPreset.Defaults[0];
        return source with
        {
            Size = _sizeSlider.Value,
            Opacity = _opacitySlider.Value,
            Flow = _flowSlider.Value,
            Hardness = _hardnessSlider.Value,
            Spacing = _spacingSlider.Value,
            Smoothing = _smoothingSlider.Value,
            Grain = _grainSlider.Value,
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

    // ── Layer panel ───────────────────────────────────────────────────────────
    private void BuildLayerList()
    {
        _layerPanel.Children.Clear();
        _layerRows.Clear();
        var layers = _canvas.Layers;

        foreach (var i in VisibleLayerIndexes())
        {
            var layer = layers[i];
            var (row, refs) = BuildLayerRow(i, layer);
            _layerPanel.Children.Add(row);
            _layerRows[i] = refs;
        }

        // Paper row — always at the bottom, non-interactive
        _layerPanel.Children.Add(BuildPaperRow());

        if (layers.Count > 0)
        {
            var active = layers[_canvas.ActiveLayerIndex];
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = active.Opacity;
            _blendModeComboBox.SelectedItem = active.BlendMode;
            _layerNameBox.Text = active.Name;
            _syncingLayerUi = false;
        }
    }

    private IEnumerable<int> VisibleLayerIndexes()
    {
        var layers = _canvas.Layers;
        foreach (var root in layers.Where(l => l.Parent == null).Reverse())
        {
            foreach (var index in VisibleLayerIndexes(root))
                yield return index;
        }
    }

    private IEnumerable<int> VisibleLayerIndexes(DrawingLayer layer)
    {
        var index = LayerIndexOf(layer);
        if (index >= 0)
            yield return index;

        if (!layer.IsGroup || !layer.IsOpen) yield break;
        for (var i = layer.Children.Count - 1; i >= 0; i--)
        {
            foreach (var childIndex in VisibleLayerIndexes(layer.Children[i]))
                yield return childIndex;
        }
    }

    private int LayerIndexOf(DrawingLayer layer)
    {
        var layers = _canvas.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            if (ReferenceEquals(layers[i], layer))
                return i;
        }

        return -1;
    }

    private (Border Row, LayerRowRefs Refs) BuildLayerRow(int i, DrawingLayer layer)
    {
        var isActive = i == _canvas.ActiveLayerIndex;
        var dimColor = isActive ? Color.Parse("#5a80c8") : Color.Parse("#383d47");
        var fgColor = isActive ? Color.Parse("#d8e0f0") : Color.Parse("#7a8494");

        var row = new Border
        {
            Background = new SolidColorBrush(isActive ? Color.Parse("#1a2a50") : Color.Parse("#16181f")),
            BorderBrush = new SolidColorBrush(isActive ? Color.Parse("#2e5fb8") : Color.Parse("#1e2128")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3, 2),
            Margin = new Thickness(layer.IndentLevel * 12, 0, 0, 0),
            Tag = i,
            ContextMenu = BuildLayerContextMenu(i, layer)
        };

        DragDrop.SetAllowDrop(row, true);
        row.PointerPressed += LayerRowPointerPressed;
        row.Tapped += LayerRowTapped;
        row.DoubleTapped += LayerRowDoubleTapped;
        row.AddHandler(DragDrop.DragOverEvent, LayerRowDragOver);
        row.AddHandler(DragDrop.DropEvent, LayerRowDrop);

        // cols: disclosure | vis | thumb | lock | alphalock | clip | name | blend | opacity
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,16,30,16,16,16,*,26,28") };

        var disclosureBtn = LayerDisclosureBtn(layer, i);

        var visBtn = LayerIconBtn(
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            "Toggle visibility",
            layer.IsVisible ? "#6a9fd8" : "#404550", i);
        visBtn.Click += (_, _) => _canvas.ToggleLayerVisibility((int)visBtn.Tag!);

        var (preview, previewImage) = BuildLayerPreview(layer);
        preview.PointerPressed += (_, e) =>
        {
            if (layer.IsGroup && e.GetCurrentPoint(preview).Properties.IsLeftButtonPressed)
            {
                _canvas.ToggleLayerOpen(i);
                e.Handled = true;
            }
        };

        var lockBtn = LayerIconBtn(
            layer.IsLocked ? Icons.LockOutline : Icons.LockOpenOutline,
            "Toggle lock",
            layer.IsLocked ? "#c89050" : "#404550", i);
        lockBtn.Click += (_, _) => _canvas.ToggleLayerLock((int)lockBtn.Tag!);

        var alphaLockBtn = LayerIconBtn(
            Icons.AlphaLock,
            "Toggle alpha lock",
            layer.IsAlphaLocked ? "#6ab8c8" : "#404550", i);
        alphaLockBtn.Click += (_, _) => _canvas.ToggleLayerAlphaLock((int)alphaLockBtn.Tag!);

        var clipBtn = LayerIconBtn(
            Icons.ClipToBelow,
            "Toggle clipping mask",
            layer.IsClipping ? "#a87ad8" : "#404550", i);
        clipBtn.Click += (_, _) => _canvas.ToggleLayerClipping((int)clipBtn.Tag!);

        var nameText = new TextBlock
        {
            Text = layer.Name,
            Foreground = new SolidColorBrush(fgColor),
            Padding = new Thickness(2, 0),
            FontSize = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var nameHost = new ContentControl
        {
            Content = nameText,
            Tag = i,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ClipToBounds = true
        };

        var blendText = new TextBlock
        {
            Text = BlendAbbr(layer.BlendMode),
            Foreground = new SolidColorBrush(dimColor),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 2, 0)
        };

        var opacityText = new TextBlock
        {
            Text = $"{Math.Round(layer.Opacity * 100):0}%",
            Foreground = new SolidColorBrush(dimColor),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 2, 0)
        };

        Grid.SetColumn(disclosureBtn, 0);
        Grid.SetColumn(visBtn, 1);
        Grid.SetColumn(preview, 2);
        Grid.SetColumn(lockBtn, 3);
        Grid.SetColumn(alphaLockBtn, 4);
        Grid.SetColumn(clipBtn, 5);
        Grid.SetColumn(nameHost, 6);
        Grid.SetColumn(blendText, 7);
        Grid.SetColumn(opacityText, 8);
        grid.Children.Add(disclosureBtn);
        grid.Children.Add(visBtn);
        grid.Children.Add(preview);
        grid.Children.Add(lockBtn);
        grid.Children.Add(alphaLockBtn);
        grid.Children.Add(clipBtn);
        grid.Children.Add(nameHost);
        grid.Children.Add(blendText);
        grid.Children.Add(opacityText);
        row.Child = grid;
        return (row, new LayerRowRefs(row, disclosureBtn, visBtn, lockBtn, alphaLockBtn, clipBtn, nameHost, blendText, opacityText, previewImage));
    }

    private Button LayerDisclosureBtn(DrawingLayer layer, int index)
    {
        var btn = new Button
        {
            Content = layer.IsGroup ? (layer.IsOpen ? "▾" : "▸") : "",
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(layer.IsGroup ? "#6a7a96" : "#30343d")),
            Padding = new Thickness(0),
            FontSize = 10,
            Tag = index,
            IsHitTestVisible = layer.IsGroup
        };
        if (layer.IsGroup)
            btn.Click += (_, _) => _canvas.ToggleLayerOpen((int)btn.Tag!);
        return btn;
    }

    private ContextMenu BuildLayerContextMenu(int index, DrawingLayer layer)
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                if (index >= 0 && index < _canvas.Layers.Count)
                    _canvas.SelectLayer(index);
                action();
            };
            return item;
        }

        var items = new List<MenuItem>
        {
            Item("_New Layer Above", () => _canvas.AddLayer()),
            Item("New _Folder Above", () => _canvas.AddGroupLayer()),
            Item("_Duplicate", () => _canvas.DuplicateLayer()),
            Item("_Delete", () => _canvas.DeleteLayer()),
            new() { Header = "-" },
            Item(layer.IsVisible ? "Hide Layer" : "Show Layer", () => _canvas.ToggleLayerVisibility(index)),
            Item(layer.IsLocked ? "Unlock Layer" : "Lock Layer", () => _canvas.ToggleLayerLock(index)),
            Item(layer.IsAlphaLocked ? "Disable Alpha Lock" : "Enable Alpha Lock", () => _canvas.ToggleLayerAlphaLock(index)),
            Item(layer.IsClipping ? "Disable Clipping Mask" : "Enable Clipping Mask", () => _canvas.ToggleLayerClipping(index))
        };

        if (layer.IsGroup)
            items.Insert(4, Item(layer.IsOpen ? "Collapse Folder" : "Expand Folder", () => _canvas.ToggleLayerOpen(index)));

        return new ContextMenu { ItemsSource = items };
    }

    private async void LayerRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int index) return;
        var point = e.GetCurrentPoint(row);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (IsLayerRowInteractiveSource(e.Source)) return;
        _canvas.SelectLayer(index);
        if (e.ClickCount > 1) return;
        _layerDragSourceIndex = index;

        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(index.ToString(CultureInfo.InvariantCulture));
        data.Add(item);
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        _layerDragSourceIndex = -1;
    }

    private void WindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_activeLayerNameEdit == null) return;
        if (IsInsideElement(e.Source, _activeLayerNameEdit)) return;
        FinishActiveLayerRename(commit: true);
    }

    private void LayerRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int index) return;
        if (IsLayerRowInteractiveSource(e.Source)) return;
        _canvas.SelectLayer(index);
    }

    private void LayerRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int index) return;
        if (IsLayerRowInteractiveSource(e.Source)) return;
        _canvas.SelectLayer(index);
        BeginLayerRename(index);
        e.Handled = true;
    }

    private static bool IsLayerRowInteractiveSource(object? source)
    {
        for (var current = source as StyledElement; current != null; current = current.Parent)
        {
            if (current is Button or TextBox or ComboBox or Slider)
                return true;
        }

        return false;
    }

    private void BeginLayerRename(int index)
    {
        var layers = _canvas.Layers;
        if (index < 0 || index >= layers.Count || !_layerRows.TryGetValue(index, out var refs)) return;

        FinishActiveLayerRename(commit: true);
        _renamingLayerIndex = index;
        var edit = new TextBox
        {
            Text = layers[index].Name,
            FontSize = 11,
            Height = 24,
            Padding = new Thickness(4, 0),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Accent)),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var finished = false;
        void Finish(bool commit)
        {
            if (finished) return;
            finished = true;

            if (commit)
            {
                var name = edit.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _canvas.SelectLayer(index);
                    _canvas.SetActiveLayerName(name);
                }
            }

            _renamingLayerIndex = -1;
            _activeLayerNameEdit = null;
            _finishLayerRename = null;
            UpdateLayerRow(index);
        }

        _activeLayerNameEdit = edit;
        _finishLayerRename = Finish;
        edit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Finish(commit: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Finish(commit: false);
                e.Handled = true;
            }
        };
        edit.LostFocus += (_, _) => Finish(commit: true);

        refs.NameHost.Content = edit;
        edit.AttachedToVisualTree += (_, _) =>
        {
            edit.Focus();
            edit.SelectAll();
        };
    }

    private void FinishActiveLayerRename(bool commit)
    {
        var finish = _finishLayerRename;
        if (finish == null) return;
        finish(commit);
    }

    private static bool IsInsideElement(object? source, StyledElement target)
    {
        for (var current = source as StyledElement; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, target))
                return true;
        }

        return false;
    }

    private void LayerRowDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int targetIndex)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceIndex = GetDraggedLayerIndex(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));
        e.DragEffects = sourceIndex >= 0 && _canvas.CanMoveLayer(sourceIndex, targetIndex, placement)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void LayerRowDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int targetIndex) return;
        var sourceIndex = GetDraggedLayerIndex(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));
        if (sourceIndex >= 0)
            _canvas.MoveLayer(sourceIndex, targetIndex, placement);
        e.Handled = true;
    }

    private int GetDraggedLayerIndex(IDataTransfer data)
    {
        var raw = data.TryGetText();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1;
    }

    private LayerDropPlacement GetLayerDropPlacement(Border row, int targetIndex, Point position)
    {
        var height = Math.Max(1, row.Bounds.Height);
        var target = _canvas.Layers[targetIndex];
        if (target.IsGroup && position.Y > height * 0.25 && position.Y < height * 0.75)
            return LayerDropPlacement.Into;
        return position.Y < height * 0.5 ? LayerDropPlacement.Above : LayerDropPlacement.Below;
    }

    private Control BuildPaperRow()
    {
        var doc = _canvas.Document;
        var paperColor = doc.PaperColor;

        _paperSwatch = new Border
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(2, 0, 2, 0),
            Background = new SolidColorBrush(paperColor),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a4050")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        _paperSwatch.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_paperSwatch).Properties.IsLeftButtonPressed)
            {
                OpenPaperColorPicker();
                e.Handled = true;
            }
        };

        _paperVisBtn = new Button
        {
            Content = Icons.Make(doc.PaperVisible ? Icons.Eye : Icons.EyeOff, 10,
                new SolidColorBrush(Color.Parse(doc.PaperVisible ? "#6a9fd8" : "#404550"))),
            Width = 14,
            Height = 14,
            Padding = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _paperVisBtn.Click += (_, _) => _canvas.SetPaperVisible(!doc.PaperVisible);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,16,30,16,16,16,*,26,28") };
        var nameLabel = new TextBlock
        {
            Text = "Paper",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#4a5468")),
            Padding = new Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(_paperVisBtn, 1);
        Grid.SetColumn(_paperSwatch, 2);
        Grid.SetColumn(nameLabel, 6);
        grid.Children.Add(_paperVisBtn);
        grid.Children.Add(_paperSwatch);
        grid.Children.Add(nameLabel);

        var row = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0f1016")),
            BorderBrush = new SolidColorBrush(Color.Parse("#1a1c22")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3, 2),
            Child = grid
        };
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                OpenPaperColorPicker();
                e.Handled = true;
            }
        };
        return row;
    }

    private void RefreshPaperRow()
    {
        if (_paperSwatch == null || _paperVisBtn == null) return;
        var doc = _canvas.Document;
        _paperSwatch.Background = new SolidColorBrush(doc.PaperColor);
        _paperVisBtn.Content = Icons.Make(doc.PaperVisible ? Icons.Eye : Icons.EyeOff, 10,
            new SolidColorBrush(Color.Parse(doc.PaperVisible ? "#6a9fd8" : "#404550")));
    }

    private void OpenPaperColorPicker()
    {
        var doc = _canvas.Document;
        var dialog = new Window
        {
            Title = "Paper Color",
            Width = 280,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };

        var picker = new HsvColorPicker
        {
            Height = 160,
            Margin = new Thickness(12, 8, 12, 8)
        };
        var (ph, ps, pv) = RgbToHsv(doc.PaperColor.R / 255.0, doc.PaperColor.G / 255.0, doc.PaperColor.B / 255.0);
        picker.SetHsv(ph, ps, pv);

        var hexBox = new TextBox
        {
            Text = $"#{doc.PaperColor.R:X2}{doc.PaperColor.G:X2}{doc.PaperColor.B:X2}",
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 12,
            Height = 26,
            Padding = new Thickness(6, 0),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a4250")),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Width = 100
        };

        Color? result = null;
        picker.HsvChanged += (h, s, v) =>
        {
            var (r, g, b) = HsvToRgb(h, s, v);
            hexBox.Text = $"#{(byte)(r * 255):X2}{(byte)(g * 255):X2}{(byte)(b * 255):X2}";
        };
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return && TryParseHexColor(hexBox.Text ?? "", out var c))
            {
                result = c;
                dialog.Close();
            }
        };
        hexBox.LostFocus += (_, _) =>
        {
            if (TryParseHexColor(hexBox.Text ?? "", out var c))
            {
                var (h, s, v) = RgbToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0);
                picker.SetHsv(h, s, v);
            }
        };

        var okBtn = new Button
        {
            Content = "OK",
            Margin = new Thickness(12, 0, 12, 12),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        okBtn.Click += (_, _) =>
        {
            if (TryParseHexColor(hexBox.Text ?? "", out var c))
                result = c;
            else
            {
                var (h, s, v) = picker.Hsv;
                var (r, g, b) = HsvToRgb(h, s, v);
                result = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
            }
            dialog.Close();
        };

        dialog.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                picker,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(12, 0, 12, 0),
                    Children = { new TextBlock { Text = "Hex", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)) }, hexBox }
                },
                okBtn
            }
        };
        dialog.ShowDialog(this);
        if (result.HasValue)
            _canvas.SetPaperColor(result.Value);
    }

    private static bool TryParseHexColor(string input, out Color color)
    {
        color = default;
        var s = input.Trim().TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var val))
        {
            color = Color.FromRgb((byte)((val >> 16) & 0xFF), (byte)((val >> 8) & 0xFF), (byte)(val & 0xFF));
            return true;
        }
        return false;
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
        var dimColor = isActive ? Color.Parse("#5a80c8") : Color.Parse("#383d47");
        var fgColor = isActive ? Color.Parse("#d8e0f0") : Color.Parse("#7a8494");

        refs.Row.Background = new SolidColorBrush(isActive ? Color.Parse("#1a2a50") : Color.Parse("#16181f"));
        refs.Row.BorderBrush = new SolidColorBrush(isActive ? Color.Parse("#2e5fb8") : Color.Parse("#1e2128"));

        SetLayerIconBtnIcon(refs.VisibilityButton,
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            layer.IsVisible ? "#6a9fd8" : "#404550");
        SetLayerIconBtnIcon(refs.LockButton,
            layer.IsLocked ? Icons.LockOutline : Icons.LockOpenOutline,
            layer.IsLocked ? "#c89050" : "#404550");
        SetLayerIconBtnIcon(refs.AlphaLockButton,
            Icons.AlphaLock,
            layer.IsAlphaLocked ? "#6ab8c8" : "#404550");
        SetLayerIconBtnIcon(refs.ClipButton,
            Icons.ClipToBelow,
            layer.IsClipping ? "#a87ad8" : "#404550");
        refs.DisclosureButton.Content = layer.IsGroup ? (layer.IsOpen ? "▾" : "▸") : "";
        refs.DisclosureButton.IsHitTestVisible = layer.IsGroup;
        if (_renamingLayerIndex != index)
        {
            refs.NameHost.Content = new TextBlock
            {
                Text = layer.Name,
                Foreground = new SolidColorBrush(fgColor),
                Padding = new Thickness(4, 1),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        }
        refs.BlendText.Text = BlendAbbr(layer.BlendMode);
        refs.BlendText.Foreground = new SolidColorBrush(dimColor);
        refs.OpacityText.Text = $"{Math.Round(layer.Opacity * 100):0}%";
        refs.OpacityText.Foreground = new SolidColorBrush(dimColor);

        if (isActive && !_syncingLayerUi)
        {
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = layer.Opacity;
            _blendModeComboBox.SelectedItem = layer.BlendMode;
            _layerNameBox.Text = layer.Name;
            _syncingLayerUi = false;
        }
    }

    private static (Control Frame, Image? PreviewImage) BuildLayerPreview(DrawingLayer layer)
    {
        var frame = new Border
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(2, 0, 2, 0),
            Background = new SolidColorBrush(Color.Parse("#1e2028")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2e3340")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true
        };

        if (layer.IsGroup)
        {
            frame.Child = new TextBlock
            {
                Text = layer.IsOpen ? "▾" : "▸",
                Foreground = new SolidColorBrush(Color.Parse("#6a7a96")),
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            return (frame, null);
        }

        var image = new Image
        {
            Source = layer.GetThumbnail(26),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        frame.Child = image;
        return (frame, image);
    }

    private sealed record LayerRowRefs(
        Border Row,
        Button DisclosureButton,
        Button VisibilityButton,
        Button LockButton,
        Button AlphaLockButton,
        Button ClipButton,
        ContentControl NameHost,
        TextBlock BlendText,
        TextBlock OpacityText,
        Image? PreviewImage);

    // ── Viewport ──────────────────────────────────────────────────────────────
    private void SyncCanvasFrameToDocument(bool fitToViewport)
    {
        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        _canvasFrame.Width = w;
        _canvasFrame.Height = h;

        if (fitToViewport)
            ResetView();
    }

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var f = App.Shortcuts.ZoomScrollFactor;
        var factor = e.Delta.Y > 0 ? f : 1.0 / f;
        SetZoom(_zoom * factor, e.GetPosition(_workspaceViewport));
        e.Handled = true;
    }

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_workspaceViewport);
        var middle = pt.Properties.IsMiddleButtonPressed;
        if (_activeGesture == GestureMode.None)
        {
            var (gesture, gestureBinding) = DetectGesture(Key.None, e.KeyModifiers, App.Shortcuts);
            if (gesture != GestureMode.None && gestureBinding?.IsModifierOnly == true)
            {
                BeginGesture(gesture, Key.None, gestureBinding);
            }
        }
        if (!middle && _activeGesture == GestureMode.None) return;
        _isPanning = true;
        _lastPanPoint = e.GetPosition(_workspaceViewport);
        _gestureStartPoint = _lastPanPoint;
        if (_activeGesture == GestureMode.BrushSize)
        {
            _canvas.LockCursorPreview(e.GetPosition(_canvas), forceBrushOutline: true);
        }
        e.Pointer.Capture(_workspaceViewport);
        e.Handled = true;
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var pt = e.GetPosition(_workspaceViewport);
        var d = pt - _lastPanPoint;
        _lastPanPoint = pt;
        var sc = App.Shortcuts;

        switch (_activeGesture)
        {
            case GestureMode.Pan:
            case GestureMode.None: // middle-mouse pan
                _canvasPan.X += d.X;
                _canvasPan.Y += d.Y;
                ClampCanvasPan();
                break;
            case GestureMode.Zoom:
                var axisDelta = sc.GestureZoomAxis == GestureAxis.Horizontal ? d.X : -d.Y;
                SetZoom(_zoom * Math.Pow(sc.GestureZoomSensitivity, axisDelta), _gestureStartPoint);
                break;
            case GestureMode.Rotate:
                SetRotation(_rotation + d.X * sc.GestureRotateSensitivity);
                break;
            case GestureMode.BrushSize:
                _sizeSlider.Value = Math.Clamp(
                    _sizeSlider.Value + d.X * sc.GestureSizeSensitivity,
                    _sizeSlider.Minimum, _sizeSlider.Maximum);
                break;
        }
        e.Handled = true;
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        _canvas.UnlockCursorPreview();
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
        _canvas.CanvasZoom = _zoom;

        if (cursor.HasValue && oldZoom > 0)
        {
            var ratio = _zoom / oldZoom;
            var vpW = _workspaceViewport.Bounds.Width;
            var vpH = _workspaceViewport.Bounds.Height;
            var c = cursor.Value;
            _canvasPan.X = (c.X - vpW * 0.5) * (1 - ratio) + _canvasPan.X * ratio;
            _canvasPan.Y = (c.Y - vpH * 0.5) * (1 - ratio) + _canvasPan.Y * ratio;
        }

        ClampCanvasPan();
        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        UpdateStatus();
    }

    private void SetRotation(double degrees)
    {
        _rotation = degrees % 360;
        _canvasRotate.Angle = _rotation;
        ClampCanvasPan();
        _rotDisplay.Text = $"{Math.Round(_rotation)}°";
        UpdateStatus();
    }

    private void ResetView()
    {
        _rotation = 0;
        _canvasRotate.Angle = 0;

        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        var vpW = Math.Max(1, _workspaceViewport.Bounds.Width);
        var vpH = Math.Max(1, _workspaceViewport.Bounds.Height);
        var outset = Math.Min(ResetViewOutset, Math.Min(vpW, vpH) * 0.2);
        var availableW = Math.Max(1, vpW - outset * 2);
        var availableH = Math.Max(1, vpH - outset * 2);

        _zoom = Math.Clamp(Math.Min(availableW / w, availableH / h), 0.05, 16.0);
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;
        _canvas.CanvasZoom = _zoom;
        _canvasPan.X = 0;
        _canvasPan.Y = 0;
        ClampCanvasPan();

        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        _rotDisplay.Text = "0°";
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
        var key = e.Key;
        var mods = Floss.App.Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);
        var sc = App.Shortcuts;

        // Pen gestures take priority — they suspend drawing and activate a drag mode
        var (gesture, gestureBinding) = DetectGesture(key, mods, sc);
        if (gesture != GestureMode.None)
        {
            BeginGesture(gesture, key, gestureBinding);
            e.Handled = true;
            return;
        }

        if (sc.Undo.Matches(key, mods)) { _canvas.Undo(); e.Handled = true; }
        else if (sc.Redo.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.RedoAlt.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.FileSave.Matches(key, mods)) { _ = SaveFlossAsync(); e.Handled = true; }
        else if (sc.FileOpen.Matches(key, mods)) { _ = OpenDocumentAsync(); e.Handled = true; }
        else if (sc.LayerNew.Matches(key, mods)) { _canvas.AddLayer(); e.Handled = true; }
        else if (sc.LayerDuplicate.Matches(key, mods)) { _canvas.DuplicateLayer(); e.Handled = true; }
        else if (sc.LayerDelete.Matches(key, mods)) { _canvas.DeleteLayer(); e.Handled = true; }
        else if (sc.LayerMoveUp.Matches(key, mods)) { _canvas.MoveActiveLayer(1); e.Handled = true; }
        else if (sc.LayerMoveDown.Matches(key, mods)) { _canvas.MoveActiveLayer(-1); e.Handled = true; }
        else if (sc.ZoomReset.Matches(key, mods)) { ResetView(); e.Handled = true; }
        else if (sc.ZoomFit.Matches(key, mods)) { SyncCanvasFrameToDocument(fitToViewport: true); e.Handled = true; }
        else if (sc.ZoomIn.Matches(key, mods) || sc.ZoomInAlt.Matches(key, mods))
        { SetZoom(_zoom * sc.ZoomKeyFactor, null); e.Handled = true; }
        else if (sc.ZoomOut.Matches(key, mods))
        { SetZoom(_zoom / sc.ZoomKeyFactor, null); e.Handled = true; }
        else if (sc.RotateReset.Matches(key, mods)) { SetRotation(0); e.Handled = true; }
        else if (sc.RotateLeft.Matches(key, mods)) { SetRotation(_rotation - sc.RotateKeyStep); e.Handled = true; }
        else if (sc.RotateRight.Matches(key, mods)) { SetRotation(_rotation + sc.RotateKeyStep); e.Handled = true; }
        else if (sc.ToolBrush.Matches(key, mods)) { SetTool("brush"); e.Handled = true; }
        else if (sc.ToolEraser.Matches(key, mods)) { SetTool("eraser"); e.Handled = true; }
        else if (sc.ToolTransform.Matches(key, mods)) { _canvas.BeginSelectionTransform(); UpdateStatus(); e.Handled = true; }
        else if (key == Key.V && mods == KeyModifiers.None) { ActivateTool(_moveTool, _moveToolButton); e.Handled = true; }
        else if (key == Key.S && mods == KeyModifiers.None) { ActivateTool(_selectTool, _selectToolButton); e.Handled = true; }
        else if (key == Key.W && mods == KeyModifiers.None) { ActivateTool(_magicWandTool, _wandToolButton); e.Handled = true; }
        else if (key == Key.G && mods == KeyModifiers.None) { ActivateTool(_fillTool, _fillToolButton); e.Handled = true; }
        else if (key == Key.L && mods == KeyModifiers.None) { ActivateTool(_lassoFillTool, _lassoFillToolButton); e.Handled = true; }
        else if (key == Key.I && mods == KeyModifiers.None) { ActivateTool(_eyedropperTool, _eyedropToolButton); e.Handled = true; }
        else if (key == Key.LeftAlt || key == Key.RightAlt)
        {
            if (_preAltTool == null && _canvas.ActiveTool != _eyedropperTool)
            {
                _preAltTool = _canvas.ActiveTool;
                _preAltToolButton = _activeToolButton ?? _brushToolButton;
                ActivateTool(_eyedropperTool, _eyedropToolButton);
            }
            e.Handled = true;
        }
        else if (key == Key.Escape)
        { _canvas.CancelActiveTool(); e.Handled = true; }
        else if ((key == Key.Return || key == Key.Enter) && _canvas.ActiveTool is SelectTool or PolylineTool or TransformTool)
        { _canvas.CommitActiveTool(); e.Handled = true; }
        else if (sc.ColorCycle.Matches(key, mods)) { CycleColor(); e.Handled = true; }
        else if (sc.ColorDefault.Matches(key, mods)) { SetColor(Color.Parse("#111111")); e.Handled = true; }
        else if (sc.OpenSettings.Matches(key, mods)) { OpenSettings(); e.Handled = true; }
        else if (sc.OpenBrushEditor.Matches(key, mods)) { OpenBrushEditor(); e.Handled = true; }
        else if (sc.BrushSizeDecrease.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Max(_sizeSlider.Minimum, _sizeSlider.Value - sc.BrushSizeStep);
            e.Handled = true;
        }
        else if (sc.BrushSizeIncrease.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Min(_sizeSlider.Maximum, _sizeSlider.Value + sc.BrushSizeStep);
            e.Handled = true;
        }
        else if (sc.BrushSizeDecreaseLarge.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Max(_sizeSlider.Minimum, _sizeSlider.Value - sc.BrushSizeStepLarge);
            e.Handled = true;
        }
        else if (sc.BrushSizeIncreaseLarge.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Min(_sizeSlider.Maximum, _sizeSlider.Value + sc.BrushSizeStepLarge);
            e.Handled = true;
        }
        else if (sc.BrushOpacityDecrease.Matches(key, mods))
        {
            _opacitySlider.Value = Math.Max(_opacitySlider.Minimum, _opacitySlider.Value - sc.BrushOpacityStep);
            e.Handled = true;
        }
        else if (sc.BrushOpacityIncrease.Matches(key, mods))
        {
            _opacitySlider.Value = Math.Min(_opacitySlider.Maximum, _opacitySlider.Value + sc.BrushOpacityStep);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var mods = Floss.App.Input.KeyBinding.ModifiersAfterKeyUp(e.Key, e.KeyModifiers);
        if (_activeGesture != GestureMode.None &&
            (_gestureKey == Key.None
                ? (mods & _gestureModifiers) != _gestureModifiers
                : e.Key == _gestureKey))
        {
            _activeGesture = GestureMode.None;
            _gestureKey = Key.None;
            _gestureModifiers = KeyModifiers.None;
            _isPanning = false;
            _canvas.UnlockCursorPreview();
            _canvas.PaintInputSuspended = false;
            Cursor = Cursor.Default;
            e.Handled = true;
        }

        if ((e.Key == Key.LeftAlt || e.Key == Key.RightAlt) && _preAltTool != null)
        {
            ActivateTool(_preAltTool, _preAltToolButton ?? _brushToolButton);
            _preAltTool = null;
            _preAltToolButton = null;
            e.Handled = true;
        }
    }

    private static (GestureMode Mode, Floss.App.Input.KeyBinding? Binding) DetectGesture(Key key, KeyModifiers mods, ShortcutsConfig sc)
    {
        if (sc.GesturePan.Matches(key, mods)) return (GestureMode.Pan, sc.GesturePan);
        if (sc.GestureZoom.Matches(key, mods)) return (GestureMode.Zoom, sc.GestureZoom);
        if (sc.GestureRotate.Matches(key, mods)) return (GestureMode.Rotate, sc.GestureRotate);
        if (sc.GestureBrushSize.Matches(key, mods)) return (GestureMode.BrushSize, sc.GestureBrushSize);
        return (GestureMode.None, null);
    }

    private void BeginGesture(GestureMode gesture, Key key, Floss.App.Input.KeyBinding? binding)
    {
        _activeGesture = gesture;
        _gestureKey = binding?.IsModifierOnly == true ? Key.None : key;
        _gestureModifiers = binding?.Modifiers ?? KeyModifiers.None;
        _canvas.PaintInputSuspended = true;
        Cursor = gesture switch
        {
            GestureMode.Pan => new Cursor(StandardCursorType.SizeAll),
            GestureMode.BrushSize => new Cursor(StandardCursorType.None),
            _ => new Cursor(StandardCursorType.Arrow)
        };
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
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

    // ── File I/O ──────────────────────────────────────────────────────────────
    private static readonly FilePickerFileType DocumentFileType = new("Supported Documents")
    {
        Patterns = ["*.floss", "*.psd", "*.kra", "*.png", "*.jpg", "*.jpeg", "*.jpe", "*.webp", "*.bmp", "*.dib", "*.gif", "*.tif", "*.tiff", "*.ico", "*.wbmp"]
    };

    private static readonly FilePickerFileType KraFileType = new("Krita Document")
    {
        Patterns = ["*.kra"]
    };

    private static readonly FilePickerFileType FlossFileType = new("Floss Document")
    {
        Patterns = ["*.floss"]
    };

    private static readonly FilePickerFileType PsdFileType = new("Photoshop Document")
    {
        Patterns = ["*.psd"]
    };

    private static readonly FilePickerFileType RasterImageFileType = new("Image Files")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.jpe", "*.webp", "*.bmp", "*.dib", "*.gif", "*.tif", "*.tiff", "*.ico", "*.wbmp"]
    };

    private async System.Threading.Tasks.Task OpenDocumentAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter = [DocumentFileType, FlossFileType, PsdFileType, KraFileType, RasterImageFileType]
        });
        if (files.Count == 0) return;

        try
        {
            var path = files[0].Path.LocalPath;
            await using var stream = await files[0].OpenReadAsync();
            var imported = await System.Threading.Tasks.Task.Run(() => LoadDocumentFromStream(stream, path));
            ApplyOpenedDocument(imported, path);
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task OpenPsdAsync() => await OpenDocumentAsync();

    public async System.Threading.Tasks.Task OpenDocumentFromPathAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _footerStatusText.Text = $"Open error: file not found {path}";
                return;
            }

            await using var stream = File.OpenRead(path);
            var imported = await System.Threading.Tasks.Task.Run(() => LoadDocumentFromStream(stream, path));
            ApplyOpenedDocument(imported, path);
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Open error: {ex.Message}";
        }
    }

    private static DrawingDocument LoadDocumentFromStream(Stream stream, string path)
        => IsFlossPath(path) ? FlossFileFormat.Load(stream)
            : IsPsdPath(path) ? PsdImporter.Load(stream)
            : IsKraPath(path) ? KraImporter.Load(stream)
            : ImageFileImporter.Load(stream, path);

    private void ApplyOpenedDocument(DrawingDocument imported, string path)
    {
        _canvas.Document.ReplaceWith(imported);
        App.Config.AddRecentFile(path);
        if (IsFlossPath(path)) _currentFlossPath = path;
        SyncCanvasFrameToDocument(fitToViewport: true);
        BuildLayerList();
        UpdateStatus();
        _footerStatusText.Text =
            $"Opened {_canvas.Document.Width}x{_canvas.Document.Height}  {Path.GetFileName(path)}";
    }

    private async System.Threading.Tasks.Task SaveFlossAsync()
    {
        if (_currentFlossPath != null)
        {
            try
            {
                await using var stream = File.Open(_currentFlossPath, FileMode.Create, FileAccess.Write);
                FlossFileFormat.Save(stream, _canvas.Document);
                _footerStatusText.Text = $"Saved {Path.GetFileName(_currentFlossPath)}";
            }
            catch (Exception ex)
            {
                _footerStatusText.Text = $"Save error: {ex.Message}";
            }
            return;
        }

        await SaveFlossAsAsync();
    }

    private async System.Threading.Tasks.Task SaveFlossAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Floss Document",
            FileTypeChoices = [FlossFileType],
            SuggestedFileName = "untitled.floss"
        });
        if (file == null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            FlossFileFormat.Save(stream, _canvas.Document);
            _currentFlossPath = file.Path.LocalPath;
            App.Config.AddRecentFile(file.Path.LocalPath);
            _footerStatusText.Text = $"Saved {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Save error: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task SavePsdAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PSD",
            FileTypeChoices = [PsdFileType]
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

    private async System.Threading.Tasks.Task ExportImageAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Image",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG Image") { Patterns = ["*.png"] },
                new FilePickerFileType("JPEG Image") { Patterns = ["*.jpg", "*.jpeg"] },
                new FilePickerFileType("WebP Image") { Patterns = ["*.webp"] },
                new FilePickerFileType("Bitmap Image") { Patterns = ["*.bmp"] },
                new FilePickerFileType("GIF Image") { Patterns = ["*.gif"] },
                new FilePickerFileType("Icon") { Patterns = ["*.ico"] },
                new FilePickerFileType("Wireless Bitmap") { Patterns = ["*.wbmp"] }
            ],
            SuggestedFileName = "floss-export.png"
        });
        if (file == null) return;

        try
        {
            var path = file.Path.LocalPath;
            await using var stream = await file.OpenWriteAsync();
            ImageFileExporter.Export(stream, _canvas.Document, path);
            _footerStatusText.Text = $"Exported {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _footerStatusText.Text = $"Export error: {ex.Message}";
        }
    }

    private static bool IsPsdPath(string path)
        => string.Equals(Path.GetExtension(path), ".psd", StringComparison.OrdinalIgnoreCase);

    private static bool IsKraPath(string path)
        => string.Equals(Path.GetExtension(path), ".kra", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlossPath(string path)
        => string.Equals(Path.GetExtension(path), FlossFileFormat.Extension, StringComparison.OrdinalIgnoreCase);

    // ── HSV ↔ RGB helpers ─────────────────────────────────────────────────────
    private static (double h, double s, double v) RgbToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double r, double g, double b) HsvToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        var hi = (int)(h / 60) % 6;
        var f = h / 60 - Math.Floor(h / 60);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
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
