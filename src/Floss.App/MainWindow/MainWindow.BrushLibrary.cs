using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Document;

namespace Floss.App;

public partial class MainWindow : Window
{
    // ── Brush section ─────────────────────────────────────────────────────────
    private Control BuildBrushSection()
    {
        _brushCategoryPanel = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        _presetPanel = new StackPanel { Spacing = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };

        _strokePreview = new BrushStrokePreview { Height = 48 };

        var importPngBtn = SmBtn("PNG", "Import brush tip PNG");
        var importAbrBtn = SmBtn("ABR", "Import .abr brush pack");
        _saveBrushButton = SmIconBtn(Icons.ContentSaveOutline, "Save brush");
        var duplicateBrushBtn = SmIconBtn(Icons.ContentCopy, "Duplicate brush");
        var editBrushBtn = SmIconBtn(Icons.TuneVertical, "Tool properties");
        importPngBtn.Width = 38;
        importAbrBtn.Width = 38;
        importPngBtn.Click += async (_, _) => await ImportBrushTipPngAsync();
        importAbrBtn.Click += async (_, _) => await ImportAbrAsync();
        _saveBrushButton.Click += (_, _) => SaveActiveBrush();
        duplicateBrushBtn.Click += (_, _) => DuplicateActiveBrush();
        editBrushBtn.Click += (_, _) => OpenToolProperties();

        var presetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Content = _presetPanel
        };

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

