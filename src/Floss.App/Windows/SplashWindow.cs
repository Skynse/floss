using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Floss.App.Windows;

using static Floss.App.AppColors;

public sealed class SplashWindow : Window
{
    private readonly TextBlock _statusText;

    public SplashWindow()
    {
        Title = "Floss";
        Width = 420;
        Height = 220;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse(Bg0));
        TransparencyLevelHint = [WindowTransparencyLevel.None];

        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Floss.App/Assets/icon.png")));

        using var iconStream = AssetLoader.Open(new Uri("avares://Floss.App/Assets/icon.png"));
        var icon = new Image
        {
            Source = new Bitmap(iconStream),
            Width = 72,
            Height = 72,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = "Floss",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var version = new TextBlock
        {
            Text = VersionText(),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _statusText = new TextBlock
        {
            Text = "Starting...",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(28, 24),
            Child = new StackPanel
            {
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    icon,
                    title,
                    version,
                    new Border { Height = 8, Background = Avalonia.Media.Brushes.Transparent },
                    _statusText
                }
            }
        };
    }

    public void SetStatus(string status)
    {
        _statusText.Text = status;
    }

    private static string VersionText()
    {
        try
        {
            var version = typeof(SplashWindow).Assembly.GetName().Version;
            return version == null ? "Digital painting workspace" : $"Version {version}";
        }
        catch
        {
            return "Digital painting workspace";
        }
    }
}
