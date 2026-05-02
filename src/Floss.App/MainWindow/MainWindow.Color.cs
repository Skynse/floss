using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Floss.App;

public partial class MainWindow
{
    // ── Color section ─────────────────────────────────────────────────────────
    private StackPanel BuildColorSection()
    {
        _colorPicker = new HsvColorPicker
        {
            Height = 168,
            Margin = new Thickness(12, 4, 12, 10)
        };
        _colorPicker.HsvChanged += OnPickerHsvChanged;

        _hexInput = new TextBox
        {
            Width = 112,
            Height = 26,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a4250")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        _hexInput.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Return) TryApplyHexColor(_hexInput.Text ?? ""); };
        _hexInput.LostFocus += (_, _) => TryApplyHexColor(_hexInput.Text ?? "");

        _swatchPanel = new WrapPanel
        {
            Margin = new Thickness(12, 2, 12, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            ItemWidth = SwatchSize + 2,
            ItemHeight = SwatchSize + 2
        };

        return new StackPanel
        {
            Children =
            {
                _colorPicker,
                new Border { Margin = new Thickness(12, 0, 12, 6), Child = _hexInput },
                _swatchPanel
            }
        };
    }

    // ── Color picker ──────────────────────────────────────────────────────────
    private void OnPickerHsvChanged(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        var color = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SetColor(color, syncPicker: false, switchToBrush: true);
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
            SetColor(Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)), switchToBrush: true);
    }

    // ── Color application ─────────────────────────────────────────────────────
    private void SetColor(Color color, bool syncPicker = true, bool switchToBrush = false)
    {
        _colorWell.Background = new SolidColorBrush(color);
        _canvas.SetPaintColor(color);
        if (switchToBrush)
        {
            var brushGroup = App.ToolGroups.Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Brush)
                ?? App.ToolGroups.Groups.FirstOrDefault();
            if (brushGroup != null)
            {
                var preset = brushGroup.ActivePreset ?? brushGroup.Presets.FirstOrDefault();
                if (preset != null) ActivatePreset(brushGroup, preset);
            }
        }
        if (syncPicker) SyncPickerFromColor(color);
    }

    private void CycleColor()
    {
        _swatchIndex = (_swatchIndex + 1) % _swatches.Length;
        SetColor(_swatches[_swatchIndex], switchToBrush: true);
    }

    // ── Swatch panel ──────────────────────────────────────────────────────────
    private const int SwatchColumns = 10;
    private const int SwatchSize = 20;

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
                BorderBrush = new SolidColorBrush(Color.Parse("#2a2d35")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(0)
            };
            ToolTip.SetTip(btn, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            btn.Click += (_, _) => { _swatchIndex = idx; SetColor(color, switchToBrush: true); };
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
