using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Floss.App.Config;
using Floss.App.Document;

namespace Floss.App.Windows;

using static AppColors;

/// <summary>Pen-position layer chooser shown while Ctrl+Shift is held on canvas.</summary>
public sealed class LayerPickerPopup : Window
{
    private const double MaxListHeight = 360;
    private const double RowMinWidth = 220;

    private readonly Action<int> _onPick;

    public LayerPickerPopup(
        DrawingDocument document,
        IReadOnlyList<int> layerIndices,
        int activeLayerIndex,
        Action<int> onPick,
        Action<IReadOnlyList<int>>? onSelectAll = null)
    {
        _onPick = onPick;

        var layers = document.Layers;
        var entries = layerIndices
            .Where(i => i >= 0 && i < layers.Count)
            .Select(i => (Index: i, Layer: layers[i]))
            .ToList();

        var list = new StackPanel { Spacing = 2 };
        foreach (var (index, layer) in entries)
            list.Children.Add(BuildRow(index, layer, index == activeLayerIndex, document));

        var scroll = new ScrollViewer
        {
            MaxHeight = MaxListHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list,
        };

        var headerText = entries.Count == 1
            ? "Layer at sample point"
            : $"{entries.Count} layers in sample area";

        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Margin = new Thickness(2, 0, 0, 4),
        };

        var root = new StackPanel
        {
            Spacing = 4,
            MinWidth = RowMinWidth,
            Children = { header, scroll },
        };

        if (entries.Count > 1 && onSelectAll != null)
        {
            var selectAllBtn = new Button
            {
                Content = $"Select all {entries.Count} layers",
                Padding = new Thickness(10, 5),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Classes = { "outline" },
            };
            selectAllBtn.Click += (_, _) =>
            {
                onSelectAll(entries.Select(e => e.Index).ToList());
                Close();
            };
            root.Children.Add(selectAllBtn);
        }

        Content = new Border
        {
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Accent)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = root,
        };

        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Avalonia.Media.Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

    }

    public void ShowNear(PixelPoint screenAnchor, PixelRect workArea)
    {
        const int offset = 14;
        Show();
        var w = (int)Math.Ceiling(Width);
        var h = (int)Math.Ceiling(Height);
        var x = screenAnchor.X + offset;
        var y = screenAnchor.Y + offset;
        if (x + w > workArea.Right) x = screenAnchor.X - w - offset;
        if (y + h > workArea.Bottom) y = screenAnchor.Y - h - offset;
        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - w));
        y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - h));
        Position = new PixelPoint(x, y);
        Activate();
    }

    private Control BuildRow(int index, DrawingLayer layer, bool isActive, DrawingDocument document)
    {
        var row = new Border
        {
            Padding = new Thickness(6, 4),
            Margin = new Thickness(layer.IndentLevel * 10, 0, 0, 0),
            CornerRadius = new CornerRadius(5),
            Background = isActive
                ? new SolidColorBrush(Color.Parse(SelectionBgActive))
                : new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = isActive
                ? new SolidColorBrush(Color.Parse(SelectionBorder))
                : new SolidColorBrush(Color.Parse(StrokeSubtle)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var thumb = BuildThumbnail(layer, isActive, document);
        var name = new TextBlock
        {
            Text = layer.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? TextPrimary : TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var kind = new TextBlock
        {
            Text = LayerKindLabel(layer),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var textStack = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, kind },
        };

        row.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("auto,*"),
            ColumnSpacing = 8,
            Children =
            {
                thumb,
                textStack,
            },
        };
        Grid.SetColumn(textStack, 1);

        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            _onPick(index);
            Close();
            e.Handled = true;
        };

        return row;
    }

    private static Control BuildThumbnail(DrawingLayer layer, bool highlighted, DrawingDocument document)
    {
        var (tw, th) = DrawingLayer.ComputeThumbnailPixelSize(layer.Width, layer.Height);
        var frame = new Border
        {
            Width = Math.Max(28, tw),
            Height = Math.Max(28, th),
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
        };

        if (layer.IsGroup)
        {
            var iconSize = Math.Min(20, Math.Min(tw, th));
            frame.Child = Icons.Make(
                layer.IsOpen ? Icons.FolderOpenOutline : Icons.Folder,
                iconSize,
                new SolidColorBrush(Color.Parse(highlighted ? TextPrimary : TextMuted)));
            return frame;
        }

        if (layer.IsPaper)
        {
            var pc = document.PaperColor;
            frame.Child = new Border { Background = new SolidColorBrush(pc) };
            return frame;
        }

        if (layer.IsObjectLayer)
        {
            frame.Child = Icons.Make(Icons.RectangleOutline, 18,
                new SolidColorBrush(Color.Parse(TextMuted)));
            return frame;
        }

        frame.Child = new Image
        {
            Source = layer.GetThumbnail(),
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapInterpolationMode(frame, BitmapInterpolationMode.None);
        return frame;
    }

    private static string LayerKindLabel(DrawingLayer layer) => layer switch
    {
        { IsGroup: true } => "Group",
        { IsPaper: true } => "Paper",
        { IsObjectLayer: true } => "Object",
        { Adjustment: not null } => "Adjustment",
        _ => "Paint",
    };

}
