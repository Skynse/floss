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
using Floss.App.Document;
using Avalonia.Controls.Templates;

namespace Floss.App;

using static Floss.App.AppColors;

public partial class MainWindow
{

    private readonly Avalonia.Collections.AvaloniaList<DrawingLayer> _visibleLayers = new();
    private ListBox _layerListBox = null!;

    // ── Pre-Allocated Layer Brushes (Performance Optimization) ──────────────

    // Row Backgrounds
    private static readonly IBrush RowBgActive = new SolidColorBrush(Color.Parse("#1a2a50"));
    private static readonly IBrush RowBgSelected = new SolidColorBrush(Color.Parse("#141e38"));
    private static readonly IBrush RowBgDefault = new SolidColorBrush(Color.Parse("#16181f"));

    // Row Borders
    private static readonly IBrush RowBorderActive = new SolidColorBrush(Color.Parse("#2e5fb8"));
    private static readonly IBrush RowBorderSelected = new SolidColorBrush(Color.Parse("#2a4a88"));
    private static readonly IBrush RowBorderDefault = new SolidColorBrush(Color.Parse("#1e2128"));

    // Text Colors
    private static readonly IBrush TextFgActive = new SolidColorBrush(Color.Parse("#d8e0f0"));
    private static readonly IBrush TextFgSelected = new SolidColorBrush(Color.Parse("#a8c0e0"));
    private static readonly IBrush TextFgDefault = new SolidColorBrush(Color.Parse("#7a8494"));

    private static readonly IBrush TextDimActive = new SolidColorBrush(Color.Parse("#5a80c8"));
    private static readonly IBrush TextDimSelected = new SolidColorBrush(Color.Parse("#4a6090"));
    private static readonly IBrush TextDimDefault = new SolidColorBrush(Color.Parse("#383d47"));

    // Icon Colors
    private static readonly IBrush IconOff = new SolidColorBrush(Color.Parse("#404550"));
    private static readonly IBrush IconVisOn = new SolidColorBrush(Color.Parse("#6a9fd8"));
    private static readonly IBrush GroupIconFg = new SolidColorBrush(Color.Parse("#6a7a96"));
    private static readonly IBrush IconStandard = new SolidColorBrush(Color.Parse("#30343d"));

    // Miscellaneous UI Elements
    private static readonly IBrush PreviewFrameBg = new SolidColorBrush(Color.Parse("#1e2028"));
    private static readonly IBrush PreviewFrameBorder = new SolidColorBrush(Color.Parse("#2e3340"));
    private static readonly IBrush SwatchBorder = new SolidColorBrush(Color.Parse("#3a4050"));

    // Clipping strip
    private static readonly IBrush ClipIndicatorBrush = new SolidColorBrush(Color.Parse("#e8527a"));

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
            Height = 22,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a4250")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0),
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
            SelectedItem = "Normal",
            FontSize = 11,
            Padding = new Thickness(5, 0),
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
        _refLayerBtn = SmIconBtn(Icons.Eye, "Reference layer");

