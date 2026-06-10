using Avalonia.Controls;
using Floss.App.Features.Menu;

namespace Floss.App.Tests;

public class MenuRegistryTests
{
    [Fact]
    public void Register_AddsItemToPath()
    {
        var registry = new MenuRegistry();
        registry.Register(new MenuItemRegistration
        {
            Id = "test.item",
            Path = "Plugins",
            Header = "Test",
            Click = () => { }
        });

        var items = registry.GetItems("Plugins");
        Assert.Single(items);
        Assert.Equal("test.item", items[0].Id);
    }

    [Fact]
    public void RefreshAnchors_InjectsMenuItem()
    {
        var registry = new MenuRegistry();
        var plugins = new MenuItem { Header = "Plugins", ItemsSource = new List<object>() };
        registry.BindMenus(new Dictionary<string, MenuItem> { ["Plugins"] = plugins });
        registry.Register(new MenuItemRegistration
        {
            Id = "hello",
            Path = "Plugins",
            Header = "Hello",
            Click = () => { }
        });

        registry.Refresh();

        Assert.Single(plugins.Items);
        Assert.Equal("hello", (plugins.Items[0] as MenuItem)?.Tag);
    }
}