        var brushToolRow = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { importPngBtn, importAbrBtn, _saveBrushButton, duplicateBrushBtn, editBrushBtn }
        };
        foreach (var b in new[] { importPngBtn, importAbrBtn, _saveBrushButton, duplicateBrushBtn, editBrushBtn })
            b.Margin = new Thickness(0, 0, 3, 3);

        var root = new Grid
        {
            Margin = new Thickness(8, 3, 8, 6),
        };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Grid.SetRow(_strokePreview, 0);
        Grid.SetRow(_activeBrushLabel, 1);
        Grid.SetRow(_brushCategoryPanel, 2);
        Grid.SetRow(presetScroll, 4);
        Grid.SetRow(brushToolRow, 5);
        _activeBrushLabel.Margin = new Thickness(0, 4, 0, 3);
        _brushCategoryPanel.Margin = new Thickness(0, 0, 0, 3);
        presetScroll.Margin = new Thickness(0, 0, 0, 1);
        root.Children.Add(_strokePreview);
        root.Children.Add(_activeBrushLabel);
        root.Children.Add(_brushCategoryPanel);
        root.Children.Add(presetScroll);
        root.Children.Add(brushToolRow);
        return root;
    }

    private static readonly DataFormat<string> CategoryDragFormat = DataFormat.CreateInProcessFormat<string>("x-floss-category");
    private static readonly DataFormat<string> PresetIdDragFormat = DataFormat.CreateInProcessFormat<string>("x-floss-preset");

    // ── Preset panel ──────────────────────────────────────────────────────────
    internal void RefreshGroupPresets()
    {
        _brushCategoryPanel.Children.Clear();
        _presetPanel.Children.Clear();

        var group = _activeToolGroup;
        if (group == null) return;

        foreach (var cat in group.Categories)
        {
            var selected = cat.Name == _selectedCategory;
            var catName = cat.Name;
            var btn = new Button
            {
                Content = catName,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(6, 2),
                Height = 22,
                Background = new SolidColorBrush(selected ? Color.Parse(AccentSoft) : Color.Parse(Bg2)),
                Foreground = new SolidColorBrush(selected ? Color.Parse(TextPrimary) : Color.Parse(TextSecondary)),
                BorderBrush = new SolidColorBrush(selected ? Color.Parse(Accent) : Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                FontSize = 10,
                Tag = catName,
                Margin = new Thickness(0, 0, 2, 2)
            };
            btn.Click += (_, _) =>
            {
                _selectedCategory = catName;
                RefreshGroupPresets();
            };
            btn.DoubleTapped += (_, _) => RenameCategoryPrompt(group, cat);
            EnableCategoryDrop(btn, group, catName);
            EnableCategoryReorder(btn, group, catName);
            _brushCategoryPanel.Children.Add(btn);
        }

        var newCatBtn = new Button
        {
            Content = Icons.Make(Icons.Plus, 12, new SolidColorBrush(Color.Parse(TextSecondary))),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(6, 2),
            Height = 22,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            FontSize = 10
        };
        newCatBtn.Click += async (_, _) =>
        {
            var name = await PromptForNewCategory();
            if (!string.IsNullOrWhiteSpace(name) && !group.Categories.Any(c => c.Name == name))
            {
                group.Categories.Add(new ToolCategory { Name = name });
                App.ToolGroups.Save();
                _selectedCategory = name;
                RefreshGroupPresets();
            }
        };
        EnableNewCategoryDrop(newCatBtn, group);
        _brushCategoryPanel.Children.Add(newCatBtn);

        IEnumerable<ToolPreset> presets;
        if (_selectedCategory == null)
        {
            presets = group.Presets;
        }
        else
        {
            var cat = group.Categories.FirstOrDefault(c => c.Name == _selectedCategory);
            presets = cat == null
                ? group.Presets
                : cat.PresetIds.Select(id => group.Presets.FirstOrDefault(p => p.Id == id)).OfType<ToolPreset>();
        }

        foreach (var preset in presets)
        {
            var isActive = group.LastActivePresetId == preset.Id;

            if (preset.BrushId != null)
            {
                var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
                if (asset != null)
                {
                    _presetPanel.Children.Add(BuildBrushPresetRow(group, preset, asset, isActive));
                    continue;
                }
            }
            _presetPanel.Children.Add(BuildSimplePresetRow(group, preset, isActive));
        }
    }

    private Button BuildBrushPresetRow(ToolGroup group, ToolPreset preset, BrushAsset asset, bool isActive)
    {
        var strokePreview = new BrushStrokePreview
        {
            Brush = asset.Preset,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        var nameText = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
        };
        var namePill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 15, 13, 18)),
            Padding = new Thickness(6, 1),
            CornerRadius = new CornerRadius(3),
            Child = nameText,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        var panel = new Panel { ClipToBounds = true };
        panel.Children.Add(strokePreview);
        panel.Children.Add(namePill);

        var row = new Button
        {
            Height = 38,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(3),
            Content = panel,
            Tag = preset.Id,
        };
    row.Click += (_, _) => ActivatePreset(group, preset);
    EnablePresetDrag(row, preset.Id);
    row.ContextMenu = BuildPresetContextMenu(group, preset);
    return row;
}

private Button BuildSimplePresetRow(ToolGroup group, ToolPreset preset, bool isActive)
    {
        var nameText = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? TextPrimary : TextSecondary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
        };
        var row = new Button
        {
            Height = 26,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(isActive ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(3),
            Content = nameText,
            Tag = preset.Id,
        };
    row.Click += (_, _) => ActivatePreset(group, preset);
    EnablePresetDrag(row, preset.Id);
    row.ContextMenu = BuildPresetContextMenu(group, preset);
    return row;
}

