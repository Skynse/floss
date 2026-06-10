using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Config;
using Floss.App.Document;
using Floss.App.Features;
using Floss.App.Features.Session;
using Floss.App.ImageFiles;
using Floss.App.Windows;
using SkiaSharp;

namespace Floss.AnimeMaskPlugin;

internal static class AnimeMaskCommands
{
    public static async Task RunGeneratorAsync(IFeatureSession session)
    {
        var shell = session.GetService<ISessionShell>();
        var canvas = session.ActiveCanvas;
        var document = session.ActiveDocument;

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = "Uses SkyTNT anime-segmentation (isnet_is) on the selected layer to detect character silhouettes\nand create a base-color mask layer below it.\n\nThe model (~168 MB) downloads once and runs locally.",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#7080a0")),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = BaseColorMaskEngine.ModelFileExists
                ? $"Model: {BaseColorMaskEngine.ModelPath}"
                : $"Model will download to:\n{BaseColorMaskEngine.ModelPath}",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse("#607090")),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Margin = new Thickness(0, 4, 0, 0),
        });

        var maskFillColor = BaseColorMaskEngine.DefaultMaskFillColor;
        var maskColorSwatch = new Border
        {
            Width = 32,
            Height = 24,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse(AppColors.Stroke)),
            Background = new SolidColorBrush(maskFillColor),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var maskColorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        maskColorRow.Children.Add(new TextBlock
        {
            Text = "Mask color",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(AppColors.TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        maskColorRow.Children.Add(maskColorSwatch);
        content.Children.Add(maskColorRow);

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
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 6,
                        Children =
                        {
                            new Button
                            {
                                Content = "Cancel",
                                Padding = new Thickness(16, 7),
                                FontSize = 11,
                                Classes = { "outline" },
                            },
                            new Button
                            {
                                Content = "Generate",
                                Padding = new Thickness(16, 7),
                                FontSize = 11,
                                Classes = { "primary" },
                            },
                        },
                    },
                },
            },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse(AppColors.Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(AppColors.TextSecondary)),
            MinWidth = 400,
        };

        var buttons = (StackPanel)((StackPanel)dialog.Content).Children[^1];
        var cancelBtn = (Button)buttons.Children[0];
        var generateBtn = (Button)buttons.Children[1];

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        generateBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        maskColorSwatch.PointerPressed += async (_, _) =>
        {
            var picker = new ColorPickerWindow(maskFillColor, c =>
            {
                maskFillColor = c;
                maskColorSwatch.Background = new SolidColorBrush(c);
            });
            await picker.ShowDialog(dialog);
        };

        await dialog.ShowDialog(shell.Owner);
        if (!await tcs.Task)
            return;

        var activeLayer = document.ActiveLayer;
        if (activeLayer is not { IsPaper: false, IsObjectLayer: false })
        {
            await shell.ShowMessageAsync("No Layer Selected",
                "Select a paint or group layer to use as the sketch input.");
            return;
        }

        var selectedMaskColor = maskFillColor;
        BaseColorMaskEngine.GrantConsent();

        using (shell.BeginBusy(BaseColorMaskEngine.ModelFileExists
            ? "Loading anime-segmentation model…"
            : "Downloading anime-segmentation model…"))
        {
            if (!await BaseColorMaskEngine.EnsureModelReadyAsync())
            {
                await shell.ShowMessageAsync("Anime Segmentation Unavailable",
                    BaseColorMaskEngine.LastError ?? "Could not download or load isnetis.onnx.");
                return;
            }
        }

        using (shell.BeginBusy("Generating character mask…"))
        {
            var inputLayer = activeLayer;
            var generation = await Task.Run(() =>
            {
                using var bitmap = DocumentRasterizer.RenderLayerBitmap(document, inputLayer);
                var w = bitmap.Width;
                var h = bitmap.Height;
                var raw = new byte[w * h * 4];
                Marshal.Copy(bitmap.GetPixels(), raw, 0, raw.Length);
                return BaseColorMaskEngine.GenerateMasks(raw, w, h, selectedMaskColor);
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
                    _ => "Mask generation failed.",
                };
                await shell.ShowMessageAsync("No Masks Generated", message);
                return;
            }

            var insertIdx = Math.Max(0, canvas.ActiveLayerIndex);
            var docW = document.Width;
            var docH = document.Height;
            for (var i = 0; i < masks.Count; i++)
            {
                var layer = new DrawingLayer("Base Color", docW, docH);
                layer.Pixels.CopyFromBgra(masks[i], docW, docH);
                layer.MarkThumbnailDirty();
                document.InsertAndSelectLayer(layer, insertIdx);
            }

            document.SelectLayer(insertIdx + masks.Count);
            canvas.InvalidateVisual();
        }
    }
}
