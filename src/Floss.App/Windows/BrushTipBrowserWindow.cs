using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using SkiaSharp;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

public sealed class BrushTipBrowserWindow : Window
{
    private readonly Action<IBrushTip> _onSelect;

    public BrushTipBrowserWindow(Window? owner, Action<IBrushTip> onSelect)
    {
        _onSelect = onSelect;

        Width = 400;
        Height = 480;
        CanResize = true;
        MinWidth = 320;
        MinHeight = 360;
        Title = "Brush Tips";

        CustomWindowChrome.ConfigurePopup(this);
        Content = CustomWindowChrome.Wrap(this, Title, BuildContent());
    }

    private Control BuildContent()
    {
        // Header
        var importBtn = SmBtn("Import PNG");
        importBtn.Click += async (_, _) => await ImportPngAsync();

        var header = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(importBtn, Dock.Right);
        header.Children.Add(MkHeader("Select a tip"));
        header.Children.Add(importBtn);

        // Procedural section
        var procGrid = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };
        foreach (var shape in Enum.GetValues<BrushTipShape>())
        {
            var btn = MakeTipBtn(
                RenderShapePreview(shape),
                shape.ToString(),
                () => Select(new ProceduralBrushTip(shape)));
            procGrid.Children.Add(btn);
        }

        // Image section
        var imgGrid = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var pngFiles = new List<string>();
        try
        {
            if (Directory.Exists(AppPaths.BrushTipsDirectory))
                pngFiles.AddRange(Directory.EnumerateFiles(AppPaths.BrushTipsDirectory, "*.png"));
        }
        catch (Exception ex) { CrashLog.Write(ex, "BrushTipBrowserWindow.LoadBrushes (enum)"); }

        foreach (var path in pngFiles.OrderBy(Path.GetFileName))
        {
            Bitmap? bitmap = null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                bitmap = new Bitmap(ms);
            }
            catch (Exception ex) { CrashLog.Write(ex, $"BrushTipBrowserWindow.LoadBrushes (load {path})"); continue; }

            var localPath = path;
            var btn = MakeTipBtn(bitmap, Path.GetFileNameWithoutExtension(localPath),
                () => Select(new ImageBrushTip(localPath)));
            imgGrid.Children.Add(btn);
        }

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);
        stack.Children.Add(MkHeader("Procedural"));
        stack.Children.Add(procGrid);

        if (imgGrid.Children.Count > 0)
        {
            stack.Children.Add(new Border { Height = 12 });
            stack.Children.Add(MkHeader("Images"));
            stack.Children.Add(imgGrid);
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Padding = new Thickness(10, 8, 10, 8),
            Content = stack
        };
    }

    private Control MakeTipBtn(Bitmap? bitmap, string label, Action onClick)
    {
        var img = new Image
        {
            Source = bitmap,
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxWidth = 60
        };

        var content = new StackPanel
        {
            Spacing = 2,
            Children = { img, lbl }
        };

        var btn = new Button
        {
            Width = 72,
            Height = 78,
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 4, 4),
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void Select(IBrushTip tip)
    {
        _onSelect(tip);
        Close();
    }

    private async Task ImportPngAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PNG brush tip",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var mem = new MemoryStream();
        await stream.CopyToAsync(mem);
        var bytes = mem.ToArray();

        try
        {
            var name = files[0].Name;
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name += ".png";
            var destPath = Path.Combine(AppPaths.BrushTipsDirectory, name);
            await File.WriteAllBytesAsync(destPath, bytes);
        }
        catch (Exception ex) { CrashLog.Write(ex, "BrushTipBrowserWindow.ImportPng"); }

        Select(new ImageBrushTip(bytes));
    }

    private static Bitmap? RenderShapePreview(BrushTipShape shape)
    {
        const int size = 64;
        var tip = new ProceduralBrushTip(shape, shape == BrushTipShape.Ellipse ? 2.4f : 1.0f);
        var mask = tip.GenerateMask(48, 0.85f);
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Fill
        };

        canvas.DrawBitmap(mask, (size - mask.Width) * 0.5f, (size - mask.Height) * 0.5f, paint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    private static TextBlock MkHeader(string text) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin = new Thickness(0, 0, 0, 4),
        LetterSpacing = 1.0
    };

    private static Button SmBtn(string label)
    {
        var b = new Button
        {
            Content = label,
            Height = 20,
            Padding = new Thickness(6, 0),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "outline" }
        };
        return b;
    }
}
