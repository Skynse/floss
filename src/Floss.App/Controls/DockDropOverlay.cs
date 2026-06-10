using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App.Controls;

using static Config.AppColors;

/// <summary>
/// Full-window overlay for docker drag-drop feedback (insertion line, tab target, hint).
/// </summary>
public sealed class DockDropOverlay : Avalonia.Controls.Canvas
{
    private readonly Border _insertLine;
    private readonly Border _insertLineVertical;
    private readonly Border _tabHighlight;
    private readonly Border _hintCard;
    private readonly TextBlock _hintText;

    public DockDropOverlay()
    {
        IsHitTestVisible = false;
        ZIndex = 5000;

        _insertLine = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.Parse(Accent)),
            IsVisible = false,
            IsHitTestVisible = false
        };

        _insertLineVertical = new Border
        {
            Width = 2,
            Background = new SolidColorBrush(Color.Parse(Accent)),
            IsVisible = false,
            IsHitTestVisible = false
        };

        _tabHighlight = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(SelectionBorder)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(40, 90, 159, 216)),
            CornerRadius = new CornerRadius(0),
            IsVisible = false,
            IsHitTestVisible = false
        };

        _hintText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            TextWrapping = TextWrapping.NoWrap
        };

        _hintCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 50, 50, 50)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 5),
            Child = _hintText,
            IsVisible = false,
            IsHitTestVisible = false
        };

        Children.Add(_tabHighlight);
        Children.Add(_insertLine);
        Children.Add(_insertLineVertical);
        Children.Add(_hintCard);
    }

    public void Clear()
    {
        _insertLine.IsVisible = false;
        _insertLineVertical.IsVisible = false;
        _tabHighlight.IsVisible = false;
        _hintCard.IsVisible = false;
    }

    public void ShowInsertLine(double x, double y, double width)
    {
        _tabHighlight.IsVisible = false;
        _insertLineVertical.IsVisible = false;
        _insertLine.IsVisible = true;
        SetLeft(_insertLine, x);
        SetTop(_insertLine, y - 1);
        _insertLine.Width = Math.Max(40, width);
    }

    public void ShowInsertLineVertical(double x, double y, double height)
    {
        _tabHighlight.IsVisible = false;
        _insertLine.IsVisible = false;
        _insertLineVertical.IsVisible = true;
        SetLeft(_insertLineVertical, x - 1);
        SetTop(_insertLineVertical, y);
        _insertLineVertical.Height = Math.Max(40, height);
    }

    public void ShowTabTarget(double x, double y, double width, double height)
    {
        _insertLine.IsVisible = false;
        _insertLineVertical.IsVisible = false;
        _tabHighlight.IsVisible = true;
        SetLeft(_tabHighlight, x);
        SetTop(_tabHighlight, y);
        _tabHighlight.Width = Math.Max(40, width);
        _tabHighlight.Height = Math.Clamp(height, 22, TabStripHeight + 4);
    }

    public void ShowHint(string text, double x, double y)
    {
        _hintText.Text = text;
        _hintCard.IsVisible = true;
        SetLeft(_hintCard, x + 14);
        SetTop(_hintCard, y + 14);
    }

    public const double TabStripHeight = 28;
}
