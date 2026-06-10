using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;

namespace Floss.App.Features.Actions;

/// <summary>Global actions with optional keyboard shortcuts (<c>action</c> pattern).</summary>
public interface IActionRegistry
{
    void Register(ActionRegistration action);

    void Unregister(string id);

    /// <summary>Try to run the highest-priority action bound to this key chord.</summary>
    bool TryExecute(Key key, KeyModifiers modifiers, Func<bool>? defaultCanExecute = null);

    bool Execute(string id);

    Task<bool> ExecuteAsync(string id);

    IReadOnlyList<ActionRegistration> GetActions();
}

public sealed class ActionRegistration
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public KeyGesture? Gesture { get; init; }

    public int Order { get; init; } = 1000;

    public Action? Execute { get; init; }

    public Func<Task>? ExecuteAsync { get; init; }

    /// <summary>When null, <see cref="IActionRegistry.TryExecute"/> uses the caller's default predicate.</summary>
    public Func<bool>? CanExecute { get; init; }
}
