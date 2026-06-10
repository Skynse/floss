using Avalonia.Input;
using Floss.App.Features.Actions;

namespace Floss.App.Tests;

public class ActionRegistryTests
{
    [Fact]
    public void Register_AddsAction()
    {
        var registry = new ActionRegistry();
        registry.Register(new ActionRegistration
        {
            Id = "test.action",
            Title = "Test",
            Execute = () => { }
        });

        Assert.Single(registry.GetActions());
        Assert.Equal("test.action", registry.GetActions()[0].Id);
    }

    [Fact]
    public void TryExecute_RunsMatchingGesture()
    {
        var registry = new ActionRegistry();
        var ran = false;
        registry.Register(new ActionRegistration
        {
            Id = "test.shortcut",
            Title = "Shortcut",
            Gesture = new KeyGesture(Key.F9),
            Execute = () => ran = true
        });

        Assert.True(registry.TryExecute(Key.F9, KeyModifiers.None));
        Assert.True(ran);
    }

    [Fact]
    public void TryExecute_RespectsCanExecute()
    {
        var registry = new ActionRegistry();
        var ran = false;
        registry.Register(new ActionRegistration
        {
            Id = "test.blocked",
            Title = "Blocked",
            Gesture = new KeyGesture(Key.F9),
            CanExecute = () => false,
            Execute = () => ran = true
        });

        Assert.False(registry.TryExecute(Key.F9, KeyModifiers.None));
        Assert.False(ran);
    }

    [Fact]
    public void TryExecute_LowerOrderWins()
    {
        var registry = new ActionRegistry();
        string? winner = null;
        registry.Register(new ActionRegistration
        {
            Id = "second",
            Title = "Second",
            Gesture = new KeyGesture(Key.F9),
            Order = 10,
            Execute = () => winner = "second"
        });
        registry.Register(new ActionRegistration
        {
            Id = "first",
            Title = "First",
            Gesture = new KeyGesture(Key.F9),
            Order = 0,
            Execute = () => winner = "first"
        });

        Assert.True(registry.TryExecute(Key.F9, KeyModifiers.None));
        Assert.Equal("first", winner);
    }

    [Fact]
    public void Execute_ById_RunsHandler()
    {
        var registry = new ActionRegistry();
        var ran = false;
        registry.Register(new ActionRegistration
        {
            Id = "by-id",
            Title = "By Id",
            Execute = () => ran = true
        });

        Assert.True(registry.Execute("by-id"));
        Assert.True(ran);
    }

    [Fact]
    public void Unregister_RemovesAction()
    {
        var registry = new ActionRegistry();
        registry.Register(new ActionRegistration
        {
            Id = "gone",
            Title = "Gone",
            Execute = () => { }
        });

        registry.Unregister("gone");
        Assert.Empty(registry.GetActions());
    }
}
