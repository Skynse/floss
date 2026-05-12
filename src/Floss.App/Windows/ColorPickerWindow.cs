using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Floss.App;

using static Floss.App.AppColors;

public sealed class ColorPickerWindow : Window
{
    private double _hue;        // 0-360
    private double _saturation; // 0-1
    private double _value;      // 0-1
    private readonly Action<Color> _onChange;
    private bool _syncing;

    // Controls
    private readonly Image _svImage;
    private readonly Image _hueImage;
    private readonly Border _previewBox;
    private readonly TextBox _hexBox;
    private readonly TextBox _rBox, _gBox, _bBox;
    private readonly TextBox _hBox, _sBox, _vBox;

    public ColorPickerWindow(Color initialColor, Action<Color> onChange)
    {
        _onChange = onChange;
        (_hue, _saturation, _value) = RgbToHsv(initialColor.R, initialColor.G, initialColor.B);

        Width = 420;
        Height = 480;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg0));
        Title = "Color";
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Make it behave like a dialog — clicking outside commits current color
        // but doesn't close automatically, user must click OK or Cancel.

        // SV Gradient
        _svImage = new Image { Width = 256, Height = 256, Stretch = Stretch.None };
        _svImage.PointerPressed += OnSvPressed;
        _svImage.PointerMoved += OnSvMoved;
        _svImage.PointerReleased += OnSvReleased;

        // Hue Slider
        _hueImage = new Image { Width = 20, Height = 256, Stretch = Stretch.None };
        _hueImage.PointerPressed += OnHuePressed;
        _hueImage.PointerMoved += OnHueMoved;
        _hueImage.PointerReleased += OnHueReleased;

        _previewBox = new Border
        {
            Width = 48,
            Height = 48,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Gray),
            Background = new SolidColorBrush(initialColor)
        };

        _hexBox = CreateInputBox("HEX", 6);
        _rBox = CreateInputBox("R", 3);
        _gBox = CreateInputBox("G", 3);
        _bBox = CreateInputBox("B", 3);
        _hBox = CreateInputBox("H", 3);
        _sBox = CreateInputBox("S", 3);
        _vBox = CreateInputBox("V", 3);

        // Wire input events
        _hexBox.LostFocus += (_, _) => ParseHex();
        _rBox.LostFocus += (_, _) => ParseRgb();
        _gBox.LostFocus += (_, _) => ParseRgb();
        _bBox.LostFocus += (_, _) => ParseRgb();
        _hBox.LostFocus += (_, _) => ParseHsv();
        _sBox.LostFocus += (_, _) => ParseHsv();
        _vBox.LostFocus += (_, _) => ParseHsv();

        Content = BuildLayout();
        UpdateImages();
        UpdateInputs();
    }

    private Control BuildLayout()
    {
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(16) };

        // Picker area: SV square + hue slider
        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pickerRow.Children.Add(_svImage);
        pickerRow.Children.Add(_hueImage);

        // Preview + inputs
        var rightPanel = new StackPanel { Spacing = 8, Width = 120 };
        rightPanel.Children.Add(_previewBox);
        rightPanel.Children.Add(_hexBox);

        var rgbRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        rgbRow.Children.Add(_rBox);
        rgbRow.Children.Add(_gBox);
        rgbRow.Children.Add(_bBox);
        rightPanel.Children.Add(rgbRow);

        var hsvRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        hsvRow.Children.Add(_hBox);
        hsvRow.Children.Add(_sBox);
        hsvRow.Children.Add(_vBox);
        rightPanel.Children.Add(hsvRow);

        var mainRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        mainRow.Children.Add(pickerRow);
        mainRow.Children.Add(rightPanel);

        root.Children.Add(mainRow);

        // OK / Cancel
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var okBtn = new Button { Content = "OK", Width = 60 };
        okBtn.Click += (_, _) =>
        {
            _onChange(ColorFromHsv(_hue, _saturation, _value));
            Close();
        };
        var cancelBtn = new Button { Content = "Cancel", Width = 60 };
        cancelBtn.Click += (_, _) => Close();
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        return root;
    }

    private static TextBox CreateInputBox(string label, int maxLength)
    {
        var box = new TextBox
        {
            Width = label == "HEX" ? 80 : 36,
            MaxLength = maxLength,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        return box;
    }

    // ── Interaction handlers ──────────────────────────────────────────────────

    private bool _svDragging, _hueDragging;

    private void OnSvPressed(object? sender, PointerPressedEventArgs e)
    {
        _svDragging = true;
        UpdateSvFromPoint(e.GetPosition(_svImage));
    }

    private void OnSvMoved(object? sender, PointerEventArgs e)
    {
        if (!_svDragging) return;
        UpdateSvFromPoint(e.GetPosition(_svImage));
    }

    private void OnSvReleased(object? sender, PointerReleasedEventArgs e)
    {
        _svDragging = false;
    }

    private void UpdateSvFromPoint(Point pt)
    {
        _saturation = Math.Clamp(pt.X / _svImage.Width, 0, 1);
        _value = Math.Clamp(1.0 - pt.Y / _svImage.Height, 0, 1);
        UpdateAll();
    }

    private void OnHuePressed(object? sender, PointerPressedEventArgs e)
    {
        _hueDragging = true;
        UpdateHueFromPoint(e.GetPosition(_hueImage));
    }

    private void OnHueMoved(object? sender, PointerEventArgs e)
    {
        if (!_hueDragging) return;
        UpdateHueFromPoint(e.GetPosition(_hueImage));
    }

    private void OnHueReleased(object? sender, PointerReleasedEventArgs e)
    {
        _hueDragging = false;
    }

    private void UpdateHueFromPoint(Point pt)
    {
        _hue = Math.Clamp(pt.Y / _hueImage.Height * 360, 0, 360);
        UpdateAll();
    }

    // ── Input parsing ─────────────────────────────────────────────────────────

    private void ParseHex()
    {
        var text = _hexBox.Text?.Replace("#", "") ?? "";
        if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            var r = (byte)((rgb >> 16) & 0xFF);
            var g = (byte)((rgb >> 8) & 0xFF);
            var b = (byte)(rgb & 0xFF);
            (_hue, _saturation, _value) = RgbToHsv(r, g, b);
            UpdateAll();
        }
    }

    private void ParseRgb()
    {
        if (byte.TryParse(_rBox.Text, out var r) &&
            byte.TryParse(_gBox.Text, out var g) &&
            byte.TryParse(_bBox.Text, out var b))
        {
            (_hue, _saturation, _value) = RgbToHsv(r, g, b);
            UpdateAll();
        }
    }

    private void ParseHsv()
    {
        if (double.TryParse(_hBox.Text, out var h) &&
            double.TryParse(_sBox.Text, out var s) &&
            double.TryParse(_vBox.Text, out var v))
        {
            _hue = Math.Clamp(h, 0, 360);
            _saturation = Math.Clamp(s / 100, 0, 1);
            _value = Math.Clamp(v / 100, 0, 1);
            UpdateAll();
        }
    }

    // ── Update UI ─────────────────────────────────────────────────────────────

    private void UpdateAll()
    {
        if (_syncing) return;
        _syncing = true;
        UpdateImages();
        UpdateInputs();
        var color = ColorFromHsv(_hue, _saturation, _value);
        _previewBox.Background = new SolidColorBrush(color);
        // NOTE: we do NOT call _onChange here — the callback is only fired
        // when the user clicks OK so that dragging doesn't spam history.
        _syncing = false;
    }

    private void UpdateImages()
    {
        _svImage.Source = RenderSvGradient(_hue);
        _hueImage.Source = RenderHueGradient();
    }

    private void UpdateInputs()
    {
        var color = ColorFromHsv(_hue, _saturation, _value);
        _hexBox.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        _rBox.Text = color.R.ToString();
        _gBox.Text = color.G.ToString();
        _bBox.Text = color.B.ToString();
        _hBox.Text = Math.Round(_hue).ToString();
        _sBox.Text = Math.Round(_saturation * 100).ToString();
        _vBox.Text = Math.Round(_value * 100).ToString();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private static Bitmap RenderSvGradient(double hue)
    {
        const int w = 256, h = 256;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);

        // Draw saturation/value gradient
        for (int y = 0; y < h; y++)
        {
            var v = 1.0 - y / (double)h;
            for (int x = 0; x < w; x++)
            {
                var s = x / (double)w;
                var (r, g, b) = HsvToRgb(hue, s, v);
                bmp.SetPixel(x, y, new SKColor((byte)r, (byte)g, (byte)b));
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new System.IO.MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    private static Bitmap RenderHueGradient()
    {
        const int w = 20, h = 256;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);

        for (int y = 0; y < h; y++)
        {
            var hue = y / (double)h * 360;
            var (r, g, b) = HsvToRgb(hue, 1, 1);
            var color = new SKColor((byte)r, (byte)g, (byte)b);
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, color);
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new System.IO.MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    // ── Color math ────────────────────────────────────────────────────────────

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd) h = 60 * ((gd - bd) / delta % 6);
            else if (max == gd) h = 60 * ((bd - rd) / delta + 2);
            else h = 60 * ((rd - gd) / delta + 4);
        }
        if (h < 0) h += 360;

        double s = max == 0 ? 0 : delta / max;
        return (h, s, max);
    }

    private static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return (
            (int)Math.Clamp((r + m) * 255, 0, 255),
            (int)Math.Clamp((g + m) * 255, 0, 255),
            (int)Math.Clamp((b + m) * 255, 0, 255)
        );
    }

    private static Color ColorFromHsv(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }
}
