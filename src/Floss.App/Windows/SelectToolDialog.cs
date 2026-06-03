using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

public sealed class SelectToolDialog : Window
{
    public string? SelectedPresetId { get; private set; }

    private readonly Dictionary<Button, string> _presetButtons = [];
    private Button? _selectedButton;

    public SelectToolDialog(string? currentPresetId = null)
    {
        Title = "Select tool";
        Width = 300;
        Height = 500;
        CanResize = true;
        MinWidth = 220;
        MinHeight = 300;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.Parse(Bg1));

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 4) };

        foreach (var group in App.ToolGroups.Groups)
        {
            var header = new Border
            {
                Padding = new Thickness(10, 5),
                Background = new SolidColorBrush(Color.Parse(Bg2)),
                Child = new TextBlock
                {
                    Text = group.Name,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(TextPrimary))
                }
            };
            list.Children.Add(header);

            foreach (var preset in group.Presets)
            {
                var isSelected = preset.Id == currentPresetId;
                var btn = new Button
                {
                    Content = preset.Name,
                    Padding = new Thickness(20, 5, 10, 5),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.Parse(isSelected ? AccentSoft : "Transparent")),
                    Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0)
                };

                var p = preset;
                btn.Click += (_, _) => SelectPreset(p.Id);
                _presetButtons[btn] = p.Id;
                if (isSelected) _selectedButton = btn;
                list.Children.Add(btn);
            }
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = list
        };

        var okBtn = new Button
        {
            Content = "OK",
            Width = 70,
            Height = 26,
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        okBtn.Classes.Add("primary");
        okBtn.Click += (_, _) => { SelectedPresetId = _presetButtons.GetValueOrDefault(_selectedButton!); Close(); };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 70,
            Height = 26,
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelBtn.Classes.Add("outline");
        cancelBtn.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 6),
            Children = { cancelBtn, okBtn }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(scroll, 0);
        Grid.SetRow(footer, 1);
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Content = root;
    }

    private void SelectPreset(string presetId)
    {
        foreach (var (btn, id) in _presetButtons)
        {
            var active = id == presetId;
            btn.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : "Transparent"));
            if (active) _selectedButton = btn;
        }
    }
}
