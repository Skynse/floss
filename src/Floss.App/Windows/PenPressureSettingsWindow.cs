using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Windows;

using static Floss.App.AppColors;

public sealed class PenPressureSettingsWindow : Window
{
    private readonly PenPressureSettings _settings;
    private readonly PenPressureSettings _working;
    private readonly CurveGraph _curveGraph;
    private readonly CheckBox _enableToggle;

    public PenPressureSettingsWindow()
    {
        _settings = App.PenPressure;
        _working = _settings.Clone();

        Width = 480;
        Height = 420;
        CanResize = true;
        MinWidth = 380;
        MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        Title = "Pen Pressure Settings";
        ShowInTaskbar = false;

        // ── Enable toggle ────────────────────────────────────────────────
        _enableToggle = new CheckBox
        {
            Content = "Enable software pressure curve",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            IsChecked = _working.Enabled,
            Margin = new Thickness(12, 10, 12, 4)
        };
        _enableToggle.IsCheckedChanged += (_, _) =>
        {
            _working.Enabled = _enableToggle.IsChecked == true;
            _curveGraph!.IsEditingEnabled = _working.Enabled;
        };

        // ── Curve graph ───────────────────────────────────────────────────
        _curveGraph = new CurveGraph
        {
            Height = 240,
            MinHeight = 200,
            LeftAxisLabel = "0%",
            RightAxisLabel = "100%",
            Margin = new Thickness(12, 0, 12, 0),
            CurvePoints = (float[])_working.CurvePoints.Clone(),
            IsEditingEnabled = _working.Enabled
        };
        _curveGraph.CurveChanged += (_, args) =>
            _working.CurvePoints = args.CurvePoints;

        // ── Buttons ───────────────────────────────────────────────────────
        var resetBtn = new Button
        {
            Content = "Reset to Linear",
            FontSize = 10,
            Padding = new Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary))
        };
        resetBtn.Click += (_, _) =>
        {
            _working.CurvePoints = [0f, 0f, 1f, 1f];
            _curveGraph.CurvePoints = [0f, 0f, 1f, 1f];
        };

        var okBtn = new Button
        {
            Content = "OK",
            FontSize = 10,
            Padding = new Thickness(18, 6),
            Background = new SolidColorBrush(Color.Parse(AccentSoft)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary))
        };
        okBtn.Click += (_, _) =>
        {
            _settings.Enabled = _working.Enabled;
            _settings.CurvePoints = _working.CurvePoints;
            _settings.Save(AppPaths.PenPressureSettingsPath);
            Close();
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            FontSize = 10,
            Padding = new Thickness(18, 6),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary))
        };
        cancelBtn.Click += (_, _) => Close();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 12, 10),
            Children = { resetBtn, cancelBtn, okBtn }
        };

        // ── Help text ─────────────────────────────────────────────────────
        var helpLabel = new TextBlock
        {
            Text = "Drag points to adjust how physical pen pressure maps to brush input.\n" +
                   "Drag a point off the graph to remove it. Click near the curve to add a new point.",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 4, 12, 0)
        };

        // ── Layout ────────────────────────────────────────────────────────
        Content = new StackPanel
        {
            Children = { _enableToggle, helpLabel, _curveGraph, btnRow }
        };
    }
}
