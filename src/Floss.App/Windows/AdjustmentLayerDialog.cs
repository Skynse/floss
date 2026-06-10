using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Controls;
using Floss.App.Controls;
using Floss.App.Document;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;
using static Floss.App.FilterControls;

/// <summary>
/// Non-destructive correction layer parameter editor.
/// Live-previews changes via a callback; OK commits, Cancel reverts.
/// </summary>
public sealed class AdjustmentLayerDialog : Window
{
    private readonly AdjustmentLayerData _original;
    private AdjustmentLayerData _working;
    private readonly Action<AdjustmentLayerData> _preview;
    private bool _committed;

    public AdjustmentLayerData? Result { get; private set; }

    public AdjustmentLayerDialog(AdjustmentLayerData current, Action<AdjustmentLayerData> livePreview)
    {
        _original = current.Clone();
        _working = current.Clone();
        _preview = livePreview;

        Title = AdjustmentLayerData.DisplayName(current.Kind);
        SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight;
        MinWidth = 380;
        CanResize = false;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.Parse(Bg1));

        var content = BuildContent();

        var okBtn = MakeButton("OK", "primary");
        okBtn.Click += (_, _) => { _committed = true; Result = _working.Clone(); Close(); };
        var cancelBtn = MakeButton("Cancel", "outline");
        cancelBtn.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8)
        };
        footer.Children.Add(cancelBtn);
        footer.Children.Add(okBtn);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(content, 0);
        Grid.SetRow(footer, 1);
        root.Children.Add(content);
        root.Children.Add(footer);
        Content = root;

        Closing += (_, _) =>
        {
            if (!_committed)
                _preview(_original);
        };
    }

    private static Button MakeButton(string text, string cls)
    {
        var btn = new Button
        {
            Content = text,
            Width = 72,
            Height = 26,
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        btn.Classes.Add(cls);
        return btn;
    }

    private Control BuildContent()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12, 14, 4) };

        // Build a filter-style slider row wired to get/set on _working.
        Control AdjRow(string label, double min, double max, Func<float> get, Action<float> set, string fmt = "0")
        {
            var lbl = FilterLabel(label);
            var sl  = FilterSlider(min, max, get());
            var vl  = FilterValueLabel(string.Format($"{{0:{fmt}}}", get()));
            sl.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                vl.Text = string.Format($"{{0:{fmt}}}", sl.Value);
                set((float)sl.Value);
            };
            return FilterRow(lbl, sl, vl);
        }

        switch (_working.Kind)
        {
            case AdjustmentKind.BrightnessContrast:
                panel.Children.Add(AdjRow("Brightness", -255, 255, () => _working.Brightness, v => { _working.Brightness = v; _preview(_working); }));
                panel.Children.Add(AdjRow("Contrast",   -100, 100, () => _working.Contrast,   v => { _working.Contrast   = v; _preview(_working); }));
                break;

            case AdjustmentKind.HueSaturationLuminosity:
                panel.Children.Add(AdjRow("Hue",        -180, 180, () => _working.Hue,        v => { _working.Hue        = v; _preview(_working); }));
                panel.Children.Add(AdjRow("Saturation", -100, 100, () => _working.Saturation, v => { _working.Saturation = v; _preview(_working); }));
                panel.Children.Add(AdjRow("Luminosity", -100, 100, () => _working.Luminosity, v => { _working.Luminosity = v; _preview(_working); }));
                break;

            case AdjustmentKind.Posterization:
                panel.Children.Add(AdjRow("Levels", 2, 16, () => _working.Levels, v => { _working.Levels = (int)v; _preview(_working); }));
                break;

            case AdjustmentKind.LevelCorrection:
                panel.Children.Add(SectionLabel("Input"));
                panel.Children.Add(AdjRow("Black", 0, 255, () => _working.LevelInBlack,  v => { _working.LevelInBlack  = v; _preview(_working); }));
                panel.Children.Add(AdjRow("White", 0, 255, () => _working.LevelInWhite,  v => { _working.LevelInWhite  = v; _preview(_working); }));
                panel.Children.Add(AdjRow("Gamma", 0, 10,  () => _working.LevelGamma,    v => { _working.LevelGamma    = v; _preview(_working); }, "F2"));
                panel.Children.Add(SectionLabel("Output"));
                panel.Children.Add(AdjRow("Black", 0, 255, () => _working.LevelOutBlack, v => { _working.LevelOutBlack = v; _preview(_working); }));
                panel.Children.Add(AdjRow("White", 0, 255, () => _working.LevelOutWhite, v => { _working.LevelOutWhite = v; _preview(_working); }));
                break;

            case AdjustmentKind.ToneCurve:
                BuildToneCurveContent(panel);
                break;

            case AdjustmentKind.ColorBalance:
                panel.Children.Add(SectionLabel("Shadows"));
                panel.Children.Add(AdjRow("R", -100, 100, () => _working.ShadowR, v => { _working.ShadowR = v; _preview(_working); }));
                panel.Children.Add(AdjRow("G", -100, 100, () => _working.ShadowG, v => { _working.ShadowG = v; _preview(_working); }));
                panel.Children.Add(AdjRow("B", -100, 100, () => _working.ShadowB, v => { _working.ShadowB = v; _preview(_working); }));
                panel.Children.Add(SectionLabel("Midtones"));
                panel.Children.Add(AdjRow("R", -100, 100, () => _working.MidtoneR, v => { _working.MidtoneR = v; _preview(_working); }));
                panel.Children.Add(AdjRow("G", -100, 100, () => _working.MidtoneG, v => { _working.MidtoneG = v; _preview(_working); }));
                panel.Children.Add(AdjRow("B", -100, 100, () => _working.MidtoneB, v => { _working.MidtoneB = v; _preview(_working); }));
                panel.Children.Add(SectionLabel("Highlights"));
                panel.Children.Add(AdjRow("R", -100, 100, () => _working.HighlightR, v => { _working.HighlightR = v; _preview(_working); }));
                panel.Children.Add(AdjRow("G", -100, 100, () => _working.HighlightG, v => { _working.HighlightG = v; _preview(_working); }));
                panel.Children.Add(AdjRow("B", -100, 100, () => _working.HighlightB, v => { _working.HighlightB = v; _preview(_working); }));
                break;

            case AdjustmentKind.Binarization:
                panel.Children.Add(AdjRow("Threshold", 0, 255, () => _working.Threshold, v => { _working.Threshold = v; _preview(_working); }));
                break;

            case AdjustmentKind.GradientMap:
                BuildGradientMapContent(panel);
                break;

            case AdjustmentKind.ReverseGradient:
                panel.Children.Add(new TextBlock
                {
                    Text = "Inverts all pixel colors. No parameters.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
                });
                break;
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            MaxHeight = 520,
            Content = panel
        };
        return scroll;
    }

    // ── Tone Curve ────────────────────────────────────────────────────────────

    private void BuildToneCurveContent(StackPanel panel)
    {
        var channels = new[] { "RGB", "R", "G", "B" };
        var graphs = new CurveGraph[4];

        // CurveGraph uses 0..1 space; AdjustmentLayerData uses 0..255 — convert on load
        float[] SrcPts(float[] pts255)
        {
            var n = new float[pts255.Length];
            for (var i = 0; i < pts255.Length; i++) n[i] = pts255[i] / 255f;
            return n;
        }
        float[] ToPts255(float[] pts01)
        {
            var n = new float[pts01.Length];
            for (var i = 0; i < pts01.Length; i++) n[i] = pts01[i] * 255f;
            return n;
        }

        graphs[0] = new CurveGraph { Width = 280, Height = 200 };
        graphs[0].CurvePoints = SrcPts(_working.CurveAll);
        graphs[1] = new CurveGraph { Width = 280, Height = 200 };
        graphs[1].CurvePoints = SrcPts(_working.CurveR);
        graphs[2] = new CurveGraph { Width = 280, Height = 200 };
        graphs[2].CurvePoints = SrcPts(_working.CurveG);
        graphs[3] = new CurveGraph { Width = 280, Height = 200 };
        graphs[3].CurvePoints = SrcPts(_working.CurveB);

        var graphHost = new ContentControl { Content = graphs[0] };
        var channelBtns = new Button[4];

        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            graphs[i].CurveChanged += (_, args) =>
            {
                var pts = ToPts255(args.CurvePoints);
                switch (idx)
                {
                    case 0: _working.CurveAll = pts; break;
                    case 1: _working.CurveR = pts; break;
                    case 2: _working.CurveG = pts; break;
                    case 3: _working.CurveB = pts; break;
                }
                _preview(_working);
            };

            var label = channels[i];
            var btn = new Button
            {
                Content = label,
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

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var btn in channelBtns) btnRow.Children.Add(btn);

        panel.Children.Add(btnRow);
        panel.Children.Add(graphHost);
    }

    // ── Gradient Map ─────────────────────────────────────────────────────────

    private void BuildGradientMapContent(StackPanel panel)
    {
        // Gradient stops: flat [pos,r,g,b, pos,r,g,b, ...] in 0..1
        var stops = ParseGradientStops(_working.GradientStops);
        var stopList = new StackPanel { Spacing = 4 };
        var gradBar = new Border { Height = 20, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 6) };

        void RebuildGradBar()
        {
            if (stops.Count == 0) { gradBar.Background = new SolidColorBrush(Colors.Transparent); return; }
            var gs = new GradientStops();
            foreach (var s in stops)
                gs.Add(new GradientStop(Color.FromRgb((byte)(s.R * 255), (byte)(s.G * 255), (byte)(s.B * 255)), s.Pos));
            gradBar.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = gs
            };
        }

        void CommitStops()
        {
            stops.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            _working.GradientStops = SerializeGradientStops(stops);
            RebuildGradBar();
            _preview(_working);
        }

        void RebuildStopList()
        {
            stopList.Children.Clear();
            for (var si = 0; si < stops.Count; si++)
            {
                var idx = si;
                var stop = stops[si];

                var posSlider = ScrubSliderFactory.Create(0, 1, stop.Pos);
                posSlider.Width = 120;
                var posLabel = new TextBlock
                {
                    Text = $"{stop.Pos:F2}",
                    FontSize = 11, Width = 34,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse(TextPrimary))
                };
                posSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                    stops[idx] = stops[idx] with { Pos = (float)posSlider.Value };
                    posLabel.Text = $"{posSlider.Value:F2}";
                    CommitStops();
                };

                var colorSwatch = new Border
                {
                    Width = 24, Height = 20,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
                    Background = new SolidColorBrush(Color.FromRgb(
                        (byte)(stop.R * 255), (byte)(stop.G * 255), (byte)(stop.B * 255))),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };

                colorSwatch.PointerPressed += (_, _) =>
                {
                    var s = stops[idx];
                    var picker = new ColorPickerWindow(
                        Color.FromRgb((byte)(s.R * 255), (byte)(s.G * 255), (byte)(s.B * 255)),
                        c =>
                        {
                            stops[idx] = stops[idx] with { R = c.R / 255f, G = c.G / 255f, B = c.B / 255f };
                            colorSwatch.Background = new SolidColorBrush(c);
                            CommitStops();
                        });
                    picker.Show(this);
                };

                var removeBtn = new Button
                {
                    Content = "×", FontSize = 12, Width = 22, Height = 22, Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsEnabled = stops.Count > 2
                };
                removeBtn.Classes.Add("outline");
                removeBtn.Click += (_, _) =>
                {
                    if (stops.Count <= 2) return;
                    stops.RemoveAt(idx);
                    CommitStops();
                    RebuildStopList();
                };

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(colorSwatch);
                row.Children.Add(posSlider);
                row.Children.Add(posLabel);
                row.Children.Add(removeBtn);
                stopList.Children.Add(row);
            }

            var addBtn = new Button { Content = "+ Add Stop", FontSize = 11, Height = 24, Padding = new Thickness(8, 0) };
            addBtn.Classes.Add("outline");
            addBtn.Click += (_, _) =>
            {
                stops.Add(new GradStop(0.5f, 0.5f, 0.5f, 0.5f));
                CommitStops();
                RebuildStopList();
            };
            stopList.Children.Add(addBtn);
        }

        RebuildGradBar();
        RebuildStopList();
        panel.Children.Add(gradBar);
        panel.Children.Add(stopList);
    }

    private static List<GradStop> ParseGradientStops(float[] raw)
    {
        var list = new List<GradStop>();
        for (var i = 0; i + 3 < raw.Length; i += 4)
            list.Add(new GradStop(raw[i], raw[i + 1], raw[i + 2], raw[i + 3]));
        if (list.Count < 2)
        {
            list.Clear();
            list.Add(new GradStop(0f, 0f, 0f, 0f));
            list.Add(new GradStop(1f, 1f, 1f, 1f));
        }
        return list;
    }

    private static float[] SerializeGradientStops(List<GradStop> stops)
    {
        var arr = new float[stops.Count * 4];
        for (var i = 0; i < stops.Count; i++)
        {
            arr[i * 4]     = stops[i].Pos;
            arr[i * 4 + 1] = stops[i].R;
            arr[i * 4 + 2] = stops[i].G;
            arr[i * 4 + 3] = stops[i].B;
        }
        return arr;
    }

    private readonly record struct GradStop(float Pos, float R, float G, float B);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        Margin = new Thickness(0, 4, 0, 0)
    };

}
