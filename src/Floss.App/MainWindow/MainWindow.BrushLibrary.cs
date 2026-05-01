using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public partial class MainWindow
{
    // ── Brush section ─────────────────────────────────────────────────────────
    private Control BuildBrushSection()
    {
        // ── Library part ──────────────────────────────────────────────────────
        _brushCategoryPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4
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

        // ── Parameters part ───────────────────────────────────────────────────
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

        var categoryScrollRow = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 30,
            ClipToBounds = true,
            Content = _brushCategoryPanel
        };

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
                categoryScrollRow,
                new Border { Height = 4 },
                presetScroll,
                new Border { Height = 2 },
                brushToolRow,
            }
        };
    }

    private static readonly DataFormat<string> CategoryDragFormat = DataFormat.CreateInProcessFormat<string>("x-floss-category");

    // ── Brush library ─────────────────────────────────────────────────────────
    private void BuildBrushCategories()
    {
        _brushCategoryPanel.Children.Clear();
        foreach (var cat in _brushPaletteConfig.Categories)
        {
            var selected = cat == _selectedBrushCategory;
            var btn = new Button
            {
                Content = cat,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(8, 4),
                Height = 26,
                Background = new SolidColorBrush(selected ? Color.Parse(AccentSoft) : Color.Parse(Bg2)),
                Foreground = new SolidColorBrush(selected ? Color.Parse(TextPrimary) : Color.Parse(TextSecondary)),
                BorderBrush = new SolidColorBrush(selected ? Color.Parse(Accent) : Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                FontSize = 10,
                Tag = cat
            };
            btn.Click += (_, _) =>
            {
                _selectedBrushCategory = cat;
                BuildBrushCategories();
                BuildPresets();
            };
            btn.DoubleTapped += (_, _) => RenameCategoryPrompt(cat);
            EnableCategoryDrop(btn, cat);
            EnableCategoryReorder(btn, cat);
            _brushCategoryPanel.Children.Add(btn);
        }

        // "New category" button / drop zone
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
        newCatBtn.Click += (_, _) =>
        {
            var name = PromptForNewCategory();
            if (!string.IsNullOrWhiteSpace(name) && !_brushPaletteConfig.Categories.Contains(name))
            {
                _brushPaletteConfig.Categories.Add(name);
                _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
                _selectedBrushCategory = name;
                BuildBrushCategories();
                BuildPresets();
            }
        };
        EnableNewCategoryDrop(newCatBtn);
        _brushCategoryPanel.Children.Add(newCatBtn);
    }

    private void BuildPresets()
    {
        _presetPanel.Children.Clear();
        var assets = _selectedBrushCategory == null || _selectedBrushCategory == "Recent"
            ? _brushAssets
            : _brushAssets.Where(p => _brushPaletteConfig.BrushCategory.GetValueOrDefault(p.Id) == _selectedBrushCategory).ToArray();

        foreach (var asset in assets)
        {
            var preset  = asset.Preset;
            var isActive = _activeBrushAsset?.Id == asset.Id;

            // Stroke preview fills the full row
            var strokePreview = new BrushStrokePreview
            {
                Brush  = preset,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
            };

            // Name pill — right-aligned, floating over the preview
            var nameText = new TextBlock
            {
                Text       = preset.Name,
                FontSize   = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth   = 160,
            };
            var namePill = new Border
            {
                Background    = new SolidColorBrush(Color.FromArgb(160, 15, 13, 18)),
                Padding       = new Thickness(7, 2),
                CornerRadius  = new CornerRadius(3),
                Child         = nameText,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                Margin        = new Thickness(0, 0, 7, 0),
            };

            var panel = new Panel { ClipToBounds = true };
            panel.Children.Add(strokePreview);
            panel.Children.Add(namePill);

            var row = new Button
            {
                Height      = 48,
                Padding     = new Thickness(0),
                HorizontalAlignment        = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
                Background  = new SolidColorBrush(Color.Parse(Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
                BorderThickness = new Thickness(isActive ? 2 : 1),
                CornerRadius    = new CornerRadius(4),
                Content = panel,
                Tag     = asset,
            };
            row.Click += (_, _) => ApplyBrushAsset(asset);
            EnablePresetDrag(row, asset);
            _presetPanel.Children.Add(row);
        }
    }

    // ── Drag-and-drop helpers ────────────────────────────────────────────────

    private static readonly DataFormat<string> BrushIdFormat = DataFormat.CreateInProcessFormat<string>("x-floss-brush");

    private static void EnablePresetDrag(Control source, BrushAsset asset)
    {
        DragDrop.SetAllowDrop(source, true);
        source.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
                e.Handled = true;
        });
        source.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
                e.DragEffects = DragDropEffects.Move;
        });

        source.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
            {
                var item = new DataTransferItem();
                item.Set(BrushIdFormat, asset.Id);
                var data = new DataTransfer();
                data.Add(item);
                _ = DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            }
        };
    }

    private void EnableCategoryDrop(Control target, string category)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(BrushIdFormat)) return;
            var brushId = e.DataTransfer.TryGetValue<string>(BrushIdFormat);
            if (string.IsNullOrWhiteSpace(brushId)) return;

            _brushPaletteConfig.BrushCategory[brushId] = category;
            _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
            BuildPresets();
            e.Handled = true;
        });
    }

    private void EnableNewCategoryDrop(Control target)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(BrushIdFormat))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(BrushIdFormat)) return;
            var brushId = e.DataTransfer.TryGetValue<string>(BrushIdFormat);
            if (string.IsNullOrWhiteSpace(brushId)) return;

            var name = PromptForNewCategory();
            if (!string.IsNullOrWhiteSpace(name) && !_brushPaletteConfig.Categories.Contains(name))
            {
                _brushPaletteConfig.Categories.Add(name);
                _brushPaletteConfig.BrushCategory[brushId] = name;
                _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
                _selectedBrushCategory = name;
                BuildBrushCategories();
                BuildPresets();
            }
            e.Handled = true;
        });
    }

    private string? PromptForNewCategory()
    {
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
        var result = (string?)null;
        ok.Click += (_, _) => { result = tb.Text; dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = tb.Text; dialog.Close(); } };
        dialog.Content = new StackPanel { Children = { tb, ok } };
        dialog.ShowDialog(this);
        return result;
    }

    private void RenameCategoryPrompt(string oldName)
    {
        var dialog = new Window
        {
            Title = "Rename Category",
            Width = 280,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        var tb = new TextBox { Margin = new Thickness(12), Text = oldName };
        var ok = new Button { Content = "Rename", Margin = new Thickness(12, 0, 12, 12) };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = tb.Text; dialog.Close(); } };
        dialog.Content = new StackPanel { Children = { tb, ok } };
        dialog.ShowDialog(this);

        var newName = result?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;
        if (_brushPaletteConfig.Categories.Contains(newName)) return;

        var idx = _brushPaletteConfig.Categories.IndexOf(oldName);
        if (idx < 0) return;
        _brushPaletteConfig.Categories[idx] = newName;
        foreach (var key in _brushPaletteConfig.BrushCategory.Keys.ToList())
            if (_brushPaletteConfig.BrushCategory[key] == oldName)
                _brushPaletteConfig.BrushCategory[key] = newName;
        if (_selectedBrushCategory == oldName) _selectedBrushCategory = newName;
        _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
        BuildBrushCategories();
        BuildPresets();
    }

    private void EnableCategoryReorder(Button btn, string cat)
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
            var from = _brushPaletteConfig.Categories.IndexOf(dragged);
            var to   = _brushPaletteConfig.Categories.IndexOf(cat);
            if (from < 0 || to < 0) return;
            _brushPaletteConfig.Categories.RemoveAt(from);
            _brushPaletteConfig.Categories.Insert(to, dragged);
            _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
            BuildBrushCategories();
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
        _brushPaletteConfig.SyncWithAssets(_brushAssets);
        _brushPaletteConfig.Save(AppPaths.BrushPaletteConfigPath);
        BuildPresets();
    }

    private void SelectInitialBrush()
    {
        var initial = _brushAssets.FirstOrDefault(b => b.Preset.Name == App.Config.LastBrushName)
            ?? _brushAssets.FirstOrDefault()
            ?? BrushAsset.FromPreset(BrushPreset.Defaults[0]);
        ApplyBrushAsset(initial);
    }

    private void ApplyBrushAsset(BrushAsset asset)
    {
        _activeBrushAsset = asset;
        ApplyPreset(asset.ToPreset(), syncSliders: true);
    }

    private void ApplyPreset(BrushPreset preset, bool syncSliders)
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
        SetTool(preset.Kind == BrushKind.Eraser ? "eraser" : "brush");
        BuildPresets();
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
        ApplyPreset(updated, syncSliders: false);
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
        ApplyPreset(_activeBrushAsset.ToPreset(), syncSliders: false);
        BuildPresets();
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

        var imported    = 0;
        var lastDiag    = "";
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