// ── Preset context menu ─────────────────────────────────────────────────

    private ContextMenu BuildPresetContextMenu(ToolGroup group, ToolPreset preset)
    {
        var menu = new ContextMenu();

        var propertiesItem = new MenuItem { Header = "Tool Properties…" };
        propertiesItem.Click += (_, _) => ShowPresetPropertiesDialog(group, preset);

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += async (_, _) => await RenamePresetPrompt(group, preset);

        var setAltInvocationItem = new MenuItem
        {
            Header = preset.AlternateInvocation.IsEmpty
                ? "Set Alt Invocation"
                : $"Alt Invocation:  {preset.AlternateInvocation.Display()}"
        };
        setAltInvocationItem.Click += (_, _) =>
        {
            if (_recordingPresetAltInvocation == preset)
            {
                CancelPresetAltInvocationRecording();
                return;
            }
            StartPresetAltInvocationRecording(group, preset);
        };

        var duplicateItem = new MenuItem { Header = "Duplicate" };
        duplicateItem.Click += (_, _) => DuplicatePreset(group, preset);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeletePreset(group, preset);

        menu.Items.Add(propertiesItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(setAltInvocationItem);
        menu.Items.Add(duplicateItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        if (preset.BrushId != null)
        {
            menu.Items.Add(new Separator());
            var restoreItem = new MenuItem { Header = "Restore Default State" };
            restoreItem.Click += (_, _) => RestorePresetDefault(group, preset);
            menu.Items.Add(restoreItem);
            var saveDefaultItem = new MenuItem { Header = "Set Current as Default" };
            saveDefaultItem.Click += (_, _) => SavePresetAsDefault(group, preset);
            menu.Items.Add(saveDefaultItem);
        }

        return menu;
    }

    private void RestorePresetDefault(ToolGroup group, ToolPreset preset)
    {
        if (preset.BrushId == null) return;

        preset.BrushSize = null;
        preset.BrushOpacity = null;
        preset.BrushFlow = null;
        preset.BrushHardness = null;
        preset.BrushSpacing = null;
        preset.BrushSmoothing = null;
        preset.BrushGrain = null;
        preset.BrushDynamicsJson = null;
        App.ToolGroups.Save();

        if (_activeToolGroup == group && group.ActivePreset == preset)
            SyncActivePresetToCanvas();

        RefreshGroupPresets();
        _footerStatusText.Text = $"Restored {preset.Name} to defaults";
    }

    private void SavePresetAsDefault(ToolGroup group, ToolPreset preset)
    {
        if (preset.BrushId == null) return;

        // Ensure this preset is active so _activeBrushAsset points to the right asset
        if (!(_activeToolGroup == group && group.ActivePreset == preset))
        {
            ActivatePreset(group, preset);
        }

        if (_activeBrushAsset != null)
        {
            _activeBrushAsset.WithPreset(CurrentBrushFromUi());
            _brushLibrary.Save(_activeBrushAsset);
            _footerStatusText.Text = $"Saved {preset.Name} as new default";
        }

        preset.BrushSize = null;
        preset.BrushOpacity = null;
        preset.BrushFlow = null;
        preset.BrushHardness = null;
        preset.BrushSpacing = null;
        preset.BrushSmoothing = null;
        preset.BrushGrain = null;
        preset.BrushDynamicsJson = null;
        App.ToolGroups.Save();

        SyncActivePresetToCanvas();
        RefreshGroupPresets();
    }

    private void SyncActivePresetToCanvas()
    {
        var active = _activeToolGroup?.ActivePreset;
        if (active == null || active.Engine is not (ToolPresetEngine.Brush or ToolPresetEngine.Eraser or ToolPresetEngine.Smudge)) return;

        BrushPreset? basePreset = null;
        if (active.BrushId != null)
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == active.BrushId);
            if (asset != null) basePreset = asset.ToPreset();
        }

        if (basePreset != null)
        {
            var overridden = active.ApplyToBrushPreset(basePreset);
            _activePreset = overridden;
            _canvas.SyncBrushFromContext(overridden);
            _activeBrushLabel.Text = basePreset.Name;
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
            var overridden = active.ApplyToBrushPreset(_canvas.Brush);
            _activePreset = overridden;
            _canvas.SyncBrushFromContext(overridden);
            _activeBrushLabel.Text = active.Name;
            _strokePreview.Brush = overridden;
        }

        var targetKind = active.Engine == ToolPresetEngine.Eraser ? BrushKind.Eraser : BrushKind.Ink;
        var current = _activePreset ?? _canvas.Brush;
        if (current.Kind != targetKind)
        {
            var fixedBrush = current with { Kind = targetKind };
            _canvas.SyncBrushFromContext(fixedBrush);
            _activePreset = fixedBrush;
            _strokePreview.Brush = fixedBrush;
        }

        UpdateStatus();
    }

