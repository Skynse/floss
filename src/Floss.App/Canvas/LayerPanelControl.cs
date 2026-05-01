using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Floss.App.Document;

namespace Floss.App.Canvas;

/// <summary>
/// Fully custom-drawn layer panel. Zero child controls — one Render call draws
/// everything. Virtualizes automatically: only visible rows are touched.
/// </summary>
public sealed class LayerPanelControl : Control
{
    // ── Layout constants ──────────────────────────────────────────────────────
    private const int RowH   = 36;
    private const int SbW    = 7;   // scrollbar strip width
    private const int ThumbW = 30;  // thumbnail pixel size
    private const int ThumbX = 18;  // thumbnail column start
    private const int ThumbColW = 34;
    private const int IconW  = 16;  // vis / lock / alpha / clip columns
    // Column starts: [0] vis  [18] thumb  [52] lock  [68] alpha  [84] clip  [100] name
    private const int XVis   = 0;
    private const int XThumb = ThumbX;
    private const int XLock  = ThumbX + ThumbColW;       // 52
    private const int XAlpha = XLock  + IconW;           // 68
    private const int XClip  = XAlpha + IconW;           // 84
    private const int XName  = XClip  + IconW;           // 100
    // Right side: blend = contentW-58, opacity = contentW-28 (each ~28px wide)
    private const int RightBlendW   = 28;
    private const int RightOpacityW = 28;
    private const int RightTotal    = RightBlendW + RightOpacityW + 2; // +2 gap

    // ── Geometry cache (parsed once, reused every frame) ──────────────────────
    private static readonly Geometry GeoEye      = Geometry.Parse(Icons.Eye);
    private static readonly Geometry GeoEyeOff   = Geometry.Parse(Icons.EyeOff);
    private static readonly Geometry GeoLock     = Geometry.Parse(Icons.LockOutline);
    private static readonly Geometry GeoLockOpen = Geometry.Parse(Icons.LockOpenOutline);
    private static readonly Geometry GeoAlpha    = Geometry.Parse(Icons.AlphaLock);
    private static readonly Geometry GeoClip     = Geometry.Parse(Icons.ClipToBelow);

    // ── Brush/pen cache ───────────────────────────────────────────────────────
    private static readonly IBrush BgActive   = new SolidColorBrush(Color.Parse("#1a2a50"));
    private static readonly IBrush BgHover    = new SolidColorBrush(Color.Parse("#1c1f28"));
    private static readonly IBrush BgDefault  = new SolidColorBrush(Color.Parse("#16181f"));
    private static readonly IBrush BgPaper    = new SolidColorBrush(Color.Parse("#1c1e24"));
    private static readonly IBrush BrDim      = new SolidColorBrush(Color.Parse("#383d47"));
    private static readonly IBrush BrVis      = new SolidColorBrush(Color.Parse("#6a9fd8"));
    private static readonly IBrush BrLock     = new SolidColorBrush(Color.Parse("#c89050"));
    private static readonly IBrush BrAlpha    = new SolidColorBrush(Color.Parse("#6ab8c8"));
    private static readonly IBrush BrClip     = new SolidColorBrush(Color.Parse("#a87ad8"));
    private static readonly IBrush BrActive   = new SolidColorBrush(Color.Parse("#d8e0f0"));
    private static readonly IBrush BrNormal   = new SolidColorBrush(Color.Parse("#7a8494"));
    private static readonly IBrush BrMeta     = new SolidColorBrush(Color.Parse("#4a5468"));
    private static readonly IBrush BrSbTrack  = new SolidColorBrush(Color.Parse("#12141a"));
    private static readonly IBrush BrSbThumb  = new SolidColorBrush(Color.Parse("#2e3240"));
    private static readonly IBrush BrThumbBg  = new SolidColorBrush(Color.Parse("#1e2028"));
    private static readonly IBrush BrDropLine = new SolidColorBrush(Color.Parse("#5a9fd8"));
    private static readonly Pen    PenSep        = new(new SolidColorBrush(Color.Parse("#1c1f28")), 1);
    private static readonly Pen    PenActiveBdr  = new(new SolidColorBrush(Color.Parse("#2e5fb8")), 1);
    private static readonly Pen    PenDropLine   = new(new SolidColorBrush(Color.Parse("#5a9fd8")), 2);
    private static readonly Pen    PenThumbBdr   = new(new SolidColorBrush(Color.Parse("#2a2e3a")), 1);

