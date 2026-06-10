using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Config;
using Floss.App.Input;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

public sealed class ModifierKeySettingsWindow : Window
{
    private static readonly (Avalonia.Input.Key? Key, KeyModifiers Mods)[] ModifierCombos =
    [
        (null, KeyModifiers.Shift),
        (null, KeyModifiers.Control),
        (null, KeyModifiers.Alt),
        (null, KeyModifiers.Control | KeyModifiers.Alt),
        (null, KeyModifiers.Control | KeyModifiers.Shift),
        (null, KeyModifiers.Alt | KeyModifiers.Shift),
        (null, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift),
        (Key.Space, KeyModifiers.None),
        (Key.Space, KeyModifiers.Control),
        (Key.Space, KeyModifiers.Alt),
        (Key.Space, KeyModifiers.Control | KeyModifiers.Alt),
        (Key.Space, KeyModifiers.Shift),
        (Key.Space, KeyModifiers.Control | KeyModifiers.Shift),
        (Key.Space, KeyModifiers.Alt | KeyModifiers.Shift),
        (Key.Space, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift),
    ];

    private readonly ModifierKeySettings _settings;

    // Mode
    private bool _toolSpecificMode;
    private ToolKind? _selectedToolKind;

    // Refine filter
    private bool _showCtrl = true, _showShift = true, _showAlt = true, _showSpace = true;

