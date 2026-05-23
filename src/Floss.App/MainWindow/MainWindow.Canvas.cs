using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Canvas;

namespace Floss.App;

using static Floss.App.AppColors;

public partial class MainWindow
{
    // ── Resize overlay state ──────────────────────────────────────────────────
    private CanvasResizeOverlay? _resizeOverlay;
    private Border? _resizeFloatingPanel;
    private NumericUpDown? _resizeWidthBox;
    private NumericUpDown? _resizeHeightBox;
    private Button? _resizeOkBtn;
    private Button? _resizeCancelBtn;
    private Button[,]? _resizeAnchorBtns;
    private int _resizeAnchorCol = 1;
    private int _resizeAnchorRow = 1;
    private bool _suppressResizeSync;
    private int _resizeOldW, _resizeOldH;
    private TaskCompletionSource<bool>? _resizeTcs;

    internal async Task ShowResizeCanvasDialog()
    {
        _resizeOldW = _canvas.Document.Width;
        _resizeOldH = _canvas.Document.Height;

        if (_resizeOverlay == null) BuildResizeOverlay();

        _suppressResizeSync = true;
        _resizeWidthBox!.Value = _resizeOldW;
        _resizeHeightBox!.Value = _resizeOldH;
        _resizeAnchorCol = 1;
        _resizeAnchorRow = 1;
        _suppressResizeSync = false;

        UpdateResizeAnchorColors();
        _resizeOverlay!.SetPreview(_resizeOldW, _resizeOldH, 0, 0);
        _resizeOverlay.IsVisible = true;
        _resizeFloatingPanel!.IsVisible = true;
        _canvas.PaintInputSuspended = true;

        _resizeTcs = new TaskCompletionSource<bool>();
        var confirmed = await _resizeTcs.Task;

        _resizeOverlay.IsVisible = false;
        _resizeFloatingPanel.IsVisible = false;
        _canvas.PaintInputSuspended = false;

        if (!confirmed) return;

        _canvas.ResizeCanvas(_resizeOverlay.PreviewW, _resizeOverlay.PreviewH,
                             _resizeOverlay.PreviewOffX, _resizeOverlay.PreviewOffY);
        SyncCanvasFrameToDocument(fitToViewport: true);
        SyncBrushSizeLimits();
        BuildLayerList();
    }

    private void BuildResizeOverlay()
    {
        _resizeOverlay = new CanvasResizeOverlay(_canvas) { IsVisible = false };
        _workspaceViewport.Children.Add(_resizeOverlay);

        _resizeWidthBox = new NumericUpDown { Value = 1, Minimum = 1, Maximum = 32000, Increment = 1, Width = 110, FontSize = 11 };
        _resizeHeightBox = new NumericUpDown { Value = 1, Minimum = 1, Maximum = 32000, Increment = 1, Width = 110, FontSize = 11 };
        _resizeWidthBox.ValueChanged += (_, _) => OnResizeInputChanged();
        _resizeHeightBox.ValueChanged += (_, _) => OnResizeInputChanged();

        // Anchor grid 3x3
        _resizeAnchorBtns = new Button[3, 3];
        var anchorGrid = new Grid { Width = 72, Height = 72 };
        for (var i = 0; i < 3; i++)
        {
            anchorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            anchorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var cr = r; var cc = c;
                var btn = new Button
                {
                    Margin = new Thickness(1),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(0)
                };
                btn.Click += (_, _) =>
                {
                    _resizeAnchorCol = cc;
                    _resizeAnchorRow = cr;
                    UpdateResizeAnchorColors();
                    OnResizeInputChanged();
                };
                Grid.SetRow(btn, r); Grid.SetColumn(btn, c);
                anchorGrid.Children.Add(btn);
                _resizeAnchorBtns[r, c] = btn;
            }
        }

