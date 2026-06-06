using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Floss.App.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Document;
using Floss.App.Filters;
using Floss.App.ImageFiles;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App;

using static Floss.App.Config.AppColors;
using static Floss.App.FilterControls;

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
        var slider = FilterSlider(0.5, 30, 3.0);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
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
        var slider = FilterSlider(0.1, 5, 1.0);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
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
        var slider = FilterSlider(0.01, 1, 0.1);
        var monoCheck = new CheckBox
        {
            Content = new TextBlock { Text = "Monochromatic", Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), FontSize = 11 },
            IsChecked = true,
            Margin = new Thickness(0, 6, 0, 0)
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
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

    internal async Task ApplyChromaticAberrationFilter()
    {
        var preview = new PreviewHandle();
        var intensityLabel = FilterValueLabel("5.0");
        var intensitySlider = FilterSlider(0, 30, 5);

        var radialToggle = new RadioButton
        {
            Content = new TextBlock { Text = "Radial", Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), FontSize = 11 },
            IsChecked = true,
            GroupName = "CAMode"
        };
        var lateralToggle = new RadioButton
        {
            Content = new TextBlock { Text = "Lateral", Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), FontSize = 11 },
            IsChecked = false,
            GroupName = "CAMode",
            Margin = new Thickness(12, 0, 0, 0)
        };

        var angleLabel = FilterValueLabel("0°");
        var angleSlider = FilterSlider(0, 360, 0);
        angleSlider.IsEnabled = false;

        lateralToggle.IsCheckedChanged += (_, _) =>
        {
            angleSlider.IsEnabled = lateralToggle.IsChecked == true;
            preview.Schedule();
        };
        radialToggle.IsCheckedChanged += (_, _) => preview.Schedule();

        intensitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                intensityLabel.Text = $"{intensitySlider.Value:0.0}";
                preview.Schedule();
            }
        };
        angleSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                angleLabel.Text = $"{(int)angleSlider.Value}°";
                preview.Schedule();
            }
        };

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () =>
        {
            var i = (float)intensitySlider.Value;
            var radial = radialToggle.IsChecked == true;
            var a = (float)angleSlider.Value;
            return l => FilterEngine.ApplyChromaticAbberation(l, i, radial, a, sel);
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(FilterRow(FilterLabel("Intensity"), intensitySlider, intensityLabel));
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        modeRow.Children.Add(radialToggle);
        modeRow.Children.Add(lateralToggle);
        panel.Children.Add(modeRow);
        panel.Children.Add(FilterRow(FilterLabel("Angle"), angleSlider, angleLabel));

        var ok = await ShowFilterDialog("Chromatic Aberration", panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        var intensity = (float)intensitySlider.Value;
        var isRadial = radialToggle.IsChecked == true;
        var angle = (float)angleSlider.Value;
        _canvas.ApplyFilter(EffectiveLayerSelection(), l => FilterEngine.ApplyChromaticAbberation(l, intensity, isRadial, angle, sel));
        BuildLayerList();
    }

    internal async Task ApplyRemoveDustFilter()
    {
        var preview = new PreviewHandle();

        var sizeLabel = FilterValueLabel("50 px");
        var sizeSlider = FilterSlider(1, 500, 50);
        sizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                sizeLabel.Text = $"{(int)sizeSlider.Value} px";
                preview.Schedule();
            }
        };

        var opacLabel = FilterValueLabel("5%");
        var opacSlider = FilterSlider(1, 100, 5);
        opacSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                opacLabel.Text = $"{(int)opacSlider.Value}%";
                preview.Schedule();
            }
        };

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () =>
        {
            var sz = (int)sizeSlider.Value;
            var threshold = (byte)Math.Clamp((int)(opacSlider.Value / 100.0 * 255), 1, 255);
            return l => FilterEngine.RemoveDust(l, sz, threshold, sel);
        };

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(FilterRow(FilterLabel("Max size"), sizeSlider, sizeLabel));
        panel.Children.Add(FilterRow(FilterLabel("Min opacity"), opacSlider, opacLabel));

        var ok = await ShowFilterDialog("Remove Dust", panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        var maxSize = (int)sizeSlider.Value;
        var alphaThreshold = (byte)Math.Clamp((int)(opacSlider.Value / 100.0 * 255), 1, 255);
        _canvas.ApplyFilter(EffectiveLayerSelection(), l => FilterEngine.RemoveDust(l, maxSize, alphaThreshold, sel));
        BuildLayerList();
    }

    internal Task ApplyBrightnessContrastFilter()
        => ApplyTwoSliderFilter(
            "Brightness / Contrast",
            "Bright", -1, 1, 0, v => $"{v:+0.00;-0.00;0.00}",
            "Contrast", -1, 1, 0, v => $"{v:+0.00;-0.00;0.00}",
            (brightness, contrast, sel) => l => FilterEngine.ApplyBrightnessContrast(l, brightness, contrast, sel));

    internal Task ApplyExposureGammaFilter()
        => ApplyTwoSliderFilter(
            "Exposure / Gamma",
            "Exposure", -4, 4, 0, v => $"{v:+0.0;-0.0;0.0}",
            "Gamma", 0.1, 3, 1, v => $"{v:0.00}",
            (exposure, gamma, sel) => l => FilterEngine.ApplyExposureGamma(l, exposure, gamma, sel));

    internal Task ApplyHueSaturationFilter()
        => ApplyThreeSliderFilter(
            "Hue / Saturation",
            "Hue", -180, 180, 0, v => $"{(int)v}°",
            "Sat", -1, 1, 0, v => $"{v:+0.00;-0.00;0.00}",
            "Light", -1, 1, 0, v => $"{v:+0.00;-0.00;0.00}",
            (hue, saturation, lightness, sel) => l => FilterEngine.ApplyHueSaturationLightness(l, hue, saturation, lightness, sel));

    internal async Task ApplyLevelsFilter()
    {
        var preview = new PreviewHandle();
        var blackLabel = FilterValueLabel("0");
        var gammaLabel = FilterValueLabel("1.00");
        var whiteLabel = FilterValueLabel("255");
        var outBlackLabel = FilterValueLabel("0");
        var outWhiteLabel = FilterValueLabel("255");

        var black = FilterSlider(0, 254, 0);
        var gamma = FilterSlider(0.1, 4, 1);
        var white = FilterSlider(1, 255, 255);
        var outBlack = FilterSlider(0, 255, 0);
        var outWhite = FilterSlider(0, 255, 255);

        void Update()
        {
            if (black.Value >= white.Value) black.Value = Math.Max(0, white.Value - 1);
            if (white.Value <= black.Value) white.Value = Math.Min(255, black.Value + 1);
            blackLabel.Text = $"{(int)black.Value}";
            gammaLabel.Text = $"{gamma.Value:0.00}";
            whiteLabel.Text = $"{(int)white.Value}";
            outBlackLabel.Text = $"{(int)outBlack.Value}";
            outWhiteLabel.Text = $"{(int)outWhite.Value}";
            preview.Schedule();
        }

        foreach (var slider in new[] { black, gamma, white, outBlack, outWhite })
            slider.PropertyChanged += (_, e) => { if (e.Property.Name == nameof(Slider.Value)) Update(); };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(FilterRow(FilterLabel("Black"), black, blackLabel));
        panel.Children.Add(FilterRow(FilterLabel("Gamma"), gamma, gammaLabel));
        panel.Children.Add(FilterRow(FilterLabel("White"), white, whiteLabel));
        panel.Children.Add(FilterRow(FilterLabel("Out B"), outBlack, outBlackLabel));
        panel.Children.Add(FilterRow(FilterLabel("Out W"), outWhite, outWhiteLabel));

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () =>
        {
            var b = (int)black.Value;
            var g = (float)gamma.Value;
            var w = (int)white.Value;
            var ob = (int)outBlack.Value;
            var ow = (int)outWhite.Value;
            return l => FilterEngine.ApplyLevels(l, b, g, w, ob, ow, sel);
        };

        var ok = await ShowFilterDialog("Levels", panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        _canvas.ApplyFilter(EffectiveLayerSelection(), buildPreview());
        BuildLayerList();
    }

    internal Task ApplySepiaFilter()
        => ApplyOneSliderFilter("Sepia", "Amount", 0, 1, 1, v => $"{(int)(v * 100)}%", (amount, sel) => l => FilterEngine.ApplySepia(l, amount, sel));

    internal Task ApplyThresholdFilter()
        => ApplyOneSliderFilter("Threshold", "Level", 0, 255, 128, v => $"{(int)v}", (level, sel) => l => FilterEngine.ApplyThreshold(l, (byte)Math.Clamp((int)level, 0, 255), sel));

    internal Task ApplyPosterizeFilter()
        => ApplyOneSliderFilter("Posterize", "Levels", 2, 32, 5, v => $"{(int)v}", (levels, sel) => l => FilterEngine.ApplyPosterize(l, (int)levels, sel));

    internal Task ApplyPixelateFilter()
        => ApplyOneSliderFilter("Pixelate", "Block", 2, 128, 12, v => $"{(int)v}px", (block, sel) => l => FilterEngine.ApplyPixelate(l, (int)block, sel));

    internal Task ApplyVignetteFilter()
        => ApplyThreeSliderFilter(
            "Vignette",
            "Amount", 0, 1, 0.45, v => $"{(int)(v * 100)}%",
            "Radius", 0.05, 1, 0.62, v => $"{(int)(v * 100)}%",
            "Soft", 0.01, 1, 0.35, v => $"{(int)(v * 100)}%",
            (amount, radius, softness, sel) => l => FilterEngine.ApplyVignette(l, amount, radius, softness, sel));

    internal Task ApplyBloomFilter()
        => ApplyThreeSliderFilter(
            "Bloom",
            "Radius", 1, 40, 8, v => $"{v:0.0}",
            "Amount", 0, 3, 0.8, v => $"{v:0.00}",
            "Thresh", 0, 255, 180, v => $"{(int)v}",
            (radius, amount, threshold, sel) => l => FilterEngine.ApplyBloom(l, radius, amount, (byte)Math.Clamp((int)threshold, 0, 255), sel));

    internal Task ApplyMotionBlurFilter()
        => ApplyTwoSliderFilter(
            "Motion Blur",
            "Length", 1, 80, 12, v => $"{(int)v}px",
            "Angle", 0, 360, 0, v => $"{(int)v}°",
            (length, angle, sel) => l => FilterEngine.ApplyMotionBlur(l, (int)length, angle, sel));

    internal Task ApplyEmbossFilter()
        => ApplyOneSliderFilter("Emboss", "Amount", 0.1, 3, 1, v => $"{v:0.00}", (amount, sel) => l => FilterEngine.ApplyEmboss(l, amount, sel));

    internal Task ApplyEdgeDetectFilter()
        => ApplyOneSliderFilter("Find Edges", "Amount", 0.1, 3, 1, v => $"{v:0.00}", (amount, sel) => l => FilterEngine.ApplyEdgeDetect(l, amount, sel));

    internal void ApplyInvertFilter() => ApplyInstantFilter(l => FilterEngine.ApplyInvert(l, _canvas.Selection));
    internal void ApplyDesaturateFilter() => ApplyInstantFilter(l => FilterEngine.ApplyDesaturate(l, _canvas.Selection));

    internal async Task RunBaseColorMaskGenerator()
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = "Uses SkyTNT anime-segmentation (isnet_is) to detect character silhouettes\nand create a base-color mask layer below your sketch.\n\nThe model (~168 MB) downloads once and runs locally.",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#7080a0")),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = BaseColorMaskEngine.ModelFileExists
                ? $"Model: {BaseColorMaskEngine.ModelPath}"
                : $"Model will download to:\n{BaseColorMaskEngine.ModelPath}",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#607090")),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MonospaceFont,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = "Base Color Masks from Sketch",
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    content,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 6,
                        Children =
                        {
                            new Button
                            {
                                Content = "Cancel",
                                Padding = new Thickness(16, 7), FontSize = 11,
                                Classes = { "outline" }
                            },
                            new Button
                            {
                                Content = "Generate",
                                Padding = new Thickness(16, 7), FontSize = 11,
                                Classes = { "primary" }
                            }
                        }
                    }
                }
            },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            MinWidth = 400
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[^1]);
        var cancelBtn = (Button)buttons.Children[0];
        var generateBtn = (Button)buttons.Children[1];

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        generateBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(this);
        if (!await tcs.Task) return;

        BaseColorMaskEngine.GrantConsent();

        using (var modelBusy = BeginBusy(BaseColorMaskEngine.ModelFileExists
            ? "Loading anime-segmentation model…"
            : "Downloading anime-segmentation model…"))
        {
            if (!await BaseColorMaskEngine.EnsureModelReadyAsync())
            {
                await ShowMessage("Anime Segmentation Unavailable",
                    BaseColorMaskEngine.LastError
                    ?? "Could not download or load isnetis.onnx.");
                return;
            }
        }

        using var busy = BeginBusy("Generating character mask…");
        var generation = await System.Threading.Tasks.Task.Run(() =>
        {
            var bitmap = DocumentRasterizer.RenderFlattenedBitmap(_canvas.Document);
            var w = bitmap.Width;
            var h = bitmap.Height;
            var raw = new byte[w * h * 4];
            Marshal.Copy(bitmap.GetPixels(), raw, 0, raw.Length);
            bitmap.Dispose();
            return BaseColorMaskEngine.GenerateMasks(raw, w, h);
        });

        var masks = generation.Masks;

        if (generation.AnimeSeg != AnimeSegStatus.Applied || masks.Count == 0)
        {
            var message = generation.AnimeSeg switch
            {
                AnimeSegStatus.ModelMissing => "Anime model file was not found.",
                AnimeSegStatus.ModelLoadFailed => BaseColorMaskEngine.LastError ?? "Anime model failed to load.",
                AnimeSegStatus.InferenceFailed => BaseColorMaskEngine.LastError ?? "Anime inference failed.",
                AnimeSegStatus.NoForegroundDetected => "No character silhouettes were detected in this image.",
                _ => "Mask generation failed."
            };
            await ShowMessage("No Masks Generated", message);
            return;
        }

        // Import mask below the sketch layer
        var insertIdx = Math.Max(0, _canvas.ActiveLayerIndex);
        var w = _canvas.Document.Width;
        var h = _canvas.Document.Height;
        for (var i = 0; i < masks.Count; i++)
        {
            var layer = new DrawingLayer("Base Color", w, h);
            layer.Pixels.CopyFromBgra(masks[i], w, h);
            layer.MarkThumbnailDirty();
            _canvas.Document.InsertAndSelectLayer(layer, insertIdx);
        }
        // Re-select the sketch layer so it sits above the fill
        _canvas.Document.SelectLayer(insertIdx + masks.Count);
        _canvas.InvalidateVisual();
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

    private async Task ApplyOneSliderFilter(
        string title,
        string label,
        double min,
        double max,
        double value,
        Func<double, string> format,
        Func<float, SelectionMask, Action<DrawingLayer>> actionFactory)
    {
        var preview = new PreviewHandle();
        var valueLabel = FilterValueLabel(format(value));
        var slider = FilterSlider(min, max, value);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLabel.Text = format(slider.Value);
            preview.Schedule();
        };

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () => actionFactory((float)slider.Value, sel);
        var ok = await ShowFilterDialog(title, FilterRow(FilterLabel(label), slider, valueLabel), LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        _canvas.ApplyFilter(EffectiveLayerSelection(), buildPreview());
        BuildLayerList();
    }

    private async Task ApplyTwoSliderFilter(
        string title,
        string label1,
        double min1,
        double max1,
        double value1,
        Func<double, string> format1,
        string label2,
        double min2,
        double max2,
        double value2,
        Func<double, string> format2,
        Func<float, float, SelectionMask, Action<DrawingLayer>> actionFactory)
    {
        var preview = new PreviewHandle();
        var valueLabel1 = FilterValueLabel(format1(value1));
        var valueLabel2 = FilterValueLabel(format2(value2));
        var slider1 = FilterSlider(min1, max1, value1);
        var slider2 = FilterSlider(min2, max2, value2);

        slider1.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLabel1.Text = format1(slider1.Value);
            preview.Schedule();
        };
        slider2.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLabel2.Text = format2(slider2.Value);
            preview.Schedule();
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(FilterRow(FilterLabel(label1), slider1, valueLabel1));
        panel.Children.Add(FilterRow(FilterLabel(label2), slider2, valueLabel2));

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () => actionFactory((float)slider1.Value, (float)slider2.Value, sel);
        var ok = await ShowFilterDialog(title, panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        _canvas.ApplyFilter(EffectiveLayerSelection(), buildPreview());
        BuildLayerList();
    }

    private async Task ApplyThreeSliderFilter(
        string title,
        string label1,
        double min1,
        double max1,
        double value1,
        Func<double, string> format1,
        string label2,
        double min2,
        double max2,
        double value2,
        Func<double, string> format2,
        string label3,
        double min3,
        double max3,
        double value3,
        Func<double, string> format3,
        Func<float, float, float, SelectionMask, Action<DrawingLayer>> actionFactory)
    {
        var preview = new PreviewHandle();
        var valueLabel1 = FilterValueLabel(format1(value1));
        var valueLabel2 = FilterValueLabel(format2(value2));
        var valueLabel3 = FilterValueLabel(format3(value3));
        var slider1 = FilterSlider(min1, max1, value1);
        var slider2 = FilterSlider(min2, max2, value2);
        var slider3 = FilterSlider(min3, max3, value3);

        slider1.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLabel1.Text = format1(slider1.Value);
            preview.Schedule();
        };
        slider2.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLabel2.Text = format2(slider2.Value);
            preview.Schedule();
        };
        slider3.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            valueLabel3.Text = format3(slider3.Value);
            preview.Schedule();
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(FilterRow(FilterLabel(label1), slider1, valueLabel1));
        panel.Children.Add(FilterRow(FilterLabel(label2), slider2, valueLabel2));
        panel.Children.Add(FilterRow(FilterLabel(label3), slider3, valueLabel3));

        var sel = _canvas.Selection;
        Func<Action<DrawingLayer>> buildPreview = () => actionFactory((float)slider1.Value, (float)slider2.Value, (float)slider3.Value, sel);
        var ok = await ShowFilterDialog(title, panel, LayerSelectionLabel(), buildPreview, preview);
        if (!ok) return;

        _canvas.ApplyFilter(EffectiveLayerSelection(), buildPreview());
        BuildLayerList();
    }

    private void ApplyInstantFilter(Action<DrawingLayer> action)
    {
        _canvas.ApplyFilter(EffectiveLayerSelection(), action);
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

        // Capture pixels so we can revert during/after preview.
        // We MUST snapshot the region alongside the bytes because unbounded
        // painting can expand Pixels.Bounds after capture, and RestorePixels
        // would then try to restore a larger region than the byte array holds.
        var captured = new Dictionary<int, (PixelRegion Region, byte[] Bytes)>();
        if (buildPreview != null)
        {
            foreach (var idx in layers)
            {
                if (idx >= 0 && idx < _canvas.Layers.Count)
                {
                    var layer = _canvas.Layers[idx];
                    if (!layer.IsGroup && !layer.IsLocked)
                    {
                        var region = layer.Pixels.Bounds;
                        captured[idx] = (region, layer.CapturePixels(region));
                    }
                }
            }
        }

        var previewOn = buildPreview != null;
        DispatcherTimer? debounce = null;

        PixelRegion FilterPreviewDirtyRegion()
        {
            var region = PixelRegion.Empty;
            foreach (var idx in layers)
            {
                if (idx >= 0 && idx < _canvas.Layers.Count)
                    region = region.Union(_canvas.Document.GetLayerDirtyRegion(idx));
            }
            return region.ClipTo(_canvas.Document.Width, _canvas.Document.Height);
        }

        void ApplyPreviewNow()
        {
            if (!previewOn || buildPreview == null) return;
            var action = buildPreview();
            foreach (var (idx, (region, pixels)) in captured)
            {
                _canvas.Layers[idx].RestorePixels(region, pixels);
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
            var dirty = FilterPreviewDirtyRegion();
            if (!dirty.IsEmpty)
                _canvas.Document.NotifyChanged(dirty, layers.Count == 1 ? layers[0] : null);
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
            foreach (var (idx, (region, pixels)) in captured)
            {
                _canvas.Layers[idx].RestorePixels(region, pixels);
                _canvas.Layers[idx].MarkThumbnailDirty();
            }
            if (captured.Count > 0)
            {
                var dirty = FilterPreviewDirtyRegion();
                if (!dirty.IsEmpty)
                    _canvas.Document.NotifyChanged(dirty, layers.Count == 1 ? layers[0] : null);
            }
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
                Padding = new Thickness(16, 7),
                FontSize = 11,
                Classes = { "outline" }
            };
            previewBtn.Click += (_, _) =>
            {
                previewOn = !previewOn;
                previewBtn.Background = new SolidColorBrush(previewOn ? Color.Parse(AccentSoft) : Colors.Transparent);
                previewBtn.Foreground = new SolidColorBrush(Color.Parse(previewOn ? TextPrimary : TextSecondary));
                if (previewOn) ApplyPreviewNow();
                else RevertPreview();
            };
        }

        // ── Action buttons ────────────────────────────────────────────────────
        var applyBtn = new Button
        {
            Content = applyLabel,
            Padding = new Thickness(16, 7),
            FontSize = 11,
            Classes = { "primary" }
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 7),
            FontSize = 11,
            Classes = { "outline" }
        };

        // Preview on left, Cancel+Apply on right
        var footerGrid = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnSpacing = 8 };
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

        dialog.Opened += (_, _) => { if (previewOn) ApplyPreviewNow(); };

        // Non-modal: canvas stays interactive for pan/zoom while adjusting filter params.
        _canvas.EnterFilterPreviewSession();
        try
        {
            dialog.Show(this);
            return await tcs.Task;
        }
        finally
        {
            _canvas.ExitFilterPreviewSession();
        }
    }

    private async Task ShowMessage(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)) },
                    new Button
                    {
                        Content = "OK", Padding = new Thickness(20, 5), FontSize = 11,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Classes = { "primary" }
                    }
                }
            },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            MinWidth = 250,
            MaxWidth = 500
        };
        var btn = (Button)((StackPanel)dialog.Content).Children[1];
        btn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(true);
        await dialog.ShowDialog(this);
        await tcs.Task;
    }
}
