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
        _presetPanel = new StackPanel { Spacing = 2, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };

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
            MaxHeight = 320,
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
                _brushCategoryPanel,
                new Border { Height = 4 },
                presetScroll,
                new Border { Height = 2 },
                brushToolRow,
            }
        };
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
                Padding = new Thickness(8, 4),
                Height = 26,
                Background = new SolidColorBrush(selected ? Color.Parse(AccentSoft) : Color.Parse(Bg2)),
                Foreground = new SolidColorBrush(selected ? Color.Parse(TextPrimary) : Color.Parse(TextSecondary)),
                BorderBrush = new SolidColorBrush(selected ? Color.Parse(Accent) : Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                FontSize = 10,
                Tag = catName,
                Margin = new Thickness(0, 0, 3, 3)
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
            MaxWidth = 160,
        };
        var namePill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 15, 13, 18)),
            Padding = new Thickness(7, 2),
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
            Height = 48,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Content = panel,
            Tag = preset.Id,
        };
        row.Click += (_, _) => ActivatePreset(group, preset);
        EnablePresetDrag(row, preset.Id);
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
            Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(isActive ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Content = nameText,
            Tag = preset.Id,
        };
        row.Click += (_, _) => ActivatePreset(group, preset);
        EnablePresetDrag(row, preset.Id);
        return row;
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
        ApplyBrushSettings(updated, syncSliders: false);
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
}
