using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{
    // ── Color section ─────────────────────────────────────────────────────────
    private int _colorViewMode; // 0=wheel, 1=hsv, 2=rgb
    private StackPanel? _colorContentArea;
    private Control? _wheelView;
    private Control? _hsvView;
    private Control? _rgbView;

    private StackPanel BuildColorSection()
    {
        // Color wheel view
        _colorPicker = new HsvColorPicker
        {
            Height = 188,
            Margin = new Thickness(16, 4, 16, 14)
        };
        _colorPicker.HsvChanged += OnPickerHsvChanged;

        _hexInput = new TextBox
        {
            Width = 116,
            Height = 28,
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
            Margin = new Thickness(16, 4, 16, 14),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            ItemWidth = SwatchSize + 2,
            ItemHeight = SwatchSize + 2
        };

        _wheelView = new StackPanel
        {
            Children = { _colorPicker, new Border { Margin = new Thickness(16, 0, 16, 14), Child = _hexInput } }
        };

        // HSV sliders view
        _hsvView = BuildColorSlidersSection();

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
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16, 4, 16, 14) };
        _rgbRSlider = CreateRgbSlider(0, 255, Colors.Black, Colors.Red);
        _rgbGSlider = CreateRgbSlider(0, 255, Colors.Black, Colors.Green);
        _rgbBSlider = CreateRgbSlider(0, 255, Colors.Black, Colors.Blue);
        panel.Children.Add(_rgbRSlider);
        panel.Children.Add(_rgbGSlider);
        panel.Children.Add(_rgbBSlider);

        var preview = new Border
        {
            Height = 24, Margin = new Thickness(0, 4, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Colors.Black)
        };
        panel.Children.Add(preview);

        WireRgbSliders(_rgbRSlider, _rgbGSlider, _rgbBSlider, preview);
        return panel;
    }

    private static Slider CreateRgbSlider(double min, double max, Color start, Color end)
    {
        var s = new Slider { Minimum = min, Maximum = max, Height = 18, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        s.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) }
        };
        return s;
    }

    private void WireRgbSliders(Slider r, Slider g, Slider b, Border preview)
    {
        void update() {
            if (_syncingColorSliders) return;
            var cr = Color.FromRgb((byte)Math.Clamp(r.Value, 0, 255), (byte)Math.Clamp(g.Value, 0, 255), (byte)Math.Clamp(b.Value, 0, 255));
            preview.Background = new SolidColorBrush(cr);
            SetColor(cr, syncPicker: false);
            SyncPickerFromColor(cr);
        }
        r.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) update(); };
        g.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) update(); };
        b.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) update(); };
    }

    // ── Color picker ──────────────────────────────────────────────────────────
    private void OnPickerHsvChanged(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SetColor(color, syncPicker: false);
    }

    private void SyncPickerFromColor(Color color)
    {
        var (h, s, v) = RgbToHsv(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        _colorPicker.SetHsv(h, s, v);
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void TryApplyHexColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
    }

    // ── Color application ─────────────────────────────────────────────────────
    private void SetColor(Color color, bool syncPicker = true)
    {
        _colorWell.Background = new SolidColorBrush(color);
        _canvas.SetPaintColor(color);
        _strokePreview.Brush = _canvas.Brush;
        _strokePreview.InvalidateBitmap();
        _toolPropsWindow?.UpdatePreviewColor(color);
        if (syncPicker) SyncPickerFromColor(color);
        RefreshColorSliders();
    }

    private void CycleColor()
    {
        _swatchIndex = (_swatchIndex + 1) % _swatches.Length;
        SetColor(_swatches[_swatchIndex]);
    }

    // ── Swatch panel ──────────────────────────────────────────────────────────
    private const int SwatchColumns = 10;
    private const int SwatchSize = 18;

    private void BuildSwatches()
    {
        _swatchPanel.Children.Clear();
        for (var i = 0; i < _swatches.Length; i++)
        {
            var idx = i;
            var color = _swatches[i];
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
            btn.Click += (_, _) => { _swatchIndex = idx; SetColor(color); };
            _swatchPanel.Children.Add(btn);
        }
    }

    // ── HSV ↔ RGB helpers ─────────────────────────────────────────────────────
    private static (double h, double s, double v) RgbToHsv(double r, double g, double b)
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

    private static (double r, double g, double b) HsvToRgb(double h, double s, double v)
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
