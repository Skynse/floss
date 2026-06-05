using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.SmartShape;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{
    private Border? _smartShapeLauncherBar;
    private StackPanel? _smartShapeLauncherRow;
    private ContextMenu? _smartShapeOverflowMenu;

    private void BuildSmartShapeLauncher()
    {
        _smartShapeLauncherRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 5)
        };

        _smartShapeLauncherBar = new Border
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(240, 22, 22, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 80, 80, 88)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Child = _smartShapeLauncherRow,
            ZIndex = 60,
            Margin = new Thickness(12, 48, 0, 0)
        };

        _workspaceViewport.Children.Add(_smartShapeLauncherBar);
    }

    private void UpdateSmartShapeLauncher()
    {
        if (_smartShapeLauncherBar == null || _smartShapeLauncherRow == null)
            return;

        var phase = _canvas.GetSmartShapePhase();
        var show = App.Config.SmartShapeShowLauncher
            && phase is SmartShapePhase.Launcher or SmartShapePhase.Gizmo;
        _smartShapeLauncherBar.IsVisible = show;

        if (!show
            || _canvas.ActiveTool is not Processes.CompositeTool { Input: Processes.Input.SmartShapeBrushInputProcess ss }
            || ss.CurrentShape == null)
            return;

        RebuildSmartShapeLauncherRow(ss, phase);
        PositionSmartShapeLauncher(ss.CurrentShape);
    }

    private void RebuildSmartShapeLauncherRow(
        Processes.Input.SmartShapeBrushInputProcess ss,
        SmartShapePhase phase)
    {
        if (_smartShapeLauncherRow == null)
            return;

        _smartShapeLauncherRow.Children.Clear();
        var active = ss.ActiveFitKind;
        var closed = ss.StrokeClosed;

        var typeBtn = LauncherFitButton(active, active, isPrimary: true);
        typeBtn.Click += (_, _) =>
        {
            if (phase == SmartShapePhase.Launcher)
                _canvas.EnterSmartShapeGizmoEdit();
            UpdateSmartShapeLauncher();
        };
        _smartShapeLauncherRow.Children.Add(typeBtn);

        _smartShapeLauncherRow.Children.Add(LauncherDivider());

        foreach (var kind in SmartShapeFitKindExtensions.PrimaryOptions(closed))
        {
            if (kind == active)
                continue;
            var btn = LauncherFitButton(kind, active, isPrimary: false);
            var captured = kind;
            btn.Click += (_, _) =>
            {
                _canvas.RefitSmartShape(captured);
                UpdateSmartShapeLauncher();
            };
            _smartShapeLauncherRow.Children.Add(btn);
        }

        var more = LauncherIconButton(Icons.DotsHorizontal, "More shape types");
        more.Click += (_, _) => ShowSmartShapeOverflow(more, closed, active);
        _smartShapeLauncherRow.Children.Add(more);

        if (phase == SmartShapePhase.Launcher)
        {
            _smartShapeLauncherRow.Children.Add(LauncherDivider());
            var edit = LauncherTextButton("Edit", "Enter edit mode");
            edit.Click += (_, _) =>
            {
                _canvas.EnterSmartShapeGizmoEdit();
                UpdateSmartShapeLauncher();
            };
            _smartShapeLauncherRow.Children.Add(edit);
        }
    }

    private void ShowSmartShapeOverflow(Control anchor, bool closed, SmartShapeFitKind active)
    {
        _smartShapeOverflowMenu?.Close();
        var items = new List<MenuItem>();
        foreach (var kind in SmartShapeFitKindExtensions.OverflowOptions(closed))
        {
            if (kind == active)
                continue;
            var item = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        FlossUi.Icon(kind.IconPath(), 14),
                        new TextBlock { Text = kind.Label(), VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            var captured = kind;
            item.Click += (_, _) =>
            {
                _canvas.RefitSmartShape(captured);
                UpdateSmartShapeLauncher();
            };
            items.Add(item);
        }

        _smartShapeOverflowMenu = new ContextMenu { ItemsSource = items };
        _smartShapeOverflowMenu.Closed += (_, _) => _smartShapeOverflowMenu = null;
        _smartShapeOverflowMenu.Open(anchor);
    }

    private static Button LauncherFitButton(SmartShapeFitKind kind, SmartShapeFitKind active, bool isPrimary)
    {
        const string accent = "#5A96FF";
        var selected = kind == active;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                FlossUi.Icon(kind.IconPath(), isPrimary ? 18 : 15, selected ? accent : TextSecondary),
                new TextBlock
                {
                    Text = kind.Label(),
                    FontSize = isPrimary ? 12 : 11,
                    Foreground = new SolidColorBrush(Color.Parse(selected ? accent : TextSecondary)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var btn = new Button
        {
            Content = row,
            Background = selected
                ? new SolidColorBrush(Color.FromArgb(50, 90, 150, 255))
                : Avalonia.Media.Brushes.Transparent,
            BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(180, 90, 150, 255)) : null,
            BorderThickness = selected ? new Thickness(1.5) : new Thickness(0),
            Padding = new Thickness(8, 4),
            MinHeight = 30,
            CornerRadius = new CornerRadius(12)
        };
        ToolTip.SetTip(btn, kind.Label());
        return btn;
    }

    private void PositionSmartShapeLauncher(SmartShapeModel shape)
    {
        if (_smartShapeLauncherBar == null || _workspaceViewport == null)
            return;

        var bounds = SmartShapeGizmo.GetBounds(shape);
        var anchor = _canvas.TranslatePoint(
            new Point(bounds.Center.X, bounds.MaxY + 20),
            _workspaceViewport);

        var barW = Math.Max(160, _smartShapeLauncherBar.Bounds.Width);
        var barH = Math.Max(38, _smartShapeLauncherBar.Bounds.Height);

        double x = 12;
        double y = 48;
        if (anchor.HasValue)
        {
            x = anchor.Value.X - barW * 0.5;
            y = anchor.Value.Y;
        }

        var maxX = Math.Max(12, _workspaceViewport.Bounds.Width - barW - 12);
        var maxY = Math.Max(12, _workspaceViewport.Bounds.Height - barH - 12);
        x = Math.Clamp(x, 12, maxX);
        y = Math.Clamp(y, 12, maxY);
        _smartShapeLauncherBar.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
    }

    private static Border LauncherDivider() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(2, 0),
        Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button LauncherTextButton(string text, string tip)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 230, 230, 230)),
                VerticalAlignment = VerticalAlignment.Center
            },
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Button LauncherIconButton(string icon, string tip)
    {
        var btn = new Button
        {
            Content = FlossUi.Icon(icon, 16),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            MinWidth = 30,
            MinHeight = 30,
            CornerRadius = new CornerRadius(10)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private bool IsOverSmartShapeLauncher(Point viewportPos)
    {
        if (_smartShapeLauncherBar is { IsVisible: true } bar && bar.Bounds.Contains(viewportPos))
            return true;
        return _smartShapeOverflowMenu?.IsOpen == true;
    }
}
