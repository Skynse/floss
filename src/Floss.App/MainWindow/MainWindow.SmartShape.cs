using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App;

public partial class MainWindow
{
    private Border? _smartShapeLauncherBar;

    private void BuildSmartShapeLauncher()
    {
        _smartShapeLauncherBar = new Border
        {
            IsVisible = false,
            ZIndex = 60
        };
        _workspaceViewport.Children.Add(_smartShapeLauncherBar);
    }

    private void UpdateSmartShapeLauncher()
    {
        if (_smartShapeLauncherBar != null)
            _smartShapeLauncherBar.IsVisible = false;
    }

    private bool IsOverSmartShapeLauncher(Point viewportPos) => false;
}
