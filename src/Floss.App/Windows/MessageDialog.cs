using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App.Windows;

using static Floss.App.Config.AppColors;

public static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
                    },
                    new Button
                    {
                        Content = "OK",
                        Padding = new Thickness(20, 5),
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Classes = { "primary" },
                    },
                },
            },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            MinWidth = 250,
            MaxWidth = 500,
        };

        var btn = (Button)((StackPanel)dialog.Content).Children[1];
        btn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(true);
        await dialog.ShowDialog(owner);
        await tcs.Task;
    }
}
