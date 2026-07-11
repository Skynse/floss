using Floss.App.Features;

namespace Floss.App.Tests;

public class PluginApiTests
{
    [Fact]
    public void PluginApiVersion_CurrentIsNotNullOrEmpty()
    {
        Assert.False(string.IsNullOrEmpty(PluginApiVersion.Current));
    }

    [Fact]
    public void PluginApiVersion_NullVersionIsCompatible()
    {
        Assert.True(PluginApiVersion.IsCompatible(null));
        Assert.True(PluginApiVersion.IsCompatible(""));
    }

    [Fact]
    public void PluginApiVersion_MatchingVersionIsCompatible()
    {
        Assert.True(PluginApiVersion.IsCompatible(PluginApiVersion.Current));
    }

    [Fact]
    public void PluginApiVersion_MismatchedVersionIsIncompatible()
    {
        Assert.False(PluginApiVersion.IsCompatible("0.9"));
        Assert.False(PluginApiVersion.IsCompatible("2.0"));
    }
}

public class FilterRegistryTests
{
    [Fact]
    public void Register_AddsFilter()
    {
        var registry = new FilterRegistry();
        var reg = new FilterRegistration
        {
            Id = "test-blur",
            DisplayName = "Test Blur",
            Category = "Test",
            Order = 1000,
            Execute = _ => { }
        };

        registry.Register(reg);

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("test-blur", all[0].Id);
    }

    [Fact]
    public void Find_ReturnsFilterById()
    {
        var registry = new FilterRegistry();
        registry.Register(new FilterRegistration
        {
            Id = "test-sharpen",
            DisplayName = "Test Sharpen",
            Category = "Test",
            Execute = _ => { }
        });

        var found = registry.Find("test-sharpen");
        Assert.NotNull(found);
        Assert.Equal("Test Sharpen", found.DisplayName);
    }

    [Fact]
    public void Find_ReturnsNullForUnknownId()
    {
        var registry = new FilterRegistry();
        Assert.Null(registry.Find("nonexistent"));
    }

    [Fact]
    public void GetAll_OrdersByCategoryThenOrder()
    {
        var registry = new FilterRegistry();
        registry.Register(new FilterRegistration
        {
            Id = "b",
            DisplayName = "B",
            Category = "Filter",
            Order = 2,
            Execute = _ => { }
        });
        registry.Register(new FilterRegistration
        {
            Id = "a",
            DisplayName = "A",
            Category = "Filter",
            Order = 1,
            Execute = _ => { }
        });

        var all = registry.GetAll();
        Assert.Equal("a", all[0].Id);
        Assert.Equal("b", all[1].Id);
    }
}
