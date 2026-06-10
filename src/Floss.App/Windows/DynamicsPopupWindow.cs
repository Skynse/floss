using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Controls;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

public sealed class DynamicsPopupWindow : Window
{
    private sealed class InputChannel
    {
        public required CheckBox Toggle { get; init; }
        public required Border Card { get; init; }
        public required CurveGraph Graph { get; init; }
        public ScrubSlider? LengthSlider { get; init; }
        public Func<ParameterDynamics, bool> IsEnabled { get; init; } = _ => false;
        public Func<ParameterDynamics, bool, ParameterDynamics> SetEnabled { get; init; } = (d, _) => d;
        public Func<ParameterDynamics, float[]> GetCurve { get; init; } = _ => ParameterDynamics.IdentityCurve;
        public Func<ParameterDynamics, float[], ParameterDynamics> SetCurve { get; init; } = (d, _) => d;
    }

    private const double GraphHeight = 68;
    private const int GridColumns = 3;

    private readonly InputChannel[] _channels;
    private readonly ScrubSlider _minSlider = MkSlider(0, 1, 0, "Output at minimum input");
    private readonly ScrubSlider _maxSlider = MkSlider(0, 1, 1, "Output at maximum input");
    private readonly ScrubSlider _distanceLengthSlider = MkSlider(16, 10000, 1000, "Stroke distance in pixels");
    private readonly ScrubSlider _fadeLengthSlider = MkSlider(1, 2000, 120, "Fade length in dab count");

    private ParameterDynamics _current;
    private readonly Action<ParameterDynamics> _onChange;
    private bool _syncing;

    public DynamicsPopupWindow(string paramName, ParameterDynamics dynamics, Action<ParameterDynamics> onChange)
    {
        _current = dynamics;
        _onChange = onChange;

        _channels =
        [
            BuildChannel("Pen pressure", "0%", "100%",
                d => d.PressureEnabled, (d, v) => d with { PressureEnabled = v },
                d => d.CurveData, (d, c) => d with { CurveData = c }),
            BuildChannel("Velocity", "Pause", "Fast",
                d => d.VelocityEnabled, (d, v) => d with { VelocityEnabled = v },
                d => d.VelocityCurveData is { Length: >= 4 }
                    ? d.VelocityCurveData
                    : ParameterDynamics.VelocityCurveFromStrength(d.VelocityStrength),
                (d, c) => d with { VelocityCurveData = c }),
            BuildChannel("Tilt", "Horizontal", "Vertical",
                d => d.TiltEnabled, (d, v) => d with { TiltEnabled = v },
                d => d.TiltCurveData, (d, c) => d with { TiltCurveData = c }),
            BuildChannel("Random", "0%", "100%",
                d => d.RandomEnabled, (d, v) => d with { RandomEnabled = v },
                d => d.RandomCurveData, (d, c) => d with { RandomCurveData = c }),
            BuildChannel("Distance", "Start", "End",
                d => d.DistanceEnabled, (d, v) => d with { DistanceEnabled = v },
                d => d.DistanceCurveData, (d, c) => d with { DistanceCurveData = c },
                _distanceLengthSlider, "Len px"),
            BuildChannel("Fade", "Start", "End",
                d => d.FadeEnabled, (d, v) => d with { FadeEnabled = v },
                d => d.FadeCurveData, (d, c) => d with { FadeCurveData = c },
                _fadeLengthSlider, "Len dabs")
        ];

        Width = 540;
        Height = 420;
        MinWidth = 480;
        MinHeight = 380;
        CanResize = true;
        Title = $"{paramName} — Dynamics";
        WindowStartupLocation = WindowStartupLocation.Manual;

        CustomWindowChrome.ConfigurePopup(this);
        Content = CustomWindowChrome.Wrap(this, Title, BuildContent());
        SyncFromDynamics(dynamics);
        WireEvents();
    }

    private InputChannel BuildChannel(
        string label,
        string leftAxis,
        string rightAxis,
        Func<ParameterDynamics, bool> isEnabled,
        Func<ParameterDynamics, bool, ParameterDynamics> setEnabled,
        Func<ParameterDynamics, float[]> getCurve,
        Func<ParameterDynamics, float[], ParameterDynamics> setCurve,
        ScrubSlider? lengthSlider = null,
        string? lengthLabel = null)
    {
        var toggle = new CheckBox
        {
            Content = label,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var graph = new CurveGraph
        {
            Height = GraphHeight,
            MinHeight = GraphHeight,
            LeftAxisLabel = leftAxis,
            RightAxisLabel = rightAxis
        };

        var cardChildren = new StackPanel { Spacing = 4 };
        cardChildren.Children.Add(toggle);
        cardChildren.Children.Add(graph);

        if (lengthSlider != null && lengthLabel != null)
            cardChildren.Children.Add(CompactLengthRow(lengthLabel, lengthSlider));

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(3),
            Child = cardChildren
        };

        return new InputChannel
        {
            Toggle = toggle,
            Card = card,
            Graph = graph,
            LengthSlider = lengthSlider,
            IsEnabled = isEnabled,
            SetEnabled = setEnabled,
            GetCurve = getCurve,
            SetCurve = setCurve
        };
    }

