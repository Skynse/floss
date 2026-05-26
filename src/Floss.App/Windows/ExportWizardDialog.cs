using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;

namespace Floss.App.Windows;

using static AppColors;

public enum ExportScaleMode { Percent, PixelSize, Dpi }
public enum ExportBackgroundMode { Document, FillWhite, Transparent }
public enum ExportResampleMode { Lanczos, Bicubic, Bilinear, Nearest }

public sealed record ExportSettings(
    int Quality,
    ExportScaleMode ScaleMode,
    double ScalePercent,
    int TargetWidth,
    int TargetHeight,
    int TargetDpi,
    ExportBackgroundMode Background,
    ExportResampleMode Resample
);

public sealed class ExportWizardDialog : Window
{
    private readonly string _format; // "PNG", "JPEG", "WebP", "BMP", etc.
    private readonly int _docWidth;
    private readonly int _docHeight;
    private readonly int _docDpi;

    // Quality
    private readonly Slider _qualitySlider;
    private readonly TextBlock _qualityLabel;
    private readonly Border _qualitySection;

    // Scale
    private readonly ComboBox _scaleModeCombo;
    private readonly NumericUpDown _scalePercentNud;
    private readonly NumericUpDown _targetWidthNud;
    private readonly NumericUpDown _targetHeightNud;
    private readonly NumericUpDown _targetDpiNud;
    private readonly StackPanel _scalePercentRow;
    private readonly StackPanel _scaleSizeRow;
    private readonly StackPanel _scaleDpiRow;
    private readonly TextBlock _outputSizeLabel;

    // Background
    private readonly ComboBox _bgCombo;

    // Resample
    private readonly ComboBox _resampleCombo;

    private bool _syncing;