private void ShowPresetPropertiesDialog(ToolGroup group, ToolPreset preset)
{
    var nameBox = new TextBox
    {
        Text = preset.Name,
        Width = 200,
        Height = 28,
        FontSize = 11,
        Padding = new Thickness(6, 0),
        Background = new SolidColorBrush(Color.Parse("#1a1c22")),
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    var enginePicker = new ComboBox
    {
        ItemsSource = Enum.GetValues<ToolPresetEngine>(),
        SelectedItem = preset.Engine,
        Width = 200,
        Height = 28,
        FontSize = 11
    };

    var outputPicker = new ComboBox
    {
        ItemsSource = Enum.GetValues<OutputProcessType>(),
        SelectedItem = preset.OutputProcess,
        Width = 200,
        Height = 28,
        FontSize = 11
    };

    var saveBtn = new Button
    {
        Content = "Save",
        Height = 28,
        Padding = new Thickness(14, 0),
        FontSize = 11,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(AccentSoft)),
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Accent)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };
    var cancelBtn = new Button
    {
        Content = "Cancel",
        Height = 28,
        Padding = new Thickness(14, 0),
        FontSize = 11,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(Bg2)),
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    var dlg = new Window
    {
        Title = $"Tool Properties — {preset.Name}",
        Width = 260,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        Background = new SolidColorBrush(Color.Parse(Bg1)),
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Name", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                nameBox,
                new TextBlock { Text = "Input Process (Engine)", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                enginePicker,
                new TextBlock { Text = "Output Process", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                outputPicker,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancelBtn, saveBtn } }
            }
        }
    };

    saveBtn.Click += (_, _) =>
    {
        preset.Name = nameBox.Text?.Trim() ?? preset.Name;
        preset.Engine = (ToolPresetEngine)(enginePicker.SelectedItem ?? preset.Engine);
        preset.OutputProcess = (OutputProcessType)(outputPicker.SelectedItem ?? preset.OutputProcess);
        App.ToolGroups.Save();
        RefreshGroupPresets();
        if (_activeToolGroup == group && group.ActivePreset == preset)
        {
            ActivatePreset(group, preset);
            BuildToolRail();
        }
        dlg.Close();
    };
    cancelBtn.Click += (_, _) => dlg.Close();

    dlg.ShowDialog(this);
}

private async System.Threading.Tasks.Task RenamePresetPrompt(ToolGroup group, ToolPreset preset)
{
    var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
    var dialog = new Window
    {
        Title = "Rename Preset",
        Width = 280,
        Height = 140,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = new SolidColorBrush(Color.Parse(Bg0))
    };
    var tb = new TextBox { Margin = new Thickness(12), Text = preset.Name };
    var ok = new Button { Content = "Rename", Margin = new Thickness(12, 0, 12, 12) };
    ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dialog.Close(); };
    tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tcs.TrySetResult(tb.Text); dialog.Close(); } };
    dialog.Closed += (_, _) => tcs.TrySetResult(null);
    dialog.Content = new StackPanel { Children = { tb, ok } };
    await dialog.ShowDialog(this);

    var newName = (await tcs.Task)?.Trim();
    if (string.IsNullOrWhiteSpace(newName) || newName == preset.Name) return;
    preset.Name = newName;
    App.ToolGroups.Save();
    RefreshGroupPresets();
    if (_activeToolGroup == group && group.ActivePreset == preset)
        ActivatePreset(group, preset);
}

    private void DuplicatePreset(ToolGroup group, ToolPreset preset)
    {
        var copy = new ToolPreset
        {
            Name = preset.Name + " Copy",
            Engine = preset.Engine,
            OutputProcess = preset.OutputProcess,
            BrushId = preset.BrushId,
            BrushSize = preset.BrushSize,
            BrushOpacity = preset.BrushOpacity,
            BrushFlow = preset.BrushFlow,
            BrushHardness = preset.BrushHardness,
            BrushSpacing = preset.BrushSpacing,
            BrushSmoothing = preset.BrushSmoothing,
            BrushGrain = preset.BrushGrain,
            BrushDynamicsJson = preset.BrushDynamicsJson,
            Tolerance = preset.Tolerance,
            SelectMode = preset.SelectMode,
            GradientType = preset.GradientType,
            ShapeKind = preset.ShapeKind,
            ShapeDrawMode = preset.ShapeDrawMode,
            ShapeStrokeWidth = preset.ShapeStrokeWidth,
            PolylineClosePath = preset.PolylineClosePath,
            PolylineStrokeWidth = preset.PolylineStrokeWidth
        };
    group.Presets.Add(copy);
    App.ToolGroups.Save();
    RefreshGroupPresets();
}

