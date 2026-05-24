using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Tools;

namespace Floss.App;

public partial class MainWindow
{
    private void EnsureTransformPropertySubscription()
    {
        if (_transformPropertySubscribed) return;
        _transformPropertySubscribed = true;
        _canvas.TransformEditChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshToolProperties);
    }

    private bool _transformPropertySubscribed;

    private IReadOnlyList<ToolPropertyDescriptor> CurrentTransformProperties()
    {
        EnsureTransformPropertySubscription();

        return
        [
            EnumProp("transform.mode", "Mode", true,
                () => _canvas.TransformEdit?.Mode ?? TransformMode.ScaleRotate,
                v => ApplyTransformEdit(s => s with { Mode = v })),
            SliderProp("transform.scaleW", "Scale W", true,
                () => _canvas.TransformEdit?.ScaleWPercent ?? 100,
                v => ApplyTransformEdit(s => s with { ScaleWPercent = v }), 1, 1000, "%"),
            SliderProp("transform.scaleH", "Scale H", true,
                () => _canvas.TransformEdit?.ScaleHPercent ?? 100,
                v => ApplyTransformEdit(s => s with { ScaleHPercent = v }), 1, 1000, "%"),
            BoolProp("transform.keepAspect", "Keep aspect ratio", true,
                () => _canvas.TransformEdit?.KeepAspectRatio ?? true,
                v => ApplyTransformEdit(s => s with { KeepAspectRatio = v })),
            SliderProp("transform.angle", "Rotation", true,
                () => _canvas.TransformEdit?.Angle ?? 0,
                v => ApplyTransformEdit(s => s with { Angle = v }), -180, 180, "°"),
        ];
    }

    private Control BuildTransformActionBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 4)
        };

        row.Children.Add(MkTransformBtn("Reset", () => _canvas.ResetTransformEdit()));
        row.Children.Add(MkTransformBtn("Flip H", () => _canvas.FlipTransformHorizontal()));
        row.Children.Add(MkTransformBtn("Flip V", () => _canvas.FlipTransformVertical()));
        row.Children.Add(MkTransformBtn("OK", () => _canvas.CommitActiveTool()));
        row.Children.Add(MkTransformBtn("Cancel", () => _canvas.CancelActiveTool()));

        return row;
    }

    private static Button MkTransformBtn(string text, Action action)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 10,
            MinHeight = 22,
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        btn.Click += (_, _) => action();
        return btn;
    }

    private void ApplyTransformEdit(Func<TransformEditSnapshot, TransformEditSnapshot> mutate)
    {
        if (_syncingToolPropertyPanel) return;
        var cur = _canvas.TransformEdit;
        if (cur == null) return;
        _canvas.UpdateTransformEdit(mutate(cur));
    }
}
