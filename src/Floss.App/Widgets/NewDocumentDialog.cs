using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;

namespace Floss.App; // Update to your actual namespace

public record DocumentSettings(string FileName, int Width, int Height, SKColor BackgroundColor);

public sealed class NewDocumentDialog : Window
{
    private readonly TextBox _nameInput;
    private readonly NumericUpDown _widthInput;
    private readonly NumericUpDown _heightInput;
    private readonly ComboBox _bgDropdown;

    public NewDocumentDialog()
    {
        Title = "New Document";
        Width = 350;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse("#202226"));
        Foreground = new SolidColorBrush(Color.Parse("#d8dbe0"));

        // Initialize inputs
        _nameInput = new TextBox { Text = "Untitled", Margin = new Thickness(0, 0, 0, 10) };
        _widthInput = new NumericUpDown { Value = App.Config.NewCanvasWidth, Minimum = 1, Maximum = 50001, Margin = new Thickness(0, 0, 0, 10) };
        _heightInput = new NumericUpDown { Value = App.Config.NewCanvasHeight, Minimum = 1, Maximum = 50001, Margin = new Thickness(0, 0, 0, 10) };

        _bgDropdown = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "White Paper", "Warm Off-White", "Transparent", "Dark Mode" },
            SelectedIndex = 0
        };

        Content = BuildShell();
    }

    private Control BuildShell()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("100, *"),
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, Auto"),
            Margin = new Thickness(0, 10)
        };

        // Helper to populate grid rows
        void AddRow(int row, string label, Control input)
        {
            var textBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(textBlock, row);
            Grid.SetColumn(textBlock, 0);

            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);

            grid.Children.Add(textBlock);
            grid.Children.Add(input);
        }

        AddRow(0, "Name:", _nameInput);
        AddRow(1, "Width (px):", _widthInput);
        AddRow(2, "Height (px):", _heightInput);
        AddRow(3, "Paper:", _bgDropdown);

        // Action Buttons
        var cancelBtn = new Button { Content = "Cancel", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancelBtn.Click += (_, _) => Close(null);

        var createBtn = new Button
        {
            Content = "Create",
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#2f7bff")),
            Foreground = new SolidColorBrush(Color.Parse("#ffffff"))
        };
        createBtn.Click += (_, _) => OnCreateClicked();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { cancelBtn, createBtn }
        };

        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Create New Canvas", FontSize = 18, FontWeight = FontWeight.Bold },
                grid,
                buttonPanel
            }
        };
    }

    private void OnCreateClicked()
    {
        // Parse the background selection into a Skia color
        SKColor bgColor = _bgDropdown.SelectedIndex switch
        {
            0 => SKColors.White,
            1 => new SKColor(247, 244, 237), // Warm Off-white
            2 => SKColors.Transparent,
            3 => new SKColor(21, 23, 25),    // Dark mode
            _ => SKColors.White
        };

        var result = new DocumentSettings(
            FileName: _nameInput.Text ?? "Untitled",
            Width: (int)(_widthInput.Value ?? 1920),
            Height: (int)(_heightInput.Value ?? 1080),
            BackgroundColor: bgColor
        );

        Close(result);
    }
}
