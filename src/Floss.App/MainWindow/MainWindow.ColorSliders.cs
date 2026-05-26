using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App;

using static Floss.App.Config.AppColors;

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
    private bool _updatingFromHsv;

    private StackPanel BuildColorSlidersSection()
    {
        _colorSlidersPanel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 8, 6) };

        _colorSpaceTabs = new TabControl
        {
            FontSize = 10,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4)
        };

        // HSV Tab — gradients are updated dynamically in RefreshColorSliders
        var hsvPanel = new StackPanel { Spacing = 4 };
        _hsvHueSlider = CreateColorSlider("H", 0, 360);
        _hsvSatSlider = CreateColorSlider("S", 0, 100);
        _hsvValSlider = CreateColorSlider("V", 0, 100);
        hsvPanel.Children.Add(_hsvHueSlider);
        hsvPanel.Children.Add(_hsvSatSlider);
        hsvPanel.Children.Add(_hsvValSlider);
        _colorSpaceTabs.Items.Add(new TabItem { Header = "HSV", Content = hsvPanel });

        // RGB Tab — static gradients, never change
        var rgbPanel = new StackPanel { Spacing = 4 };
        _rgbRSlider = CreateColorSlider("R", 0, 255, Colors.Black, Colors.Red);
        _rgbGSlider = CreateColorSlider("G", 0, 255, Colors.Black, Colors.Green);
        _rgbBSlider = CreateColorSlider("B", 0, 255, Colors.Black, Colors.Blue);
        rgbPanel.Children.Add(_rgbRSlider);
        rgbPanel.Children.Add(_rgbGSlider);
        rgbPanel.Children.Add(_rgbBSlider);
        _colorSpaceTabs.Items.Add(new TabItem { Header = "RGB", Content = rgbPanel });

        // Color Preview
        _colorPreview = new Border
        {
            Height = 24,
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

    private static Slider CreateColorSlider(string label, double min, double max)
    {
        return new Slider
        {
            Minimum = min,
            Maximum = max,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Slider CreateColorSlider(string label, double min, double max, Color startColor, Color endColor)
    {
        var slider = CreateColorSlider(label, min, max);
        slider.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(startColor, 0), new GradientStop(endColor, 1) }
        };
        return slider;
    }

    private static void WireColorSlider(Slider slider, Action onChange)
    {
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                onChange();
        };
    }

    private void UpdateColorFromHsv()
    {
        if (_syncingColorSliders) return;
        var h = _hsvHueSlider.Value;
        var s = _hsvSatSlider.Value / 100;
        var v = _hsvValSlider.Value / 100;
        var (rd, gd, bd) = HsvToRgb(h, s, v);
        _updatingFromHsv = true;
        SetColor(Color.FromRgb((byte)(rd * 255), (byte)(gd * 255), (byte)(bd * 255)));
        _updatingFromHsv = false;
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
            double h, s, v;

            if (_updatingFromHsv)
            {
                // HSV sliders drove this update — read values directly to avoid
                // the RGB→HSV roundtrip clobbering H/S at low value or saturation.
                h = _hsvHueSlider.Value;
                s = _hsvSatSlider.Value / 100;
                v = _hsvValSlider.Value / 100;
            }
            else
            {
                (h, s, v) = RgbToHsv(color.R / 255.0, color.G / 255.0, color.B / 255.0);
                _hsvHueSlider.Value = h;
                _hsvSatSlider.Value = s * 100;
                _hsvValSlider.Value = v * 100;
            }

            _rgbRSlider.Value = color.R;
            _rgbGSlider.Value = color.G;
            _rgbBSlider.Value = color.B;

            _colorPreview.Background = new SolidColorBrush(color);
            UpdateHsvGradients(h, s, v);
        }
        finally
        {
            _syncingColorSliders = false;
        }
    }

    private void UpdateHsvGradients(double h, double s, double v)
    {
        // Hue: full rainbow 0° → 360°
        _hsvHueSlider.Background = BuildHueGradient();

        // Saturation: gray (same hue, s=0) → full color (same hue, s=1)
        var (r0, g0, b0) = HsvToRgb(h, 0, v);
        var (r1, g1, b1) = HsvToRgb(h, 1, v);
        _hsvSatSlider.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb((byte)(r0 * 255), (byte)(g0 * 255), (byte)(b0 * 255)), 0),
                new GradientStop(Color.FromRgb((byte)(r1 * 255), (byte)(g1 * 255), (byte)(b1 * 255)), 1)
            }
        };

        // Value: black (v=0) → full color (v=1, same h/s)
        var (rv, gv, bv) = HsvToRgb(h, s, 1);
        _hsvValSlider.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Color.FromRgb((byte)(rv * 255), (byte)(gv * 255), (byte)(bv * 255)), 1)
            }
        };
    }

    private static LinearGradientBrush BuildHueGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        for (int i = 0; i <= 6; i++)
        {
            var hue = i * 60;
            var (r, g, b) = HsvToRgb(hue, 1, 1);
            brush.GradientStops.Add(new GradientStop(
                Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)),
                i / 6.0));
        }
        return brush;
    }
}
