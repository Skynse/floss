using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App;

using static Floss.App.AppColors;

public sealed class AngleDynamicsPopupWindow : Window
{
    private readonly ComboBox _sourceCombo;
    private readonly Slider _randomSlider;
    private readonly TextBlock _randomVal;

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
        Width = 340;
        Height = 200;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var sourceLbl = new TextBlock
        {
            Text = "Input source",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            Margin = new Thickness(0, 0, 0, 4)
        };

        _sourceCombo = new ComboBox
        {
            ItemsSource = new[] { "None", "Direction of line", "Pen tilt", "Pen twist" },
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

        var jitterLbl = new TextBlock
        {
            Text = "Random jitter",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _randomVal = new TextBlock
        {
            Text = $"{currentRandom * 100:0}%",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 35,
            TextAlignment = TextAlignment.Right
        };

        _randomSlider = new Slider { Minimum = 0, Maximum = 1, Value = currentRandom, Height = 26, MinHeight = 22 };
        _randomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _randomVal.Text = $"{_randomSlider.Value * 100:0}%";
                _onRandomChanged((float)_randomSlider.Value);
            }
        };

        var jitterHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(jitterLbl, Dock.Left);
        DockPanel.SetDock(_randomVal, Dock.Right);
        jitterHeader.Children.Add(jitterLbl);
        jitterHeader.Children.Add(_randomVal);

        Content = new StackPanel
        {
            Margin = new Thickness(16, 14),
            Spacing = 8,
            Children =
            {
                sourceLbl,
                _sourceCombo,
                new Border { Height = 1, Background = new SolidColorBrush(Color.Parse(Stroke)), Margin = new Thickness(0, 6) },
                jitterHeader,
                _randomSlider
            }
        };
    }

    public void SyncState(AngleSource source, float random)
    {
        _sourceCombo.SelectedIndex = (int)source;
        _randomSlider.Value = random;
        _randomVal.Text = $"{random * 100:0}%";
    }
}
