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
using Floss.App.Config;
using Floss.App.Controls;
using Floss.App.Document;
using Floss.App.Tools;
using Floss.App.Windows;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed partial class BrushDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    public bool SyncingBrushUi { get; set; }
    private BrushLibrary _brushLibrary = null!;
    private IReadOnlyList<BrushAsset> _brushAssets = [];
    private readonly HashSet<string> _dirtyBrushAssetIds = new();
    private ToolPropertiesWindow? _toolPropertiesWindow;
    public BrushDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);_brushLibrary=new BrushLibrary(AppPaths.BrushesDirectory);Content=BuildBrushSectionImpl();}
    public BrushStrokePreview StrokePreview=>_strokePreview; public TextBlock ActiveBrushLabel=>_activeBrushLabel;
    public ScrubSlider SizeSlider=>_sizeSlider; public ScrubSlider MaxSizePercentSlider=>_maxSizePercentSlider; public ScrubSlider OpacitySlider=>_opacitySlider;
    public ScrubSlider FlowSlider=>_flowSlider; public ScrubSlider HardnessSlider=>_hardnessSlider; public ScrubSlider SpacingSlider=>_spacingSlider;
    public ScrubSlider SmoothingSlider=>_smoothingSlider; public ScrubSlider GrainSlider=>_grainSlider; public Button SaveBrushButton=>_saveBrushButton;
    public void UpdateCurrentBrush(Func<BrushPreset,BrushPreset> u)=>UpdateCurrentBrushInternal(u);
    public void PreviewCurrentBrush(Func<BrushPreset,BrushPreset> u)=>PreviewCurrentBrushInternal(u);
    public void LoadBrushAssets()=>LoadBrushAssetsInternal(); public void SelectInitialTool()=>SelectInitialToolInternal();
    public void ScheduleToolGroupsSave()=>ScheduleToolGroupsSaveInternal(); public void SaveActiveToolSelection()=>SaveActiveToolSelectionInternal();
    public void ScheduleBrushPresetAutosave()=>ScheduleBrushPresetAutosaveInternal(); public void FlushToolGroupsSave()=>FlushToolGroupsSaveInternal();
    public void FlushBrushPresetAutosave()=>FlushBrushPresetAutosaveInternal();
    public static string? ResolveStartupCategory(ToolGroup g,string? p,ToolPreset pr)=>ResolveStartupCategoryImpl(g,p,pr);
    public static BrushTipNodeGraph GraphForBrushTip(IBrushTip tip)=>GraphForBrushTipImpl(tip);
    public void EnableCategoryPromoteDrop(Control t,ToolGroup? g)=>EnableCategoryPromoteDropImpl(t,g);
    public void RefreshGroupPresets()=>RefreshGroupPresetsImpl(); public void ApplyBrushSettings(BrushPreset p,bool s)=>ApplyBrushSettingsImpl(p,s);
    public void CaptureActiveBrushToPresetIfChanged()=>CaptureActiveBrushToPresetIfChangedImpl();
    public void CaptureBrushToPresetIfChanged(ToolPreset p)=>CaptureBrushToPresetIfChangedImpl(p);
    public void SaveActiveBrush() => SaveActiveBrushImpl();
    public void DuplicateActiveBrush() => DuplicateActiveBrushImpl();
    public Task ImportAbrAsync() => ImportAbrAsyncImpl();
    public Task ImportBrushTipPngAsync() => ImportBrushTipPngAsyncImpl();
    public void SaveNodeGraphAsNewBrushPreset(BrushTipNodeGraph g,string n)=>SaveNodeGraphAsNewBrushPresetImpl(g,n);
    // ── Brush section ─────────────────────────────────────────────────────────
    /// <summary>Fixed tile width for wrap layout (adaptive columns).</summary>
    private const double BrushPresetTileWidth = 136;
    private const double BrushPresetPreviewRenderWidth = 128;
    private const double BrushPresetPreviewRenderHeight = 44;

    private const int BrushPresetAutosaveDebounceMs = 700;
    private const int ToolGroupsSaveDebounceMs = 400;
    private DispatcherTimer? _brushPresetAutosaveTimer;
    private DispatcherTimer? _toolGroupsSaveTimer;
    private readonly Dictionary<string, BrushPresetRowHost> _brushPresetRowCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastCapturedBrushByPresetId = new(StringComparer.Ordinal);

    private BrushStrokePreview _strokePreview = null!;
    private ScrubSlider _sizeSlider = null!;
    private ScrubSlider _maxSizePercentSlider = null!;
    private ScrubSlider _opacitySlider = null!;
    private ScrubSlider _flowSlider = null!;
    private ScrubSlider _hardnessSlider = null!;
    private ScrubSlider _spacingSlider = null!;
    private ScrubSlider _smoothingSlider = null!;
    private ScrubSlider _grainSlider = null!;
    private TextBlock _activeBrushLabel = null!;
    private Button _saveBrushButton = null!;
    private WrapPanel _brushCategoryPanel = null!;
    private WrapPanel _presetPanel = null!;
    private ScrollViewer? _brushPresetScroll;

    private static readonly FilePickerFileType FlossSubToolFileType = new("Floss Sub Tool")
    {
        Patterns = ["*.flbr"]
    };

    private static readonly FilePickerFileType FlossSubToolGroupFileType = new("Floss Sub Tool Group")
    {
        Patterns = ["*.flbrg"]
    };

    private sealed class BrushPresetRowHost
    {
        public required Button Row { get; init; }
        public required BrushStrokePreview Preview { get; init; }
        public string PreviewKey = "";
    }

    private Control BuildBrushSectionImpl()
    {
        _brushPresetRowCache.Clear();

        _strokePreview = new BrushStrokePreview { Height = 34, Margin = new Thickness(4, 1, 4, 3) };

        _sizeSlider = DockPanelUiHelpers.MkSlider(1, BrushSizeLimits.FallbackMaxDiameterPx, 20, "Size");
        _maxSizePercentSlider = DockPanelUiHelpers.MkSlider(
            BrushSizeLimits.MinMaxSizePercent,
            BrushSizeLimits.StudioMaxSizePercent,
            BrushSizeLimits.DefaultMaxSizePercent,
            "Max size — canvas-scaled ceiling for this brush (100–400%)");
        _opacitySlider = DockPanelUiHelpers.MkSlider(0.01, 1, 1.0, "Opacity");
        _flowSlider = DockPanelUiHelpers.MkSlider(0.01, 1, 1.0, "Flow — controls paint buildup per dab");
        _hardnessSlider = DockPanelUiHelpers.MkSlider(0, 1, 0.9, "Hardness — edge softness");
        _spacingSlider = DockPanelUiHelpers.MkSlider(0.02, 1, 0.1, "Spacing");
            _smoothingSlider = DockPanelUiHelpers.MkSlider(0, 0.95, 0.3, "Stabilization");
        _grainSlider = DockPanelUiHelpers.MkSlider(0, 1, 0.0, "Grain — noise texture");

        _activeBrushLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var panelMenuBtn = DockPanelUiHelpers.SmIconBtn(Icons.DotsVertical, "Panel menu");
        panelMenuBtn.Click += (_, _) => ShowBrushPanelMenu(panelMenuBtn);
        _saveBrushButton = DockPanelUiHelpers.SmIconBtn(Icons.ContentSaveOutline, "Save brush");
        var duplicateBrushBtn = DockPanelUiHelpers.SmIconBtn(Icons.ContentCopy, "Duplicate brush");
        var editBrushBtn = DockPanelUiHelpers.SmIconBtn(Icons.TuneVertical, "Tool properties");
        _saveBrushButton.Click += (_, _) => SaveActiveBrushImpl();
        duplicateBrushBtn.Click += (_, _) => DuplicateActiveBrushImpl();
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
    internal void RefreshGroupPresetsImpl()
    {
        if (_brushCategoryPanel is null || _presetPanel is null)
            return;

        _brushCategoryPanel.Children.Clear();
        _presetPanel.Children.Clear();

        var group = _ps.Tools.ActiveToolGroup;
        if (group == null) return;

        if (_ps.Brush.SelectedCategory == null)
            _ps.Brush.SelectedCategory = group.Categories.FirstOrDefault()?.Name;

        foreach (var cat in group.Categories)
        {
            var selected = cat.Name == _ps.Brush.SelectedCategory;
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
                Background = new SolidColorBrush(Color.Parse(selected ? SelectionBg : Bg2)),
                Foreground = new SolidColorBrush(Color.Parse(selected ? TextPrimary : TextSecondary)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Tag = catName,
            };
            btn.Click += (_, _) =>
            {
                _ps.Brush.SelectedCategory = catName;
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
                    _ps.Tools.ActivatePreset(group, toActivate);
                else
                {
                    SaveActiveToolSelectionInternal();
                    RefreshGroupPresetsImpl();
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
                Floss.App.App.ToolGroups.Save();
                _ps.Brush.SelectedCategory = name;
                RefreshGroupPresetsImpl();
            }
        };
        EnableNewCategoryDrop(newCatBtn, group);
        _brushCategoryPanel.Children.Add(newCatBtn);

        if (_ps.Brush.SelectedCategory == null)
            _ps.Brush.SelectedCategory = group.Categories.FirstOrDefault(c => c.PresetIds.Count > 0)?.Name
                ?? group.Categories.FirstOrDefault()?.Name;

        IEnumerable<ToolPreset> presets;
        if (_ps.Brush.SelectedCategory == null)
        {
            presets = group.Presets;
        }
        else
        {
            var cat = group.Categories.FirstOrDefault(c => c.Name == _ps.Brush.SelectedCategory);
            if (cat == null)
            {
                _ps.Brush.SelectedCategory = group.Categories.FirstOrDefault(c => c.PresetIds.Count > 0)?.Name;
                cat = _ps.Brush.SelectedCategory == null
                    ? null
                    : group.Categories.FirstOrDefault(c => c.Name == _ps.Brush.SelectedCategory);
            }

            presets = cat == null
                ? group.Presets
                : cat.PresetIds.Select(id => group.Presets.FirstOrDefault(p => p.Id == id)).OfType<ToolPreset>();
        }

        foreach (var preset in presets)
        {
            var isActive = group.LastActivePresetId == preset.Id;

            if (preset.Kind.IsBrushFamily() && preset.Kind.IsBrushFamily())
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
        => $"{GraphForBrushTipImpl(brushPreset.Tip).CacheKey()}|{brushPreset.Size:F2}|{brushPreset.Hardness:F3}|{brushPreset.Opacity:F3}|{brushPreset.Flow:F3}";

    private static void ApplyBrushPresetRowActive(Button row, bool isActive)
    {
        row.BorderBrush = new SolidColorBrush(Color.Parse(Stroke));
        row.BorderThickness = new Thickness(isActive ? 2 : 1, 1, 1, 1);
        if (isActive)
            row.BorderBrush = new SolidColorBrush(Color.Parse(SelectionBorder));
        row.Background = new SolidColorBrush(Color.Parse(Bg2));
        if (row.Content is Grid grid && grid.Children.Count >= 2
            && grid.Children[1] is Border nameRow)
        {
            nameRow.Background = new SolidColorBrush(Color.Parse(isActive ? SelectionBgActive : Bg2));
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

        var iconPath = preset.PresetIcon ?? Icons.DefaultIcon(preset.Kind);
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
        row.Click += (_, _) => _ps.Tools.ActivatePreset(group, preset);
        EnablePresetDragAndReorder(row, group, preset);
        row.ContextMenu = BuildPresetContextMenu(group, preset);
        return row;
    }

    private Button BuildSimplePresetRow(ToolGroup group, ToolPreset preset, bool isActive)
    {
        var iconPath = preset.PresetIcon ?? Icons.DefaultIcon(preset.Kind);
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
            Background = new SolidColorBrush(Color.Parse(isActive ? SelectionBgActive : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? SelectionBorder : Stroke)),
            BorderThickness = new Thickness(isActive ? 2 : 1, 1, 1, 1),
            CornerRadius = new CornerRadius(2),
            Content = content,
            Tag = preset.Id,
        };
        row.Click += (_, _) => _ps.Tools.ActivatePreset(group, preset);
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

        if (preset.Kind.IsBrushFamily() && preset.Kind.IsBrushFamily())
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
        CaptureActiveBrushToPresetIfChangedImpl();

        var file = await _ps.Shell.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Sub Tool",
            FileTypeChoices = [FlossSubToolFileType],
            SuggestedFileName = SafePresetFileName(preset.Name, PresetPackageFormat.SubToolExtension)
        });
        if (file == null) return;

        try
        {
            PresetPackageFormat.ExportSubTool(file.Path.LocalPath, group, preset, _brushAssets);
            _ps.Shell.FooterStatusText.Text = $"Exported sub tool {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.SubToolExport");
            _ps.Shell.FooterStatusText.Text = $"Sub tool export error: {ex.Message}";
        }
    }

    private void RestorePresetDefault(ToolGroup group, ToolPreset preset)
    {
        if (!preset.Kind.IsBrushFamily() || preset.Kind != ToolKind.Brush)
            return;

        if (preset.BrushId != null)
            preset.ClearBrushOverrides();
        else
            preset.RestoreBrushDefaults(ToolGroupConfig.CreateFactorySmudgeOverride());

        Floss.App.App.ToolGroups.Save();

        if (preset.BrushId != null)
            LoadBrushAssetsInternal();

        if (_ps.Tools.ActiveToolGroup == group && group.ActivePreset == preset)
            SyncActivePresetToCanvas();
        RefreshGroupPresetsImpl();
        _ps.Shell.FooterStatusText.Text = $"Restored {preset.Name} to defaults";
    }

    private void SavePresetAsDefault(ToolGroup group, ToolPreset preset)
    {
        if (!preset.Kind.IsBrushFamily() || preset.Kind != ToolKind.Brush)
            return;

        if (!(_ps.Tools.ActiveToolGroup == group && group.ActivePreset == preset))
            _ps.Tools.ActivatePreset(group, preset);

        if (preset.BrushId != null)
        {
            var asset = _ps.Brush.ActiveBrushAsset ?? _brushAssets.FirstOrDefault(a => a.Id == preset.BrushId);
            if (asset == null)
            {
                _ps.Shell.FooterStatusText.Text = $"Could not find brush asset for {preset.Name}";
                return;
            }

            asset.WithPreset(CurrentBrushFromUi());
            _brushLibrary.Save(asset);
            preset.ClearBrushOverrides();
            Floss.App.App.ToolGroups.Save();
            LoadBrushAssetsInternal();
        }
        else
        {
            CaptureActiveBrushToPresetIfChangedImpl();
            preset.SaveBrushOverrideAsDefault();
            Floss.App.App.ToolGroups.Save();
        }

        SyncActivePresetToCanvas();
        RefreshGroupPresetsImpl();
        _ps.Shell.FooterStatusText.Text = $"Saved {preset.Name} as new default";
    }

    private void SyncActivePresetToCanvas()
    {
        var active = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (active == null || !active.Kind.IsBrushFamily()) return;

        BrushPreset? basePreset = null;
        if (active.BrushId != null)
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == active.BrushId);
            if (asset != null) basePreset = asset.ToPreset();
        }

        if (basePreset != null)
        {
            var overridden = active.ApplyToBrushPreset(basePreset);
            _ps.Brush.ActivePreset = overridden;
            _ps.Canvas.SyncBrushFromContext(overridden);
            _activeBrushLabel.Text = basePreset.Name;
            _strokePreview.Brush = overridden;
            SyncingBrushUi = true;
            _sizeSlider.Value = Math.Clamp(overridden.Size, _sizeSlider.Minimum, _sizeSlider.Maximum);
            _opacitySlider.Value = Math.Clamp(overridden.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
            _flowSlider.Value = Math.Clamp(overridden.Flow, _flowSlider.Minimum, _flowSlider.Maximum);
            _hardnessSlider.Value = Math.Clamp(overridden.Hardness, _hardnessSlider.Minimum, _hardnessSlider.Maximum);
            _spacingSlider.Value = Math.Clamp(overridden.Spacing, _spacingSlider.Minimum, _spacingSlider.Maximum);
            _smoothingSlider.Value = Math.Clamp(overridden.Smoothing, _smoothingSlider.Minimum, _smoothingSlider.Maximum);
            _grainSlider.Value = Math.Clamp(overridden.Grain, _grainSlider.Minimum, _grainSlider.Maximum);
            SyncingBrushUi = false;
        }
        else
        {
            var overridden = active.ApplyToBrushPreset(_ps.Canvas.Brush);
            _ps.Brush.ActivePreset = overridden;
            _ps.Canvas.SyncBrushFromContext(overridden);
            _activeBrushLabel.Text = active.Name;
            _strokePreview.Brush = overridden;
        }

        _ps.Shell.UpdateStatus();
        _ps.Brush.RefreshToolProperties();
        _ps.Brush.SyncNodeGraphDockToActiveBrush(force: true);
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

        var kindPicker = new ComboBox
        {
            ItemsSource = Enum.GetValues<ToolKind>(),
            SelectedItem = preset.Kind,
            Width = 200,
            Height = 28,
            FontSize = 11
        };

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
                new TextBlock { Text = "Tool Kind", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                kindPicker,
                new TextBlock { Text = "Tool Icon", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                iconPickerPanel,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancelBtn, saveBtn } }
            }
            }
        };

        saveBtn.Click += (_, _) =>
        {
            preset.Name = nameBox.Text?.Trim() ?? preset.Name;
            preset.Kind = (ToolKind)(kindPicker.SelectedItem ?? preset.Kind);
            preset.PresetIcon = selectedIcon;
            Floss.App.App.ToolGroups.Save();
            RefreshGroupPresetsImpl();
            if (_ps.Tools.ActiveToolGroup == group && group.ActivePreset == preset)
            {
                _ps.Tools.ActivatePreset(group, preset);
                _ps.Tools.RebuildToolRail();
            }
            dlg.Close();
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        dlg.ShowDialog(_ps.Shell.Owner);
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
        await dialog.ShowDialog(_ps.Shell.Owner);

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

        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
        if (_ps.Tools.ActiveToolGroup == group && group.ActivePreset == preset)
            _ps.Tools.ActivatePreset(group, preset);
    }

    private void DuplicatePreset(ToolGroup group, ToolPreset preset)
    {
        var copy = new ToolPreset
        {
            Name = preset.Name + " Copy",
            Kind = preset.Kind,
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
        else if (_ps.Brush.SelectedCategory != null)
        {
            var cat = group.Categories.FirstOrDefault(c => c.Name == _ps.Brush.SelectedCategory);
            if (cat != null && !cat.PresetIds.Contains(copy.Id))
                cat.PresetIds.Add(copy.Id);
        }

        _ps.Tools.ActivatePreset(group, copy);
    }

    private void DeletePreset(ToolGroup group, ToolPreset preset)
    {
        var wasActive = _ps.Tools.ActiveToolGroup == group && group.LastActivePresetId == preset.Id;
        if (wasActive)
            _ps.Tools.CaptureBrushToPresetIfChanged(preset);

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
            Floss.App.App.ToolGroups.Groups.Remove(group);
            if (_ps.Tools.ActiveToolGroup == group)
            {
                var next = Floss.App.App.ToolGroups.Groups.FirstOrDefault();
                if (next != null)
                {
                    var fallback = next.ActivePreset ?? next.Presets.FirstOrDefault();
                    if (fallback != null) _ps.Tools.ActivatePreset(next, fallback);
                }
            }
            _ps.Tools.RebuildToolRail();
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
            if (_ps.Tools.ActiveToolGroup == group)
            {
                var active = group.ActivePreset ?? group.Presets.FirstOrDefault();
                if (active != null) _ps.Tools.ActivatePreset(group, active);
            }
        }
        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
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
        await dialog.ShowDialog(_ps.Shell.Owner);

        var name = (await tcs.Task)?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        CaptureActiveBrushToPresetIfChangedImpl();

        var brushPreset = new BrushPreset(name, 40, 1.0, 0.9, 0.10, Colors.Black, 0)
        {
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Smoothing = 0.3
        };
        if (preset.BrushBlendMode.HasValue)
            brushPreset = brushPreset with { BlendMode = preset.BrushBlendMode.Value };

        var asset = BrushAsset.FromPreset(brushPreset, category: _ps.Brush.SelectedCategory);
        _brushLibrary.Save(asset);
        _brushAssets = [.._brushAssets, asset];

        var newPreset = new ToolPreset
        {
            Name = name,
            Kind = ToolKind.Brush,
            BrushId = asset.Id,
            BrushBlendMode = preset.BrushBlendMode
        };

        group.Presets.Add(newPreset);

        var cat = group.Categories.FirstOrDefault(c => c.Name == _ps.Brush.SelectedCategory);
        if (cat != null && !cat.PresetIds.Contains(newPreset.Id))
            cat.PresetIds.Add(newPreset.Id);

        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
        _ps.Tools.ActivatePreset(group, newPreset);
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
                _ps.Brush.SelectedCategory = targetCat.Name;
        }

        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
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
                _ps.Brush.SelectedCategory = name;
                RefreshGroupPresetsImpl();
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
        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
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
        await dialog.ShowDialog(_ps.Shell.Owner);
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
        await dialog.ShowDialog(_ps.Shell.Owner);

        var newName = (await tcs.Task)?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == cat.Name) return;
        if (group.Categories.Any(c => c.Name == newName)) return;

        if (_ps.Brush.SelectedCategory == cat.Name) _ps.Brush.SelectedCategory = newName;
        cat.Name = newName;
        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
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
        CaptureActiveBrushToPresetIfChangedImpl();

        var file = await _ps.Shell.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Tool Group",
            FileTypeChoices = [FlossSubToolGroupFileType],
            SuggestedFileName = SafePresetFileName(cat.Name, PresetPackageFormat.SubToolGroupExtension)
        });
        if (file == null) return;

        try
        {
            PresetPackageFormat.ExportSubToolGroup(file.Path.LocalPath, group, cat, _brushAssets);
            _ps.Shell.FooterStatusText.Text = $"Exported tool group {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.ToolGroupExport");
            _ps.Shell.FooterStatusText.Text = $"Tool group export error: {ex.Message}";
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
                _ps.Tools.CaptureBrushToPresetIfChanged(leaving);
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

        if (_ps.Brush.SelectedCategory == cat.Name)
            _ps.Brush.SelectedCategory = group.Categories.FirstOrDefault()?.Name;

        Floss.App.App.ToolGroups.Save();
        LoadBrushAssetsInternal();

        if (wasActiveDeleted && _ps.Tools.ActiveToolGroup == group)
        {
            var next = group.ActivePreset ?? group.Presets.FirstOrDefault();
            if (next != null) _ps.Tools.ActivatePreset(group, next);
        }

        _ps.Shell.FooterStatusText.Text = $"Deleted category \"{cat.Name}\" and {presetIds.Count} brush{(presetIds.Count == 1 ? "" : "es")}";
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
            Floss.App.App.ToolGroups.Save();
            RefreshGroupPresetsImpl();
            e.Handled = true;
        });
    }

    private void ShowBrushPanelMenu(Button anchor)
    {
        var menu = new ContextMenu();

        var importAbr = new MenuItem { Header = "Import .abr brush pack…" };
        importAbr.Click += async (_, _) => await ImportAbrAsyncImpl();

        var importTool = new MenuItem { Header = "Import Tool (.flbr)…" };
        importTool.Click += async (_, _) => await ImportSubToolAsync();

        var importToolGroup = new MenuItem { Header = "Import Tool Group (.flbrg)…" };
        importToolGroup.Click += async (_, _) => await ImportSubToolGroupAsync();

        menu.Items.Add(importAbr);
        menu.Items.Add(importTool);
        menu.Items.Add(importToolGroup);

        var group = _ps.Tools.ActiveToolGroup;
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
        var files = await _ps.Shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Tool",
            AllowMultiple = false,
            FileTypeFilter = [FlossSubToolFileType]
        });
        if (files.Count == 0) return;

        try
        {
            using var busy = _ps.Shell.BeginBusy("Importing sub tool…");
            var (importedGroup, importedPreset, brushAssets) = await System.Threading.Tasks.Task.Run(
                () => PresetPackageFormat.ImportSubTool(files[0].Path.LocalPath));

            busy.Report("Saving imported brushes…");

            foreach (var asset in brushAssets)
                _brushLibrary.Save(asset);

            var targetGroup = _ps.Tools.ActiveToolGroup
                ?? Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultKind == ToolKind.Brush)
                ?? Floss.App.App.ToolGroups.Groups.FirstOrDefault();
            if (targetGroup == null) return;

            importedPreset.Id = Guid.NewGuid().ToString("N");
            targetGroup.Presets.Add(importedPreset);

            var catName = importedGroup.Categories.FirstOrDefault(c => c.PresetIds.Any())?.Name ?? _ps.Brush.SelectedCategory;
            if (catName != null)
            {
                var cat = targetGroup.Categories.FirstOrDefault(c => c.Name == catName);
                if (cat == null) { cat = new ToolCategory { Name = catName }; targetGroup.Categories.Add(cat); }
                if (!cat.PresetIds.Contains(importedPreset.Id)) cat.PresetIds.Add(importedPreset.Id);
            }

            Floss.App.App.ToolGroups.Save();
            LoadBrushAssetsInternal();
            _ps.Shell.FooterStatusText.Text = $"Imported tool \"{importedPreset.Name}\"";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.SubToolImport");
            _ps.Shell.FooterStatusText.Text = $"Import error: {ex.Message}";
        }
    }

    private async Task ImportSubToolGroupAsync()
    {
        var files = await _ps.Shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Tool Group",
            AllowMultiple = false,
            FileTypeFilter = [FlossSubToolGroupFileType]
        });
        if (files.Count == 0) return;

        try
        {
            using var busy = _ps.Shell.BeginBusy("Importing tool group…");
            var (importedGroups, brushAssets) = await System.Threading.Tasks.Task.Run(
                () => PresetPackageFormat.ImportSubToolGroup(files[0].Path.LocalPath));

            busy.Report("Saving imported brushes…");

            foreach (var asset in brushAssets)
                _brushLibrary.Save(asset);

            foreach (var importedGroup in importedGroups)
            {
                var existing = Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.Name == importedGroup.Name);
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
                    Floss.App.App.ToolGroups.Groups.Add(importedGroup);
                }
            }

            Floss.App.App.ToolGroups.Save();
            LoadBrushAssetsInternal();
            _ps.Tools.RebuildToolRail();
            _ps.Shell.FooterStatusText.Text = $"Imported tool group";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.ToolGroupImport");
            _ps.Shell.FooterStatusText.Text = $"Import error: {ex.Message}";
        }
    }

    private async Task ExportCurrentToolGroupAsync()
    {
        var group = _ps.Tools.ActiveToolGroup;
        if (group == null) return;
        CaptureActiveBrushToPresetIfChangedImpl();

        var file = await _ps.Shell.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Tool Group",
            FileTypeChoices = [FlossSubToolGroupFileType],
            SuggestedFileName = SafePresetFileName(group.Name, PresetPackageFormat.SubToolGroupExtension)
        });
        if (file == null) return;

        try
        {
            PresetPackageFormat.ExportSubToolGroup(file.Path.LocalPath, group, _brushAssets);
            _ps.Shell.FooterStatusText.Text = $"Exported \"{group.Name}\" to {Path.GetFileName(file.Path.LocalPath)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.BrushLibrary.ToolGroupExportCurrent");
            _ps.Shell.FooterStatusText.Text = $"Export error: {ex.Message}";
        }
    }

    internal void EnableCategoryPromoteDropImpl(Control target, ToolGroup? targetGroup)
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
        var sourceGroup = Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.Id == sourceGroupId);
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
                DefaultKind = sourceGroup.DefaultKind,
                Presets = []
            };
            Floss.App.App.ToolGroups.Groups.Add(targetGroup);
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

        var sourceWasActive = _ps.Tools.ActiveToolGroup == sourceGroup;
        if (!sourceGroup.Presets.Any() && !sourceGroup.Categories.Any())
        {
            Floss.App.App.ToolGroups.Groups.Remove(sourceGroup);
            if (sourceWasActive) _ps.Tools.ActiveToolGroup = null;
        }

        Floss.App.App.ToolGroups.Save();
        _ps.Tools.RebuildToolRail();

        _ps.Brush.SelectedCategory = catName;
        if (_ps.Tools.ActiveToolGroup != targetGroup)
        {
            var preset = targetGroup.ActivePreset ?? targetGroup.Presets.FirstOrDefault();
            if (preset != null)
                _ps.Tools.ActivatePreset(targetGroup, preset);
            else
            {
                _ps.Tools.ActiveToolGroup = targetGroup;
                RefreshGroupPresetsImpl();
            }
        }
        else
        {
            RefreshGroupPresetsImpl();
        }
    }

    private void LoadBrushAssetsInternal()
    {
        _brushAssets = _brushLibrary.Load();
        _ps.Brush.BrushAssets = _brushAssets;
        _dirtyBrushAssetIds.Clear();
        Floss.App.App.ToolGroups.SyncWithAssets(_brushAssets, _ps.Tools.ActiveToolGroup);
        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
        if (_ps.DockLayout.IsDockerVisible("node-graph"))
            _ps.Brush.InvalidateNodeGraphDockState();
    }

    private void SelectInitialToolInternal()
    {
        var cfg = _ps.Config;
        var group = cfg.LastToolGroupId == null
            ? null
            : Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.Id == cfg.LastToolGroupId);

        group ??= cfg.LastToolPresetId == null
            ? null
            : Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.Presets.Any(p => p.Id == cfg.LastToolPresetId));

        if (group != null)
        {
            var preset = cfg.LastToolPresetId == null
                ? group.ActivePreset
                : group.Presets.FirstOrDefault(p => p.Id == cfg.LastToolPresetId) ?? group.ActivePreset;
            if (preset != null)
            {
                _ps.Brush.SelectedCategory = ResolveStartupCategoryImpl(group, cfg.LastToolCategoryName, preset);
                _ps.Tools.ActivatePreset(group, preset);
                return;
            }
        }

        SelectInitialBrush();
    }

    private static string? ResolveStartupCategoryImpl(ToolGroup group, string? preferredCategory, ToolPreset preset)
    {
        if (preferredCategory != null &&
            group.Categories.Any(c => c.Name == preferredCategory && c.PresetIds.Contains(preset.Id)))
            return preferredCategory;

        return group.Categories.FirstOrDefault(c => c.PresetIds.Contains(preset.Id))?.Name
            ?? group.Categories.FirstOrDefault()?.Name;
    }

    private void SelectInitialBrush()
    {
        var initial = _brushAssets.FirstOrDefault(b => b.Preset.Name == _ps.Config.LastBrushName)
            ?? _brushAssets.FirstOrDefault()
            ?? BrushAsset.FromPreset(BrushPreset.Defaults[0]);

        // Find the group+preset that owns this brush asset and do a full activation
        foreach (var group in Floss.App.App.ToolGroups.Groups)
        {
            var preset = group.Presets.FirstOrDefault(p => p.BrushId == initial.Id);
            if (preset != null) { _ps.Tools.ActivatePreset(group, preset); return; }
        }

        // No linked preset yet — activate the brush group's first preset and load the brush
        var brushGroup = Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultKind == ToolKind.Brush)
            ?? Floss.App.App.ToolGroups.Groups.FirstOrDefault();
        if (brushGroup != null)
        {
            var fallback = brushGroup.ActivePreset ?? brushGroup.Presets.FirstOrDefault();
            if (fallback != null) _ps.Tools.ActivatePreset(brushGroup, fallback);
        }
        ApplyBrushAsset(initial);
    }

    private void SaveActiveToolSelectionInternal()
    {
        var group = _ps.Tools.ActiveToolGroup;
        var preset = group?.ActivePreset;
        if (group == null || preset == null) return;

        _ps.Config.LastToolGroupId = group.Id;
        _ps.Config.LastToolPresetId = preset.Id;
        _ps.Config.LastToolCategoryName = _ps.Brush.SelectedCategory;
        _ps.Config.LastBrushName = _ps.Brush.ActivePreset?.Name ?? _ps.Config.LastBrushName;
        _ps.Config.Save();
    }

    private void ApplyBrushAsset(BrushAsset asset)
    {
        _ps.Brush.ActiveBrushAsset = asset;
        ApplyBrushSettingsImpl(asset.ToPreset(), syncSliders: true);
    }

    internal void ApplyBrushSettingsImpl(BrushPreset preset, bool syncSliders)
        => ApplyBrushSettingsImpl(preset, syncSliders, syncToolPropertiesWindow: true);

    private void ApplyBrushSettingsImpl(BrushPreset preset, bool syncSliders, bool syncToolPropertiesWindow)
    {
        _ps.Brush.ActivePreset = preset;
        _activeBrushLabel.Text = preset.Name;

        _ps.Brush.SyncBrushSizeLimits();
        SyncBrushScalarControls(preset);

        var applied = preset with { Color = _ps.Canvas.PaintColor };
        _ps.Canvas.SetBrush(applied);
        _strokePreview.Brush = applied;
        var activeToolPreset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (syncToolPropertiesWindow && activeToolPreset != null && _toolPropertiesWindow?.CanSyncToolPreset(activeToolPreset) == true)
        {
            _ps.Sync.SyncingToolPropertyPanel = true;
            try
            {
                _toolPropertiesWindow.SyncFromPreset(applied with { Color = _ps.Canvas.Brush.Color });
            }
            finally
            {
                _ps.Sync.SyncingToolPropertyPanel = false;
            }
        }
        _ps.Shell.UpdateStatus();
        _ps.Brush.RefreshToolProperties();
        _ps.Brush.SyncNodeGraphDockToActiveBrush();
    }

    private void SyncBrushScalarControls(BrushPreset preset)
    {
        SyncingBrushUi = true;
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
            SyncingBrushUi = false;
        }
    }

    private void PreviewCurrentBrushInternal(Func<BrushPreset, BrushPreset> update)
    {
        if (SyncingBrushUi || _ps.Sync.SyncingToolPropertyPanel) return;
        _ps.Brush.ActivePreset ??= _ps.Canvas.Brush;
        _ps.Brush.ActivePreset = update(_ps.Brush.ActivePreset);
        var applied = _ps.Brush.ActivePreset with { Color = _ps.Canvas.PaintColor };
        _ps.Canvas.SetBrush(applied);
        _strokePreview.Brush = applied;
    }

    private void UpdateCurrentBrushInternal(Func<BrushPreset, BrushPreset> update)
    {
        if (SyncingBrushUi || _ps.Sync.SyncingToolPropertyPanel) return;
        _ps.Brush.ActivePreset ??= _ps.Canvas.Brush;
        var updated = update(_ps.Brush.ActivePreset);
        _ps.Brush.ActivePreset = updated;
        var activeToolPreset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (activeToolPreset?.Kind.IsBrushFamily() == true &&
            activeToolPreset.Kind.IsBrushFamily())
        {
            activeToolPreset.CaptureFromBrushPreset(updated);
            _lastCapturedBrushByPresetId[activeToolPreset.Id] = BuildBrushCaptureSignature(updated);
            Floss.App.App.ToolGroups.Save();
        }
        else if (activeToolPreset?.Kind.UsesPaintBrushOverride() == true)
        {
            activeToolPreset.BrushOverride ??= new BrushPresetOverrideDocument();
            activeToolPreset.BrushOverride.Opacity = updated.Opacity;
            activeToolPreset.BrushOverride.BlendMode = updated.BlendMode;
            Floss.App.App.ToolGroups.Save();
            SyncActivePaintToolOutput();
        }
        ScheduleBrushPresetAutosaveInternal();
        ApplyBrushSettingsImpl(updated, syncSliders: false);
        RefreshGroupPresetsImpl();
        _ps.Brush.RefreshNodeGraphImageOptions();
    }

    private void UpdateCurrentBrushFromToolProperties(Func<BrushPreset, BrushPreset> update)
    {
        if (SyncingBrushUi || _ps.Sync.SyncingToolPropertyPanel) return;
        _ps.Brush.ActivePreset ??= _ps.Canvas.Brush;
        var updated = update(_ps.Brush.ActivePreset);
        _ps.Brush.ActivePreset = updated;
        var activeToolPreset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (activeToolPreset?.Kind.IsBrushFamily() == true &&
            activeToolPreset.Kind.IsBrushFamily())
        {
            activeToolPreset.CaptureFromBrushPreset(updated);
            _lastCapturedBrushByPresetId[activeToolPreset.Id] = BuildBrushCaptureSignature(updated);
            Floss.App.App.ToolGroups.Save();
        }
        else if (activeToolPreset?.Kind.UsesPaintBrushOverride() == true)
        {
            activeToolPreset.BrushOverride ??= new BrushPresetOverrideDocument();
            activeToolPreset.BrushOverride.Opacity = updated.Opacity;
            activeToolPreset.BrushOverride.BlendMode = updated.BlendMode;
            Floss.App.App.ToolGroups.Save();
            SyncActivePaintToolOutput();
        }
        ScheduleBrushPresetAutosaveInternal();
        ApplyBrushSettingsImpl(updated, syncSliders: false, syncToolPropertiesWindow: false);
        SyncBrushScalarControls(updated);
        RefreshGroupPresetsImpl();
        _ps.Brush.RefreshNodeGraphImageOptions();
    }

    private void SyncActivePaintToolOutput()
    {
        var preset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (preset?.Kind.UsesPaintBrushOverride() != true) return;
        _ps.Canvas.SetActiveTool(_ps.Tools.ToolForPreset(preset), preset);
    }

    private void OpenToolProperties()
    {
        var toolPreset = _ps.Tools.ActiveToolGroup?.ActivePreset;
        if (toolPreset == null) return;

        var brushPreset = _ps.Brush.ActivePreset ?? _ps.Canvas.Brush;

        if (_toolPropertiesWindow != null)
        {
            if (!_toolPropertiesWindow.CanSyncToolPreset(toolPreset))
            {
                _toolPropertiesWindow.Close();
                _toolPropertiesWindow = null;
            }
            else
            {
                _toolPropertiesWindow.SyncFromToolPreset(toolPreset);
                if (brushPreset != null)
                {
                    _ps.Sync.SyncingToolPropertyPanel = true;
                    try
                    {
                        _toolPropertiesWindow.SyncFromPreset(brushPreset with { Color = _ps.Canvas.Brush.Color });
                    }
                    finally
                    {
                        _ps.Sync.SyncingToolPropertyPanel = false;
                    }
                }
                _toolPropertiesWindow.Activate();
                return;
            }
        }

        _toolPropertiesWindow = new ToolPropertiesWindow(toolPreset, brushPreset, (tp, brushUpdate) =>
        {
            if (SyncingBrushUi) return;
            if (_ps.Tools.ActiveToolGroup?.ActivePreset != tp) return;

            if (brushUpdate != null)
            {
                // Brush tool: apply only the changed property via UpdateCurrentBrush so nothing else resets
                UpdateCurrentBrushFromToolProperties(brushUpdate);
            }
            else
            {
                // Non-brush tool: paint settings travel through CommitTool/_toolPreset
                if (tp.BrushOverride?.Opacity is { } opacity)
                    UpdateCurrentBrushInternal(p => p with { Opacity = opacity });
                if (tp.BrushOverride?.BlendMode is { } blendMode)
                    UpdateCurrentBrushInternal(p => p with { BlendMode = blendMode });
            }

            if (_ps.Tools.ActiveToolGroup?.ActivePreset == tp && !tp.Kind.IsBrushFamily())
            {
                if (tp.Kind == ToolKind.Assistant)
                    _ps.Tools.InvalidatePresetToolCache(tp.Id);
                _ps.Canvas.SetActiveTool(_ps.Tools.ToolForPreset(tp), tp);
            }

            if (brushUpdate == null)
                Floss.App.App.ToolGroups.Save();

            _ps.Brush.RefreshToolProperties();
        }, SaveNodeGraphAsNewBrushPresetImpl, _ps.Brush.OpenBrushTipGraphEditor);
        _toolPropertiesWindow.Closed += (_, _) => _toolPropertiesWindow = null;
        _toolPropertiesWindow.Show(_ps.Shell.Owner);
    }

    private void SaveNodeGraphAsNewBrushPresetImpl(BrushTipNodeGraph graph, string name)
    {
        if (graph.Validate().Count > 0) return;

        var group = _ps.Tools.ActiveToolGroup
            ?? Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultKind == ToolKind.Brush)
            ?? Floss.App.App.ToolGroups.Groups.FirstOrDefault();
        if (group == null) return;

        var clone = graph.DeepClone();
        clone.BuiltInShape = null;
        clone.BuiltInAspectRatio = 1.0f;
        var tip = (IBrushTip)new NodeBrushTip(clone);
        var brushName = string.IsNullOrWhiteSpace(name) ? "Custom Node Graph" : name.Trim();
        var source = _ps.Brush.ActivePreset ?? _ps.Canvas.Brush ?? BrushPreset.Defaults[0];
        var brushPreset = source with
        {
            Name = brushName,
            Tip = tip,
            Shape = null,
            Tips = [],
            TipSelectionMode = BrushTipSelectionMode.Single
        };

        var category = _ps.Brush.SelectedCategory
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
            Kind = ToolKind.Brush,
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

        Floss.App.App.ToolGroups.Save();
        RefreshGroupPresetsImpl();
        _ps.Tools.ActivatePreset(group, toolPreset);
        _ps.Shell.FooterStatusText.Text = $"Saved brush {brushName}";
    }

    private static BrushTipNodeGraph GraphForBrushTipImpl(IBrushTip tip)
        => tip switch
        {
            ProceduralBrushTip proc => proc.Graph.DeepClone(),
            NodeBrushTip node => node.Graph.DeepClone(),
            ImageBrushTip img => BrushTipNodeGraph.FromImageTip(img.GetPngBytes()),
            _ => BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Circle)
        };

    private static IReadOnlyList<BrushTipData> PreserveMaterialBrushTips(BrushPreset preset)
        => BrushMaterialTips.PreserveForPreset(preset);

    private void SaveActiveBrushImpl()
    {
        if (_ps.Brush.ActiveBrushAsset == null || _ps.Brush.ActivePreset == null) return;
        _ps.Brush.ActiveBrushAsset.WithPreset(CurrentBrushFromUi());
        _brushLibrary.Save(_ps.Brush.ActiveBrushAsset);
        LoadBrushAssetsInternal();
        _ps.Brush.ActiveBrushAsset = _brushAssets.FirstOrDefault(b => b.Id == _ps.Brush.ActiveBrushAsset.Id) ?? _ps.Brush.ActiveBrushAsset;
        _ps.Shell.FooterStatusText.Text = $"Saved brush {_ps.Brush.ActiveBrushAsset.Preset.Name}";
    }

    private void DuplicateActiveBrushImpl()
    {
        var group = _ps.Tools.ActiveToolGroup;
        var preset = group?.ActivePreset;
        if (group == null || preset == null) return;
        DuplicatePreset(group, preset);
    }

    private async System.Threading.Tasks.Task ImportBrushTipPngAsyncImpl()
    {
        if (_ps.Brush.ActiveBrushAsset == null || _ps.Brush.ActivePreset == null) return;
        var files = await _ps.Shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        UpdateCurrentBrushInternal(_ => updated);
        RefreshGroupPresetsImpl();
        _ps.Shell.FooterStatusText.Text = $"Added PNG sampler to {updated.Name}";
    }

    private async System.Threading.Tasks.Task ImportAbrAsyncImpl()
    {
        var files = await _ps.Shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import .abr brush pack",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Adobe Brush") { Patterns = ["*.abr"] }]
        });
        if (files.Count == 0) return;

        using var busy = _ps.Shell.BeginBusy("Importing brush pack…");
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
            LoadBrushAssetsInternal();

            // Gather all imported presets from wherever SyncWithAssets placed them,
            // then consolidate them into a single named category in the brush group.
            var brushGroup = Floss.App.App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultKind == ToolKind.Brush)
                ?? Floss.App.App.ToolGroups.Groups.First();

            foreach (var (catName, ids) in fileImports)
            {
                foreach (var assetId in ids)
                {
                    // Search every group — eraser-kind assets may have landed in eraserGroup.
                    ToolPreset? preset = null;
                    ToolGroup? sourceGroup = null;
                    foreach (var g in Floss.App.App.ToolGroups.Groups)
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

            Floss.App.App.ToolGroups.Save();
            _ps.Brush.SelectedCategory = fileImports[^1].CategoryName;
            RefreshGroupPresetsImpl();
            _ps.Shell.FooterStatusText.Text = $"Imported {imported} brush{(imported == 1 ? "" : "es")} [{lastDiag}]";
        }
        else
        {
            _ps.Shell.FooterStatusText.Text = $"No brushes imported — {lastDiag}";
        }
    }

    private BrushPreset CurrentBrushFromUi()
    {
        var source = _ps.Brush.ActivePreset ?? BrushPreset.Defaults[0];
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

    private void ScheduleToolGroupsSaveInternal()
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
                Floss.App.App.ToolGroups.Save();
            };
        }

        _toolGroupsSaveTimer.Stop();
        _toolGroupsSaveTimer.Start();
    }

    private void FlushToolGroupsSaveInternal()
    {
        _toolGroupsSaveTimer?.Stop();
        Floss.App.App.ToolGroups.Save();
    }

    private void ScheduleBrushPresetAutosaveInternal()
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
                FlushBrushPresetAutosaveInternal();
            };
        }

        _brushPresetAutosaveTimer.Stop();
        _brushPresetAutosaveTimer.Start();
    }

    private void FlushBrushPresetAutosaveInternal()
    {
        _brushPresetAutosaveTimer?.Stop();

        CaptureActiveBrushToPresetIfChangedImpl();

        if (_ps.Brush.ActiveBrushAsset != null && _dirtyBrushAssetIds.Contains(_ps.Brush.ActiveBrushAsset.Id))
        {
            if (_ps.Brush.ActivePreset != null)
                _ps.Brush.ActiveBrushAsset.WithPreset(_ps.Brush.ActivePreset);
            _brushLibrary.Save(_ps.Brush.ActiveBrushAsset);
            _dirtyBrushAssetIds.Remove(_ps.Brush.ActiveBrushAsset.Id);
        }

        foreach (var assetId in _dirtyBrushAssetIds.ToList())
        {
            var asset = _brushAssets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) continue;
            _brushLibrary.Save(asset);
            _dirtyBrushAssetIds.Remove(assetId);
        }

        ScheduleToolGroupsSaveInternal();
    }

    internal void CaptureActiveBrushToPresetIfChangedImpl()
    {
        if (_ps.Tools.ActiveToolGroup?.ActivePreset is { } active)
            _ps.Tools.CaptureBrushToPresetIfChanged(active);
    }

    private void CaptureBrushToPresetIfChangedImpl(ToolPreset preset)
    {
        if (_ps.Brush.ActivePreset == null) return;
        if (!preset.Kind.IsBrushFamily() || preset.Kind != ToolKind.Brush) return;

        var signature = BuildBrushCaptureSignature(_ps.Brush.ActivePreset);
        if (_lastCapturedBrushByPresetId.TryGetValue(preset.Id, out var previous)
            && string.Equals(previous, signature, StringComparison.Ordinal))
            return;

        preset.CaptureFromBrushPreset(_ps.Brush.ActivePreset);
        _lastCapturedBrushByPresetId[preset.Id] = signature;
    }

    private static string BuildBrushCaptureSignature(BrushPreset preset)
    {
        return string.Join('|',
            GraphForBrushTipImpl(preset.Tip).CacheKey(),
            preset.Size.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Opacity.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Hardness.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Spacing.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Flow.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Grain.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.Smoothing.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.SpeedAdaptiveStabilizer.ToString(),
            preset.AutoSpacingActive.ToString(),
            preset.AutoSpacingCoeff.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ((int)preset.GapMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            preset.Angle.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.TipDensity.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            preset.TipThickness.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ((int)preset.BlendMode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string SafePresetFileName(string name, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name
            .Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c)
            .ToArray())
            .Trim(' ', '.', '-');

        if (string.IsNullOrWhiteSpace(safe))
            safe = "sub-tool";

        return safe + extension;
    }
}
