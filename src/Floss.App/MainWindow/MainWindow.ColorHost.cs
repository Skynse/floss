using System;
using Avalonia.Media;
using Floss.App.Features.Dock.Panels;

namespace Floss.App;

public partial class MainWindow
{
    private void SetColor(Color color, bool syncPicker = true)
    {
        GetToolsPanel().ColorWell.Background = new SolidColorBrush(color);
        _canvas.SetPaintColor(color);
        var preview = GetBrushPanel().StrokePreview;
        preview.Brush = _canvas.Brush;
        preview.InvalidateBitmap();
        _toolPropsWindow?.UpdatePreviewColor(color);
        if (syncPicker)
            SyncPickerFromColor(color);
        RefreshColorSliders();
    }

    private void SyncPickerFromColor(Color color)
    {
        var panel = GetColorPanel();
        panel.SyncPickerFromColor(color);
    }
}