    private static readonly Typeface Face = new(FontFamily.Default);

    // ── State ─────────────────────────────────────────────────────────────────
    private IReadOnlyList<DrawingLayer> _layers = [];
    private int[]  _displayIndices = [];   // panel row → index in _layers (tree-visible only)
    private int    _activeIndex  = -1;
    private int    _scrollPx;
    private int    _hoverRow     = -1;

    // Paper state (set by host)
    private bool  _paperVisible  = true;
    private Color _paperColor    = Colors.White;

    // Scrollbar drag
    private bool   _sbDragging;
    private double _sbDragY0;
    private int    _sbDragScroll0;

    // Layer drag-reorder (panel-row indices)
    private bool   _rowDragging;
    private int    _dragRow      = -1;
    private double _dragStartY;
    private int    _dropRow      = -1; // insertion panel-row index (-1 = no drag)

    // ── Callbacks wired by MainWindow ─────────────────────────────────────────
    public Action<int>?         OnSelectLayer { get; set; }
    public Action<int>?         OnToggleVis   { get; set; }
    public Action<int>?         OnToggleLock  { get; set; }
    public Action<int>?         OnToggleAlpha { get; set; }
    public Action<int>?         OnToggleClip  { get; set; }
    public Action<int>?         OnToggleOpen  { get; set; }
    public Action<int, Point>?  OnContextMenu { get; set; }
    /// <summary>
    /// Called when a drag-reorder completes.
    /// from = source layer index; insertBefore = layer index to insert before (-1 = below all).
    /// </summary>
    public Action<int, int>?    OnMoveLayer   { get; set; }
    public Action?              OnPaperVis    { get; set; }
    public Action?              OnPaperSwatch { get; set; }

    // ── Public API ────────────────────────────────────────────────────────────
    public LayerPanelControl()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    public void Update(IReadOnlyList<DrawingLayer> layers, int activeIndex)
    {
        _layers      = layers;
        _activeIndex = activeIndex;
        RebuildDisplayIndices();
        ClampScroll();
        InvalidateVisual();
    }

    // Builds the flat panel-row list from the layer tree, honouring group open/close.
    // Panel row 0 = top-most visible layer (highest z-order).
    private void RebuildDisplayIndices()
    {
        var result = new List<int>(_layers.Count);
        // Walk root layers from highest flat index downward (high index = drawn on top).
        for (int i = _layers.Count - 1; i >= 0; i--)
            if (_layers[i].Parent == null)
                AppendVisibleSubtree(result, i);
        _displayIndices = result.ToArray();
    }