        _lockLayerBtn.Click += (_, _) => { _canvas.ToggleLayerLock(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _alphaLockLayerBtn.Click += (_, _) => { _canvas.ToggleLayerAlphaLock(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _clipLayerBtn.Click += (_, _) => { _canvas.ToggleLayerClipping(_canvas.ActiveLayerIndex); BuildLayerList(); };
        _refLayerBtn.Click += (_, _) => { _canvas.ToggleLayerReference(_canvas.ActiveLayerIndex); BuildLayerList(); };

        foreach (var btn in new[] { _lockLayerBtn, _alphaLockLayerBtn, _clipLayerBtn, _refLayerBtn })
            btn.Margin = new Thickness(0, 0, 2, 0);

        var toggleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
            Children = { _lockLayerBtn, _alphaLockLayerBtn, _clipLayerBtn, _refLayerBtn }
        };

        // 1. Initialize the Virtualized ListBox
        _layerListBox = new ListBox
        {
            ItemsSource = _visibleLayers,
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

        // ── Action buttons (bottom bar — like CSP) ───────────────────────────────
        var addBtn = SmIconBtn(Icons.LayerPlus, "Add layer  (Ctrl+Shift+N)");
        var folderBtn = SmIconBtn(Icons.FolderOpenOutline, "Add layer folder");
        var dupBtn = SmIconBtn(Icons.ContentCopy, "Duplicate  (Ctrl+J)");
        _deleteLayerButton = SmIconBtn(Icons.DeleteOutline, "Delete  (Ctrl+Delete)");
        _moveLayerUpButton = SmIconBtn(Icons.ArrowUp, "Move up  (Ctrl+Up)");
        _moveLayerDownButton = SmIconBtn(Icons.ArrowDown, "Move down  (Ctrl+Down)");

        addBtn.Click += (_, _) => _canvas.AddLayer();
        folderBtn.Click += (_, _) => _canvas.AddGroupLayer();
        dupBtn.Click += (_, _) => _canvas.DuplicateLayer();
        _deleteLayerButton.Click += (_, _) => _canvas.DeleteLayer();
        _moveLayerUpButton.Click += (_, _) => _canvas.MoveActiveLayer(1);
        _moveLayerDownButton.Click += (_, _) => _canvas.MoveActiveLayer(-1);

        foreach (var btn in new Button[] { addBtn, folderBtn, dupBtn, _deleteLayerButton, _moveLayerUpButton, _moveLayerDownButton })
            btn.Margin = new Thickness(0, 0, 2, 0);

        var ctrlRow = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
            Children = { addBtn, folderBtn, dupBtn, _moveLayerUpButton, _moveLayerDownButton, _deleteLayerButton }
        };

        // Grid: name | blend+opacity | toggles | list(*) | actions
        var mainGrid = new Grid
        {
            Margin = new Thickness(6, 3, 6, 6),
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *, Auto")
        };

        Grid.SetRow(nameRow, 0);
        Grid.SetRow(blendOpRow, 1);
        Grid.SetRow(toggleRow, 2);
        Grid.SetRow(_layerListBox, 3);
        Grid.SetRow(ctrlRow, 4);

        blendOpRow.Margin = new Thickness(0, 3, 0, 0);

        mainGrid.Children.Add(nameRow);
        mainGrid.Children.Add(blendOpRow);
        mainGrid.Children.Add(toggleRow);
        mainGrid.Children.Add(_layerListBox);
        mainGrid.Children.Add(ctrlRow);

        // Cache layer panel — complex but mostly static between layer changes
        mainGrid.CacheMode = new Avalonia.Media.BitmapCache();

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
        _lockLayerBtn.IsEnabled = !layer.IsPaper;
        SetToggleActive(_lockLayerBtn, layer.IsLocked);
        SetToggleActive(_alphaLockLayerBtn, layer.IsAlphaLocked);
        SetToggleActive(_clipLayerBtn, layer.IsClipping);
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
            BorderBrush = isActive ? RowBorderActive : isSelected ? RowBorderSelected : RowBorderDefault,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2, 1),
            Margin = new Thickness(layer.IndentLevel * 10, 0, 0, 0),
            Tag = i,
            ContextMenu = BuildLayerContextMenu(i)
        };

        DragDrop.SetAllowDrop(row, true);
        row.PointerPressed += LayerRowPointerPressed;
        row.Tapped += LayerRowTapped;
        row.DoubleTapped += LayerRowDoubleTapped;
        row.AddHandler(DragDrop.DragOverEvent, LayerRowDragOver);
        row.AddHandler(DragDrop.DropEvent, LayerRowDrop);

        // cols: clip-strip | disclosure | visibility | thumbnail | name/status
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,12,16,28,*") };

        // Thin pink strip on the left edge to signal "clipped to layer below"
        var clipStrip = new Border
        {
            Background = layer.IsClipping ? ClipIndicatorBrush : Avalonia.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };

        var disclosureBtn = LayerDisclosureBtn(layer, i);

        var visBtn = LayerIconBtn(
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            "Toggle visibility",
            layer.IsVisible ? IconVisOn : IconOff, i);
        visBtn.Click += (_, _) => _canvas.ToggleLayerVisibility((int)visBtn.Tag!);

        var (preview, previewImage) = BuildLayerPreview(layer, layer.IsPaper ? _canvas.Document.PaperColor : null);
        preview.PointerPressed += (_, e) =>
        {
            if (layer.IsGroup && e.GetCurrentPoint(preview).Properties.IsLeftButtonPressed)
            {
                _canvas.ToggleLayerOpen(i);
                e.Handled = true;
            }
        };
        if (layer.IsPaper)
        {
            preview.DoubleTapped += (_, _) => ShowPaperColorPicker();
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
        Grid.SetColumn(preview, 3);
        Grid.SetColumn(nameHost, 4);
        grid.Children.Add(clipStrip);
        grid.Children.Add(disclosureBtn);
        grid.Children.Add(visBtn);
        grid.Children.Add(preview);
        grid.Children.Add(nameHost);
        row.Child = grid;
        return (row, new LayerRowRefs(row, disclosureBtn, visBtn, nameHost, previewImage, clipStrip));
    }

    private static string LayerStatusText(DrawingLayer layer)
    {
        var flags = new List<string>(4);
        if (layer.IsLocked) flags.Add("Lock");
        if (layer.IsAlphaLocked) flags.Add("Alpha");
        if (layer.IsReference) flags.Add("Ref");
        if (layer.IsClipping) flags.Add("Clip");
        if (layer.IsPaper) flags.Add("Paper");
        var suffix = flags.Count == 0 ? "" : "  " + string.Join(" ", flags);
        return $"{Math.Round(layer.Opacity * 100):0}%  {layer.BlendMode}{suffix}";
    }

    private Button LayerDisclosureBtn(DrawingLayer layer, int index)
    {
        var btn = new Button
        {
            Content = layer.IsGroup ? Icons.Make(layer.IsOpen ? Icons.ArrowDown : Icons.ChevronRight, 10, layer.IsGroup ? GroupIconFg : IconStandard) : null,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            Foreground = layer.IsGroup ? GroupIconFg : IconStandard,
            Padding = new Thickness(0),
            Tag = index,
            IsHitTestVisible = layer.IsGroup
        };
        if (layer.IsGroup)
            btn.Click += (_, _) => _canvas.ToggleLayerOpen((int)btn.Tag!);
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
                if (index >= 0 && index < _canvas.Layers.Count)
                    _canvas.SelectLayer(index);
                action();
            };
            return item;
        }

