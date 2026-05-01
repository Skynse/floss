using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App;

public sealed class AngleDynamicsPopupWindow : Window
{
    private const string Bg1 = "#13151a";
    private const string Bg2 = "#1a1c22";
    private const string Stroke = "#2b303b";
    private const string TextPrimary = "#d7dde8";
    private const string TextMuted = "#6f7888";

    private readonly ComboBox _sourceCombo;
    private readonly Slider _randomSlider;
    private readonly TextBlock _randomVal;

    // Callbacks to update the preset
    private readonly Action<AngleSource> _onSourceChanged;
    private readonly Action<float> _onRandomChanged;

    public AngleDynamicsPopupWindow(
        AngleSource currentSource,
        float currentRandom,
        Action<AngleSource> onSourceChanged,
        Action<float> onRandomChanged)
    {
        _onSourceChanged = onSourceChanged;
        _onRandomChanged = onRandomChanged;

        Title = "Angle Dynamics";
        Width = 260;
        Height = 320;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 1. Random Jitter Section
        var randomLbl = new TextBlock { Text = "Random Jitter", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextPrimary)), VerticalAlignment = VerticalAlignment.Center };
        _randomVal = new TextBlock { Text = $"{currentRandom * 100:0}%", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextMuted)), VerticalAlignment = VerticalAlignment.Center, Width = 35, TextAlignment = TextAlignment.Right };

        _randomSlider = new Slider { Minimum = 0, Maximum = 1, Value = currentRandom, Height = 20 };
        _randomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _randomVal.Text = $"{_randomSlider.Value * 100:0}%";
                _onRandomChanged((float)_randomSlider.Value);
            }
        };

        var randomRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8) };
        DockPanel.SetDock(randomLbl, Dock.Left);
        DockPanel.SetDock(_randomVal, Dock.Right);
        randomRow.Children.Add(randomLbl);
        randomRow.Children.Add(_randomVal);
        randomRow.Children.Add(_randomSlider);

        // 2. Control Source Section
        var sourceLbl = new TextBlock { Text = "Control Source", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextPrimary)), Margin = new Thickness(0, 0, 0, 4) };
        _sourceCombo = new ComboBox
        {
            ItemsSource = new[] { "None", "Direction of line", "Direction of pen (Twist)", "Direction of pen (Tilt)" },
            SelectedIndex = (int)currentSource,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11,
            Height = 24
        };
        _sourceCombo.SelectionChanged += (_, _) =>
        {
            if (_sourceCombo.SelectedIndex >= 0)
                _onSourceChanged((AngleSource)_sourceCombo.SelectedIndex);
        };

        // 3. Response Curve Placeholder (Inject your existing curve editor here)
        var curveBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Height = 120,
            Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock
            {
                Text = "[ Curve Editor Here ]",
                Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var layout = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                randomRow,
                new Border { Height = 1, Background = new SolidColorBrush(Color.Parse(Stroke)), Margin = new Thickness(0, 8) },
                sourceLbl,
                _sourceCombo,
                curveBox
            }
        };

        Content = layout;
    }

    public void SyncState(AngleSource source, float random)
    {
        _sourceCombo.SelectedIndex = (int)source;
        _randomSlider.Value = random;
        _randomVal.Text = $"{random * 100:0}%";
    }
}