    private void AppendVisibleSubtree(List<int> result, int index)
    {
        result.Add(index);
        var layer = _layers[index];
        if (!layer.IsGroup || !layer.IsOpen) return;
        // Children: last child in Children list = highest z-order = show first.
        var children = layer.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            int ci = IndexInLayers(children[i]);
            if (ci >= 0) AppendVisibleSubtree(result, ci);
        }
    }

    private int IndexInLayers(DrawingLayer target)
    {
        for (int i = 0; i < _layers.Count; i++)
            if (ReferenceEquals(_layers[i], target)) return i;
        return -1;
    }

    public void UpdatePaperState(bool visible, Color color)
    {
        _paperVisible = visible;
        _paperColor   = color;
        InvalidateVisual();
    }

    // ── Index helpers ─────────────────────────────────────────────────────────
    // Panel row → layer index (respects group open/close via _displayIndices).
    private int LayerAt(int row) => _displayIndices[row];
    // Display row count (excludes paper row).
    private int RowCount => _displayIndices.Length;

    // ── Geometry helpers ──────────────────────────────────────────────────────
    private int  TotalContentH => (RowCount + 1) * RowH; // +1 for paper row
    private int  MaxScroll     => Math.Max(0, TotalContentH - (int)Bounds.Height);
    private void ClampScroll() => _scrollPx = Math.Clamp(_scrollPx, 0, MaxScroll);
    private int  RowAtY(double y) => (int)Math.Floor((y + _scrollPx) / RowH);
    private double RowTop(int i)  => i * RowH - _scrollPx;

    // ── Layout override ───────────────────────────────────────────────────────
    protected override Size MeasureOverride(Size av) => av;
    protected override Size ArrangeOverride(Size final) { ClampScroll(); return final; }

    // ── Render ────────────────────────────────────────────────────────────────
    public override void Render(DrawingContext dc)
    {
        double w  = Bounds.Width;
        double h  = Bounds.Height;
        double cw = w - SbW;

        dc.FillRectangle(BgDefault, new Rect(0, 0, w, h));

        using (dc.PushClip(new Rect(0, 0, cw, h)))
        {
            int first = Math.Max(0, _scrollPx / RowH);
            int last  = Math.Min(RowCount - 1, (_scrollPx + (int)h) / RowH + 1);

            for (int i = first; i <= last; i++)
                DrawLayerRow(dc, i, cw, h);

            // Paper row at the bottom
            double py = RowTop(RowCount);
            if (py < h)
                DrawPaperRow(dc, py, cw);

            // Drag-drop insertion line
            if (_rowDragging && _dropRow >= 0)
            {
                double lineY = RowTop(_dropRow) - 1;
                if (lineY >= -2 && lineY <= h + 2)
                    dc.DrawLine(PenDropLine, new Point(XName, lineY), new Point(cw, lineY));
            }
        }

        DrawScrollbar(dc, w, h);
    }

    private void DrawLayerRow(DrawingContext dc, int panelRow, double cw, double vh)
    {
        var    layer    = _layers[LayerAt(panelRow)];
        double y        = RowTop(panelRow);
        bool   isActive = LayerAt(panelRow) == _activeIndex;
        bool   isHover  = panelRow == _hoverRow && !_rowDragging;
        bool   isDrag   = _rowDragging && panelRow == _dragRow;

        var bg = (isActive, isDrag) switch
        {
            (_, true) => BgHover,
            (true, _) => BgActive,
            _ when isHover => BgHover,
            _ => BgDefault
        };

        dc.FillRectangle(bg, new Rect(0, y, cw, RowH));

        if (isActive)
            dc.DrawRectangle(null, PenActiveBdr, new Rect(0.5, y + 0.5, cw - 1, RowH - 1));

        double iconY = y + (RowH - 11) * 0.5;

        // Visibility
        DrawIcon(dc, layer.IsVisible ? GeoEye : GeoEyeOff,
            XVis + (IconW - 11) * 0.5, iconY, 11,
            layer.IsVisible ? BrVis : BrDim);

        // Thumbnail
        DrawThumb(dc, layer, y);

        // Lock
        DrawIcon(dc, layer.IsLocked ? GeoLock : GeoLockOpen,
            XLock + (IconW - 11) * 0.5, iconY, 11,
            layer.IsLocked ? BrLock : BrDim);

        // Alpha lock
        DrawIcon(dc, GeoAlpha, XAlpha + (IconW - 11) * 0.5, iconY, 11,
            layer.IsAlphaLocked ? BrAlpha : BrDim);

        // Clip
        DrawIcon(dc, GeoClip, XClip + (IconW - 11) * 0.5, iconY, 11,
            layer.IsClipping ? BrClip : BrDim);

        // Name — indent for child layers
        double indent  = layer.IndentLevel * 10.0;
        double nameX   = XName + indent + 2;
        double nameW   = cw - nameX - RightTotal - SbW - 2;
        var    nameFg  = isActive ? BrActive : BrNormal;
        var    prefix  = layer.IsGroup ? (layer.IsOpen ? "▾ " : "▸ ") : "";
        if (nameW > 4)
            DrawText(dc, prefix + layer.Name, nameX, y + 12, nameW, 11, nameFg);

        // Blend + opacity (right-aligned, dim)
        double blendX = cw - RightTotal - SbW;
        DrawText(dc, BlendAbbr(layer.BlendMode), blendX,     y + 12, RightBlendW,   9, BrMeta);
        DrawText(dc, $"{Math.Round(layer.Opacity * 100):0}%",
            blendX + RightBlendW + 2, y + 12, RightOpacityW, 9, BrMeta);

        dc.DrawLine(PenSep, new Point(0, y + RowH - 1), new Point(cw, y + RowH - 1));
    }

    private static void DrawThumb(DrawingContext dc, DrawingLayer layer, double y)
    {
        var r = new Rect(XThumb + 2, y + 3, ThumbW, ThumbW);
        dc.FillRectangle(BrThumbBg, r);

        if (layer.IsGroup)
        {
            DrawText(dc, layer.IsOpen ? "▾" : "▸",
                XThumb + 9, y + 12, ThumbW, 11, BrNormal);
        }
        else
        {
            var tb = layer.GetThumbnail(ThumbW);
            dc.DrawImage(tb, r);
        }

        dc.DrawRectangle(null, PenThumbBdr, r);
    }

    private void DrawPaperRow(DrawingContext dc, double y, double cw)
    {
        dc.FillRectangle(BgPaper, new Rect(0, y, cw, RowH));

        // Visibility icon
        double iconY = y + (RowH - 11) * 0.5;
        DrawIcon(dc, _paperVisible ? GeoEye : GeoEyeOff,
            XVis + (IconW - 11) * 0.5, iconY, 11,
            _paperVisible ? BrVis : BrDim);

        // Paper color swatch
        var swatchBrush = new SolidColorBrush(_paperColor);
        var r = new Rect(XThumb + 2, y + 3, ThumbW, ThumbW);
        dc.FillRectangle(swatchBrush, r);
        dc.DrawRectangle(null, PenThumbBdr, r);

        DrawText(dc, "Paper", XName + 2, y + 12, 80, 11, BrMeta);
        dc.DrawLine(PenSep, new Point(0, y + RowH - 1), new Point(cw, y + RowH - 1));
    }

    private static void DrawIcon(DrawingContext dc, Geometry geo, double x, double y, double size, IBrush brush)
    {
        var s = size / 24.0;
        using (dc.PushTransform(new Matrix(s, 0, 0, s, x, y)))
            dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double maxW, double size, IBrush brush)
    {
        if (maxW <= 0 || string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, size, brush)
        {
            MaxTextWidth = maxW,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(ft, new Point(x, y));
    }

    private void DrawScrollbar(DrawingContext dc, double w, double h)
    {
        double x = w - SbW;
        dc.FillRectangle(BrSbTrack, new Rect(x, 0, SbW, h));
        if (TotalContentH <= h) return;

        double ratio  = h / TotalContentH;
        double thumbH = Math.Max(18, h * ratio);
        double thumbY = _scrollPx / (double)(TotalContentH - h) * (h - thumbH);
        dc.FillRectangle(BrSbThumb, new Rect(x + 1, thumbY, SbW - 2, thumbH));
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _scrollPx -= (int)(e.Delta.Y * RowH * 3);
        ClampScroll();
        UpdateHover(e.GetCurrentPoint(this).Position);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateHover(e.GetCurrentPoint(this).Position);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverRow != -1) { _hoverRow = -1; InvalidateVisual(); }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetCurrentPoint(this).Position;

        if (_sbDragging)
        {
            double h       = Bounds.Height;
            double thumbH  = Math.Max(18, h * h / TotalContentH);
            double range   = h - thumbH;
            if (range > 0)
                _scrollPx = _sbDragScroll0 + (int)((pos.Y - _sbDragY0) * (TotalContentH - h) / range);
            ClampScroll();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_rowDragging)
        {
            int newDrop = RowAtY(pos.Y);
            newDrop = Math.Clamp(newDrop, 0, RowCount);
            if (newDrop != _dropRow) { _dropRow = newDrop; InvalidateVisual(); }
            e.Handled = true;
            return;
        }

        if (_dragRow >= 0 && Math.Abs(pos.Y - _dragStartY) > 6)
        {
            _rowDragging = true;
            _dropRow     = _dragRow;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        UpdateHover(pos);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos   = e.GetCurrentPoint(this).Position;
        var props = e.GetCurrentPoint(this).Properties;

        // Scrollbar
        if (pos.X >= Bounds.Width - SbW)
        {
            _sbDragging    = true;
            _sbDragY0      = pos.Y;
            _sbDragScroll0 = _scrollPx;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        int panelRow = RowAtY(pos.Y);

        // Paper row
        if (panelRow == RowCount)
        {
            if (props.IsLeftButtonPressed)
            {
                if (pos.X < XThumb)
                    OnPaperVis?.Invoke();
                else
                    OnPaperSwatch?.Invoke();
            }
            e.Handled = true;
            return;
        }

        if (panelRow < 0 || panelRow >= RowCount) return;
        int layerIndex = LayerAt(panelRow);

        // Right-click → context menu
        if (props.IsRightButtonPressed)
        {
            OnSelectLayer?.Invoke(layerIndex);
            OnContextMenu?.Invoke(layerIndex, e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        double x = pos.X;

        if (x < XThumb) // visibility column
        {
            OnToggleVis?.Invoke(layerIndex);
        }
        else if (x < XLock) // thumbnail → select / toggle group open
        {
            var layer = _layers[layerIndex];
            if (layer.IsGroup && e.ClickCount >= 2)
                OnToggleOpen?.Invoke(layerIndex);
            else
                OnSelectLayer?.Invoke(layerIndex);
            _dragRow    = panelRow;
            _dragStartY = pos.Y;
            e.Pointer.Capture(this);
        }
        else if (x < XAlpha) // lock column
        {
            OnToggleLock?.Invoke(layerIndex);
        }
        else if (x < XClip) // alpha lock column
        {
            OnToggleAlpha?.Invoke(layerIndex);
        }
        else if (x < XName) // clip column
        {
            OnToggleClip?.Invoke(layerIndex);
        }
        else // name / blend / opacity → select + drag
        {
            var layer = _layers[layerIndex];
            if (layer.IsGroup && e.ClickCount >= 2)
                OnToggleOpen?.Invoke(layerIndex);
            else
                OnSelectLayer?.Invoke(layerIndex);
            _dragRow    = panelRow;
            _dragStartY = pos.Y;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_sbDragging)
        {
            _sbDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_rowDragging && _dragRow >= 0 && _dropRow >= 0
            && _dropRow != _dragRow && _dropRow != _dragRow + 1)
        {
            int fromLayer    = LayerAt(_dragRow);
            // _dropRow is a panel insertion point (0..RowCount); convert to layer index
            int insertBefore = _dropRow < RowCount ? LayerAt(_dropRow) : -1;
            OnSelectLayer?.Invoke(fromLayer);
            OnMoveLayer?.Invoke(fromLayer, insertBefore);
        }

        _rowDragging = false;
        _dragRow     = -1;
        _dropRow     = -1;
        e.Pointer.Capture(null);
        UpdateHover(e.GetCurrentPoint(this).Position);
        InvalidateVisual();
        e.Handled = true;
    }

    private void UpdateHover(Point pos)
    {
        int newHover = -1;
        if (pos.X < Bounds.Width - SbW)
        {
            int r = RowAtY(pos.Y);
            if (r >= 0 && r < RowCount) newHover = r;
        }
        if (newHover != _hoverRow) { _hoverRow = newHover; InvalidateVisual(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    internal static string BlendAbbr(string m) => m switch
    {
        "Normal"       => "Nrm",  "Multiply"     => "Mul",  "Screen"    => "Scr",
        "Overlay"      => "Ovl",  "SoftLight"    => "SfL",  "HardLight" => "HdL",
        "ColorDodge"   => "CDg",  "ColorBurn"    => "CBr",  "Darken"    => "Drk",
        "Lighten"      => "Ltn",  "Difference"   => "Dif",  "Exclusion" => "Exc",
        "LinearBurn"   => "LBr",  "LinearDodge"  => "LDg",  "VividLight"=> "VvL",
        "LinearLight"  => "LnL",  "PinLight"     => "PnL",  "HardMix"   => "HMx",
        "DarkerColor"  => "DkC",  "LighterColor" => "LtC",  "Subtract"  => "Sub",
        "Divide"       => "Div",  "Hue"          => "Hue",  "Saturation"=> "Sat",
        "Color"        => "Clr",  "Luminosity"   => "Lum",  "PassThrough"=> "Pss",
        "Dissolve"     => "Dsl",  _ => m.Length > 4 ? m[..4] : m
    };

    /// <summary>Scrolls so that the given layer index is fully visible.</summary>
    public void ScrollToRow(int layerIndex)
    {
        int panelRow = Array.IndexOf(_displayIndices, layerIndex);
        if (panelRow < 0) return;
        double h   = Bounds.Height;
        double top = panelRow * RowH;
        double bot = top + RowH;
        if (top < _scrollPx)
            _scrollPx = (int)top;
        else if (bot > _scrollPx + h)
            _scrollPx = (int)(bot - h);
        ClampScroll();
    }
}