        MenuItem MakeLockItem(DrawingLayer l, int idx)
        {
            var lockItem = Item(l.IsLocked ? "Unlock Layer" : "Lock Layer", () => _canvas.ToggleLayerLock(idx));
            lockItem.IsEnabled = !l.IsPaper;
            return lockItem;
        }

        var pasteItem = Item("_Paste", () => _canvas.PasteLayer(index));
        pasteItem.IsEnabled = _canvas.CanPasteLayer;

        var multiSelected = _selectedLayerIndices.Count > 1;
        var hasSelection = _selectedLayerIndices.Count >= 1;

        var items = new List<MenuItem>
        {
            Item("_New Layer Above", () => _canvas.AddLayer(), new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
            Item("New _Folder Above", () => _canvas.AddGroupLayer(), new KeyGesture(Key.G, KeyModifiers.Control)),
            Item("_Duplicate", () => _canvas.DuplicateLayer(), new KeyGesture(Key.J, KeyModifiers.Control)),
            Item("_Copy", () => _canvas.CopyLayer(index)),
            pasteItem,
            Item("_Delete", () => _canvas.DeleteLayer(), new KeyGesture(Key.Delete, KeyModifiers.Control)),
            new() { Header = "-" },
            Item(layer.IsVisible ? "Hide Layer" : "Show Layer", () => _canvas.ToggleLayerVisibility(index)),
            MakeLockItem(layer, index),
            Item(layer.IsAlphaLocked ? "Disable Alpha Lock" : "Enable Alpha Lock", () => _canvas.ToggleLayerAlphaLock(index)),
            Item(layer.IsReference ? "Disable Reference Layer" : "Enable Reference Layer", () => _canvas.ToggleLayerReference(index)),
            Item(layer.IsClipping ? "Disable Clipping Mask" : "Enable Clipping Mask", () => _canvas.ToggleLayerClipping(index))
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
            items.Insert(4, Item(layer.IsOpen ? "Collapse Folder" : "Expand Folder", () => _canvas.ToggleLayerOpen(index)));
            items.Insert(5, Item("Flatten Folder", () => _canvas.FlattenGroup(index)));
        }
        else
        {
            items.Insert(multiSelected ? 5 : 4, Item(
                multiSelected ? $"Merge {_selectedLayerIndices.Count} Selected Layers" : "Merge Down",
                () =>
                {
                    if (multiSelected)
                        _canvas.MergeSelectedLayers(_selectedLayerIndices.OrderBy(x => x).ToList());
                    else
                        _canvas.MergeDown();
                },
                new KeyGesture(Key.E, KeyModifiers.Control)));
        }

        var visibleCount = VisibleLayerIndexes().Count();
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
            mi.Click += async (_, _) => await action();
            return mi;
        }
        items.Add(new() { Header = "-" });
        items.Add(new MenuItem
        {
            Header = "Filters",
            ItemsSource = new object[]
            {
                AsyncItem("_Gaussian Blur...", ApplyBlurFilter),
                AsyncItem("_Sharpen...",       ApplySharpenFilter),
                AsyncItem("_Noise...",         ApplyNoiseFilter),
                new Separator(),
                AsyncItem("_Color Curves...",  ApplyColorCurvesFilter),
                AsyncItem("Chromatic _Aberration...", ApplyChromaticAberrationFilter),
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

    private async void LayerRowPointerPressed(object? sender, PointerPressedEventArgs e)
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
                // Right-click: update selection without clearing it, then let the
                // context menu open. Do NOT start a drag.
                if (!alreadySelected)
                {
                _selectedLayerIndices.Add(index);
                BuildLayerList();
            }
            _canvas.SelectLayer(index);
            e.Handled = true;
            return;
        }

        SelectLayerWithModifiers(index, e.KeyModifiers);

        if (e.ClickCount > 1) return;
        _layerDragSourceIndex = index;

        // Include all selected layers in the drag payload so multi-select works
        var draggedIndices = _selectedLayerIndices.Contains(index)
            ? _selectedLayerIndices.OrderBy(i => i).ToList()
            : new List<int> { index };

        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(string.Join(",", draggedIndices));
        data.Add(item);
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        _layerDragSourceIndex = -1;
        }
        catch (Exception ex) { CrashLog.Write(ex, "MainWindow.LayerRowPointerPressed"); }
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

        var sourceIndices = GetDraggedLayerIndices(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));

