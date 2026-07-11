using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
namespace Floss.App.Features.Actions;

public sealed class ActionRegistry : IActionRegistry
{
    private readonly ConcurrentDictionary<string, ActionRegistration> _actions =
        new(StringComparer.Ordinal);

    public void Register(ActionRegistration action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrEmpty(action.Id);
        ArgumentException.ThrowIfNullOrEmpty(action.Title);
        if (action.Execute == null && action.ExecuteAsync == null)
            throw new ArgumentException("Action must provide Execute or ExecuteAsync.", nameof(action));

        if (_actions.TryAdd(action.Id, action) == false)
            Trace.WriteLine($"[Floss] Action id '{action.Id}' overwritten by new registration");
    }

    public void Unregister(string id) => _actions.TryRemove(id, out _);

    public bool TryExecute(Key key, KeyModifiers modifiers, Func<bool>? defaultCanExecute = null)
    {
        var mods = Input.KeyBinding.ModifiersWithKeyDown(key, modifiers);
        foreach (var action in _actions.Values
                     .Where(a => a.Gesture != null && GestureMatches(a.Gesture, key, mods))
                     .OrderBy(a => a.Order)
                     .ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (!CanRun(action, defaultCanExecute))
                continue;

            Run(action);
            return true;
        }

        return false;
    }

    public bool Execute(string id)
    {
        if (!_actions.TryGetValue(id, out var action))
            return false;

        if (action.CanExecute?.Invoke() == false)
            return false;

        Run(action);
        return true;
    }

    public async Task<bool> ExecuteAsync(string id)
    {
        if (!_actions.TryGetValue(id, out var action))
            return false;

        if (action.CanExecute?.Invoke() == false)
            return false;

        if (action.ExecuteAsync != null)
            await action.ExecuteAsync();
        else
            action.Execute?.Invoke();

        return true;
    }

    public IReadOnlyList<ActionRegistration> GetActions()
        => _actions.Values
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Title, StringComparer.Ordinal)
            .ToList();

    private static bool GestureMatches(KeyGesture gesture, Key key, KeyModifiers mods)
        => gesture.Key == key && gesture.KeyModifiers == mods;

    private static bool CanRun(ActionRegistration action, Func<bool>? defaultCanExecute)
    {
        if (action.CanExecute != null)
            return action.CanExecute();

        return defaultCanExecute?.Invoke() ?? true;
    }

    private static void Run(ActionRegistration action)
    {
        if (action.ExecuteAsync != null)
            _ = action.ExecuteAsync();
        else
            action.Execute?.Invoke();
    }
}
