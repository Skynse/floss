using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Config;

namespace Floss.App.Controls;

using static AppColors;

/// <summary>
/// Labeled H/S/B row: short label, <see cref="ScrubSlider"/> track, numeric field with suffix.
/// </summary>
public sealed class HsvSliderRow : Grid
{
    private readonly ScrubSlider _scrub;
    private readonly TextBox _valueBox;
    private readonly TextBlock _suffixBlock;

    private bool _syncing;

    public event Action<double>? ValueChanged;

    public double Minimum
    {
        get => _scrub.Minimum;
        set => _scrub.Minimum = value;
    }

    public double Maximum
    {
        get => _scrub.Maximum;
        set => _scrub.Maximum = value;
    }

    public double Value
    {
        get => _scrub.Value;
        set => _scrub.Value = Math.Clamp(value, Minimum, Maximum);
    }

    public HsvSliderRow(string label, string suffix, double min, double max, double initial)
    {
        _scrub = new ScrubSlider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(initial, min, max),
            ShowValueFill = false
        };
        _scrub.PropertyChanged += (_, e) =>
        {
            if (_syncing || e.Property != RangeBase.ValueProperty) return;
            SyncValueText();
            if (!_scrub.IsScrubbing)
                ValueChanged?.Invoke(Value);
        };
        _scrub.ScrubCompleted += (_, _) =>
        {
            if (_syncing) return;
            ValueChanged?.Invoke(Value);
        };

        ColumnDefinitions =
        [
            new ColumnDefinition(18, GridUnitType.Pixel),
            new ColumnDefinition(1, GridUnitType.Star),
            new ColumnDefinition(58, GridUnitType.Pixel)
        ];

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(labelBlock, 0);

        _scrub.Margin = new Thickness(0, 0, 10, 0);
        _scrub.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_scrub, 1);

        _valueBox = new TextBox
        {
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, suffix.Length > 0 ? 16 : 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            CaretBrush = new SolidColorBrush(Color.Parse(TextPrimary)),
            TextAlignment = TextAlignment.Right,
            MinHeight = 22,
            MaxHeight = 22
        };
        _valueBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
                CommitValueText();
        };
        _valueBox.LostFocus += (_, _) => CommitValueText();

        _suffixBlock = new TextBlock
        {
            Text = suffix,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 5, 0),
            IsHitTestVisible = false
        };

        var valueHost = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(valueHost, 2);
        valueHost.Children.Add(_valueBox);
        if (!string.IsNullOrEmpty(suffix))
            valueHost.Children.Add(_suffixBlock);

        Children.Add(labelBlock);
        Children.Add(_scrub);
        Children.Add(valueHost);

        SyncValueText();
    }

    public void SetTrackBackground(IBrush? brush) => _scrub.TrackBackground = brush;

    private void SyncValueText()
    {
        _syncing = true;
        try
        {
            _valueBox.Text = Math.Round(Value).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void CommitValueText()
    {
        if (_syncing) return;
        var text = _valueBox.Text?.Trim().TrimEnd('°', '%') ?? "";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return;

        _syncing = true;
        Value = parsed;
        _syncing = false;
        ValueChanged?.Invoke(Value);
    }
}
