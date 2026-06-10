using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Floss.App.Controls;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed class ColorSlidersDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    private StackPanel _colorSlidersPanel = null!;
    private HsvSliderRow _hsvHueRow = null!;
    private HsvSliderRow _hsvSatRow = null!;
    private HsvSliderRow _hsvValRow = null!;
    private Border _colorPreview = null!;
    private bool _updatingFromHsv;

    public ColorSlidersDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);
        Content = BuildColorSlidersSection();
    }

    public void Refresh() => RefreshColorSliders();

    private StackPanel BuildColorSlidersSection()
    {
        _colorSlidersPanel = new StackPanel { Spacing = 10, Margin = new Thickness(10, 6, 10, 10) };

        _hsvHueRow = new HsvSliderRow("H", "°", 0, 360, 0);
        _hsvSatRow = new HsvSliderRow("S", "%", 0, 100, 100);
        _hsvValRow = new HsvSliderRow("B", "%", 0, 100, 50);

        _hsvHueRow.ValueChanged += _ => UpdateColorFromHsv();
        _hsvSatRow.ValueChanged += _ => UpdateColorFromHsv();
        _hsvValRow.ValueChanged += _ => UpdateColorFromHsv();

        _colorSlidersPanel.Children.Add(_hsvHueRow);
        _colorSlidersPanel.Children.Add(_hsvSatRow);
        _colorSlidersPanel.Children.Add(_hsvValRow);

        _colorPreview = new Border
        {
            Height = 20,
            Margin = new Thickness(0, 4, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(_ps.Canvas.Brush.Color)
        };
        _colorSlidersPanel.Children.Add(_colorPreview);

        RefreshColorSliders();
        return _colorSlidersPanel;
    }

    internal StackPanel BuildEmbeddedSection()
    {
        var panel = BuildColorSlidersSection();
        return panel;
    }

    private void UpdateColorFromHsv()
    {
        if (_ps.Sync.SyncingColorSliders) return;
        var h = _hsvHueRow.Value;
        var s = _hsvSatRow.Value / 100;
        var v = _hsvValRow.Value / 100;
        var (rd, gd, bd) = ColorDockPanel.HsvToRgb(h, s, v);
        _updatingFromHsv = true;
        _ps.Color.SetColor(Color.FromRgb((byte)(rd * 255), (byte)(gd * 255), (byte)(bd * 255)));
        _updatingFromHsv = false;
    }

    private void RefreshColorSliders()
    {
        if (_colorSlidersPanel == null) return;

        _ps.Sync.SyncingColorSliders = true;
        try
        {
            var color = _ps.Canvas.Brush.Color;
            double h, s, v;

            if (_updatingFromHsv)
            {
                h = _hsvHueRow.Value;
                s = _hsvSatRow.Value / 100;
                v = _hsvValRow.Value / 100;
            }
            else
            {
                (h, s, v) = ColorDockPanel.RgbToHsv(color.R / 255.0, color.G / 255.0, color.B / 255.0);
                _hsvHueRow.Value = h;
                _hsvSatRow.Value = s * 100;
                _hsvValRow.Value = v * 100;
            }

            _colorPreview.Background = new SolidColorBrush(color);
            UpdateHsvGradients(h, s, v);
        }
        finally
        {
            _ps.Sync.SyncingColorSliders = false;
        }
    }

    private void UpdateHsvGradients(double h, double s, double v)
    {
        _hsvHueRow.SetTrackBackground(BuildHueGradient());

        var (r0, g0, b0) = ColorDockPanel.HsvToRgb(h, 0, v);
        var (r1, g1, b1) = ColorDockPanel.HsvToRgb(h, 1, v);
        _hsvSatRow.SetTrackBackground(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb((byte)(r0 * 255), (byte)(g0 * 255), (byte)(b0 * 255)), 0),
                new GradientStop(Color.FromRgb((byte)(r1 * 255), (byte)(g1 * 255), (byte)(b1 * 255)), 1)
            }
        });

        var (rv, gv, bv) = ColorDockPanel.HsvToRgb(h, s, 1);
        _hsvValRow.SetTrackBackground(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Color.FromRgb((byte)(rv * 255), (byte)(gv * 255), (byte)(bv * 255)), 1)
            }
        });
    }

    private static LinearGradientBrush BuildHueGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        for (var i = 0; i <= 6; i++)
        {
            var hue = i * 60;
            var (r, g, b) = ColorDockPanel.HsvToRgb(hue, 1, 1);
            brush.GradientStops.Add(new GradientStop(
                Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)),
                i / 6.0));
        }
        return brush;
    }
}