        // Allow if every selected layer can move to the target
        e.DragEffects = sourceIndices.Count > 0 && sourceIndices.All(si => _canvas.CanMoveLayer(si, targetIndex, placement))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void LayerRowDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Border row || row.Tag is not int targetIndex) return;
        var sourceIndices = GetDraggedLayerIndices(e.DataTransfer);
        var placement = GetLayerDropPlacement(row, targetIndex, e.GetPosition(row));
        if (sourceIndices.Count == 0) return;

        // Move all selected layers to the target position.
        // We re-resolve layer indices after each move since the flat list shifts.
        var layersToMove = sourceIndices.Select(si => _canvas.Layers[si]).ToList();
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
        if (target.IsGroup && position.Y > height * 0.25 && position.Y < height * 0.75)
            return LayerDropPlacement.Into;
        return position.Y < height * 0.5 ? LayerDropPlacement.Above : LayerDropPlacement.Below;
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
        refs.Row.BorderBrush = isActive ? RowBorderActive : isSelected ? RowBorderSelected : RowBorderDefault;
        refs.ClipStrip.Background = layer.IsClipping ? ClipIndicatorBrush : Avalonia.Media.Brushes.Transparent;

        SetLayerIconBtnIcon(refs.VisibilityButton,
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            layer.IsVisible ? IconVisOn : IconOff);

        refs.DisclosureButton.Content = layer.IsGroup ? Icons.Make(layer.IsOpen ? Icons.ArrowDown : Icons.ChevronRight, 10, GroupIconFg) : null;
        refs.DisclosureButton.IsHitTestVisible = layer.IsGroup;
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

    private static (Control Frame, Image? PreviewImage) BuildLayerPreview(DrawingLayer layer, Avalonia.Media.Color? paperColor = null)
    {
        var frame = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(1, 0, 1, 0),
            Background = PreviewFrameBg,
            BorderBrush = PreviewFrameBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true
        };

        if (layer.IsGroup)
        {
            frame.Child = Icons.Make(layer.IsOpen ? Icons.ArrowDown : Icons.ChevronRight, 12, GroupIconFg);
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
            var docIcon = Icons.Make(Icons.PaperDocument, 14,
                new SolidColorBrush(pc.R + pc.G + pc.B > 384 ? Colors.Black : Colors.White));
            docIcon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            docIcon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            swatchGrid.Children.Add(docIcon);
            frame.Child = swatchGrid;
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
        ContentControl NameHost,
        Image? PreviewImage,
        Border ClipStrip);
}
