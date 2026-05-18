using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Document;

namespace Floss.App;

using static AppColors;

public sealed class SaveTemplateDialog : Window
{
    private readonly TextBox _nameInput;
    private readonly CheckBox _sizeCheck;
    private readonly CheckBox _dpiCheck;
    private readonly CheckBox _bgCheck;

    private readonly int _currentWidth;
    private readonly int _currentHeight;
    private readonly int _currentDpi;
    private readonly string _currentBg;

    public SaveTemplateDialog(string suggestedName, int width, int height, int dpi, string background)
    {
        _currentWidth  = width;
        _currentHeight = height;
        _currentDpi    = dpi;
        _currentBg     = background;

        Title = "Save as Template";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse(Bg2));
        Foreground = new SolidColorBrush(Color.Parse(TextPrimary));

        _nameInput = new TextBox
        {
            Text = suggestedName,
            FontSize = 12,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _sizeCheck = MkCheck($"Canvas size  ({width} × {height} px)", true);
        _dpiCheck  = MkCheck($"Resolution  ({dpi} DPI)", true);
        _bgCheck   = MkCheck($"Background  ({background})", false);

        Content = BuildShell();
    }

    private Control BuildShell()
    {
        var nameRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(new TextBlock
        {
            Text = "Template name:",
            FontSize = 11,
            Width = 110,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        }, Dock.Left);
        nameRow.Children.Add(new TextBlock
        {
            Text = "Template name:",
            FontSize = 11,
            Width = 110,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center
        });
        nameRow.Children.Add(_nameInput);

        var itemsGroup = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    new TextBlock
                    {
                        Text = "SETTINGS TO INCLUDE",
                        FontSize = 9,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                        LetterSpacing = 1.2,
                        Margin = new Thickness(0, 0, 0, 6)
                    },
                    _sizeCheck,
                    _dpiCheck,
                    _bgCheck
                }
            }
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 28,
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        cancelBtn.Click += (_, _) => Close(null);

        var okBtn = new Button
        {
            Content = "OK",
            Width = 80,
            Height = 28,
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Accent)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        okBtn.Click += (_, _) => OnOk();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelBtn, okBtn }
        };

        return new StackPanel
        {
            Margin = new Thickness(18, 16, 18, 18),
            Spacing = 0,
            Children = { nameRow, itemsGroup, btnRow }
        };
    }

    private void OnOk()
    {
        var name = _nameInput.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = "My Template";

        var tpl = new DocumentTemplate { Name = name };

        if (_sizeCheck.IsChecked == true)
        {
            tpl.Width  = _currentWidth;
            tpl.Height = _currentHeight;
        }
        if (_dpiCheck.IsChecked == true)
            tpl.Dpi = _currentDpi;
        if (_bgCheck.IsChecked == true)
            tpl.Background = _currentBg;

        Close(tpl);
    }

    private static CheckBox MkCheck(string label, bool isChecked) => new()
    {
        Content = label,
        IsChecked = isChecked,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
        Margin = new Thickness(0, 3, 0, 3)
    };
}
