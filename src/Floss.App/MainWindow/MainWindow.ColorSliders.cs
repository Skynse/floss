using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App;

public partial class MainWindow
{
    private StackPanel _colorSlidersPanel = null!;
    private TabControl _colorSpaceTabs = null!;

    // HSV controls
    private Slider _hsvHueSlider = null!;
    private Slider _hsvSatSlider = null!;
    private Slider _hsvValSlider = null!;

    // RGB controls  
    private Slider _rgbRSlider = null!;
    private Slider _rgbGSlider = null!;
    private Slider _rgbBSlider = null!;

    // Preview
    private Border _colorPreview = null!;
    private bool _syncingColorSliders;

    private StackPanel BuildColorSlidersSection()
    {
        _colorSlidersPanel = new StackPanel { Spacing = 6, Margin = new Thickness(10, 6, 10, 10) };

        _colorSpaceTabs = new TabControl
        {
            FontSize = 11,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4)
        };

        // HSV Tab
        var hsvPanel = new StackPanel { Spacing = 4 };
        _hsvHueSlider = CreateColorSlider("H", 0, 360, "°", Colors.Red, Colors.Red);
        _hsvSatSlider = CreateColorSlider("S", 0, 100, "%", Colors.Gray, Colors.Red);
        _hsvValSlider = CreateColorSlider("V", 0, 100, "%", Colors.Black, Colors.White);
        hsvPanel.Children.Add(_hsvHueSlider);
        hsvPanel.Children.Add(_hsvSatSlider);
        hsvPanel.Children.Add(_hsvValSlider);
        _colorSpaceTabs.Items.Add(new TabItem { Header = "HSV", Content = hsvPanel });

        // RGB Tab
        var rgbPanel = new StackPanel { Spacing = 4 };
        _rgbRSlider = CreateColorSlider("R", 0, 255, "", Colors.Black, Colors.Red);
        _rgbGSlider = CreateColorSlider("G", 0, 255, "", Colors.Black, Colors.Green);
        _rgbBSlider = CreateColorSlider("B", 0, 255, "", Colors.Black, Colors.Blue);
        rgbPanel.Children.Add(_rgbRSlider);
        rgbPanel.Children.Add(_rgbGSlider);
        rgbPanel.Children.Add(_rgbBSlider);
        _colorSpaceTabs.Items.Add(new TabItem { Header = "RGB", Content = rgbPanel });

        // Color Preview
        _colorPreview = new Border
        {
            Height = 28,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(_canvas?.Brush.Color ?? Colors.Black)
        };

        _colorSlidersPanel.Children.Add(_colorSpaceTabs);
        _colorSlidersPanel.Children.Add(_colorPreview);

        // Wire events
        WireColorSlider(_hsvHueSlider, () => UpdateColorFromHsv());
        WireColorSlider(_hsvSatSlider, () => UpdateColorFromHsv());
        WireColorSlider(_hsvValSlider, () => UpdateColorFromHsv());
        WireColorSlider(_rgbRSlider, () => UpdateColorFromRgb());
        WireColorSlider(_rgbGSlider, () => UpdateColorFromRgb());
        WireColorSlider(_rgbBSlider, () => UpdateColorFromRgb());

        RefreshColorSliders();
        return _colorSlidersPanel;
    }

    private static Slider CreateColorSlider(string label, double min, double max, string fmt, Color startColor, Color endColor)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Create gradient background
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(startColor, 0),
                new GradientStop(endColor, 1)
            }
        };
        slider.Background = gradient;

        return slider;
    }

    private static void WireColorSlider(Slider slider, Action onChange)
    {
        slider.AddHandler(Slider.PointerReleasedEvent, (_, _) => onChange(), handledEventsToo: true);
        slider.LostFocus += (_, _) => onChange();
    }

    private void UpdateColorFromHsv()
    {
        if (_syncingColorSliders) return;
        var h = _hsvHueSlider.Value;
        var s = _hsvSatSlider.Value / 100;
        var v = _hsvValSlider.Value / 100;
        var (rd, gd, bd) = HsvToRgb(h, s, v);
        SetColor(Color.FromRgb((byte)(rd * 255), (byte)(gd * 255), (byte)(bd * 255)));
    }

    private void UpdateColorFromRgb()
    {
        if (_syncingColorSliders) return;
        var r = (byte)Math.Clamp(_rgbRSlider.Value, 0, 255);
        var g = (byte)Math.Clamp(_rgbGSlider.Value, 0, 255);
        var b = (byte)Math.Clamp(_rgbBSlider.Value, 0, 255);
        SetColor(Color.FromRgb(r, g, b));
    }

    private void RefreshColorSliders()
    {
        if (_colorSlidersPanel == null || _canvas == null) return;

        _syncingColorSliders = true;
        try
        {
            var color = _canvas.Brush.Color;
            var (h, s, v) = RgbToHsv(color.R / 255.0, color.G / 255.0, color.B / 255.0);

            _hsvHueSlider.Value = h;
            _hsvSatSlider.Value = s * 100;
            _hsvValSlider.Value = v * 100;

            _rgbRSlider.Value = color.R;
            _rgbGSlider.Value = color.G;
            _rgbBSlider.Value = color.B;

            _colorPreview.Background = new SolidColorBrush(color);
        }
        finally
        {
            _syncingColorSliders = false;
        }
    }
}