        _resizeOkBtn = new Button
        {
            Content = "Resize",
            Padding = new Thickness(12, 4),
            FontSize = 11,
            Background = new SolidColorBrush(Color.Parse("#1e3a78")),
            Foreground = new SolidColorBrush(Color.Parse("#90baf0")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2a4a98")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        _resizeCancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(12, 4),
            FontSize = 11,
            Background = new SolidColorBrush(Color.Parse("#1a1c22")),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        _resizeOkBtn.Click += (_, _) => _resizeTcs?.TrySetResult(true);
        _resizeCancelBtn.Click += (_, _) => _resizeTcs?.TrySetResult(false);

        var inputGrid = new Grid { RowSpacing = 5, ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var wLbl = FieldLabel("Width");
        var hLbl = FieldLabel("Height");
        Grid.SetRow(wLbl, 0); Grid.SetColumn(wLbl, 0);
        Grid.SetRow(_resizeWidthBox, 0); Grid.SetColumn(_resizeWidthBox, 1);
        Grid.SetRow(hLbl, 1); Grid.SetColumn(hLbl, 0);
        Grid.SetRow(_resizeHeightBox, 1); Grid.SetColumn(_resizeHeightBox, 1);
        inputGrid.Children.Add(wLbl);
        inputGrid.Children.Add(_resizeWidthBox);
        inputGrid.Children.Add(hLbl);
        inputGrid.Children.Add(_resizeHeightBox);

        var anchorLabel = new TextBlock
        {
            Text = "Anchor",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Margin = new Thickness(0, 0, 0, 3)
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(_resizeCancelBtn);
        btnRow.Children.Add(_resizeOkBtn);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 0 };
        panel.Children.Add(inputGrid);
        panel.Children.Add(anchorLabel);
        panel.Children.Add(anchorGrid);
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(btnRow);

        _resizeFloatingPanel = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 16, 16),
            Child = panel
        };
        _workspaceViewport.Children.Add(_resizeFloatingPanel);

        _resizeOverlay.PreviewChanged += (w, h, _, _) =>
        {
            _suppressResizeSync = true;
            _resizeWidthBox.Value = w;
            _resizeHeightBox.Value = h;
            _suppressResizeSync = false;
        };
    }

    // ── Resize drag — called from workspace pointer handlers ──────────────────

    internal bool TryBeginResizeDrag(Point vpPos, bool leftButton)
    {
        if (_resizeOverlay == null || !_resizeOverlay.IsVisible || !leftButton) return false;
        var h = _resizeOverlay.HitHandle(vpPos);
        if (h < 0) return false;
        _resizeOverlay.BeginDrag(h, vpPos);
        return true;
    }

    internal bool IsResizeDragging => _resizeOverlay?.DragHandle >= 0;

    internal void UpdateResizeDrag(Point vpPos)
    {
        if (_resizeOverlay == null) return;
        _resizeOverlay.UpdateDrag(vpPos);
        _resizeOverlay.InvalidateVisual();
    }

    internal void EndResizeDrag()
    {
        _resizeOverlay?.EndDrag();
    }

    private void OnResizeInputChanged()
    {
        if (_suppressResizeSync || _resizeOverlay == null) return;
        var nw = (int)(_resizeWidthBox!.Value ?? _resizeOldW);
        var nh = (int)(_resizeHeightBox!.Value ?? _resizeOldH);
        var offX = (int)Math.Round((nw - _resizeOldW) * _resizeAnchorCol / 2.0);
        var offY = (int)Math.Round((nh - _resizeOldH) * _resizeAnchorRow / 2.0);
        _resizeOverlay.SetPreview(nw, nh, offX, offY);
    }

    private void UpdateResizeAnchorColors()
    {
        if (_resizeAnchorBtns == null) return;
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                var active = r == _resizeAnchorRow && c == _resizeAnchorCol;
                _resizeAnchorBtns[r, c].Background = new SolidColorBrush(Color.Parse(active ? "#1e3a78" : Bg3));
                _resizeAnchorBtns[r, c].BorderBrush = new SolidColorBrush(Color.Parse(active ? "#3a5aaa" : Stroke));
            }
    }

    private void ShowPaperColorPicker()
    {
        var picker = new ColorPickerWindow(_canvas.Document.PaperColor, color =>
        {
            _canvas.Document.SetPaperColor(color);
            var paper = _canvas.Document.PaperLayer;
            if (paper != null)
                paper.FillSolid(paper.Pixels.Bounds, color);
            _canvas.InvalidateCompositor();
            _canvas.InvalidateVisual();
            _checkerboardOverlay?.InvalidateVisual();
            _resizeOverlay?.InvalidateVisual();
            BuildLayerList(); // refresh paper layer swatch color
        });
        picker.Show(this);
    }

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        Width = 46,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
    };
}
