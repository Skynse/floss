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
using Floss.App.Processes;
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

    private string? _selectedCategory;

    // ── Blend modes ───────────────────────────────────────────────────────────
    private static readonly string[] BlendModes =
    [
        "Normal", "PassThrough", "Dissolve",
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
    private WrapPanel _brushCategoryPanel = null!;
    private StackPanel _presetPanel = null!;
    private StackPanel _toolPropertyPanel = null!;
    private TextBlock _toolPropertyTitle = null!;
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
    private Button _deleteLayerButton = null!;
    private Button _moveLayerUpButton = null!;
    private Button _moveLayerDownButton = null!;
    private Button _lockLayerBtn = null!;
    private Button _alphaLockLayerBtn = null!;
    private Button _clipLayerBtn = null!;
    private Button _refLayerBtn = null!;
    private TextBlock _toolStatusText = null!;
    private TextBlock _footerStatusText = null!;
    private TextBlock _canvasStatusText = null!;
    private TextBlock _zoomDisplay = null!;
    private TextBlock _rotDisplay = null!;
    private Button _saveBrushButton = null!;
    private BrushStrokePreview _strokePreview = null!;

    // ── State ─────────────────────────────────────────────────────────────────
    private ToolPropertiesWindow? _toolPropsWindow;
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
    private string? _currentFilePath; // Replaces _currentFlossPath
    private int _layerDragSourceIndex = -1;
    private int _renamingLayerIndex = -1;
    private TextBox? _activeLayerNameEdit;
    private Action<bool>? _finishLayerRename;

    // ── Tool factory (new process-based architecture) ─────────────────────────
    private ToolFactory? _toolFactory;
    private readonly List<Button> _toolButtons = [];
    private readonly List<(ToolGroup Group, Button Button)> _toolGroupButtons = [];
    private ToolGroup? _activeToolGroup;
    private ToolGroup? _recordingToolGroup;
    private Button? _recordingToolGroupButton;
    private ToolPreset? _recordingPresetAltInvocation;
    private KeyModifiers _recordingPresetPendingMods;
    private StackPanel _toolRailStack = null!;

    private enum GestureMode { None, Pan, Zoom, Rotate, BrushSize }
    private GestureMode _activeGesture;
    private Key _gestureKey = Key.None;
    private KeyModifiers _gestureModifiers;
    private bool _isPanning;
    private Point _lastPanPoint;
    private Point _gestureStartPoint;
    private SettingsWindow? _settingsWindow;

    private bool _suppressBrushSettingsRestored;
    private static readonly Cursor CursorPan = new(StandardCursorType.SizeAll);
    private static readonly Cursor CursorArrow = new(StandardCursorType.Arrow);
    private static readonly Cursor CursorNone = new(StandardCursorType.None);
    private ITool? _altToolBeforeGesture;

    private ScaleTransform _canvasFlip = null!;
    private ScaleTransform _canvasScale = null!;
    private RotateTransform _canvasRotate = null!;
    private TranslateTransform _canvasPan = null!;

    private bool _syncingLayerUi;
    private bool _syncingBrushUi;
    private Control? _leftRail;
    private Control? _rightPanel;
    private Control? _splitterControl;
    private Control? _shellMenu;
    private Control? _shellToolbar;
    private Control? _statusBar;
    private Control? _footer;
    private Control? _rulerOverlay;
    private CheckerboardOverlay? _checkerboardOverlay;
    private bool _canvasOnly;
    private bool _showRulers;

    // Layout grids for canvas-only mode collapse
    private Grid? _rootGrid;
    private GridLength[]? _rootColumnWidths;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        _brushLibrary = new BrushLibrary(AppPaths.BrushesDirectory);
        _showRulers = App.Config.ShowRulers;
        BuildUi();
        WireControls();
        RestoreFromConfig();
        BuildSwatches();
        LoadBrushAssets();
        SelectInitialBrush();
        SetColor(Color.Parse(App.Config.LastColor));
        // Hide canvas frame until user creates a document via the startup dialog
        _canvasFrame.IsVisible = false;
        if (_canvas.Layers.Count > 0) _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);
        BuildLayerList();
        UpdateStatus();
        Closing += (_, _) => SaveToConfig();
        Deactivated += (_, _) => ResetTransientInputState();
        LostFocus += (_, _) => ResetTransientInputState();
        Loaded += async (_, _) => await NewDocumentAsync();
        _canvas.DirtyStateChanged += (_, _) =>
        {
            string fileName = _currentFilePath == null ? "Untitled" : Path.GetFileName(_currentFilePath);
            string dirtyStar = _canvas.IsDirty ? "*" : "";
            Title = $"Floss Studio - {fileName}{dirtyStar}";
        };
    }

    // ── Root layout ───────────────────────────────────────────────────────────
    private void BuildUi()
    {
        _canvas = new DrawingCanvas();
        _toolFactory = new ToolFactory(_canvas.Document, _canvas.BrushEngine);

        var tg = new TransformGroup();
        _canvasFlip = new ScaleTransform(1, 1);
        _canvasScale = new ScaleTransform(1, 1);
        _canvasRotate = new RotateTransform(0);
        _canvasPan = new TranslateTransform(0, 0);
        tg.Children.Add(_canvasFlip);
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
            Background = CheckerboardOverlay.BackgroundBrush,
            Focusable = true
        };
        _checkerboardOverlay = new CheckerboardOverlay(_canvas);
        _workspaceViewport.Children.Add(_checkerboardOverlay);
        _workspaceViewport.Children.Add(_canvasFrame);

        _rulerOverlay = new RulerOverlay(_canvas);
        _rulerOverlay.IsVisible = _showRulers;
        _workspaceViewport.Children.Add(_rulerOverlay);

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
        root.ColumnDefinitions.Add(new ColumnDefinition(520, GridUnitType.Pixel) { MinWidth = 500, MaxWidth = 900 });
        _rootGrid = root;
        _rootColumnWidths = [.. root.ColumnDefinitions.Select(c => c.Width)];

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

        _shellMenu = menu;
        _shellToolbar = toolbar;
        _leftRail = leftRail;
        _rightPanel = rightPanel;
        _splitterControl = splitter;
        _statusBar = statusBar;
        _footer = footer;

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
                MenuAction("_New...", async () => await NewDocumentAsync()),
                MenuAction("_Open...", async () => await OpenDocumentAsync()),
                MenuAction("_Save Floss", async () => await SaveDocumentAsync()),
                MenuAction("_Save Floss As...", async () => await SaveDocumentAsAsync()),
                MenuAction("_Export Image...", async () => await ExportImageAsync()),
                new Separator(),
                MenuAction("_Reset View", ResetView),
                new Separator(),
                MenuAction("_Settings...", OpenSettings)
            }
        };

        var editMenu = new MenuItem
        {
            Header = "_Edit",
            ItemsSource = new object[]
            {
                MenuAction("_Undo", () => _canvas.Undo()),
                MenuAction("_Redo", () => _canvas.Redo()),
                new Separator(),
                MenuAction("_Copy", () => _canvas.CopyToClipboard()),
                MenuAction("_Paste", () => _canvas.PasteFromClipboard()),
                new Separator(),
                MenuAction("_Delete", () => _canvas.ClearSelectionContent())
            }
        };

        var viewMenu = new MenuItem
        {
            Header = "_View",
            ItemsSource = new object[]
            {
                MenuAction("_Mirror Horizontal", () =>
                {
                    _canvasFlip.ScaleX = -_canvasFlip.ScaleX;
                    _canvas.FlipX = (int)_canvasFlip.ScaleX;
                    _rulerOverlay?.InvalidateVisual();
                    ClampCanvasPan(); UpdateStatus();
                }),
                MenuAction("Mirror _Vertical", () =>
                {
                    _canvasFlip.ScaleY = -_canvasFlip.ScaleY;
                    _canvas.FlipY = (int)_canvasFlip.ScaleY;
                    _rulerOverlay?.InvalidateVisual();
                    ClampCanvasPan(); UpdateStatus();
                }),
                new Separator(),
                MenuAction("_Show Canvas Only", ToggleCanvasOnly),
                MenuAction("Show _Rulers", ToggleRulers),
            }
        };

        var imageMenu = new MenuItem
        {
            Header = "_Image",
            ItemsSource = new object[]
            {
                MenuAction("Rotate 90° _Clockwise", () => { _canvas.RotateCanvas90Clockwise(); SyncCanvasFrameToDocument(false); ClampCanvasPan(); _rulerOverlay?.InvalidateVisual(); }),
                MenuAction("Rotate 90° _Counter-Clockwise", () => { _canvas.RotateCanvas90CounterClockwise(); SyncCanvasFrameToDocument(false); ClampCanvasPan(); _rulerOverlay?.InvalidateVisual(); }),
                MenuAction("Rotate _180°", () => _canvas.RotateCanvas180()),
                new Separator(),
                MenuAction("Flip Canvas _Horizontal", () => _canvas.FlipCanvas(horizontal: true)),
                MenuAction("Flip Canvas _Vertical", () => _canvas.FlipCanvas(horizontal: false)),
                new Separator(),
                MenuAction("_Resize Canvas...", async () => await ShowResizeCanvasDialog()),
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
                MenuAction("Move Layer _Down", () => _canvas.MoveActiveLayer(-1)),
                new Separator(),
                MenuAction("Add _Background", AddBackgroundLayer),
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

        var filterMenu = new MenuItem
        {
            Header = "F_ilter",
            ItemsSource = new object[]
            {
                MenuAction("_Gaussian Blur...",  async () => await ApplyBlurFilter()),
                MenuAction("_Sharpen...",         async () => await ApplySharpenFilter()),
                MenuAction("_Noise...",           async () => await ApplyNoiseFilter()),
                new Separator(),
                MenuAction("_Color Curves...",    async () => await ApplyColorCurvesFilter()),
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
                ItemsSource = new[] { fileMenu, editMenu, viewMenu, imageMenu, layerMenu, brushMenu, filterMenu }
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
        var SaveDocumentTb = TbarBtn(Icons.ContentSaveOutline, "Save document  (Ctrl+S)");
        var saveTb = TbarBtn(Icons.ContentSaveOutline, "Save PSD");
        var exportTb = TbarBtn(Icons.ContentSaveOutline, "Export image");
        openTb.Click += async (_, _) => await OpenDocumentAsync();
        SaveDocumentTb.Click += async (_, _) => await SaveDocumentAsync();
        exportTb.Click += async (_, _) => await ExportImageAsync();

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(8, 0)
        };
        row.Children.Add(openTb);
        row.Children.Add(SaveDocumentTb);
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
    private Button LayerIconBtn(string icon, string tooltip, IBrush iconBrush, int index)
    {
        var btn = new Button
        {
            Content = Icons.Make(icon, 16, iconBrush),
            [ToolTip.TipProperty] = tooltip, // <-- Use the attached property syntax here
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            Tag = index
        };
        return btn;
    }

    private void SetLayerIconBtnIcon(Button btn, string icon, IBrush iconBrush)
    {
        // Notice we are passing iconBrush directly here too
        btn.Content = Icons.Make(icon, 16, iconBrush);
    }


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
        var leftDock = new Grid { ClipToBounds = true };
        leftDock.RowDefinitions.Add(new RowDefinition(new GridLength(1.7, GridUnitType.Star)) { MinHeight = 220 });
        leftDock.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        leftDock.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        leftDock.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = 160 });

        var brush = PanelSection("Brush", BuildBrushSection());
        var toolProperties = PanelSection("Tool Property", BuildToolPropertySection());
        var layerProperties = PanelSection("Layer Properties", BuildLayerPropertiesSection());
        var layers = PanelSection("Layers", BuildLayersSection());

        Grid.SetRow(brush, 0);
        Grid.SetRow(toolProperties, 1);
        Grid.SetRow(layerProperties, 2);
        Grid.SetRow(layers, 3);
        leftDock.Children.Add(brush);
        leftDock.Children.Add(toolProperties);
        leftDock.Children.Add(layerProperties);
        leftDock.Children.Add(layers);

        var rightDock = new Grid { ClipToBounds = true };
        rightDock.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rightDock.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rightDock.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = 160 });

        var color = PanelSection("Color", BuildColorSection());
        var colorSliders = PanelSection("Color Slider", BuildColorSlidersSection());
        var brushSize = PanelSection("Brush Size", new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ClipToBounds = true,
            Content = BuildBrushSizePalette()
        });

        Grid.SetRow(color, 0);
        Grid.SetRow(colorSliders, 1);
        Grid.SetRow(brushSize, 2);
        rightDock.Children.Add(color);
        rightDock.Children.Add(colorSliders);
        rightDock.Children.Add(brushSize);

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

        Grid.SetColumn(leftDock, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(rightDock, 2);
        grid.Children.Add(leftDock);
        grid.Children.Add(splitter);
        grid.Children.Add(rightDock);

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
            Margin = new Thickness(0, 0, 4, 0)
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
            Padding = new Thickness(8, 4, 8, 3),
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

        content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        var outer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(content, 1);
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
            Width = 24,
            Height = 22,
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

    private static Button SmIconBtn(string icon, string tip)
    {
        var btn = SmBtn("", tip);
        btn.Content = Icons.Make(icon, 13, new SolidColorBrush(Color.Parse(TextSecondary)));
        return btn;
    }

    private static Slider MkSlider(double min, double max, double value, string tip)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Height = 22,
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
        "°" => $"{v:0}°",
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

    private readonly HashSet<string> _dirtyBrushAssetIds = new();

    private void SaveToConfig()
    {
        CaptureActiveBrushToPreset();

        // Persist brush asset so curve edits, slider changes, etc. survive restart
        if (_activeBrushAsset != null)
        {
            _activeBrushAsset.WithPreset(CurrentBrushFromUi());
            _brushLibrary.Save(_activeBrushAsset);
        }

        // Save any other brush assets that were modified while not active
        foreach (var assetId in _dirtyBrushAssetIds.ToList())
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && asset != _activeBrushAsset)
                _brushLibrary.Save(asset);
        }
        _dirtyBrushAssetIds.Clear();

        var cfg = App.Config;
        cfg.LastBrushSize = _sizeSlider.Value;
        cfg.LastBrushOpacity = _opacitySlider.Value;
        cfg.LastBrushHardness = _hardnessSlider.Value;
        cfg.LastBrushSpacing = _spacingSlider.Value;
        var c = _canvas.PaintColor;
        cfg.LastColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        cfg.LastBrushName = _activePreset?.Name ?? "";
        cfg.Save();
        App.ToolGroups.Save();
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
            SyncCanvasFrameToDocument(fitToViewport: false);
            _rulerOverlay?.InvalidateVisual();
            BuildLayerList();
            UpdateStatus();
        };
        _canvas.LayerMetadataChanged += (_, e) => { UpdateLayerRow(e.LayerIndex); UpdateStatus(); };
        _canvas.ColorSampled += (_, c) => SetColor(c);
        _canvas.BrushSettingsRestored += brush =>
        {
            if (_suppressBrushSettingsRestored) return;
            _syncingBrushUi = true;
            _sizeSlider.Value = Math.Clamp(brush.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
            _opacitySlider.Value = Math.Clamp(brush.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
            _flowSlider.Value = Math.Clamp(brush.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
            _hardnessSlider.Value = Math.Clamp(brush.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
            _spacingSlider.Value = Math.Clamp(brush.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
            _smoothingSlider.Value = Math.Clamp(brush.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
            _grainSlider.Value = Math.Clamp(brush.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
            _syncingBrushUi = false;
            _activePreset = brush;
            _strokePreview.Brush = brush;
            _canvas.SyncBrushFromContext(brush);
            RefreshToolProperties();
        };

        SliderChanged(_sizeSlider, v => UpdateCurrentBrush(p => p with { Size = v }));
        SliderChanged(_opacitySlider, v => UpdateCurrentBrush(p => p with { Opacity = v }));
        SliderChanged(_flowSlider, v => UpdateCurrentBrush(p => p with { Flow = v }));
        SliderChanged(_hardnessSlider, v => UpdateCurrentBrush(p => p with { Hardness = v }));
        SliderChanged(_spacingSlider, v => UpdateCurrentBrush(p => p with { Spacing = v }));
        SliderChanged(_smoothingSlider, v => UpdateCurrentBrush(p => p with { Smoothing = v }));
        SliderChanged(_grainSlider, v => UpdateCurrentBrush(p => p with { Grain = v }));

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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

    private void ActivateTool(ITool tool, Button? button, ToolPreset? preset = null)
    {
        if (_canvas.IsTransformActive)
        {
            // In CSP-style transform mode, clicking another tool cancels the transform
            _canvas.CancelActiveTool();
            // After cancel, the previous tool is restored; if user clicked a different tool,
            // we still want to honor that switch, so fall through.
            if (_canvas.ActiveTool == tool) return;
        }

        _canvas.SetActiveTool(tool, preset);
        _activeToolButton = button;
        foreach (var b in _toolButtons) SetRailActive(b, b == button);
        _footerStatusText.Text = ToolDisplayName(tool);
        RefreshToolProperties();
    }

    private string ToolDisplayName(ITool tool)
    {
        return _activeToolGroup?.Name ?? tool switch
        {
            CompositeTool ct => ct.Output.IsPaintOutput ? "Brush" : "Tool",
            TransformTool => "Transform",
            _ => "Tool"
        };
    }

    internal void ActivatePreset(ToolGroup group, ToolPreset preset)
    {
        // Snapshot ALL tool settings into the preset we're leaving
        CaptureActiveBrushToPreset();

        group.LastActivePresetId = preset.Id;
        _activeToolGroup = group;

        // For brush-based engines, load the referenced brush asset first
        // and overlay the tool-preset scalar values (size, opacity etc.).
        //
        // Snapshot the *current* brush into the engine preset BEFORE we
        // touch _ctx.Brush via SyncBrushFromContext.  If we don't do this
        // first, SetActiveTool will save the *new* brush to the old engine
        // slot, swapping Brush ↔ Eraser presets on every cross-engine switch.
        _canvas.SaveBrushEnginePreset();

        var btn = _toolGroupButtons.FirstOrDefault(x => x.Group == group).Button;
        _suppressBrushSettingsRestored = true;
        try
        {
            ActivateTool(ToolForPreset(preset), btn, preset);
        }
        finally
        {
            _suppressBrushSettingsRestored = false;
        }

        // Now load the brush asset and sync the tool-property panel.
        // This runs after ActivateTool so that SetActiveTool's per-engine
        // save correctly recorded the *previous* brush.
        if (preset.Engine is ToolPresetEngine.Brush or ToolPresetEngine.Eraser or ToolPresetEngine.Smudge
            || (preset.InputProcess == InputProcessType.BrushStroke && preset.OutputProcess == OutputProcessType.DirectDraw))
        {
            BrushPreset? assetPreset = null;

            if (preset.BrushId != null)
            {
                var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
                if (asset != null) assetPreset = asset.ToPreset();
            }

            if (assetPreset != null)
            {
                var overridden = preset.ApplyToBrushPreset(assetPreset);
                _activePreset = overridden;
                _canvas.SyncBrushFromContext(overridden);
                _activeBrushLabel.Text = assetPreset.Name;
                _strokePreview.Brush = overridden;
                _syncingBrushUi = true;
                _sizeSlider.Value = Math.Clamp(overridden.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
                _opacitySlider.Value = Math.Clamp(overridden.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
                _flowSlider.Value = Math.Clamp(overridden.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
                _hardnessSlider.Value = Math.Clamp(overridden.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
                _spacingSlider.Value = Math.Clamp(overridden.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
                _smoothingSlider.Value = Math.Clamp(overridden.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
                _grainSlider.Value = Math.Clamp(overridden.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
                _syncingBrushUi = false;
            }
            else
            {
                var overridden = preset.ApplyToBrushPreset(_canvas.Brush);
                _activePreset = overridden;
                _canvas.SyncBrushFromContext(overridden);
                _activeBrushLabel.Text = preset.Name;
                _strokePreview.Brush = overridden;
            }

            // Ensure the brush Kind matches the target engine even when there's
            // no linked brush asset (e.g. default presets with BrushId == null).
            var targetKind = preset.Engine == ToolPresetEngine.Eraser
                ? BrushKind.Eraser
                : BrushKind.Ink;
            if ((_activePreset ?? _canvas.Brush).Kind != targetKind)
            {
                var fixedBrush = (_activePreset ?? _canvas.Brush) with { Kind = targetKind };
                _canvas.SyncBrushFromContext(fixedBrush);
                _activePreset = fixedBrush;
                _strokePreview.Brush = fixedBrush;
            }
        }
        else
        {
            _activePreset = null;
            _strokePreview.Brush = null;
            _activeBrushLabel.Text = preset.Name;
        }

        // Reflect active preset engine in the rail button icon
        if (btn != null) btn.Content = MaterialIcon(group.ActiveIcon, 18);

        RefreshGroupPresets();
        App.ToolGroups.Save();
    }

    private void CaptureActiveBrushToPreset()
    {
        if (_activeToolGroup == null) return;
        var active = _activeToolGroup.ActivePreset;
        if (active == null || _activePreset == null) return;
        if (active.Engine is not (ToolPresetEngine.Brush or ToolPresetEngine.Eraser or ToolPresetEngine.Smudge)) return;
        active.CaptureFromBrushPreset(_activePreset);
    }

    internal ITool ToolForPreset(ToolPreset preset)
    {
        // Always use new process-based architecture.
        if (preset.InputProcess == default || preset.OutputProcess == default)
            preset.MigrateFromLegacy();
        return _toolFactory!.CreateTool(preset);
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
            refs.PreviewImage.Source = layer.GetThumbnail(26);
        }
    }
}

internal sealed class RulerOverlay : Control
{
    private readonly DrawingCanvas _canvas;
    private const double RulerThickness = 20;

    public RulerOverlay(DrawingCanvas canvas) { _canvas = canvas; IsHitTestVisible = false; }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        var docW = _canvas.Document.Width;
        var docH = _canvas.Document.Height;
        var zoom = _canvas.CanvasZoom;
        var flipX = _canvas.FlipX;
        var flipY = _canvas.FlipY;
        var panX = _canvas.PanOffsetX;
        var panY = _canvas.PanOffsetY;

        var bg = new SolidColorBrush(Color.FromArgb(180, 30, 30, 35));
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 180, 200, 220)), 1);
        var labelBrush = new SolidColorBrush(Color.FromArgb(220, 200, 210, 220));

        var scaledW = docW * zoom;
        var stepSize = scaledW <= 400 ? 25 : scaledW <= 800 ? 50 : 100;

        // Horizontal ruler bar (top of viewport)
        ctx.FillRectangle(bg, new Rect(0, 0, w, RulerThickness));
        if (scaledW > 0)
        {
            for (var x = 0.0; x <= docW; x += stepSize)
            {
                var sx = flipX == 1
                    ? (w - scaledW) * 0.5 + x * zoom + panX
                    : (w + scaledW) * 0.5 - x * zoom + panX;
                if (sx < -0.5 || sx > w) continue;
                var isMajor = (x % (stepSize * 5)) == 0 || (stepSize >= 50 && (x % (stepSize * 2)) == 0);
                var tickH = isMajor ? RulerThickness : RulerThickness * 0.4;
                ctx.DrawLine(tickPen, new Point(sx, 0), new Point(sx, tickH));
                if (isMajor)
                {
                    var ft = new FormattedText(((int)x).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        Typeface.Default, 9, labelBrush);
                    ctx.DrawText(ft, new Point(sx + 2, tickH - 12));
                }
            }
        }

        // Vertical ruler bar (left of viewport)
        ctx.FillRectangle(bg, new Rect(0, 0, RulerThickness, h));
        var scaledH = docH * zoom;
        if (scaledH > 0)
        {
            for (var y = 0.0; y <= docH; y += stepSize)
            {
                var sy = flipY == 1
                    ? (h - scaledH) * 0.5 + y * zoom + panY
                    : (h + scaledH) * 0.5 - y * zoom + panY;
                if (sy < -0.5 || sy > h) continue;
                var isMajor = (y % (stepSize * 5)) == 0 || (stepSize >= 50 && (y % (stepSize * 2)) == 0);
                var tickW = isMajor ? RulerThickness : RulerThickness * 0.4;
                ctx.DrawLine(tickPen, new Point(0, sy), new Point(tickW, sy));
                if (isMajor)
                {
                    var ft = new FormattedText(((int)y).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        Typeface.Default, 9, labelBrush);
                    ctx.DrawText(ft, new Point(tickW - 15, sy + 2));
                }
            }
        }
    }
}
