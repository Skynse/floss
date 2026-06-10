using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;

namespace Floss.App.Features.Menu;

/// <summary>Declarative menu contributions (<c>action plugin</c> pattern).</summary>
public interface IMenuRegistry
{
    void Register(MenuItemRegistration item);

    void Unregister(string id);

    IReadOnlyList<MenuItemRegistration> GetItems(string menuPath);

    /// <summary>Top-level paths with at least one registration (includes <c>Plugins</c> when used).</summary>
    IReadOnlyList<string> GetActiveTopLevelMenus();

    void BindMenus(IReadOnlyDictionary<string, MenuItem> menuRoots);

    void Refresh();
}

public sealed class MenuItemRegistration
{
    public required string Id { get; init; }

    /// <summary>e.g. <c>Plugins</c>, <c>Window</c>, <c>Filter/Adjust</c></summary>
    public required string Path { get; init; }

    public required string Header { get; init; }

    public KeyGesture? Gesture { get; init; }

    public int Order { get; init; } = 1000;

    public Action? Click { get; init; }

    public Func<Task>? ClickAsync { get; init; }

    public bool IsSeparator { get; init; }
}
