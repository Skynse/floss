using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;

namespace Floss.App.Features.Menu;

public sealed class MenuRegistry : IMenuRegistry
{
    private readonly ConcurrentDictionary<string, MenuItemRegistration> _items = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, MenuItem> _anchors = new Dictionary<string, MenuItem>(StringComparer.Ordinal);

    public void Register(MenuItemRegistration item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrEmpty(item.Id);
        ArgumentException.ThrowIfNullOrEmpty(item.Path);
        if (!item.IsSeparator)
            ArgumentException.ThrowIfNullOrEmpty(item.Header);

        if (_items.TryAdd(item.Id, item) == false)
            Trace.WriteLine($"[Floss] Menu item id '{item.Id}' overwritten by new registration");
    }

    public void Unregister(string id) => _items.TryRemove(id, out _);

    public IReadOnlyList<MenuItemRegistration> GetItems(string menuPath)
        => _items.Values
            .Where(i => string.Equals(i.Path, menuPath, StringComparison.Ordinal))
            .OrderBy(i => i.Order)
            .ThenBy(i => i.Header, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<string> GetActiveTopLevelMenus()
        => _items.Values
            .Select(i => i.Path.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    public void BindMenus(IReadOnlyDictionary<string, MenuItem> menuRoots)
        => _anchors = menuRoots;

    public void Refresh()
    {
        foreach (var (path, menu) in _anchors)
        {
            EnsureMutableItems(menu);
            ApplyToMenu(menu, path);
        }
    }

    private static void EnsureMutableItems(MenuItem menu)
    {
        if (menu.ItemsSource == null)
            return;

        var snapshot = menu.ItemsSource.Cast<object>().ToList();
        menu.ItemsSource = null;
        menu.Items.Clear();
        foreach (var entry in snapshot)
            menu.Items.Add(entry);
    }

    private void ApplyToMenu(MenuItem root, string path)
    {
        EnsureMutableItems(root);
        RemoveInjectedItems(root);

        foreach (var item in GetItems(path))
        {
            if (item.IsSeparator)
            {
                root.Items.Add(new Separator());
                continue;
            }

            var menuItem = new MenuItem { Header = item.Header, Tag = item.Id };
            if (item.Gesture != null)
                menuItem.HotKey = item.Gesture;

            if (item.ClickAsync != null)
                menuItem.Click += async (_, _) => await item.ClickAsync();
            else if (item.Click != null)
                menuItem.Click += (_, _) => item.Click();

            root.Items.Add(menuItem);
        }

        foreach (var childPath in _items.Values
                     .Select(i => i.Path)
                     .Where(p => p.StartsWith(path + "/", StringComparison.Ordinal))
                     .Select(p => p.Split('/')[1])
                     .Distinct(StringComparer.Ordinal))
        {
            var subPath = path + "/" + childPath;
            var sub = FindOrCreateSubMenu(root, childPath);
            ApplyToMenu(sub, subPath);
        }
    }

    private static MenuItem FindOrCreateSubMenu(MenuItem root, string header)
    {
        EnsureMutableItems(root);
        foreach (var existing in root.Items)
        {
            if (existing is MenuItem mi && HeaderMatches(mi.Header?.ToString(), header))
                return mi;
        }

        var created = new MenuItem { Header = header };
        root.Items.Add(created);
        return created;
    }

    private static bool HeaderMatches(string? actual, string expected)
    {
        if (actual == null)
            return false;

        var normalized = actual.Replace("_", "", StringComparison.Ordinal);
        var expectedNorm = expected.Replace("_", "", StringComparison.Ordinal);
        return string.Equals(normalized, expectedNorm, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveInjectedItems(MenuItem root)
    {
        for (var i = root.Items.Count - 1; i >= 0; i--)
        {
            if (root.Items[i] is MenuItem mi && mi.Tag is string)
                root.Items.RemoveAt(i);
            else if (root.Items[i] is Separator && i > 0 && root.Items[i - 1] is MenuItem prev && prev.Tag is string)
                root.Items.RemoveAt(i);
        }
    }
}
