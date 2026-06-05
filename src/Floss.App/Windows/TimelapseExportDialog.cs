using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Floss.App.Timelapse;

namespace Floss.App.Windows;

using static AppColors;

public sealed class TimelapseExportDialog : Window
{
    private readonly TimelapseSession _session;
    private readonly IReadOnlyList<string> _frames;
    private readonly Image _preview;
    private readonly Button _playButton;
    private readonly ScrubSlider _seek;
    private readonly TextBlock _time;
    private readonly ComboBox _length;
    private readonly ComboBox _aspect;
    private readonly ComboBox _size;
    private readonly DispatcherTimer _timer;
    private int _index;
    private bool _playing;
    private Bitmap? _previewBitmap;

    public TimelapseExportDialog(TimelapseSession session)
    {
        _session = session;
        _frames = session.FramePaths();

        Title = "Export Timelapse";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg2));
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary));

        _preview = new Image
        {
            Width = 300,
            Height = 300,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _playButton = SmallButton("▶");
        _playButton.Click += (_, _) => TogglePlay();

        _seek = ScrubSliderFactory.Create(0, Math.Max(0, _frames.Count - 1), 0);
        _seek.Width = 280;
        _seek.IsEnabled = _frames.Count > 1;
        _seek.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_playing)
                ShowFrame((int)Math.Round(_seek.Value));
        };

        _time = new TextBlock
        {
            Width = 64,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        _length = new ComboBox { ItemsSource = BuildLengthOptions(), SelectedIndex = 0, MinWidth = 200 };
        _aspect = new ComboBox { ItemsSource = new[] { "Original", "Landscape", "Portrait" }, SelectedIndex = 0, MinWidth = 200 };
        _size = new ComboBox { ItemsSource = new[] { "1280 px", "1920 px", "2560 px" }, SelectedIndex = 0, MinWidth = 200 };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 12.0) };
        _timer.Tick += (_, _) => StepPlayback();

        Content = BuildContent();
        ShowFrame(0);
    }

    private Control BuildContent()
    {
        var ok = DialogButton("OK", accent: true);
        ok.Click += (_, _) => Close(BuildSettings());
        var cancel = DialogButton("Cancel", accent: false);
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Spacing = 6,
            Children = { ok, cancel },
            VerticalAlignment = VerticalAlignment.Top
        };

        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(20, 18, 14, 8) };
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(_preview);

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(20, 4, 20, 10),
            Children = { _playButton, _seek, _time }
        };

        var options = new Grid { Margin = new Thickness(20, 0, 20, 18), RowDefinitions = new RowDefinitions("32,32,32") };
        options.ColumnDefinitions.Add(new ColumnDefinition(160, GridUnitType.Pixel));
        options.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        AddOption(options, 0, "Length", _length);
        AddOption(options, 1, "Size", _size);
        AddOption(options, 2, "Aspect ratio", _aspect);

        return new StackPanel { Children = { top, transport, options } };
    }

    private IReadOnlyList<string> BuildLengthOptions()
    {
        var items = new List<string> { "All" };
        if (_session.HasEnoughFrames(TimelapseLength.Seconds15)) items.Add("15 seconds");
        if (_session.HasEnoughFrames(TimelapseLength.Seconds30)) items.Add("30 seconds");
        if (_session.HasEnoughFrames(TimelapseLength.Seconds60)) items.Add("60 seconds");
        return items;
    }

    private TimelapseExportSettings BuildSettings()
    {
        var lengthText = _length.SelectedItem?.ToString() ?? "All";
        var aspectText = _aspect.SelectedItem?.ToString() ?? "Original";
        var sizeText = _size.SelectedItem?.ToString() ?? "1280 px";

        return new TimelapseExportSettings
        {
            Length = lengthText.StartsWith("15", StringComparison.Ordinal) ? TimelapseLength.Seconds15 :
                lengthText.StartsWith("30", StringComparison.Ordinal) ? TimelapseLength.Seconds30 :
                lengthText.StartsWith("60", StringComparison.Ordinal) ? TimelapseLength.Seconds60 :
                TimelapseLength.All,
            Aspect = aspectText == "Landscape" ? TimelapseAspect.Landscape :
                aspectText == "Portrait" ? TimelapseAspect.Portrait :
                TimelapseAspect.Original,
            LongestSidePixels = sizeText.StartsWith("1920", StringComparison.Ordinal) ? 1920 :
                sizeText.StartsWith("2560", StringComparison.Ordinal) ? 2560 : 1280
        };
    }

    private void TogglePlay()
    {
        if (_frames.Count == 0) return;
        _playing = !_playing;
        _playButton.Content = _playing ? "Ⅱ" : "▶";
        if (_playing) _timer.Start();
        else _timer.Stop();
    }

    private void StepPlayback()
    {
        if (_frames.Count == 0) return;
        ShowFrame((_index + 1) % _frames.Count);
    }

    private void ShowFrame(int index)
    {
        if (_frames.Count == 0)
        {
            _preview.Source = null;
            _time.Text = "00:00";
            return;
        }

        _index = Math.Clamp(index, 0, _frames.Count - 1);
        _seek.Value = _index;
        try
        {
            using var stream = File.OpenRead(_frames[_index]);
            var next = new Bitmap(stream);
            _previewBitmap?.Dispose();
            _previewBitmap = next;
            _preview.Source = next;
        }
        catch
        {
            _previewBitmap?.Dispose();
            _previewBitmap = null;
            _preview.Source = null;
        }

        var seconds = _index / 12;
        _time.Text = $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private static void AddOption(Grid grid, int row, string label, Control input)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);
    }

    private static Button SmallButton(string label) => new()
    {
        Content = label,
        Width = 28,
        Height = 28,
        Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(Bg1)),
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    private static Button DialogButton(string label, bool accent) => new()
    {
        Content = label,
        Width = 80,
        Height = 28,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(accent ? Accent : Bg1)),
        Foreground = new SolidColorBrush(accent ? Colors.White : Color.Parse(TextSecondary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = accent ? new Thickness(0) : new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };
}
