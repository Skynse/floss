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

public record DocumentSettings(string FileName, int Width, int Height, int Dpi, SKColor BackgroundColor);

public sealed class NewDocumentDialog : Window
{
    private readonly TextBox _nameInput;
    private readonly NumericUpDown _widthInput;
    private readonly NumericUpDown _heightInput;
    private readonly NumericUpDown _dpiInput;
    private readonly ComboBox _bgDropdown;
    private readonly ComboBox _templateDropdown;
    private readonly Button _saveTemplateBtn;
    private readonly Button _deleteTemplateBtn;

    private List<DocumentTemplate> _customTemplates;
    private bool _applyingTemplate;

    private static readonly string[] BgOptions =
        ["White", "Warm Off-White", "Transparent", "Dark Mode"];

    public NewDocumentDialog()
    {
        Title = "New Document";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg2));
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary));

        _customTemplates = DocumentTemplateStore.LoadCustom();

        _nameInput = MkTextBox("Untitled");

        _widthInput = MkNud((decimal)App.Config.NewCanvasWidth, 1, 16000);
        _heightInput = MkNud((decimal)App.Config.NewCanvasHeight, 1, 16000);
        _dpiInput = MkNud((decimal)App.Config.NewCanvasDpi, 1, 1200);

        var savedBgIdx = Array.IndexOf(BgOptions, App.Config.NewCanvasBackground);
        _bgDropdown = new ComboBox
        {
            ItemsSource = BgOptions,
            SelectedIndex = savedBgIdx >= 0 ? savedBgIdx : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12
        };

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

        Content = BuildShell();
    }

    private void RebuildTemplateDropdown(int selectIndex = -1, string? selectName = null)
    {
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
        if (_applyingTemplate) return;
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
        UpdateDeleteButton();
    }

    private async void OnSaveTemplate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var suggested = _nameInput.Text?.Trim() is { Length: > 0 } n ? n : "My Template";
        var w   = (int)(_widthInput.Value  ?? 1920);
        var h   = (int)(_heightInput.Value ?? 1080);
        var dpi = (int)(_dpiInput.Value    ?? 72);
        var bg  = BgOptions[Math.Clamp(_bgDropdown.SelectedIndex, 0, BgOptions.Length - 1)];

        var tpl = await new SaveTemplateDialog(suggested, w, h, dpi, bg).ShowDialog<DocumentTemplate?>(this);
        if (tpl == null) return;

        // Avoid shadowing a built-in name
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
        var idx = _templateDropdown.SelectedIndex;
        int builtInCount = DocumentTemplateStore.BuiltIn.Count;
        if (idx <= builtInCount) return; // can't delete built-ins or "Custom"

        var customIdx = idx - 1 - builtInCount;
        if (customIdx < 0 || customIdx >= _customTemplates.Count) return;

        _customTemplates.RemoveAt(customIdx);
        DocumentTemplateStore.SaveCustom(_customTemplates);
        RebuildTemplateDropdown(selectIndex: 0);
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        var idx = _templateDropdown.SelectedIndex;
        int builtInCount = DocumentTemplateStore.BuiltIn.Count;
        _deleteTemplateBtn.IsEnabled = idx > builtInCount;
    }

    private Control BuildShell()
    {
        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(20, 18, 20, 20)
        };

        stack.Children.Add(new TextBlock
        {
            Text = "New Document",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Template row
        stack.Children.Add(SectionLabel("TEMPLATE"));
        var templateRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(_saveTemplateBtn, Dock.Right);
        DockPanel.SetDock(_deleteTemplateBtn, Dock.Right);
        templateRow.Children.Add(_saveTemplateBtn);
        templateRow.Children.Add(new Border { Width = 4 });
        templateRow.Children.Add(_deleteTemplateBtn);
        templateRow.Children.Add(new Border { Width = 4 });
        templateRow.Children.Add(_templateDropdown);
        stack.Children.Add(templateRow);

        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(SectionLabel("DOCUMENT"));

        // Name
        stack.Children.Add(FieldRow("Name", _nameInput));

        // Width/Height side by side
        var sizeRow = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition(80, GridUnitType.Pixel));
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition(16, GridUnitType.Pixel));
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var wLabel = FieldLabel("Width");
        var hLabel = FieldLabel("Height");
        Grid.SetColumn(wLabel, 0);
        Grid.SetColumn(_widthInput, 1);
        Grid.SetColumn(hLabel, 2);
        Grid.SetColumn(_heightInput, 3);

        var pxLabel = new TextBlock { Text = "px", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(TextMuted)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        hLabel.Width = 0;
        hLabel.Margin = new Thickness(8, 0, 4, 0);

        sizeRow.Children.Add(wLabel);
        sizeRow.Children.Add(_widthInput);
        sizeRow.Children.Add(hLabel);
        sizeRow.Children.Add(_heightInput);
        stack.Children.Add(sizeRow);

        stack.Children.Add(FieldRow("DPI", _dpiInput));
        stack.Children.Add(FieldRow("Background", _bgDropdown));

        // Swap orientation button
        var swapBtn = new Button
        {
            Content = "⇅  Swap W/H",
            Height = 24,
            Padding = new Thickness(8, 0),
            FontSize = 11,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        swapBtn.Click += (_, _) => (_widthInput.Value, _heightInput.Value) = (_heightInput.Value, _widthInput.Value);
        stack.Children.Add(swapBtn);

        stack.Children.Add(new Border { Height = 14 });

        // Buttons
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 88,
            Height = 30,
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        cancelBtn.Click += (_, _) => Close(null);

        var createBtn = new Button
        {
            Content = "Create",
            Width = 88,
            Height = 30,
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Accent)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        createBtn.Click += (_, _) => OnCreate();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelBtn, createBtn }
        };
        stack.Children.Add(btnRow);

        return stack;
    }

    private void OnCreate()
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

        App.Config.NewCanvasWidth = w;
        App.Config.NewCanvasHeight = h;
        App.Config.NewCanvasDpi = dpi;
        App.Config.NewCanvasBackground = BgOptions[Math.Clamp(_bgDropdown.SelectedIndex, 0, BgOptions.Length - 1)];
        App.Config.Save();

        Close(new DocumentSettings(
            FileName: _nameInput.Text?.Trim() is { Length: > 0 } n ? n : "Untitled",
            Width: w,
            Height: h,
            Dpi: dpi,
            BackgroundColor: bgColor));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NumericUpDown MkNud(decimal value, decimal min, decimal max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FontSize = 12,
        MinHeight = 28,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBox MkTextBox(string text) => new()
    {
        Text = text,
        FontSize = 12,
        MinHeight = 28,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static Button SmBtn(string label) => new()
    {
        Content = label,
        Height = 26,
        Padding = new Thickness(8, 0),
        FontSize = 11,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(Bg1)),
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3)
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin = new Thickness(0, 0, 0, 2),
        LetterSpacing = 1.2
    };

    private static Control FieldRow(string label, Control input)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(FieldLabel(label), Dock.Left);
        row.Children.Add(FieldLabel(label));
        row.Children.Add(input);
        return row;
    }

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Width = 80,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        VerticalAlignment = VerticalAlignment.Center
    };
}