private void DeletePreset(ToolGroup group, ToolPreset preset)
{
    if (group.Presets.Count <= 1) return;
    group.Presets.Remove(preset);
    if (group.LastActivePresetId == preset.Id)
        group.LastActivePresetId = group.Presets.FirstOrDefault()?.Id;
    App.ToolGroups.Save();
    RefreshGroupPresets();
    if (_activeToolGroup == group)
    {
        var active = group.ActivePreset ?? group.Presets.FirstOrDefault();
        if (active != null) ActivatePreset(group, active);
    }
}

// ── Drag-and-drop helpers ─────────────────────────────────────────────────

    private static void EnablePresetDrag(Control source, string presetId)
    {
        DragDrop.SetAllowDrop(source, true);
        source.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(PresetIdDragFormat))
                e.Handled = true;
        });
        source.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(PresetIdDragFormat))
                e.DragEffects = DragDropEffects.Move;
        });
        source.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
            {
                var item = new DataTransferItem();
                item.Set(PresetIdDragFormat, presetId);
                var data = new DataTransfer();
                data.Add(item);
                _ = DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            }
        };
    }

    private void EnableCategoryDrop(Control target, ToolGroup group, string category)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(PresetIdDragFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(PresetIdDragFormat)) return;
            var presetId = e.DataTransfer.TryGetValue<string>(PresetIdDragFormat);
            if (string.IsNullOrWhiteSpace(presetId)) return;
            MovePresetToCategory(group, presetId, category);
            e.Handled = true;
        });
    }

    private void EnableNewCategoryDrop(Control target, ToolGroup group)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(PresetIdDragFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        target.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (!e.DataTransfer.Contains(PresetIdDragFormat)) return;
            var presetId = e.DataTransfer.TryGetValue<string>(PresetIdDragFormat);
            if (string.IsNullOrWhiteSpace(presetId)) return;
            var name = await PromptForNewCategory();
            if (!string.IsNullOrWhiteSpace(name) && !group.Categories.Any(c => c.Name == name))
            {
                group.Categories.Add(new ToolCategory { Name = name });
                MovePresetToCategory(group, presetId, name);
                _selectedCategory = name;
                RefreshGroupPresets();
            }
            e.Handled = true;
        });
    }

    private void MovePresetToCategory(ToolGroup group, string presetId, string categoryName)
    {
        foreach (var c in group.Categories) c.PresetIds.Remove(presetId);
        var cat = group.Categories.FirstOrDefault(c => c.Name == categoryName);
        if (cat == null) { cat = new ToolCategory { Name = categoryName }; group.Categories.Add(cat); }
        if (!cat.PresetIds.Contains(presetId)) cat.PresetIds.Add(presetId);
        App.ToolGroups.Save();
        RefreshGroupPresets();
    }

    private async Task<string?> PromptForNewCategory()
    {
        var tcs = new TaskCompletionSource<string?>();
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
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tcs.TrySetResult(tb.Text); dialog.Close(); } };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Content = new StackPanel { Children = { tb, ok } };
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async void RenameCategoryPrompt(ToolGroup group, ToolCategory cat)
    {
        var tcs = new TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Title = "Rename Category",
            Width = 280,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        var tb = new TextBox { Margin = new Thickness(12), Text = cat.Name };
        var ok = new Button { Content = "Rename", Margin = new Thickness(12, 0, 12, 12) };
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tcs.TrySetResult(tb.Text); dialog.Close(); } };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Content = new StackPanel { Children = { tb, ok } };
        await dialog.ShowDialog(this);

        var newName = (await tcs.Task)?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == cat.Name) return;
        if (group.Categories.Any(c => c.Name == newName)) return;

        if (_selectedCategory == cat.Name) _selectedCategory = newName;
        cat.Name = newName;
        App.ToolGroups.Save();
        RefreshGroupPresets();
    }

    private void EnableCategoryReorder(Button btn, ToolGroup group, string cat)
    {
        DragDrop.SetAllowDrop(btn, true);
        btn.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(btn).Properties.IsLeftButtonPressed) return;
            var item = new DataTransferItem();
            item.Set(CategoryDragFormat, cat);
            var data = new DataTransfer();
            data.Add(item);
            _ = DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        };
        btn.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(CategoryDragFormat) && e.DataTransfer.TryGetValue<string>(CategoryDragFormat) != cat)
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        btn.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var dragged = e.DataTransfer.TryGetValue<string>(CategoryDragFormat);
            if (string.IsNullOrEmpty(dragged) || dragged == cat) return;
            var fromCat = group.Categories.FirstOrDefault(c => c.Name == dragged);
            var toCat = group.Categories.FirstOrDefault(c => c.Name == cat);
            if (fromCat == null || toCat == null) return;
            var from = group.Categories.IndexOf(fromCat);
            var to = group.Categories.IndexOf(toCat);
            if (from < 0 || to < 0) return;
            group.Categories.RemoveAt(from);
            group.Categories.Insert(to, fromCat);
            App.ToolGroups.Save();
            RefreshGroupPresets();
            e.Handled = true;
        });
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
        _dirtyBrushAssetIds.Clear();
        App.ToolGroups.SyncWithAssets(_brushAssets, _activeToolGroup);
        App.ToolGroups.Save();
        RefreshGroupPresets();
    }

    private void SelectInitialBrush()
    {
        var initial = _brushAssets.FirstOrDefault(b => b.Preset.Name == App.Config.LastBrushName)
            ?? _brushAssets.FirstOrDefault()
            ?? BrushAsset.FromPreset(BrushPreset.Defaults[0]);

        // Find the group+preset that owns this brush asset and do a full activation
        foreach (var group in App.ToolGroups.Groups)
        {
            var preset = group.Presets.FirstOrDefault(p => p.BrushId == initial.Id);
            if (preset != null) { ActivatePreset(group, preset); return; }
        }

        // No linked preset yet — activate the brush group's first preset and load the brush
        var brushGroup = App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Brush)
            ?? App.ToolGroups.Groups.FirstOrDefault();
        if (brushGroup != null)
        {
            var fallback = brushGroup.ActivePreset ?? brushGroup.Presets.FirstOrDefault();
            if (fallback != null) ActivatePreset(brushGroup, fallback);
        }
        ApplyBrushAsset(initial);
    }

    private void ApplyBrushAsset(BrushAsset asset)
    {
        _activeBrushAsset = asset;
        ApplyBrushSettings(asset.ToPreset(), syncSliders: true);
    }

    internal void ApplyBrushSettings(BrushPreset preset, bool syncSliders)
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
        UpdateStatus();
        RefreshToolProperties();
    }

    private void UpdateCurrentBrush(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncingBrushUi || _syncingToolPropertyPanel) return;
        _activePreset ??= _canvas.Brush;
        var updated = update(_activePreset);
        _activePreset = updated;
        if (_activeBrushAsset != null)
        {
            _activeBrushAsset.Preset = updated;
            _activeBrushAsset.Tip = BrushTipData.FromTip(updated.Tip);
            _dirtyBrushAssetIds.Add(_activeBrushAsset.Id);
        }
        ApplyBrushSettings(updated, syncSliders: false);
    }

    private void OpenToolProperties()
    {
        var toolPreset = _activeToolGroup?.ActivePreset;
        if (toolPreset == null) return;

        var brushPreset = _activePreset ?? _canvas.Brush;

        if (_toolPropsWindow != null)
        {
            _toolPropsWindow.SyncFromToolPreset(toolPreset);
            if (brushPreset != null)
                _toolPropsWindow.SyncFromPreset(brushPreset);
            _toolPropsWindow.Activate();
            return;
        }

        _toolPropsWindow = new ToolPropertiesWindow(toolPreset, brushPreset, (tp, bp) =>
        {
            if (_syncingBrushUi) return;

            // Apply brush changes for DirectDraw tools
            if (bp != null && toolPreset.OutputProcess == OutputProcessType.DirectDraw)
            {
                _activePreset = bp;
                if (_activeBrushAsset != null)
                {
                    _activeBrushAsset.Preset = bp;
                    _activeBrushAsset.Tip = BrushTipData.FromTip(bp.Tip);
                    _dirtyBrushAssetIds.Add(_activeBrushAsset.Id);
                }
                _suppressBrushSettingsRestored = true;
                try
                {
                    ApplyBrushSettings(bp, syncSliders: true);
                }
                finally
                {
                    _suppressBrushSettingsRestored = false;
                }
            }

            // Apply paint settings (opacity/blend) for all paint tools
            if (tp.BrushOpacity.HasValue)
                UpdateCurrentBrush(p => p with { Opacity = tp.BrushOpacity.Value });
            if (tp.BrushBlendMode.HasValue)
                UpdateCurrentBrush(p => p with { BlendMode = tp.BrushBlendMode.Value });

            if (_activeToolGroup?.ActivePreset == tp && tp.OutputProcess != OutputProcessType.DirectDraw)
                _canvas.SetActiveTool(ToolForPreset(tp), tp);

            RefreshToolProperties();
        });
        _toolPropsWindow.Closed += (_, _) => _toolPropsWindow = null;
        _toolPropsWindow.Show(this);
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
        _dirtyBrushAssetIds.Add(_activeBrushAsset.Id);
        ApplyBrushSettings(_activeBrushAsset.ToPreset(), syncSliders: false);
        RefreshGroupPresets();
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

        var imported = 0;
        var lastDiag = "";
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

    private void StartPresetAltInvocationRecording(ToolGroup group, ToolPreset preset)
    {
        _recordingPresetAltInvocation = preset;
        _footerStatusText.Text = $"Press a key for \"{preset.Name}\" alternate invocation… (Esc = cancel)";
    }

    internal void CommitPresetAltInvocation(Input.KeyBinding kb)
    {
        if (_recordingPresetAltInvocation == null) return;
        var preset = _recordingPresetAltInvocation;
        CancelPresetAltInvocationRecording();
        preset.AlternateInvocation = kb;
        App.ToolGroups.Save();
        RefreshGroupPresets();
        _footerStatusText.Text = $"Alt invocation set to {kb.Display()}";
    }

    internal void CancelPresetAltInvocationRecording()
    {
        _recordingPresetAltInvocation = null;
        _recordingPresetPendingMods = KeyModifiers.None;
        RefreshGroupPresets();
        _footerStatusText.Text = "";
    }
}
