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
    private Border _selectionActionBar = null!;
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
    private readonly Dictionary<string, RowDefinition> _dockerRows = new();
    private readonly Dictionary<string, Border> _dockerSections = new();
    private readonly Dictionary<string, int> _dockerSectionColumns = new();
    private readonly Dictionary<string, Window> _floatingDockers = new();
    private MenuItem? _workspaceLoadMenu;
    private MenuItem? _saveMenuItem;
    private MenuItem? _saveAsMenuItem;
    private MenuItem? _exportMenu;
    private MenuItem? _resetViewMenuItem;
    private MenuItem? _undoMenuItem;
    private MenuItem? _redoMenuItem;
    private MenuItem? _copyMenuItem;
    private MenuItem? _pasteMenuItem;
    private MenuItem? _editDeleteMenuItem;
    private MenuItem? _mirrorHMenuItem;
    private MenuItem? _mirrorVMenuItem;
    private MenuItem? _showCanvasOnlyMenuItem;
    private MenuItem? _showRulersMenuItem;
    private MenuItem? _imageMenu;
    private MenuItem? _addLayerMenuItem;
    private MenuItem? _duplicateLayerMenuItem;
    private MenuItem? _deleteLayerMenuItem;
    private MenuItem? _mergeDownMenuItem;
    private MenuItem? _moveUpMenuItem;
    private MenuItem? _moveDownMenuItem;
    private MenuItem? _addBackgroundMenuItem;
    private MenuItem? _filterMenu;
    private bool _suppressFloatingDockerClosed;
    private bool _suppressFloatingDockerSnap;
    private Grid? _dockerHostGrid;
    private Border? _dockerDropIndicator;
    private string? _draggingDockerId;
    private Point _dockerDragStart;
    private bool _isDockerDragging;
    private int _dockerDropColumn = -1;
    private int _dockerDropIndex = -1;
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
    private Panel _toolRailStack = null!;

    private enum GestureMode { None, Pan, Zoom, Rotate, BrushSize }
    private GestureMode _activeGesture;
    private Key _gestureKey = Key.None;
    private KeyModifiers _gestureModifiers;
    private bool _isPanning;
    private Point _lastPanPoint;
    private Point _gestureStartPoint;
    private Point _brushSizeGestureStartCanvasPoint;
    private Point _brushSizeGestureCenterCanvasPoint;
    private double _brushSizeGestureStartSize;
    private bool _brushSizeGestureHasCenter;
    private double _brushSizeLastDirX;
    private double _brushSizeLastDirY;
    private bool _brushSizeHasLastDir;
    private SettingsWindow? _settingsWindow;

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
    private Control? _leftSplitter;
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
        SelectInitialTool();
        SetColor(Color.Parse(App.Config.LastColor));
        _canvasFrame.IsVisible = false;
        SetDocumentPanelsVisible(false);
        BuildLayerList();
        UpdateStatus();
        Closing += (_, _) => SaveToConfig();
        Deactivated += (_, _) => ResetTransientInputState();
        LostFocus += (_, _) => ResetTransientInputState();
        Loaded += (_, _) => UpdateTabBar();
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

        _selectionActionBar = BuildSelectionActionBar();
        _workspaceViewport.Children.Add(_selectionActionBar);

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

        BuildTabBar();
        var centerArea = new Grid { RowDefinitions = new RowDefinitions("28,26,*,22") };
        Grid.SetRow(_tabBarContainer, 0);
        Grid.SetRow(statusBar, 1);
        Grid.SetRow(_workspaceViewport, 2);
        Grid.SetRow(footer, 3);
        centerArea.Children.Add(_tabBarContainer);
        centerArea.Children.Add(statusBar);
        centerArea.Children.Add(_workspaceViewport);
        centerArea.Children.Add(footer);

        _dockerRows.Clear();
        _dockerSections.Clear();
        _dockerSectionColumns.Clear();
        var leftRail = BuildLeftColumn();
        var rightPanel = BuildRightPanel();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(48, GridUnitType.Pixel) { MinWidth = 36 });
        root.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
        root.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(520, GridUnitType.Pixel) { MinWidth = 300, MaxWidth = 900 });
        _rootGrid = root;
        _rootColumnWidths = [.. root.ColumnDefinitions.Select(c => c.Width)];

        var leftSplitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg2))
        };
        var rightSplitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg2))
        };

        Grid.SetColumn(leftRail, 0);
        Grid.SetColumn(leftSplitter, 1);
        Grid.SetColumn(centerArea, 2);
        Grid.SetColumn(rightSplitter, 3);
        Grid.SetColumn(rightPanel, 4);
        root.Children.Add(leftRail);
        root.Children.Add(leftSplitter);
        root.Children.Add(centerArea);
        root.Children.Add(rightSplitter);
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
        _leftSplitter = leftSplitter;
        _rightPanel = rightPanel;
        _splitterControl = rightSplitter;
        _statusBar = statusBar;
        _footer = footer;

        Content = shell;
        AddHandler(PointerPressedEvent, WindowPointerPressed, RoutingStrategies.Tunnel);
    }

    private void SetDocumentPanelsVisible(bool enabled)
    {
        if (_leftRail != null) _leftRail.IsEnabled = enabled;
        if (_rightPanel != null) _rightPanel.IsEnabled = enabled;
        if (_shellToolbar != null) _shellToolbar.IsEnabled = enabled;
        if (_statusBar != null) _statusBar.IsEnabled = enabled;
    }

    private static TextBlock MiniText() => new()
    {
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        FontSize = 11,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private Border BuildSelectionActionBar()
    {
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 1,
            Margin = new Thickness(4)
        };

        var invert = SelectionBarButton(Icons.InvertColors, "Invert selection");
        invert.Click += (_, _) => { _canvas.InvertSelection(); UpdateSelectionActionBar(); };

        var cutPaste = SelectionBarButton(Icons.ContentCut, "Cut and paste selection");
        cutPaste.Click += (_, _) => { _canvas.CutSelectionAndPaste(); BuildLayerList(); UpdateSelectionActionBar(); };

        var copyPaste = SelectionBarButton(Icons.ContentCopy, "Copy and paste selection");
        copyPaste.Click += (_, _) => { _canvas.CopySelectionAndPaste(); BuildLayerList(); UpdateSelectionActionBar(); };

        var transform = SelectionBarButton(Icons.FitToScreenOutline, "Scale / rotate selection");
        transform.Click += (_, _) =>
        {
            _canvas.BeginSelectionTransform(_selectedLayerIndices.Count > 1 ? _selectedLayerIndices.ToList() : null);
            UpdateSelectionActionBar();
        };

        var fill = SelectionBarButton(Icons.FormatColorFill, "Fill selection");
        fill.Click += (_, _) => { _canvas.FillSelection(); UpdateSelectionActionBar(); };

        row.Children.Add(invert);
        row.Children.Add(cutPaste);
        row.Children.Add(copyPaste);
        row.Children.Add(transform);
        row.Children.Add(fill);

        return new Border
        {
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse("#3c3c3f")),
            BorderBrush = new SolidColorBrush(Color.Parse("#6a6a70")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = row,
            ZIndex = 50
        };
    }

    private static Button SelectionBarButton(string icon, string tip)
    {
        var btn = new Button
        {
            Content = Icons.Make(icon, 15, new SolidColorBrush(Color.Parse(TextPrimary))),
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(2)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private Control BuildMenuBar()
    {
        _saveMenuItem = MenuAction("_Save", new KeyGesture(Key.S, KeyModifiers.Control), async () => await SaveDocumentAsync());
        _saveAsMenuItem = MenuAction("Save _As...", new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift), async () => await SaveDocumentAsAsync());
        _exportMenu = new MenuItem
        {
            Header = "_Export",
            ItemsSource = new object[]
            {
                MenuAction(".bmp (BMP)...", async () => await ExportImageAsync(".bmp")),
                MenuAction(".jpg (JPEG)...", async () => await ExportImageAsync(".jpg")),
                MenuAction(".png (PNG)...", async () => await ExportImageAsync(".png")),
                MenuAction(".tif (TIFF)...", async () => await ExportImageAsync(".tif")),
                MenuAction(".webp (WebP)...", async () => await ExportImageAsync(".webp")),
                MenuAction(".psd (Photoshop Document)...", async () => await ExportPsdAsync())
            }
        };
        _resetViewMenuItem = MenuAction("_Reset View", ResetView);

        var fileMenu = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_New...", new KeyGesture(Key.N, KeyModifiers.Control), async () => await NewDocumentAsync()),
                MenuAction("_Open...", new KeyGesture(Key.O, KeyModifiers.Control), async () => await OpenDocumentAsync()),
                _saveMenuItem,
                _saveAsMenuItem,
                new Separator(),
                _exportMenu,
                new Separator(),
                _resetViewMenuItem,
                new Separator(),
                MenuAction("_Settings...", OpenSettings)
            }
        };

        _undoMenuItem = MenuAction("_Undo", new KeyGesture(Key.Z, KeyModifiers.Control), () => _canvas.Undo());
        _redoMenuItem = MenuAction("_Redo", new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift), () => _canvas.Redo());
        _copyMenuItem = MenuAction("_Copy", new KeyGesture(Key.C, KeyModifiers.Control), () => _canvas.CopyToClipboard());
        _pasteMenuItem = MenuAction("_Paste", new KeyGesture(Key.V, KeyModifiers.Control), () => _ = _canvas.PasteFromOSClipboardAsync());
        _editDeleteMenuItem = MenuAction("_Delete", () => _canvas.ClearSelectionContent());

        var editMenu = new MenuItem
        {
            Header = "_Edit",
            ItemsSource = new object[]
            {
                _undoMenuItem,
                _redoMenuItem,
                new Separator(),
                _copyMenuItem,
                _pasteMenuItem,
                new Separator(),
                _editDeleteMenuItem
            }
        };

        _mirrorHMenuItem = MenuAction("_Mirror Horizontal", () =>
                {
                    _canvasFlip.ScaleX = -_canvasFlip.ScaleX;
                    _canvas.FlipX = (int)_canvasFlip.ScaleX;
                    _rulerOverlay?.InvalidateVisual();
                    ClampCanvasPan(); UpdateStatus();
                });
        _mirrorVMenuItem = MenuAction("Mirror _Vertical", () =>
                {
                    _canvasFlip.ScaleY = -_canvasFlip.ScaleY;
                    _canvas.FlipY = (int)_canvasFlip.ScaleY;
                    _rulerOverlay?.InvalidateVisual();
                    ClampCanvasPan(); UpdateStatus();
                });
        _showCanvasOnlyMenuItem = MenuAction("_Show Canvas Only", ToggleCanvasOnly);
        _showRulersMenuItem = MenuAction("Show _Rulers", ToggleRulers);

        var viewMenu = new MenuItem
        {
            Header = "_View",
            ItemsSource = new object[]
            {
                _mirrorHMenuItem,
                _mirrorVMenuItem,
                new Separator(),
                _showCanvasOnlyMenuItem,
                _showRulersMenuItem,
            }
        };

        _imageMenu = new MenuItem
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

        _addLayerMenuItem = MenuAction("_Add Layer", new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift), () => _canvas.AddLayer());
        _duplicateLayerMenuItem = MenuAction("_Duplicate Layer", new KeyGesture(Key.J, KeyModifiers.Control), () => _canvas.DuplicateLayer());
        _deleteLayerMenuItem = MenuAction("_Delete Layer", new KeyGesture(Key.Delete, KeyModifiers.Control), () => _canvas.DeleteLayer());
        _mergeDownMenuItem = MenuAction("_Merge Down", new KeyGesture(Key.E, KeyModifiers.Control), () => _canvas.MergeDown());
        _moveUpMenuItem = MenuAction("Move Layer _Up", () => _canvas.MoveActiveLayer(1));
        _moveDownMenuItem = MenuAction("Move Layer _Down", () => _canvas.MoveActiveLayer(-1));
        _addBackgroundMenuItem = MenuAction("Add _Background", AddBackgroundLayer);

        var layerMenu = new MenuItem
        {
            Header = "_Layer",
            ItemsSource = new object[]
            {
                _addLayerMenuItem,
                _duplicateLayerMenuItem,
                _deleteLayerMenuItem,
                new Separator(),
                _mergeDownMenuItem,
                new Separator(),
                _moveUpMenuItem,
                _moveDownMenuItem,
                new Separator(),
                _addBackgroundMenuItem,
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

        _filterMenu = new MenuItem
        {
            Header = "F_ilter",
            ItemsSource = new object[]
            {
                MenuAction("_Gaussian Blur...",  async () => await ApplyBlurFilter()),
                MenuAction("_Sharpen...",         async () => await ApplySharpenFilter()),
                MenuAction("_Noise...",           async () => await ApplyNoiseFilter()),
                new Separator(),
                MenuAction("_Color Curves...",    async () => await ApplyColorCurvesFilter()),
                MenuAction("Chromatic _Aberration...", async () => await ApplyChromaticAberrationFilter()),
                new Separator(),
                MenuAction("_Base Color Masks from Sketch...", async () => await RunBaseColorMaskGenerator()),
                MenuAction("Remove _Dust...", async () => await ApplyRemoveDustFilter()),
            }
        };

        var dockersMenu = new MenuItem { Header = "_Dockers" };
        dockersMenu.SubmenuOpened += (_, _) => RefreshDockersMenu(dockersMenu);
        RefreshDockersMenu(dockersMenu);

        var workspaceMenu = BuildWorkspaceMenu();
        var windowMenu = new MenuItem
        {
            Header = "_Window",
            ItemsSource = new object[] { dockersMenu, new Separator(), workspaceMenu }
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
                ItemsSource = new[] { fileMenu, editMenu, viewMenu, _imageMenu, layerMenu, brushMenu, _filterMenu, windowMenu }
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

        var openTb = TbarBtn(Icons.FolderOpenOutline, "Open document  (Ctrl+O)");
        var SaveDocumentTb = TbarBtn(Icons.ContentSaveOutline, "Save document  (Ctrl+S)");
        var exportPsdTb = TbarBtn(Icons.ContentSaveOutline, "Export PSD");
        var exportTb = TbarBtn(Icons.ContentSaveOutline, "Export image");
        openTb.Click += async (_, _) => await OpenDocumentAsync();
        SaveDocumentTb.Click += async (_, _) => await SaveDocumentAsync();
        exportPsdTb.Click += async (_, _) => await ExportPsdAsync();
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
        row.Children.Add(exportPsdTb);
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

    private static MenuItem MenuAction(string header, KeyGesture gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Left column ────────────────────────────────────────────────────────────
    private Border BuildLeftColumn()
    {
        var layout = NormalizedWorkspaceLayout();
        var column = BuildDockColumn(layout.LeftColumn, -1);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            CacheMode = new BitmapCache(),
            ClipToBounds = true,
            Child = column
        };
    }

    // ── Right panel ───────────────────────────────────────────────────────────
    private Border BuildRightPanel()
    {
        var layout = NormalizedWorkspaceLayout();
        var leftDock = BuildDockColumn(layout.RightColumns[0], 0);
        var rightDock = BuildDockColumn(layout.RightColumns[1], 1);

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Stroke))
        };

        var grid = new Grid { ClipToBounds = true };
        _dockerHostGrid = grid;
        var split = Math.Clamp(layout.RightDockSplit, 0.2, 0.8);
        grid.ColumnDefinitions.Add(new ColumnDefinition(split, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1 - split, GridUnitType.Star));

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

    private Grid BuildDockColumn(DockColumnConfig column, int columnIndex)
    {
        var grid = new Grid { ClipToBounds = true };
        var panelIds = column.Panels
            .Where(id => GetDockerInfo(id) != null && !IsDockerFloating(id) && IsDockerVisible(id))
            .ToList();

        if (panelIds.Count == 0)
            return grid;

        for (var i = 0; i < panelIds.Count; i++)
        {
            var id = panelIds[i];
            var height = App.Config.WorkspaceLayout.PanelHeights.TryGetValue(id, out var saved)
                ? Math.Max(80, saved)
                : DefaultDockerHeight(id);
            var row = new RowDefinition(new GridLength(height, GridUnitType.Star))
            {
                MinHeight = MinDockerHeight(id)
            };
            _dockerRows[id] = row;
            grid.RowDefinitions.Add(row);

            var info = GetDockerInfo(id)!;
            var section = PanelSection(id, info.Title, info.Build());
            _dockerSections[id] = section;
            _dockerSectionColumns[id] = columnIndex;
            Grid.SetRow(section, grid.RowDefinitions.Count - 1);
            grid.Children.Add(section);

            if (i == panelIds.Count - 1) continue;
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(5, GridUnitType.Pixel)));
            var splitter = new GridSplitter
            {
                Height = 5,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                Background = new SolidColorBrush(Color.Parse(Stroke))
            };
            Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
            grid.Children.Add(splitter);
        }

        return grid;
    }

    private Border PanelSection(string id, string title, Control content)
    {
        var body = BuildDockerBody(id, content);
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
            Children = { titleText }
        };
        var header = new Border
        {
            Padding = new Thickness(8, 4, 8, 3),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = headerRow,
            ContextMenu = BuildDockerContextMenu(id)
        };
        header.PointerPressed += (_, e) => DockerHeaderPointerPressed(id, header, e);
        header.PointerMoved += (_, e) => DockerHeaderPointerMoved(id, header, e);
        header.PointerReleased += (_, e) => DockerHeaderPointerReleased(id, header, e);
        header.PointerCaptureLost += (_, _) => CancelDockerDrag();

        var outer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ClipToBounds = true
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        outer.Children.Add(header);
        outer.Children.Add(body);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            ClipToBounds = true,
            Child = outer
        };
    }

    private static Control BuildDockerBody(string id, Control content)
    {
        content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        content.ClipToBounds = true;

        Control child = id == "tool-properties"
            ? new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ClipToBounds = true,
                Content = content
            }
            : content;

        return new Border
        {
            ClipToBounds = true,
            Child = child
        };
    }

    private void DockerHeaderPointerPressed(string id, Control header, PointerPressedEventArgs e)
    {
        if (IsDockerFloating(id)) return;
        var point = e.GetCurrentPoint(header);
        if (!point.Properties.IsLeftButtonPressed) return;
        _draggingDockerId = id;
        _dockerDragStart = point.Position;
        _isDockerDragging = false;
        _dockerDropColumn = -1;
        _dockerDropIndex = -1;
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    private void DockerHeaderPointerMoved(string id, Control header, PointerEventArgs e)
    {
        if (_draggingDockerId != id) return;
        var local = e.GetPosition(header);
        var dx = local.X - _dockerDragStart.X;
        var dy = local.Y - _dockerDragStart.Y;
        if (!_isDockerDragging && dx * dx + dy * dy < 36)
            return;

        _isDockerDragging = true;
        if (_dockerHostGrid == null) return;
        UpdateDockerDropPreview(id, e.GetPosition(_dockerHostGrid));
        e.Handled = true;
    }

    private void DockerHeaderPointerReleased(string id, Control header, PointerReleasedEventArgs e)
    {
        if (_draggingDockerId != id) return;
        if (_isDockerDragging && _dockerDropColumn >= 0 && _dockerDropIndex >= 0)
            ApplyDockerDrop(id, _dockerDropColumn, _dockerDropIndex);
        CancelDockerDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CancelDockerDrag()
    {
        _draggingDockerId = null;
        _isDockerDragging = false;
        _dockerDropColumn = -1;
        _dockerDropIndex = -1;
        if (_dockerDropIndicator != null)
            _dockerDropIndicator.IsVisible = false;
    }

    private void UpdateDockerDropPreview(string movingId, Point hostPoint)
    {
        if (_dockerHostGrid == null) return;
        var target = ResolveDockerDropTarget(movingId, hostPoint);
        if (target == null)
        {
            if (_dockerDropIndicator != null)
                _dockerDropIndicator.IsVisible = false;
            _dockerDropColumn = -1;
            _dockerDropIndex = -1;
            return;
        }

        var (columnIndex, insertionIndex, x, y, width) = target.Value;
        _dockerDropColumn = columnIndex;
        _dockerDropIndex = insertionIndex;

        _dockerDropIndicator ??= new Border
        {
            Height = 3,
            Background = new SolidColorBrush(Color.Parse(Accent)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            ZIndex = 1000
        };
        if (_dockerDropIndicator.Parent is Panel parent && parent != _dockerHostGrid)
            parent.Children.Remove(_dockerDropIndicator);
        if (!_dockerHostGrid.Children.Contains(_dockerDropIndicator))
            _dockerHostGrid.Children.Add(_dockerDropIndicator);

        _dockerDropIndicator.Width = Math.Max(16, width);
        _dockerDropIndicator.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
        _dockerDropIndicator.IsVisible = true;
    }

    private (int ColumnIndex, int InsertionIndex, double X, double Y, double Width)? ResolveDockerDropTarget(string movingId, Point hostPoint)
    {
        if (_dockerHostGrid == null || _dockerHostGrid.ColumnDefinitions.Count < 3)
            return null;
        if (hostPoint.X < 0 || hostPoint.Y < 0 ||
            hostPoint.X > _dockerHostGrid.Bounds.Width ||
            hostPoint.Y > _dockerHostGrid.Bounds.Height)
            return null;

        var leftWidth = _dockerHostGrid.ColumnDefinitions[0].ActualWidth;
        var splitterWidth = _dockerHostGrid.ColumnDefinitions[1].ActualWidth;
        var rightWidth = _dockerHostGrid.ColumnDefinitions[2].ActualWidth;
        var columnIndex = hostPoint.X <= leftWidth + splitterWidth * 0.5 ? 0 : 1;
        var x = columnIndex == 0 ? 0 : leftWidth + splitterWidth;
        var width = columnIndex == 0 ? leftWidth : rightWidth;

        var ids = NormalizedWorkspaceLayout().RightColumns[columnIndex].Panels
            .Where(id => id != movingId && !IsDockerFloating(id) && _dockerSections.ContainsKey(id))
            .ToList();

        if (ids.Count == 0)
            return (columnIndex, 0, x, 0, width);

        var insertionIndex = 0;
        var y = 0.0;
        for (var i = 0; i < ids.Count; i++)
        {
            var section = _dockerSections[ids[i]];
            var topLeft = section.TranslatePoint(new Point(0, 0), _dockerHostGrid) ?? new Point(x, 0);
            var midpoint = topLeft.Y + section.Bounds.Height * 0.5;
            if (hostPoint.Y < midpoint)
            {
                y = topLeft.Y;
                insertionIndex = i;
                return (columnIndex, insertionIndex, x, Math.Max(0, y), width);
            }

            insertionIndex = i + 1;
            y = topLeft.Y + section.Bounds.Height;
        }

        return (columnIndex, insertionIndex, x, Math.Max(0, y), width);
    }

    private void ApplyDockerDrop(string id, int columnIndex, int insertionIndex)
    {
        SaveWorkspaceLayoutFromUi();
        var layout = NormalizedWorkspaceLayout();

        foreach (var column in layout.RightColumns)
            column.Panels.Remove(id);
        layout.LeftColumn.Panels.Remove(id);

        if (columnIndex < 0)
        {
            insertionIndex = Math.Clamp(insertionIndex, 0, layout.LeftColumn.Panels.Count);
            layout.LeftColumn.Panels.Insert(insertionIndex, id);
        }
        else
        {
            if ((uint)columnIndex >= (uint)layout.RightColumns.Count) return;
            var target = layout.RightColumns[columnIndex].Panels;
            insertionIndex = Math.Clamp(insertionIndex, 0, target.Count);
            target.Insert(insertionIndex, id);
        }

        if (layout.FloatingPanels.TryGetValue(id, out var floating))
            floating.IsFloating = false;

        RebuildDockers();
        App.Config.Save();
    }

    private sealed record DockerInfo(string Id, string Title, Func<Control> Build);

    private DockerInfo? GetDockerInfo(string id) => id switch
    {
        "tools" => new DockerInfo(id, "Tools", BuildToolsContent),
        "brush" => new DockerInfo(id, "Brush", BuildBrushSection),
        "tool-properties" => new DockerInfo(id, "Tool Property", BuildToolPropertySection),
        "layer-properties" => new DockerInfo(id, "Layer Properties", BuildLayerPropertiesSection),
        "layers" => new DockerInfo(id, "Layers", BuildLayersSection),
        "color" => new DockerInfo(id, "Color", BuildColorSection),
        "color-slider" => new DockerInfo(id, "Color Slider", BuildColorSlidersSection),
        "brush-size" => new DockerInfo(id, "Brush Size", () => new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ClipToBounds = true,
            Content = BuildBrushSizePalette()
        }),
        _ => null
    };

    private static readonly string[] AllDockerIds = ["tools", "brush", "tool-properties", "layer-properties", "layers", "color", "color-slider", "brush-size"];

    private WorkspaceLayoutConfig NormalizedWorkspaceLayout()
    {
        var layout = App.Config.WorkspaceLayout ??= WorkspaceLayoutConfig.Default();
        if (layout.RightColumns.Count < 2)
            layout.RightColumns = WorkspaceLayoutConfig.Default().RightColumns;
        var known = new HashSet<string>(AllDockerIds);
        foreach (var column in layout.RightColumns)
            column.Panels = column.Panels.Where(known.Contains).Distinct().ToList();
        layout.LeftColumn.Panels = layout.LeftColumn.Panels.Where(known.Contains).Distinct().ToList();
        foreach (var id in known)
        {
            if (layout.RightColumns.Any(c => c.Panels.Contains(id))) continue;
            if (layout.LeftColumn.Panels.Contains(id)) continue;
            if (layout.FloatingPanels.ContainsKey(id)) continue;
            if (id == "tools")
                layout.LeftColumn.Panels.Add(id);
            else
                layout.RightColumns[0].Panels.Add(id);
        }
        return layout;
    }

    private bool IsDockerFloating(string id)
        => App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var f) && f.IsFloating;

    private static double DefaultDockerHeight(string id) => id switch
    {
        "brush" => 260,
        "layers" => 220,
        "brush-size" => 260,
        "color" => 230,
        "color-slider" => 170,
        _ => 120
    };

    private static double MinDockerHeight(string id) => id switch
    {
        "brush" => 160,
        "layers" => 140,
        "brush-size" => 140,
        "color" => 180,
        _ => 64
    };

    private ContextMenu BuildDockerContextMenu(string id)
    {
        var placement = FindDockerPlacement(id);
        var isOnLeft = placement?.ColumnIndex == -1;
        var isFloating = IsDockerFloating(id);

        var floatItem = new MenuItem { Header = isFloating ? "_Dock" : "_Detach" };
        floatItem.Click += (_, _) =>
        {
            if (IsDockerFloating(id)) DockDocker(id);
            else DetachDocker(id);
        };

        var moveLeft = new MenuItem { Header = "Move to _Left Side", IsEnabled = !isFloating && !isOnLeft };
        moveLeft.Click += (_, _) => DockDockerToColumn(id, -1);

        var moveRight = new MenuItem { Header = "Move to _Right Side", IsEnabled = !isFloating && isOnLeft };
        moveRight.Click += (_, _) => DockDockerToColumn(id, 0);

        var moveUp = new MenuItem { Header = "Move _Up", IsEnabled = !isFloating };
        moveUp.Click += (_, _) => MoveDocker(id, -1);

        var moveDown = new MenuItem { Header = "Move _Down", IsEnabled = !isFloating };
        moveDown.Click += (_, _) => MoveDocker(id, 1);

        var reset = new MenuItem { Header = "_Reset Panel Size" };
        reset.Click += (_, _) =>
        {
            App.Config.WorkspaceLayout.PanelHeights.Remove(id);
            RebuildDockers();
        };

        return new ContextMenu
        {
            ItemsSource = new object[] { floatItem, new Separator(), moveLeft, moveRight, new Separator(), moveUp, moveDown, new Separator(), reset }
        };
    }

    private void ToggleDockerVisibility(string id)
    {
        var layout = NormalizedWorkspaceLayout();
        if (layout.HiddenDockers.Contains(id))
            layout.HiddenDockers.Remove(id);
        else
            layout.HiddenDockers.Add(id);
        RebuildDockers();
        App.Config.Save();
    }

    private bool IsDockerVisible(string id)
        => !NormalizedWorkspaceLayout().HiddenDockers.Contains(id);

    private void RefreshDockersMenu(MenuItem dockersMenu)
    {
        dockersMenu.Items.Clear();
        foreach (var id in AllDockerIds)
        {
            var dockerId = id;
            var item = new MenuItem
            {
                Header = DockerTitle(id),
                IsChecked = IsDockerVisible(id)
            };
            item.Click += (_, _) => ToggleDockerVisibility(dockerId);
            dockersMenu.Items.Add(item);
        }
    }

    private static string DockerTitle(string id) => id switch
    {
        "tools" => "Tools",
        "brush" => "Brush",
        "tool-properties" => "Tool Property",
        "layer-properties" => "Layer Properties",
        "layers" => "Layers",
        "color" => "Color",
        "color-slider" => "Color Slider",
        "brush-size" => "Brush Size",
        _ => id
    };

    private (DockColumnConfig Column, int ColumnIndex, int PanelIndex)? FindDockerPlacement(string id)
    {
        var layout = NormalizedWorkspaceLayout();
        var leftIdx = layout.LeftColumn.Panels.IndexOf(id);
        if (leftIdx >= 0)
            return (layout.LeftColumn, -1, leftIdx);
        for (var col = 0; col < layout.RightColumns.Count; col++)
        {
            var index = layout.RightColumns[col].Panels.IndexOf(id);
            if (index >= 0)
                return (layout.RightColumns[col], col, index);
        }
        return null;
    }

    private void MoveDocker(string id, int delta)
    {
        SaveWorkspaceLayoutFromUi();
        var placement = FindDockerPlacement(id);
        if (placement == null) return;
        var (column, _, panelIndex) = placement.Value;
        var target = Math.Clamp(panelIndex + delta, 0, column.Panels.Count - 1);
        if (target == panelIndex) return;
        column.Panels.RemoveAt(panelIndex);
        column.Panels.Insert(target, id);
        RebuildDockers();
        App.Config.Save();
    }

    private void MoveDockerToOtherColumn(string id)
    {
        SaveWorkspaceLayoutFromUi();
        var placement = FindDockerPlacement(id);
        if (placement == null) return;
        var (column, columnIndex, panelIndex) = placement.Value;
        var layout = NormalizedWorkspaceLayout();

        column.Panels.RemoveAt(panelIndex);

        if (columnIndex < 0)
        {
            layout.RightColumns[0].Panels.Add(id);
        }
        else if (App.Config.WorkspaceLayout.RightColumns.Count >= 2)
        {
            var targetColumn = App.Config.WorkspaceLayout.RightColumns[columnIndex == 0 ? 1 : 0];
            targetColumn.Panels.Add(id);
        }
        else
        {
            layout.LeftColumn.Panels.Add(id);
        }

        RebuildDockers();
        App.Config.Save();
    }

    private void DockDockerToColumn(string id, int columnIndex)
    {
        SaveWorkspaceLayoutFromUi();
        var layout = NormalizedWorkspaceLayout();

        foreach (var column in layout.RightColumns)
            column.Panels.Remove(id);
        layout.LeftColumn.Panels.Remove(id);

        if (columnIndex < 0)
            layout.LeftColumn.Panels.Add(id);
        else if (columnIndex < layout.RightColumns.Count)
            layout.RightColumns[columnIndex].Panels.Add(id);

        if (layout.FloatingPanels.TryGetValue(id, out var floating))
            floating.IsFloating = false;
        if (_floatingDockers.Remove(id, out var win))
        {
            win.Closing -= FloatingDockerClosing;
            win.Close();
        }

        RebuildDockers();
        App.Config.Save();
    }

    private void DetachDocker(string id)
    {
        SaveWorkspaceLayoutFromUi();
        if (!App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var floating))
            floating = App.Config.WorkspaceLayout.FloatingPanels[id] = new FloatingDockerConfig();
        floating.IsFloating = true;
        if (floating.Width <= 0) floating.Width = 320;
        if (floating.Height <= 0) floating.Height = 480;
        RebuildDockers();
        OpenFloatingDocker(id);
        App.Config.Save();
    }

    private void DockDocker(string id)
    {
        SaveFloatingDockerBounds(id);
        if (App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var floating))
            floating.IsFloating = false;
        if (_floatingDockers.Remove(id, out var win))
        {
            win.Closing -= FloatingDockerClosing;
            win.Close();
        }
        RebuildDockers();
        App.Config.Save();
    }

    private void OpenFloatingDockersFromConfig()
    {
        foreach (var id in App.Config.WorkspaceLayout.FloatingPanels
                     .Where(pair => pair.Value.IsFloating)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            OpenFloatingDocker(id);
        }
    }

    private void OpenFloatingDocker(string id)
    {
        if (_floatingDockers.ContainsKey(id)) return;
        var info = GetDockerInfo(id);
        if (info == null) return;

        var cfg = App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var f)
            ? f
            : new FloatingDockerConfig();
        var window = new Window
        {
            Title = info.Title,
            Width = Math.Max(220, cfg.Width),
            Height = Math.Max(180, cfg.Height),
            MinWidth = 220,
            MinHeight = 140,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Content = PanelSection(id, info.Title, info.Build())
        };
        var position = FindFloatingDockerPosition(id, cfg, window.Width, window.Height);
        window.Position = position;
        window.PositionChanged += (_, _) => SnapFloatingDocker(id);
        window.Closing += FloatingDockerClosing;
        window.Closed += (_, _) =>
        {
            _floatingDockers.Remove(id);
            if (_suppressFloatingDockerClosed) return;
            if (App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var floating))
            {
                floating.IsFloating = false;
                App.Config.Save();
                RebuildDockers();
            }
        };
        _floatingDockers[id] = window;
        window.Show(this);
    }

    private const int DockerSnapDistance = 18;
    private const int DockerGap = 6;

    private PixelPoint FindFloatingDockerPosition(string id, FloatingDockerConfig cfg, double width, double height)
    {
        var x = cfg.X;
        var y = cfg.Y;
        var rect = new Rect(x, y, Math.Max(220, width), Math.Max(180, height));

        for (var i = 0; i < 24 && FloatingDockerOverlaps(id, rect); i++)
        {
            rect = rect.WithX(rect.X + 28).WithY(rect.Y + 28);
        }

        return new PixelPoint((int)Math.Round(rect.X), (int)Math.Round(rect.Y));
    }

    private void SnapFloatingDocker(string id)
    {
        if (_suppressFloatingDockerSnap) return;
        if (!_floatingDockers.TryGetValue(id, out var window)) return;

        var rect = WindowRect(window);
        var snapped = SnapRectToMagneticTargets(id, rect);
        snapped = PushRectOutOfOverlaps(id, snapped);

        if (Math.Abs(snapped.X - rect.X) < 0.5 && Math.Abs(snapped.Y - rect.Y) < 0.5)
            return;

        _suppressFloatingDockerSnap = true;
        window.Position = new PixelPoint((int)Math.Round(snapped.X), (int)Math.Round(snapped.Y));
        _suppressFloatingDockerSnap = false;
    }

    private Rect SnapRectToMagneticTargets(string id, Rect rect)
    {
        foreach (var target in FloatingDockerMagneticTargets(id))
            rect = SnapRectToTarget(rect, target);
        return rect;
    }

    private Rect SnapRectToTarget(Rect rect, Rect target)
    {
        var x = rect.X;
        var y = rect.Y;

        if (RangesOverlap(rect.Top, rect.Bottom, target.Top, target.Bottom, DockerSnapDistance))
        {
            if (Near(rect.Left, target.Left)) x = target.Left;
            else if (Near(rect.Right, target.Right)) x = target.Right - rect.Width;
            else if (Near(rect.Left, target.Right + DockerGap)) x = target.Right + DockerGap;
            else if (Near(rect.Right + DockerGap, target.Left)) x = target.Left - DockerGap - rect.Width;
        }

        if (RangesOverlap(rect.Left, rect.Right, target.Left, target.Right, DockerSnapDistance))
        {
            if (Near(rect.Top, target.Top)) y = target.Top;
            else if (Near(rect.Bottom, target.Bottom)) y = target.Bottom - rect.Height;
            else if (Near(rect.Top, target.Bottom + DockerGap)) y = target.Bottom + DockerGap;
            else if (Near(rect.Bottom + DockerGap, target.Top)) y = target.Top - DockerGap - rect.Height;
        }

        return new Rect(x, y, rect.Width, rect.Height);
    }

    private Rect PushRectOutOfOverlaps(string id, Rect rect)
    {
        for (var i = 0; i < 8; i++)
        {
            var overlap = FloatingDockerRects(id).FirstOrDefault(other => Intersects(rect, other));
            if (overlap.Width <= 0 || overlap.Height <= 0)
                return rect;

            var pushRight = overlap.Right + DockerGap - rect.Left;
            var pushLeft = rect.Right - overlap.Left + DockerGap;
            var pushDown = overlap.Bottom + DockerGap - rect.Top;
            var pushUp = rect.Bottom - overlap.Top + DockerGap;
            var min = Math.Min(Math.Min(pushRight, pushLeft), Math.Min(pushDown, pushUp));

            if (Math.Abs(min - pushRight) < 0.001)
                rect = rect.WithX(overlap.Right + DockerGap);
            else if (Math.Abs(min - pushLeft) < 0.001)
                rect = rect.WithX(overlap.Left - DockerGap - rect.Width);
            else if (Math.Abs(min - pushDown) < 0.001)
                rect = rect.WithY(overlap.Bottom + DockerGap);
            else
                rect = rect.WithY(overlap.Top - DockerGap - rect.Height);
        }

        return rect;
    }

    private IEnumerable<Rect> FloatingDockerMagneticTargets(string movingId)
    {
        yield return WindowRect(this);
        foreach (var rect in FloatingDockerRects(movingId))
            yield return rect;
    }

    private IEnumerable<Rect> FloatingDockerRects(string movingId)
    {
        foreach (var (id, window) in _floatingDockers)
        {
            if (id == movingId) continue;
            yield return WindowRect(window);
        }
    }

    private bool FloatingDockerOverlaps(string id, Rect rect)
        => FloatingDockerRects(id).Any(other => Intersects(rect, other));

    private static Rect WindowRect(Window window)
        => new(window.Position.X, window.Position.Y, Math.Max(1, window.Width), Math.Max(1, window.Height));

    private static bool Near(double a, double b)
        => Math.Abs(a - b) <= DockerSnapDistance;

    private static bool RangesOverlap(double a0, double a1, double b0, double b1, double pad = 0)
        => a0 <= b1 + pad && b0 <= a1 + pad;

    private static bool Intersects(Rect a, Rect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private void FloatingDockerClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window) return;
        var id = _floatingDockers.FirstOrDefault(pair => pair.Value == window).Key;
        if (string.IsNullOrWhiteSpace(id)) return;
        SaveFloatingDockerBounds(id);
    }

    private void SaveFloatingDockerBounds(string id)
    {
        if (!_floatingDockers.TryGetValue(id, out var window)) return;
        if (!App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var cfg))
            cfg = App.Config.WorkspaceLayout.FloatingPanels[id] = new FloatingDockerConfig();
        cfg.X = window.Position.X;
        cfg.Y = window.Position.Y;
        cfg.Width = window.Width;
        cfg.Height = window.Height;
    }

    private void SyncLeftColumnWidth()
    {
        if (_rootGrid == null || _rootGrid.ColumnDefinitions.Count < 1) return;
        var layout = NormalizedWorkspaceLayout();
        var hasFullPanels = layout.LeftColumn.Panels.Any(id => id != "tools");
        if (hasFullPanels)
        {
            // Expand to stored width or a sensible default if we were in narrow mode
            if (layout.LeftRailWidth <= 56)
                layout.LeftRailWidth = 280;
            _rootGrid.ColumnDefinitions[0].Width =
                new GridLength(Math.Clamp(layout.LeftRailWidth, 120, 800), GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[0].MinWidth = 120;
        }
        else
        {
            // Narrow tool-strip mode
            layout.LeftRailWidth = 48;
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(48, GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[0].MinWidth = 36;
        }
    }

    private void RebuildDockers()
    {
        if (_rootGrid == null) return;
        if (_dockerDropIndicator != null)
        {
            if (_dockerHostGrid?.Children.Contains(_dockerDropIndicator) == true)
                _dockerHostGrid.Children.Remove(_dockerDropIndicator);
            _dockerDropIndicator = null;
        }

        // Rebuild left column
        if (_leftRail != null)
        {
            var leftCol = Grid.GetColumn(_leftRail);
            _rootGrid.Children.Remove(_leftRail);
            _leftRail = BuildLeftColumn();
            Grid.SetColumn(_leftRail, leftCol);
            _rootGrid.Children.Add(_leftRail);
        }

        // Rebuild right panel
        if (_rightPanel != null)
        {
            var rightCol = Grid.GetColumn(_rightPanel);
            _rootGrid.Children.Remove(_rightPanel);
            _rightPanel = BuildRightPanel();
            Grid.SetColumn(_rightPanel, rightCol);
            _rootGrid.Children.Add(_rightPanel);
        }

        SyncLeftColumnWidth();
        RefreshDockerContentAfterRebuild();
    }

    private void RefreshDockerContentAfterRebuild()
    {
        RefreshGroupPresets();
        RefreshToolProperties();
        RefreshLayerProperties();
        BuildLayerList();

        if (_activePreset != null && _strokePreview != null)
            _strokePreview.Brush = _activePreset;
        if (_activeBrushLabel != null)
            _activeBrushLabel.Text = _activeBrushAsset?.Preset.Name
                ?? _activeToolGroup?.ActivePreset?.Name
                ?? "";
    }

    private void SaveWorkspaceLayoutFromUi()
    {
        var layout = NormalizedWorkspaceLayout();
        if (_rootGrid != null && _rootGrid.ColumnDefinitions.Count > 4)
        {
            if (_rootGrid.ColumnDefinitions[0].ActualWidth > 0)
                layout.LeftRailWidth = Math.Max(36, _rootGrid.ColumnDefinitions[0].ActualWidth);
            if (_rootGrid.ColumnDefinitions[4].ActualWidth > 0)
                layout.RightPanelWidth = Math.Max(300, _rootGrid.ColumnDefinitions[4].ActualWidth);
        }
        if (_rightPanel is Border { Child: Grid dockGrid } && dockGrid.ColumnDefinitions.Count >= 3)
        {
            var left = dockGrid.ColumnDefinitions[0].ActualWidth;
            var right = dockGrid.ColumnDefinitions[2].ActualWidth;
            if (left + right > 1)
                layout.RightDockSplit = left / (left + right);
        }

        foreach (var (id, row) in _dockerRows)
            layout.PanelHeights[id] = Math.Max(40, row.ActualHeight > 0 ? row.ActualHeight : row.Height.Value);

        foreach (var id in _floatingDockers.Keys.ToArray())
            SaveFloatingDockerBounds(id);
    }

    private MenuItem BuildWorkspaceMenu()
    {
        _workspaceLoadMenu = new MenuItem { Header = "_Load Preset" };
        RefreshWorkspaceLoadMenu();

        var savePreset = new MenuItem { Header = "_Save Preset..." };
        savePreset.Click += async (_, _) =>
        {
            var name = await PromptForWorkspacePresetName();
            if (string.IsNullOrWhiteSpace(name)) return;
            SaveWorkspaceLayoutFromUi();
            App.Config.WorkspacePresets[name.Trim()] = App.Config.WorkspaceLayout.Clone();
            App.Config.Save();
            RefreshWorkspaceLoadMenu();
        };

        var deletePreset = new MenuItem { Header = "_Delete Preset" };
        deletePreset.SubmenuOpened += (_, _) =>
        {
            deletePreset.ItemsSource = App.Config.WorkspacePresets.Count == 0
                ? new object[] { new MenuItem { Header = "(No presets)", IsEnabled = false } }
                : App.Config.WorkspacePresets.Keys.OrderBy(x => x).Select(name =>
                {
                    var item = new MenuItem { Header = name };
                    item.Click += (_, _) =>
                    {
                        App.Config.WorkspacePresets.Remove(name);
                        App.Config.Save();
                        RefreshWorkspaceLoadMenu();
                    };
                    return item;
                }).Cast<object>().ToArray();
        };

        var reset = new MenuItem { Header = "_Reset Layout" };
        reset.Click += (_, _) =>
        {
            App.Config.WorkspaceLayout = WorkspaceLayoutConfig.Default();
            ApplyWorkspaceLayout();
        };

        return new MenuItem
        {
            Header = "_Workspace",
            ItemsSource = new object[] { savePreset, _workspaceLoadMenu, deletePreset, new Separator(), reset }
        };
    }

    private void RefreshWorkspaceLoadMenu()
    {
        if (_workspaceLoadMenu == null) return;
        var items = App.Config.WorkspacePresets.Count == 0
            ? new object[] { new MenuItem { Header = "(No presets)", IsEnabled = false } }
            : App.Config.WorkspacePresets.Keys.OrderBy(x => x).Select(name =>
            {
                var item = new MenuItem { Header = name };
                item.Click += (_, _) =>
                {
                    SaveWorkspaceLayoutFromUi();
                    App.Config.WorkspaceLayout = App.Config.WorkspacePresets[name].Clone();
                    ApplyWorkspaceLayout();
                };
                return item;
            }).Cast<object>().ToArray();
        _workspaceLoadMenu.ItemsSource = items;
    }

    private void ApplyWorkspaceLayout()
    {
        _suppressFloatingDockerClosed = true;
        foreach (var window in _floatingDockers.Values.ToArray())
            window.Close();
        _suppressFloatingDockerClosed = false;
        _floatingDockers.Clear();
        if (_rootGrid != null && _rootGrid.ColumnDefinitions.Count > 4)
        {
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(
                Math.Clamp(App.Config.WorkspaceLayout.LeftRailWidth, 36, 800),
                GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[4].Width = new GridLength(
                Math.Clamp(App.Config.WorkspaceLayout.RightPanelWidth, 300, 1000),
                GridUnitType.Pixel);
        }
        RebuildDockers();
        OpenFloatingDockersFromConfig();
        RefreshWorkspaceLoadMenu();
        App.Config.Save();
    }

    private async System.Threading.Tasks.Task<string?> PromptForWorkspacePresetName()
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Title = "Save Workspace Preset",
            Width = 300,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        var tb = new TextBox { Margin = new Thickness(12), PlaceholderText = "Preset name" };
        var ok = new Button { Content = "Save", Margin = new Thickness(12, 0, 12, 12) };
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tcs.TrySetResult(tb.Text); dialog.Close(); } };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Content = new StackPanel { Children = { tb, ok } };
        await dialog.ShowDialog(this);
        return await tcs.Task;
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
        cfg.WorkspaceLayout ??= WorkspaceLayoutConfig.Default();
        if (_rootGrid != null && _rootGrid.ColumnDefinitions.Count > 4)
        {
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(
                Math.Clamp(cfg.WorkspaceLayout.LeftRailWidth, 36, 800),
                GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[4].Width = new GridLength(
                Math.Clamp(cfg.WorkspaceLayout.RightPanelWidth, 300, 1000),
                GridUnitType.Pixel);
        }
        _sizeSlider.Value = Math.Clamp(cfg.LastBrushSize, _sizeSlider.Minimum, _sizeSlider.Maximum);
        _opacitySlider.Value = Math.Clamp(cfg.LastBrushOpacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
        _hardnessSlider.Value = Math.Clamp(cfg.LastBrushHardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
        _spacingSlider.Value = Math.Clamp(cfg.LastBrushSpacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
        OpenFloatingDockersFromConfig();
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
        SaveWorkspaceLayoutFromUi();
        SaveActiveToolSelection();
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
        _workspaceViewport.SizeChanged += (_, _) => SyncCanvasViewport();

        WireCanvas();
        SyncCanvasViewport();

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

        // Save the current category for the group we're leaving
        if (_activeToolGroup != null && _activeToolGroup != group)
            _activeToolGroup.LastActiveCategoryName = _selectedCategory;

        group.LastActivePresetId = preset.Id;
        _activeToolGroup = group;
        _selectedCategory = ResolveStartupCategory(group, group.LastActiveCategoryName, preset);

        // Record the last active preset per category so switching back remembers the right one
        var activeCat = group.Categories.FirstOrDefault(c => c.Name == _selectedCategory);
        if (activeCat != null && activeCat.PresetIds.Contains(preset.Id))
            activeCat.LastActivePresetId = preset.Id;

        // For brush-based engines, load the referenced brush asset first
        // and overlay the tool-preset scalar values (size, opacity etc.).
        //
        // Snapshot the *current* brush into the engine preset BEFORE we
        // touch _ctx.Brush via SyncBrushFromContext.  If we don't do this
        // first, SetActiveTool will save the *new* brush to the old engine
        // slot, swapping Brush ↔ Eraser presets on every cross-engine switch.
        _canvas.SaveBrushEnginePreset();

        var btn = _toolGroupButtons.FirstOrDefault(x => x.Group == group).Button;
        ActivateTool(ToolForPreset(preset), btn, preset);

        // Now load the brush asset and sync the tool-property panel.
        // This runs after ActivateTool so that SetActiveTool's per-engine
        // save correctly recorded the *previous* brush.
        if (preset.InputProcess.IsBrushFamily() && preset.OutputProcess == OutputProcessType.DirectDraw)
        {
            BrushPreset? assetPreset = null;
            _activeBrushAsset = null;

            if (preset.BrushId != null)
            {
                var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
                if (asset != null)
                {
                    _activeBrushAsset = asset;
                    assetPreset = asset.ToPreset();
                }
            }

            if (assetPreset != null)
            {
                var overridden = preset.ApplyToBrushPreset(assetPreset);
                _activePreset = overridden;
                _canvas.SyncBrushFromContext(overridden);
                _activeBrushLabel.Text = assetPreset.Name;
                _strokePreview.Brush = overridden;
                if (_toolPropsWindow?.CanSyncToolPreset(preset) == true)
                {
                    _toolPropsWindow.SyncFromToolPreset(preset);
                    _toolPropsWindow.SyncFromPreset(overridden);
                }
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
                // No brush asset — start from a clean default, never from
                // the previous tool's live state (_canvas.Brush).
                var basePreset = BrushPreset.Defaults[0];
                var overridden = preset.ApplyToBrushPreset(basePreset);
                _activePreset = overridden;
                _canvas.SyncBrushFromContext(overridden);
                _activeBrushLabel.Text = preset.Name;
                _strokePreview.Brush = overridden;
                if (_toolPropsWindow?.CanSyncToolPreset(preset) == true)
                {
                    _toolPropsWindow.SyncFromToolPreset(preset);
                    _toolPropsWindow.SyncFromPreset(overridden);
                }
            }
        }
        else
        {
            _activeBrushAsset = null;
            _activePreset = null;
            _strokePreview.Brush = null;
            _activeBrushLabel.Text = preset.Name;
        }

        // Reflect active preset engine in the rail button icon
        if (btn != null) btn.Content = MaterialIcon(group.ActiveIcon, 18);

        RefreshGroupPresets();
        App.ToolGroups.Save();
        SaveActiveToolSelection();
        RefreshToolProperties();
    }

    private void CaptureActiveBrushToPreset()
    {
        if (_activeToolGroup == null) return;
        var active = _activeToolGroup.ActivePreset;
        if (active == null || _activePreset == null) return;
        if (!active.InputProcess.IsBrushFamily() || active.OutputProcess != OutputProcessType.DirectDraw) return;
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
        var hasDocument = _canvasFrame.IsVisible;
        var layers = _canvas.Layers;

        UpdateMenuState(hasDocument, layers);

        if (layers.Count == 0 || !hasDocument) return;

        var activeIdx = _canvas.ActiveLayerIndex;
        var layer = layers[activeIdx];
        _canvasStatusText.Text =
            $"{Math.Round(_zoom * 100)}%  {Math.Round(_rotation)}°  " +
            $"layer {activeIdx + 1}/{layers.Count}  " +
            $"{layer.BlendMode}";
        if (_undoButton != null) _undoButton.IsEnabled = _canvas.CanUndo;
        if (_redoButton != null) _redoButton.IsEnabled = _canvas.CanRedo;
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

    private void UpdateMenuState(bool hasDocument, IReadOnlyList<DrawingLayer> layers)
    {
        var activeIdx = hasDocument && layers.Count > 0 ? _canvas.ActiveLayerIndex : -1;
        var canModifyActive = hasDocument && _canvas.Document.CanModifyActiveLayer;
        var canDeleteLayer = hasDocument && _canvas.CanDeleteLayer;

        if (_saveMenuItem != null) _saveMenuItem.IsEnabled = hasDocument;
        if (_saveAsMenuItem != null) _saveAsMenuItem.IsEnabled = hasDocument;
        if (_exportMenu != null) _exportMenu.IsEnabled = hasDocument;
        if (_resetViewMenuItem != null) _resetViewMenuItem.IsEnabled = hasDocument;

        if (_undoMenuItem != null) _undoMenuItem.IsEnabled = hasDocument && _canvas.CanUndo;
        if (_redoMenuItem != null) _redoMenuItem.IsEnabled = hasDocument && _canvas.CanRedo;
        if (_copyMenuItem != null) _copyMenuItem.IsEnabled = hasDocument;
        if (_pasteMenuItem != null) _pasteMenuItem.IsEnabled = hasDocument;
        if (_editDeleteMenuItem != null) _editDeleteMenuItem.IsEnabled = hasDocument;

        if (_mirrorHMenuItem != null) _mirrorHMenuItem.IsEnabled = hasDocument;
        if (_mirrorVMenuItem != null) _mirrorVMenuItem.IsEnabled = hasDocument;
        if (_showCanvasOnlyMenuItem != null) _showCanvasOnlyMenuItem.IsEnabled = hasDocument;
        if (_showRulersMenuItem != null) _showRulersMenuItem.IsEnabled = hasDocument;

        if (_imageMenu != null) _imageMenu.IsEnabled = hasDocument;

        if (_addLayerMenuItem != null) _addLayerMenuItem.IsEnabled = hasDocument;
        if (_duplicateLayerMenuItem != null) _duplicateLayerMenuItem.IsEnabled = hasDocument && canModifyActive;
        if (_deleteLayerMenuItem != null) _deleteLayerMenuItem.IsEnabled = hasDocument && canDeleteLayer;
        if (_mergeDownMenuItem != null) _mergeDownMenuItem.IsEnabled = hasDocument && canModifyActive && activeIdx > 0;
        if (_moveUpMenuItem != null) _moveUpMenuItem.IsEnabled = hasDocument && activeIdx < layers.Count - 1;
        if (_moveDownMenuItem != null) _moveDownMenuItem.IsEnabled = hasDocument && activeIdx > 0;
        if (_addBackgroundMenuItem != null) _addBackgroundMenuItem.IsEnabled = hasDocument;

        if (_filterMenu != null) _filterMenu.IsEnabled = hasDocument && canModifyActive;
    }
}

internal sealed class RulerOverlay : Control
{
    private DrawingCanvas _canvas;
    private const double RulerThickness = 20;

    internal DrawingCanvas Canvas { get => _canvas; set => _canvas = value; }
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
