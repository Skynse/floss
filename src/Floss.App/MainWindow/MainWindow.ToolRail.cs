using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App;

public partial class MainWindow
{
    // ── Left rail ─────────────────────────────────────────────────────────────
    private Control BuildLeftRail()
    {
        _toolStatusText = new TextBlock { IsVisible = false };

        _colorWell = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a3e")),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.Parse("#111112"))
        };
        var colorBtn = new Button
        {
            Content = _colorWell,
            Width = 36,
            Height = 34,
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(colorBtn, "Cycle color  (X)");
        colorBtn.Click += (_, _) => CycleColor();

        _toolRailStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6),
            Spacing = 1
        };

        BuildToolRail();

        _undoButton = RailBtn(Icons.UndoVariant, "Undo  (Ctrl+Z)");
        _redoButton = RailBtn(Icons.RedoVariant, "Redo  (Ctrl+Shift+Z)");
        _undoButton.Click += (_, _) => _canvas.Undo();
        _redoButton.Click += (_, _) => _canvas.Redo();

        var clearBtn = RailBtn(Icons.DeleteOutline, "Clear layer");
        clearBtn.Click += (_, _) => _canvas.Clear();

        var outerStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6),
            Spacing = 1
        };
        outerStack.Children.Add(_toolRailStack);
        outerStack.Children.Add(RailSep());
        outerStack.Children.Add(colorBtn);
        outerStack.Children.Add(RailSep());
        outerStack.Children.Add(_undoButton);
        outerStack.Children.Add(_redoButton);
        outerStack.Children.Add(clearBtn);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            CacheMode = new BitmapCache(),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = outerStack
            }
        };
    }

    private void BuildToolRail()
    {
        _toolGroupButtons.Clear();
        _toolButtons.Clear();
        _toolRailStack.Children.Clear();

        foreach (var group in App.ToolGroups.Groups)
        {
            var btn = MakeToolGroupButton(group);
            _toolGroupButtons.Add((group, btn));
            _toolButtons.Add(btn);
            _toolRailStack.Children.Add(btn);
        }

        var addBtn = new Button
        {
            Content = "+",
            Width = 36,
            Height = 28,
            FontSize = 16,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0)
        };
        ToolTip.SetTip(addBtn, "Add tool group");
        addBtn.Click += (_, _) => ShowAddToolGroupDialog();
        _toolRailStack.Children.Add(addBtn);
    }

    private Button MakeToolGroupButton(ToolGroup group)
    {
        var shortcutHint = group.Shortcut.IsEmpty ? "" : $"  ({group.Shortcut.Display()})";
        var btn = RailBtn(group.ActiveIcon, $"{group.Name}{shortcutHint}");

        btn.Click += (_, _) =>
        {
            if (_recordingToolGroup != null) return;
            if (_activeToolGroup == group)
            {
                // Already active — do nothing. Preset cycling is handled
                // through the sub-tool popup, not the main rail button.
                return;
            }
            var preset = group.ActivePreset ?? group.Presets.FirstOrDefault();
            if (preset != null) ActivatePreset(group, preset);
        };

        var menu = new ContextMenu();

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => StartToolGroupRename(group, btn);

        var setIconItem = new MenuItem { Header = "Set Icon" };
        setIconItem.Click += (_, _) => ShowIconPickerDialog(group);

        var setShortcutItem = new MenuItem { Header = "Set Shortcut" };
        setShortcutItem.Click += (_, _) => StartToolGroupShortcutRecording(group, btn);

        var moveUpItem = new MenuItem { Header = "Move Up" };
        moveUpItem.Click += (_, _) => MoveToolGroup(group, -1);

        var moveDownItem = new MenuItem { Header = "Move Down" };
        moveDownItem.Click += (_, _) => MoveToolGroup(group, +1);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteToolGroup(group);

        menu.Items.Add(renameItem);
        menu.Items.Add(setIconItem);
        menu.Items.Add(setShortcutItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(moveUpItem);
        menu.Items.Add(moveDownItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        btn.ContextMenu = menu;
        return btn;
    }

    // ── Tool group mutations ──────────────────────────────────────────────────

    private void MoveToolGroup(ToolGroup group, int delta)
    {
        var groups = App.ToolGroups.Groups;
        var idx = groups.IndexOf(group);
        if (idx < 0) return;
        var newIdx = Math.Clamp(idx + delta, 0, groups.Count - 1);
        if (newIdx == idx) return;
        groups.RemoveAt(idx);
        groups.Insert(newIdx, group);
        App.ToolGroups.Save();
        BuildToolRail();
    }

    private void DeleteToolGroup(ToolGroup group)
    {
        if (App.ToolGroups.Groups.Count <= 1) return;
        App.ToolGroups.Groups.Remove(group);
        App.ToolGroups.Save();
        if (_activeToolGroup == group)
        {
            var first = App.ToolGroups.Groups.FirstOrDefault();
            if (first != null)
            {
                var preset = first.ActivePreset ?? first.Presets.FirstOrDefault();
                if (preset != null) ActivatePreset(first, preset);
                else _activeToolGroup = first;
            }
        }
        BuildToolRail();
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
            Background = new SolidColorBrush(Color.Parse("#1a1c22")),
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
            App.ToolGroups.Save();
            BuildToolRail();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return || e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { done = true; BuildToolRail(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();

        var idx = _toolRailStack.Children.IndexOf(btn);
        if (idx >= 0)
        {
            _toolRailStack.Children[idx] = box;
            box.Focus();
            box.SelectAll();
        }
    }

    // ── Tool group shortcut recording ─────────────────────────────────────────

    private void StartToolGroupShortcutRecording(ToolGroup group, Button btn)
    {
        _recordingToolGroup = group;
        _recordingToolGroupButton = btn;
        btn.Content = MaterialIcon(group.ActiveIcon, 18);
        btn.Background = new SolidColorBrush(Color.Parse(AccentSoft));
        btn.BorderBrush = new SolidColorBrush(Color.Parse(Accent));
        btn.BorderThickness = new Thickness(1);
        ToolTip.SetTip(btn, $"Press a key for \"{group.Name}\"… (Esc = cancel, Backspace = clear)");
    }

    internal void CommitToolGroupShortcut(Input.KeyBinding kb)
    {
        if (_recordingToolGroup == null) return;
        var group = _recordingToolGroup;
        CancelToolGroupShortcutRecording();
        group.Shortcut = kb;
        App.ToolGroups.Save();
        BuildToolRail();
    }

    internal void CancelToolGroupShortcutRecording()
    {
        if (_recordingToolGroupButton != null)
        {
            _recordingToolGroupButton.Background = Avalonia.Media.Brushes.Transparent;
            _recordingToolGroupButton.BorderThickness = new Thickness(0);
            var group = _recordingToolGroup;
            if (group != null)
            {
                var shortcutHint = group.Shortcut.IsEmpty ? "" : $"  ({group.Shortcut.Display()})";
                ToolTip.SetTip(_recordingToolGroupButton, $"{group.Name}{shortcutHint}");
            }
        }
        _recordingToolGroup = null;
        _recordingToolGroupButton = null;
    }

    // ── Add tool group dialog ─────────────────────────────────────────────────

    private void ShowAddToolGroupDialog()
    {
        var engines = Enum.GetValues<ToolPresetEngine>();

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
                    new TextBlock { Text = "Engine", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                    enginePicker,
                    new TextBlock { Text = "Name", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) },
                    nameBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancelBtn, addBtn } }
                }
            }
        };

        addBtn.Click += (_, _) =>
        {
            var engine = (ToolPresetEngine)(enginePicker.SelectedItem ?? ToolPresetEngine.Brush);
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) name = engine.ToString();
            App.ToolGroups.Groups.Add(new ToolGroup
            {
                Name = name,
                DefaultEngine = engine,
                Shortcut = Input.KeyBinding.Empty,
                Presets = [new ToolPreset { Name = name, Engine = engine }]
            });
            App.ToolGroups.Save();
            BuildToolRail();
            dlg.Close();
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        dlg.ShowDialog(this);
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
            Width = 36, Height = 34,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(group.CustomIcon == null ? AccentSoft : Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(group.CustomIcon == null ? Accent : Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0)
        };
        Window? dlg = null;
        autoBtn.Click += (_, _) =>
        {
            group.CustomIcon = null;
            App.ToolGroups.Save();
            BuildToolRail();
            dlg?.Close();
        };
        grid.Children.Add(autoBtn);

        foreach (var (name, path) in Icons.ToolIcons)
        {
            var p = path;
            var iconBtn = new Button
            {
                Content = MaterialIcon(p, 18),
                Width = 36, Height = 34,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Color.Parse(group.CustomIcon == p ? AccentSoft : Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(group.CustomIcon == p ? Accent : Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(iconBtn, name);
            iconBtn.Click += (_, _) =>
            {
                group.CustomIcon = p;
                App.ToolGroups.Save();
                BuildToolRail();
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
        dlg.ShowDialog(this);
    }

    // ── Rail helpers ──────────────────────────────────────────────────────────

    private static Button RailBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = MaterialIcon(icon, 18),
            Width = 36,
            Height = 34,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Border RailSep() => new()
    {
        Height = 1,
        Width = 26,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(0, 4)
    };
}