    // UI containers rebuilt on mode/filter/tool change
    private readonly Grid _tableHost = new();
    private readonly TextBlock _toolLabel = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)) };
    private readonly TextBlock _kindLabel = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)) };
    private readonly Border _toolHeader;

    public ModifierKeySettingsWindow()
    {
        _settings = App.ModifierKeys;

        Width = 700;
        Height = 540;
        CanResize = true;
        MinWidth = 500;
        MinHeight = 380;
        Title = "Modifier Key Settings";

        CustomWindowChrome.ConfigurePopup(this);

        // ── Top: mode radio buttons ──────────────────────────────────────────
        var generalRadio = new RadioButton
        {
            Content = "General settings(C)",
            IsChecked = true,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            GroupName = "mode"
        };
        var specificRadio = new RadioButton
        {
            Content = "Tool-specific settings",
            IsChecked = false,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            GroupName = "mode"
        };

        generalRadio.IsCheckedChanged += (_, _) => { if (generalRadio.IsChecked == true) SetMode(false); };
        specificRadio.IsCheckedChanged += (_, _) => { if (specificRadio.IsChecked == true) SetMode(true); };

        var modeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Margin = new Thickness(12, 10, 12, 6),
            Children = { generalRadio, specificRadio }
        };

        // ── Tool header (tool-specific mode) ─────────────────────────────────
        var toolSelectBtn = new Button
        {
            Height = 24,
            FontSize = 11,
            Padding = new Thickness(10, 0),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        UpdateToolSelectButton(toolSelectBtn);
        toolSelectBtn.Click += (_, _) => ShowToolComboPickerDialog(toolSelectBtn);

        _toolHeader = new Border
        {
            IsVisible = false,
            Padding = new Thickness(12, 4, 12, 4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Tool:", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)), VerticalAlignment = VerticalAlignment.Center },
                            toolSelectBtn
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = "Kind:", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)), VerticalAlignment = VerticalAlignment.Center },
                            _kindLabel
                        }
                    }
                }
            }
        };

        // ── Refine filter ─────────────────────────────────────────────────────
        var ctrlCb = MakeFilterCheck("Ctrl", v => _showCtrl = v);
        var shiftCb = MakeFilterCheck("Shift", v => _showShift = v);
        var altCb = MakeFilterCheck("Alt", v => _showAlt = v);
        var spaceCb = MakeFilterCheck("Space", v => _showSpace = v);

        CheckBox MakeFilterCheck(string label, Action<bool> setter)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = true,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
            };
            cb.IsCheckedChanged += (_, _) => { setter(cb.IsChecked == true); RebuildTable(); };
            return cb;
        }

        var refineRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(12, 2, 12, 6),
            Children =
            {
                new TextBlock { Text = "Refine:", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)), VerticalAlignment = VerticalAlignment.Center },
                ctrlCb, shiftCb, altCb, spaceCb
            }
        };

        // ── Table host (rebuilt on changes) ──────────────────────────────────
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = _tableHost
        };

        // ── Footer buttons ────────────────────────────────────────────────────
        var okBtn = Btn("OK", true);
        okBtn.Click += (_, _) => { _settings.Save(); Close(); };

        var cancelBtn = Btn("Cancel", false);
        cancelBtn.Click += (_, _) => Close();

        var resetBtn = Btn("Reset", false);
        resetBtn.Click += (_, _) =>
        {
            if (_toolSpecificMode && _selectedToolKind.HasValue)
                _settings.ToolSpecificAssignments.Remove(ModifierKeySettings.KeyFor(_selectedToolKind.Value));
            else
            {
                _settings.GeneralAssignments.Clear();
                foreach (var a in ModifierKeySettings.CreateDefaults().GeneralAssignments)
                    _settings.GeneralAssignments.Add(a);
            }
            _settings.Save();
            RebuildTable();
        };

        var footer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { resetBtn, cancelBtn, okBtn }
            }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(modeRow, 0);
        Grid.SetRow(_toolHeader, 1);
        Grid.SetRow(refineRow, 2);
        Grid.SetRow(MakeSeparator(), 3);
        Grid.SetRow(scroll, 4);
        Grid.SetRow(footer, 5);
        root.Children.Add(modeRow);
        root.Children.Add(_toolHeader);
        root.Children.Add(refineRow);
        root.Children.Add(MakeSeparator());
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Content = CustomWindowChrome.Wrap(this, Title, root);

        RebuildTable();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _settings.Save();
    }

    private void SetMode(bool toolSpecific)
    {
        _toolSpecificMode = toolSpecific;
        _toolHeader.IsVisible = toolSpecific;

        if (toolSpecific && _selectedToolKind == null)
        {
            var first = GetUniqueToolKinds().FirstOrDefault();
            if (first.Kind != default)
            {
                _selectedToolKind = first.Kind;
                UpdateToolLabels();
            }
        }

        RebuildTable();
    }

    private void ShowToolComboPickerDialog(Button anchor)
    {
        var kinds = GetUniqueToolKinds();
        if (kinds.Count == 0) return;

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(4) };
        Window? dlg = null;

        foreach (var (kind, presets) in kinds)
        {
            var isSelected = _selectedToolKind == kind;
            var btn = new Button
            {
                Content = kind.ToString(),
                Padding = new Thickness(10, 5),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.Parse(isSelected ? AccentSoft : "Transparent")),
                Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0)
            };
            var captured = kind;
            btn.Click += (_, _) =>
            {
                _selectedToolKind = captured;
                UpdateToolLabels();
                UpdateToolSelectButton(anchor);
                RebuildTable();
                dlg?.Close();
            };
            list.Children.Add(btn);
        }

        dlg = new Window
        {
            Title = "Select Tool Kind",
            Width = 280,
            Height = 320,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = list
            }
        };
        dlg.ShowDialog(this);
    }

    private void UpdateToolSelectButton(Button btn)
    {
        btn.Content = _selectedToolKind?.ToString() ?? "(select tool)";
    }

    private void UpdateToolLabels()
    {
        _kindLabel.Text = _selectedToolKind?.ToString() ?? "";
    }

    private void RebuildTable()
    {
        _tableHost.Children.Clear();
        _tableHost.RowDefinitions.Clear();

        var rows = BuildRows();
        foreach (var row in rows)
        {
            _tableHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(row, _tableHost.RowDefinitions.Count - 1);
            _tableHost.Children.Add(row);
        }
    }

    private IEnumerable<Control> BuildRows()
    {
        var comboKey = _toolSpecificMode && _selectedToolKind.HasValue
            ? ModifierKeySettings.KeyFor(_selectedToolKind.Value)
            : null;
        var specificList = comboKey != null && _settings.ToolSpecificAssignments.TryGetValue(comboKey, out var sl) ? sl : null;

        foreach (var (key, mods) in ModifierCombos)
        {
            if (!PassesFilter(key, mods)) continue;

            ModifierKeyAssignment? current;
            if (_toolSpecificMode && specificList != null)
                current = specificList.FirstOrDefault(a => a.Modifiers == mods && a.Key == key);
            else
                current = _settings.GeneralAssignments.FirstOrDefault(a => a.Modifiers == mods && a.Key == key);

            var (k, m) = (key, mods); // capture
            yield return BuildRow(k, m, current, comboKey);
        }
    }

    private bool PassesFilter(Avalonia.Input.Key? key, KeyModifiers mods)
    {
        if (key == Key.Space) return _showSpace;
        if ((mods & KeyModifiers.Control) != 0 && !_showCtrl) return false;
        if ((mods & KeyModifiers.Shift) != 0 && !_showShift) return false;
        if ((mods & KeyModifiers.Alt) != 0 && !_showAlt) return false;
        return true;
    }

    private Border BuildRow(Avalonia.Input.Key? key, KeyModifiers mods, ModifierKeyAssignment? current, string? comboKey)
    {
        var actions = _toolSpecificMode
            ? new[] { ModifierAction.None, ModifierAction.Common, ModifierAction.AlternateInvocation, ModifierAction.ChangeToolTemporarily, ModifierAction.ToolAux, ModifierAction.ChangeBrushSize }
            : new[] { ModifierAction.None, ModifierAction.ChangeToolTemporarily, ModifierAction.ToolAux, ModifierAction.ChangeBrushSize };

        var actionDropdown = new ComboBox
        {
            Width = 190,
            Height = 24,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        foreach (var a in actions)
            actionDropdown.Items.Add(new ComboBoxItem { Content = ActionLabel(a), Tag = a });
        var selectedIdx = Array.IndexOf(actions, current?.Action ?? ModifierAction.None);
        actionDropdown.SelectedIndex = Math.Max(0, selectedIdx);

        // Description text + Settings button (rebuilt when action changes)
        var descLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            MinWidth = 80,
            Text = DescriptionFor(current)
        };

        var auxOperDropdown = new ComboBox
        {
            Width = 150,
            Height = 24,
            FontSize = 10,
            Margin = new Thickness(4, 0),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            IsVisible = current?.Action == ModifierAction.ToolAux
        };
        foreach (var oper in Enum.GetValues<ToolAuxOperationType>())
        {
            if (oper == ToolAuxOperationType.None) continue;
            auxOperDropdown.Items.Add(new ComboBoxItem { Content = ToolAuxLabel(oper), Tag = oper });
        }
        if (current?.Action == ModifierAction.ToolAux)
        {
            var auxIdx = Array.FindIndex(auxOperDropdown.Items.OfType<ComboBoxItem>().ToArray(),
                i => i.Tag is ToolAuxOperationType t && t == current.ToolAuxOper);
            auxOperDropdown.SelectedIndex = Math.Max(0, auxIdx);
        }

        var settingsBtn = new Button
        {
            Content = "Settings",
            Height = 22,
            FontSize = 10,
            Padding = new Thickness(8, 0),
            Margin = new Thickness(4, 0),
            IsVisible = current?.Action is ModifierAction.ChangeToolTemporarily or ModifierAction.AlternateInvocation,
            Classes = { "outline" }
        };

        settingsBtn.Click += (_, _) =>
        {
            var curAssignment = GetAssignment(key, mods, comboKey);
            var dlg = new SelectToolDialog(curAssignment?.TemporaryToolPresetId);
            dlg.ShowDialog(this).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    CrashLog.Write(t.Exception!, "ModifierKeySettingsWindow.SelectToolDialog");
                if (dlg.SelectedPresetId != null)
                {
                    SetTemporaryToolPreset(key, mods, comboKey, dlg.SelectedPresetId);
                    descLabel.Text = FindPresetName(dlg.SelectedPresetId);
                }
            });
        };

        auxOperDropdown.SelectionChanged += (_, _) =>
        {
            if (auxOperDropdown.SelectedItem is not ComboBoxItem { Tag: ToolAuxOperationType oper }) return;
            SetToolAuxOper(key, mods, comboKey, oper);
            descLabel.Text = DescriptionFor(GetAssignment(key, mods, comboKey));
            _settings.Save();
        };

        actionDropdown.SelectionChanged += (_, _) =>
        {
            if (actionDropdown.SelectedItem is not ComboBoxItem { Tag: ModifierAction action }) return;
            var prior = GetAssignment(key, mods, comboKey);
            SetAction(key, mods, action, comboKey, prior?.ToolAuxOper);
            settingsBtn.IsVisible = action is ModifierAction.ChangeToolTemporarily or ModifierAction.AlternateInvocation;
            auxOperDropdown.IsVisible = action == ModifierAction.ToolAux;
            if (action == ModifierAction.ToolAux)
            {
                var assignment = GetAssignment(key, mods, comboKey);
                var auxIdx = Array.FindIndex(auxOperDropdown.Items.OfType<ComboBoxItem>().ToArray(),
                    i => i.Tag is ToolAuxOperationType t && t == assignment?.ToolAuxOper);
                auxOperDropdown.SelectedIndex = Math.Max(0, auxIdx);
            }
            descLabel.Text = DescriptionFor(GetAssignment(key, mods, comboKey));
            _settings.Save();
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition(130, GridUnitType.Pixel));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var label = new TextBlock
        {
            Text = ComboLabel(key, mods) + ":",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0)
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(actionDropdown, 1);
        Grid.SetColumn(auxOperDropdown, 2);
        Grid.SetColumn(settingsBtn, 3);
        Grid.SetColumn(descLabel, 4);
        row.Children.Add(label);
        row.Children.Add(actionDropdown);
        row.Children.Add(auxOperDropdown);
        row.Children.Add(settingsBtn);
        row.Children.Add(descLabel);

        return new Border
        {
            Padding = new Thickness(0, 3),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private void SetAction(Avalonia.Input.Key? key, KeyModifiers mods, ModifierAction action, string? comboKey,
        ToolAuxOperationType? priorAuxOper = null)
    {
        var defaultAux = priorAuxOper is ToolAuxOperationType prior && prior != ToolAuxOperationType.None
            ? prior
            : ToolAuxOperationType.StraightLine;

        if (comboKey != null)
        {
            if (!_settings.ToolSpecificAssignments.TryGetValue(comboKey, out var lst))
            {
                lst = [];
                _settings.ToolSpecificAssignments[comboKey] = lst;
            }
            lst.RemoveAll(a => a.Key == key && a.Modifiers == mods);
            if (action != ModifierAction.None)
                lst.Add(new ModifierKeyAssignment
                {
                    Key = key,
                    Modifiers = mods,
                    Action = action,
                    ToolAuxOper = action == ModifierAction.ToolAux ? defaultAux : ToolAuxOperationType.None
                });
            if (lst.Count == 0)
                _settings.ToolSpecificAssignments.Remove(comboKey);
        }
        else
        {
            _settings.GeneralAssignments.RemoveAll(a => a.Key == key && a.Modifiers == mods);
            if (action != ModifierAction.None)
                _settings.GeneralAssignments.Add(new ModifierKeyAssignment
                {
                    Key = key,
                    Modifiers = mods,
                    Action = action,
                    ToolAuxOper = action == ModifierAction.ToolAux ? defaultAux : ToolAuxOperationType.None
                });
        }
    }

    private void SetToolAuxOper(Avalonia.Input.Key? key, KeyModifiers mods, string? comboKey, ToolAuxOperationType oper)
    {
        List<ModifierKeyAssignment> lst;
        if (comboKey != null)
        {
            if (!_settings.ToolSpecificAssignments.TryGetValue(comboKey, out lst!))
            {
                lst = [];
                _settings.ToolSpecificAssignments[comboKey] = lst;
            }
        }
        else
        {
            lst = _settings.GeneralAssignments;
        }

        var existing = lst.FirstOrDefault(a => a.Key == key && a.Modifiers == mods);
        if (existing != null)
            existing.ToolAuxOper = oper;
        else
            lst.Add(new ModifierKeyAssignment
            {
                Key = key,
                Modifiers = mods,
                Action = ModifierAction.ToolAux,
                ToolAuxOper = oper
            });
    }

    private void SetTemporaryToolPreset(Avalonia.Input.Key? key, KeyModifiers mods, string? comboKey, string presetId)
    {
        List<ModifierKeyAssignment> lst;
        if (comboKey != null)
        {
            if (!_settings.ToolSpecificAssignments.TryGetValue(comboKey, out lst!))
            {
                lst = [];
                _settings.ToolSpecificAssignments[comboKey] = lst;
            }
        }
        else
        {
            lst = _settings.GeneralAssignments;
        }

        var existing = lst.FirstOrDefault(a => a.Key == key && a.Modifiers == mods);
        if (existing != null)
            existing.TemporaryToolPresetId = presetId;
        else
            lst.Add(new ModifierKeyAssignment { Key = key, Modifiers = mods, Action = ModifierAction.ChangeToolTemporarily, TemporaryToolPresetId = presetId });
        _settings.Save();
    }

    private ModifierKeyAssignment? GetAssignment(Avalonia.Input.Key? key, KeyModifiers mods, string? comboKey)
    {
        if (comboKey != null && _settings.ToolSpecificAssignments.TryGetValue(comboKey, out var lst))
            return lst.FirstOrDefault(a => a.Key == key && a.Modifiers == mods);
        return _settings.GeneralAssignments.FirstOrDefault(a => a.Key == key && a.Modifiers == mods);
    }

    private string DescriptionFor(ModifierKeyAssignment? a)
    {
        if (a == null || a.Action == ModifierAction.None || a.Action == ModifierAction.Common) return "";
        return a.Action switch
        {
            ModifierAction.AlternateInvocation => a.TemporaryToolPresetId != null
                ? $"Fallback: {FindPresetName(a.TemporaryToolPresetId)}"
                : "Eyedropper",
            ModifierAction.ChangeToolTemporarily when a.TemporaryToolPresetId != null => FindPresetName(a.TemporaryToolPresetId),
            ModifierAction.ToolAux => a.ToolAuxOper switch
            {
                ToolAuxOperationType.StraightLine => "Straight line",
                ToolAuxOperationType.AddToSelection => "Add to selection",
                ToolAuxOperationType.RemoveFromSelection => "Remove from selection",
                ToolAuxOperationType.SelectFromSelection => "Select from selection",
                _ => ""
            },
            ModifierAction.ChangeBrushSize => "",
            _ => ""
        };
    }

    private static string FindPresetName(string presetId)
    {
        foreach (var group in App.ToolGroups.Groups)
        {
            var preset = group.Presets.FirstOrDefault(p => p.Id == presetId);
            if (preset != null) return $"{group.Name}/{preset.Name}";
        }
        return presetId;
    }

    private static string ToolAuxLabel(ToolAuxOperationType oper) => oper switch
    {
        ToolAuxOperationType.StraightLine => "Straight line",
        ToolAuxOperationType.AddToSelection => "Add to selection",
        ToolAuxOperationType.RemoveFromSelection => "Remove from selection",
        ToolAuxOperationType.SelectFromSelection => "Select from selection",
        _ => oper.ToString()
    };

    private static string ActionLabel(ModifierAction action) => action switch
    {
        ModifierAction.None => "None",
        ModifierAction.Common => "General",
        ModifierAction.AlternateInvocation => "Alternate invocation",
        ModifierAction.ChangeToolTemporarily => "Change tool temporarily",
        ModifierAction.ToolAux => "Tool aux. operation",
        ModifierAction.ChangeBrushSize => "Change brush size",
        _ => action.ToString()
    };

    private static string ComboLabel(Avalonia.Input.Key? key, KeyModifiers mods)
    {
        var modStr = mods switch
        {
            KeyModifiers.None => "",
            KeyModifiers.Shift => "Shift",
            KeyModifiers.Control => "Ctrl",
            KeyModifiers.Alt => "Alt",
            KeyModifiers.Control | KeyModifiers.Alt => "Ctrl+Alt",
            KeyModifiers.Control | KeyModifiers.Shift => "Ctrl+Shift",
            KeyModifiers.Alt | KeyModifiers.Shift => "Alt+Shift",
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift => "Ctrl+Alt+Shift",
            _ => mods.ToString()
        };
        if (key == null) return modStr;
        var keyStr = key == Avalonia.Input.Key.Space ? "Space" : key.ToString()!;
        return string.IsNullOrEmpty(modStr) ? keyStr : modStr + "+" + keyStr;
    }

    private static Border MakeSeparator() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(Color.Parse("#222428")),
        Margin = new Thickness(0, 2, 0, 2)
    };

    private static Button Btn(string text, bool accent) => new()
    {
        Content = text,
        Width = 70,
        Height = 26,
        FontSize = 11,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(accent ? AccentSoft : Bg2)),
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
        BorderBrush = new SolidColorBrush(Color.Parse(accent ? Accent : Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    private static List<(ToolKind Kind, List<string> Presets)> GetUniqueToolKinds()
    {
        var map = new Dictionary<ToolKind, List<string>>();
        foreach (var group in App.ToolGroups.Groups)
        foreach (var preset in group.Presets)
        {
            if (!map.TryGetValue(preset.Kind, out var presets))
            {
                presets = [];
                map[preset.Kind] = presets;
            }
            presets.Add($"{group.Name}/{preset.Name}");
        }
        return map.Select(kv => (kv.Key, kv.Value)).OrderBy(v => v.Key).ToList();
    }
}