    private Control BuildContent()
    {
        var outputRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var outputLabel = new TextBlock
        {
            Text = "Output range",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        DockPanel.SetDock(outputLabel, Dock.Left);
        outputRow.Children.Add(outputLabel);
        outputRow.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                CompactRange("Min", _minSlider),
                CompactRange("Max", _maxSlider)
            }
        });

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        for (var c = 0; c < GridColumns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var i = 0; i < _channels.Length; i++)
        {
            Grid.SetRow(_channels[i].Card, i / GridColumns);
            Grid.SetColumn(_channels[i].Card, i % GridColumns);
            grid.Children.Add(_channels[i].Card);
        }

        return new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 12),
            Spacing = 0,
            Children =
            {
                outputRow,
                new TextBlock
                {
                    Text = "Input curves — check to apply",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                    Margin = new Thickness(0, 0, 0, 6)
                },
                grid
            }
        };
    }

    private void WireEvents()
    {
        foreach (var channel in _channels)
        {
            channel.Graph.CurveChanged += (_, a) => Commit(d => channel.SetCurve(d, a.CurvePoints));
            channel.Toggle.IsCheckedChanged += (_, _) =>
            {
                if (_syncing) return;
                Commit(d => channel.SetEnabled(d, channel.Toggle.IsChecked == true));
            };
        }

        WireSlider(_minSlider, v => Commit(d => d with { Min = (float)v }));
        WireSlider(_maxSlider, v => Commit(d => d with { Max = (float)v }));
        WireSlider(_distanceLengthSlider, v => Commit(d => d with { DistanceLength = (float)v }));
        WireSlider(_fadeLengthSlider, v => Commit(d => d with { FadeLength = (float)v }));
    }

    private void Commit(Func<ParameterDynamics, ParameterDynamics> update)
    {
        if (_syncing) return;
        _current = update(_current);
        SyncChannelStates();
        _onChange(_current);
    }

    public void SyncFromDynamics(ParameterDynamics d)
    {
        _syncing = true;
        _current = d;

        foreach (var channel in _channels)
            channel.Graph.CurvePoints = ParameterDynamics.NormalizeCurveDataForEditor(channel.GetCurve(d));

        _minSlider.Value = Math.Clamp(d.Min, 0, 1);
        _maxSlider.Value = Math.Clamp(d.Max, 0, 1);
        _distanceLengthSlider.Value = Math.Clamp(d.DistanceLength, _distanceLengthSlider.Minimum, _distanceLengthSlider.Maximum);
        _fadeLengthSlider.Value = Math.Clamp(d.FadeLength, _fadeLengthSlider.Minimum, _fadeLengthSlider.Maximum);

        _syncing = false;
        SyncChannelStates();
    }

    private void SyncChannelStates()
    {
        foreach (var channel in _channels)
        {
            var enabled = channel.IsEnabled(_current);
            _syncing = true;
            channel.Toggle.IsChecked = enabled;
            _syncing = false;

            channel.Graph.IsEditingEnabled = enabled;
            channel.Card.BorderBrush = new SolidColorBrush(Color.Parse(enabled ? Accent : Stroke));
            channel.Card.Opacity = enabled ? 1.0 : 0.82;

            if (channel.LengthSlider != null)
                channel.LengthSlider.IsEnabled = enabled;
        }
    }

    private static Control CompactRange(string label, ScrubSlider slider)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(28)),
                new ColumnDefinition(new GridLength(120)),
                new ColumnDefinition(new GridLength(36))
            },
            VerticalAlignment = VerticalAlignment.Center
        };

        var val = new TextBlock
        {
            Text = $"{slider.Value * 100:0}%",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                val.Text = $"{slider.Value * 100:0}%";
        };

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(val, 2);
        row.Children.Add(slider);
        row.Children.Add(val);
        return row;
    }

    private static Control CompactLengthRow(string label, ScrubSlider slider)
    {
        var val = new TextBlock
        {
            Text = $"{slider.Value:0}",
            FontSize = 9,
            Width = 36,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                val.Text = $"{slider.Value:0}";
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(52)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(36))
            }
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(val, 2);
        row.Children.Add(slider);
        row.Children.Add(val);
        return row;
    }

    private static ScrubSlider MkSlider(double min, double max, double value, string tip)
        => ScrubSliderFactory.Create(min, max, value, tip);

    private static void WireSlider(ScrubSlider slider, Action<double> onChange)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !slider.IsScrubbing)
                onChange(slider.Value);
        };
        slider.ScrubCompleted += (_, v) => onChange(v);
    }
}
