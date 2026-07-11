using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App;

using static AppColors;

public enum DocumentPropertiesMode { New, Edit }

public record DocumentSettings(string FileName, int Width, int Height, int Dpi, SKColor BackgroundColor, bool RecordTimelapse);

/// <summary>Initial values when editing an open document (Edit → Canvas Information).</summary>
public readonly record struct CanvasPropertiesInitial(
    int Width, int Height, int Dpi, Avalonia.Media.Color PaperColor);

public sealed class DocumentPropertiesDialog : Window
{
    private readonly DocumentPropertiesMode _mode;
    private readonly TextBox? _nameInput;
    private readonly NumericUpDown _widthInput;
    private readonly NumericUpDown _heightInput;
    private readonly NumericUpDown _dpiInput;
    private readonly ComboBox _bgDropdown;
    private readonly ComboBox? _templateDropdown;
    private readonly Button? _saveTemplateBtn;
    private readonly Button? _deleteTemplateBtn;
    private readonly CheckBox? _recordTimelapseCheckBox;
    private readonly Border _previewFrame;
    private readonly Border _previewInner;

    private List<DocumentTemplate> _customTemplates;
    private bool _applyingTemplate;

    private static readonly string[] BgOptions =
        ["White", "Warm Off-White", "Transparent", "Dark Mode"];

