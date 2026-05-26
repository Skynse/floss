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

    // HSV controls
    private Slider _hsvHueSlider = null!;
    private Slider _hsvSatSlider = null!;
    private Slider _hsvValSlider = null!;

    private Border _colorPreview = null!;
    private bool _syncingColorSliders;
    private bool _updatingFromHsv;

    private StackPanel BuildColorSlidersSection()
    {
        _colorSlidersPanel = new StackPanel { Spacing = 3, Margin = new Thickness(8, 2, 8, 4) };

        _hsvHueSlider = CreateColorSlider(0, 360);
        _hsvSatSlider = CreateColorSlider(0, 100);
        _hsvValSlider = CreateColorSlider(0, 100);
        _colorSlidersPanel.Children.Add(_hsvHueSlider);
        _colorSlidersPanel.Children.Add(_hsvSatSlider);
        _colorSlidersPanel.Children.Add(_hsvValSlider);

        _colorPreview = new Border
        {
            Height = 16,
            Margin = new Thickness(0, 2, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(_canvas?.Brush.Color ?? Colors.Black)
        };
        _colorSlidersPanel.Children.Add(_colorPreview);

        WireColorSlider(_hsvHueSlider, () => UpdateColorFromHsv());
        WireColorSlider(_hsvSatSlider, () => UpdateColorFromHsv());
        WireColorSlider(_hsvValSlider, () => UpdateColorFromHsv());

        RefreshColorSliders();
        return _colorSlidersPanel;
    }

    private static Slider CreateColorSlider(double min, double max)
    {
        return new Slider
        {
            Minimum = min,
            Maximum = max,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
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
