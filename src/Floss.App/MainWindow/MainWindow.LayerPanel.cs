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
using Floss.App.Canvas;
using Floss.App.Canvas.Compositing;
using Floss.App.Controls;
using Floss.App.Document;
using Avalonia.Controls.Templates;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{

    private readonly Avalonia.Collections.AvaloniaList<DrawingLayer> _visibleLayers = new();
    private ListBox _layerListBox = null!;

    // Drag-drop insertion line overlay
    private Avalonia.Controls.Canvas? _dropLineCanvas;
    private Border? _dropLine;
    private Grid? _layerMainGrid;
    private Border? _dropTargetRow;
    private Thickness _dropTargetOriginalThickness;
    private IBrush? _dropTargetOriginalBorderBrush;
    private static readonly IBrush DropIndicatorBrush = new SolidColorBrush(Color.Parse(Accent));

    // ── Pre-Allocated Layer Brushes (Performance Optimization) ──────────────

    // Row Backgrounds
    private static readonly IBrush RowBgActive = new SolidColorBrush(Color.Parse(Accent));
    private static readonly IBrush RowBgSelected = new SolidColorBrush(Color.Parse("#2f3a48"));
    private static readonly IBrush RowBgDefault = new SolidColorBrush(Color.Parse(Bg2));

    // Row Borders
    private static readonly IBrush RowBorderActive = new SolidColorBrush(Color.Parse(Accent));
    private static readonly IBrush RowBorderSelected = new SolidColorBrush(Color.Parse("#485566"));
    private static readonly IBrush RowBorderDefault = new SolidColorBrush(Color.Parse(Stroke));

    // Text Colors
    private static readonly IBrush TextFgActive = new SolidColorBrush(Color.Parse(TextPrimary));
    private static readonly IBrush TextFgSelected = new SolidColorBrush(Color.Parse(TextPrimary));
    private static readonly IBrush TextFgDefault = new SolidColorBrush(Color.Parse(TextSecondary));

    private static readonly IBrush TextDimActive = new SolidColorBrush(Color.Parse("#9fb6d6"));
    private static readonly IBrush TextDimSelected = new SolidColorBrush(Color.Parse(TextSecondary));
    private static readonly IBrush TextDimDefault = new SolidColorBrush(Color.Parse(TextMuted));

    // Icon Colors
    private static readonly IBrush IconOff = new SolidColorBrush(Color.Parse("#5b5b5b"));
    private static readonly IBrush IconVisOn = new SolidColorBrush(Color.Parse("#8aa6cc"));
    private static readonly IBrush GroupIconFg = new SolidColorBrush(Color.Parse(TextMuted));
    private static readonly IBrush GroupFolderIcon = new SolidColorBrush(Color.Parse("#8aa3c4"));
    private static readonly IBrush GroupFolderIconActive = new SolidColorBrush(Color.Parse("#c5d8f0"));
    private static readonly IBrush GroupPreviewBg = new SolidColorBrush(Color.Parse("#263040"));
    private static readonly IBrush GroupPreviewBgActive = new SolidColorBrush(Color.Parse("#334158"));

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
    private Control BuildLayersSection()
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
            if (_syncingLayerUi) return;
            if (_blendModeComboBox.SelectedItem is BlendMode mode)
                _canvas.SetActiveLayerBlendMode(mode);
        };

        // Opacity — Krita-style: live preview while dragging, one undo step on release.
        _layerOpacitySlider = MkSlider(0, 1, 1, "Layer opacity");
        _layerOpacitySlider.PointerPressed += (_, _) =>
        {
            if (_syncingLayerUi) return;
            _canvas.BeginActiveLayerOpacityScrub();
            _layerOpacityScrubActive = true;
        };
        _layerOpacitySlider.PointerReleased += (_, _) => CommitLayerOpacityScrubIfActive();
        _layerOpacitySlider.PointerCaptureLost += (_, _) => CommitLayerOpacityScrubIfActive();
        _layerOpacitySlider.PropertyChanged += (_, e) =>
        {
            if (_syncingLayerUi || e.Property != Slider.ValueProperty) return;
            if (_layerOpacityScrubActive)
                _canvas.PreviewActiveLayerOpacity(_layerOpacitySlider.Value);
            else
                _canvas.SetActiveLayerOpacity(_layerOpacitySlider.Value);
        };

        // Blend + Opacity row
        var blendOpRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        Grid.SetColumn(_blendModeComboBox, 0);
        Grid.SetColumn(_layerOpacitySlider, 2);
        blendOpRow.Children.Add(_blendModeComboBox);
        blendOpRow.Children.Add(_layerOpacitySlider);

        // ── Layer panel hamburger menu (CSP-style) ──────────────────────────────
        var layerMenuBtn = SmIconBtn(Icons.DotsVertical, "Layer menu");
        layerMenuBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var layer = _canvas.Document.ActiveLayer;
            var index = _canvas.ActiveLayerIndex;
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

        // ── Layer property toggles (row 3 — above list, like CSP) ───────────────
        _lockLayerBtn = SmIconBtn(Icons.LockOutline, "Lock layer");
        _alphaLockLayerBtn = SmIconBtn(Icons.AlphaLock, "Alpha lock");
        _clipLayerBtn = SmIconBtn(Icons.ClipToBelow, "Clipping mask");
        _maskLayerBtn = SmIconBtn(Icons.RectangleOutline, "Layer mask");
        _refLayerBtn = SmIconBtn(Icons.Eye, "Reference layer");

        _lockLayerBtn.Click += (_, _) => { _canvas.ToggleLayerLock(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _alphaLockLayerBtn.Click += (_, _) => { _canvas.ToggleLayerAlphaLock(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _clipLayerBtn.Click += (_, _) => { _canvas.ToggleLayerClipping(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _maskLayerBtn.Click += (_, _) => { _canvas.ToggleLayerMaskEditing(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _refLayerBtn.Click += (_, _) => { _canvas.ToggleLayerReference(_canvas.ActiveLayerIndex); BuildLayerList(); };

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

        // ── Action buttons (bottom bar — like CSP) ───────────────────────────────
        var addBtn = SmIconBtn(Icons.LayerPlus, "Add layer  (Ctrl+Shift+N)");
        var folderBtn = SmIconBtn(Icons.Folder, "Add layer folder");
        var dupBtn = SmIconBtn(Icons.ContentCopy, "Duplicate  (Ctrl+J)");
        _deleteLayerButton = SmIconBtn(Icons.DeleteOutline, "Delete  (Ctrl+Delete)");
        _moveLayerUpButton = SmIconBtn(Icons.ArrowUp, "Move up  (Ctrl+Up)");
        _moveLayerDownButton = SmIconBtn(Icons.ArrowDown, "Move down  (Ctrl+Down)");

        addBtn.Click += (_, _) => _canvas.AddLayer();
        folderBtn.Click += (_, _) => _canvas.AddGroupLayer();
        dupBtn.Click += (_, _) => _canvas.DuplicateLayer();
        _deleteLayerButton.Click += (_, _) => DeleteSelectedLayers();
        _moveLayerUpButton.Click += (_, _) => _canvas.MoveActiveLayer(1);
        _moveLayerDownButton.Click += (_, _) => _canvas.MoveActiveLayer(-1);

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
            _canvas.SetActiveLayerName(name);
    }

    // ── Rect-pick: expand folders containing found layers, multi-select, scroll ─
    private void ExpandAndScrollToLayers(IReadOnlyList<int> foundIndices)
    {
        if (foundIndices.Count == 0) return;
        var layers = _canvas.Layers;

        // Expand every ancestor group that contains a found layer.
        foreach (var idx in foundIndices)
        {
            for (var parent = layers[idx].Parent; parent != null; parent = parent.Parent)
            {
                if (!parent.IsOpen)
                    _canvas.ToggleLayerOpen(LayerIndexOf(parent));
            }
        }

        // Select all found layers; activate the topmost one (highest index = visually on top).
        _selectedLayerIndices.Clear();
        foreach (var idx in foundIndices) _selectedLayerIndices.Add(idx);
        _canvas.SelectLayer(foundIndices[0]); // foundIndices[0] is already highest index (top-down search)

        BuildLayerList();

        // Scroll the list to the active layer's row.
        var scrollTarget = layers[foundIndices[0]];
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _layerListBox.ScrollIntoView(scrollTarget);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void PruneLayerSelection()
    {
        var max = _canvas.Layers.Count;
        _selectedLayerIndices.RemoveWhere(i => i < 0 || i >= max);
        var active = _canvas.ActiveLayerIndex;
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
        return _canvas.ActiveLayerIndex >= 0 ? [_canvas.ActiveLayerIndex] : Array.Empty<int>();
    }

    private bool CanDeleteSelectedLayers() =>
        _canvas.CanDeleteLayer(LayerDeleteTargetIndices());

    private void DeleteSelectedLayers()
    {
        var indices = LayerDeleteTargetIndices();
        if (!_canvas.CanDeleteLayer(indices))
            return;

        _canvas.DeleteLayer(indices);
        _selectedLayerIndices.Clear();
        if (_canvas.ActiveLayerIndex >= 0)
            _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);
        BuildLayerList();
    }

    /// <summary>
    /// Sets the active layer for a menu/shortcut action without collapsing a multi-selection.
    /// </summary>
    private void FocusLayerForAction(int index)
    {
        if (index < 0 || index >= _canvas.Layers.Count)
            return;

        if (_selectedLayerIndices.Count > 1 && _selectedLayerIndices.Contains(index))
        {
            if (index != _canvas.ActiveLayerIndex)
                _canvas.SelectLayer(index);
            return;
        }

        _selectedLayerIndices.Clear();
        _selectedLayerIndices.Add(index);
        if (index != _canvas.ActiveLayerIndex)
            _canvas.SelectLayer(index);
    }

    // ── Layer panel ───────────────────────────────────────────────────────────
    private void BuildLayerList()
    {
        _layerRows.Clear();
        if (_canvas == null || _canvas.Layers == null) return; // Defense 1

        var layers = _canvas.Layers;
        var visibleIndexes = VisibleLayerIndexes().ToList();
        var layersToDisplay = visibleIndexes.Select(i => layers[i]).ToList();

        _visibleLayers.Clear();
        _visibleLayers.InsertRange(0, layersToDisplay);
        _layerListBox.SelectedIndex = -1;

        if (layers.Count > 0)
        {
            var active = layers[_canvas.ActiveLayerIndex];
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = active.Opacity;
            _blendModeComboBox.SelectedItem = active.BlendMode;
            _layerNameBox.Text = active.Name;
            _syncingLayerUi = false;
            RefreshLayerProperties();
            RefreshLayerToggleButtons(active);
        }
    }

    private void RefreshLayerToggleButtons(DrawingLayer layer)
    {
        if (_lockLayerBtn == null) return;
        SetToggleActive(_lockLayerBtn, layer.IsLocked);
        SetToggleActive(_alphaLockLayerBtn, layer.IsAlphaLocked);
        SetToggleActive(_clipLayerBtn, layer.IsClipping);
        SetToggleActive(_maskLayerBtn, layer.IsMaskEditing);
        SetToggleActive(_refLayerBtn, layer.IsReference);
    }


    private IEnumerable<int> VisibleLayerIndexes()
    {
        var layers = _canvas.Layers;
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
        var isSelected = _selectedLayerIndices.Contains(i);

        // Pre-allocated brush selections
        var dimBrush = isActive ? TextDimActive : isSelected ? TextDimSelected : TextDimDefault;
        var fgBrush = isActive ? TextFgActive : isSelected ? TextFgSelected : TextFgDefault;

        var row = new Border
        {
            Background = isActive ? RowBgActive : isSelected ? RowBgSelected : RowBgDefault,
            BorderBrush = layer.IsMaskEditing ? MaskEditBorderBrush
                : isActive ? RowBorderActive : isSelected ? RowBorderSelected : RowBorderDefault,
            BorderThickness = layer.IsMaskEditing ? new Thickness(2) : new Thickness(1),
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
        row.AddHandler(DragDrop.DragOverEvent, LayerRowDragOver);
        row.AddHandler(DragDrop.DropEvent, LayerRowDrop);

        // cols: clip-strip | disclosure | visibility | thumbnail | name/status
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,16,20,48,*") };

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
        visBtn.Click += (_, _) => _canvas.ToggleLayerVisibility((int)visBtn.Tag!);

        var (preview, previewImage) = BuildLayerPreview(layer, isActive || isSelected, layer.IsPaper ? _canvas.Document.PaperColor : null);
        preview.PointerPressed += (_, e) =>
        {
            if (layer.IsGroup && e.GetCurrentPoint(preview).Properties.IsLeftButtonPressed)
            {
                _canvas.ToggleLayerOpen(i);
                BuildLayerList();
                e.Handled = true;
            }
        };
        if (layer.IsPaper)
        {
            preview.DoubleTapped += (_, _) => ShowPaperColorPicker();
        }

        // Mask indicator overlay on the preview
        var previewHost = new Grid();
        previewHost.Children.Add(preview);
        if (layer.HasMask)
        {
            var maskBadge = new Border
            {
                Width = 14,
                Height = 14,
                Background = layer.IsMaskEditing ? MaskEditBorderBrush : MaskThumbBg,
                BorderBrush = layer.IsMaskEditing ? MaskEditBorderBrush : MaskThumbBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = "M",
                    FontSize = 8,
                    FontWeight = FontWeight.Bold,
                    Foreground = layer.IsMaskEditing ? Avalonia.Media.Brushes.White : MaskThumbText,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 1, 1)
            };
            previewHost.Children.Add(maskBadge);
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
        Grid.SetColumn(previewHost, 3);
        Grid.SetColumn(nameHost, 4);
        grid.Children.Add(clipStrip);
        grid.Children.Add(disclosureBtn);
        grid.Children.Add(visBtn);
        grid.Children.Add(previewHost);
        grid.Children.Add(nameHost);
        row.Child = grid;
        return (row, new LayerRowRefs(row, disclosureBtn, visBtn, nameHost, previewImage, clipStrip));
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

        var flags = new List<string>(4);
        if (layer.IsLocked) flags.Add("Lock");
        if (layer.IsAlphaLocked) flags.Add("Alpha");
        if (layer.IsReference) flags.Add("Ref");
        if (layer.IsClipping) flags.Add("Clip");
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
        _canvas.CommitActiveLayerOpacityScrub();
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
                _canvas.ToggleLayerOpen(idx);
                // Rebuild immediately — waiting for LayersChanged coalesce can leave
                // the tree expanded until the user clicks another layer.
                BuildLayerList();
            };
        }
        return btn;
    }

    private ContextMenu BuildLayerContextMenu(int index)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            if (index < 0 || index >= _canvas.Layers.Count) return;
            menu.ItemsSource = BuildLayerContextMenuItems(index, _canvas.Layers[index]);
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
            => Item(l.IsLocked ? "Unlock Layer" : "Lock Layer", () => _canvas.ToggleLayerLock(idx));

        var pasteItem = Item("_Paste", () => _canvas.PasteLayer(index));
        pasteItem.IsEnabled = _canvas.CanPasteLayer;

        var hasSelection = _selectedLayerIndices.Count >= 1;

        var deleteItem = Item("_Delete", DeleteSelectedLayers, new KeyGesture(Key.Delete, KeyModifiers.Control));
        deleteItem.IsEnabled = CanDeleteSelectedLayers();

        MenuItem AdjItem(string header, AdjustmentKind kind)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                FocusLayerForAction(index);
                _canvas.AddAdjustmentLayer(kind);
                OpenAdjustmentLayerDialog(_canvas.ActiveLayerIndex);
            };
            return mi;
        }

        var items = new List<MenuItem>
        {
            Item("_New Layer Above", () => _canvas.AddLayer(), new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
            Item("New _Folder Above", () => _canvas.AddGroupLayer(), new KeyGesture(Key.G, KeyModifiers.Control)),
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
            Item("_Duplicate", () => _canvas.DuplicateLayer(), new KeyGesture(Key.J, KeyModifiers.Control)),
            Item("_Copy", () => _canvas.CopyLayer(index)),
            pasteItem,
            deleteItem,
            new() { Header = "-" },
            Item(layer.IsVisible ? "Hide Layer" : "Show Layer", () => _canvas.ToggleLayerVisibility(index)),
            MakeLockItem(layer, index),
            Item(layer.IsAlphaLocked ? "Disable Alpha Lock" : "Enable Alpha Lock", () => _canvas.ToggleLayerAlphaLock(index)),
            Item(layer.IsReference ? "Disable Reference Layer" : "Enable Reference Layer", () => _canvas.ToggleLayerReference(index)),
            Item(layer.IsClipping ? "Disable Clipping Mask" : "Enable Clipping Mask", () => _canvas.ToggleLayerClipping(index)),
            Item(layer.HasMask ? (layer.IsMaskEditing ? "Exit Mask Edit" : "Edit Mask") : "Create Mask", () => _canvas.ToggleLayerMaskEditing(index)),
            Item(layer.IsMaskVisible ? "Disable Mask" : "Enable Mask", () => _canvas.ToggleLayerMask(index)),
            Item("Delete Mask", () => _canvas.DeleteLayerMask(index)),
            Item("Apply Mask", () => _canvas.ApplyLayerMask(index))
        };

        if (layer.IsPaper)
        {
            // Insert "Paper Color..." after visibility toggle (index 8 in base list)
            items.Insert(9, Item("Paper _Color...", ShowPaperColorPicker));
        }

        if (hasSelection)
        {
            items.Insert(2, Item("Create Folder and Insert Layers", () =>
            {
                var sorted = _selectedLayerIndices.OrderBy(i => i).ToList();
                _canvas.GroupSelectedLayers(sorted);
                _selectedLayerIndices.Clear();
                _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);
                BuildLayerList();
            }, new KeyGesture(Key.G, KeyModifiers.Control)));
        }

        if (layer.IsGroup)
        {
            items.Insert(4, Item(layer.IsOpen ? "Collapse Folder" : "Expand Folder", () =>
            {
                _canvas.ToggleLayerOpen(index);
                BuildLayerList();
            }));
            items.Insert(5, Item("Flatten Folder", () => _canvas.FlattenGroup(index)));
        }
        else if (_selectedLayerIndices.Count > 1)
        {
            items.Insert(4, Item(
                $"Merge {_selectedLayerIndices.Count} Selected Layers",
                () => _canvas.MergeSelectedLayers(_selectedLayerIndices.OrderBy(x => x).ToList())));
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
                BuildLayerList();
            };
            items.Add(selectAll);
        }
        if (_selectedLayerIndices.Count > 1)
        {
            var deselectAll = new MenuItem { Header = "Deselect All" };
            deselectAll.Click += (_, _) => { _selectedLayerIndices.Clear(); BuildLayerList(); };
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
                        AsyncItem("Brightness / Contrast...", ApplyBrightnessContrastFilter),
                        AsyncItem("Exposure / Gamma...", ApplyExposureGammaFilter),
                        AsyncItem("Levels...", ApplyLevelsFilter),
                        AsyncItem("Hue / Saturation...", ApplyHueSaturationFilter),
                        AsyncItem("Color Curves...", ApplyColorCurvesFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Color",
                    ItemsSource = new object[]
                    {
                        InstantFilterItem("Invert", ApplyInvertFilter),
                        InstantFilterItem("Desaturate", ApplyDesaturateFilter),
                        AsyncItem("Sepia...", ApplySepiaFilter),
                        AsyncItem("Threshold...", ApplyThresholdFilter),
                        AsyncItem("Posterize...", ApplyPosterizeFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Blur / Enhance",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Gaussian Blur...", ApplyBlurFilter),
                        AsyncItem("Motion Blur...", ApplyMotionBlurFilter),
                        new Separator(),
                        AsyncItem("Sharpen...", ApplySharpenFilter),
                        AsyncItem("Bloom...", ApplyBloomFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Stylize",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Pixelate...", ApplyPixelateFilter),
                        AsyncItem("Vignette...", ApplyVignetteFilter),
                        AsyncItem("Emboss...", ApplyEmbossFilter),
                        AsyncItem("Find Edges...", ApplyEdgeDetectFilter),
                        AsyncItem("Chromatic Aberration...", ApplyChromaticAberrationFilter),
                        AsyncItem("Noise...", ApplyNoiseFilter),
                    }
                },
                new MenuItem
                {
                    Header = "Cleanup",
                    ItemsSource = new object[]
                    {
                        AsyncItem("Remove Dust...", ApplyRemoveDustFilter),
                    }
                },
            }
        });

        return items;
    }

    private void SelectLayerWithModifiers(int index, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Control))
        {
            var wasSelected = _selectedLayerIndices.Contains(index);
            var saved = _selectedLayerIndices.ToList();
            _canvas.SelectLayer(index);
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
            var active = _canvas.ActiveLayerIndex;
            _canvas.SelectLayer(index);
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
            _canvas.SelectLayer(index);
        }
        BuildLayerList();
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
                    _canvas.SelectLayer(index);
                    BuildLayerList();
                }
                return;
            }

            // Ctrl+click always toggles selection (even on already-selected layers).
            // For plain click on an already-selected layer, skip SelectLayerWithModifiers
            // so the multi-selection persists through the pending drag gesture.
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (ctrl || !alreadySelected)
                SelectLayerWithModifiers(index, e.KeyModifiers);

            // Don't start a drag when modifier keys are held (Ctrl=toggle, Shift=range).
            if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.ClickCount > 1) return;

            // Store pending drag state; actual drag starts in PointerMoved after threshold exceeded.
            _pendingDragIndex = index;
            _pendingDragStartPos = point.Position;
            _pendingDragArgs = e;
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
        }
    }

    private void LayerRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pendingDragIndex = -1;
        _pendingDragArgs = null;
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
        _canvas.SelectLayer(index);
        var layers = _canvas.Layers;
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
            ClearDropIndicator();
            return;
        }

        var sourceIndices = GetDraggedLayerIndices(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));

        e.DragEffects = sourceIndices.Count > 0 && sourceIndices.All(si => _canvas.CanMoveLayer(si, targetIndex, placement))
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
        if (sender is not Border row || row.Tag is not int targetIndex) return;
        var sourceIndices = GetDraggedLayerIndices(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));
        if (sourceIndices.Count == 0) return;

        var layerCount = _canvas.Layers.Count;
        // Drag data captures indices at drag-start time; by drop time the layer
        // list may have shifted (undo, auto-documents, etc.). Filter stale indices.
        sourceIndices = sourceIndices.Where(si => si >= 0 && si < layerCount).ToList();
        if (sourceIndices.Count == 0) return;
        if (targetIndex < 0 || targetIndex >= layerCount) return;

        var layersToMove = sourceIndices
            .Select(si => _canvas.Layers[si])
            .ToList();
        if (placement == LayerDropPlacement.Above)
            layersToMove.Reverse();
        var targetLayer = _canvas.Layers[targetIndex];
        foreach (var layer in layersToMove)
        {
            var currentSource = -1;
            var currentTarget = -1;
            for (var i = 0; i < _canvas.Layers.Count; i++)
            {
                if (_canvas.Layers[i] == layer) currentSource = i;
                if (_canvas.Layers[i] == targetLayer) currentTarget = i;
            }
            if (currentSource < 0 || currentTarget < 0 || currentSource == currentTarget) continue;
            _canvas.MoveLayer(currentSource, currentTarget, placement);
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
        var height = Math.Max(1, row.Bounds.Height);
        var target = _canvas.Layers[targetIndex];

        // Top 30% → insert above this row; bottom 30% → insert below.
        // Middle 40% → into group (if it's a group), otherwise split at 50%.
        if (position.Y < height * 0.30)
            return LayerDropPlacement.Above;
        if (position.Y > height * 0.70)
            return LayerDropPlacement.Below;
        if (target.IsGroup)
            return LayerDropPlacement.Into;
        return position.Y < height * 0.5 ? LayerDropPlacement.Above : LayerDropPlacement.Below;
    }

    private void UpdateDropIndicator(Border row, LayerDropPlacement placement)
    {
        // Clear previous indicator on a different row
        if (_dropTargetRow != null && !ReferenceEquals(_dropTargetRow, row))
            ClearDropIndicator();

        if (placement == LayerDropPlacement.Into)
        {
            // Highlight the row border for "into group"
            if (!ReferenceEquals(_dropTargetRow, row))
            {
                _dropTargetRow = row;
                _dropTargetOriginalThickness = row.BorderThickness;
                _dropTargetOriginalBorderBrush = row.BorderBrush;
                row.BorderThickness = new Thickness(2);
                row.BorderBrush = DropIndicatorBrush;
            }
            if (_dropLine is not null)
                _dropLine.IsVisible = false;
        }
        else
        {
            // Restore row border if we previously highlighted for Into
            if (_dropTargetRow != null)
                ClearDropIndicator();

            // Position floating insertion line based on Above/Below
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

    private void UpdateLayerRow(int index)
    {
        var layers = _canvas.Layers;
        if (index < 0 || index >= layers.Count || !_layerRows.TryGetValue(index, out var refs))
        {
            // The row is scrolled off-screen and recycled. We don't need to update its visuals.
            return;
        }

        var layer = layers[index];
        var isActive = index == _canvas.ActiveLayerIndex;
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

        if (isActive && !_syncingLayerUi)
        {
            _syncingLayerUi = true;
            _layerOpacitySlider.Value = layer.Opacity;
            _blendModeComboBox.SelectedItem = layer.BlendMode;
            _layerNameBox.Text = layer.Name;
            _syncingLayerUi = false;
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
        var layers = _canvas.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return;
        var layer = layers[layerIndex];
        if (layer.Adjustment == null) return;

        var snapshot = layer.Adjustment.Clone();
        var dlg = new Floss.App.Windows.AdjustmentLayerDialog(
            layer.Adjustment,
            preview => _canvas.PreviewLayerAdjustmentParams(layerIndex, preview));

        await dlg.ShowDialog(this);

        if (dlg.Result != null)
            _canvas.SetLayerAdjustmentParams(layerIndex, dlg.Result);
        else
            _canvas.PreviewLayerAdjustmentParams(layerIndex, snapshot);
    }

    private sealed record LayerRowRefs(
        Border Row,
        Button DisclosureButton,
        Button VisibilityButton,
        ContentControl NameHost,
        Image? PreviewImage,
        Border ClipStrip);
}