    public DocumentPropertiesDialog(DocumentPropertiesMode mode, CanvasPropertiesInitial? initial = null)
    {
        _mode = mode;
        Title = mode == DocumentPropertiesMode.New ? "New Document" : "Canvas Properties";
        Width = mode == DocumentPropertiesMode.New ? 620 : 720;
        MinWidth = Width;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg2));
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary));
        Padding = new Thickness(0);

        _customTemplates = DocumentTemplateStore.LoadCustom();

        if (mode == DocumentPropertiesMode.New)
            _nameInput = MkTextBox("Untitled");
        else
            _nameInput = null;

        if (initial is { } init)
        {
            _widthInput = MkNud(init.Width, 1, 16000);
            _heightInput = MkNud(init.Height, 1, 16000);
            _dpiInput = MkNud(init.Dpi, 1, 1200);
            var bgIdx = PaperColorToBgIndex(init.PaperColor);
            _bgDropdown = MkBgDropdown(bgIdx);
        }
        else
        {
            _widthInput = MkNud((decimal)App.Config.NewCanvasWidth, 1, 16000);
            _heightInput = MkNud((decimal)App.Config.NewCanvasHeight, 1, 16000);
            _dpiInput = MkNud((decimal)App.Config.NewCanvasDpi, 1, 1200);
            var savedBgIdx = Array.IndexOf(BgOptions, App.Config.NewCanvasBackground);
            _bgDropdown = MkBgDropdown(savedBgIdx >= 0 ? savedBgIdx : 0);
        }

        _widthInput.ValueChanged += (_, _) => UpdatePreviewAspect();
        _heightInput.ValueChanged += (_, _) => UpdatePreviewAspect();

        if (mode == DocumentPropertiesMode.New)
        {
            _templateDropdown = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12
            };
            RebuildTemplateDropdown(selectIndex: 0);
            _templateDropdown.SelectionChanged += OnTemplateSelected;

            _saveTemplateBtn = SmBtn("Save as template");
            _saveTemplateBtn.Click += OnSaveTemplate;

            _deleteTemplateBtn = SmBtn("Delete");
            _deleteTemplateBtn.Click += OnDeleteTemplate;
            UpdateDeleteButton();

            _recordTimelapseCheckBox = new CheckBox
            {
                Content = "Record timelapse",
                IsChecked = App.Config.RecordTimelapse,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                Margin = new Thickness(0, 8, 0, 0)
            };
        }
        else
        {
            _templateDropdown = null;
            _saveTemplateBtn = null;
            _deleteTemplateBtn = null;
            _recordTimelapseCheckBox = null;
        }

        _previewInner = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _previewFrame = new Border
        {
            Width = 148,
            Height = 148,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Child = _previewInner
        };
        UpdatePreviewAspect();

        Content = BuildShell();
    }

    private ComboBox MkBgDropdown(int selectedIndex) => new()
    {
        ItemsSource = BgOptions,
        SelectedIndex = Math.Clamp(selectedIndex, 0, BgOptions.Length - 1),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        FontSize = 12
    };

    private static int PaperColorToBgIndex(Avalonia.Media.Color c)
    {
        if (c.A == 0) return Array.IndexOf(BgOptions, "Transparent");
        if (c.R == 21 && c.G == 23 && c.B == 25 && c.A == 255) return Array.IndexOf(BgOptions, "Dark Mode");
        if (c.R == 247 && c.G == 244 && c.B == 237) return Array.IndexOf(BgOptions, "Warm Off-White");
        return 0;
    }

    private void UpdatePreviewAspect()
    {
        var w = Math.Max(1, (int)(_widthInput.Value ?? 1));
        var h = Math.Max(1, (int)(_heightInput.Value ?? 1));
        const double max = 112;
        var scale = Math.Min(max / w, max / h);
        _previewInner.Width = Math.Max(8, w * scale);
        _previewInner.Height = Math.Max(8, h * scale);
        var bgIdx = Math.Clamp(_bgDropdown.SelectedIndex, 0, BgOptions.Length - 1);
        _previewInner.Background = new SolidColorBrush(BgOptions[bgIdx] switch
        {
            "Warm Off-White" => Color.FromRgb(247, 244, 237),
            "Transparent" => Color.FromArgb(0, 0, 0, 0),
            "Dark Mode" => Color.FromRgb(21, 23, 25),
            _ => Colors.White
        });
    }

    private void RebuildTemplateDropdown(int selectIndex = -1, string? selectName = null)
    {
        if (_templateDropdown == null) return;
        var items = new List<string> { "Custom" };
        items.AddRange(DocumentTemplateStore.BuiltIn.Select(t => t.Name));
        items.AddRange(_customTemplates.Select(t => "★ " + t.Name));
        _templateDropdown.ItemsSource = items;

        if (selectName != null)
        {
            var idx = items.IndexOf("★ " + selectName);
            _templateDropdown.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else
            _templateDropdown.SelectedIndex = Math.Clamp(selectIndex, 0, items.Count - 1);
    }

    private void OnTemplateSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingTemplate || _templateDropdown == null) return;
        var idx = _templateDropdown.SelectedIndex;
        if (idx <= 0) { UpdateDeleteButton(); return; }

        DocumentTemplate? tpl;
        int builtInCount = DocumentTemplateStore.BuiltIn.Count;
        if (idx <= builtInCount)
            tpl = DocumentTemplateStore.BuiltIn[idx - 1];
        else
            tpl = _customTemplates.ElementAtOrDefault(idx - 1 - builtInCount);

        if (tpl == null) { UpdateDeleteButton(); return; }

        _applyingTemplate = true;
        if (tpl.Width.HasValue)  _widthInput.Value  = (decimal)tpl.Width.Value;
        if (tpl.Height.HasValue) _heightInput.Value = (decimal)tpl.Height.Value;
        if (tpl.Dpi.HasValue)    _dpiInput.Value    = (decimal)tpl.Dpi.Value;
        if (tpl.Background != null)
        {
            var bgIdx = Array.IndexOf(BgOptions, tpl.Background);
            if (bgIdx >= 0) _bgDropdown.SelectedIndex = bgIdx;
        }
        _applyingTemplate = false;
        UpdatePreviewAspect();
        UpdateDeleteButton();
    }

    private async void OnSaveTemplate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_saveTemplateBtn == null) return;
        var suggested = _nameInput?.Text?.Trim() is { Length: > 0 } n ? n : "My Template";
        var w   = (int)(_widthInput.Value  ?? 1920);
        var h   = (int)(_heightInput.Value ?? 1080);
        var dpi = (int)(_dpiInput.Value    ?? 72);
        var bg  = BgOptions[Math.Clamp(_bgDropdown.SelectedIndex, 0, BgOptions.Length - 1)];

        var tpl = await new SaveTemplateDialog(suggested, w, h, dpi, bg).ShowDialog<DocumentTemplate?>(this);
        if (tpl == null) return;

        if (DocumentTemplateStore.BuiltIn.Any(t => t.Name.Equals(tpl.Name, StringComparison.OrdinalIgnoreCase)))
            tpl.Name += " (Custom)";

        var existing = _customTemplates.FirstOrDefault(t =>
            t.Name.Equals(tpl.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _customTemplates[_customTemplates.IndexOf(existing)] = tpl;
        else
            _customTemplates.Add(tpl);

        DocumentTemplateStore.SaveCustom(_customTemplates);
        RebuildTemplateDropdown(selectName: tpl.Name);
        UpdateDeleteButton();
    }

    private void OnDeleteTemplate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_templateDropdown == null) return;
        var idx = _templateDropdown.SelectedIndex;
        int builtInCount = DocumentTemplateStore.BuiltIn.Count;
        if (idx <= builtInCount) return;

        var customIdx = idx - 1 - builtInCount;
        if (customIdx < 0 || customIdx >= _customTemplates.Count) return;

        _customTemplates.RemoveAt(customIdx);
        DocumentTemplateStore.SaveCustom(_customTemplates);
        RebuildTemplateDropdown(selectIndex: 0);
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        if (_templateDropdown == null || _deleteTemplateBtn == null) return;
        var idx = _templateDropdown.SelectedIndex;
        int builtInCount = DocumentTemplateStore.BuiltIn.Count;
        _deleteTemplateBtn.IsEnabled = idx > builtInCount;
    }

    private Control BuildShell()
    {
        var cancelBtn = MkDialogButton("Cancel", primary: false);
        cancelBtn.Click += (_, _) => Close(null);

        var okLabel = _mode == DocumentPropertiesMode.New ? "Create" : "OK";
        var okBtn = MkDialogButton(okLabel, primary: true);
        okBtn.Click += (_, _) => OnConfirm();

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(cancelBtn, Dock.Right);
        DockPanel.SetDock(okBtn, Dock.Right);
        header.Children.Add(cancelBtn);
        header.Children.Add(new Border { Width = 8 });
        header.Children.Add(okBtn);
        header.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        });

        var fields = new StackPanel { Spacing = 0, MinWidth = 400 };

        if (_mode == DocumentPropertiesMode.New && _templateDropdown != null)
        {
            fields.Children.Add(SectionLabel("TEMPLATE"));
            var templateRow = new Grid
            {
                Margin = new Thickness(0, 4, 0, 6),
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition(1, GridUnitType.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                }
            };
            Grid.SetColumn(_templateDropdown, 0);
            Grid.SetColumn(_deleteTemplateBtn!, 1);
            Grid.SetColumn(_saveTemplateBtn!, 2);
            templateRow.Children.Add(_templateDropdown);
            templateRow.Children.Add(_deleteTemplateBtn!);
            templateRow.Children.Add(_saveTemplateBtn!);
            fields.Children.Add(templateRow);
            fields.Children.Add(new Border { Height = 8 });
        }

        fields.Children.Add(SectionLabel(_mode == DocumentPropertiesMode.New ? "DOCUMENT" : "CANVAS"));

        if (_nameInput != null)
            fields.Children.Add(FieldRow("Name", _nameInput));

        fields.Children.Add(BuildSizeRow());
        fields.Children.Add(BuildResolutionRow());
        fields.Children.Add(FieldRow("Paper color", _bgDropdown));

        _bgDropdown.SelectionChanged += (_, _) => UpdatePreviewAspect();

        if (_recordTimelapseCheckBox != null)
        {
            _recordTimelapseCheckBox.Margin = new Thickness(0, 10, 0, 0);
            fields.Children.Add(_recordTimelapseCheckBox);
        }

        var swapBtn = new Button
        {
            Content = "⇅  Swap W/H",
            Height = 28,
            MinWidth = 108,
            Padding = new Thickness(10, 0),
            FontSize = 11,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        swapBtn.Click += (_, _) =>
        {
            (_widthInput.Value, _heightInput.Value) = (_heightInput.Value, _widthInput.Value);
            UpdatePreviewAspect();
        };
        fields.Children.Add(swapBtn);

        var unitLabel = new TextBlock
        {
            Text = "Unit: px",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Margin = new Thickness(0, 12, 0, 2)
        };
        fields.Children.Add(unitLabel);

        var body = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 400 },
                new ColumnDefinition(168, GridUnitType.Pixel)
            },
            ColumnSpacing = 24
        };
        Grid.SetColumn(fields, 0);
        Grid.SetColumn(_previewFrame, 1);
        _previewFrame.Margin = new Thickness(0, 22, 0, 0);
        _previewFrame.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(fields);
        body.Children.Add(_previewFrame);

        var root = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(22, 18, 22, 22),
            Children = { header, body }
        };

        return root;
    }

    private Control BuildSizeRow()
    {
        const int labelCol = 88;
        var row = new Grid
        {
            Margin = new Thickness(0, 6, 0, 6),
            ColumnSpacing = 12
        };
        row.ColumnDefinitions.Add(new ColumnDefinition(labelCol, GridUnitType.Pixel));
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 132 });
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 132 });

        var wLabel = FieldLabel("Width", labelCol);
        var hLabel = FieldLabel("Height", width: 52);
        hLabel.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(wLabel, 0);
        Grid.SetColumn(_widthInput, 1);
        Grid.SetColumn(hLabel, 2);
        Grid.SetColumn(_heightInput, 3);
        row.Children.Add(wLabel);
        row.Children.Add(_widthInput);
        row.Children.Add(hLabel);
        row.Children.Add(_heightInput);
        return row;
    }

    private Control BuildResolutionRow()
    {
        const int labelCol = 88;
        var row = new Grid
        {
            Margin = new Thickness(0, 6, 0, 6),
            ColumnSpacing = 12
        };
        row.ColumnDefinitions.Add(new ColumnDefinition(labelCol, GridUnitType.Pixel));
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 132 });
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var dpiSuffix = new TextBlock
        {
            Text = "DPI",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };

        var label = FieldLabel("Resolution", labelCol);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(_dpiInput, 1);
        Grid.SetColumn(dpiSuffix, 2);
        row.Children.Add(label);
        row.Children.Add(_dpiInput);
        row.Children.Add(dpiSuffix);
        return row;
    }

    private void OnConfirm()
    {
        var bgIdx = Math.Clamp(_bgDropdown.SelectedIndex, 0, BgOptions.Length - 1);
        SKColor bgColor = BgOptions[bgIdx] switch
        {
            "Warm Off-White" => new SKColor(247, 244, 237),
            "Transparent"    => SKColors.Transparent,
            "Dark Mode"      => new SKColor(21, 23, 25),
            _                => SKColors.White
        };

        var w = Math.Max(1, (int)(_widthInput.Value ?? 1920));
        var h = Math.Max(1, (int)(_heightInput.Value ?? 1080));
        var dpi = Math.Max(1, (int)(_dpiInput.Value ?? 72));

        if (_mode == DocumentPropertiesMode.New)
        {
            App.Config.NewCanvasWidth = w;
            App.Config.NewCanvasHeight = h;
            App.Config.NewCanvasDpi = dpi;
            App.Config.NewCanvasBackground = BgOptions[bgIdx];
            App.Config.RecordTimelapse = _recordTimelapseCheckBox?.IsChecked == true;
            App.Config.Save();
        }

        Close(new DocumentSettings(
            FileName: _nameInput?.Text?.Trim() is { Length: > 0 } n ? n : "Untitled",
            Width: w,
            Height: h,
            Dpi: dpi,
            BackgroundColor: bgColor,
            RecordTimelapse: _recordTimelapseCheckBox?.IsChecked == true));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NumericUpDown MkNud(decimal value, decimal min, decimal max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FontSize = 12,
        MinHeight = 30,
        // Spinner buttons eat ~28px; keep enough room for 5-digit values like 16000.
        MinWidth = 132,
        Padding = new Thickness(8, 4, 4, 4),
        FormatString = "0",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static TextBox MkTextBox(string text) => new()
    {
        Text = text,
        FontSize = 12,
        MinHeight = 30,
        Padding = new Thickness(8, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Button SmBtn(string label) => new()
    {
        Content = label,
        Height = 30,
        MinHeight = 30,
        Padding = new Thickness(12, 0),
        FontSize = 11,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(Bg1)),
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    private static Button MkDialogButton(string label, bool primary) => new()
    {
        Content = label,
        MinWidth = 80,
        Width = double.NaN,
        Height = 30,
        Padding = new Thickness(14, 0),
        FontSize = 12,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(primary ? Accent : Bg1)),
        Foreground = new SolidColorBrush(primary ? Colors.White : Color.Parse(TextSecondary)),
        BorderBrush = primary ? null : new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = primary ? new Thickness(0) : new Thickness(1),
        CornerRadius = new CornerRadius(4)
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin = new Thickness(0, 2, 0, 6),
        LetterSpacing = 1.2
    };

    private static Control FieldRow(string label, Control input)
    {
        const int labelCol = 88;
        var row = new Grid
        {
            Margin = new Thickness(0, 6, 0, 6),
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(labelCol, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star)
            }
        };
        var lbl = FieldLabel(label, labelCol);
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(input, 1);
        row.Children.Add(lbl);
        row.Children.Add(input);
        return row;
    }

    private static TextBlock FieldLabel(string text, double width = 88) => new()
    {
        Text = text,
        FontSize = 11,
        Width = width,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.None
    };
}
