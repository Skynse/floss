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
    private static readonly IBrush IconLockOn = new SolidColorBrush(Color.Parse("#c89050"));
    private static readonly IBrush IconAlphaOn = new SolidColorBrush(Color.Parse("#6ab8c8"));
    private static readonly IBrush IconClipOn = new SolidColorBrush(Color.Parse("#a87ad8"));

    private static readonly IBrush GroupIconFg = new SolidColorBrush(Color.Parse("#6a7a96"));
    private static readonly IBrush IconStandard = new SolidColorBrush(Color.Parse("#30343d"));

    // Miscellaneous UI Elements
    private static readonly IBrush PreviewFrameBg = new SolidColorBrush(Color.Parse("#1e2028"));
    private static readonly IBrush PreviewFrameBorder = new SolidColorBrush(Color.Parse("#2e3340"));
    private static readonly IBrush SwatchBorder = new SolidColorBrush(Color.Parse("#3a4050"));

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

        // 1. Initialize the Virtualized ListBox
        _layerListBox = new ListBox
        {
            ItemsSource = _visibleLayers,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // Strip out the ugly default hover/selection styles of the ListBoxItem
            Styles =
            {
                new Style(x => x.OfType<ListBoxItem>())
                {
                    Setters =
                    {
                        // FIXED: Use ListBoxItem instead of Control for these properties
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

                if (i >= 0)
                {
                    _layerRows[i] = refs;
                }

                return row;
            })
        };

        // 2. Constrained Grid for Virtualization
        var mainGrid = new Grid
        {
            Margin = new Thickness(8, 4, 8, 8),

            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *")
        };

        Grid.SetRow(_layerNameBox, 0);
        Grid.SetRow(blendOpRow, 1);
        Grid.SetRow(ctrlRow, 2);
        Grid.SetRow(_layerListBox, 3);

        blendOpRow.Margin = new Thickness(0, 4, 0, 0);
        ctrlRow.Margin = new Thickness(0, 4, 0, 4);

        mainGrid.Children.Add(_layerNameBox);
        mainGrid.Children.Add(blendOpRow);
        mainGrid.Children.Add(ctrlRow);
        mainGrid.Children.Add(_layerListBox);

        return mainGrid;
    }
    private void ApplyLayerName()
    {
        var name = _layerNameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            _canvas.SetActiveLayerName(name);
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
        }
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
            layer.IsVisible ? IconVisOn : IconOff, i);
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
            layer.IsLocked ? IconLockOn : IconOff, i);
        lockBtn.Click += (_, _) => _canvas.ToggleLayerLock((int)lockBtn.Tag!);

        var alphaLockBtn = LayerIconBtn(
            Icons.AlphaLock,
            "Toggle alpha lock",
            layer.IsAlphaLocked ? IconAlphaOn : IconOff, i);
        alphaLockBtn.Click += (_, _) => _canvas.ToggleLayerAlphaLock((int)alphaLockBtn.Tag!);

        var clipBtn = LayerIconBtn(
            Icons.ClipToBelow,
            "Toggle clipping mask",
            layer.IsClipping ? IconClipOn : IconOff, i);
        clipBtn.Click += (_, _) => _canvas.ToggleLayerClipping((int)clipBtn.Tag!);

        var nameText = new TextBlock
        {
            Text = layer.Name,
            Foreground = fgBrush,
            Padding = new Thickness(2, 0),
            FontSize = 11,

            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            //TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
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
            Foreground = dimBrush,
            FontSize = 9,
            FontFamily = MonospaceFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 2, 0),
            IsHitTestVisible = false
        };

        var opacityText = new TextBlock
        {
            Text = $"{Math.Round(layer.Opacity * 100):0}%",
            Foreground = dimBrush,
            FontSize = 9,
            FontFamily = MonospaceFont,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 2, 0),
            IsHitTestVisible = false
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
            Foreground = layer.IsGroup ? GroupIconFg : IconStandard,
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

        var pasteItem = Item("_Paste", () => _canvas.PasteLayer(index));
        pasteItem.IsEnabled = _canvas.CanPasteLayer;

        var items = new List<MenuItem>
        {
            Item("_New Layer Above", () => _canvas.AddLayer()),
            Item("New _Folder Above", () => _canvas.AddGroupLayer()),
            Item("_Duplicate", () => _canvas.DuplicateLayer()),
            Item("_Copy", () => _canvas.CopyLayer(index)),
            pasteItem,
            Item("_Delete", () => _canvas.DeleteLayer()),
            new() { Header = "-" },
            Item(layer.IsVisible ? "Hide Layer" : "Show Layer", () => _canvas.ToggleLayerVisibility(index)),
            Item(layer.IsLocked ? "Unlock Layer" : "Lock Layer", () => _canvas.ToggleLayerLock(index)),
            Item(layer.IsAlphaLocked ? "Disable Alpha Lock" : "Enable Alpha Lock", () => _canvas.ToggleLayerAlphaLock(index)),
            Item(layer.IsClipping ? "Disable Clipping Mask" : "Enable Clipping Mask", () => _canvas.ToggleLayerClipping(index))
        };

        if (layer.IsGroup)
        {
            items.Insert(4, Item(layer.IsOpen ? "Collapse Folder" : "Expand Folder", () => _canvas.ToggleLayerOpen(index)));
            items.Insert(5, Item("Flatten Folder", () => _canvas.FlattenGroup(index)));
        }
        else
        {
            var multiSelected = _selectedLayerIndices.Count > 1;
            items.Insert(4, Item(
                multiSelected ? $"Merge {_selectedLayerIndices.Count} Selected Layers" : "Merge Down",
                () =>
                {
                    if (multiSelected)
                        _canvas.MergeSelectedLayers(_selectedLayerIndices.OrderBy(x => x).ToList());
                    else
                        _canvas.MergeDown();
                }));
        }

        // Selection helpers
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

        // Filters submenu
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
            ItemsSource = new[]
            {
                AsyncItem("_Gaussian Blur...", ApplyBlurFilter),
                AsyncItem("_Sharpen...",       ApplySharpenFilter),
                AsyncItem("_Noise...",         ApplyNoiseFilter),
                AsyncItem("_Color Curves...",   ApplyColorCurvesFilter),
            }
        });

        return new ContextMenu { ItemsSource = items };
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
        if (sender is not Border row || row.Tag is not int index) return;
        var point = e.GetCurrentPoint(row);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (IsLayerRowInteractiveSource(e.Source)) return;
        SelectLayerWithModifiers(index, e.KeyModifiers);
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

        SetLayerIconBtnIcon(refs.VisibilityButton,
            layer.IsVisible ? Icons.Eye : Icons.EyeOff,
            layer.IsVisible ? IconVisOn : IconOff);
        SetLayerIconBtnIcon(refs.LockButton,
            layer.IsLocked ? Icons.LockOutline : Icons.LockOpenOutline,
            layer.IsLocked ? IconLockOn : IconOff);
        SetLayerIconBtnIcon(refs.AlphaLockButton,
            Icons.AlphaLock,
            layer.IsAlphaLocked ? IconAlphaOn : IconOff);
        SetLayerIconBtnIcon(refs.ClipButton,
            Icons.ClipToBelow,
            layer.IsClipping ? IconClipOn : IconOff);

        refs.DisclosureButton.Content = layer.IsGroup ? (layer.IsOpen ? "▾" : "▸") : "";
        refs.DisclosureButton.IsHitTestVisible = layer.IsGroup;
        if (_renamingLayerIndex != index)
        {
            refs.NameHost.Content = new TextBlock
            {
                Text = layer.Name,
                Foreground = fgBrush,
                Padding = new Thickness(4, 1),
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        }
        refs.BlendText.Text = BlendAbbr(layer.BlendMode);
        refs.BlendText.Foreground = dimBrush;
        refs.OpacityText.Text = $"{Math.Round(layer.Opacity * 100):0}%";
        refs.OpacityText.Foreground = dimBrush;

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
            Background = PreviewFrameBg,
            BorderBrush = PreviewFrameBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true
        };

        if (layer.IsGroup)
        {
            frame.Child = new TextBlock
            {
                Text = layer.IsOpen ? "▾" : "▸",
                Foreground = GroupIconFg,
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
}
