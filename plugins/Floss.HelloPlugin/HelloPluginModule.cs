using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Config;
using Floss.App.Docking;
using Floss.App.Features;
using Floss.App.Features.Dock;
using Avalonia.Input;
using Floss.App.Features.Actions;
using Floss.App.Features.Menu;
using Floss.App.Features.Session;
using Floss.App.Features.Overlays;
using Floss.App.Features.Tools;
using Avalonia.Media;

namespace Floss.HelloPlugin;

/// <summary>Sample runtime plugin — menu item + docker panel.</summary>
public sealed class HelloPluginModule : IFeatureModule
{
    public const string PanelId = "hello-plugin";
    private static int _toolFactoryCalls;

    public void Register(IFeatureSession session)
    {
        var menus = session.GetService<IMenuRegistry>();
        menus.Register(new MenuItemRegistration
        {
            Id = "hello-plugin.greet",
            Path = "Plugins",
            Header = "_Greet from Hello Plugin...",
            Order = 0,
            Click = () => ShowGreeting(session)
        });

        menus.Register(new MenuItemRegistration
        {
            Id = "hello-plugin.window",
            Path = "Window",
            Header = "Hello Plugin _Info",
            Order = 2000,
            Click = () => ShowGreeting(session)
        });

        var actions = session.GetService<IActionRegistry>();
        actions.Register(new ActionRegistration
        {
            Id = "hello-plugin.greet-shortcut",
            Title = "Greet from Hello Plugin",
            Gesture = new KeyGesture(Key.F9),
            Order = 0,
            Execute = () => ShowGreeting(session)
        });

        var tools = session.GetService<IToolRegistry>();
        tools.RegisterFactory(ctx =>
        {
            Interlocked.Increment(ref _toolFactoryCalls);
            return null;
        }, order: 5000);

        var overlays = session.GetService<ICanvasOverlayRegistry>();
        overlays.Register(new HelloCornerOverlay());

        DockFeature.Register(
            PanelId,
            "Hello Plugin",
            () => new HelloPluginPanel(session),
            defaultZone: "right-0",
            proportion: 0.12,
            minHeight: 80,
            sizing: DockPanelSizing.Fill);
    }

    private sealed class HelloCornerOverlay : ICanvasOverlay
    {
        public int Order => 5000;

        public void Render(CanvasOverlayContext context)
        {
            var margin = 12 / Math.Max(context.Zoom, 0.001);
            var size = 24 / Math.Max(context.Zoom, 0.001);
            var rect = new Rect(margin, margin, size, size);
            context.DrawingContext.DrawRectangle(
                null,
                new Pen(Brushes.CornflowerBlue, 2 / Math.Max(context.Zoom, 0.001)),
                rect);
        }

        public bool TryHandlePointer(CanvasOverlayPointerEvent pointerEvent) => false;
    }

    private static void ShowGreeting(IFeatureSession session)
    {
        var shell = session.GetService<ISessionShell>();
        shell.FooterStatusText.Text =
            $"Hello from plugin — layers: {session.ActiveDocument.Layers.Count}, " +
            $"tool factory probes: {_toolFactoryCalls}";
    }

    private sealed class HelloPluginPanel : ContentControl
    {
        private readonly TextBlock _eventsLine;

        public HelloPluginPanel(IFeatureSession session)
        {
            _eventsLine = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#aac4e8")),
                TextWrapping = TextWrapping.Wrap,
                Text = "Document events: (idle)"
            };

            var events = session.GetService<IDocumentEvents>();
            events.StructureChanged += () => SetEvent("structure");
            events.SelectionChanged += () => SetEvent("selection");
            events.HistoryChanged += () => SetEvent("history");
            events.ViewportChanged += () => SetEvent("viewport");

            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2d3a4a")),
                Padding = new Thickness(10),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Hello Plugin",
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brushes.White
                        },
                        new TextBlock
                        {
                            Text = $"Loaded from {Path.Combine(AppPaths.PluginsDirectory, "Floss.HelloPlugin.dll")}",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#aac4e8")),
                            TextWrapping = TextWrapping.Wrap
                        },
                        _eventsLine,
                        new Button
                        {
                            Content = "Run layer blur (session API)",
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                        }
                    }
                }
            };

            if (Content is Border { Child: StackPanel { Children: { Count: >= 4 } } stack }
                && stack.Children[3] is Button btn)
            {
                btn.Click += async (_, _) =>
                {
                    var layers = session.GetService<ILayerCommands>();
                    await layers.ApplyBlurFilter();
                };
            }
        }

        private void SetEvent(string name)
            => _eventsLine.Text = $"Document events: {name} @ {DateTime.Now:HH:mm:ss}";
    }
}
