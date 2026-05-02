using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App;

public partial class MainWindow
{
    internal async Task ShowResizeCanvasDialog()
    {
        var oldW = _canvas.Document.Width;
        var oldH = _canvas.Document.Height;

        // ── Inputs ────────────────────────────────────────────────────────────
        var widthBox  = new NumericUpDown { Value = oldW, Minimum = 1, Maximum = 32000, Increment = 1, Width = 130, FontSize = 12 };
        var heightBox = new NumericUpDown { Value = oldH, Minimum = 1, Maximum = 32000, Increment = 1, Width = 130, FontSize = 12 };

        // ── Preview canvas (scaled) ───────────────────────────────────────────
        const int PreviewSize = 160;
        var previewControl = new PreviewBox(PreviewSize) { Width = PreviewSize, Height = PreviewSize };

        // ── Anchor grid (3×3) ─────────────────────────────────────────────────
        var anchorCol = 1;
        var anchorRow = 1;
        var anchorBtns = new Button[3, 3];
        var anchorGrid = new Grid { Width = 96, Height = 96 };
        for (var i = 0; i < 3; i++)
        {
            anchorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            anchorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }

        void UpdatePreview()
        {
            var nw = (int)(widthBox.Value  ?? oldW);
            var nh = (int)(heightBox.Value ?? oldH);
            var ox = (int)Math.Round((nw - oldW) * anchorCol / 2.0);
            var oy = (int)Math.Round((nh - oldH) * anchorRow / 2.0);
            previewControl.SetData(nw, nh, ox, oy, oldW, oldH);
        }

        void UpdateAnchorColors()
        {
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                {
                    var active = r == anchorRow && c == anchorCol;
                    anchorBtns[r, c].Background = new SolidColorBrush(active ? Color.Parse("#1e3a78") : Color.Parse(Bg3));
                    anchorBtns[r, c].BorderBrush = new SolidColorBrush(active ? Color.Parse("#3a5aaa") : Color.Parse(Stroke));
                }
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
                btn.Click += (_, _) => { anchorCol = cc; anchorRow = cr; UpdateAnchorColors(); UpdatePreview(); };
                Grid.SetRow(btn, r); Grid.SetColumn(btn, c);
                anchorGrid.Children.Add(btn);
                anchorBtns[r, c] = btn;
            }
        }
        UpdateAnchorColors();

        widthBox.ValueChanged  += (_, _) => UpdatePreview();
        heightBox.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        // ── Layout ────────────────────────────────────────────────────────────
        var inputGrid = new Grid { Margin = new Thickness(0, 0, 0, 12), RowSpacing = 6, ColumnSpacing = 8 };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var wLabel = FieldLabel("Width");
        var hLabel = FieldLabel("Height");
        Grid.SetRow(wLabel, 0); Grid.SetColumn(wLabel, 0);
        Grid.SetRow(widthBox, 0); Grid.SetColumn(widthBox, 1);
        Grid.SetRow(hLabel, 1); Grid.SetColumn(hLabel, 0);
        Grid.SetRow(heightBox, 1); Grid.SetColumn(heightBox, 1);
        inputGrid.Children.Add(wLabel);
        inputGrid.Children.Add(widthBox);
        inputGrid.Children.Add(hLabel);
        inputGrid.Children.Add(heightBox);

        var anchorLabel = new TextBlock { Text = "Anchor", FontSize = 10, Foreground = new SolidColorBrush(Color.Parse(TextSecondary)), Margin = new Thickness(0, 0, 0, 4) };

        var leftCol = new StackPanel { Spacing = 10 };
        leftCol.Children.Add(inputGrid);
        leftCol.Children.Add(anchorLabel);
        leftCol.Children.Add(anchorGrid);

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        topRow.Children.Add(leftCol);
        topRow.Children.Add(previewControl);

        // ── Footer ────────────────────────────────────────────────────────────
        var okBtn = new Button
        {
            Content = "Resize", Padding = new Thickness(14, 5), FontSize = 11,
            Background = new SolidColorBrush(Color.Parse("#1e3a78")),
            Foreground = new SolidColorBrush(Color.Parse("#90baf0")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2a4a98")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel", Padding = new Thickness(14, 5), FontSize = 11,
            Background = new SolidColorBrush(Color.Parse("#1a1c22")),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3)
        };

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(cancelBtn, 1); Grid.SetColumn(okBtn, 2);
        footer.Children.Add(cancelBtn);
        footer.Children.Add(okBtn);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 0 };
        root.Children.Add(topRow);
        root.Children.Add(footer);

        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = "Resize Canvas",
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            MinWidth = 320
        };

        okBtn.Click += (_, _) => { tcs.TrySetResult(true);  dialog.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(this);
        if (!await tcs.Task) return;

        var newW   = (int)(widthBox.Value  ?? oldW);
        var newH   = (int)(heightBox.Value ?? oldH);
        var offX   = (int)Math.Round((newW - oldW) * anchorCol / 2.0);
        var offY   = (int)Math.Round((newH - oldH) * anchorRow / 2.0);

        _canvas.ResizeCanvas(newW, newH, offX, offY);
        SyncCanvasFrameToDocument(fitToViewport: true);
        BuildLayerList();
    }

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        Width = 52,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
    };

    // ── Preview box ───────────────────────────────────────────────────────────

    private sealed class PreviewBox : Control
    {
        private readonly int _size;
        private double _nw, _nh, _ox, _oy, _cw, _ch;

        public PreviewBox(int size) { _size = size; }

        public void SetData(int newW, int newH, int offsetX, int offsetY, int contentW, int contentH)
        {
            _nw = newW; _nh = newH; _ox = offsetX; _oy = offsetY; _cw = contentW; _ch = contentH;
            InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            if (_nw <= 0 || _nh <= 0) return;

            // Scale to fit preview box with padding
            const double pad = 6;
            var scale = Math.Min((_size - pad * 2) / _nw, (_size - pad * 2) / _nh);
            var pw = _nw * scale;
            var ph = _nh * scale;
            var px = (_size - pw) / 2.0;
            var py = (_size - ph) / 2.0;

            // New canvas border
            var canvasRect = new Rect(px, py, pw, ph);
            dc.DrawRectangle(new SolidColorBrush(Color.Parse("#1a1c24")), new Pen(new SolidColorBrush(Color.Parse("#3a4060")), 1), canvasRect);

            // Content position (old canvas area mapped into new canvas)
            var cx = px + _ox * scale;
            var cy = py + _oy * scale;
            var cw = _cw * scale;
            var ch = _ch * scale;

            // Clipped intersection
            var ix = Math.Max(cx, px);
            var iy = Math.Max(cy, py);
            var ix2 = Math.Min(cx + cw, px + pw);
            var iy2 = Math.Min(cy + ch, py + ph);
            if (ix2 > ix && iy2 > iy)
            {
                var contentRect = new Rect(ix, iy, ix2 - ix, iy2 - iy);
                dc.DrawRectangle(new SolidColorBrush(Color.Parse("#243050")), new Pen(new SolidColorBrush(Color.Parse("#4060a8")), 1), contentRect);
            }
        }
    }
}
