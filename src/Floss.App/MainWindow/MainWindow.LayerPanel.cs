using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Document;

namespace Floss.App;

public partial class MainWindow
{
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
        var isSelected = _selectedLayerIndices.Contains(i);
        var dimColor = isActive ? Color.Parse("#5a80c8") : isSelected ? Color.Parse("#4a6090") : Color.Parse("#383d47");
        var fgColor = isActive ? Color.Parse("#d8e0f0") : isSelected ? Color.Parse("#a8c0e0") : Color.Parse("#7a8494");

        var row = new Border
        {
            Background = new SolidColorBrush(isActive ? Color.Parse("#1a2a50") : isSelected ? Color.Parse("#141e38") : Color.Parse("#16181f")),
            BorderBrush = new SolidColorBrush(isActive ? Color.Parse("#2e5fb8") : isSelected ? Color.Parse("#2a4a88") : Color.Parse("#1e2128")),
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
            TextTrimming = TextTrimming.CharacterEllipsis,
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
            Foreground = new SolidColorBrush(dimColor),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 2, 0),
            IsHitTestVisible = false
        };

        var opacityText = new TextBlock
        {
            Text = $"{Math.Round(layer.Opacity * 100):0}%",
            Foreground = new SolidColorBrush(dimColor),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
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

        return new ContextMenu { ItemsSource = items };
    }

    private void SelectLayerWithModifiers(int index, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Control))
        {
            if (!_selectedLayerIndices.Add(index))
                _selectedLayerIndices.Remove(index);
            _canvas.SelectLayer(index);
        }
        else if (mods.HasFlag(KeyModifiers.Shift) && _selectedLayerIndices.Count > 0)
        {
            var active = _canvas.ActiveLayerIndex;
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
            _canvas.SelectLayer(index);
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
}