    public ExportWizardDialog(string format, int docWidth, int docHeight, int docDpi = 72)
    {
        _format = format.ToUpperInvariant();
        _docWidth = Math.Max(1, docWidth);
        _docHeight = Math.Max(1, docHeight);
        _docDpi = Math.Max(1, docDpi);

        Title = $"{format} Export Settings";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg2));
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary));

        bool hasQuality = _format is "JPEG" or "JPG" or "WEBP";

        _qualitySlider = new Slider
        {
            Minimum = 1,
            Maximum = 100,
            Value = _format is "JPEG" or "JPG" ? 90 : 80,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Height = 26
        };
        _qualityLabel = new TextBlock
        {
            FontSize = 11,
            Width = 36,
            TextAlignment = Avalonia.Media.TextAlignment.Right,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Text = $"{_qualitySlider.Value:0}"
        };
        _qualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                _qualityLabel.Text = $"{_qualitySlider.Value:0}";
        };
        _qualitySection = new Border { IsVisible = hasQuality };

        _scaleModeCombo = new ComboBox
        {
            ItemsSource = new[] { "Scale (%)","Specify size (px)", "Specify DPI" },
            SelectedIndex = 0,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _scalePercentNud = MkNud(100, 1, 10000);
        _targetWidthNud  = MkNud(_docWidth, 1, 64000);
        _targetHeightNud = MkNud(_docHeight, 1, 64000);
        _targetDpiNud    = MkNud(_docDpi, 1, 4800);

        _outputSizeLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        UpdateOutputSizeLabel();

        _scalePercentRow = new StackPanel { Spacing = 4 };
        _scaleSizeRow    = new StackPanel { Spacing = 4 };
        _scaleDpiRow     = new StackPanel { Spacing = 4 };

        _bgCombo = new ComboBox
        {
            ItemsSource = new[] { "Document default", "Fill white", "Transparent" },
            SelectedIndex = 0,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _resampleCombo = new ComboBox
        {
            ItemsSource = new[] { "Lanczos (best quality)", "Bicubic", "Bilinear", "Nearest (pixel art)" },
            SelectedIndex = 0,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        WireEvents();
        Content = BuildShell();
        SyncScalePanels();
    }

    private void WireEvents()
    {
        _scaleModeCombo.SelectionChanged += (_, _) => SyncScalePanels();

        _scalePercentNud.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            UpdateOutputSizeLabel();
        };
        _targetWidthNud.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            var ratio = (double)_docHeight / _docWidth;
            _targetHeightNud.Value = (decimal)Math.Max(1, (int)((double)(_targetWidthNud.Value ?? 1) * ratio));
            _syncing = false;
            UpdateOutputSizeLabel();
        };
        _targetHeightNud.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            var ratio = (double)_docWidth / _docHeight;
            _targetWidthNud.Value = (decimal)Math.Max(1, (int)((double)(_targetHeightNud.Value ?? 1) * ratio));
            _syncing = false;
            UpdateOutputSizeLabel();
        };
        _targetDpiNud.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            UpdateOutputSizeLabel();
        };
    }

    private void SyncScalePanels()
    {
        _scalePercentRow.IsVisible = _scaleModeCombo.SelectedIndex == 0;
        _scaleSizeRow.IsVisible    = _scaleModeCombo.SelectedIndex == 1;
        _scaleDpiRow.IsVisible     = _scaleModeCombo.SelectedIndex == 2;
        UpdateOutputSizeLabel();
    }

    private void UpdateOutputSizeLabel()
    {
        var (w, h) = GetOutputPixelSize();
        _outputSizeLabel.Text = $"Output: {w} × {h} px";
    }

    private (int W, int H) GetOutputPixelSize()
    {
        return _scaleModeCombo.SelectedIndex switch
        {
            0 => ScaleByPercent(),
            1 => ((int)(_targetWidthNud.Value ?? _docWidth), (int)(_targetHeightNud.Value ?? _docHeight)),
            2 => ScaleByDpi(),
            _ => (_docWidth, _docHeight)
        };

        (int, int) ScaleByPercent()
        {
            var pct = (double)(_scalePercentNud.Value ?? 100m) / 100.0;
            return (Math.Max(1, (int)(_docWidth * pct)), Math.Max(1, (int)(_docHeight * pct)));
        }

        (int, int) ScaleByDpi()
        {
            var targetDpi = (double)(_targetDpiNud.Value ?? (decimal)_docDpi);
            var scale = targetDpi / _docDpi;
            return (Math.Max(1, (int)(_docWidth * scale)), Math.Max(1, (int)(_docHeight * scale)));
        }
    }

    private Control BuildShell()
    {
        var root = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(20, 18, 20, 20)
        };

        // Quality section (JPEG/WebP only)
        _qualitySection.Child = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                SectionLabel("QUALITY"),
                BuildQualityRow(),
                new Border { Height = 12 }
            }
        };
        root.Children.Add(_qualitySection);

        // Output size section
        root.Children.Add(SectionLabel("OUTPUT SIZE"));
        root.Children.Add(new Border { Height = 4 });
        root.Children.Add(_scaleModeCombo);
        root.Children.Add(new Border { Height = 6 });

        _scalePercentRow.Children.Add(FieldRow("Scale", _scalePercentNud, "%"));
        _scaleSizeRow.Children.Add(FieldRow("Width", _targetWidthNud, "px"));
        _scaleSizeRow.Children.Add(FieldRow("Height", _targetHeightNud, "px"));
        _scaleDpiRow.Children.Add(FieldRow("DPI", _targetDpiNud, ""));

        root.Children.Add(_scalePercentRow);
        root.Children.Add(_scaleSizeRow);
        root.Children.Add(_scaleDpiRow);
        root.Children.Add(_outputSizeLabel);
        root.Children.Add(new Border { Height = 12 });

        // Background
        root.Children.Add(SectionLabel("BACKGROUND"));
        root.Children.Add(new Border { Height = 4 });
        root.Children.Add(_bgCombo);
        root.Children.Add(new Border { Height = 12 });

        // Resampling (only relevant when scaling)
        root.Children.Add(SectionLabel("RESAMPLING"));
        root.Children.Add(new Border { Height = 4 });
        root.Children.Add(_resampleCombo);
        root.Children.Add(new Border { Height = 16 });

        // Buttons
        var cancelBtn = ActionBtn("Cancel", false);
        cancelBtn.Click += (_, _) => Close(null);

        var okBtn = ActionBtn("Export", true);
        okBtn.Click += (_, _) => OnExport();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelBtn, okBtn }
        };
        root.Children.Add(btnRow);

        return root;
    }

    private Control BuildQualityRow()
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_qualityLabel, Dock.Right);
        row.Children.Add(_qualityLabel);
        row.Children.Add(_qualitySlider);
        return row;
    }

    private void OnExport()
    {
        var (outW, outH) = GetOutputPixelSize();

        var settings = new ExportSettings(
            Quality: (int)_qualitySlider.Value,
            ScaleMode: (ExportScaleMode)_scaleModeCombo.SelectedIndex,
            ScalePercent: (double)(_scalePercentNud.Value ?? 100m),
            TargetWidth: outW,
            TargetHeight: outH,
            TargetDpi: (int)(_targetDpiNud.Value ?? (decimal)_docDpi),
            Background: (ExportBackgroundMode)_bgCombo.SelectedIndex,
            Resample: (ExportResampleMode)_resampleCombo.SelectedIndex
        );

        Close(settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NumericUpDown MkNud(decimal value, decimal min, decimal max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FontSize = 12,
        MinHeight = 26,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static Control FieldRow(string label, Control input, string unit)
    {
        var unitLabel = new TextBlock
        {
            Text = unit,
            FontSize = 11,
            Width = unit.Length > 0 ? 24 : 0,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Width = 60,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        }, Dock.Left);
        DockPanel.SetDock(unitLabel, Dock.Right);

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Width = 60,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(unitLabel);
        row.Children.Add(input);
        return row;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin = new Thickness(0, 0, 0, 0),
        LetterSpacing = 1.2
    };

    private static Button ActionBtn(string label, bool primary) => new()
    {
        Content = label,
        Width = 88,
        Height = 30,
        FontSize = 12,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = new SolidColorBrush(primary ? Color.Parse(Accent) : Color.Parse(Bg1)),
        Foreground = new SolidColorBrush(primary ? Colors.White : Color.Parse(TextSecondary)),
        BorderBrush = new SolidColorBrush(primary ? Color.Parse(Accent) : Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4)
    };
}
