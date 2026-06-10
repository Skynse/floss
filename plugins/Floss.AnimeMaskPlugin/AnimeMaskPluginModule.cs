using Avalonia.Input;
using Floss.App.Features;
using Floss.App.Features.Actions;
using Floss.App.Features.Menu;

namespace Floss.AnimeMaskPlugin;

public sealed class AnimeMaskPluginModule : IFeatureModule
{
    private IFeatureSession? _session;

    public void Register(IFeatureSession session)
    {
        _session = session;

        var menus = session.GetService<IMenuRegistry>();
        menus.Register(new MenuItemRegistration
        {
            Id = "anime-mask.generate",
            Path = "Filter",
            Header = "_Base Color Masks from Sketch...",
            Order = 5000,
            ClickAsync = () => AnimeMaskCommands.RunGeneratorAsync(session),
        });

        var actions = session.GetService<IActionRegistry>();
        actions.Register(new ActionRegistration
        {
            Id = "anime-mask.generate-shortcut",
            Title = "Base Color Masks from Sketch",
            Gesture = new KeyGesture(Key.M, KeyModifiers.Control | KeyModifiers.Shift),
            Order = 5000,
            ExecuteAsync = () => AnimeMaskCommands.RunGeneratorAsync(session),
        });
    }
}
