using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed partial class ToolsDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    public ToolsDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);Content=BuildToolsContentImpl();}
    public Border ColorWell=>_colorWell;
    public void RebuildToolRail()=>BuildToolRail();
    public void CommitToolGroupShortcut(Input.KeyBinding kb)=>CommitToolGroupShortcutImpl(kb);
    public void CancelToolGroupShortcutRecording()=>CancelToolGroupShortcutRecordingImpl();
    public IReadOnlyList<(ToolGroup Group, Button Button)> ToolGroupButtons => _toolGroupButtons;

    public void UpdateRailSelection(Button? activeButton)
    {
        foreach (var b in _toolButtons)
            SetRailActive(b, b == activeButton);
    }

    private static void SetRailActive(Button button, bool active)
    {
        button.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : "Transparent"));
        button.BorderBrush = Avalonia.Media.Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(0);
    }

    private Border _colorWell = null!;
    private WrapPanel _toolRailStack = null!;
    private ScrollViewer? _toolRailScroll;
    private TextBlock _toolStatusText = null!;
    private readonly List<Button> _toolButtons = [];
    private readonly List<(ToolGroup Group, Button Button)> _toolGroupButtons = [];

    // ── Tools docker content (dockable panel — Window → Dockers, drag to float) ──
    private const double ToolRailIconWidth = 40;
    private const double ToolRailIconSpacing = 4;

    private Control BuildToolsContentImpl() => BuildLeftRail();

    // ── Left rail ─────────────────────────────────────────────────────────────
    private Control BuildLeftRail()
    {
        _toolStatusText = new TextBlock { IsVisible = false };

        _colorWell = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        var colorBtn = new Button
        {
            Content = _colorWell,
            Width = 26,
            Height = 24,
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(colorBtn, "Cycle color  (X)");
        colorBtn.Click += (_, _) => _ps.Color.CycleColor();

        _toolRailStack = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = ToolRailIconSpacing,
            LineSpacing = ToolRailIconSpacing,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 6, 4, 4)
        };

        var colorBar = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 6),
            Children = { RailSep(), colorBtn }
        };

        var outerStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        outerStack.Children.Add(_toolRailStack);
        outerStack.Children.Add(colorBar);

        _toolRailScroll = ScrollHelper.Create(sv =>
        {
            ScrollHelper.UseVisibleScrollBars(sv, horizontal: false, vertical: true);
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Content = outerStack;
        });
        _toolRailScroll.SizeChanged += (_, _) => QueueSyncToolRailLayout();

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(BgSidebar)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CacheMode = new BitmapCache(),
            Child = _toolRailScroll
        };
    }

    public void QueueSyncToolRailLayout()
    {
        if (_toolRailScroll is null)
            return;

        Dispatcher.UIThread.Post(SyncToolRailLayout, DispatcherPriority.Loaded);
    }

    /// <summary>: icon size fixed; wrap into more columns when the tools panel is wide.</summary>
    private void SyncToolRailLayout()
    {
        if (_toolRailScroll is null)
            return;

        var viewportW = _toolRailScroll.Viewport.Width;
        if (viewportW <= 1 || double.IsInfinity(viewportW) || double.IsNaN(viewportW))
            viewportW = _toolRailScroll.Bounds.Width;
        if (viewportW <= 1)
            return;

        const double horizontalInset = 8;
        var contentW = Math.Max(ToolRailIconWidth, viewportW - horizontalInset);

        _toolRailStack.Width = contentW;
        _toolRailStack.MaxWidth = contentW;
        _toolRailStack.InvalidateMeasure();
    }

    private void BuildToolRail()
    {
        _toolGroupButtons.Clear();
        _toolButtons.Clear();
        _toolRailStack.Children.Clear();

        foreach (var group in Floss.App.App.ToolGroups.Groups)
        {
            var btn = MakeToolGroupButton(group);
            _toolGroupButtons.Add((group, btn));
            _toolButtons.Add(btn);
            _toolRailStack.Children.Add(btn);
        }

        var addBtn = new Button
        {
            Content = FlossUi.Icon(Icons.Plus, FlossUi.IconRail),
            Classes = { "tool-rail" },
        };
        ToolTip.SetTip(addBtn, "Add tool group");
        addBtn.Click += (_, _) => ShowAddToolGroupDialog();
        _ps.Tools.EnableCategoryPromoteDrop(addBtn, null);
        _toolRailStack.Children.Add(addBtn);
        QueueSyncToolRailLayout();
    }

    private Button MakeToolGroupButton(ToolGroup group)
    {
        var shortcutHint = group.Shortcut.IsEmpty ? "" : $"  ({group.Shortcut.Display()})";
        var btn = RailBtn(group.ActiveIcon, $"{group.Name}{shortcutHint}");

        btn.Click += (_, _) =>
        {
            if (_ps.Tools.RecordingToolGroup != null) return;
            if (_ps.Tools.ActiveToolGroup == group)
            {
                // Already active — do nothing. Preset cycling is handled
                // through the sub-tool popup, not the main rail button.
                return;
            }
            var preset = group.ActivePreset ?? group.Presets.FirstOrDefault();
            if (preset != null) _ps.Tools.ActivatePreset(group, preset);
        };

        var menu = new ContextMenu();

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => StartToolGroupRename(group, btn);

        var setIconItem = new MenuItem { Header = "Set Icon" };
        setIconItem.Click += (_, _) => ShowIconPickerDialog(group);

        var setShortcutItem = new MenuItem { Header = "Set Shortcut" };
        setShortcutItem.Click += (_, _) => StartToolGroupShortcutRecording(group, btn);

        var addFromDefaultItem = new MenuItem { Header = "Add from Default" };
        foreach (var defaultGroup in ToolGroupConfig.Defaults())
        {
            var template = defaultGroup;
            var item = new MenuItem
            {
                Header = template.Name,
                Icon = MaterialIcon(template.ActiveIcon, 14)
            };
            item.Click += (_, _) => AddToolGroupFromDefault(group, template);
            addFromDefaultItem.Items.Add(item);
        }

        var moveUpItem = new MenuItem { Header = "Move Up" };
        moveUpItem.Click += (_, _) => MoveToolGroup(group, -1);

        var moveDownItem = new MenuItem { Header = "Move Down" };
        moveDownItem.Click += (_, _) => MoveToolGroup(group, +1);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteToolGroup(group);

        menu.Items.Add(renameItem);
        menu.Items.Add(setIconItem);
        menu.Items.Add(setShortcutItem);
        menu.Items.Add(addFromDefaultItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(moveUpItem);
        menu.Items.Add(moveDownItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        btn.ContextMenu = menu;
        _ps.Tools.EnableCategoryPromoteDrop(btn, group);
        return btn;
    }

    // ── Tool group mutations ──────────────────────────────────────────────────

    private void AddToolGroupFromDefault(ToolGroup afterGroup, ToolGroup template)
    {
        var groups = Floss.App.App.ToolGroups.Groups;
        if (groups.Any(g => !g.Shortcut.IsEmpty && g.Shortcut.ToString() == template.Shortcut.ToString()))
            template.Shortcut = Input.KeyBinding.Empty;

        var newGroup = Floss.App.App.ToolGroups.CreateGroupFromDefault(template, _ps.Tools.BrushAssets);

        var index = groups.IndexOf(afterGroup);
        groups.Insert(index < 0 ? groups.Count : index + 1, newGroup);
        Floss.App.App.ToolGroups.Save();
        RebuildToolRail();

        var preset = newGroup.ActivePreset ?? newGroup.Presets.FirstOrDefault();
        if (preset != null) _ps.Tools.ActivatePreset(newGroup, preset);
    }

    private void MoveToolGroup(ToolGroup group, int delta)
    {
        var groups = Floss.App.App.ToolGroups.Groups;
        var idx = groups.IndexOf(group);
        if (idx < 0) return;
        var newIdx = Math.Clamp(idx + delta, 0, groups.Count - 1);
        if (newIdx == idx) return;
        groups.RemoveAt(idx);
        groups.Insert(newIdx, group);
        Floss.App.App.ToolGroups.Save();
        RebuildToolRail();
    }

    private void DeleteToolGroup(ToolGroup group)
    {
        if (Floss.App.App.ToolGroups.Groups.Count <= 1) return;

        var wasActive = _ps.Tools.ActiveToolGroup == group;
        if (wasActive && group.ActivePreset is { } leaving)
            _ps.Tools.CaptureBrushToPresetIfChanged(leaving);

        Floss.App.App.ToolGroups.Groups.Remove(group);
        Floss.App.App.ToolGroups.Save();
        if (wasActive)
        {
            var first = Floss.App.App.ToolGroups.Groups.FirstOrDefault();
            if (first != null)
            {
                var preset = first.ActivePreset ?? first.Presets.FirstOrDefault();
                if (preset != null) _ps.Tools.ActivatePreset(first, preset);
                else _ps.Tools.ActiveToolGroup = first;
            }
        }
        RebuildToolRail();
    }

    private void StartToolGroupRename(ToolGroup group, Button btn)
    {
        var box = new TextBox
        {
            Text = group.Name,
            Width = 80,
            Height = 24,
            FontSize = 10,
            Padding = new Thickness(4, 2),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Accent)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary))
        };

        var done = false;
        void Commit()
        {
            if (done) return;
            done = true;
            var name = box.Text?.Trim();
            if (!string.IsNullOrEmpty(name)) group.Name = name;
            Floss.App.App.ToolGroups.Save();
            RebuildToolRail();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return || e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { done = true; RebuildToolRail(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();

        var idx = _toolRailStack.Children.IndexOf(btn);
        if (idx >= 0)
        {
            _toolRailStack.Children[idx] = box;
            _ps.Shell.Owner.Focus();
            box.SelectAll();
        }
    }

    // ── Tool group shortcut recording ─────────────────────────────────────────

    private void StartToolGroupShortcutRecording(ToolGroup group, Button btn)
    {
        _ps.Tools.RecordingToolGroup = group;
        _ps.Tools.RecordingToolGroupButton = btn;
        btn.Content = MaterialIcon(group.ActiveIcon, 18);
        btn.Background = new SolidColorBrush(Color.Parse(AccentSoft));
        btn.BorderBrush = new SolidColorBrush(Color.Parse(Accent));
        btn.BorderThickness = new Thickness(1);
        var prompt = $"Press a key for \"{group.Name}\" (Esc cancel, Backspace clear)";
        ToolTip.SetTip(btn, prompt);
        _ps.Shell.FooterStatusText.Text = prompt;
        _ps.Shell.Owner.Focus();
    }

    internal void CommitToolGroupShortcutImpl(Input.KeyBinding kb)
    {
        if (_ps.Tools.RecordingToolGroup == null) return;
        var group = _ps.Tools.RecordingToolGroup;
        CancelToolGroupShortcutRecordingImpl();
        group.Shortcut = kb;
        Floss.App.App.ToolGroups.Save();
        RebuildToolRail();
        _ps.Shell.FooterStatusText.Text = kb.IsEmpty
            ? $"Cleared shortcut for {group.Name}"
            : $"Set {group.Name} shortcut to {kb.Display()}";
    }

    internal void CancelToolGroupShortcutRecordingImpl()
    {
        if (_ps.Tools.RecordingToolGroupButton != null)
        {
            _ps.Tools.RecordingToolGroupButton.Background = Avalonia.Media.Brushes.Transparent;
            _ps.Tools.RecordingToolGroupButton.BorderThickness = new Thickness(0);
            var group = _ps.Tools.RecordingToolGroup;
            if (group != null)
            {
                var shortcutHint = group.Shortcut.IsEmpty ? "" : $"  ({group.Shortcut.Display()})";
                ToolTip.SetTip(_ps.Tools.RecordingToolGroupButton, $"{group.Name}{shortcutHint}");
            }
        }
        if (_ps.Tools.RecordingToolGroup != null)
            _ps.Shell.FooterStatusText.Text = "Shortcut recording cancelled";
        _ps.Tools.RecordingToolGroup = null;
        _ps.Tools.RecordingToolGroupButton = null;
    }

    // ── Add tool group dialog ─────────────────────────────────────────────────

    private void ShowAddToolGroupDialog()
    {
        var engines = Enum.GetValues<ToolKind>();

        var nameBox = new TextBox
        {
            PlaceholderText = "Group name",
            Width = 160,
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
            ItemsSource = engines,
            SelectedIndex = 0,
            Width = 160,
            Height = 28,
            FontSize = 11
        };

        var addBtn = new Button
        {
            Content = "Add",
            Height = 28,
            Padding = new Thickness(14, 0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
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
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var dlg = new Window
        {
            Title = "Add Tool Group",
            Width = 220,
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
                    new TextBlock { Text = "Tool Kind", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                    enginePicker,
                    new TextBlock { Text = "Name", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                    nameBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancelBtn, addBtn } }
                }
            }
        };

        addBtn.Click += (_, _) =>
        {
            var engine = (ToolKind)(enginePicker.SelectedItem ?? ToolKind.Brush);
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) name = engine.ToString();
            Floss.App.App.ToolGroups.Groups.Add(new ToolGroup
            {
                Name = name,
                DefaultKind = engine,
                Shortcut = Input.KeyBinding.Empty,
                Presets = [new ToolPreset { Name = name, Kind = engine }]
            });
            Floss.App.App.ToolGroups.Save();
            RebuildToolRail();
            dlg.Close();
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        dlg.ShowDialog(_ps.Shell.Owner);
    }

    // ── Icon picker ───────────────────────────────────────────────────────────

    private void ShowIconPickerDialog(ToolGroup group)
    {
        var grid = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8)
        };

        // "Auto" button clears the override
        var autoBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "Auto",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            Width = 32,
            Height = 30,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(group.CustomIcon == null ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(group.CustomIcon == null ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(0)
        };
        Window? dlg = null;
        autoBtn.Click += (_, _) =>
        {
            group.CustomIcon = null;
            Floss.App.App.ToolGroups.Save();
            RebuildToolRail();
            dlg?.Close();
        };
        grid.Children.Add(autoBtn);

        foreach (var (name, path) in Icons.ToolIcons)
        {
            var p = path;
            var iconBtn = new Button
            {
                Content = MaterialIcon(p, 18),
            Width = 28,
            Height = 26,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Color.Parse(group.CustomIcon == p ? AccentSoft : Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(group.CustomIcon == p ? Accent : Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(iconBtn, name);
            iconBtn.Click += (_, _) =>
            {
                group.CustomIcon = p;
                Floss.App.App.ToolGroups.Save();
                RebuildToolRail();
                dlg?.Close();
            };
            grid.Children.Add(iconBtn);
        }

        dlg = new Window
        {
            Title = $"Icon — {group.Name}",
            Width = 220,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Content = grid
        };
        dlg.ShowDialog(_ps.Shell.Owner);
    }

    // ── Rail helpers ──────────────────────────────────────────────────────────

    private static PathIcon MaterialIcon(string pathData, double size) =>
        Icons.Make(pathData, size, new SolidColorBrush(Color.Parse(TextSecondary)));

    private static Button RailBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = FlossUi.Icon(icon, FlossUi.IconRail),
            Classes = { "tool-rail" },
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Border RailSep() => new()
    {
        Height = 1,
        Width = 32,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(0, 4)
    };
}
