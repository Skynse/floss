using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Controls;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

public sealed partial class ColorDockPanel : ContentControl
{
    private readonly PanelSession _ps;

    private int _colorViewMode;
    private StackPanel? _colorContentArea;
    private Control? _wheelView;
    private Control? _hsvView;
    private Control? _rgbView;
    private HsvColorPicker _colorPicker = null!;
    private TextBox _hexInput = null!;
    private ScrubSlider _rgbRSlider = null!;
    private ScrubSlider _rgbGSlider = null!;
    private ScrubSlider _rgbBSlider = null!;
    private WrapPanel _swatchPanel = null!;
    private int _swatchIndex;

    public ColorDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);
        Content = BuildColorSection();
        BuildSwatches();
    }

    public void RefreshSliders() => _ps.Color.RefreshColorSliders();

    public void CycleColor()
    {
        _swatchIndex = (_swatchIndex + 1) % _ps.Color.Swatches.Length;
        _ps.Color.SetColor(_ps.Color.Swatches[_swatchIndex]);
    }

    internal static (double h, double s, double v) RgbToHsv(double r, double g, double b) => RgbToHsvImpl(r, g, b);
    internal static (double r, double g, double b) HsvToRgb(double h, double s, double v) => HsvToRgbImpl(h, s, v);

    private StackPanel BuildEmbeddedHsvView()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(10, 6, 10, 10) };
        var hue = new HsvSliderRow("H", "°", 0, 360, 0);
        var sat = new HsvSliderRow("S", "%", 0, 100, 100);
        var val = new HsvSliderRow("B", "%", 0, 100, 50);
        void update()
        {
            if (_ps.Sync.SyncingColorSliders) return;
            var (rd, gd, bd) = HsvToRgb(hue.Value, sat.Value / 100, val.Value / 100);
            _ps.Color.SetColor(Color.FromRgb((byte)(rd * 255), (byte)(gd * 255), (byte)(bd * 255)), syncPicker: false);
        }
        hue.ValueChanged += _ => update();
        sat.ValueChanged += _ => update();
        val.ValueChanged += _ => update();
        panel.Children.Add(hue);
        panel.Children.Add(sat);
        panel.Children.Add(val);
        return panel;
    }

    private StackPanel BuildColorSection()
    {
        // Color wheel view
        _colorPicker = new HsvColorPicker
        {
            Height = 130,
            Margin = new Thickness(4, 2, 4, 4)
        };
        _colorPicker.HsvChanged += OnPickerHsvChanged;

        _hexInput = new TextBox
        {
            Width = 80,
            Height = 24,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Background = new SolidColorBrush(Color.Parse(Bg3)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        _hexInput.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Return) TryApplyHexColor(_hexInput.Text ?? ""); };
        _hexInput.LostFocus += (_, _) => TryApplyHexColor(_hexInput.Text ?? "");

        _swatchPanel = new WrapPanel
        {
            Margin = new Thickness(8, 2, 8, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            ItemWidth = SwatchSize + 2,
            ItemHeight = SwatchSize + 2
        };

        _wheelView = new StackPanel
        {
            Children = { _colorPicker, new Border { Margin = new Thickness(8, 0, 8, 8), Child = _hexInput } }
        };

        // HSV sliders view
        _hsvView = BuildEmbeddedHsvView();

        // RGB sliders view
        _rgbView = BuildRgbSlidersView();

        // Content area that switches
        _colorContentArea = new StackPanel();
        ApplyColorViewMode();

        // Kebab menu button
        var menuBtn = new Button
        {
            Content = Icons.Make(Icons.DotsVertical, 14, new SolidColorBrush(Color.Parse(TextMuted))),
            Width = 32, Height = 28,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0)
        };
        menuBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var wheelItem = new MenuItem { Header = "Color Wheel", IsChecked = _colorViewMode == 0 };
            var hsvItem = new MenuItem { Header = "HSV Sliders", IsChecked = _colorViewMode == 1 };
            var rgbItem = new MenuItem { Header = "RGB Sliders", IsChecked = _colorViewMode == 2 };
            wheelItem.Click += (_, _) => { _colorViewMode = 0; ApplyColorViewMode(); };
            hsvItem.Click += (_, _) => { _colorViewMode = 1; ApplyColorViewMode(); };
            rgbItem.Click += (_, _) => { _colorViewMode = 2; ApplyColorViewMode(); };
            menu.ItemsSource = new object[] { wheelItem, hsvItem, rgbItem };
            menu.Open(menuBtn);
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetColumn(menuBtn, 1);
        header.Children.Add(menuBtn);

        return new StackPanel
        {
            Children =
            {
                header,
                _colorContentArea,
                _swatchPanel
            }
        };
    }

    private void ApplyColorViewMode()
    {
        if (_colorContentArea == null) return;
        _colorContentArea.Children.Clear();
        _colorContentArea.Children.Add((_colorViewMode switch
        {
            0 => _wheelView,
            1 => _hsvView,
            2 => _rgbView,
            _ => _wheelView
        })!);
    }

    private StackPanel BuildRgbSlidersView()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(10, 6, 10, 10) };
        _rgbRSlider = CreateRgbSlider(0, 255, Colors.Black, Colors.Red);
        _rgbGSlider = CreateRgbSlider(0, 255, Colors.Black, Colors.Green);
        _rgbBSlider = CreateRgbSlider(0, 255, Colors.Black, Colors.Blue);
        panel.Children.Add(_rgbRSlider);
        panel.Children.Add(_rgbGSlider);
        panel.Children.Add(_rgbBSlider);
        var preview = new Border
        {
            Height = 20, Margin = new Thickness(0, 2, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Colors.Black)
        };
        panel.Children.Add(preview);

        WireRgbSliders(_rgbRSlider, _rgbGSlider, _rgbBSlider, preview);
        return panel;
    }

    private static ScrubSlider CreateRgbSlider(double min, double max, Color start, Color end)
    {
        var s = ScrubSliderFactory.Create(min, max, 0);
        s.ShowValueFill = false;
        s.TrackBackground = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) }
        };
        return s;
    }

    private void WireRgbSliders(ScrubSlider r, ScrubSlider g, ScrubSlider b, Border preview)
    {
        void update() {
            if (_ps.Sync.SyncingColorSliders) return;
            var cr = Color.FromRgb((byte)Math.Clamp(r.Value, 0, 255), (byte)Math.Clamp(g.Value, 0, 255), (byte)Math.Clamp(b.Value, 0, 255));
            preview.Background = new SolidColorBrush(cr);
            _ps.Color.SetColor(cr, syncPicker: false);
            SyncPickerFromColor(cr);
        }
        r.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) update(); };
        g.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) update(); };
        b.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) update(); };
    }

    // ── Color picker ──────────────────────────────────────────────────────────
    private void OnPickerHsvChanged(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgbImpl(h, s, v);
        var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _ps.Color.SetColor(color, syncPicker: false);
    }

    public void SyncPickerFromColor(Color color)
    {
        if (_colorPicker == null)
            return;

        var (h, s, v) = RgbToHsvImpl(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        _colorPicker.SetHsv(h, s, v);
        if (_hexInput != null)
            _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void TryApplyHexColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            _ps.Color.SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
    }

    // ── Swatch panel ──────────────────────────────────────────────────────────
    private const int SwatchColumns = 12;
    private const int SwatchSize = 14;

    private void BuildSwatches()
    {
        _swatchPanel.Children.Clear();
        for (var i = 0; i < _ps.Color.Swatches.Length; i++)
        {
            var idx = i;
            var color = _ps.Color.Swatches[i];
            var btn = new Button
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(0, 0, 2, 2),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(0)
            };
            ToolTip.SetTip(btn, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            btn.Click += (_, _) => { _swatchIndex = idx; _ps.Color.SetColor(color); };
            _swatchPanel.Children.Add(btn);
        }
    }

    // ── HSV ↔ RGB helpers ─────────────────────────────────────────────────────
    private static (double h, double s, double v) RgbToHsvImpl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double r, double g, double b) HsvToRgbImpl(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        var hi = (int)(h / 60) % 6;
        var f = h / 60 - Math.Floor(h / 60);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        return hi switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
    }
}
