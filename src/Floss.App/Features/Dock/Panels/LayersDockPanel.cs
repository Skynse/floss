using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Floss.App.Canvas;
using Floss.App.Canvas.Compositing;
using Floss.App.Controls;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Avalonia.Controls.Templates;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed partial class LayersDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    private readonly HashSet<int> _selectedLayerIndices=new(); private bool _pendingLayerRebuild; private bool _layerRebuildCoalesce; public bool SyncingLayerUi{get;set;}
    public LayersDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);Content=BuildLayersSectionImpl();}
    public IReadOnlyCollection<int> SelectedLayerIndices=>_selectedLayerIndices;
    public void ClearLayerSelection()=>_selectedLayerIndices.Clear(); public void AddLayerSelection(int i)=>_selectedLayerIndices.Add(i);
    public int LayerSelectionCount=>_selectedLayerIndices.Count; public List<int> GetLayerSelectionSorted()=>_selectedLayerIndices.OrderBy(x=>x).ToList();
    public void ReplaceLayerSelection(IEnumerable<int> idx){_selectedLayerIndices.Clear();foreach(var i in idx)_selectedLayerIndices.Add(i);}
    public void ApplySingleLayerSelection(int index)
    {
        _pendingLayerSelectIndex = -1;
        _pendingDragIndex = -1;
        _pendingDragArgs = null;
        _selectedLayerIndices.Clear();
        if (index >= 0)
            _selectedLayerIndices.Add(index);
        RefreshLayerRowSelectionStylesImpl();
    }
    public void DeleteSelectedLayers() => DeleteSelectedLayersImpl();
    public void Rebuild()=>BuildLayerListImpl();
    public void ScheduleRebuild(){if(_pendingLayerRebuild){_layerRebuildCoalesce=true;return;}_pendingLayerRebuild=true;Avalonia.Threading.Dispatcher.UIThread.Post(()=>{do{_layerRebuildCoalesce=false;_pendingLayerRebuild=false;BuildLayerListImpl();}while(_layerRebuildCoalesce);},Avalonia.Threading.DispatcherPriority.Background);}
    public void RefreshLayerRowSelectionStyles()=>RefreshLayerRowSelectionStylesImpl(); public void ExpandAndScrollToLayers(IReadOnlyList<int> f)=>ExpandAndScrollToLayersImpl(f); public void PruneLayerSelection()=>PruneLayerSelectionImpl(); public void UpdateLayerRow(int i)=>UpdateLayerRowImpl(i); public bool CanDeleteSelectedLayers()=>CanDeleteSelectedLayersImpl(); public void SyncLayerStatusBar(int a,IReadOnlyList<DrawingLayer> l)=>SyncLayerStatusBarImpl(a,l);
    public void InvalidateLayerListCache() => _lastVisibleLayerIndexes = null;

    private readonly Avalonia.Collections.AvaloniaList<DrawingLayer> _visibleLayers = new();
    private readonly Dictionary<int, LayerRowRefs> _layerRows = new();
    private TextBox _layerNameBox = null!;
    private Button _deleteLayerButton = null!;
    private Button _moveLayerUpButton = null!;
    private Button _moveLayerDownButton = null!;
    private Button _lockLayerBtn = null!;
    private Button _alphaLockLayerBtn = null!;
    private Button _clipLayerBtn = null!;
    private Button _maskLayerBtn = null!;
    private Button _refLayerBtn = null!;
    private ScrubSlider _layerOpacitySlider = null!;
    private bool _layerOpacityScrubActive;
    private ComboBox _blendModeComboBox = null!;
    private ListBox _layerListBox = null!;
    private ContextMenu? _maskLayerToolbarMenu;

    // Drag-drop insertion line overlay
    private Avalonia.Controls.Canvas? _dropLineCanvas;
    private Border? _dropLine;
    private Grid? _layerMainGrid;
    private Border? _dropTargetRow;
    private Thickness _dropTargetOriginalThickness;
    private IBrush? _dropTargetOriginalBorderBrush;
    private static readonly IBrush DropIndicatorBrush = new SolidColorBrush(Color.Parse(Accent));
    private const RoutingStrategies LayerDropRouting = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

    private int _layerDragSourceIndex = -1;
    private int _pendingDragIndex = -1;
    private int _pendingLayerSelectIndex = -1;
    private bool _layerDragInProgress;
    private Point _pendingDragStartPos;
    private PointerPressedEventArgs? _pendingDragArgs;
    private int _renamingLayerIndex = -1;
    private TextBox? _activeLayerNameEdit;
    private Action<bool>? _finishLayerRename;
    private List<int>? _lastVisibleLayerIndexes;

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

    // ── Pre-Allocated Layer Brushes (Performance Optimization) ──────────────

    // Row Backgrounds
    private static readonly IBrush RowBgActive = new SolidColorBrush(Color.Parse(SelectionBgActive));
    private static readonly IBrush RowBgSelected = new SolidColorBrush(Color.Parse(SelectionBg));
    private static readonly IBrush RowBgDefault = new SolidColorBrush(Color.Parse(Bg2));

    // Row Borders
    private static readonly IBrush RowBorderActive = new SolidColorBrush(Color.Parse(SelectionBorder));
    private static readonly IBrush RowBorderSelected = new SolidColorBrush(Color.Parse(Stroke));
    private static readonly IBrush RowBorderDefault = new SolidColorBrush(Color.Parse(Stroke));

    // Text Colors
    private static readonly IBrush TextFgActive = new SolidColorBrush(Color.Parse(TextPrimary));
    private static readonly IBrush TextFgSelected = new SolidColorBrush(Color.Parse(TextPrimary));
    private static readonly IBrush TextFgDefault = new SolidColorBrush(Color.Parse(TextSecondary));

    private static readonly IBrush TextDimActive = new SolidColorBrush(Color.Parse(TextSecondary));
    private static readonly IBrush TextDimSelected = new SolidColorBrush(Color.Parse(TextSecondary));
    private static readonly IBrush TextDimDefault = new SolidColorBrush(Color.Parse(TextMuted));

    // Icon Colors
    private static readonly IBrush IconOff = new SolidColorBrush(Color.Parse("#5b5b5b"));
    private static readonly IBrush IconVisOn = new SolidColorBrush(Color.Parse(TextSecondary));
    private static readonly IBrush GroupIconFg = new SolidColorBrush(Color.Parse(TextMuted));
    private static readonly IBrush GroupFolderIcon = new SolidColorBrush(Color.Parse(TextMuted));
    private static readonly IBrush GroupFolderIconActive = new SolidColorBrush(Color.Parse(TextPrimary));
    private static readonly IBrush GroupPreviewBg = new SolidColorBrush(Color.Parse(Bg2));
    private static readonly IBrush GroupPreviewBgActive = new SolidColorBrush(Color.Parse(Bg3));

    // Miscellaneous UI Elements
    // Layer thumbnail — alpha checkerboard so transparent regions are visible.
    private static readonly IBrush PreviewThumbBg = CreateThumbCheckerboardBrush();

    private static IBrush CreateThumbCheckerboardBrush()
    {
        var dark = new SolidColorBrush(Color.Parse("#888888"));
        var light = new SolidColorBrush(Color.Parse("#BBBBBB"));
        var checkSize = 6.0;
        var group = new DrawingGroup();
        // Dark squares at (0,0) and (cs,cs)
        group.Children.Add(new GeometryDrawing
        {
            Brush = dark,
            Geometry = new RectangleGeometry(new Rect(0, 0, checkSize, checkSize))
        });
        group.Children.Add(new GeometryDrawing
        {
            Brush = dark,
            Geometry = new RectangleGeometry(new Rect(checkSize, checkSize, checkSize, checkSize))
        });
        // Light squares at (cs,0) and (0,cs)
        group.Children.Add(new GeometryDrawing
        {
            Brush = light,
            Geometry = new RectangleGeometry(new Rect(checkSize, 0, checkSize, checkSize))
        });
        group.Children.Add(new GeometryDrawing
        {
            Brush = light,
            Geometry = new RectangleGeometry(new Rect(0, checkSize, checkSize, checkSize))
        });
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, checkSize * 2, checkSize * 2, RelativeUnit.Absolute),
            Stretch = Stretch.None
        };
    }

    private static readonly IBrush PreviewFrameBorder = new SolidColorBrush(Color.Parse(Stroke));
    private static readonly IBrush SwatchBorder = new SolidColorBrush(Color.Parse(Stroke));

    // Clipping strip
    private static readonly IBrush ClipIndicatorBrush = new SolidColorBrush(Color.Parse("#e8527a"));

    // Mask editing indicator
    private static readonly IBrush MaskEditBorderBrush = new SolidColorBrush(Color.Parse("#38a169"));

    // Mask thumbnail badge
    private static readonly IBrush MaskThumbBg = new SolidColorBrush(Color.Parse("#1e1e2e"));
    private static readonly IBrush MaskThumbBorder = new SolidColorBrush(Color.Parse("#45475a"));
    private static readonly IBrush MaskThumbText = new SolidColorBrush(Color.Parse("#a6adc8"));

    // Fonts
    private static readonly FontFamily MonospaceFont = new FontFamily("Consolas, Courier New, monospace");

    // ── Layers section ────────────────────────────────────────────────────────
    private Control BuildLayersSectionImpl()
    {
        // Layer name
        _layerNameBox = new TextBox
        {
            PlaceholderText = "Layer name",
            FontSize = 11,
            Height = 26,
            Background = new SolidColorBrush(Color.Parse(Bg3)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        _layerNameBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                ApplyLayerName();
                e.Handled = true;
            }
        };
        _layerNameBox.LostFocus += (_, _) => ApplyLayerName();

        // Blend mode
        _blendModeComboBox = new ComboBox
        {
            ItemsSource = BlendModes,
            SelectedItem = BlendMode.Normal,
            FontSize = 11,
            Padding = new Thickness(5, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _blendModeComboBox.SelectionChanged += (_, _) =>
        {
            if (SyncingLayerUi) return;
            if (_blendModeComboBox.SelectedItem is BlendMode mode)
                _ps.Canvas.SetActiveLayerBlendMode(mode);
        };

        // Opacity — live preview while scrubbing; one undo step on ScrubCompleted.
        _layerOpacitySlider = DockPanelUiHelpers.MkSlider(0, 1, 1, "Layer opacity");
        _layerOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (SyncingLayerUi || e.Property != RangeBase.ValueProperty) return;
            if (_layerOpacitySlider.IsScrubbing)
            {
                if (!_layerOpacityScrubActive)
                {
                    _ps.Canvas.BeginActiveLayerOpacityScrub();
                    _layerOpacityScrubActive = true;
                }
                _ps.Canvas.PreviewActiveLayerOpacity(_layerOpacitySlider.Value);
                return;
            }

            if (!_layerOpacityScrubActive)
                _ps.Canvas.SetActiveLayerOpacity(_layerOpacitySlider.Value);
        };
        _layerOpacitySlider.ScrubCompleted += (_, _) => CommitLayerOpacityScrubIfActive();

        // Blend + Opacity row
        var blendOpRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        Grid.SetColumn(_blendModeComboBox, 0);
        Grid.SetColumn(_layerOpacitySlider, 2);
        blendOpRow.Children.Add(_blendModeComboBox);
        blendOpRow.Children.Add(_layerOpacitySlider);

        // ── Layer panel hamburger menu () ──────────────────────────────
        var layerMenuBtn = DockPanelUiHelpers.SmIconBtn(Icons.DotsVertical, "Layer menu");
        layerMenuBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var layer = _ps.Canvas.Document.ActiveLayer;
            var index = _ps.Canvas.ActiveLayerIndex;
            if (layer != null)
            {
                menu.ItemsSource = BuildLayerContextMenuItems(index, layer);
                menu.Open(layerMenuBtn);
            }
        };
        layerMenuBtn.Margin = new Thickness(0, 0, 4, 0);

        var nameRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(layerMenuBtn, Avalonia.Controls.Dock.Left);
        nameRow.Children.Add(layerMenuBtn);
        nameRow.Children.Add(_layerNameBox);

        // ── Layer property toggles (row 3 — above list, like ) ───────────────
        _lockLayerBtn = DockPanelUiHelpers.SmIconBtn(Icons.LockOutline, "Lock layer");
        _alphaLockLayerBtn = DockPanelUiHelpers.SmIconBtn(Icons.AlphaLock, "Alpha lock");
        _clipLayerBtn = DockPanelUiHelpers.SmIconBtn(Icons.ClipToBelow, "Clipping mask");
        _maskLayerBtn = DockPanelUiHelpers.SmIconBtn(Icons.LayerMask, "Layer mask (click to create or edit, right-click for options)");
        _refLayerBtn = DockPanelUiHelpers.SmIconBtn(Icons.Eye, "Reference layer");

        _lockLayerBtn.Click += (_, _) => _ps.Canvas.ToggleLayerLock(_ps.Canvas.ActiveLayerIndex);
        _alphaLockLayerBtn.Click += (_, _) => _ps.Canvas.ToggleLayerAlphaLock(_ps.Canvas.ActiveLayerIndex);
        _clipLayerBtn.Click += (_, _) => _ps.Canvas.ToggleLayerClipping(_ps.Canvas.ActiveLayerIndex);
        _maskLayerToolbarMenu = new ContextMenu();
        _maskLayerBtn.ContextMenu = _maskLayerToolbarMenu;
        _maskLayerToolbarMenu.Opening += (_, _) => RebuildMaskToolbarMenu();
        _maskLayerBtn.Click += (_, _) => OnMaskToolbarClick();
        _refLayerBtn.Click += (_, _) => _ps.Canvas.ToggleLayerReference(_ps.Canvas.ActiveLayerIndex);

        var toggleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 6, 0, 6),
            Children = { _lockLayerBtn, _alphaLockLayerBtn, _clipLayerBtn, _maskLayerBtn, _refLayerBtn }
        };

        // 1. Initialize the Virtualized ListBox
        _layerListBox = new ListBox
        {
            ItemsSource = _visibleLayers,
            // Selection is tracked in _selectedLayerIndices; ListBox chrome selection
            // fights row clicks on the already-active layer.
            SelectionMode = SelectionMode.Single,
            SelectedIndex = -1,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Styles =
            {
                new Style(x => x.OfType<ListBoxItem>())
                {
                    Setters =
                    {
                        new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                        new Setter(ListBoxItem.MarginProperty, new Thickness(0, 0, 0, 2)),
                        new Setter(ListBoxItem.BackgroundProperty, Avalonia.Media.Brushes.Transparent),
                        new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0))
                    }
                }
            },
            ItemTemplate = new FuncDataTemplate<DrawingLayer>((layer, _) =>
            {
                if (layer == null) return new Border();
                var i = LayerIndexOf(layer);
                var (row, refs) = BuildLayerRow(i, layer);
                if (i >= 0) _layerRows[i] = refs;
                return row;
            })
        };

        // Pointer drag must be reserved for layer reordering, not scrolling.
        _layerListBox.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } sv)
            {
                ScrollHelper.UseVisibleScrollBars(sv, horizontal: false, vertical: true);
                ScrollHelper.DisablePointerPanScroll(sv);
            }
        };

        // ── Action buttons (bottom bar — like ) ───────────────────────────────
        var addBtn = DockPanelUiHelpers.SmIconBtn(Icons.LayerPlus, "Add layer  (Ctrl+Shift+N)");
        var folderBtn = DockPanelUiHelpers.SmIconBtn(Icons.Folder, "Add layer folder");
        var dupBtn = DockPanelUiHelpers.SmIconBtn(Icons.ContentCopy, "Duplicate  (Ctrl+J)");
        _deleteLayerButton = DockPanelUiHelpers.SmIconBtn(Icons.DeleteOutline, "Delete  (Ctrl+Delete)");
        _moveLayerUpButton = DockPanelUiHelpers.SmIconBtn(Icons.ArrowUp, "Move up  (Ctrl+Up)");
        _moveLayerDownButton = DockPanelUiHelpers.SmIconBtn(Icons.ArrowDown, "Move down  (Ctrl+Down)");

        addBtn.Click += (_, _) => _ps.Canvas.AddLayer();
        folderBtn.Click += (_, _) => _ps.Canvas.AddGroupLayer();
        dupBtn.Click += (_, _) => _ps.Canvas.DuplicateLayer();
        _deleteLayerButton.Click += (_, _) => DeleteSelectedLayersImpl();
        _moveLayerUpButton.Click += (_, _) => _ps.Canvas.MoveActiveLayer(1);
        _moveLayerDownButton.Click += (_, _) => _ps.Canvas.MoveActiveLayer(-1);

        foreach (var btn in new Button[] { addBtn, folderBtn, dupBtn, _deleteLayerButton, _moveLayerUpButton, _moveLayerDownButton })
            btn.Margin = new Thickness(0, 0, 2, 0);

        var ctrlRow = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { addBtn, folderBtn, dupBtn, _moveLayerUpButton, _moveLayerDownButton, _deleteLayerButton }
        };

        // Grid: name | blend+opacity | toggles | list(*) | actions
        var mainGrid = new Grid
        {
            Margin = new Thickness(4, 1, 4, 3),
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *, Auto")
        };

        Grid.SetRow(nameRow, 0);
        Grid.SetRow(blendOpRow, 1);
        Grid.SetRow(toggleRow, 2);
        Grid.SetRow(_layerListBox, 3);
        Grid.SetRow(ctrlRow, 4);

        blendOpRow.Margin = new Thickness(0, 8, 0, 0);

        mainGrid.Children.Add(nameRow);
        mainGrid.Children.Add(blendOpRow);
        mainGrid.Children.Add(toggleRow);
        mainGrid.Children.Add(_layerListBox);
        mainGrid.Children.Add(ctrlRow);

        // Drop insertion line overlay — floats above the ListBox to show where dragged
        // layers will be inserted. Must NOT be cached so it updates live during drag.
        _dropLine = new Border
        {
            Height = 4,
            Background = DropIndicatorBrush,
            CornerRadius = new CornerRadius(2),
            IsVisible = false,
            IsHitTestVisible = false
        };
        _dropLineCanvas = new Avalonia.Controls.Canvas
        {
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Children = { _dropLine }
        };
        Grid.SetRow(_dropLineCanvas, 3);
        mainGrid.Children.Add(_dropLineCanvas);

        _layerMainGrid = mainGrid;

        return mainGrid;
    }
    private void ApplyLayerName()
    {
        var name = _layerNameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            _ps.Canvas.SetActiveLayerName(name);
    }

    // ── Rect-pick: expand folders containing found layers, multi-select, scroll ─
    private void ExpandAndScrollToLayersImpl(IReadOnlyList<int> foundIndices)
    {
        if (foundIndices.Count == 0) return;
        var layers = _ps.Canvas.Layers;

        // Expand every ancestor group that contains a found layer.
        foreach (var idx in foundIndices)
        {
            for (var parent = layers[idx].Parent; parent != null; parent = parent.Parent)
            {
                if (!parent.IsOpen)
                    _ps.Canvas.ToggleLayerOpen(LayerIndexOf(parent));
            }
        }

        // Select all found layers; activate the topmost one (highest index = visually on top).
        _selectedLayerIndices.Clear();
        foreach (var idx in foundIndices) _selectedLayerIndices.Add(idx);
        _ps.Canvas.SelectLayer(foundIndices[0]); // foundIndices[0] is already highest index (top-down search)

        BuildLayerListImpl();

        // Scroll the list to the active layer's row.
        var scrollTarget = layers[foundIndices[0]];
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _layerListBox.ScrollIntoView(scrollTarget);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void PruneLayerSelectionImpl()
    {
        var max = _ps.Canvas.Layers.Count;
        _selectedLayerIndices.RemoveWhere(i => i < 0 || i >= max);
        var active = _ps.Canvas.ActiveLayerIndex;
        // If the active layer isn't in the selection the document changed active
        // programmatically (new layer, undo/redo, move, etc.). Reset so only the
        // active row is highlighted — don't leave a stale secondary selection.
        if (max > 0 && !_selectedLayerIndices.Contains(active))
        {
            _selectedLayerIndices.Clear();
            if (active >= 0) _selectedLayerIndices.Add(active);
        }
    }

    private IReadOnlyList<int> LayerDeleteTargetIndices()
    {
        if (_selectedLayerIndices.Count > 0)
            return _selectedLayerIndices.ToList();
        return _ps.Canvas.ActiveLayerIndex >= 0 ? [_ps.Canvas.ActiveLayerIndex] : Array.Empty<int>();
    }

    private bool CanDeleteSelectedLayersImpl() =>
        _ps.Canvas.CanDeleteLayer(LayerDeleteTargetIndices());

    private void DeleteSelectedLayersImpl()
    {
        var indices = LayerDeleteTargetIndices();
        if (!_ps.Canvas.CanDeleteLayer(indices))
            return;

        _ps.Canvas.DeleteLayer(indices);
        _selectedLayerIndices.Clear();
        if (_ps.Canvas.ActiveLayerIndex >= 0)
            _selectedLayerIndices.Add(_ps.Canvas.ActiveLayerIndex);
        BuildLayerListImpl();
    }

    /// <summary>
    /// Sets the active layer for a menu/shortcut action without collapsing a multi-selection.
    /// </summary>
    private void FocusLayerForAction(int index)
    {
        if (index < 0 || index >= _ps.Canvas.Layers.Count)
            return;

        if (_selectedLayerIndices.Count > 1 && _selectedLayerIndices.Contains(index))
        {
            if (index != _ps.Canvas.ActiveLayerIndex)
                _ps.Canvas.SelectLayer(index);
            return;
        }

        _selectedLayerIndices.Clear();
        _selectedLayerIndices.Add(index);
        if (index != _ps.Canvas.ActiveLayerIndex)
            _ps.Canvas.SelectLayer(index);
    }

    // ── Layer panel ───────────────────────────────────────────────────────────
    private void BuildLayerListImpl()
    {
        if (_ps.Canvas == null || _ps.Canvas.Layers == null) return;

        var layers = _ps.Canvas.Layers;
        var visibleIndexes = VisibleLayerIndexes().ToList();
        if (TryRefreshLayerListInPlace(layers, visibleIndexes))
            return;

        _lastVisibleLayerIndexes = visibleIndexes;
        _layerRows.Clear();
        var layersToDisplay = visibleIndexes.Select(i => layers[i]).ToList();

        _visibleLayers.Clear();
        _visibleLayers.InsertRange(0, layersToDisplay);
        _layerListBox.SelectedIndex = -1;
        SyncActiveLayerPanel();
    }

    private bool TryRefreshLayerListInPlace(IReadOnlyList<DrawingLayer> layers, List<int> visibleIndexes)
    {
        if (_lastVisibleLayerIndexes == null
            || _lastVisibleLayerIndexes.Count != visibleIndexes.Count
            || !visibleIndexes.SequenceEqual(_lastVisibleLayerIndexes))
        {
            return false;
        }

        var layersToDisplay = visibleIndexes.Select(i => layers[i]).ToList();
        if (_visibleLayers.Count != layersToDisplay.Count)
            return false;

        for (var i = 0; i < layersToDisplay.Count; i++)
        {
            if (!ReferenceEquals(_visibleLayers[i], layersToDisplay[i]))
                return false;
        }

        foreach (var index in visibleIndexes)
            UpdateLayerRowImpl(index);

        SyncActiveLayerPanel();
        return true;
    }

    private void SyncActiveLayerPanel()
    {
        var layers = _ps.Canvas.Layers;
        if (layers.Count == 0 || _ps.Canvas.ActiveLayerIndex < 0)
            return;

        var active = layers[_ps.Canvas.ActiveLayerIndex];
        SyncingLayerUi = true;
        _layerOpacitySlider.Value = active.Opacity;
        _blendModeComboBox.SelectedItem = active.BlendMode;
        _layerNameBox.Text = active.Name;
        SyncingLayerUi = false;
        _ps.Layers.RefreshLayerProperties();
        RefreshLayerToggleButtons(active);
    }

    private void RefreshLayerToggleButtons(DrawingLayer layer)
    {
        if (_lockLayerBtn == null) return;
        SetToggleActive(_lockLayerBtn, layer.IsLocked);
        SetToggleActive(_alphaLockLayerBtn, layer.IsAlphaLocked);
        SetToggleActive(_clipLayerBtn, layer.IsClipping);
        SetToggleActive(_maskLayerBtn, layer.HasMask && layer.IsMaskEditing);
        SetToggleActive(_refLayerBtn, layer.IsReference);

        if (_maskLayerBtn != null)
        {
            ToolTip.SetTip(_maskLayerBtn, layer.HasMask
                ? layer.IsMaskEditing
                    ? "Editing layer mask (right-click for delete, etc.)"
                    : "Layer mask — click to edit (right-click for options)"
                : "Add layer mask");
        }
    }

    private void OnMaskToolbarClick()
    {
        var idx = _ps.Canvas.ActiveLayerIndex;
        if (idx < 0 || idx >= _ps.Canvas.Layers.Count) return;
        var layer = _ps.Canvas.Layers[idx];
        if (layer.IsGroup || layer.IsPaper) return;

        _ps.Canvas.ToggleLayerMaskEditing(idx);
        BuildLayerListImpl();
    }

    private void RebuildMaskToolbarMenu()
    {
        if (_maskLayerToolbarMenu == null) return;
        _maskLayerToolbarMenu.Items.Clear();

        var idx = _ps.Canvas.ActiveLayerIndex;
        if (idx < 0 || idx >= _ps.Canvas.Layers.Count) return;
        var layer = _ps.Canvas.Layers[idx];
        if (layer.IsGroup || layer.IsPaper) return;

        if (!layer.HasMask)
        {
            _maskLayerToolbarMenu.Items.Add(MaskMenuItem("Create Mask", () =>
            {
                _ps.Canvas.CreateLayerMask(idx);
                BuildLayerListImpl();
            }));
            _maskLayerToolbarMenu.Items.Add(MaskMenuItem("Create and Edit Mask", () =>
            {
                _ps.Canvas.CreateLayerMask(idx);
                _ps.Canvas.SetLayerMaskEditing(idx, true);
                BuildLayerListImpl();
            }));
            return;
        }

        _maskLayerToolbarMenu.Items.Add(MaskMenuItem(
            layer.IsMaskEditing ? "Edit Layer (exit mask)" : "Edit Mask",
            () =>
            {
                if (layer.IsMaskEditing)
                    _ps.Canvas.SetLayerContentEditing(idx);
                else
                    _ps.Canvas.SetLayerMaskEditing(idx, true);
                BuildLayerListImpl();
            }));
        _maskLayerToolbarMenu.Items.Add(MaskMenuItem(
            layer.IsMaskVisible ? "Disable Mask" : "Enable Mask",
            () =>
            {
                _ps.Canvas.ToggleLayerMask(idx);
                BuildLayerListImpl();
            }));
        _maskLayerToolbarMenu.Items.Add(MaskMenuItem("Invert Mask", () =>
        {
            InvertLayerMask(idx);
            BuildLayerListImpl();
        }));
        _maskLayerToolbarMenu.Items.Add(MaskMenuItem("Apply Mask", () =>
        {
            _ps.Canvas.ApplyLayerMask(idx);
            BuildLayerListImpl();
        }));
        _maskLayerToolbarMenu.Items.Add(new MenuItem { Header = "-" });
        _maskLayerToolbarMenu.Items.Add(MaskMenuItem("Delete Mask", () =>
        {
            _ps.Canvas.DeleteLayerMask(idx);
            BuildLayerListImpl();
        }));
    }

    private static MenuItem MaskMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void InvertLayerMask(int idx)
    {
        var layer = _ps.Canvas.Layers[idx];
        if (layer.MaskPixels == null) return;
        var tiles = layer.MaskPixels.CaptureTiles();
        foreach (var (key, tile) in tiles)
        {
            if (tile == null) continue;
            for (var i = 0; i < tile.Length; i += 4)
            {
                var v = (byte)(255 - tile[i + 3]);
                tile[i] = v;
                tile[i + 1] = v;
                tile[i + 2] = v;
                tile[i + 3] = v;
            }
        }
        layer.MaskPixels.RestoreTiles(tiles);
        layer.MarkMaskThumbnailDirty();
        _ps.Canvas.InvalidateVisual();
    }


    private IEnumerable<int> VisibleLayerIndexes()
    {
        var layers = _ps.Canvas.Layers;
        var indexMap = new Dictionary<DrawingLayer, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layers.Count; i++)
        {
            indexMap[layers[i]] = i;
        }

        foreach (var root in layers.Where(l => l.Parent == null).Reverse())
        {
            foreach (var index in GetVisibleIndexes(root, indexMap))
                yield return index;
        }
    }

    private IEnumerable<int> GetVisibleIndexes(DrawingLayer layer, Dictionary<DrawingLayer, int> indexMap)
    {
        if (indexMap.TryGetValue(layer, out var index))
            yield return index;

        if (!layer.IsGroup || !layer.IsOpen) yield break;

        for (var i = layer.Children.Count - 1; i >= 0; i--)
        {
            foreach (var childIndex in GetVisibleIndexes(layer.Children[i], indexMap))
                yield return childIndex;
        }
    }

    private int LayerIndexOf(DrawingLayer layer)
    {
        var layers = _ps.Canvas.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            if (ReferenceEquals(layers[i], layer))
                return i;
        }

        return -1;
    }

    private (Border Row, LayerRowRefs Refs) BuildLayerRow(int i, DrawingLayer layer)
    {
        var isActive = i == _ps.Canvas.ActiveLayerIndex;
        var isSelected = _selectedLayerIndices.Contains(i);

        // Pre-allocated brush selections
        var dimBrush = isActive ? TextDimActive : isSelected ? TextDimSelected : TextDimDefault;
        var fgBrush = isActive ? TextFgActive : isSelected ? TextFgSelected : TextFgDefault;

        var row = new Border
        {
            Background = isActive ? RowBgActive : isSelected ? RowBgSelected : RowBgDefault,
            BorderBrush = layer.IsMaskEditing ? MaskEditBorderBrush
                : isActive ? RowBorderActive : isSelected ? RowBorderSelected : RowBorderDefault,
            BorderThickness = layer.IsMaskEditing
                ? new Thickness(2)
                : isActive ? new Thickness(2, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3, 2),
            Margin = new Thickness(layer.IndentLevel * 8, 0, 0, 0),
            Tag = i,
            ContextMenu = BuildLayerContextMenu(i)
        };

        DragDrop.SetAllowDrop(row, true);
        row.PointerPressed += LayerRowPointerPressed;
        row.PointerMoved += LayerRowPointerMoved;
        row.PointerReleased += LayerRowPointerReleased;
        row.Tapped += LayerRowTapped;
        row.DoubleTapped += LayerRowDoubleTapped;
        row.AddHandler(DragDrop.DragOverEvent, LayerRowDragOver, LayerDropRouting);
        row.AddHandler(DragDrop.DropEvent, LayerRowDrop, LayerDropRouting);

        var showMaskThumb = layer.HasMask && !layer.IsGroup && !layer.IsPaper;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(showMaskThumb ? "3,16,20,16,44,44,*" : "3,16,20,16,48,*")
        };

        // Thin pink strip on the left edge to signal "clipped to layer below"
        var clipStrip = new Border
        {
            Background = layer.IsClipping ? ClipIndicatorBrush : Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };

        var disclosureBtn = LayerDisclosureBtn(layer, i, isActive || isSelected);

        var visBtn = LayerIconBtn(
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            "Toggle visibility",
            layer.IsVisible ? IconVisOn : IconOff, i);
        visBtn.Click += (_, _) => _ps.Canvas.ToggleLayerVisibility((int)visBtn.Tag!);

        var hasRulers = layer.RulerSet is { HasRulers: true };
        var rulerBtn = LayerIconBtn(
            Icons.LineVariant,
            hasRulers ? "Toggle rulers" : "No rulers",
            hasRulers && layer.RulerSet!.RulersVisible ? IconVisOn : IconOff,
            i);
        rulerBtn.IsEnabled = hasRulers;
        rulerBtn.Opacity = hasRulers ? 1 : 0.25;
        rulerBtn.Click += (_, _) =>
        {
            if (!hasRulers) return;
            FocusLayerForAction(i);
            _ps.Canvas.ToggleLayerRulersVisibility(i);
            BuildLayerListImpl();
        };

        var (preview, previewImage) = BuildLayerPreview(layer,
            (isActive || isSelected) && !layer.IsMaskEditing,
            layer.IsPaper ? _ps.Canvas.Document.PaperColor : null);
        preview.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(preview).Properties.IsLeftButtonPressed) return;
            if (layer.IsGroup)
            {
                _ps.Canvas.ToggleLayerOpen(i);
                BuildLayerListImpl();
                e.Handled = true;
                return;
            }

            FocusLayerForAction(i);
            _ps.Canvas.SetLayerContentEditing(i);
            BuildLayerListImpl();
            e.Handled = true;
        };
        if (layer.IsPaper)
            preview.DoubleTapped += (_, _) => _ps.Layers.ShowPaperColorPicker();

        Control thumbHost = preview;
        Image? maskPreviewImage = null;
        if (showMaskThumb)
        {
            var (maskPreview, maskImg) = BuildLayerMaskPreview(layer, isActive || isSelected);
            maskPreviewImage = maskImg;
            maskPreview.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(maskPreview).Properties.IsLeftButtonPressed) return;
                FocusLayerForAction(i);
                _ps.Canvas.SetLayerMaskEditing(i, true);
                BuildLayerListImpl();
                e.Handled = true;
            };

            thumbHost = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 2,
                Children = { preview, maskPreview }
            };
        }

        var nameText = new TextBlock
        {
            Text = layer.Name,
            Foreground = fgBrush,
            FontSize = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        var statusText = new TextBlock
        {
            Text = LayerStatusText(layer),
            Foreground = dimBrush,
            FontSize = 9,
            FontFamily = MonospaceFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        var nameStack = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { nameText, statusText }
        };
        var nameHost = new ContentControl
        {
            Content = nameStack,
            Tag = i,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ClipToBounds = true
        };

        Grid.SetColumn(clipStrip, 0);
        Grid.SetColumn(disclosureBtn, 1);
        Grid.SetColumn(visBtn, 2);
        Grid.SetColumn(rulerBtn, 3);
        Grid.SetColumn(thumbHost, 4);
        Grid.SetColumn(nameHost, showMaskThumb ? 6 : 5);
        grid.Children.Add(clipStrip);
        grid.Children.Add(disclosureBtn);
        grid.Children.Add(visBtn);
        grid.Children.Add(rulerBtn);
        grid.Children.Add(thumbHost);
        grid.Children.Add(nameHost);
        row.Child = grid;
        return (row, new LayerRowRefs(row, disclosureBtn, visBtn, nameHost, previewImage, maskPreviewImage, clipStrip));
    }

    private static string LayerStatusText(DrawingLayer layer)
    {
        if (layer.IsGroup)
        {
            var count = CountLayersInTree(layer);
            return count == 1 ? "1 layer" : $"{count} layers";
        }

        if (layer.Adjustment != null)
        {
            var opPct = (int)Math.Round(layer.Opacity * 100);
            return $"{opPct}%  {AdjustmentLayerData.DisplayName(layer.Adjustment.Kind)}";
        }

        if (layer.RulerSet is { HasRulers: true } rulers)
        {
            var count = rulers.Rulers.Count;
            var label = count == 1 ? "Ruler" : $"{count} rulers";
            return $"{Math.Round(layer.Opacity * 100):0}%  {label}";
        }

        var flags = new List<string>(4);
        if (layer.IsLocked) flags.Add("Lock");
        if (layer.IsAlphaLocked) flags.Add("Alpha");
        if (layer.IsReference) flags.Add("Ref");
        if (layer.IsClipping) flags.Add("Clip");
        if (layer.IsMaskEditing) flags.Add("Mask");
        if (layer.HasMask && !layer.IsMaskVisible) flags.Add("MaskOff");
        if (layer.IsPaper) flags.Add("Paper");
        var suffix = flags.Count == 0 ? "" : "  " + string.Join(" ", flags);
        return $"{Math.Round(layer.Opacity * 100):0}%  {layer.BlendMode}{suffix}";
    }

    private static int CountLayersInTree(DrawingLayer layer)
    {
        var count = 0;
        foreach (var _ in EnumerateLayerTree(layer))
            count++;
        return count;
    }

    private static IEnumerable<DrawingLayer> EnumerateLayerTree(DrawingLayer layer)
    {
        if (!layer.IsGroup) yield break;
        foreach (var child in layer.Children)
        {
            yield return child;
            foreach (var nested in EnumerateLayerTree(child))
                yield return nested;
        }
    }

    private void CommitLayerOpacityScrubIfActive()
    {
        if (!_layerOpacityScrubActive) return;
        _layerOpacityScrubActive = false;
        _ps.Canvas.CommitActiveLayerOpacityScrub();
    }

    private Button LayerDisclosureBtn(DrawingLayer layer, int index, bool highlighted)
    {
        var btn = new Button
        {
            Content = layer.IsGroup
                ? Icons.Make(layer.IsOpen ? Icons.ChevronDown : Icons.ChevronRight, 11, highlighted ? GroupFolderIconActive : GroupIconFg)
                : null,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            Foreground = highlighted ? GroupFolderIconActive : GroupIconFg,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Tag = index,
            IsHitTestVisible = layer.IsGroup
        };
        if (layer.IsGroup)
        {
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                var idx = (int)btn.Tag!;
                _ps.Canvas.ToggleLayerOpen(idx);
                // Rebuild immediately — waiting for LayersChanged coalesce can leave
                // the tree expanded until the user clicks another layer.
                BuildLayerListImpl();
            };
        }
        return btn;
    }

    private ContextMenu BuildLayerContextMenu(int index)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            if (index < 0 || index >= _ps.Canvas.Layers.Count) return;
            menu.ItemsSource = BuildLayerContextMenuItems(index, _ps.Canvas.Layers[index]);
        };
        return menu;
    }

    private List<MenuItem> BuildLayerContextMenuItems(int index, DrawingLayer layer)
    {
        MenuItem Item(string header, Action action, KeyGesture? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture != null) item.InputGesture = gesture;
            item.Click += (_, _) =>
            {
                FocusLayerForAction(index);
                action();
            };
            return item;
        }

        MenuItem MakeLockItem(DrawingLayer l, int idx)
            => Item(l.IsLocked ? "Unlock Layer" : "Lock Layer", () => _ps.Canvas.ToggleLayerLock(idx));

        var pasteItem = Item("_Paste", () => _ps.Canvas.PasteLayer(index));
        pasteItem.IsEnabled = _ps.Canvas.CanPasteLayer;

        var hasSelection = _selectedLayerIndices.Count >= 1;

        var deleteItem = Item("_Delete", DeleteSelectedLayersImpl, new KeyGesture(Key.Delete, KeyModifiers.Control));
        deleteItem.IsEnabled = CanDeleteSelectedLayersImpl();

        MenuItem AdjItem(string header, AdjustmentKind kind)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                FocusLayerForAction(index);
                _ps.Canvas.AddAdjustmentLayer(kind);
                OpenAdjustmentLayerDialog(_ps.Canvas.ActiveLayerIndex);
            };
            return mi;
        }

        var items = new List<MenuItem>
        {
            Item("_New Layer Above", () => _ps.Canvas.AddLayer(), new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
            Item("New _Folder Above", () => _ps.Canvas.AddGroupLayer(), new KeyGesture(Key.G, KeyModifiers.Control)),
            new MenuItem
            {
                Header = "New _Correction Layer",
                ItemsSource = new object[]
                {
                    AdjItem("Brightness / Contrast", AdjustmentKind.BrightnessContrast),
                    AdjItem("Hue / Saturation / Luminosity", AdjustmentKind.HueSaturationLuminosity),
                    AdjItem("Level Correction", AdjustmentKind.LevelCorrection),
                    AdjItem("Tone Curve", AdjustmentKind.ToneCurve),
                    AdjItem("Color Balance", AdjustmentKind.ColorBalance),
                    AdjItem("Posterization", AdjustmentKind.Posterization),
                    AdjItem("Binarization", AdjustmentKind.Binarization),
                    AdjItem("Gradient Map", AdjustmentKind.GradientMap),
                    AdjItem("Reverse Gradient", AdjustmentKind.ReverseGradient),
                }
            },
            Item("_Duplicate", () => _ps.Canvas.DuplicateLayer(), new KeyGesture(Key.J, KeyModifiers.Control)),
            Item("_Copy", () => _ps.Canvas.CopyLayer(index)),
            pasteItem,
            deleteItem,
            new() { Header = "-" },
            Item(layer.IsVisible ? "Hide Layer" : "Show Layer", () => _ps.Canvas.ToggleLayerVisibility(index)),
            MakeLockItem(layer, index),
            Item(layer.IsAlphaLocked ? "Disable Alpha Lock" : "Enable Alpha Lock", () => _ps.Canvas.ToggleLayerAlphaLock(index)),
            Item(layer.IsReference ? "Disable Reference Layer" : "Enable Reference Layer", () => _ps.Canvas.ToggleLayerReference(index)),
            Item(layer.IsClipping ? "Disable Clipping Mask" : "Enable Clipping Mask", () => _ps.Canvas.ToggleLayerClipping(index)),
        };
        if (!layer.IsGroup && !layer.IsPaper && !layer.HasMask)
            items.Add(Item("Create Mask", () =>
            {
                _ps.Canvas.CreateLayerMask(index);
                _ps.Canvas.SetLayerMaskEditing(index, true);
                BuildLayerListImpl();
            }));
        else if (!layer.IsGroup && !layer.IsPaper)
        {
            items.Add(Item(layer.IsMaskEditing ? "Edit Layer (exit mask)" : "Edit Mask", () =>
            {
                if (layer.IsMaskEditing)
                    _ps.Canvas.SetLayerContentEditing(index);
                else
                    _ps.Canvas.SetLayerMaskEditing(index, true);
                BuildLayerListImpl();
            }));
            items.Add(Item(layer.IsMaskVisible ? "Disable Mask" : "Enable Mask", () =>
            {
                _ps.Canvas.ToggleLayerMask(index);
                BuildLayerListImpl();
            }));
            items.Add(Item("Apply Mask", () =>
            {
                _ps.Canvas.ApplyLayerMask(index);
                BuildLayerListImpl();
            }));
            items.Add(new MenuItem { Header = "-" });
            items.Add(Item("Delete Mask", () =>
            {
                _ps.Canvas.DeleteLayerMask(index);
                BuildLayerListImpl();
            }));
        }

        if (layer.IsPaper)
        {
            // Insert "Paper Color..." after visibility toggle (index 8 in base list)
            items.Insert(9, Item("Paper _Color...", _ps.Layers.ShowPaperColorPicker));
        }

        if (hasSelection)
        {
            items.Insert(2, Item("Create Folder and Insert Layers", () =>
            {
                var sorted = _selectedLayerIndices.OrderBy(i => i).ToList();
                _ps.Canvas.GroupSelectedLayers(sorted);
                _selectedLayerIndices.Clear();
                _selectedLayerIndices.Add(_ps.Canvas.ActiveLayerIndex);
                BuildLayerListImpl();
            }, new KeyGesture(Key.G, KeyModifiers.Control)));
        }

        if (layer.IsGroup)
        {
            items.Insert(4, Item(layer.IsOpen ? "Collapse Folder" : "Expand Folder", () =>
            {
                _ps.Canvas.ToggleLayerOpen(index);
                BuildLayerListImpl();
            }));
            items.Insert(5, Item("Flatten Folder", () => _ps.Canvas.FlattenGroup(index)));
        }
        else if (_selectedLayerIndices.Count > 1)
        {
            items.Insert(4, Item(
                $"Merge {_selectedLayerIndices.Count} Selected Layers",
                () => _ps.Canvas.MergeSelectedLayers(_selectedLayerIndices.OrderBy(x => x).ToList())));
        }

        var visibleCount = _visibleLayers.Count;
        items.Add(new() { Header = "-" });
        if (_selectedLayerIndices.Count < visibleCount)
        {
            var selectAll = new MenuItem { Header = "Select All Layers" };
            selectAll.Click += (_, _) =>
            {
                _selectedLayerIndices.Clear();
                foreach (var i in VisibleLayerIndexes()) _selectedLayerIndices.Add(i);
                BuildLayerListImpl();
            };
            items.Add(selectAll);
        }
        if (_selectedLayerIndices.Count > 1)
        {
            var deselectAll = new MenuItem { Header = "Deselect All" };
            deselectAll.Click += (_, _) => { _selectedLayerIndices.Clear(); BuildLayerListImpl(); };
            items.Add(deselectAll);
        }

        MenuItem AsyncItem(string header, Func<Task> action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += async (_, _) =>
            {
                FocusLayerForAction(index);
                await action();
            };
            return mi;
        }
        MenuItem InstantFilterItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                FocusLayerForAction(index);
                action();
            };
            return mi;
        }
        items.Add(new() { Header = "-" });
        items.Add(new MenuItem
        {
            Header = "Filters",
            ItemsSource = new object[]
            {
                new MenuItem
                {
                    Header = "Adjust",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Brightness / Contrast...", _ps.Layers.ApplyBrightnessContrastFilter),
                        AsyncItem("Exposure / Gamma...", _ps.Layers.ApplyExposureGammaFilter),
                        AsyncItem("Levels...", _ps.Layers.ApplyLevelsFilter),
                        AsyncItem("Hue / Saturation...", _ps.Layers.ApplyHueSaturationFilter),
                        AsyncItem("Color Curves...", _ps.Layers.ApplyColorCurvesFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Color",
                    ItemsSource = new object[]
                    {
                        InstantFilterItem("Invert", _ps.Layers.ApplyInvertFilter),
                        InstantFilterItem("Desaturate", _ps.Layers.ApplyDesaturateFilter),
                        AsyncItem("Sepia...", _ps.Layers.ApplySepiaFilter),
                        AsyncItem("Threshold...", _ps.Layers.ApplyThresholdFilter),
                        AsyncItem("Posterize...", _ps.Layers.ApplyPosterizeFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Blur / Enhance",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Gaussian Blur...", _ps.Layers.ApplyBlurFilter),
                        AsyncItem("Motion Blur...", _ps.Layers.ApplyMotionBlurFilter),
                        new Separator(),
                        AsyncItem("Sharpen...", _ps.Layers.ApplySharpenFilter),
                        AsyncItem("Bloom...", _ps.Layers.ApplyBloomFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Stylize",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Pixelate...", _ps.Layers.ApplyPixelateFilter),
                        AsyncItem("Vignette...", _ps.Layers.ApplyVignetteFilter),
                        AsyncItem("Emboss...", _ps.Layers.ApplyEmbossFilter),
                        AsyncItem("Find Edges...", _ps.Layers.ApplyEdgeDetectFilter),
                        AsyncItem("Chromatic Aberration...", _ps.Layers.ApplyChromaticAberrationFilter),
                        AsyncItem("Noise...", _ps.Layers.ApplyNoiseFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Cleanup",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Remove Dust...", _ps.Layers.ApplyRemoveDustFilter),
                    }
                },
            }
        });

        return items;
    }

    private void SelectLayerWithModifiers(int index, KeyModifiers mods, bool rebuildList = false)
    {
        if (mods.HasFlag(KeyModifiers.Control))
        {
            var wasSelected = _selectedLayerIndices.Contains(index);
            var saved = _selectedLayerIndices.ToList();
            _ps.Canvas.SelectLayer(index);
            _selectedLayerIndices.Clear();
            foreach (var i in saved)
                _selectedLayerIndices.Add(i);
            if (wasSelected)
                _selectedLayerIndices.Remove(index);
            else
                _selectedLayerIndices.Add(index);
        }
        else if (mods.HasFlag(KeyModifiers.Shift) && _selectedLayerIndices.Count > 0)
        {
            var saved = _selectedLayerIndices.ToList();
            var active = _ps.Canvas.ActiveLayerIndex;
            _ps.Canvas.SelectLayer(index);
            _selectedLayerIndices.Clear();
            foreach (var i in saved)
                _selectedLayerIndices.Add(i);

            var visible = VisibleLayerIndexes().ToList();
            var ai = visible.IndexOf(active);
            var ti = visible.IndexOf(index);
            if (ai >= 0 && ti >= 0)
            {
                var lo = Math.Min(ai, ti);
                var hi = Math.Max(ai, ti);
                for (var vi = lo; vi <= hi; vi++)
                    _selectedLayerIndices.Add(visible[vi]);
            }
        }
        else
        {
            _selectedLayerIndices.Clear();
            _selectedLayerIndices.Add(index);
            _ps.Canvas.SelectLayer(index);
        }

        if (rebuildList)
            BuildLayerListImpl();
        else
            RefreshLayerRowSelectionStylesImpl();
    }

    private void CommitPendingLayerSelection()
    {
        if (_ps.Canvas == null || _pendingLayerSelectIndex < 0) return;

        var index = _pendingLayerSelectIndex;
        _pendingLayerSelectIndex = -1;
        if (index != _ps.Canvas.ActiveLayerIndex)
            _ps.Canvas.SelectLayer(index);
        else
            RefreshLayerRowSelectionStylesImpl();
    }

    private void RefreshLayerRowSelectionStylesImpl()
    {
        if (_ps.Canvas == null) return;

        foreach (var (idx, refs) in _layerRows)
        {
            if (idx < 0 || idx >= _ps.Canvas.Layers.Count) continue;
            var layer = _ps.Canvas.Layers[idx];
            var isActive = idx == _ps.Canvas.ActiveLayerIndex
                || (_pendingLayerSelectIndex >= 0 && idx == _pendingLayerSelectIndex);
            var isSelected = _selectedLayerIndices.Contains(idx);
            refs.Row.Background = isActive ? RowBgActive : isSelected ? RowBgSelected : RowBgDefault;
            refs.Row.BorderBrush = layer.IsMaskEditing ? MaskEditBorderBrush
                : isActive ? RowBorderActive : isSelected ? RowBorderSelected : RowBorderDefault;
            refs.Row.BorderThickness = layer.IsMaskEditing
                ? new Thickness(2)
                : isActive ? new Thickness(2, 1, 1, 1) : new Thickness(1);
        }

        if (_ps.Canvas.Layers.Count > 0)
        {
            var active = _ps.Canvas.Layers[_ps.Canvas.ActiveLayerIndex];
            RefreshLayerToggleButtons(active);
            _ps.Layers.RefreshLayerProperties();
        }
    }

    private void LayerRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (sender is not Border row || row.Tag is not int index) return;
            var point = e.GetCurrentPoint(row);
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;
            if (IsLayerRowInteractiveSource(e.Source)) return;

            var isRightClick = point.Properties.IsRightButtonPressed;
            var alreadySelected = _selectedLayerIndices.Contains(index);
            if (isRightClick)
            {
                // Right-click unselected row: select it alone, then open the menu.
                // Right-click an already-selected row: keep the full multi-selection
                // (do not call SelectLayer — that fires LayersChanged and clears it).
                if (!alreadySelected)
                {
                    _selectedLayerIndices.Clear();
                    _selectedLayerIndices.Add(index);
                    _ps.Canvas.SelectLayer(index);
                    RefreshLayerRowSelectionStylesImpl();
                }
                return;
            }

            // Ctrl+click always toggles selection (even on already-selected layers).
            // Shift+click extends the range immediately.
            // Plain click on an unselected row: panel selection only until release or
            // drag end. Calling _ps.Canvas.SelectLayer here fires LayersChanged and
            // ScheduleLayerListRebuild, which recreates rows and breaks drag.
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (ctrl || shift)
            {
                SelectLayerWithModifiers(index, e.KeyModifiers);
                _pendingLayerSelectIndex = -1;
            }
            else if (!alreadySelected)
            {
                _selectedLayerIndices.Clear();
                _selectedLayerIndices.Add(index);
                _pendingLayerSelectIndex = index;
                RefreshLayerRowSelectionStylesImpl();
            }
            else
            {
                _pendingLayerSelectIndex = -1;
            }

            if (ctrl || shift || e.ClickCount > 1) return;

            // Store pending drag state; actual drag starts in PointerMoved after threshold exceeded.
            _pendingDragIndex = index;
            _pendingDragStartPos = point.Position;
            _pendingDragArgs = e;
            if (!ctrl && !shift && e.ClickCount <= 1)
                e.Pointer.Capture(row);
        }
        catch (Exception ex) { CrashLog.Write(ex, "MainWindow.LayerRowPointerPressed"); }
    }

    private const double LayerDragThreshold = 20.0;

    private async void LayerRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingDragIndex < 0 || _pendingDragArgs == null) return;
        var pos = e.GetCurrentPoint(sender as Visual).Position;
        var dx = pos.X - _pendingDragStartPos.X;
        var dy = pos.Y - _pendingDragStartPos.Y;
        if (dx * dx + dy * dy < LayerDragThreshold * LayerDragThreshold) return;

        // If the button was released before the threshold was reached (fast tap with
        // touchpad jitter), don't initiate — calling DoDragDropAsync with the button
        // already up confuses the native XDnD/Wayland state machine and causes it to
        // get permanently stuck.
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        var index = _pendingDragIndex;
        var args = _pendingDragArgs!;
        _pendingDragIndex = -1;
        _pendingDragArgs = null;
        _layerDragInProgress = true;

        _layerDragSourceIndex = index;
        var draggedIndices = _selectedLayerIndices.Contains(index)
            ? _selectedLayerIndices.OrderBy(i => i).ToList()
            : new List<int> { index };

        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(string.Join(",", draggedIndices));
        data.Add(item);

        var pointer = e.Pointer;
        e.Handled = true;
        try
        {
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move);
        }
        finally
        {
            _layerDragSourceIndex = -1;
            ClearDropIndicator();
            pointer.Capture(null);
            CommitPendingLayerSelection();
            _layerDragInProgress = false;
            BuildLayerListImpl();
        }
    }

    private void LayerRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_layerDragInProgress && _pendingLayerSelectIndex >= 0)
            CommitPendingLayerSelection();

        _pendingDragIndex = -1;
        _pendingDragArgs = null;
        if (sender is Border row && ReferenceEquals(e.Pointer.Captured, row))
            e.Pointer.Capture(null);
    }

    private void WindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_activeLayerNameEdit == null) return;
        if (IsInsideElement(e.Source, _activeLayerNameEdit)) return;
        FinishActiveLayerRename(commit: true);
    }

    private void LayerRowTapped(object? sender, TappedEventArgs e)
    {
        // Tapped fires after PointerPressed, so selection is already handled there.
    }

    private void LayerRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int index) return;
        if (IsLayerRowInteractiveSource(e.Source)) return;
        _ps.Canvas.SelectLayer(index);
        var layers = _ps.Canvas.Layers;
        if (index >= 0 && index < layers.Count && layers[index].Adjustment != null)
        {
            OpenAdjustmentLayerDialog(index);
        }
        else
        {
            BeginLayerRename(index);
        }
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
        var layers = _ps.Canvas.Layers;
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
                    _ps.Canvas.SelectLayer(index);
                    _ps.Canvas.SetActiveLayerName(name);
                }
            }

            _renamingLayerIndex = -1;
            _activeLayerNameEdit = null;
            _finishLayerRename = null;
            UpdateLayerRowImpl(index);
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
            _ps.Shell.Owner.Focus();
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

    private bool TryGetLayerRow(object? source, out Border row, out int targetIndex)
    {
        row = null!;
        targetIndex = -1;
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is not Border border || border.Tag is not int idx)
                continue;
            if (_layerRows.TryGetValue(idx, out var refs) && ReferenceEquals(refs.Row, border))
            {
                row = border;
                targetIndex = idx;
                return true;
            }
        }

        return false;
    }

    private void LayerRowDragOver(object? sender, DragEventArgs e)
    {
        if (!TryGetLayerRow(e.Source, out var row, out var targetIndex))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            ClearDropIndicator();
            return;
        }

        var sourceIndices = GetDraggedLayerIndices(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));

        e.DragEffects = sourceIndices.Count > 0 && sourceIndices.All(si => _ps.Canvas.CanMoveLayer(si, targetIndex, placement))
            ? DragDropEffects.Move
            : DragDropEffects.None;

        if (e.DragEffects == DragDropEffects.Move)
            UpdateDropIndicator(row, placement);
        else
            ClearDropIndicator();

        e.Handled = true;
    }

    private void LayerRowDrop(object? sender, DragEventArgs e)
    {
        ClearDropIndicator();
        if (!TryGetLayerRow(e.Source, out var row, out var targetIndex))
            return;

        var sourceIndices = GetDraggedLayerIndices(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));
        if (sourceIndices.Count == 0) return;

        var layerCount = _ps.Canvas.Layers.Count;
        // Drag data captures indices at drag-start time; by drop time the layer
        // list may have shifted (undo, auto-documents, etc.). Filter stale indices.
        sourceIndices = sourceIndices.Where(si => si >= 0 && si < layerCount).ToList();
        if (sourceIndices.Count == 0) return;
        if (targetIndex < 0 || targetIndex >= layerCount) return;

        var layersToMove = sourceIndices
            .Select(si => _ps.Canvas.Layers[si])
            .ToList();
        if (placement == LayerDropPlacement.Above)
            layersToMove.Reverse();
        var targetLayer = _ps.Canvas.Layers[targetIndex];
        foreach (var layer in layersToMove)
        {
            var currentSource = -1;
            var currentTarget = -1;
            for (var i = 0; i < _ps.Canvas.Layers.Count; i++)
            {
                if (_ps.Canvas.Layers[i] == layer) currentSource = i;
                if (_ps.Canvas.Layers[i] == targetLayer) currentTarget = i;
            }
            if (currentSource < 0 || currentTarget < 0 || currentSource == currentTarget) continue;
            _ps.Canvas.MoveLayer(currentSource, currentTarget, placement);
        }
        e.Handled = true;
    }

    private List<int> GetDraggedLayerIndices(IDataTransfer data)
    {
        var raw = data.TryGetText();
        if (string.IsNullOrEmpty(raw)) return [];
        return raw.Split(',')
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : -1)
            .Where(i => i >= 0)
            .OrderBy(i => i)
            .ToList();
    }

    private LayerDropPlacement GetLayerDropPlacement(Border row, int targetIndex, Point position)
    {
        var target = _ps.Canvas.Layers[targetIndex];

        // Folder rows are drop-onto targets; reorder above/below via adjacent layer rows.
        if (target.IsGroup)
            return LayerDropPlacement.Into;

        var height = Math.Max(1, row.Bounds.Height);
        return position.Y < height * 0.5 ? LayerDropPlacement.Above : LayerDropPlacement.Below;
    }

    private void UpdateDropIndicator(Border row, LayerDropPlacement placement)
    {
        if (placement == LayerDropPlacement.Into)
        {
            if (_dropTargetRow != null && !ReferenceEquals(_dropTargetRow, row))
                ClearDropIndicator();

            if (_dropLine is not null)
                _dropLine.IsVisible = false;

            if (!ReferenceEquals(_dropTargetRow, row))
            {
                _dropTargetRow = row;
                _dropTargetOriginalThickness = row.BorderThickness;
                _dropTargetOriginalBorderBrush = row.BorderBrush;
                row.BorderThickness = new Thickness(2);
                row.BorderBrush = DropIndicatorBrush;
            }

            return;
        }

        if (_dropTargetRow != null)
            ClearDropIndicator();

        if (_dropLineCanvas == null || _dropLine == null) return;

        var pt = row.TranslatePoint(new Point(0, 0), _dropLineCanvas);
        if (!pt.HasValue) return;

        var y = placement == LayerDropPlacement.Above
            ? pt.Value.Y - 1
            : pt.Value.Y + row.Bounds.Height - 3;
        Avalonia.Controls.Canvas.SetTop(_dropLine, y);
        Avalonia.Controls.Canvas.SetLeft(_dropLine, 0);
        var cw = _dropLineCanvas.Bounds.Width;
        _dropLine.Width = cw > 0 ? cw : _layerListBox.Bounds.Width;
        _dropLine.IsVisible = true;
    }

    private void ClearDropIndicator()
    {
        if (_dropTargetRow != null)
        {
            _dropTargetRow.BorderThickness = _dropTargetOriginalThickness;
            _dropTargetRow.BorderBrush = _dropTargetOriginalBorderBrush;
            _dropTargetRow = null;
        }

        if (_dropLine != null)
            _dropLine.IsVisible = false;
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

    private void UpdateLayerRowImpl(int index)
    {
        var layers = _ps.Canvas.Layers;
        if (index < 0 || index >= layers.Count || !_layerRows.TryGetValue(index, out var refs))
        {
            // The row is scrolled off-screen and recycled. We don't need to update its visuals.
            return;
        }

        var layer = layers[index];
        var isActive = index == _ps.Canvas.ActiveLayerIndex;
        var isSelected = _selectedLayerIndices.Contains(index);
        var dimBrush = isActive ? TextDimActive : TextDimDefault;
        var fgBrush = isActive ? TextFgActive : TextFgDefault;

        refs.Row.Background = isActive ? RowBgActive : isSelected ? RowBgSelected : RowBgDefault;
        refs.Row.BorderBrush = layer.IsMaskEditing ? MaskEditBorderBrush
            : isActive ? RowBorderActive : isSelected ? RowBorderSelected : RowBorderDefault;
        refs.Row.BorderThickness = layer.IsMaskEditing ? new Thickness(2) : new Thickness(1);
        refs.ClipStrip.Background = layer.IsClipping ? ClipIndicatorBrush : Avalonia.Media.Brushes.Transparent;

        SetLayerIconBtnIcon(refs.VisibilityButton,
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            layer.IsVisible ? IconVisOn : IconOff);

        refs.DisclosureButton.Content = layer.IsGroup
            ? Icons.Make(layer.IsOpen ? Icons.ChevronDown : Icons.ChevronRight, 11,
                isActive || isSelected ? GroupFolderIconActive : GroupIconFg)
            : null;
        refs.DisclosureButton.IsHitTestVisible = layer.IsGroup;
        refs.DisclosureButton.Foreground = isActive || isSelected ? GroupFolderIconActive : GroupIconFg;
        if (_renamingLayerIndex != index)
        {
            refs.NameHost.Content = new StackPanel
            {
                Spacing = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = layer.Name,
                        Foreground = fgBrush,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = LayerStatusText(layer),
                        Foreground = dimBrush,
                        FontSize = 9,
                        FontFamily = MonospaceFont,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }
                }
            };
        }

        if (isActive && !SyncingLayerUi)
        {
            SyncingLayerUi = true;
            _layerOpacitySlider.Value = layer.Opacity;
            _blendModeComboBox.SelectedItem = layer.BlendMode;
            _layerNameBox.Text = layer.Name;
            SyncingLayerUi = false;
            RefreshLayerToggleButtons(layer);
        }
    }

    private static (Control Frame, Image? PreviewImage) BuildLayerPreview(
        DrawingLayer layer,
        bool highlighted,
        Avalonia.Media.Color? paperColor = null)
    {
        var (thumbW, thumbH) = DrawingLayer.ComputeThumbnailPixelSize(layer.Width, layer.Height);
        var frame = new Border
        {
            Width = thumbW,
            Height = thumbH,
            Margin = new Thickness(1, 0, 1, 0),
            Background = PreviewThumbBg,
            BorderBrush = PreviewFrameBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        if (layer.IsObjectLayer)
        {
            frame.Background = highlighted ? GroupPreviewBgActive : GroupPreviewBg;
            frame.BorderBrush = highlighted
                ? new SolidColorBrush(Color.Parse(Accent))
                : PreviewFrameBorder;
            var iconSize = Math.Min(22, Math.Min(thumbW, thumbH) - 4);
            frame.Child = Icons.Make(Icons.RectangleOutline, iconSize, highlighted ? GroupFolderIconActive : GroupFolderIcon);
            return (frame, null);
        }

        if (layer.IsGroup)
        {
            frame.Background = highlighted ? GroupPreviewBgActive : GroupPreviewBg;
            frame.BorderBrush = highlighted
                ? new SolidColorBrush(Color.Parse(Accent))
                : PreviewFrameBorder;
            var iconSize = Math.Min(22, Math.Min(thumbW, thumbH) - 4);
            frame.Child = Icons.Make(
                layer.IsOpen ? Icons.FolderOpenOutline : Icons.Folder,
                iconSize,
                highlighted ? GroupFolderIconActive : GroupFolderIcon);
            return (frame, null);
        }

        if (layer.IsPaper && paperColor is { } pc)
        {
            var swatchGrid = new Grid();
            swatchGrid.Children.Add(new Border
            {
                Background = new SolidColorBrush(pc),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            });
            var docIconSize = Math.Min(18, Math.Min(thumbW, thumbH) - 4);
            var docIcon = Icons.Make(Icons.PaperDocument, docIconSize,
                new SolidColorBrush(pc.R + pc.G + pc.B > 384 ? Colors.Black : Colors.White));
            docIcon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            docIcon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            swatchGrid.Children.Add(docIcon);
            frame.Child = swatchGrid;
            return (frame, null);
        }

        var image = new Image
        {
            Source = layer.GetThumbnail(),
            Stretch = Stretch.Fill,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        frame.Child = image;
        return (frame, image);
    }

    private async void OpenAdjustmentLayerDialog(int layerIndex)
    {
        var layers = _ps.Canvas.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return;
        var layer = layers[layerIndex];
        if (layer.Adjustment == null) return;

        var snapshot = layer.Adjustment.Clone();
        var dlg = new Floss.App.Windows.AdjustmentLayerDialog(
            layer.Adjustment,
            preview => _ps.Canvas.PreviewLayerAdjustmentParams(layerIndex, preview));

        await dlg.ShowDialog(_ps.Shell.Owner);

        if (dlg.Result != null)
            _ps.Canvas.SetLayerAdjustmentParams(layerIndex, dlg.Result);
        else
            _ps.Canvas.PreviewLayerAdjustmentParams(layerIndex, snapshot);
    }

    private static (Border Frame, Image? PreviewImage) BuildLayerMaskPreview(DrawingLayer layer, bool highlighted)
    {
        var (thumbW, thumbH) = DrawingLayer.ComputeThumbnailPixelSize(layer.Width, layer.Height);
        var editing = layer.IsMaskEditing;
        var frame = new Border
        {
            Width = thumbW,
            Height = thumbH,
            Margin = new Thickness(1, 0, 1, 0),
            Background = MaskThumbBg,
            BorderBrush = editing ? MaskEditBorderBrush : highlighted ? new SolidColorBrush(Color.Parse(Accent)) : MaskThumbBorder,
            BorderThickness = editing ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(frame, "Layer mask (click to edit)");

        var image = new Image
        {
            Source = layer.GetMaskThumbnail(),
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        frame.Child = image;
        return (frame, image);
    }

    private void SyncLayerStatusBarImpl(int activeIdx, IReadOnlyList<DrawingLayer> layers)
    {
        _deleteLayerButton.IsEnabled = CanDeleteSelectedLayersImpl();
        _moveLayerUpButton.IsEnabled = activeIdx < layers.Count - 1;
        _moveLayerDownButton.IsEnabled = activeIdx > 0;
        if (_layerRows.TryGetValue(activeIdx, out var refs))
        {
            var layer = layers[activeIdx];
            layer.MarkThumbnailDirty();
            if (refs.PreviewImage != null)
                refs.PreviewImage.Source = layer.GetThumbnail();
            if (refs.MaskPreviewImage != null && layer.HasMask)
            {
                layer.MarkMaskThumbnailDirty();
                refs.MaskPreviewImage.Source = layer.GetMaskThumbnail();
            }
        }
    }

    private sealed record LayerRowRefs(
        Border Row,
        Button DisclosureButton,
        Button VisibilityButton,
        ContentControl NameHost,
        Image? PreviewImage,
        Image? MaskPreviewImage,
        Border ClipStrip);

    public void HandleWindowPointerPressed(PointerPressedEventArgs e) => WindowPointerPressed(null, e);

    private Button LayerIconBtn(string icon, string tooltip, IBrush iconBrush, int index)
    {
        var btn = new Button
        {
            Content = Icons.Make(icon, 16, iconBrush),
            [ToolTip.TipProperty] = tooltip,
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
        => btn.Content = Icons.Make(icon, 16, iconBrush);

    private static void SetToggleActive(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(Color.Parse(active ? SelectionBg : "Transparent"));
        btn.BorderBrush = new SolidColorBrush(Color.Parse("Transparent"));
        btn.BorderThickness = new Thickness(0);
        btn.Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
    }
}
