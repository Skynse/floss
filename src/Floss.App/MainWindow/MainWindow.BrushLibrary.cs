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
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Document;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow : Window
{
    // ── Brush section ─────────────────────────────────────────────────────────
    /// <summary>Fixed tile width for wrap layout (CSP-style adaptive columns).</summary>
    private const double BrushPresetTileWidth = 136;
    private const double BrushPresetPreviewRenderWidth = 128;
    private const double BrushPresetPreviewRenderHeight = 44;

    private const int BrushPresetAutosaveDebounceMs = 700;
    private const int ToolGroupsSaveDebounceMs = 400;
    private DispatcherTimer? _brushPresetAutosaveTimer;
    private DispatcherTimer? _toolGroupsSaveTimer;
    private readonly Dictionary<string, BrushPresetRowHost> _brushPresetRowCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastCapturedBrushByPresetId = new(StringComparer.Ordinal);

    private sealed class BrushPresetRowHost
    {
        public required Button Row { get; init; }
        public required BrushStrokePreview Preview { get; init; }
        public string PreviewKey = "";
    }

    private Control BuildBrushSection()
    {
        _brushPresetRowCache.Clear();

        _strokePreview = new BrushStrokePreview { Height = 34, Margin = new Thickness(4, 1, 4, 3) };

        _sizeSlider = MkSlider(1, BrushSizeLimits.FallbackMaxDiameterPx, 20, "Size");
        _maxSizePercentSlider = MkSlider(
            BrushSizeLimits.MinMaxSizePercent,
            BrushSizeLimits.StudioMaxSizePercent,
            BrushSizeLimits.DefaultMaxSizePercent,
            "Max size — canvas-scaled ceiling for this brush (100–400%)");
        _opacitySlider = MkSlider(0.01, 1, 1.0, "Opacity");
        _flowSlider = MkSlider(0.01, 1, 1.0, "Flow — controls paint buildup per dab");
        _hardnessSlider = MkSlider(0, 1, 0.9, "Hardness — edge softness");
        _spacingSlider = MkSlider(0.02, 1, 0.1, "Spacing");
            _smoothingSlider = MkSlider(0, 0.95, 0.3, "Stabilization");
        _grainSlider = MkSlider(0, 1, 0.0, "Grain — noise texture");

        _activeBrushLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var panelMenuBtn = SmIconBtn(Icons.DotsVertical, "Panel menu");
        panelMenuBtn.Click += (_, _) => ShowBrushPanelMenu(panelMenuBtn);
        _saveBrushButton = SmIconBtn(Icons.ContentSaveOutline, "Save brush");
        var duplicateBrushBtn = SmIconBtn(Icons.ContentCopy, "Duplicate brush");
        var editBrushBtn = SmIconBtn(Icons.TuneVertical, "Tool properties");
        _saveBrushButton.Click += (_, _) => SaveActiveBrush();
        duplicateBrushBtn.Click += (_, _) => DuplicateActiveBrush();
        editBrushBtn.Click += (_, _) => OpenToolProperties();

        // Header: active brush name + action buttons
        var headerRow = new DockPanel { Margin = new Thickness(8, 0, 8, 6), LastChildFill = true };
        DockPanel.SetDock(panelMenuBtn, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(editBrushBtn, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(duplicateBrushBtn, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(_saveBrushButton, Avalonia.Controls.Dock.Right);
        headerRow.Children.Add(panelMenuBtn);
        headerRow.Children.Add(editBrushBtn);
        headerRow.Children.Add(duplicateBrushBtn);
        headerRow.Children.Add(_saveBrushButton);
        headerRow.Children.Add(_activeBrushLabel);

        // Category strip — wraps like preset tiles (readable labels, no equal-width squeeze)
        _brushCategoryPanel = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            ItemSpacing = 4,
            LineSpacing = 4,
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };

        _presetPanel = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            ItemWidth = BrushPresetTileWidth,
            ItemSpacing = 4,
            LineSpacing = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        var presetScroll = ScrollHelper.Create(sv =>
        {
            ScrollHelper.UseVisibleScrollBars(sv, horizontal: false, vertical: true);
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Content = _presetPanel;
        });
        _brushPresetScroll = presetScroll;
        presetScroll.CacheMode = new Avalonia.Media.BitmapCache();
        presetScroll.SizeChanged += (_, _) => QueueSyncBrushPresetPanelLayout();

        var listArea = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 8, 8)
        };
        Grid.SetRow(_brushCategoryPanel, 0);
        Grid.SetRow(presetScroll, 1);
        listArea.Children.Add(_brushCategoryPanel);
        listArea.Children.Add(presetScroll);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));

        Grid.SetRow(_strokePreview, 0);
        Grid.SetRow(headerRow, 1);
        Grid.SetRow(listArea, 2);
        root.Children.Add(_strokePreview);
        root.Children.Add(headerRow);
        root.Children.Add(listArea);

        return root;
    }

    private static readonly DataFormat<string> CategoryDragFormat = DataFormat.CreateInProcessFormat<string>("x-floss-category");
    private static readonly DataFormat<string> CategoryGroupDragFormat = DataFormat.CreateInProcessFormat<string>("x-floss-category-group");
    private static readonly DataFormat<string> PresetIdDragFormat = DataFormat.CreateInProcessFormat<string>("x-floss-preset");

    // ── Preset panel ──────────────────────────────────────────────────────────
    internal void RefreshGroupPresets()
    {
        if (_brushCategoryPanel is null || _presetPanel is null)
            return;

        _brushCategoryPanel.Children.Clear();
        _presetPanel.Children.Clear();

        var group = _activeToolGroup;
        if (group == null) return;

        if (_selectedCategory == null)
            _selectedCategory = group.Categories.FirstOrDefault()?.Name;

        foreach (var cat in group.Categories)
        {
            var selected = cat.Name == _selectedCategory;
            var catName = cat.Name;
            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = catName,
                    FontSize = 11,
                    TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
                Padding = new Thickness(8, 5),
                MinHeight = 28,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(selected ? Bg3 : Bg2)),
                Foreground = new SolidColorBrush(Color.Parse(selected ? TextPrimary : TextSecondary)),
                BorderBrush = new SolidColorBrush(Color.Parse(selected ? Accent : Stroke)),
                BorderThickness = new Thickness(1, 1, 1, selected ? 2 : 1),
                CornerRadius = new CornerRadius(3),
                Tag = catName,
            };
            btn.Click += (_, _) =>
            {
                _selectedCategory = catName;
                group.LastActiveCategoryName = catName;

                var catObj = group.Categories.FirstOrDefault(c => c.Name == catName);
                ToolPreset? toActivate = null;
                if (catObj != null)
                {
                    if (catObj.LastActivePresetId != null && catObj.PresetIds.Contains(catObj.LastActivePresetId))
                        toActivate = group.Presets.FirstOrDefault(p => p.Id == catObj.LastActivePresetId);
                    toActivate ??= catObj.PresetIds
                        .Select(id => group.Presets.FirstOrDefault(p => p.Id == id))
                        .FirstOrDefault(p => p != null);
                }

                if (toActivate != null)
                    ActivatePreset(group, toActivate);
                else
                {
                    SaveActiveToolSelection();
                    RefreshGroupPresets();
                }
            };
            btn.DoubleTapped += (_, _) => RenameCategoryPrompt(group, cat);
            btn.ContextMenu = BuildCategoryContextMenu(group, cat);
            EnableCategoryDrop(btn, group, catName);
            EnableCategoryReorder(btn, group, catName);
            _brushCategoryPanel.Children.Add(btn);
        }

        var newCatBtn = new Button
        {
            Content = Icons.Make(Icons.Plus, 11, new SolidColorBrush(Color.Parse(TextMuted))),
            MinHeight = 28,
            MinWidth = 36,
            Width = 36,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(0, 5),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
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

        if (_selectedCategory == null)
            _selectedCategory = group.Categories.FirstOrDefault(c => c.PresetIds.Count > 0)?.Name
                ?? group.Categories.FirstOrDefault()?.Name;

        IEnumerable<ToolPreset> presets;
        if (_selectedCategory == null)
        {
            presets = group.Presets;
        }
        else
        {
            var cat = group.Categories.FirstOrDefault(c => c.Name == _selectedCategory);
            if (cat == null)
            {
                _selectedCategory = group.Categories.FirstOrDefault(c => c.PresetIds.Count > 0)?.Name;
                cat = _selectedCategory == null
                    ? null
                    : group.Categories.FirstOrDefault(c => c.Name == _selectedCategory);
            }

            presets = cat == null
                ? group.Presets
                : cat.PresetIds.Select(id => group.Presets.FirstOrDefault(p => p.Id == id)).OfType<ToolPreset>();
        }

        foreach (var preset in presets)
        {
            var isActive = group.LastActivePresetId == preset.Id;

            if (preset.InputProcess.IsBrushFamily() && preset.OutputProcess == OutputProcessType.DirectDraw)
            {
                BrushPreset? brushPreset = null;
                if (preset.BrushId != null)
                {
                    var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
                    if (asset != null)
                        brushPreset = preset.ApplyToBrushPreset(asset.ToPreset());
                }
                brushPreset ??= preset.ApplyToBrushPreset(BrushPreset.Defaults[0]);
                _presetPanel.Children.Add(GetOrUpdateBrushPresetRow(group, preset, brushPreset, isActive));
                continue;
            }
            _presetPanel.Children.Add(BuildSimplePresetRow(group, preset, isActive));
        }

        var visiblePresetIds = presets.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _brushPresetRowCache.Keys.Where(id => !visiblePresetIds.Contains(id)).ToList())
            _brushPresetRowCache.Remove(staleId);

        QueueSyncBrushPresetPanelLayout();
        _brushPresetScroll?.InvalidateVisual();
    }

    private void QueueSyncBrushPresetPanelLayout()
    {
        if (_brushPresetScroll is null)
            return;

        Dispatcher.UIThread.Post(SyncBrushPresetPanelLayout, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// ScrollViewer gives content infinite width; constrain to viewport so WrapPanel wraps full tiles only.
    /// </summary>
    private void SyncBrushPresetPanelLayout()
    {
        if (_brushPresetScroll is null)
            return;

        var viewportW = _brushPresetScroll.Viewport.Width;
        if (viewportW <= 1 || double.IsInfinity(viewportW) || double.IsNaN(viewportW))
            viewportW = _brushPresetScroll.Bounds.Width;
        if (viewportW <= 1)
            return;

        const double listRightMargin = 8;
        var contentW = Math.Max(BrushPresetTileWidth, viewportW - listRightMargin);

        _brushCategoryPanel.Width = contentW;
        _brushCategoryPanel.MaxWidth = contentW;

        _presetPanel.Width = contentW;
        _presetPanel.MaxWidth = contentW;

        var spacing = _presetPanel.ItemSpacing;
        var columns = Math.Max(1, (int)((contentW + spacing) / (BrushPresetTileWidth + spacing)));
        _presetPanel.ItemWidth = (contentW - (columns - 1) * spacing) / columns;
        _presetPanel.InvalidateMeasure();
        _brushCategoryPanel.InvalidateMeasure();
    }

    private Button GetOrUpdateBrushPresetRow(ToolGroup group, ToolPreset preset, BrushPreset brushPreset, bool isActive)
    {
        if (!_brushPresetRowCache.TryGetValue(preset.Id, out var host))
        {
            host = BuildBrushPresetRowHost(group, preset, brushPreset, isActive);
            _brushPresetRowCache[preset.Id] = host;
            return host.Row;
        }

        var previewKey = BuildBrushPreviewKey(brushPreset);
        if (!string.Equals(host.PreviewKey, previewKey, StringComparison.Ordinal))
        {
            host.Preview.Brush = brushPreset;
            host.PreviewKey = previewKey;
        }

        ApplyBrushPresetRowActive(host.Row, isActive);
        if (host.Row.Parent is Panel staleParent && !ReferenceEquals(staleParent, _presetPanel))
            staleParent.Children.Remove(host.Row);
        return host.Row;
    }

    private BrushPresetRowHost BuildBrushPresetRowHost(ToolGroup group, ToolPreset preset, BrushPreset brushPreset, bool isActive)
    {
        var row = BuildBrushPresetRow(group, preset, brushPreset, isActive);
        var previewHost = (Grid)((Grid)row.Content!).Children[0];
        var preview = (BrushStrokePreview)previewHost.Children[0];
        return new BrushPresetRowHost
        {
            Row = row,
            Preview = preview,
            PreviewKey = BuildBrushPreviewKey(brushPreset)
        };
    }

    private static string BuildBrushPreviewKey(BrushPreset brushPreset)
        => $"{GraphForBrushTip(brushPreset.Tip).CacheKey()}|{brushPreset.Size:F2}|{brushPreset.Hardness:F3}|{brushPreset.Opacity:F3}|{brushPreset.Flow:F3}";

    private static void ApplyBrushPresetRowActive(Button row, bool isActive)
    {
        row.BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke));
        row.BorderThickness = new Thickness(isActive ? 2 : 1);
        row.Background = new SolidColorBrush(Color.Parse(Bg2));
        if (row.Content is Grid grid && grid.Children.Count >= 2
            && grid.Children[1] is Border nameRow)
        {
            nameRow.Background = new SolidColorBrush(Color.Parse(isActive ? AccentSoft : Bg1));
        }
    }

    private Button BuildBrushPresetRow(ToolGroup group, ToolPreset preset, BrushPreset brushPreset, bool isActive)
    {
        var strokePreview = new BrushStrokePreview
        {
            Brush = brushPreset,
            CompactPreview = true,
            FixedRenderWidth = (int)BrushPresetPreviewRenderWidth,
            FixedRenderHeight = (int)BrushPresetPreviewRenderHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        Avalonia.Media.RenderOptions.SetEdgeMode(strokePreview, Avalonia.Media.EdgeMode.Aliased);

        var iconPath = preset.PresetIcon ?? Icons.DefaultIcon(preset.Engine);
        var iconOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 0, 4),
            Child = FlossUi.Icon(iconPath, FlossUi.IconDense)
        };

        var previewHost = new Grid
        {
            Height = BrushPresetPreviewRenderHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        previewHost.Children.Add(strokePreview);
        previewHost.Children.Add(iconOverlay);

        var nameText = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var nameRow = new Border
        {
            Padding = new Thickness(8, 4),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = nameText
        };

        var content = new Grid
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            RowDefinitions = new RowDefinitions("Auto,Auto")
        };
        Grid.SetRow(previewHost, 0);
        Grid.SetRow(nameRow, 1);
        content.Children.Add(previewHost);
        content.Children.Add(nameRow);

        var row = new Button
        {
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(4),
            Content = content,
            Tag = preset.Id,
        };
        ApplyBrushPresetRowActive(row, isActive);
        row.Click += (_, _) => ActivatePreset(group, preset);
        EnablePresetDragAndReorder(row, group, preset);
        row.ContextMenu = BuildPresetContextMenu(group, preset);
        return row;
    }

    private Button BuildSimplePresetRow(ToolGroup group, ToolPreset preset, bool isActive)
    {
        var iconPath = preset.PresetIcon ?? Icons.DefaultIcon(preset.Engine);
        var iconElem = Icons.Make(iconPath, 12,
            new SolidColorBrush(Color.Parse(isActive ? TextPrimary : TextSecondary)));
        iconElem.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var nameText = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? TextPrimary : TextSecondary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
            Spacing = 5,
        };
        content.Children.Add(iconElem);
        content.Children.Add(nameText);

        var row = new Button
        {
            Height = 34,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(isActive ? Accent : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Content = content,
            Tag = preset.Id,
        };
        row.Click += (_, _) => ActivatePreset(group, preset);
        EnablePresetDragAndReorder(row, group, preset);
        row.ContextMenu = BuildPresetContextMenu(group, preset);
        return row;
    }

    // ── Preset context menu ─────────────────────────────────────────────────

    private ContextMenu BuildPresetContextMenu(ToolGroup group, ToolPreset preset)
    {
        var menu = new ContextMenu();

        var createItem = new MenuItem { Header = "Create Custom Tool…" };
        createItem.Click += async (_, _) => await CreateCustomToolAsync(group, preset);

        var propertiesItem = new MenuItem { Header = "Tool Properties…" };
        propertiesItem.Click += (_, _) => ShowPresetPropertiesDialog(group, preset);

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += async (_, _) => await RenamePresetPrompt(group, preset);

        var duplicateItem = new MenuItem { Header = "Duplicate" };
        duplicateItem.Click += (_, _) => DuplicatePreset(group, preset);

        var exportItem = new MenuItem { Header = "Export Sub Tool…" };
        exportItem.Click += async (_, _) => await ExportSubToolAsync(group, preset);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeletePreset(group, preset);

        menu.Items.Add(createItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(propertiesItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(duplicateItem);
        menu.Items.Add(exportItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        if (preset.InputProcess.IsBrushFamily() && preset.OutputProcess == OutputProcessType.DirectDraw)
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

    private async Task ExportSubToolAsync(ToolGroup group, ToolPreset preset)
    {
        CaptureActiveBrushToPresetIfChanged();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Sub Tool",
            FileTypeChoices = [FlossSubToolFileType],
            SuggestedFileName = SafePresetFileName(preset.Name, PresetPackageFormat.SubToolExtension)
        });
        if (file == null) return;

        try
        {
            PresetPackageFormat.ExportSubTool(file.Path.LocalPath, group, preset, _brushAssets);
            _footerStatusText.Text = $"Exported sub tool {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.SubToolExport");
            _footerStatusText.Text = $"Sub tool export error: {ex.Message}";
        }
    }

    private void RestorePresetDefault(ToolGroup group, ToolPreset preset)
    {
        if (!preset.InputProcess.IsBrushFamily() || preset.OutputProcess != OutputProcessType.DirectDraw)
            return;

        if (preset.BrushId != null)
            preset.ClearBrushOverrides();
        else
            preset.RestoreBrushDefaults(ToolGroupConfig.CreateFactorySmudgeOverride());

        App.ToolGroups.Save();

        if (preset.BrushId != null)
            LoadBrushAssets();

        if (_activeToolGroup == group && group.ActivePreset == preset)
            SyncActivePresetToCanvas();
        RefreshGroupPresets();
        _footerStatusText.Text = $"Restored {preset.Name} to defaults";
    }

    private void SavePresetAsDefault(ToolGroup group, ToolPreset preset)
    {
        if (!preset.InputProcess.IsBrushFamily() || preset.OutputProcess != OutputProcessType.DirectDraw)
            return;

        if (!(_activeToolGroup == group && group.ActivePreset == preset))
            ActivatePreset(group, preset);

        if (preset.BrushId != null)
        {
            var asset = _activeBrushAsset ?? _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
            if (asset == null)
            {
                _footerStatusText.Text = $"Could not find brush asset for {preset.Name}";
                return;
            }

            asset.WithPreset(CurrentBrushFromUi());
            _brushLibrary.Save(asset);
            preset.ClearBrushOverrides();
            App.ToolGroups.Save();
            LoadBrushAssets();
        }
        else
        {
            CaptureActiveBrushToPresetIfChanged();
            preset.SaveBrushOverrideAsDefault();
            App.ToolGroups.Save();
        }

        SyncActivePresetToCanvas();
        RefreshGroupPresets();
        _footerStatusText.Text = $"Saved {preset.Name} as new default";
    }

    private void SyncActivePresetToCanvas()
    {
        var active = _activeToolGroup?.ActivePreset;
        if (active == null || !active.InputProcess.IsBrushFamily() || active.OutputProcess != OutputProcessType.DirectDraw) return;

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

        UpdateStatus();
        RefreshToolProperties();
        SyncNodeGraphDockToActiveBrush(force: true);
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
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var inputPicker = new ComboBox
        {
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

        // Filter input options based on selected output (CSP-style coupling)
        void RefreshInputPicker()
        {
            var selectedOutput = (OutputProcessType)(outputPicker.SelectedItem ?? preset.OutputProcess);
            var validInputs = ProcessCompatibility.ValidInputsFor(selectedOutput);
            inputPicker.ItemsSource = validInputs;
            
            // If current input is invalid for new output, select default
            var currentInput = (InputProcessType?)inputPicker.SelectedItem ?? preset.InputProcess;
            if (!validInputs.Contains(currentInput))
                inputPicker.SelectedItem = ProcessCompatibility.DefaultInputFor(selectedOutput);
            else
                inputPicker.SelectedItem = currentInput;
                
            // Disable input picker for locked outputs
            inputPicker.IsEnabled = !ProcessCompatibility.LockedOutputs.Contains(selectedOutput);
        }
        
        outputPicker.SelectionChanged += (_, _) => RefreshInputPicker();
        RefreshInputPicker(); // Initial population

        var saveBtn = new Button
        {
            Content = "Save",
            Height = 28,
            Padding = new Thickness(16, 7),
            FontSize = 11,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Classes = { "primary" }
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height = 28,
            Padding = new Thickness(16, 7),
            FontSize = 11,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Classes = { "outline" }
        };

        string? selectedIcon = preset.PresetIcon;
        Action refreshIconPicker = null!;
        var iconPickerPanel = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        refreshIconPicker = () =>
        {
            iconPickerPanel.Children.Clear();
            foreach (var (iName, iPath) in Icons.ToolIcons)
            {
                var iSelected = iPath == selectedIcon;
                var iBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(4),
                    Content = Icons.Make(iPath, 14, new SolidColorBrush(Color.Parse(iSelected ? Accent : TextSecondary))),
                    Background = new SolidColorBrush(Color.Parse(iSelected ? AccentSoft : Bg2)),
                    BorderBrush = new SolidColorBrush(Color.Parse(iSelected ? Accent : Stroke)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 3, 3)
                };
                ToolTip.SetTip(iBtn, iName);
                var capturedPath = iPath;
                iBtn.Click += (_, _) => { selectedIcon = capturedPath; refreshIconPicker(); };
                iconPickerPanel.Children.Add(iBtn);
            }
        };
        refreshIconPicker();

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
                new TextBlock { Text = "Input Process", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                inputPicker,
                new TextBlock { Text = "Output Process", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                outputPicker,
                new TextBlock { Text = "Tool Icon", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                iconPickerPanel,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancelBtn, saveBtn } }
            }
            }
        };

        saveBtn.Click += (_, _) =>
        {
            preset.Name = nameBox.Text?.Trim() ?? preset.Name;
            preset.InputProcess = (InputProcessType)(inputPicker.SelectedItem ?? preset.InputProcess);
            preset.OutputProcess = (OutputProcessType)(outputPicker.SelectedItem ?? preset.OutputProcess);
            preset.PresetIcon = selectedIcon;
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
        var ok = new Button { Content = "Rename", Margin = new Thickness(12, 0, 12, 12), Classes = { "primary" } };
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tcs.TrySetResult(tb.Text); dialog.Close(); } };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Content = new StackPanel { Children = { tb, ok } };
        await dialog.ShowDialog(this);

        var newName = (await tcs.Task)?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == preset.Name) return;
        preset.Name = newName;

        // Also rename the BrushAsset.Preset so the UI label stays in sync
        if (preset.BrushId != null)
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
            if (asset != null)
            {
                asset.Preset = asset.Preset with { Name = newName };
                _brushLibrary.Save(asset);
            }
        }

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
            InputProcess = preset.InputProcess,
            OutputProcess = preset.OutputProcess,
            BrushOverride = preset.BrushOverride?.DeepClone(),
            Tolerance = preset.Tolerance,
            SelectMode = preset.SelectMode,
            GradientType = preset.GradientType,
            ShapeKind = preset.ShapeKind,
            ShapeDrawMode = preset.ShapeDrawMode,
            ShapeStrokeWidth = preset.ShapeStrokeWidth,
            PolylineClosePath = preset.PolylineClosePath,
            PolylineStrokeWidth = preset.PolylineStrokeWidth
        };

        if (preset.BrushId != null)
        {
            var sourceAsset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
            if (sourceAsset != null)
            {
                var assetCopy = sourceAsset.CloneForSaveAs(copy.Name);
                _brushLibrary.Save(assetCopy);
                _brushAssets = [.._brushAssets, assetCopy];
                copy.BrushId = assetCopy.Id;
            }
            else
            {
                copy.BrushId = preset.BrushId;
            }
        }

        group.Presets.Add(copy);

        // Place the copy in the same category as the source
        var sourceCat = group.Categories.FirstOrDefault(c => c.PresetIds.Contains(preset.Id));
        if (sourceCat != null && !sourceCat.PresetIds.Contains(copy.Id))
        {
            var sourceIdx = sourceCat.PresetIds.IndexOf(preset.Id);
            sourceCat.PresetIds.Insert(sourceIdx + 1, copy.Id);
        }
        else if (_selectedCategory != null)
        {
            var cat = group.Categories.FirstOrDefault(c => c.Name == _selectedCategory);
            if (cat != null && !cat.PresetIds.Contains(copy.Id))
                cat.PresetIds.Add(copy.Id);
        }

        ActivatePreset(group, copy);
    }

    private void DeletePreset(ToolGroup group, ToolPreset preset)
    {
        var wasActive = _activeToolGroup == group && group.LastActivePresetId == preset.Id;
        if (wasActive)
            CaptureBrushToPresetIfChanged(preset);

        if (preset.BrushId != null)
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
            if (asset != null) _brushLibrary.Delete(asset);
        }
        // Save category info before removing preset from it
        var deletedCat = group.Categories.FirstOrDefault(c => c.PresetIds.Contains(preset.Id));
        var deletedCatIdx = deletedCat?.PresetIds.IndexOf(preset.Id) ?? -1;
        group.Presets.Remove(preset);
        deletedCat?.PresetIds.Remove(preset.Id);

        if (group.Presets.Count == 0)
        {
            App.ToolGroups.Groups.Remove(group);
            if (_activeToolGroup == group)
            {
                var next = App.ToolGroups.Groups.FirstOrDefault();
                if (next != null)
                {
                    var fallback = next.ActivePreset ?? next.Presets.FirstOrDefault();
                    if (fallback != null) ActivatePreset(next, fallback);
                }
            }
            BuildToolRail();
        }
        else
        {
            if (group.LastActivePresetId == preset.Id)
            {
                // Activate the next preset in the same category
                if (deletedCat != null && deletedCatIdx >= 0 && deletedCat.PresetIds.Count > 0)
                {
                    var nextId = deletedCatIdx < deletedCat.PresetIds.Count
                        ? deletedCat.PresetIds[deletedCatIdx]
                        : deletedCat.PresetIds[^1];
                    group.LastActivePresetId = nextId;
                }
                else
                {
                    // Category is now empty — move to the first non-empty category
                    group.LastActivePresetId = null;
                    var nextCat = group.Categories.FirstOrDefault(c => c.PresetIds.Count > 0);
                    group.LastActiveCategoryName = nextCat?.Name;
                }
            }
            if (_activeToolGroup == group)
            {
                var active = group.ActivePreset ?? group.Presets.FirstOrDefault();
                if (active != null) ActivatePreset(group, active);
            }
        }
        App.ToolGroups.Save();
        RefreshGroupPresets();
    }

    private async System.Threading.Tasks.Task CreateCustomToolAsync(ToolGroup group, ToolPreset preset)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Title = "Create Custom Tool",
            Width = 300,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        var tb = new TextBox { Margin = new Thickness(12), Text = "Custom Tool" };
        var ok = new Button { Content = "Create", Margin = new Thickness(12, 0, 12, 12), Classes = { "primary" } };
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dialog.Close(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { tcs.TrySetResult(tb.Text); dialog.Close(); } };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        dialog.Content = new StackPanel { Children = { tb, ok } };
        await dialog.ShowDialog(this);

        var name = (await tcs.Task)?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        CaptureActiveBrushToPresetIfChanged();

        var brushPreset = new BrushPreset(name, 40, 1.0, 0.9, 0.10, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Smoothing = 0.3
        };
        if (preset.BrushBlendMode.HasValue)
            brushPreset = brushPreset with { BlendMode = preset.BrushBlendMode.Value };

        var asset = BrushAsset.FromPreset(brushPreset, category: _selectedCategory);
        _brushLibrary.Save(asset);
        _brushAssets = [.._brushAssets, asset];

        var newPreset = new ToolPreset
        {
            Name = name,
            InputProcess = InputProcessType.Brush,
            OutputProcess = OutputProcessType.DirectDraw,
            BrushId = asset.Id,
            BrushBlendMode = preset.BrushBlendMode
        };

        group.Presets.Add(newPreset);

        var cat = group.Categories.FirstOrDefault(c => c.Name == _selectedCategory);
        if (cat != null && !cat.PresetIds.Contains(newPreset.Id))
            cat.PresetIds.Add(newPreset.Id);

        App.ToolGroups.Save();
        RefreshGroupPresets();
        ActivatePreset(group, newPreset);
    }

    // ── Drag-and-drop helpers ─────────────────────────────────────────────────

    private void EnablePresetDragAndReorder(Button row, ToolGroup group, ToolPreset preset)
    {
        DragDrop.SetAllowDrop(row, true);
        var presetId = preset.Id;
        var origBorderBrush = row.BorderBrush;
        var origBorderThickness = row.BorderThickness;

        PointerPressedEventArgs? pressEvent = null;
        var dragging = false;

        // Capture press in tunnel phase before Button steals pointer focus.
        row.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                pressEvent = e;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        row.PointerMoved += (_, e) =>
        {
            if (dragging || pressEvent == null) return;
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { pressEvent = null; return; }
            var delta = e.GetPosition(row) - pressEvent.GetPosition(row);
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;

            dragging = true;
            var savedPress = pressEvent;
            pressEvent = null;
            var item = new DataTransferItem();
            item.Set(PresetIdDragFormat, presetId);
            var data = new DataTransfer();
            data.Add(item);
            DragDrop.DoDragDropAsync(savedPress, data, DragDropEffects.Move)
                .ContinueWith(t =>
                {
                    if (t.Exception != null)
                        CrashLog.Write(t.Exception, "MainWindow.BrushLibrary.DragDrop");
                    dragging = false;
                }, TaskContinuationOptions.ExecuteSynchronously);
        };

        row.PointerReleased += (_, _) => { pressEvent = null; dragging = false; };

        row.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            if (!e.DataTransfer.Contains(PresetIdDragFormat)) return;
            e.DragEffects = DragDropEffects.Move;
            var draggedId = e.DataTransfer.TryGetValue<string>(PresetIdDragFormat);
            if (string.IsNullOrEmpty(draggedId) || draggedId == presetId) return;
            var before = e.GetPosition(row).Y < row.Bounds.Height / 2;
            row.BorderThickness = before ? new Thickness(1, 3, 1, 1) : new Thickness(1, 1, 1, 3);
            row.BorderBrush = new SolidColorBrush(Color.Parse(Accent));
            e.Handled = true;
        });

        row.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            row.BorderBrush = origBorderBrush;
            row.BorderThickness = origBorderThickness;
        });

        row.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) =>
        {
            row.BorderBrush = origBorderBrush;
            row.BorderThickness = origBorderThickness;
            if (!e.DataTransfer.Contains(PresetIdDragFormat)) return;
            var draggedId = e.DataTransfer.TryGetValue<string>(PresetIdDragFormat);
            if (string.IsNullOrEmpty(draggedId) || draggedId == presetId) return;
            var before = e.GetPosition(row).Y < row.Bounds.Height / 2;
            MovePresetRelativeTo(group, draggedId, presetId, before);
            e.Handled = true;
        });
    }

    private void MovePresetRelativeTo(ToolGroup group, string draggedId, string targetId, bool insertBefore)
    {
        if (draggedId == targetId) return;

        ToolCategory? draggedCat = null, targetCat = null;
        foreach (var c in group.Categories)
        {
            if (c.PresetIds.Contains(draggedId)) draggedCat = c;
            if (c.PresetIds.Contains(targetId)) targetCat = c;
        }

        if (targetCat == null)
        {
            // Neither is in a category — reorder in group.Presets
            var dp = group.Presets.FirstOrDefault(p => p.Id == draggedId);
            var tp = group.Presets.FirstOrDefault(p => p.Id == targetId);
            if (dp == null || tp == null) return;
            group.Presets.Remove(dp);
            var idx = group.Presets.IndexOf(tp);
            group.Presets.Insert(Math.Clamp(insertBefore ? idx : idx + 1, 0, group.Presets.Count), dp);
        }
        else
        {
            draggedCat?.PresetIds.Remove(draggedId);
            var tIdx = targetCat.PresetIds.IndexOf(targetId);
            var ins = tIdx < 0 ? targetCat.PresetIds.Count : (insertBefore ? tIdx : tIdx + 1);
            targetCat.PresetIds.Insert(Math.Clamp(ins, 0, targetCat.PresetIds.Count), draggedId);
            if (draggedCat != null && draggedCat != targetCat)
                _selectedCategory = targetCat.Name;
        }

        App.ToolGroups.Save();
        RefreshGroupPresets();
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
        var ok = new Button { Content = "Create", Margin = new Thickness(12, 0, 12, 12), Classes = { "primary" } };
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
        var ok = new Button { Content = "Rename", Margin = new Thickness(12, 0, 12, 12), Classes = { "primary" } };
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

    private ContextMenu BuildCategoryContextMenu(ToolGroup group, ToolCategory cat)
    {
        var menu = new ContextMenu();

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => RenameCategoryPrompt(group, cat);

        var exportItem = new MenuItem { Header = "Export Tool Group…" };
        exportItem.Click += async (_, _) => await ExportSubToolGroupAsync(group, cat);

        var deleteItem = new MenuItem { Header = "Delete Category and Brushes" };
        deleteItem.Click += (_, _) => DeleteCategory(group, cat);

        menu.Items.Add(renameItem);
        menu.Items.Add(exportItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        return menu;
    }

    private async Task ExportSubToolGroupAsync(ToolGroup group, ToolCategory cat)
    {
        CaptureActiveBrushToPresetIfChanged();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Tool Group",
            FileTypeChoices = [FlossSubToolGroupFileType],
            SuggestedFileName = SafePresetFileName(cat.Name, PresetPackageFormat.SubToolGroupExtension)
        });
        if (file == null) return;

        try
        {
            PresetPackageFormat.ExportSubToolGroup(file.Path.LocalPath, group, cat, _brushAssets);
            _footerStatusText.Text = $"Exported tool group {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.ToolGroupExport");
            _footerStatusText.Text = $"Tool group export error: {ex.Message}";
        }
    }

    private void DeleteCategory(ToolGroup group, ToolCategory cat)
    {
        var presetIds = cat.PresetIds.ToList();
        var activeId = group.LastActivePresetId;
        if (activeId != null && presetIds.Contains(activeId))
        {
            var leaving = group.Presets.FirstOrDefault(p => p.Id == activeId);
            if (leaving != null)
                CaptureBrushToPresetIfChanged(leaving);
        }

        group.Categories.Remove(cat);

        var wasActiveDeleted = false;
        foreach (var presetId in presetIds)
        {
            var preset = group.Presets.FirstOrDefault(p => p.Id == presetId);
            if (preset == null) continue;

            if (preset.BrushId != null)
            {
                var asset = _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
                if (asset != null) _brushLibrary.Delete(asset);
            }

            group.Presets.Remove(preset);
            if (group.LastActivePresetId == preset.Id)
            {
                group.LastActivePresetId = null;
                wasActiveDeleted = true;
            }
        }

        if (_selectedCategory == cat.Name)
            _selectedCategory = group.Categories.FirstOrDefault()?.Name;

        App.ToolGroups.Save();
        LoadBrushAssets();

        if (wasActiveDeleted && _activeToolGroup == group)
        {
            var next = group.ActivePreset ?? group.Presets.FirstOrDefault();
            if (next != null) ActivatePreset(group, next);
        }

        _footerStatusText.Text = $"Deleted category \"{cat.Name}\" and {presetIds.Count} brush{(presetIds.Count == 1 ? "" : "es")}";
    }

    private void EnableCategoryReorder(Button btn, ToolGroup group, string cat)
    {
        DragDrop.SetAllowDrop(btn, true);
        btn.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(btn).Properties.IsLeftButtonPressed) return;
            var item = new DataTransferItem();
            item.Set(CategoryGroupDragFormat, $"{group.Id}\0{cat}");
            var data = new DataTransfer();
            data.Add(item);
            DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move)
                .FireAndForget("MainWindow.BrushLibrary.CategoryDragDrop");
        };
        btn.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(CategoryGroupDragFormat)) return;
            var val = e.DataTransfer.TryGetValue<string>(CategoryGroupDragFormat);
            if (string.IsNullOrEmpty(val)) return;
            var sep = val.IndexOf('\0');
            if (sep < 0) return;
            if (val[..sep] != group.Id || val[(sep + 1)..] == cat) return;
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        });
        btn.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(CategoryGroupDragFormat)) return;
            var val = e.DataTransfer.TryGetValue<string>(CategoryGroupDragFormat);
            if (string.IsNullOrEmpty(val)) return;
            var sep = val.IndexOf('\0');
            if (sep < 0) return;
            if (val[..sep] != group.Id) return;
            var dragged = val[(sep + 1)..];
            if (dragged == cat) return;
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

    private void ShowBrushPanelMenu(Button anchor)
    {
        var menu = new ContextMenu();

        var importAbr = new MenuItem { Header = "Import .abr brush pack…" };
        importAbr.Click += async (_, _) => await ImportAbrAsync();

        var importTool = new MenuItem { Header = "Import Tool (.flbr)…" };
        importTool.Click += async (_, _) => await ImportSubToolAsync();

        var importToolGroup = new MenuItem { Header = "Import Tool Group (.flbrg)…" };
        importToolGroup.Click += async (_, _) => await ImportSubToolGroupAsync();

        menu.Items.Add(importAbr);
        menu.Items.Add(importTool);
        menu.Items.Add(importToolGroup);

        var group = _activeToolGroup;
        if (group != null)
        {
            menu.Items.Add(new Separator());

            var exportGroup = new MenuItem { Header = $"Export Tool Group \"{group.Name}\"…" };
            exportGroup.Click += async (_, _) => await ExportCurrentToolGroupAsync();
            menu.Items.Add(exportGroup);

            var activePreset = group.ActivePreset;
            if (activePreset != null)
            {
                var exportTool = new MenuItem { Header = $"Export Tool \"{activePreset.Name}\"…" };
                exportTool.Click += async (_, _) => await ExportSubToolAsync(group, activePreset);
                menu.Items.Add(exportTool);
            }
        }

        menu.Open(anchor);
    }

    private async Task ImportSubToolAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Tool",
            AllowMultiple = false,
            FileTypeFilter = [FlossSubToolFileType]
        });
        if (files.Count == 0) return;

        try
        {
            using var busy = BeginBusy("Importing sub tool…");
            var (importedGroup, importedPreset, brushAssets) = await System.Threading.Tasks.Task.Run(
                () => PresetPackageFormat.ImportSubTool(files[0].Path.LocalPath));

            busy.Report("Saving imported brushes…");

            foreach (var asset in brushAssets)
                _brushLibrary.Save(asset);

            var targetGroup = _activeToolGroup
                ?? App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Brush)
                ?? App.ToolGroups.Groups.FirstOrDefault();
            if (targetGroup == null) return;

            importedPreset.Id = Guid.NewGuid().ToString("N");
            targetGroup.Presets.Add(importedPreset);

            var catName = importedGroup.Categories.FirstOrDefault(c => c.PresetIds.Any())?.Name ?? _selectedCategory;
            if (catName != null)
            {
                var cat = targetGroup.Categories.FirstOrDefault(c => c.Name == catName);
                if (cat == null) { cat = new ToolCategory { Name = catName }; targetGroup.Categories.Add(cat); }
                if (!cat.PresetIds.Contains(importedPreset.Id)) cat.PresetIds.Add(importedPreset.Id);
            }

            App.ToolGroups.Save();
            LoadBrushAssets();
            _footerStatusText.Text = $"Imported tool \"{importedPreset.Name}\"";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.SubToolImport");
            _footerStatusText.Text = $"Import error: {ex.Message}";
        }
    }

    private async Task ImportSubToolGroupAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Tool Group",
            AllowMultiple = false,
            FileTypeFilter = [FlossSubToolGroupFileType]
        });
        if (files.Count == 0) return;

        try
        {
            using var busy = BeginBusy("Importing tool group…");
            var (importedGroups, brushAssets) = await System.Threading.Tasks.Task.Run(
                () => PresetPackageFormat.ImportSubToolGroup(files[0].Path.LocalPath));

            busy.Report("Saving imported brushes…");

            foreach (var asset in brushAssets)
                _brushLibrary.Save(asset);

            foreach (var importedGroup in importedGroups)
            {
                var existing = App.ToolGroups.Groups.FirstOrDefault(g => g.Name == importedGroup.Name);
                if (existing != null)
                {
                    var idMap = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var preset in importedGroup.Presets)
                    {
                        var newId = Guid.NewGuid().ToString("N");
                        idMap[preset.Id] = newId;
                        preset.Id = newId;
                        existing.Presets.Add(preset);
                    }
                    foreach (var cat in importedGroup.Categories)
                    {
                        var existingCat = existing.Categories.FirstOrDefault(c => c.Name == cat.Name);
                        if (existingCat == null) { existingCat = new ToolCategory { Name = cat.Name }; existing.Categories.Add(existingCat); }
                        foreach (var oldId in cat.PresetIds)
                            if (idMap.TryGetValue(oldId, out var newId) && !existingCat.PresetIds.Contains(newId))
                                existingCat.PresetIds.Add(newId);
                    }
                }
                else
                {
                    importedGroup.Id = Guid.NewGuid().ToString("N");
                    App.ToolGroups.Groups.Add(importedGroup);
                }
            }

            App.ToolGroups.Save();
            LoadBrushAssets();
            BuildToolRail();
            _footerStatusText.Text = $"Imported tool group";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.ToolGroupImport");
            _footerStatusText.Text = $"Import error: {ex.Message}";
        }
    }

    private async Task ExportCurrentToolGroupAsync()
    {
        var group = _activeToolGroup;
        if (group == null) return;
        CaptureActiveBrushToPresetIfChanged();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Tool Group",
            FileTypeChoices = [FlossSubToolGroupFileType],
            SuggestedFileName = SafePresetFileName(group.Name, PresetPackageFormat.SubToolGroupExtension)
        });
        if (file == null) return;

        try
        {
            PresetPackageFormat.ExportSubToolGroup(file.Path.LocalPath, group, _brushAssets);
            _footerStatusText.Text = $"Exported \"{group.Name}\" to {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.ToolGroupExportCurrent");
            _footerStatusText.Text = $"Export error: {ex.Message}";
        }
    }

    internal void EnableCategoryPromoteDrop(Control target, ToolGroup? targetGroup)
    {
        DragDrop.SetAllowDrop(target, true);
        var origBackground = (target as Button)?.Background;
        var origBorder = (target as Button)?.BorderBrush;
        var origThickness = (target as Button)?.BorderThickness ?? new Thickness(0);

        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!e.DataTransfer.Contains(CategoryGroupDragFormat)) return;
            var val = e.DataTransfer.TryGetValue<string>(CategoryGroupDragFormat);
            if (string.IsNullOrEmpty(val)) return;
            var sep = val.IndexOf('\0');
            if (sep < 0) return;
            if (targetGroup?.Id == val[..sep]) return;
            e.DragEffects = DragDropEffects.Move;
            if (target is Button b)
            {
                b.Background = new SolidColorBrush(Color.Parse(AccentSoft));
                b.BorderBrush = new SolidColorBrush(Color.Parse(Accent));
                b.BorderThickness = new Thickness(1);
            }
            e.Handled = true;
        });
        target.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            if (target is Button b)
            {
                b.Background = origBackground;
                b.BorderBrush = origBorder;
                b.BorderThickness = origThickness;
            }
        });
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (target is Button b)
            {
                b.Background = origBackground;
                b.BorderBrush = origBorder;
                b.BorderThickness = origThickness;
            }
            if (!e.DataTransfer.Contains(CategoryGroupDragFormat)) return;
            var val = e.DataTransfer.TryGetValue<string>(CategoryGroupDragFormat);
            if (string.IsNullOrEmpty(val)) return;
            var sep = val.IndexOf('\0');
            if (sep < 0) return;
            PromoteCategoryToGroup(val[..sep], val[(sep + 1)..], targetGroup);
            e.Handled = true;
        });
    }

    private void PromoteCategoryToGroup(string sourceGroupId, string catName, ToolGroup? targetGroup)
    {
        var sourceGroup = App.ToolGroups.Groups.FirstOrDefault(g => g.Id == sourceGroupId);
        if (sourceGroup == null) return;
        if (targetGroup != null && targetGroup.Id == sourceGroupId) return;

        var sourceCat = sourceGroup.Categories.FirstOrDefault(c => c.Name == catName);
        if (sourceCat == null) return;

        var presetIds = sourceCat.PresetIds.ToList();
        var presets = presetIds
            .Select(id => sourceGroup.Presets.FirstOrDefault(p => p.Id == id))
            .OfType<ToolPreset>()
            .ToList();

        foreach (var preset in presets)
        {
            sourceGroup.Presets.Remove(preset);
            foreach (var c in sourceGroup.Categories)
                c.PresetIds.Remove(preset.Id);
        }
        sourceGroup.Categories.Remove(sourceCat);

        if (targetGroup == null)
        {
            targetGroup = new ToolGroup
            {
                Name = catName,
                DefaultEngine = sourceGroup.DefaultEngine,
                Presets = []
            };
            App.ToolGroups.Groups.Add(targetGroup);
        }

        foreach (var preset in presets)
            targetGroup.Presets.Add(preset);

        var targetCat = targetGroup.Categories.FirstOrDefault(c => c.Name == catName);
        if (targetCat == null)
        {
            targetCat = new ToolCategory { Name = catName };
            targetGroup.Categories.Add(targetCat);
        }
        foreach (var id in presetIds)
            if (!targetCat.PresetIds.Contains(id))
                targetCat.PresetIds.Add(id);

        var sourceWasActive = _activeToolGroup == sourceGroup;
        if (!sourceGroup.Presets.Any() && !sourceGroup.Categories.Any())
        {
            App.ToolGroups.Groups.Remove(sourceGroup);
            if (sourceWasActive) _activeToolGroup = null;
        }

        App.ToolGroups.Save();
        BuildToolRail();

        _selectedCategory = catName;
        if (_activeToolGroup != targetGroup)
        {
            var preset = targetGroup.ActivePreset ?? targetGroup.Presets.FirstOrDefault();
            if (preset != null)
                ActivatePreset(targetGroup, preset);
            else
            {
                _activeToolGroup = targetGroup;
                RefreshGroupPresets();
            }
        }
        else
        {
            RefreshGroupPresets();
        }
    }

    private void LoadBrushAssets()
    {
        _brushAssets = _brushLibrary.Load();
        _dirtyBrushAssetIds.Clear();
        App.ToolGroups.SyncWithAssets(_brushAssets, _activeToolGroup);
        App.ToolGroups.Save();
        RefreshGroupPresets();
        if (_nodeGraphDockVisible)
            InvalidateNodeGraphDockState();
    }

    private void SelectInitialTool()
    {
        var cfg = App.Config;
        var group = cfg.LastToolGroupId == null
            ? null
            : App.ToolGroups.Groups.FirstOrDefault(g => g.Id == cfg.LastToolGroupId);

        group ??= cfg.LastToolPresetId == null
            ? null
            : App.ToolGroups.Groups.FirstOrDefault(g => g.Presets.Any(p => p.Id == cfg.LastToolPresetId));

        if (group != null)
        {
            var preset = cfg.LastToolPresetId == null
                ? group.ActivePreset
                : group.Presets.FirstOrDefault(p => p.Id == cfg.LastToolPresetId) ?? group.ActivePreset;
            if (preset != null)
            {
                _selectedCategory = ResolveStartupCategory(group, cfg.LastToolCategoryName, preset);
                ActivatePreset(group, preset);
                return;
            }
        }

        SelectInitialBrush();
    }

    private static string? ResolveStartupCategory(ToolGroup group, string? preferredCategory, ToolPreset preset)
    {
        if (preferredCategory != null &&
            group.Categories.Any(c => c.Name == preferredCategory && c.PresetIds.Contains(preset.Id)))
            return preferredCategory;

        return group.Categories.FirstOrDefault(c => c.PresetIds.Contains(preset.Id))?.Name
            ?? group.Categories.FirstOrDefault()?.Name;
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

    private void SaveActiveToolSelection()
    {
        var group = _activeToolGroup;
        var preset = group?.ActivePreset;
        if (group == null || preset == null) return;

        App.Config.LastToolGroupId = group.Id;
        App.Config.LastToolPresetId = preset.Id;
        App.Config.LastToolCategoryName = _selectedCategory;
        App.Config.LastBrushName = _activePreset?.Name ?? App.Config.LastBrushName;
        App.Config.Save();
    }

    private void ApplyBrushAsset(BrushAsset asset)
    {
        _activeBrushAsset = asset;
        ApplyBrushSettings(asset.ToPreset(), syncSliders: true);
    }

    internal void ApplyBrushSettings(BrushPreset preset, bool syncSliders)
        => ApplyBrushSettings(preset, syncSliders, syncToolPropertiesWindow: true);

    private void ApplyBrushSettings(BrushPreset preset, bool syncSliders, bool syncToolPropertiesWindow)
    {
        _activePreset = preset;
        _activeBrushLabel.Text = preset.Name;

        SyncBrushSizeLimits();
        SyncBrushScalarControls(preset);

        var applied = preset with { Color = _canvas.PaintColor };
        _canvas.SetBrush(applied);
        _strokePreview.Brush = applied;
        var activeToolPreset = _activeToolGroup?.ActivePreset;
        if (syncToolPropertiesWindow && activeToolPreset != null && _toolPropsWindow?.CanSyncToolPreset(activeToolPreset) == true)
        {
            _syncingToolPropertyPanel = true;
            try
            {
                _toolPropsWindow.SyncFromPreset(applied with { Color = _canvas.Brush.Color });
            }
            finally
            {
                _syncingToolPropertyPanel = false;
            }
        }
        UpdateStatus();
        RefreshToolProperties();
        SyncNodeGraphDockToActiveBrush();
    }

    private void SyncBrushScalarControls(BrushPreset preset)
    {
        _syncingBrushUi = true;
        try
        {
            _sizeSlider.Value = Math.Clamp(preset.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
            _maxSizePercentSlider.Value = Math.Clamp(
                preset.MaxSizePercent,
                _maxSizePercentSlider.Minimum,
                _maxSizePercentSlider.Maximum);
            _opacitySlider.Value = Math.Clamp(preset.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
            _flowSlider.Value = Math.Clamp(preset.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
            _hardnessSlider.Value = Math.Clamp(preset.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
            _spacingSlider.Value = Math.Clamp(preset.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
            _smoothingSlider.Value = Math.Clamp(preset.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
            _grainSlider.Value = Math.Clamp(preset.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
        }
        finally
        {
            _syncingBrushUi = false;
        }
    }

    private void UpdateCurrentBrush(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncingBrushUi || _syncingToolPropertyPanel) return;
        _activePreset ??= _canvas.Brush;
        var updated = update(_activePreset);
        _activePreset = updated;
        var activeToolPreset = _activeToolGroup?.ActivePreset;
        if (activeToolPreset?.InputProcess.IsBrushFamily() == true &&
            activeToolPreset.OutputProcess == OutputProcessType.DirectDraw)
        {
            activeToolPreset.CaptureFromBrushPreset(updated);
            _lastCapturedBrushByPresetId[activeToolPreset.Id] = BuildBrushCaptureSignature(updated);
            App.ToolGroups.Save();
        }
        else if (activeToolPreset?.OutputProcess is OutputProcessType.ClosedAreaFill
            or OutputProcessType.FloodFill
            or OutputProcessType.Gradient
            or OutputProcessType.Stroke)
        {
            activeToolPreset.BrushOverride ??= new BrushPresetOverrideDocument();
            activeToolPreset.BrushOverride.Opacity = updated.Opacity;
            activeToolPreset.BrushOverride.BlendMode = updated.BlendMode;
            App.ToolGroups.Save();
        }
        ScheduleBrushPresetAutosave();
        ApplyBrushSettings(updated, syncSliders: false);
        RefreshGroupPresets();
        RefreshNodeGraphImageOptions();
    }

    private void UpdateCurrentBrushFromToolProperties(Func<BrushPreset, BrushPreset> update)
    {
        if (_syncingBrushUi || _syncingToolPropertyPanel) return;
        _activePreset ??= _canvas.Brush;
        var updated = update(_activePreset);
        _activePreset = updated;
        var activeToolPreset = _activeToolGroup?.ActivePreset;
        if (activeToolPreset?.InputProcess.IsBrushFamily() == true &&
            activeToolPreset.OutputProcess == OutputProcessType.DirectDraw)
        {
            activeToolPreset.CaptureFromBrushPreset(updated);
            _lastCapturedBrushByPresetId[activeToolPreset.Id] = BuildBrushCaptureSignature(updated);
            App.ToolGroups.Save();
        }
        else if (activeToolPreset?.OutputProcess is OutputProcessType.ClosedAreaFill
            or OutputProcessType.FloodFill
            or OutputProcessType.Gradient
            or OutputProcessType.Stroke)
        {
            activeToolPreset.BrushOverride ??= new BrushPresetOverrideDocument();
            activeToolPreset.BrushOverride.Opacity = updated.Opacity;
            activeToolPreset.BrushOverride.BlendMode = updated.BlendMode;
            App.ToolGroups.Save();
        }
        ScheduleBrushPresetAutosave();
        ApplyBrushSettings(updated, syncSliders: false, syncToolPropertiesWindow: false);
        SyncBrushScalarControls(updated);
        RefreshGroupPresets();
        RefreshNodeGraphImageOptions();
    }

    private void OpenToolProperties()
    {
        var toolPreset = _activeToolGroup?.ActivePreset;
        if (toolPreset == null) return;

        var brushPreset = _activePreset ?? _canvas.Brush;

        if (_toolPropsWindow != null)
        {
            if (!_toolPropsWindow.CanSyncToolPreset(toolPreset))
            {
                _toolPropsWindow.Close();
                _toolPropsWindow = null;
            }
            else
            {
                _toolPropsWindow.SyncFromToolPreset(toolPreset);
                if (brushPreset != null)
                {
                    _syncingToolPropertyPanel = true;
                    try
                    {
                        _toolPropsWindow.SyncFromPreset(brushPreset with { Color = _canvas.Brush.Color });
                    }
                    finally
                    {
                        _syncingToolPropertyPanel = false;
                    }
                }
                _toolPropsWindow.Activate();
                return;
            }
        }

        _toolPropsWindow = new ToolPropertiesWindow(toolPreset, brushPreset, (tp, brushUpdate) =>
        {
            if (_syncingBrushUi) return;
            if (_activeToolGroup?.ActivePreset != tp) return;

            if (brushUpdate != null)
            {
                // Brush tool: apply only the changed property via UpdateCurrentBrush so nothing else resets
                UpdateCurrentBrushFromToolProperties(brushUpdate);
            }
            else
            {
                // Non-brush tool: paint settings travel through CommitTool/_toolPreset
                if (tp.BrushOverride?.Opacity is { } opacity)
                    UpdateCurrentBrush(p => p with { Opacity = opacity });
                if (tp.BrushOverride?.BlendMode is { } blendMode)
                    UpdateCurrentBrush(p => p with { BlendMode = blendMode });
            }

            if (_activeToolGroup?.ActivePreset == tp && tp.OutputProcess != OutputProcessType.DirectDraw)
                _canvas.SetActiveTool(ToolForPreset(tp), tp);

            if (brushUpdate == null)
                App.ToolGroups.Save();

            RefreshToolProperties();
        }, SaveNodeGraphAsNewBrushPreset, OpenBrushTipGraphEditor);
        _toolPropsWindow.Closed += (_, _) => _toolPropsWindow = null;
        _toolPropsWindow.Show(this);
    }

    private void SaveNodeGraphAsNewBrushPreset(BrushTipNodeGraph graph, string name)
    {
        if (graph.Validate().Count > 0) return;

        var group = _activeToolGroup
            ?? App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Brush)
            ?? App.ToolGroups.Groups.FirstOrDefault();
        if (group == null) return;

        var clone = graph.DeepClone();
        clone.BuiltInShape = null;
        clone.BuiltInAspectRatio = 1.0f;
        var tip = (IBrushTip)new NodeBrushTip(clone);
        var brushName = string.IsNullOrWhiteSpace(name) ? "Custom Node Graph" : name.Trim();
        var source = _activePreset ?? _canvas.Brush ?? BrushPreset.Defaults[0];
        var brushPreset = source with
        {
            Name = brushName,
            Tip = tip,
            Shape = null,
            Tips = [],
            TipSelectionMode = BrushTipSelectionMode.Single
        };

        var category = _selectedCategory
            ?? group.LastActiveCategoryName
            ?? group.Categories.FirstOrDefault()?.Name;
        var asset = new BrushAsset
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = category,
            Preset = brushPreset,
            Tip = BrushTipData.FromTip(tip),
            ShapeData = null
        };
        _brushLibrary.Save(asset);
        _brushAssets = [.._brushAssets, asset];

        var toolPreset = new ToolPreset
        {
            Name = brushName,
            InputProcess = InputProcessType.Brush,
            OutputProcess = OutputProcessType.DirectDraw,
            BrushId = asset.Id,
            BrushBlendMode = brushPreset.BlendMode
        };
        toolPreset.CaptureFromBrushPreset(brushPreset);
        group.Presets.Add(toolPreset);

        if (category != null)
        {
            var cat = group.Categories.FirstOrDefault(c => c.Name == category);
            if (cat != null && !cat.PresetIds.Contains(toolPreset.Id))
                cat.PresetIds.Add(toolPreset.Id);
        }

        App.ToolGroups.Save();
        RefreshGroupPresets();
        ActivatePreset(group, toolPreset);
        _footerStatusText.Text = $"Saved brush {brushName}";
    }

    private static BrushTipNodeGraph GraphForBrushTip(IBrushTip tip)
        => tip switch
        {
            ProceduralBrushTip proc => proc.Graph.DeepClone(),
            NodeBrushTip node => node.Graph.DeepClone(),
            ImageBrushTip img => BrushTipNodeGraph.FromImageTip(img.GetPngBytes()),
            _ => BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Circle)
        };

    private static IReadOnlyList<BrushTipData> PreserveMaterialBrushTips(BrushPreset preset)
        => BrushMaterialTips.PreserveForPreset(preset);

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
        var group = _activeToolGroup;
        var preset = group?.ActivePreset;
        if (group == null || preset == null) return;
        DuplicatePreset(group, preset);
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
        var current = CurrentBrushFromUi();
        var updated = current with
        {
            Tip = new NodeBrushTip(BrushTipNodeGraph.FromImageTip(tip.PngBytes)),
            Tips = [..current.Tips.Where(t => t.Kind == BrushTipStorageKind.EmbeddedPng).Select(t => t.DeepClone()), tip.DeepClone()]
        };
        UpdateCurrentBrush(_ => updated);
        RefreshGroupPresets();
        _footerStatusText.Text = $"Added PNG sampler to {updated.Name}";
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

        using var busy = BeginBusy("Importing brush pack…");
        var imported = 0;
        var lastDiag = "";
        var fileImports = new List<(string CategoryName, List<string> AssetIds)>();

        foreach (var file in files)
        {
            busy.Report($"Reading {file.Name}…");
            await using var stream = await file.OpenReadAsync();
            List<Brushes.BrushAsset> brushes;
            try { brushes = await System.Threading.Tasks.Task.Run(() => AbrImporter.Import(stream, out lastDiag)); }
            catch (Exception ex) { CrashLog.Write(ex, "MainWindow.BrushLibrary.AbrImport"); continue; }

            busy.Report($"Saving {file.Name}…");

            var categoryName = Path.GetFileNameWithoutExtension(file.Name);
            var assetIds = new List<string>();
            foreach (var asset in brushes)
            {
                _brushLibrary.Save(asset);
                assetIds.Add(asset.Id);
                imported++;
            }
            if (assetIds.Count > 0)
                fileImports.Add((categoryName, assetIds));
        }

        if (imported > 0)
        {
            LoadBrushAssets();

            // Gather all imported presets from wherever SyncWithAssets placed them,
            // then consolidate them into a single named category in the brush group.
            var brushGroup = App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Brush)
                ?? App.ToolGroups.Groups.First();

            foreach (var (catName, ids) in fileImports)
            {
                foreach (var assetId in ids)
                {
                    // Search every group — eraser-kind assets may have landed in eraserGroup.
                    ToolPreset? preset = null;
                    ToolGroup? sourceGroup = null;
                    foreach (var g in App.ToolGroups.Groups)
                    {
                        preset = g.Presets.FirstOrDefault(p => p.BrushId == assetId);
                        if (preset != null) { sourceGroup = g; break; }
                    }
                    if (preset == null) continue;

                    // Pull it out of the source group if it's not already in the brush group.
                    if (sourceGroup != brushGroup)
                    {
                        sourceGroup!.Presets.Remove(preset);
                        foreach (var c in sourceGroup.Categories) c.PresetIds.Remove(preset.Id);
                        brushGroup.Presets.Add(preset);
                    }

                    // Place in the named category.
                    foreach (var c in brushGroup.Categories) c.PresetIds.Remove(preset.Id);
                    var cat = brushGroup.Categories.FirstOrDefault(c => c.Name == catName);
                    if (cat == null) { cat = new ToolCategory { Name = catName }; brushGroup.Categories.Add(cat); }
                    if (!cat.PresetIds.Contains(preset.Id)) cat.PresetIds.Add(preset.Id);
                }
            }

            App.ToolGroups.Save();
            _selectedCategory = fileImports[^1].CategoryName;
            RefreshGroupPresets();
            _footerStatusText.Text = $"Imported {imported} brush{(imported == 1 ? "" : "es")} [{lastDiag}]";
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

    private void ScheduleToolGroupsSave()
    {
        if (_toolGroupsSaveTimer == null)
        {
            _toolGroupsSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ToolGroupsSaveDebounceMs)
            };
            _toolGroupsSaveTimer.Tick += (_, _) =>
            {
                _toolGroupsSaveTimer.Stop();
                App.ToolGroups.Save();
            };
        }

        _toolGroupsSaveTimer.Stop();
        _toolGroupsSaveTimer.Start();
    }

    private void FlushToolGroupsSave()
    {
        _toolGroupsSaveTimer?.Stop();
        App.ToolGroups.Save();
    }

    private void ScheduleBrushPresetAutosave()
    {
        if (_brushPresetAutosaveTimer == null)
        {
            _brushPresetAutosaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(BrushPresetAutosaveDebounceMs)
            };
            _brushPresetAutosaveTimer.Tick += (_, _) =>
            {
                _brushPresetAutosaveTimer.Stop();
                FlushBrushPresetAutosave();
            };
        }

        _brushPresetAutosaveTimer.Stop();
        _brushPresetAutosaveTimer.Start();
    }

    private void FlushBrushPresetAutosave()
    {
        _brushPresetAutosaveTimer?.Stop();

        CaptureActiveBrushToPresetIfChanged();

        if (_activeBrushAsset != null && _dirtyBrushAssetIds.Contains(_activeBrushAsset.Id))
        {
            if (_activePreset != null)
                _activeBrushAsset.WithPreset(_activePreset);
            _brushLibrary.Save(_activeBrushAsset);
            _dirtyBrushAssetIds.Remove(_activeBrushAsset.Id);
        }

        foreach (var assetId in _dirtyBrushAssetIds.ToList())
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) continue;
            _brushLibrary.Save(asset);
            _dirtyBrushAssetIds.Remove(assetId);
        }

        ScheduleToolGroupsSave();
    }

    internal void CaptureActiveBrushToPresetIfChanged()
    {
        if (_activeToolGroup?.ActivePreset is { } active)
            CaptureBrushToPresetIfChanged(active);
    }

    internal void CaptureBrushToPresetIfChanged(ToolPreset preset)
    {
        if (_activePreset == null) return;
        if (!preset.InputProcess.IsBrushFamily() || preset.OutputProcess != OutputProcessType.DirectDraw) return;

        var signature = BuildBrushCaptureSignature(_activePreset);
        if (_lastCapturedBrushByPresetId.TryGetValue(preset.Id, out var previous)
            && string.Equals(previous, signature, StringComparison.Ordinal))
            return;

        preset.CaptureFromBrushPreset(_activePreset);
        _lastCapturedBrushByPresetId[preset.Id] = signature;
    }

    private static string BuildBrushCaptureSignature(BrushPreset preset)
    {
        return string.Join('|',
            GraphForBrushTip(preset.Tip).CacheKey(),
            preset.Size.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Opacity.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Hardness.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Spacing.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Flow.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Grain.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Smoothing.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.SpeedAdaptiveStabilizer.ToString(),
            preset.Angle.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.TipDensity.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.TipThickness.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ((int)preset.BlendMode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

}
