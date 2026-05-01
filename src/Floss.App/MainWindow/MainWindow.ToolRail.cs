using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Floss.App;

public partial class MainWindow
{
    // ── Left rail ─────────────────────────────────────────────────────────────
    private Control BuildLeftRail()
    {
        _toolStatusText = new TextBlock { IsVisible = false };

        _colorWell = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a3e")),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.Parse("#111112"))
        };
        var colorBtn = new Button
        {
            Content = _colorWell,
            Width = 36,
            Height = 34,
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(5),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(colorBtn, "Cycle color  (X)");
        colorBtn.Click += (_, _) => CycleColor();

        _brushToolButton = RailBtn(Icons.BrushOutline, "Brush  (B)");
        _eraserToolButton = RailBtn(Icons.Eraser, "Eraser  (E)");
        _smudgeToolButton = RailBtn(Icons.BlurLinear, "Smudge  (U)");
        _moveToolButton = RailBtn(Icons.ArrowAll, "Move layer  (V)");
        _selectToolButton = RailBtn(Icons.SelectionRect, "Select  (S). Click again: rect/lasso/polyline");
        _wandToolButton = RailBtn(Icons.AutoFix, "Magic Wand  (W)");
        _fillToolButton = RailBtn(Icons.FormatColorFill, "Fill  (G)");
        _lassoFillToolButton = RailBtn(Icons.Lasso, "Lasso Fill  (L)");
        _eyedropToolButton = RailBtn(Icons.Eyedropper, "Eyedropper  (I)");
        _gradientToolButton = RailBtn(Icons.GradientHorizontal, "Gradient. Click again: linear/radial");
        _shapeToolButton = RailBtn(Icons.RectangleOutline, "Shape. Click again: rectangle/ellipse/line");
        _polylineToolButton = RailBtn(Icons.VectorPolyline, "Polyline. Click again: open/closed");

        _brushToolButton.Click += (_, _) => ActivateTool(_canvas.BrushTool, _brushToolButton);
        _eraserToolButton.Click += (_, _) => ActivateTool(_canvas.EraserTool, _eraserToolButton);
        _smudgeToolButton.Click += (_, _) => ActivateTool(_canvas.SmudgeTool, _smudgeToolButton);
        _moveToolButton.Click += (_, _) => ActivateTool(_moveTool, _moveToolButton);
        _selectToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _selectTool)) CycleSelectMode();
            else ActivateTool(_selectTool, _selectToolButton);
        };
        _wandToolButton.Click += (_, _) => ActivateTool(_magicWandTool, _wandToolButton);
        _fillToolButton.Click += (_, _) => ActivateTool(_fillTool, _fillToolButton);
        _lassoFillToolButton.Click += (_, _) => ActivateTool(_lassoFillTool, _lassoFillToolButton);
        _eyedropToolButton.Click += (_, _) => ActivateTool(_eyedropperTool, _eyedropToolButton);
        _gradientToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _gradientTool)) CycleGradientMode();
            else ActivateTool(_gradientTool, _gradientToolButton);
        };
        _shapeToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _shapeTool)) CycleShapeMode();
            else ActivateTool(_shapeTool, _shapeToolButton);
        };
        _polylineToolButton.Click += (_, _) =>
        {
            if (ReferenceEquals(_canvas.ActiveTool, _polylineTool)) TogglePolylineClosePath();
            else ActivateTool(_polylineTool, _polylineToolButton);
        };

        _toolButtons.AddRange([
            _brushToolButton, _eraserToolButton, _smudgeToolButton, _moveToolButton,
            _selectToolButton, _wandToolButton, _fillToolButton, _lassoFillToolButton,
            _eyedropToolButton, _gradientToolButton, _shapeToolButton, _polylineToolButton
        ]);

        _undoButton = RailBtn(Icons.UndoVariant, "Undo  (Ctrl+Z)");
        _redoButton = RailBtn(Icons.RedoVariant, "Redo  (Ctrl+Shift+Z)");
        _undoButton.Click += (_, _) => _canvas.Undo();
        _redoButton.Click += (_, _) => _canvas.Redo();

        var clearBtn = RailBtn(Icons.DeleteOutline, "Clear layer");
        clearBtn.Click += (_, _) => _canvas.Clear();

        var stack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 6),
            Spacing = 1
        };
        stack.Children.Add(_brushToolButton);
        stack.Children.Add(_eraserToolButton);
        stack.Children.Add(_smudgeToolButton);
        stack.Children.Add(_moveToolButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(_selectToolButton);
        stack.Children.Add(_wandToolButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(_fillToolButton);
        stack.Children.Add(_lassoFillToolButton);
        stack.Children.Add(_gradientToolButton);
        stack.Children.Add(_shapeToolButton);
        stack.Children.Add(_polylineToolButton);
        stack.Children.Add(RailSep());
        stack.Children.Add(_eyedropToolButton);
        stack.Children.Add(colorBtn);
        stack.Children.Add(RailSep());
        stack.Children.Add(_undoButton);
        stack.Children.Add(_redoButton);
        stack.Children.Add(clearBtn);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            CacheMode = new Avalonia.Media.BitmapCache(),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = stack
            }
        };
    }

    private static Button RailBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = MaterialIcon(icon, 18),
            Width = 36,
            Height = 34,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private static Border RailSep() => new()
    {
        Height = 1,
        Width = 26,
        Background = new SolidColorBrush(Color.Parse(Stroke)),
        Margin = new Thickness(0, 4)
    };
}
