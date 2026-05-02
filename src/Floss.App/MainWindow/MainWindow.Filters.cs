using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Document;
using Floss.App.Filters;

namespace Floss.App;

public partial class MainWindow
{
    // ── Preview handle ────────────────────────────────────────────────────────
    // Lets each filter method trigger debounced preview updates without needing
    // direct access to ShowFilterDialog's internals.

    private sealed class PreviewHandle { public Action Schedule = () => { }; }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private IReadOnlyList<int> EffectiveLayerSelection()
        => _selectedLayerIndices.Count > 1
            ? (IReadOnlyList<int>)_selectedLayerIndices.OrderBy(x => x).ToList()
            : new[] { _canvas.ActiveLayerIndex };

    private string LayerSelectionLabel()
    {
        var n = _selectedLayerIndices.Count > 1 ? _selectedLayerIndices.Count : 1;
        return n == 1 ? "Apply to 1 layer" : $"Apply to {n} layers";
    }

    // ── Filter entry points ───────────────────────────────────────────────────

    internal async Task ApplyBlurFilter()
    {
        var preview = new PreviewHandle();
        var valueLabel = FilterValueLabel("3.0");
        var slider = new Slider { Minimum = 0.5, Maximum = 30, Value = 3.0, Width = 240, Margin = new Thickness(0, 4, 0, 0) };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                valueLabel.Text = $"{slider.Value:0.0}";
                preview.Schedule();
            }
        };

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () => { var s = (float)slider.Value; return l => FilterEngine.ApplyGaussianBlur(l, s, sel); };
        var content = FilterRow(FilterLabel("Radius"), slider, valueLabel);
        var ok = await ShowFilterDialog("Gaussian Blur", content, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        var sigma = (float)slider.Value;
        _canvas.ApplyFilter(EffectiveLayerSelection(), l => FilterEngine.ApplyGaussianBlur(l, sigma, sel));
        BuildLayerList();
    }

    internal async Task ApplySharpenFilter()
    {
        var preview = new PreviewHandle();
        var valueLabel = FilterValueLabel("1.0");
        var slider = new Slider { Minimum = 0.1, Maximum = 5, Value = 1.0, Width = 240, Margin = new Thickness(0, 4, 0, 0) };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                valueLabel.Text = $"{slider.Value:0.0}";
                preview.Schedule();
            }
        };

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () => { var a = (float)slider.Value; return l => FilterEngine.ApplySharpen(l, a, sel); };
        var content = FilterRow(FilterLabel("Amount"), slider, valueLabel);
        var ok = await ShowFilterDialog("Sharpen", content, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        var amount = (float)slider.Value;
        _canvas.ApplyFilter(EffectiveLayerSelection(), l => FilterEngine.ApplySharpen(l, amount, sel));
        BuildLayerList();
    }

    internal async Task ApplyNoiseFilter()
    {
        var preview = new PreviewHandle();
        var valueLabel = FilterValueLabel("10%");
        var slider = new Slider { Minimum = 0.01, Maximum = 1, Value = 0.1, Width = 240, Margin = new Thickness(0, 4, 0, 0) };
        var monoCheck = new CheckBox
        {
            Content = new TextBlock { Text = "Monochromatic", Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), FontSize = 11 },
            IsChecked = true,
            Margin = new Thickness(0, 6, 0, 0)
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                valueLabel.Text = $"{(int)(slider.Value * 100)}%";
                preview.Schedule();
            }
        };
        monoCheck.IsCheckedChanged += (_, _) => preview.Schedule();

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () =>
        {
            var a = (float)slider.Value; var m = monoCheck.IsChecked == true;
            return l => FilterEngine.ApplyNoise(l, a, m, sel);
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(FilterRow(FilterLabel("Amount"), slider, valueLabel));
        panel.Children.Add(monoCheck);

        var ok = await ShowFilterDialog("Add Noise", panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        var amt = (float)slider.Value;
        var mono = monoCheck.IsChecked == true;
        _canvas.ApplyFilter(EffectiveLayerSelection(), l => FilterEngine.ApplyNoise(l, amt, mono, sel));
        BuildLayerList();
    }

    internal async Task ApplyColorCurvesFilter()
    {
        var preview = new PreviewHandle();
        var channels = new[] { "RGB", "R", "G", "B" };
        var graphs = new CurveGraph[4];
        for (var i = 0; i < 4; i++)
            graphs[i] = new CurveGraph { Width = 230, Height = 200 };

        var channelBtns = new Button[4];
        var graphHost = new ContentControl { Content = graphs[0] };

        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            graphs[i].CurveChanged += (_, _) => preview.Schedule();
            var btn = new Button
            {
                Content = channels[i],
                Padding = new Thickness(8, 3),
                FontSize = 11,
                Background = new SolidColorBrush(i == 0 ? Color.Parse("#1e2e5a") : Color.Parse("#14161e")),
                Foreground = new SolidColorBrush(i == 0 ? Color.Parse("#80aaee") : Color.Parse(TextSecondary)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            };
            btn.Click += (_, _) =>
            {
                graphHost.Content = graphs[idx];
                for (var j = 0; j < 4; j++)
                {
                    channelBtns[j].Background = new SolidColorBrush(j == idx ? Color.Parse("#1e2e5a") : Color.Parse("#14161e"));
                    channelBtns[j].Foreground = new SolidColorBrush(j == idx ? Color.Parse("#80aaee") : Color.Parse(TextSecondary));
                }
            };
            channelBtns[i] = btn;
        }

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var btn in channelBtns) btnRow.Children.Add(btn);

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () =>
        {
            var lm = graphs[0].ComputeLut(); var lr = graphs[1].ComputeLut();
            var lg = graphs[2].ComputeLut(); var lb = graphs[3].ComputeLut();
            return l => FilterEngine.ApplyCurves(l, lm, lr, lg, lb, sel);
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(btnRow);
        panel.Children.Add(graphHost);

        var ok = await ShowFilterDialog("Color Curves", panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        var lutM = graphs[0].ComputeLut(); var lutR = graphs[1].ComputeLut();
        var lutG = graphs[2].ComputeLut(); var lutB = graphs[3].ComputeLut();
        _canvas.ApplyFilter(EffectiveLayerSelection(), l => FilterEngine.ApplyCurves(l, lutM, lutR, lutG, lutB, sel));
        BuildLayerList();
    }

    // ── Dialog system ─────────────────────────────────────────────────────────

    private async Task<bool> ShowFilterDialog(
        string title,
        Control content,
        string applyLabel,
        Func<Action<DrawingLayer>>? buildPreview = null,
        PreviewHandle? previewHandle = null)
    {
        var layers = EffectiveLayerSelection();

        // Capture pixels so we can revert during/after preview
        var captured = new Dictionary<int, byte[]>();
        if (buildPreview != null)
        {
            foreach (var idx in layers)
            {
                if (idx >= 0 && idx < _canvas.Layers.Count)
                {
                    var layer = _canvas.Layers[idx];
                    if (!layer.IsGroup && !layer.IsLocked)
                        captured[idx] = layer.CapturePixels();
                }
            }
        }

        var previewOn = false;
        DispatcherTimer? debounce = null;

        void ApplyPreviewNow()
        {
            if (!previewOn || buildPreview == null) return;
            var action = buildPreview();
            foreach (var (idx, pixels) in captured)
            {
                _canvas.Layers[idx].RestorePixels(pixels);
                _canvas.Layers[idx].MarkThumbnailDirty();
            }
            foreach (var idx in layers)
            {
                if (idx >= 0 && idx < _canvas.Layers.Count)
                {
                    var layer = _canvas.Layers[idx];
                    if (!layer.IsGroup && !layer.IsLocked)
                        action(layer);
                }
            }
            _canvas.Document.NotifyChanged();
        }

        void SchedulePreview()
        {
            if (!previewOn) return;
            if (debounce == null)
            {
                debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                debounce.Tick += (_, _) => { debounce.Stop(); ApplyPreviewNow(); };
            }
            debounce.Stop();
            debounce.Start();
        }

        void RevertPreview()
        {
            debounce?.Stop();
            foreach (var (idx, pixels) in captured)
            {
                _canvas.Layers[idx].RestorePixels(pixels);
                _canvas.Layers[idx].MarkThumbnailDirty();
            }
            if (captured.Count > 0)
                _canvas.Document.NotifyChanged();
        }

        if (previewHandle != null)
            previewHandle.Schedule = SchedulePreview;

        // ── Preview toggle button ─────────────────────────────────────────────
        Button? previewBtn = null;
        if (buildPreview != null)
        {
            previewBtn = new Button
            {
                Content = "Preview",
                Padding = new Thickness(10, 4),
                FontSize = 11,
                Background = new SolidColorBrush(Color.Parse("#14161e")),
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            };
            previewBtn.Click += (_, _) =>
            {
                previewOn = !previewOn;
                previewBtn.Background = new SolidColorBrush(previewOn ? Color.Parse("#1e3050") : Color.Parse("#14161e"));
                previewBtn.Foreground = new SolidColorBrush(previewOn ? Color.Parse("#70a0e8") : Color.Parse(TextSecondary));
                previewBtn.BorderBrush = new SolidColorBrush(previewOn ? Color.Parse("#2a4a88") : Color.Parse(Stroke));
                if (previewOn) ApplyPreviewNow();
                else RevertPreview();
            };
        }

        // ── Action buttons ────────────────────────────────────────────────────
        var applyBtn = new Button
        {
            Content = applyLabel,
            Padding = new Thickness(14, 5),
            FontSize = 11,
            Background = new SolidColorBrush(Color.Parse("#1e3a78")),
            Foreground = new SolidColorBrush(Color.Parse("#90baf0")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2a4a98")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(14, 5),
            FontSize = 11,
            Background = new SolidColorBrush(Color.Parse("#1a1c22")),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        // Preview on left, Cancel+Apply on right
        var footerGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        if (previewBtn != null)
        {
            footerGrid.ColumnDefinitions.Insert(0, new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(previewBtn, 0);
            Grid.SetColumn(cancelBtn, 2);
            Grid.SetColumn(applyBtn, 3);
            footerGrid.Children.Add(previewBtn);
        }
        else
        {
            Grid.SetColumn(cancelBtn, 1);
            Grid.SetColumn(applyBtn, 2);
        }
        footerGrid.Children.Add(cancelBtn);
        footerGrid.Children.Add(applyBtn);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 0 };
        root.Children.Add(content);
        root.Children.Add(footerGrid);

        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            MinWidth = 300
        };

        applyBtn.Click += (_, _) =>
        {
            debounce?.Stop();
            if (previewOn) RevertPreview(); // revert so caller's ApplyFilter records proper undo
            tcs.TrySetResult(true);
            dialog.Close();
        };
        cancelBtn.Click += (_, _) =>
        {
            debounce?.Stop();
            if (previewOn) RevertPreview();
            tcs.TrySetResult(false);
            dialog.Close();
        };
        dialog.Closed += (_, _) =>
        {
            debounce?.Stop();
            if (previewOn) RevertPreview();
            tcs.TrySetResult(false);
        };

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    private static TextBlock FilterLabel(string text) => new()
    {
        Text = text,
        Width = 60,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
    };

    private static TextBlock FilterValueLabel(string text) => new()
    {
        Text = text,
        Width = 44,
        FontSize = 11,
        TextAlignment = Avalonia.Media.TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
    };

    private static Control FilterRow(TextBlock label, Slider slider, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }
}
