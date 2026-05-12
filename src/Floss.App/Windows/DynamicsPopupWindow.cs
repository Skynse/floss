using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App;

using static Floss.App.AppColors;

public sealed class DynamicsPopupWindow : Window
{
    private readonly CurveGraph _graph = new() { Height = 150 };
    private readonly Button _pressureToggle;
    private readonly Slider _minSlider = MkSlider(0, 1, 0, "Output at minimum pressure");
    private readonly Slider _maxSlider = MkSlider(0, 1, 1, "Output at maximum pressure");
    private readonly Button _velocityToggle;
    private readonly Slider _velStrSlider = MkSlider(0, 1, 0.3, "How much velocity reduces the parameter");

    private ParameterDynamics _current;
    private readonly Action<ParameterDynamics> _onChange;
    private bool _syncing;

    public DynamicsPopupWindow(string paramName, ParameterDynamics dynamics, Action<ParameterDynamics> onChange)
    {
        _current = dynamics;
        _onChange = onChange;

        _pressureToggle = MkToggle("Pressure");
        _velocityToggle = MkToggle("Velocity");

        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        Title = $"{paramName} — Dynamics";

        Content = BuildContent();
        SyncFromDynamics(dynamics);
        WireEvents();
    }

    private Control BuildContent()
    {
        var pressureSection = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                _pressureToggle,
                new Border
                {
                    Margin  = new Thickness(10, 4, 0, 0),
                    Child   = new StackPanel
                    {
                        Spacing = 0,
                        Children =
                        {
                            LabelSlider("Min", _minSlider),
                            LabelSlider("Max", _maxSlider)
                        }
                    }
                }
            }
        };

        var velocitySection = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                _velocityToggle,
                new Border
                {
                    Margin  = new Thickness(10, 4, 0, 0),
                    Child   = LabelSlider("Strength", _velStrSlider)
                }
            }
        };

        return new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 16),
            Spacing = 0,
            Children = { _graph, pressureSection, velocitySection }
        };
    }

    private void WireEvents()
    {
        _graph.CurveChanged += (_, a) => Commit(d => d with { CurveData = a.CurvePoints });

        _pressureToggle.Click += (_, _) => Commit(d => d with { PressureEnabled = !d.PressureEnabled });
        _velocityToggle.Click += (_, _) => Commit(d => d with { VelocityEnabled = !d.VelocityEnabled });

        WireSlider(_minSlider, v => Commit(d => d with { Min = (float)v }));
        WireSlider(_maxSlider, v => Commit(d => d with { Max = (float)v }));
        WireSlider(_velStrSlider, v => Commit(d => d with { VelocityStrength = (float)v }));
    }

    private void Commit(Func<ParameterDynamics, ParameterDynamics> update)
    {
        if (_syncing) return;
        _current = update(_current);
        SyncToggles();
        _onChange(_current);
    }

    public void SyncFromDynamics(ParameterDynamics d)
    {
        _syncing = true;
        _current = d;

        _graph.CurvePoints = d.CurveData;

        _minSlider.Value = Math.Clamp(d.Min, 0, 1);
        _maxSlider.Value = Math.Clamp(d.Max, 0, 1);
        _velStrSlider.Value = Math.Clamp(d.VelocityStrength, 0, 1);

        _syncing = false;
        SyncToggles();
    }

    private void SyncToggles()
    {
        SetActive(_pressureToggle, _current.PressureEnabled);
        SetActive(_velocityToggle, _current.VelocityEnabled);
    }

    private static void SetActive(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2));
        btn.BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : Stroke));
        btn.Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
    }

    private static Control LabelSlider(string label, Slider slider)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Width = 54,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var val = new TextBlock
        {
            Text = $"{slider.Value * 100:0}%",
            FontSize = 10,
            Width = 34,
            TextAlignment = Avalonia.Media.TextAlignment.Right,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) val.Text = $"{slider.Value * 100:0}%";
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(val);
        row.Children.Add(slider);
        return row;
    }

    private static Button MkToggle(string label) => new()
    {
        Content = label,
        Height = 24,
        Padding = new Thickness(8, 0),
        FontSize = 10,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(Bg2)),
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    private static Slider MkSlider(double min, double max, double value, string tip)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Height = 26,
            MinHeight = 22,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(s, tip);
        return s;
    }

    private static void WireSlider(Slider slider, Action<double> onChange)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) onChange(slider.Value);
        };
    }
}
