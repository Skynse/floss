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
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Controls;
using Floss.App.Canvas.Compositing;
using Floss.App.Docking;
using Floss.App.Document;
using Floss.App.FlossFiles;
using Floss.App.ImageFiles;
using Floss.App.Input;
using Floss.App.Kra;
using Floss.App.Psd;
using Floss.App.Processes;
using Floss.App.Processes.Input;
using Floss.App.Timelapse;
using Floss.App.Tools;
using Floss.App.Windows;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow : Window, Tools.IViewportController
{
    private const double ResetViewOutset = 80.0;

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
    private static readonly BlendMode[] BlendModes =
    [
        BlendMode.Normal, BlendMode.PassThrough, BlendMode.Dissolve,
        BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight,
        BlendMode.ColorDodge, BlendMode.EasyDodge, BlendMode.ColorBurn, BlendMode.LinearDodge, BlendMode.LinearBurn,
        BlendMode.Darken, BlendMode.Lighten, BlendMode.DarkerColor, BlendMode.LighterColor,
        BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide,
        BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity,
        BlendMode.VividLight, BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix,
    ];

    private static string BlendAbbr(BlendMode mode) => mode switch
    {
        BlendMode.Normal => "Nrm",
        BlendMode.Dissolve => "Dis",
        BlendMode.Multiply => "Mul",
        BlendMode.Screen => "Scr",
        BlendMode.Overlay => "Ovl",
        BlendMode.SoftLight => "SL",
        BlendMode.HardLight => "HL",
        BlendMode.ColorDodge => "CDg",
        BlendMode.EasyDodge => "EDg",
        BlendMode.ColorBurn => "CBn",
        BlendMode.LinearDodge => "LDg",
        BlendMode.LinearBurn => "LBn",
        BlendMode.Darken => "Drk",
        BlendMode.Lighten => "Lgt",
        BlendMode.DarkerColor => "DC",
        BlendMode.LighterColor => "LC",
        BlendMode.Difference => "Dif",
        BlendMode.Exclusion => "Exc",
        BlendMode.Subtract => "Sub",
        BlendMode.Divide => "Div",
        BlendMode.Hue => "Hue",
        BlendMode.Saturation => "Sat",
        BlendMode.Color => "Col",
        BlendMode.Luminosity => "Lum",
        BlendMode.VividLight => "VL",
        BlendMode.LinearLight => "LL",
        BlendMode.PinLight => "PL",
        BlendMode.HardMix => "HM",
        BlendMode.PassThrough => "PT",
        _ => mode.ToString()[..Math.Min(3, mode.ToString().Length)]
    };

    // ── Controls ──────────────────────────────────────────────────────────────
    private DrawingCanvas _canvas = null!;
    private Grid _workspaceViewport = null!;
    private Border _canvasFrame = null!;
    private Border _selectionActionBar = null!;
    private HsvColorPicker _colorPicker = null!;
    private TextBox _hexInput = null!;
    private ScrubSlider _rgbRSlider = null!;
    private ScrubSlider _rgbGSlider = null!;
    private ScrubSlider _rgbBSlider = null!;
    private Border _colorWell = null!;
    private WrapPanel _swatchPanel = null!;
    private WrapPanel _brushCategoryPanel = null!;
    private WrapPanel _presetPanel = null!;
    private ScrollViewer? _brushPresetScroll;
    private Control? _toolPropertiesContent;
    private StackPanel _toolPropertyPanel = null!;
    private TextBlock _toolPropertyTitle = null!;
    private ScrubSlider _sizeSlider = null!;
    private ScrubSlider _maxSizePercentSlider = null!;
    private ScrubSlider _opacitySlider = null!;
    private ScrubSlider _hardnessSlider = null!;
    private ScrubSlider _spacingSlider = null!;
    private ScrubSlider _smoothingSlider = null!;
    private ScrubSlider _grainSlider = null!;
    private ScrubSlider _flowSlider = null!;
    private ScrubSlider _layerOpacitySlider = null!;
    private bool _layerOpacityScrubActive;
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
    private Button _maskLayerBtn = null!;
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
    private readonly Dictionary<string, Control> _dockerPanelBodies = new();
    private readonly Dictionary<string, Window> _floatingDockers = new();
    private MenuItem? _workspaceLoadMenu;
    private MenuItem? _saveMenuItem;
    private MenuItem? _saveAsMenuItem;
    private MenuItem? _exportMenu;
    private MenuItem? _recordTimelapseMenuItem;
    private MenuItem? _exportTimelapseMenuItem;
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
    private MenuItem? _moveUpMenuItem;
    private MenuItem? _moveDownMenuItem;
    private MenuItem? _addBackgroundMenuItem;
    private MenuItem? _filterMenu;
    private Button? _toolbarNewBtn;
    private Button? _toolbarOpenBtn;
    private Button? _toolbarSaveBtn;
    private Button? _toolbarUndoBtn;
    private Button? _toolbarRedoBtn;
    private bool _suppressFloatingDockerClosed;
    private bool _suppressFloatingDockerSnap;
    private Grid? _dockerHostGrid;
    private Grid? _leftDockerHostGrid;
    private Popup? _dockerPopup;
    private string? _popupContentId;
    private Control? _cachedToolContent;
    private string? _currentFilePath; // Replaces _currentFlossPath
    private int _layerDragSourceIndex = -1;
    private int _pendingDragIndex = -1;
    private Point _pendingDragStartPos;
    private PointerPressedEventArgs? _pendingDragArgs;
    private int _renamingLayerIndex = -1;
    private TextBox? _activeLayerNameEdit;
    private Action<bool>? _finishLayerRename;

    // ── Tool factory (new process-based architecture) ─────────────────────────
    private ToolFactory? _toolFactory;
    private readonly List<Button> _toolButtons = [];
    private readonly List<(ToolGroup Group, Button Button)> _toolGroupButtons = [];
    private ToolGroup? _activeToolGroup;
    private ToolPreset? _savedPresetTemp;
    private bool _temporaryPresetActive;
    private readonly Dictionary<string, ITool> _presetToolCache = new(StringComparer.Ordinal);
    private ToolGroup? _recordingToolGroup;
    private Button? _recordingToolGroupButton;

    private WrapPanel _toolRailStack = null!;
    private ScrollViewer? _toolRailScroll;
    private SettingsWindow? _settingsWindow;
    private Floss.App.Windows.ModifierKeySettingsWindow? _modifierKeySettingsWindow;
    private Floss.App.Windows.PenPressureSettingsWindow? _penPressureSettingsWindow;

    // ── Avalonia KeyBinding shortcut registration ──────────────────────────
    private sealed class DelegateCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public DelegateCommand(Action execute, Func<bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();
    }

    private void AddShortcut(Input.KeyBinding shortcut, Action action, Func<bool>? canExecute = null)
    {
        if (shortcut.IsEmpty || shortcut.Key == Key.None) return;
        KeyBindings.Add(new Avalonia.Input.KeyBinding
        {
            Gesture = new KeyGesture(shortcut.Key, shortcut.Modifiers),
            Command = new DelegateCommand(action, canExecute)
        });
    }

    private bool CanExecuteCanvasShortcut()
    {
        var focused = FocusManager.GetFocusedElement() as IInputElement;
        return _keyboardInputScope.ShouldRouteToCanvas(focused);
    }

    private bool CanExecuteCanvasDocumentShortcut()
        => _canvas.HasDocument && CanExecuteCanvasShortcut();

    /// <summary>
    /// Viewport/view shortcuts that should work from side panels, not only when
    /// the pointer is over the canvas workspace.
    /// </summary>
    private bool CanExecuteDocumentViewShortcut()
    {
        if (!_canvas.HasDocument) return false;

        var focused = FocusManager.GetFocusedElement() as IInputElement;
        if (KeyboardInputScope.IsTextEntryFocused(focused)) return false;
        if (KeyboardInputScope.IsPopupNodeGraphFocused(focused)) return false;
        return !_keyboardInputScope.ShouldRouteToNodeGraph(focused);
    }

    /// <summary>
    /// Selection shortcuts should work from side panels and the canvas workspace,
    /// not only when keyboard focus is on the canvas.
    /// </summary>
    private bool CanExecuteSelectionShortcut()
        => _canvas.HasDocument && CanExecuteDocumentViewShortcut();

    private void RegisterShortcuts()
    {
        var s = App.Shortcuts;

        // File
        AddShortcut(s.FileNew, () => _ = NewDocumentAsync());
        AddShortcut(s.FileOpen, () => _ = OpenDocumentAsync());
        AddShortcut(s.FileSave, () => _ = SaveDocumentAsync());
        AddShortcut(s.FileSaveAs, () => _ = SaveDocumentAsAsync());

        // Edit — suppressed while node graph editor has keyboard focus
        AddShortcut(s.Undo, () => _canvas.Undo(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.Redo, () => _canvas.Redo(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.RedoAlt, () => _canvas.Redo(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.Copy, () => _canvas.CopyToClipboard(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.Paste, () => _ = _canvas.PasteFromOSClipboardAsync(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.DeleteSelection, DeleteSelectionAction, CanExecuteCanvasDocumentShortcut);

        // View - flip
        AddShortcut(s.FlipHorizontal, () => _canvas.FlipCanvas(horizontal: true), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FlipVertical, () => _canvas.FlipCanvas(horizontal: false), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.MirrorHorizontal, MirrorHorizontalAction, CanExecuteDocumentViewShortcut);
        AddShortcut(s.MirrorVertical, MirrorVerticalAction, CanExecuteDocumentViewShortcut);

        // View - zoom
        AddShortcut(s.ZoomIn, () => SetZoom(_zoom * s.ZoomKeyFactor, null), CanExecuteCanvasShortcut);
        AddShortcut(s.ZoomInAlt, () => SetZoom(_zoom * s.ZoomKeyFactor, null), CanExecuteCanvasShortcut);
        AddShortcut(s.ZoomOut, () => SetZoom(_zoom / s.ZoomKeyFactor, null), CanExecuteCanvasShortcut);
        AddShortcut(s.ZoomReset, () => ResetView(), CanExecuteCanvasShortcut);
        AddShortcut(s.ZoomFit, () => SyncCanvasFrameToDocument(fitToViewport: true), CanExecuteCanvasShortcut);

        // View - rotate
        AddShortcut(s.RotateLeft, () => SetRotation(_rotation - s.RotateKeyStep), CanExecuteCanvasShortcut);
        AddShortcut(s.RotateRight, () => SetRotation(_rotation + s.RotateKeyStep), CanExecuteCanvasShortcut);
        AddShortcut(s.RotateReset, () => SetRotation(0), CanExecuteCanvasShortcut);

        // Image - rotate canvas
        AddShortcut(s.RotateCanvas90Cw, RotateCanvas90CwAction, CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.RotateCanvas90Ccw, RotateCanvas90CcwAction, CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.RotateCanvas180, () => _canvas.RotateCanvas180(), CanExecuteCanvasDocumentShortcut);

        // Selection
        AddShortcut(s.SelectAll, () => _canvas.SelectAll(), CanExecuteSelectionShortcut);
        AddShortcut(s.Deselect, () =>
        {
            if (_canvas.CommitSmartShapeIfLauncherShowing())
                return;
            _canvas.Deselect();
        }, CanExecuteSelectionShortcut);
        AddShortcut(s.InvertSelect, () => _canvas.InvertSelection(), CanExecuteSelectionShortcut);
        AddShortcut(s.Transform, TransformAction, CanExecuteCanvasDocumentShortcut);

        // Brush - size
        AddShortcut(s.BrushSizeDecrease, () => NudgeBrushSize(-1, false), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.BrushSizeIncrease, () => NudgeBrushSize(1, false), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.BrushSizeDecreaseLarge, () => NudgeBrushSize(-1, true), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.BrushSizeIncreaseLarge, () => NudgeBrushSize(1, true), CanExecuteCanvasDocumentShortcut);

        // Brush - opacity
        AddShortcut(s.BrushOpacityDecrease, () => { _opacitySlider.Value = Math.Max(_opacitySlider.Minimum, _opacitySlider.Value - s.BrushOpacityStep); }, CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.BrushOpacityIncrease, () => { _opacitySlider.Value = Math.Min(_opacitySlider.Maximum, _opacitySlider.Value + s.BrushOpacityStep); }, CanExecuteCanvasDocumentShortcut);

        // Color
        AddShortcut(s.ColorCycle, () => CycleColor());
        AddShortcut(s.ColorDefault, () => SetColor(Color.Parse("#111111")));

        // Layers
        AddShortcut(s.LayerNew, () => _canvas.AddLayer(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.LayerDuplicate, () => _canvas.DuplicateLayer(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.LayerDelete, DeleteSelectedLayers, CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.LayerMoveUp, () => _canvas.MoveActiveLayer(1), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.LayerMoveDown, () => _canvas.MoveActiveLayer(-1), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.LayerGroup, LayerGroupAction, CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.LayerToggleColor, () => ToggleActiveLayerColor(), CanExecuteCanvasDocumentShortcut);

        // Filters
        AddShortcut(s.FilterBlur, () => _ = ApplyBlurFilter(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FilterSharpen, () => _ = ApplySharpenFilter(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FilterNoise, () => _ = ApplyNoiseFilter(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FilterColorCurves, () => _ = ApplyColorCurvesFilter(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FilterChromaticAberration, () => _ = ApplyChromaticAberrationFilter(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FilterBaseColorMask, () => _ = RunBaseColorMaskGenerator(), CanExecuteCanvasDocumentShortcut);
        AddShortcut(s.FilterRemoveDust, () => _ = ApplyRemoveDustFilter(), CanExecuteCanvasDocumentShortcut);

        // Misc
        AddShortcut(s.OpenSettings, () => OpenSettings());
        AddShortcut(s.OpenBrushEditor, () => OpenBrushTipGraphEditor());
        AddShortcut(s.ToggleCanvasOnly, () => ToggleCanvasOnly());
        AddShortcut(s.ToggleRulers, () => ToggleRulers());

        // Special keys (not in ShortcutsConfig but handled same way)
        AddShortcut(new Input.KeyBinding(Key.Escape), () =>
        {
            if (_canvas.CommitSmartShapeIfLauncherShowing())
            {
                ResetTransientInputState();
                return;
            }
            if (_canvas.ActiveTool.HasPendingOperation)
                _canvas.CancelActiveTool();
            else if (_canvas.HasSelection)
                _canvas.Deselect();
            else
                _canvas.CancelActiveTool();
            ResetTransientInputState();
        }, CanExecuteSelectionShortcut);
        AddShortcut(new Input.KeyBinding(Key.Back), DeleteSelectionAction, CanExecuteCanvasDocumentShortcut);
        AddShortcut(new Input.KeyBinding(Key.Return), () => _canvas.CommitActiveTool(),
            () => CanExecuteCanvasDocumentShortcut() &&
                  _canvas.ActiveTool is TransformTool or CompositeTool { CanCommitFromClick: true });
        AddShortcut(new Input.KeyBinding(Key.Enter), () => _canvas.CommitActiveTool(),
            () => CanExecuteCanvasDocumentShortcut() &&
                  _canvas.ActiveTool is TransformTool or CompositeTool { CanCommitFromClick: true });
    }

    internal void ReloadShortcuts()
    {
        KeyBindings.Clear();
        RegisterShortcuts();
    }

    private void DeleteSelectionAction()
    {
        if (_canvas.ActiveTool is TransformTool) _canvas.DeleteSelectionTransform();
        else _canvas.ClearSelectionContent();
    }

    private void TransformAction()
    {
        if (_canvas.IsTransformActive) { _canvas.CommitActiveTool(); }
        else
        {
            PopTemporaryPreset();
            _canvas.BeginSelectionTransform(
                _selectedLayerIndices.Count > 3 ? _selectedLayerIndices.ToList() : null);
        }
        RefreshToolProperties();
    }

    private void LayerGroupAction()
    {
        if (_selectedLayerIndices.Count >= 1)
        {
            var sorted = _selectedLayerIndices.OrderBy(x => x).ToList();
            _canvas.GroupSelectedLayers(sorted);
            _selectedLayerIndices.Clear();
            _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);
            BuildLayerList();
        }
        else
        {
            _canvas.AddGroupLayer();
        }
    }

    private void MirrorHorizontalAction()
    {
        _canvasFlip.ScaleX = -_canvasFlip.ScaleX;
        SyncViewportStateToCanvas();
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual();
        ClampCanvasPan();
        UpdateStatus();
    }

    private void MirrorVerticalAction()
    {
        _canvasFlip.ScaleY = -_canvasFlip.ScaleY;
        SyncViewportStateToCanvas();
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual();
        ClampCanvasPan();
        UpdateStatus();
    }

    private void RotateCanvas90CwAction()
    {
        _canvas.RotateCanvas90Clockwise();
        SyncCanvasFrameToDocument(false);
        ClampCanvasPan();
        _rulerOverlay?.InvalidateVisual();
    }

    private void RotateCanvas90CcwAction()
    {
        _canvas.RotateCanvas90CounterClockwise();
        SyncCanvasFrameToDocument(false);
        ClampCanvasPan();
        _rulerOverlay?.InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_recordingToolGroup != null)
        {
            HandleShortcutRecording(e);
            return;
        }

        base.OnKeyDown(e);
        if (e.Handled) return;
        if (!CanExecuteCanvasShortcut()) return;
        if (_inputRouter.IsTransactionActive) return;

        var key = e.Key;
        var mods = Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);

        // Tool group shortcuts (checked after regular shortcuts via KeyBinding above)
        var matching = _toolGroupButtons
            .Where(p => !p.Group.Shortcut.IsEmpty && p.Group.Shortcut.Matches(key, mods))
            .ToList();
        if (matching.Count > 0)
        {
            var activeIdx = matching.FindIndex(p => p.Group == _activeToolGroup);
            if (!(matching.Count == 1 && activeIdx >= 0))
            {
                var next = matching[(activeIdx + 1) % matching.Count];
                var preset = next.Group.ActivePreset ?? next.Group.Presets.FirstOrDefault();
                if (preset != null) ActivatePreset(next.Group, preset);
            }
            e.Handled = true;
        }
    }

    private static readonly Cursor CursorArrow = new(StandardCursorType.Arrow);
    private static readonly Cursor CursorNone = new(StandardCursorType.None);


    private ScaleTransform _canvasFlip = null!;
    private ScaleTransform _canvasScale = null!;
    private RotateTransform _canvasRotate = null!;
    private TranslateTransform _canvasPan = null!;

    private bool _syncingLayerUi;
    private bool _syncingBrushUi;
    private Control? _rightPanel;
    private Control? _leftPanel;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;

    // Root grid columns — see notes/wide-dockable-layout.md
    private const int RootColLeftDock = 0;
    private const int RootColLeftSplitter = 1;
    private const int RootColCenter = 2;
    private const int RootColRightSplitter = 3;
    private const int RootColRightDock = 4;
    private Control? _shellMenu;
    private MenuItem? _openRecentMenu;
    private string[] _recentMenuSnapshot = [];
    private Control? _statusBar;
    private Control? _footer;
    private Control? _rulerOverlay;
    private CheckerboardOverlay? _checkerboardOverlay;
    private SelectionOutlineOverlay? _selectionOutlineOverlay;
    private Grid _canvasHost = null!;
    private bool _canvasOnly;
    private bool _showRulers;

    // Layout grids for canvas-only mode collapse
    private Grid? _rootGrid;
    private GridLength[]? _rootColumnWidths;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.None];
        Background = new SolidColorBrush(Color.Parse(Bg0));
        Icon = new WindowIcon(AssetLoader.Open(new Uri(AppAssets.IconUri)));
        _brushLibrary = new BrushLibrary(AppPaths.BrushesDirectory);
        _showRulers = App.Config.ShowRulers;

        RegisterDockerPanels();
        App.Config.WorkspaceLayout.Normalize(PanelRegistry.AllIds);
        AppConfig.ToolPropertyVisibilityChanged += OnToolPropertyVisibilityChanged;
        BuildUi();
        InitInputRouter();
        EnsurePopupContent();  // pre-build brush/tool controls so sliders exist
        WireControls();
        RestoreFromConfig();
        //BuildSwatches();
        LoadBrushAssets();
        SelectInitialTool();
        SetColor(Color.Parse(App.Config.LastColor));
        _canvasFrame.IsVisible = false;
        SetDocumentPanelsVisible(false);
        UpdateTimelapseMenuState();
        BuildLayerList();
        UpdateStatus();
        Closing += MainWindow_Closing;
        Deactivated += (_, _) => ResetTransientInputState();
        LostFocus += (_, _) => ResetTransientInputState();
        Loaded += (_, _) =>
        {
            UpdateTabBar();
            SyncCanvasViewport();
            CaptureRootColumnWidths();
            SyncBottomDockVisibility();

            if (App.Config.LayoutWasReset)
            {
                App.Config.LayoutWasReset = false;
                ShowLayoutResetNotification();
            }
        };
    }

    // ── Root layout ───────────────────────────────────────────────────────────
    private void BuildUi()
    {
        _canvas = new DrawingCanvas();
        _toolFactory = new ToolFactory(_canvas.Document, _canvas.BrushEngine);
        InvalidatePresetToolCache();

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
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        _canvasHost = new Grid();
        _canvasHost.Children.Add(_canvas);
        _selectionOutlineOverlay = new SelectionOutlineOverlay(_canvas);
        _canvasHost.Children.Add(_selectionOutlineOverlay);
        _canvasFrame.Child = _canvasHost;

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

        WireViewportCursor();
        BuildSmartShapeLauncher();

        _canvasStatusText = MiniText();
        _footerStatusText = MiniText();

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 16,
            Padding = new Thickness(6, 0),
            Child = _canvasStatusText,
            IsHitTestVisible = false
        };

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 18,
            Padding = new Thickness(6, 0),
            Child = BuildFooterPanel(),
            IsHitTestVisible = false
        };

        BuildTabBar();
        var centerArea = new Grid { RowDefinitions = new RowDefinitions("24,16,*,18") };
        Grid.SetRow(_tabBarContainer, 0);
        Grid.SetRow(statusBar, 1);
        Grid.SetRow(_workspaceViewport, 2);
        Grid.SetRow(footer, 3);
        centerArea.Children.Add(_tabBarContainer);
        centerArea.Children.Add(statusBar);
        centerArea.Children.Add(_workspaceViewport);
        centerArea.Children.Add(footer);
        AttachBusyOverlay();
        AttachBottomDockToCenter(centerArea);

        _dockerRows.Clear();
        _dockerSections.Clear();
        _dockerPanelBodies.Clear();
        var leftPanel = BuildLeftDockColumn();
        var rightPanel = BuildRightPanel();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(0, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 260 });
        root.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(320, GridUnitType.Pixel) { MinWidth = 240, MaxWidth = 600 });
        _rootGrid = root;
        _rootColumnWidths = [.. root.ColumnDefinitions.Select(c => c.Width)];

        var leftSplitter = new GridSplitter
        {
            Width = 3,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            IsVisible = false
        };
        leftSplitter.DragCompleted += (_, _) =>
        {
            CaptureRootColumnWidths();
            PersistWorkspaceLayout();
        };
        _leftSplitter = leftSplitter;

        var rightSplitter = new GridSplitter
        {
            Width = 3,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            IsVisible = false
        };
        rightSplitter.DragCompleted += (_, _) =>
        {
            CaptureRootColumnWidths();
            PersistWorkspaceLayout();
        };
        _rightSplitter = rightSplitter;

        // Popup that floats over the canvas, anchored to the right panel's left edge
        _dockerPopup = new Popup
        {
            IsLightDismissEnabled = false,
            PlacementTarget = rightPanel,
            Placement = PlacementMode.Left,
            HorizontalOffset = 0,
            VerticalOffset = 0
        };
        _dockerPopup.Closed += (_, _) => _popupContentId = null;
        _dockerPopup.Opened += (_, _) =>
        {
            if (_dockerPopup?.PlacementTarget is Control target && _dockerPopup.Child is Control child)
            {
                _dockerPopup.VerticalOffset = -(target.Bounds.Height - child.Bounds.Height) / 2;
            }
        };

        Grid.SetColumn(leftPanel, RootColLeftDock);
        Grid.SetColumn(leftSplitter, RootColLeftSplitter);
        Grid.SetColumn(centerArea, RootColCenter);
        Grid.SetColumn(rightSplitter, RootColRightSplitter);
        Grid.SetColumn(rightPanel, RootColRightDock);
        root.Children.Add(leftPanel);
        root.Children.Add(leftSplitter);
        root.Children.Add(centerArea);
        root.Children.Add(rightSplitter);
        root.Children.Add(rightPanel);
        // Popup must be in the visual tree to function; column 99 avoids conflicts
        Grid.SetColumn(_dockerPopup, 99);
        root.Children.Add(_dockerPopup);

        var shell = new Grid { RowDefinitions = new RowDefinitions("24,*") };
        var menu = BuildMenuBar();
        Grid.SetRow(menu, 0);
        Grid.SetRow(root, 1);
        shell.Children.Add(menu);
        shell.Children.Add(root);

        _shellMenu = menu;
        _leftPanel = leftPanel;
        _rightPanel = rightPanel;
        _statusBar = statusBar;
        _footer = footer;
        UpdateLeftColumnWidth();
        UpdateRightPanelWidth();

        Content = shell;
        EnsureDockDropOverlay();
        WireFileDragDrop();
        AddHandler(PointerPressedEvent, WindowPointerPressed, RoutingStrategies.Tunnel);
    }

    private void SetDocumentPanelsVisible(bool enabled)
    {
        if (_rightPanel != null) _rightPanel.IsEnabled = enabled;
        if (_statusBar != null) _statusBar.IsEnabled = enabled;
    }

    private static TextBlock MiniText() => new()
    {
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        FontSize = 10,
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
            _canvas.BeginSelectionTransform(_selectedLayerIndices.Count > 3 ? _selectedLayerIndices.ToList() : null);
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
            Background = new SolidColorBrush(Color.Parse(Bg3)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
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
            Width = 28,
            Height = 24,
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
        _recordTimelapseMenuItem = MenuAction("Record Timelapse", ToggleTimelapseRecording);
        _exportTimelapseMenuItem = MenuAction("Export Timelapse...", async () => await ExportTimelapseAsync());
        var timelapseMenu = new MenuItem
        {
            Header = "_Timelapse",
            ItemsSource = new object[]
            {
                _recordTimelapseMenuItem,
                _exportTimelapseMenuItem
            }
        };
        _resetViewMenuItem = MenuAction("_Reset View", ResetView);

        _openRecentMenu = new MenuItem { Header = "Open _Recent" };
        _openRecentMenu.SubmenuOpened += (_, _) => RefreshRecentFilesMenu(force: true);
        RefreshRecentFilesMenu(force: true);

        var fileMenu = new MenuItem
        {
            Header = "_File",
            ItemsSource = new object[]
            {
                MenuAction("_New...", new KeyGesture(Key.N, KeyModifiers.Control), async () => await NewDocumentAsync()),
                MenuAction("_Open...", new KeyGesture(Key.O, KeyModifiers.Control), async () => await OpenDocumentAsync()),
                _openRecentMenu,
                new Separator(),
                _saveMenuItem,
                _saveAsMenuItem,
                new Separator(),
                _exportMenu,
                timelapseMenu,
                new Separator(),
                _resetViewMenuItem,
                new Separator(),
                MenuAction("_Pen Pressure Settings...", OpenPenPressureSettings),
                new Separator(),
                MenuAction("_Settings...", OpenSettings),
                MenuAction("_Modifier Key Settings...", OpenModifierKeySettings)
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
                _editDeleteMenuItem,
                new Separator(),
                MenuAction("Canvas _Information...", async () => await ShowCanvasInformationDialogAsync())
            }
        };

        _mirrorHMenuItem = MenuAction("_Mirror Horizontal", () =>
                {
                    _canvasFlip.ScaleX = -_canvasFlip.ScaleX;
                    SyncViewportStateToCanvas();
                    _rulerOverlay?.InvalidateVisual();
                    ClampCanvasPan(); UpdateStatus();
                });
        _mirrorVMenuItem = MenuAction("Mirror _Vertical", () =>
                {
                    _canvasFlip.ScaleY = -_canvasFlip.ScaleY;
                    SyncViewportStateToCanvas();
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
                MenuAction("Canvas Background _Color...", ShowPaperColorPicker),
            }
        };

        _addLayerMenuItem = MenuAction("_Add Layer", new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift), () => _canvas.AddLayer());
        _duplicateLayerMenuItem = MenuAction("_Duplicate Layer", new KeyGesture(Key.J, KeyModifiers.Control), () => _canvas.DuplicateLayer());
        _deleteLayerMenuItem = MenuAction("_Delete Layer", new KeyGesture(Key.Delete, KeyModifiers.Control), DeleteSelectedLayers);
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
                MenuAction("_Brush Tip Graph...", new KeyGesture(Key.B, KeyModifiers.Control | KeyModifiers.Shift), OpenBrushTipGraphEditor),
                new Separator(),
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
                new MenuItem
                {
                    Header = "_Adjust",
                    ItemsSource = new object[]
                    {
                        MenuAction("_Brightness / Contrast...", async () => await ApplyBrightnessContrastFilter()),
                        MenuAction("_Exposure / Gamma...", async () => await ApplyExposureGammaFilter()),
                        MenuAction("_Levels...", async () => await ApplyLevelsFilter()),
                        MenuAction("_Hue / Saturation...", async () => await ApplyHueSaturationFilter()),
                        MenuAction("_Color Curves...", async () => await ApplyColorCurvesFilter()),
                    }
                },
                new MenuItem
                {
                    Header = "_Color",
                    ItemsSource = new object[]
                    {
                        MenuAction("_Invert", ApplyInvertFilter),
                        MenuAction("_Desaturate", ApplyDesaturateFilter),
                        MenuAction("_Sepia...", async () => await ApplySepiaFilter()),
                        MenuAction("_Threshold...", async () => await ApplyThresholdFilter()),
                        MenuAction("_Posterize...", async () => await ApplyPosterizeFilter()),
                    }
                },
                new MenuItem
                {
                    Header = "_Blur / Enhance",
                    ItemsSource = new object[]
                    {
                        MenuAction("_Gaussian Blur...", async () => await ApplyBlurFilter()),
                        MenuAction("_Motion Blur...", async () => await ApplyMotionBlurFilter()),
                        new Separator(),
                        MenuAction("_Sharpen...", async () => await ApplySharpenFilter()),
                        MenuAction("_Bloom...", async () => await ApplyBloomFilter()),
                    }
                },
                new MenuItem
                {
                    Header = "_Stylize",
                    ItemsSource = new object[]
                    {
                        MenuAction("_Pixelate...", async () => await ApplyPixelateFilter()),
                        MenuAction("_Vignette...", async () => await ApplyVignetteFilter()),
                        MenuAction("_Emboss...", async () => await ApplyEmbossFilter()),
                        MenuAction("_Find Edges...", async () => await ApplyEdgeDetectFilter()),
                        MenuAction("Chromatic _Aberration...", async () => await ApplyChromaticAberrationFilter()),
                        MenuAction("_Noise...", async () => await ApplyNoiseFilter()),
                    }
                },
                new MenuItem
                {
                    Header = "_Cleanup",
                    ItemsSource = new object[]
                    {
                        MenuAction("Remove _Dust...", async () => await ApplyRemoveDustFilter()),
                    }
                },
                new Separator(),
                MenuAction("_Base Color Masks from Sketch...", async () => await RunBaseColorMaskGenerator()),
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
            Background = new SolidColorBrush(Color.Parse(Bg0)),
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
            Width = 38,
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        _rotDisplay = new TextBlock
        {
            Text = "0°",
            Width = 30,
            FontSize = 9,
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

        var newTb = TbarBtn(Icons.Plus, "New document  (Ctrl+N)");
        var openTb = TbarBtn(Icons.Import, "Open document  (Ctrl+O)");
        var saveTb = TbarBtn(Icons.ContentSaveOutline, "Save document  (Ctrl+S)");
        newTb.Click += async (_, _) => await NewDocumentAsync();
        openTb.Click += async (_, _) => await OpenDocumentAsync();
        saveTb.Click += async (_, _) => await SaveDocumentAsync();
        _toolbarNewBtn = newTb;
        _toolbarOpenBtn = openTb;
        _toolbarSaveBtn = saveTb;

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 1,
            Margin = new Thickness(6, 0)
        };
        row.Children.Add(newTb);
        row.Children.Add(openTb);
        row.Children.Add(saveTb);
        row.Children.Add(TbarSep());
        row.Children.Add(undoTb);
        row.Children.Add(redoTb);
        _toolbarUndoBtn = undoTb;
        _toolbarRedoBtn = redoTb;
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
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private static Button TbarBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = MaterialIcon(icon, 14),
            Width = 22,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            CornerRadius = new CornerRadius(2)
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
        Height = 16,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(5, 0)
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

    // ── Left dock (one or more columns beside the canvas) ─────────────────────
    private Border BuildLeftDockColumn()
    {
        var layout = App.Config.WorkspaceLayout;
        var columns = layout.LeftColumns
            .Select((column, index) => (Column: column, Index: index))
            .Where(entry => HasVisibleDockerRows(entry.Column))
            .ToList();

        var grid = BuildMultiColumnDockHost(
            columns.Select(c => (c.Column, DockColumnIndices.Left(c.Index))).ToList(),
            BuildDockColumn,
            PersistWorkspaceLayout);
        _leftDockerHostGrid = grid;

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            ClipToBounds = true,
            Child = grid
        };
    }

    // ── Right panel ───────────────────────────────────────────────────────────
    private Border BuildRightPanel()
    {
        var layout = App.Config.WorkspaceLayout;
        var columns = layout.RightColumns
            .Select((column, index) => (Column: column, Index: index))
            .Where(entry => HasVisibleDockerRows(entry.Column))
            .ToList();

        var grid = BuildMultiColumnDockHost(
            columns.Select(c => (c.Column, DockColumnIndices.Right(c.Index))).ToList(),
            BuildDockColumn,
            PersistWorkspaceLayout);
        _dockerHostGrid = grid;

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            Child = grid
        };
    }

    private static int DockColumnIndexFromLayoutId(string columnId)
    {
        if (columnId == "bottom")
            return DockColumnIndices.Bottom(0);
        if (columnId.StartsWith("bottom-", StringComparison.Ordinal)
            && int.TryParse(columnId.AsSpan(7), out var bi))
            return DockColumnIndices.Bottom(bi);
        if (columnId.StartsWith("left-", StringComparison.Ordinal)
            && int.TryParse(columnId.AsSpan(5), out var li))
            return DockColumnIndices.Left(li);
        if (columnId == "left")
            return DockColumnIndices.Left(0);
        if (columnId.StartsWith("right-", StringComparison.Ordinal)
            && int.TryParse(columnId.AsSpan(6), out var ri))
            return ri;
        return 0;
    }

    private bool HasVisibleDockerRows(DockColumnLayout column)
        => column.ResolvedRows()
            .Any(row => row.PanelIds.Any(id => GetDockerInfo(id) != null && !IsDockerFloating(id) && IsDockerVisible(id)));

    private Border WrapWithPopupStrip(Border dockContent)
    {
        var strip = BuildPopupTriggerStrip();
        var wrapper = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ClipToBounds = true
        };
        Grid.SetColumn(strip, 0);
        Grid.SetColumn(dockContent, 1);
        wrapper.Children.Add(strip);
        wrapper.Children.Add(dockContent);
        return new Border { Child = wrapper, ClipToBounds = true };
    }

    private Grid BuildDockColumn(DockColumnLayout column, int columnIndex)
    {
        var grid = new Grid { ClipToBounds = true };

        // Use ResolvedRows to get effective row layout (solo panels + tab groups)
        var resolvedRows = column.ResolvedRows();
        var visibleRows = resolvedRows
            .Where(row => row.PanelIds.Any(id => GetDockerInfo(id) != null && !IsDockerFloating(id) && IsDockerVisible(id)))
            .ToList();

        if (visibleRows.Count == 0)
            return grid;

        // Auto rows measure to content; fill rows split the remaining column space.
        var rawProps = new double[visibleRows.Count];
        var rawMins = new double[visibleRows.Count];
        double totalProportion = 0;
        for (var i = 0; i < visibleRows.Count; i++)
        {
            var row = visibleRows[i];
            var pids = row.PanelIds;
            var isTabGroup = row.Orientation == DockOrientation.Vertical;
            var isHorizontal = row.Orientation == DockOrientation.Horizontal;
            var primaryId = pids[0];
            var sizing = ResolveDockerRowSizing(pids, isTabGroup, isHorizontal);
            var proportion = App.Config.WorkspaceLayout.PanelProportions.TryGetValue(
                    isTabGroup ? "tab:" + string.Join("|", pids) : primaryId, out var saved)
                ? Math.Max(0.05, saved)
                : (PanelRegistry.Get(primaryId)?.Proportion ?? 0.2);
            rawProps[i] = proportion;
            rawMins[i] = pids.Select(id => PanelRegistry.Get(id)?.MinHeight ?? 64).Min();
            if (sizing == DockPanelSizing.Fill)
                totalProportion += proportion;
        }
        totalProportion = Math.Max(totalProportion, 0.01);

        for (var i = 0; i < visibleRows.Count; i++)
        {
            var resolvedRow = visibleRows[i];
            var rowPanelIds = resolvedRow.PanelIds;
            var isTabGroup = resolvedRow.Orientation == DockOrientation.Vertical;
            var isHorizontal = resolvedRow.Orientation == DockOrientation.Horizontal;
            var primaryId = rowPanelIds[0];
            var sizing = ResolveDockerRowSizing(rowPanelIds, isTabGroup, isHorizontal);
            var height = sizing == DockPanelSizing.Auto
                ? GridLength.Auto
                : new GridLength(rawProps[i] / totalProportion, GridUnitType.Star);
            var rowDef = new RowDefinition(height) { MinHeight = rawMins[i] };
            _dockerRows[primaryId] = rowDef;
            grid.RowDefinitions.Add(rowDef);

            Control rowContent;
            if (isHorizontal)
            {
                // Side-by-side horizontal layout within this row
                var horizontalGrid = new Grid { ClipToBounds = true };
                var visibleInRow = rowPanelIds
                    .Where(id => GetDockerInfo(id) != null && !IsDockerFloating(id) && IsDockerVisible(id))
                    .ToList();
                var count = visibleInRow.Count;
                var colProps = new double[count];
                var colSum = 0.0;
                for (var ci = 0; ci < count; ci++)
                {
                    var pid = visibleInRow[ci];
                    var prop = App.Config.WorkspaceLayout.PanelProportions.TryGetValue(pid, out var saved)
                        ? Math.Max(0.05, saved)
                        : (PanelRegistry.Get(pid)?.Proportion ?? 1.0 / count);
                    colProps[ci] = prop;
                    colSum += prop;
                }
                colSum = Math.Max(colSum, 0.01);
                for (var ci = 0; ci < count; ci++)
                {
                    horizontalGrid.ColumnDefinitions.Add(
                        new ColumnDefinition(colProps[ci] / colSum, GridUnitType.Star));
                    var section = GetOrCreatePanelSection(visibleInRow[ci]);
                    _dockerSections[visibleInRow[ci]] = section;
                    Grid.SetColumn(section, ci);
                    horizontalGrid.Children.Add(section);

                    if (ci < count - 1)
                    {
                        horizontalGrid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
                        var hSplitter = new GridSplitter
                        {
                            Width = 3,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                            Background = new SolidColorBrush(Color.Parse(Bg0))
                        };
                        hSplitter.DragCompleted += (_, _) => PersistWorkspaceLayout();
                        Grid.SetColumn(hSplitter, ci * 2 + 1);
                        horizontalGrid.Children.Add(hSplitter);
                    }
                }
                rowContent = horizontalGrid;
            }
            else if (isTabGroup)
            {
                var tabGroup = BuildTabGroupRow(rowPanelIds, column);
                rowContent = tabGroup;
            }
            else
            {
                var section = GetOrCreatePanelSection(primaryId);
                _dockerSections[primaryId] = section;
                rowContent = section;
            }

            Grid.SetRow(rowContent, grid.RowDefinitions.Count - 1);
            grid.Children.Add(rowContent);

            if (i == visibleRows.Count - 1) continue;
            var nextRow = visibleRows[i + 1];
            var nextPanelIds = nextRow.PanelIds;
            var nextIsTabGroup = nextRow.Orientation == DockOrientation.Vertical;
            var nextIsHorizontal = nextRow.Orientation == DockOrientation.Horizontal;
            var nextSizing = ResolveDockerRowSizing(nextPanelIds, nextIsTabGroup, nextIsHorizontal);

            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Pixel)));
            if (sizing == DockPanelSizing.Auto || nextSizing == DockPanelSizing.Auto)
            {
                var separator = new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.Parse(Stroke))
                };
                Grid.SetRow(separator, grid.RowDefinitions.Count - 1);
                grid.Children.Add(separator);
                continue;
            }

            var splitter = new GridSplitter
            {
                Height = 1,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                Background = new SolidColorBrush(Color.Parse(Stroke))
            };
            splitter.DragCompleted += (_, _) => PersistWorkspaceLayout();
            Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
            grid.Children.Add(splitter);
        }

        return grid;
    }

    private static DockPanelSizing ResolveDockerRowSizing(
        IReadOnlyList<string> panelIds,
        bool isTabGroup,
        bool isHorizontal)
    {
        if (isTabGroup || isHorizontal)
            return panelIds.Any(id => PanelRegistry.Get(id)?.Sizing == DockPanelSizing.Fill)
                ? DockPanelSizing.Fill
                : DockPanelSizing.Auto;

        return PanelRegistry.Get(panelIds[0])?.Sizing ?? DockPanelSizing.Fill;
    }

    /// <summary>
    /// Builds a DockTabGroup for a set of panel IDs that share one row.
    /// </summary>
    private DockTabGroup BuildTabGroupRow(IReadOnlyList<string> panelIds, DockColumnLayout column)
    {
        var content = new Dictionary<string, Control>();
        var titles = new Dictionary<string, string>();

        var colIdx = DockColumnIndexFromLayoutId(column.Id);

        foreach (var id in panelIds)
        {
            content[id] = GetOrCreatePanelBody(id);
            titles[id] = DockerTitle(id);
        }

        var activeIndex = 0;
        var lookupKey = column.PanelIds.FirstOrDefault(p =>
            column.TabGroups.TryGetValue(p, out var t) && t.PanelIds.SequenceEqual(panelIds));
        if (lookupKey != null && column.TabGroups.TryGetValue(lookupKey, out var tab))
            activeIndex = Math.Clamp(tab.ActiveIndex, 0, panelIds.Count - 1);

        var tabGroup = new DockTabGroup(panelIds, content, titles, panelIds[activeIndex]);
        WireDockTabGroupDrag(tabGroup);
        tabGroup.TabChanged += id =>
        {
            var groupKey = column.PanelIds.FirstOrDefault(p =>
                column.TabGroups.TryGetValue(p, out var t) && t.PanelIds.SequenceEqual(panelIds));
            if (groupKey != null && column.TabGroups.TryGetValue(groupKey, out var tg))
                tg.ActiveIndex = panelIds.ToList().IndexOf(id);
        };
        return tabGroup;
    }

    /// <summary>
    /// Returns a cached panel section if one exists, detaching it from any
    /// previous parent. Otherwise builds a fresh section from the factory.
    /// This avoids "Control already has a parent" crashes during rebuild.
    /// </summary>
    private Control GetOrCreatePanelBody(string id)
    {
        if (_dockerPanelBodies.TryGetValue(id, out var cached))
        {
            DetachFromVisualParent(cached);
            return cached;
        }

        var info = GetDockerInfo(id);
        if (info == null)
        {
            return new Border
            {
                Child = new TextBlock
                {
                    Text = $"Missing panel: {id}",
                    Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                    FontSize = 9,
                    Margin = new Thickness(8)
                }
            };
        }

        var content = info.Build();
        DetachFromVisualParent(content);
        var body = BuildDockerBody(id, content);
        _dockerPanelBodies[id] = body;
        return body;
    }

    private Border GetOrCreatePanelSection(string id)
    {
        if (_dockerSections.TryGetValue(id, out var cached))
        {
            DetachFromVisualParent(cached);
            return cached;
        }

        var info = GetDockerInfo(id);
        if (info == null)
        {
            return new Border
            {
                Child = new TextBlock
                {
                    Text = $"Missing panel: {id}",
                    Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        FontSize = 9,
                    Margin = new Thickness(8)
                }
            };
        }

        var content = info.Build();
        DetachFromVisualParent(content);

        return PanelSection(id, info.Title, content);
    }

    /// <summary>
    /// Detaches a control from its current visual parent, regardless of parent type.
    /// Handles Panel (Grid, StackPanel), Border, ContentControl, Decorator.
    /// </summary>
    private static void DetachFromVisualParent(Control control)
    {
        var parent = control.Parent;
        if (parent == null) return;

        if (parent is Panel panel)
        {
            panel.Children.Remove(control);
        }
        else if (parent is Border border && ReferenceEquals(border.Child, control))
        {
            border.Child = null;
        }
        else if (parent is ContentControl cc && ReferenceEquals(cc.Content, control))
        {
            cc.Content = null;
        }
        else if (parent is Decorator decorator && ReferenceEquals(decorator.Child, control))
        {
            decorator.Child = null;
        }
    }

    private Border PanelSection(string id, string title, Control content)
    {
        var body = BuildDockerBody(id, content);
        var titleText = new TextBlock
        {
            Text = title,
            Classes = { "section-header" },
            Margin = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var headerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { titleText }
        };
        var ctxMenu = BuildDockerContextMenu(id);
        ctxMenu.Opening += (_, _) =>
        {
            var fresh = BuildDockerContextMenu(id);
            ctxMenu.ItemsSource = fresh.ItemsSource;
        };
        var header = new Border
        {
            Padding = new Thickness(10, 6, 10, 4),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(StrokeSubtle)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerRow,
            ContextMenu = ctxMenu
        };

        var outer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ClipToBounds = true
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        outer.Children.Add(header);
        outer.Children.Add(body);
        var section = new Border
        {
            Tag = id,
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            ClipToBounds = true,
            Child = outer
        };
        WireDockerHeaderDrag(section, id);
        return section;
    }

    private static Control BuildDockerBody(string id, Control content)
    {
        content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        content.ClipToBounds = true;

        Control child = id == "tool-properties"
            ? ScrollHelper.Create(sv =>
            {
                ScrollHelper.UseVisibleScrollBars(sv, horizontal: false, vertical: true);
                sv.ClipToBounds = true;
                sv.Content = content;
            })
            : content;

        return new Border
        {
            ClipToBounds = true,
            Child = child
        };
    }

    private sealed record DockerInfo(string Id, string Title, Func<Control> Build);

    private void RegisterDockerPanels()
    {
        if (PanelRegistry.AllIds.Count > 0) return; // Already registered

        PanelRegistry.RegisterDefaults(id => () =>
        {
            return id switch
            {
                "tools" => BuildToolsContent(),
                "brush" => BuildBrushSection(),
                "tool-properties" => GetToolPropertiesContent(),
                "layer-properties" => BuildLayerPropertiesSection(),
                "layers" => BuildLayersSection(),
                "color" => BuildColorSection(),
                "color-slider" => BuildColorSlidersSection(),
                "node-graph" => BuildNodeGraphDockerContent(),
                _ => new TextBlock { Text = $"Unknown panel: {id}" }
            };
        });
    }

    private static DockerInfo? GetDockerInfo(string id)
    {
        var panel = PanelRegistry.Get(id);
        return panel == null ? null : new DockerInfo(panel.Id, panel.Title, panel.BuildContent);
    }

    private static string DockerTitle(string id)
        => PanelRegistry.Get(id)?.Title ?? id;

    private bool IsDockerFloating(string id)
        => App.Config.WorkspaceLayout.FloatingPanels.TryGetValue(id, out var f) && f.IsFloating;

    private ContextMenu BuildDockerContextMenu(string id)
    {
        var layout = App.Config.WorkspaceLayout;
        var placement = DockLayoutOps.FindPlacement(layout, id);
        var isOnLeft = placement != null && DockColumnIndices.IsLeft(placement.ColumnIndex);
        var isInBottom = placement != null && DockColumnIndices.IsBottom(placement.ColumnIndex);
        var isFloating = IsDockerFloating(id);
        var isDocked = placement != null && !isFloating;

        var floatItem = new MenuItem { Header = isFloating ? "_Dock" : "_Detach" };
        floatItem.Click += (_, _) =>
        {
            if (IsDockerFloating(id)) DockDocker(id);
            else DetachDocker(id);
        };

        var moveLeft = new MenuItem { Header = "Move to _Left Side", IsEnabled = isDocked && !isOnLeft };
        moveLeft.Click += (_, _) => DockDockerToColumn(id, -1);

        var moveRight = new MenuItem { Header = "Move to _Right Side", IsEnabled = isDocked && (isOnLeft || isInBottom) };
        moveRight.Click += (_, _) => DockDockerToColumn(id, 0);

        var moveUp = new MenuItem { Header = "Move _Up", IsEnabled = isDocked };
        moveUp.Click += (_, _) => MoveDocker(id, -1);

        var moveDown = new MenuItem { Header = "Move _Down", IsEnabled = isDocked };
        moveDown.Click += (_, _) => MoveDocker(id, 1);

        var detachTab = new MenuItem
        {
            Header = "Move to _Own Row",
            IsEnabled = placement is { IsTabMember: true }
        };
        detachTab.Click += (_, _) =>
        {
            if (placement == null) return;
            SaveWorkspaceLayoutFromUi();
            DockLayoutOps.ExtractToRow(layout, id, placement.ColumnIndex, placement.RowIndex + 1);
            DockLayoutOps.CompactTabGroups(placement.Column);
            RebuildDockers();
            App.Config.Save();
        };

        var reset = new MenuItem { Header = "_Reset Panel Size" };
        reset.Click += (_, _) =>
        {
            App.Config.WorkspaceLayout.PanelProportions.Remove(id);
            RebuildDockers();
        };

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                floatItem, new Separator(),
                moveLeft, moveRight, new Separator(),
                moveUp, moveDown, detachTab, new Separator(),
                reset
            }
        };
    }

    private void ToggleDockerVisibility(string id)
    {
        var layout = App.Config.WorkspaceLayout;
        if (layout.HiddenPanelIds.Contains(id))
            layout.HiddenPanelIds.Remove(id);
        else
            layout.HiddenPanelIds.Add(id);

        RebuildDockers();
        if (id == "node-graph")
            SyncBottomDockVisibility();
        App.Config.Save();
    }

    private bool IsDockerVisible(string id)
        => !App.Config.WorkspaceLayout.HiddenPanelIds.Contains(id);

    private void RefreshRecentFilesMenu(bool force = false)
    {
        if (_openRecentMenu == null)
            return;

        App.Config.PruneRecentFiles();
        var recent = App.Config.RecentFiles;

        if (!force && recent.SequenceEqual(_recentMenuSnapshot))
            return;

        _recentMenuSnapshot = [.. recent];
        _openRecentMenu.Items.Clear();
        if (recent.Length == 0)
        {
            _openRecentMenu.Items.Add(new MenuItem
            {
                Header = "(No recent files)",
                IsEnabled = false
            });
            return;
        }

        for (var i = 0; i < recent.Length; i++)
        {
            var path = recent[i];
            var fileName = Path.GetFileName(path);
            var dir = Path.GetDirectoryName(path);
            var display = string.IsNullOrEmpty(dir)
                ? $"{i + 1}. {fileName}"
                : $"{i + 1}. {fileName}  —  {dir}";

            var item = new MenuItem { Header = display };
            ToolTip.SetTip(item, path);
            var openPath = path;
            item.Click += (_, _) => _ = OpenDocumentFromPathAsync(openPath);
            _openRecentMenu.Items.Add(item);
        }

        _openRecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear Recent Files" };
        clear.Click += (_, _) =>
        {
            App.Config.RecentFiles = [];
            App.Config.Save();
            RefreshRecentFilesMenu();
        };
        _openRecentMenu.Items.Add(clear);
    }

    private void RefreshDockersMenu(MenuItem dockersMenu)
    {
        dockersMenu.Items.Clear();
        foreach (var id in PanelRegistry.AllIds)
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

    private void MoveDocker(string id, int delta)
    {
        SaveWorkspaceLayoutFromUi();
        if (!DockLayoutOps.MovePanel(App.Config.WorkspaceLayout, id, delta))
            return;
        RebuildDockers();
        App.Config.Save();
    }

    private void MoveDockerToOtherColumn(string id)
    {
        SaveWorkspaceLayoutFromUi();
        var placement = DockLayoutOps.FindPlacement(App.Config.WorkspaceLayout, id);
        if (placement == null) return;
        var layout = App.Config.WorkspaceLayout;
        DockLayoutOps.RemoveFromAllColumns(layout, id);
        var columnIndex = placement.ColumnIndex;

        if (DockColumnIndices.IsLeft(columnIndex))
        {
            layout.RightColumns[0].PanelIds.Add(id);
        }
        else if (App.Config.WorkspaceLayout.RightColumns.Count >= 2)
        {
            var targetColumn = App.Config.WorkspaceLayout.RightColumns[columnIndex == 0 ? 1 : 0];
            targetColumn.PanelIds.Add(id);
        }
        else
        {
            layout.LeftColumns[0].PanelIds.Add(id);
        }

        RebuildDockers();
        App.Config.Save();
    }

    private void DockDockerToColumn(string id, int columnIndex)
    {
        SaveWorkspaceLayoutFromUi();
        var layout = App.Config.WorkspaceLayout;
        DockLayoutOps.DockToColumn(layout, id, columnIndex);

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
            floating = App.Config.WorkspaceLayout.FloatingPanels[id] = new FloatingPanelState();
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
            : new FloatingPanelState();

        // Reuse the cached section if available — it has properly wired controls.
        // Otherwise build fresh content.
        var section = _dockerSections.TryGetValue(id, out var cached)
            ? cached
            : PanelSection(id, info.Title, info.Build());

        // Detach from any previous parent (Panel, Border, ContentControl, etc.)
        DetachFromVisualParent(section);

        var window = new Window
        {
            Title = info.Title,
            Width = Math.Max(220, cfg.Width),
            Height = Math.Max(180, cfg.Height),
            MinWidth = 220,
            MinHeight = 140,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Content = section
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

    private PixelPoint FindFloatingDockerPosition(string id, FloatingPanelState cfg, double width, double height)
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
            cfg = App.Config.WorkspaceLayout.FloatingPanels[id] = new FloatingPanelState();
        cfg.X = window.Position.X;
        cfg.Y = window.Position.Y;
        cfg.Width = window.Width;
        cfg.Height = window.Height;
    }

    private void RebuildDockers()
    {
        if (_rootGrid == null) return;

        _dockerRows.Clear();

        // Detach all cached sections before rebuilding
        foreach (var kv in _dockerSections)
            DetachFromVisualParent(kv.Value);

        // Rebuild left column
        if (_leftPanel != null)
        {
            var leftCol = Grid.GetColumn(_leftPanel);
            _rootGrid.Children.Remove(_leftPanel);
            _leftPanel = BuildLeftDockColumn();
            Grid.SetColumn(_leftPanel, leftCol);
            _rootGrid.Children.Add(_leftPanel);
            UpdateLeftColumnWidth();
        }

        // Rebuild right panel
        if (_rightPanel != null)
        {
            var rightCol = Grid.GetColumn(_rightPanel);
            _rootGrid.Children.Remove(_rightPanel);
            _rightPanel = BuildRightPanel();
            Grid.SetColumn(_rightPanel, rightCol);
            _rootGrid.Children.Add(_rightPanel);
            UpdateRightPanelWidth();
        }

        RebuildBottomDock();

        // Fix popup placement target
        if (_dockerPopup != null)
            _dockerPopup.PlacementTarget = _rightPanel;

        RefreshDockerContentAfterRebuild();
    }

    private void UpdateLeftColumnWidth()
    {
        if (_rootGrid == null || _rootGrid.ColumnDefinitions.Count <= RootColLeftSplitter) return;
        var layout = App.Config.WorkspaceLayout;
        var hasPanels = layout.LeftColumns.Any(HasVisibleDockerRows);
        var dockCol = _rootGrid.ColumnDefinitions[RootColLeftDock];
        var splitCol = _rootGrid.ColumnDefinitions[RootColLeftSplitter];

        if (hasPanels)
        {
            dockCol.MinWidth = 200;
            dockCol.MaxWidth = 1600;
            dockCol.Width = new GridLength(Math.Clamp(layout.LeftRailWidth, 200, 1600), GridUnitType.Pixel);
            splitCol.MinWidth = 3;
            splitCol.Width = new GridLength(3, GridUnitType.Pixel);
            _leftSplitter!.IsVisible = true;
            if (_leftPanel != null)
                _leftPanel.IsVisible = true;
        }
        else
        {
            dockCol.MinWidth = 0;
            dockCol.MaxWidth = double.PositiveInfinity;
            dockCol.Width = new GridLength(0);
            splitCol.MinWidth = 0;
            splitCol.Width = new GridLength(0);
            _leftSplitter!.IsVisible = false;
            if (_leftPanel != null)
                _leftPanel.IsVisible = false;
        }
    }

    private void UpdateRightPanelWidth()
    {
        if (_rootGrid == null || _rootGrid.ColumnDefinitions.Count <= RootColRightDock) return;

        var layout = App.Config.WorkspaceLayout;
        var hasPanels = layout.RightColumns.Any(HasVisibleDockerRows);
        var dockCol = _rootGrid.ColumnDefinitions[RootColRightDock];
        var splitCol = _rootGrid.ColumnDefinitions[RootColRightSplitter];

        if (hasPanels)
        {
            dockCol.MinWidth = 240;
            dockCol.MaxWidth = 600;
            dockCol.Width = new GridLength(
                Math.Clamp(layout.RightPanelWidth, 240, 600),
                GridUnitType.Pixel);
            splitCol.MinWidth = 3;
            splitCol.Width = new GridLength(3, GridUnitType.Pixel);
            _rightSplitter!.IsVisible = true;
            if (_rightPanel != null)
                _rightPanel.IsVisible = true;
        }
        else
        {
            dockCol.MinWidth = 0;
            dockCol.MaxWidth = double.PositiveInfinity;
            dockCol.Width = new GridLength(0);
            splitCol.MinWidth = 0;
            splitCol.Width = new GridLength(0);
            _rightSplitter!.IsVisible = false;
            if (_rightPanel != null)
                _rightPanel.IsVisible = false;
        }
    }

    private void RefreshDockerContentAfterRebuild()
    {
        QueueSyncToolRailLayout();
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
        var layout = App.Config.WorkspaceLayout;
        if (_rootGrid != null && _rootGrid.ColumnDefinitions.Count > RootColRightDock)
        {
            if (_rootGrid.ColumnDefinitions[RootColLeftDock].ActualWidth > 0)
                layout.LeftRailWidth = Math.Max(200, _rootGrid.ColumnDefinitions[RootColLeftDock].ActualWidth);
            if (_rootGrid.ColumnDefinitions[RootColRightDock].ActualWidth > 0)
                layout.RightPanelWidth = Math.Max(240, _rootGrid.ColumnDefinitions[RootColRightDock].ActualWidth);
        }
        if (_leftDockerHostGrid != null)
            SaveColumnProportionsFromHost(_leftDockerHostGrid,
                layout.LeftColumns.Select(c => c.Id).ToList());
        if (_dockerHostGrid != null)
            SaveColumnProportionsFromHost(_dockerHostGrid,
                layout.RightColumns.Select(c => c.Id).ToList());
        if (_bottomDockerHostGrid != null)
            SaveColumnProportionsFromHost(_bottomDockerHostGrid,
                layout.BottomColumns.Select(c => c.Id).ToList());

        UpdateBottomDockHeight();

        SavePanelProportions();

        foreach (var id in _floatingDockers.Keys.ToArray())
            SaveFloatingDockerBounds(id);
    }

    private void SavePanelProportions()
    {
        var layout = App.Config.WorkspaceLayout;

        // Collect all visible row IDs per column (solo panel IDs + tab group first-panel IDs)
        var columnRows = new Dictionary<int, List<string>>();
        for (var lc = 0; lc < layout.LeftColumns.Count; lc++)
            columnRows[DockColumnIndices.Left(lc)] = layout.LeftColumns[lc].ResolvedRows()
                .Select(r => r.PanelIds[0]).ToList();
        for (var c = 0; c < layout.RightColumns.Count; c++)
            columnRows[DockColumnIndices.Right(c)] = layout.RightColumns[c].ResolvedRows()
                .Select(r => r.PanelIds[0]).ToList();
        for (var bc = 0; bc < layout.BottomColumns.Count; bc++)
            columnRows[DockColumnIndices.Bottom(bc)] = layout.BottomColumns[bc].ResolvedRows()
                .Select(r => r.PanelIds[0]).ToList();

        foreach (var (ci, rowIds) in columnRows)
        {
            double totalH = 0;
            var heights = new Dictionary<string, double>();
            foreach (var id in rowIds)
            {
                if (!_dockerRows.TryGetValue(id, out var row)) continue;
                var h = row.ActualHeight > 0 ? row.ActualHeight : row.Height.Value;
                if (h <= 0) continue;
                heights[id] = h;
                totalH += h;
            }

            if (totalH <= 0) continue;
            foreach (var (id, h) in heights)
                layout.PanelProportions[id] = Math.Max(0.05, h / totalH);
        }
    }

    private void PersistWorkspaceLayout()
    {
        if (!_canvasOnly)
            CaptureRootColumnWidths();
        SaveWorkspaceLayoutFromUi();
        App.Config.Save();
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
            App.Config.WorkspaceLayout = App.Config.WorkspacePresets.TryGetValue(
                    BundledWorkspaceLayouts.DefaultPresetName, out var preset)
                ? preset.Clone()
                : WorkspaceLayout.CreateDefault();
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

    private void ShowLayoutResetNotification()
    {
        var tb = new TextBlock
        {
            Text = "Your workspace layout was corrupted and has been reset to defaults.\n\nThis can happen if panels were moved between tab groups in a way\nthat left some panels unreachable. Your documents are unaffected.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        var ok = new Button { Content = "OK", Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => (ok.Parent as Window)?.Close();
        var dialog = new Window
        {
            Title = "Layout Reset",
            Width = 380,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Content = new StackPanel { Children = { tb, ok } }
        };
        dialog.ShowDialog(this);
    }

    private void ApplyWorkspaceLayout()
    {
        _suppressFloatingDockerClosed = true;
        foreach (var window in _floatingDockers.Values.ToArray())
            window.Close();
        _suppressFloatingDockerClosed = false;
        _floatingDockers.Clear();
        UpdateLeftColumnWidth();
        UpdateRightPanelWidth();
        RebuildDockers();
        OpenFloatingDockersFromConfig();
        RefreshWorkspaceLoadMenu();
        CaptureRootColumnWidths();
        PersistWorkspaceLayout();
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
        btn.Background = new SolidColorBrush(Color.Parse(active ? SelectionBg : "Transparent"));
        btn.BorderBrush = new SolidColorBrush(Color.Parse("Transparent"));
        btn.BorderThickness = new Thickness(0);
        btn.Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
    }

    private static Button SmBtn(string glyph, string tip)
    {
        var btn = new Button
        {
            Content = glyph,
            Classes = { "icon-tool" },
            FontSize = 12,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Button SmIconBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = FlossUi.Icon(icon, FlossUi.IconPanel),
            Classes = { "icon-tool" },
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static ScrubSlider MkSlider(double min, double max, double value, string tip)
        => ScrubSliderFactory.Create(min, max, value, tip);

    private static Control LabelSlider(string label, ScrubSlider slider, string fmt = "")
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
            if (e.Property == RangeBase.ValueProperty)
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
        cfg.WorkspaceLayout ??= WorkspaceLayout.CreateDefault();
        if (_rootGrid != null && _rootGrid.ColumnDefinitions.Count > RootColRightDock)
        {
            UpdateLeftColumnWidth();
            UpdateRightPanelWidth();
            CaptureRootColumnWidths();
        }
        if (_sizeSlider != null)
            _sizeSlider.Value = Math.Clamp(cfg.LastBrushSize, _sizeSlider.Minimum, _sizeSlider.Maximum);
        if (_opacitySlider != null)
            _opacitySlider.Value = Math.Clamp(cfg.LastBrushOpacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
        if (_hardnessSlider != null)
            _hardnessSlider.Value = Math.Clamp(cfg.LastBrushHardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
        if (_spacingSlider != null)
            _spacingSlider.Value = Math.Clamp(cfg.LastBrushSpacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
        OpenFloatingDockersFromConfig();
        UpdateLeftColumnWidth();
    }

    private readonly HashSet<string> _dirtyBrushAssetIds = new();
    private bool _closeConfirmed;
    private bool _closePromptRunning;

    private void SaveToConfig()
    {
        FlushLayoutToConfig();
        FlushToolGroupsSave();
    }

    internal void FlushLayoutToConfig()
    {
        FlushBrushPresetAutosave();
        CaptureActiveBrushToPreset();

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
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            SaveToConfig();
            return;
        }

        e.Cancel = true;
        if (_closePromptRunning)
            return;

        _closePromptRunning = true;
        try
        {
            if (!await ResolveUnsavedDocumentsBeforeCloseAsync())
                return;

            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closePromptRunning = false;
        }
    }

    // ── Wire-up ───────────────────────────────────────────────────────────────
    private void WireControls()
    {
        _workspaceViewport.AddHandler(PointerWheelChangedEvent, Workspace_OnPointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _workspaceViewport.AddHandler(PointerPressedEvent, Workspace_OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _workspaceViewport.AddHandler(PointerMovedEvent, Workspace_OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _workspaceViewport.AddHandler(PointerReleasedEvent, Workspace_OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _workspaceViewport.PointerExited += Workspace_OnPointerExited;
        _workspaceViewport.PointerCaptureLost += Workspace_OnPointerCaptureLost;
        _workspaceViewport.SizeChanged += (_, _) => SyncCanvasViewport();

        WireCanvas();

        WireBrushSlider(_sizeSlider, p => p with { Size = _sizeSlider.Value });
        WireBrushSlider(_maxSizePercentSlider, p => p with { MaxSizePercent = _maxSizePercentSlider.Value }, SyncBrushSizeLimits);
        WireBrushSlider(_opacitySlider, p => p with { Opacity = _opacitySlider.Value });
        WireBrushSlider(_flowSlider, p => p with { Flow = _flowSlider.Value });
        WireBrushSlider(_hardnessSlider, p => p with { Hardness = _hardnessSlider.Value });
        WireBrushSlider(_spacingSlider, p => p with { Spacing = _spacingSlider.Value });
        WireBrushSlider(_smoothingSlider, p => p with { Smoothing = _smoothingSlider.Value });
        WireBrushSlider(_grainSlider, p => p with { Grain = _grainSlider.Value });

        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        WireKeyboardRegionTracking();
        RegisterShortcuts();
    }

    private void WireBrushSlider(
        ScrubSlider? slider,
        Func<BrushPreset, BrushPreset> map,
        Action? afterCommit = null)
    {
        if (slider == null) return;

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncingBrushUi) return;
            if (slider.IsScrubbing)
                PreviewCurrentBrush(map);
            else
            {
                UpdateCurrentBrush(map);
                afterCommit?.Invoke();
            }
        };
        slider.ScrubCompleted += (_, _) =>
        {
            if (_syncingBrushUi) return;
            UpdateCurrentBrush(map);
            afterCommit?.Invoke();
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
        // Snapshot settings into the preset we're leaving. Callers that repoint
        // LastActivePresetId before calling us must capture explicitly first.
        if (_activeToolGroup?.ActivePreset is { } leaving && leaving.Id != preset.Id)
            CaptureBrushToPresetIfChanged(leaving);

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
                    _syncingToolPropertyPanel = true;
                    try
                    {
                        _toolPropsWindow.SyncFromToolPreset(preset);
                        _toolPropsWindow.SyncFromPreset(overridden with { Color = _canvas.Brush.Color });
                    }
                    finally
                    {
                        _syncingToolPropertyPanel = false;
                    }
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
                    _syncingToolPropertyPanel = true;
                    try
                    {
                        _toolPropsWindow.SyncFromToolPreset(preset);
                        _toolPropsWindow.SyncFromPreset(overridden with { Color = _canvas.Brush.Color });
                    }
                    finally
                    {
                        _syncingToolPropertyPanel = false;
                    }
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
        if (btn != null) btn.Content = FlossUi.Icon(group.ActiveIcon, FlossUi.IconRail);

        RefreshGroupPresets();
        ScheduleToolGroupsSave();
        SaveActiveToolSelection();
        RefreshToolProperties();
        SyncNodeGraphDockToActiveBrush();
    }

    private void CaptureActiveBrushToPreset()
        => CaptureActiveBrushToPresetIfChanged();

    private static bool IsViewportNavigationPreset(string presetId)
        => presetId is ToolGroupConfig.ViewHandPresetId
            or ToolGroupConfig.ViewRotatePresetId
            or ToolGroupConfig.ViewZoomInPresetId
            or ToolGroupConfig.ViewZoomOutPresetId;

    internal bool PushTemporaryPreset(string presetId)
    {
        if (_temporaryPresetActive) return false;

        // Pan/zoom/rotate the viewport without committing or canceling transform / smart-shape edit.
        if ((_canvas.IsTransformActive || _canvas.IsSmartShapeEditActive) && IsViewportNavigationPreset(presetId))
        {
            foreach (var group in App.ToolGroups.Groups)
            {
                var preset = group.Presets.FirstOrDefault(p => p.Id == presetId);
                if (preset == null) continue;
                if (!_canvas.PushViewportNavOverlay(ToolForPreset(preset))) return false;
                _temporaryPresetActive = true;
                return true;
            }
            return false;
        }

        foreach (var group in App.ToolGroups.Groups)
        {
            var preset = group.Presets.FirstOrDefault(p => p.Id == presetId);
            if (preset == null) continue;
            if (_canvas.IsSmartShapeEditActive)
                return false;
            if (_canvas.ActiveTool.HasPendingOperation)
                _canvas.CommitActiveTool();
            _savedPresetTemp = _activeToolGroup?.ActivePreset;
            _temporaryPresetActive = true;
            _canvas.SetActiveTool(ToolForPreset(preset), preset);
            return true;
        }
        return false;
    }

    internal void PopTemporaryPreset()
    {
        if (!_temporaryPresetActive) return;

        if (_canvas.HasViewportNavOverlay)
        {
            _canvas.PopViewportNavOverlay();
            _temporaryPresetActive = false;
            return;
        }

        _temporaryPresetActive = false;
        var prev = _savedPresetTemp;
        _savedPresetTemp = null;

        // Commit any pending operation on the temporary tool before switching back
        // (e.g. eyedropper click that hasn't received PointerUp yet because the
        // modifier key was released first).
        if (!_canvas.IsSmartShapeEditActive && _canvas.ActiveTool.HasPendingOperation)
            _canvas.CommitActiveTool();

        if (_canvas.IsSmartShapeEditActive)
            return;

        if (prev != null)
            _canvas.SetActiveTool(ToolForPreset(prev), prev);
        else
            _canvas.SetActiveTool(_toolFactory!.CreateTool(new ToolPreset
            {
                InputProcess = InputProcessType.Brush,
                OutputProcess = OutputProcessType.DirectDraw
            }), null);
    }

    internal void InvalidatePresetToolCache(string? presetId = null)
    {
        if (presetId == null)
            _presetToolCache.Clear();
        else
            _presetToolCache.Remove(presetId);
    }

    internal ITool ToolForPreset(ToolPreset preset)
    {
        // Always use new process-based architecture.
        if (preset.InputProcess == default || preset.OutputProcess == default)
            preset.MigrateFromLegacy();

        if (_presetToolCache.TryGetValue(preset.Id, out var cached))
        {
            if (cached is CompositeTool ct && CachedInputMatchesPreset(ct.Input, preset))
            {
                ToolPresetSync.Apply(ct, preset);
                return cached;
            }

            _presetToolCache.Remove(preset.Id);
        }

        var tool = _toolFactory!.CreateTool(preset);
        _presetToolCache[preset.Id] = tool;
        return tool;
    }

    private static bool CachedInputMatchesPreset(IInputProcess input, ToolPreset preset)
        => preset.InputProcess switch
        {
            InputProcessType.Lasso => input is LassoInputProcess,
            InputProcessType.Polyline => input is PolylineInputProcess,
            InputProcessType.Rect => input is RectInputProcess,
            InputProcessType.Click => input is ClickInputProcess,
            InputProcessType.Liquify => input is LiquifyInputProcess,
            InputProcessType.Drag or InputProcessType.MoveLayer or InputProcessType.Hand
                or InputProcessType.Rotate or InputProcessType.Zoom => input is DragInputProcess,
            InputProcessType.Pen or InputProcessType.Brush or InputProcessType.Eraser
                or InputProcessType.Smudge => input is BrushStrokeInputProcess,
            _ => true
        };

    private static void SetRailActive(Button button, bool active)
    {
        button.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : "Transparent"));
        button.BorderBrush = Avalonia.Media.Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0);
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
        _deleteLayerButton.IsEnabled = CanDeleteSelectedLayers();
        _moveLayerUpButton.IsEnabled = activeIdx < layers.Count - 1;
        _moveLayerDownButton.IsEnabled = activeIdx > 0;

        if (_layerRows.TryGetValue(activeIdx, out var refs))
        {
            layer.MarkThumbnailDirty();
            layer.RefreshThumbnail();
            if (refs.PreviewImage != null)
                refs.PreviewImage.Source = layer.GetThumbnail();
            if (refs.MaskPreviewImage != null && layer.HasMask)
            {
                layer.MarkMaskThumbnailDirty();
                layer.RefreshMaskThumbnail();
                refs.MaskPreviewImage.Source = layer.GetMaskThumbnail();
            }
        }
    }

    private void UpdateMenuState(bool hasDocument, IReadOnlyList<DrawingLayer> layers)
    {
        var activeIdx = hasDocument && layers.Count > 0 ? _canvas.ActiveLayerIndex : -1;
        var canModifyActive = hasDocument && _canvas.Document.CanModifyActiveLayer;
        var canDeleteLayer = hasDocument && CanDeleteSelectedLayers();

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
        if (_moveUpMenuItem != null) _moveUpMenuItem.IsEnabled = hasDocument && activeIdx < layers.Count - 1;
        if (_moveDownMenuItem != null) _moveDownMenuItem.IsEnabled = hasDocument && activeIdx > 0;
        if (_addBackgroundMenuItem != null) _addBackgroundMenuItem.IsEnabled = hasDocument;

        if (_filterMenu != null) _filterMenu.IsEnabled = hasDocument && canModifyActive;

        if (_toolbarSaveBtn != null) _toolbarSaveBtn.IsEnabled = hasDocument;
        if (_toolbarUndoBtn != null) _toolbarUndoBtn.IsEnabled = hasDocument && _canvas.CanUndo;
        if (_toolbarRedoBtn != null) _toolbarRedoBtn.IsEnabled = hasDocument && _canvas.CanRedo;
    }

    // ── Docker Popup System ────────────────────────────────────────────────────

    /// <summary>Pre-builds tool popup content so sliders exist for wire/restore.</summary>
    private void EnsurePopupContent()
    {
        // Reuse the same tool-properties tree as the docked BRUSH panel.
        _cachedToolContent = GetToolPropertiesContent();
    }

    private Control BuildPopupTriggerStrip()
    {
        var strip = new Border
        {
            Width = 32,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            ClipToBounds = true
        };

        var stack = new StackPanel { Spacing = 0 };

        var toolBtn = PopupTriggerButton("tool-properties", Icons.TuneVertical, "Tool");
        var layerBtn = PopupTriggerButton("layer-properties", Icons.DotsVertical, "Layer Properties");

        stack.Children.Add(toolBtn);
        stack.Children.Add(layerBtn);

        strip.Child = stack;
        return strip;
    }

    private Button PopupTriggerButton(string contentId, string iconPath, string tooltip)
    {
        var icon = Icons.Make(iconPath, 18, new SolidColorBrush(Color.Parse("#999999")));
        icon.Margin = new Thickness(0, 10, 0, 0);

        var btn = new Button
        {
            Content = icon,
            Width = 32,
            Height = 40,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        btn.Click += (_, _) => ToggleDockerPopup(contentId);
        return btn;
    }

    private void ToggleDockerPopup(string contentId)
    {
        if (_dockerPopup == null || _rightPanel == null) return;

        // If same popup is open, close it
        if (_popupContentId == contentId && _dockerPopup.IsOpen)
        {
            _dockerPopup.IsOpen = false;
            _popupContentId = null;
            return;
        }

        // Get cached or build content, detach from any previous parent
        Control content = contentId switch
        {
            "tool-properties" => _cachedToolContent ??= GetToolPropertiesContent(),
            "layer-properties" => BuildLayerPropertiesSection(),
            _ => new TextBlock { Text = contentId }
        };
        DetachFromVisualParent(content);

        // Wrap in a border — anchored to top of right panel
        var popupW = Math.Max(320, _rightPanel.Bounds.Width * 0.85);
        var popupH = Math.Max(400, _rightPanel.Bounds.Height * 0.9);
        var wrapper = new Border
        {
            Width = popupW,
            MaxHeight = popupH,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            ClipToBounds = true,
            Child = content
        };

        _dockerPopup.Width = wrapper.Width;
        _dockerPopup.Child = wrapper;
        _dockerPopup.IsOpen = true;
        _popupContentId = contentId;
    }

    // ── End Docker Popup System ────────────────────────────────────────────────
}

internal sealed class RulerOverlay : Control
{
    private DrawingCanvas _canvas;
    private const double RulerThickness = 20;
    private const double MinMinorTickPixels = 8;
    private const double MinLabelPixels = 72;
    private static readonly double[] NiceSteps = [1, 2, 5];

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

        var bg = new SolidColorBrush(Color.FromArgb(255, 30, 30, 35));
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 180, 200, 220)), 1);
        var labelBrush = new SolidColorBrush(Color.FromArgb(220, 200, 210, 220));

        var scaledW = docW * zoom;
        var minorStep = NiceStepForPixels(zoom, MinMinorTickPixels);
        var majorStep = NiceStepForPixels(zoom, MinLabelPixels);

        // Horizontal ruler bar (bottom of viewport)
        var rulerY = h - RulerThickness;
        ctx.FillRectangle(bg, new Rect(0, rulerY, w, RulerThickness));
        if (scaledW > 0)
        {
            var startX = Math.Floor(ScreenToDocX(0, w, scaledW, zoom, flipX, panX) / minorStep) * minorStep;
            var endX = Math.Ceiling(ScreenToDocX(w, w, scaledW, zoom, flipX, panX) / minorStep) * minorStep;
            if (startX > endX) (startX, endX) = (endX, startX);
            startX = Math.Max(0, startX);
            endX = Math.Min(docW, endX);

            for (var x = startX; x <= endX; x += minorStep)
            {
                var sx = DocToScreenX(x, w, scaledW, zoom, flipX, panX);
                if (sx < -0.5 || sx > w) continue;
                var isMajor = IsMajorTick(x, majorStep);
                var tickH = isMajor ? RulerThickness : RulerThickness * 0.4;
                ctx.DrawLine(tickPen, new Point(sx, rulerY), new Point(sx, rulerY + tickH));
                if (isMajor)
                {
                    var ft = new FormattedText(((int)x).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        Typeface.Default, 9, labelBrush);
                    ctx.DrawText(ft, new Point(sx + 2, rulerY - 12));
                }
            }
        }

        // Vertical ruler bar (left of viewport)
        ctx.FillRectangle(bg, new Rect(0, 0, RulerThickness, h));
        var scaledH = docH * zoom;
        if (scaledH > 0)
        {
            var startY = Math.Floor(ScreenToDocY(0, h, scaledH, zoom, flipY, panY) / minorStep) * minorStep;
            var endY = Math.Ceiling(ScreenToDocY(h, h, scaledH, zoom, flipY, panY) / minorStep) * minorStep;
            if (startY > endY) (startY, endY) = (endY, startY);
            startY = Math.Max(0, startY);
            endY = Math.Min(docH, endY);

            for (var y = startY; y <= endY; y += minorStep)
            {
                var sy = DocToScreenY(y, h, scaledH, zoom, flipY, panY);
                if (sy < -0.5 || sy > h) continue;
                var isMajor = IsMajorTick(y, majorStep);
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

    private static double NiceStepForPixels(double zoom, double minPixels)
    {
        if (zoom <= 0) return minPixels;
        var raw = minPixels / zoom;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var step in NiceSteps)
        {
            var candidate = step * magnitude;
            if (candidate >= raw)
                return candidate;
        }
        return 10 * magnitude;
    }

    private static bool IsMajorTick(double value, double majorStep)
    {
        if (majorStep <= 0) return false;
        var nearest = Math.Round(value / majorStep) * majorStep;
        return Math.Abs(value - nearest) <= Math.Max(0.001, majorStep * 0.0001);
    }

    private static double DocToScreenX(double x, double viewportW, double scaledW, double zoom, int flipX, double panX)
        => flipX == 1
            ? (viewportW - scaledW) * 0.5 + x * zoom + panX
            : (viewportW + scaledW) * 0.5 - x * zoom + panX;

    private static double ScreenToDocX(double sx, double viewportW, double scaledW, double zoom, int flipX, double panX)
        => flipX == 1
            ? (sx - panX - (viewportW - scaledW) * 0.5) / zoom
            : ((viewportW + scaledW) * 0.5 + panX - sx) / zoom;

    private static double DocToScreenY(double y, double viewportH, double scaledH, double zoom, int flipY, double panY)
        => flipY == 1
            ? (viewportH - scaledH) * 0.5 + y * zoom + panY
            : (viewportH + scaledH) * 0.5 - y * zoom + panY;

    private static double ScreenToDocY(double sy, double viewportH, double scaledH, double zoom, int flipY, double panY)
        => flipY == 1
            ? (sy - panY - (viewportH - scaledH) * 0.5) / zoom
            : ((viewportH + scaledH) * 0.5 + panY - sy) / zoom;
}
