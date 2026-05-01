using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App;

public sealed record CurveChangedArgs(
    ResponseCurveKind Kind,
    float Gamma,
    float X1, float Y1,
    float X2, float Y2);

public sealed class CurveGraph : Control
{
    // Curve state
    private ResponseCurveKind _kind  = ResponseCurveKind.Power;
    private float _gamma             = 1.0f;
    private float _x1 = 0.25f, _y1  = 0.25f;
    private float _x2 = 0.75f, _y2  = 0.75f;

    // Interaction
    private enum DragTarget { None, Gamma, H1, H2 }
    private DragTarget _drag         = DragTarget.None;
    private Point      _dragOrigin;
    private float      _dragA, _dragB;

    // Mode button layout (populated in Render)
    private Rect _btnLinear, _btnPower, _btnBezier;
    private const double BtnH       = 18;
    private const double BtnW       = 22;
    private const double BtnGap     = 2;
    private const double TopPad     = BtnH + 4;  // graph starts below buttons
    private const double HitRadius  = 11;

    // Colors
    private static readonly IBrush BgBrush      = new SolidColorBrush(Color.Parse("#090b0f"));
    private static readonly IBrush GridBrush    = new SolidColorBrush(Color.Parse("#181c28"));
    private static readonly IBrush RefBrush     = new SolidColorBrush(Color.Parse("#222840"));
    private static readonly IBrush CurveBrush   = new SolidColorBrush(Color.Parse("#4c7ed8"));
    private static readonly IBrush TangentBrush = new SolidColorBrush(Color.Parse("#243560"));
    private static readonly IBrush AnchorBrush  = new SolidColorBrush(Color.Parse("#b0c4e8"));
    private static readonly IBrush HandleBrush  = new SolidColorBrush(Color.Parse("#4c7ed8"));
    private static readonly IBrush LabelBrush   = new SolidColorBrush(Color.Parse("#44506a"));
    private static readonly IBrush BtnBg        = new SolidColorBrush(Color.Parse("#141828"));
    private static readonly IBrush BtnActive    = new SolidColorBrush(Color.Parse("#1e2e5a"));
    private static readonly IBrush BtnTxt       = new SolidColorBrush(Color.Parse("#606888"));
    private static readonly IBrush BtnActiveTxt = new SolidColorBrush(Color.Parse("#80aaee"));
    private static readonly IBrush BtnBorder    = new SolidColorBrush(Color.Parse("#1e2438"));

    // ── Public API ────────────────────────────────────────────────────────────

    public ResponseCurveKind Kind
    {
        get => _kind;
        set { _kind = value; UpdateCursor(); InvalidateVisual(); }
    }
    public float Gamma  { get => _gamma; set { _gamma = Math.Clamp(value, 0.1f, 4f); InvalidateVisual(); } }
    public float X1     { get => _x1;   set { _x1 = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public float Y1     { get => _y1;   set { _y1 = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public float X2     { get => _x2;   set { _x2 = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public float Y2     { get => _y2;   set { _y2 = Math.Clamp(value, 0, 1); InvalidateVisual(); } }

    public event EventHandler<CurveChangedArgs>? CurveChanged;

    public CurveGraph() => UpdateCursor();

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;

        var gh = h - TopPad;
        var gy = TopPad;

        ctx.FillRectangle(BgBrush, new Rect(0, 0, w, h));

        RenderModeButtons(ctx, w);
        RenderGrid(ctx, w, gh, gy);
        RenderCurve(ctx, w, gh, gy);
        if (_kind == ResponseCurveKind.Bezier)
            RenderBezierHandles(ctx, w, gh, gy);
        RenderLabel(ctx, gh, gy);
    }

    private void RenderModeButtons(DrawingContext ctx, double w)
    {
        var totalW = BtnW * 3 + BtnGap * 2;
        var sx     = w - totalW - 3;
        _btnLinear = new Rect(sx,                        2, BtnW, BtnH);
        _btnPower  = new Rect(sx + BtnW + BtnGap,        2, BtnW, BtnH);
        _btnBezier = new Rect(sx + (BtnW + BtnGap) * 2,  2, BtnW, BtnH);

        DrawBtn(ctx, _btnLinear, "—",  _kind == ResponseCurveKind.Linear);
        DrawBtn(ctx, _btnPower,  "∿",  _kind == ResponseCurveKind.Power);
        DrawBtn(ctx, _btnBezier, "⌇",  _kind == ResponseCurveKind.Bezier);
    }

    private static void DrawBtn(DrawingContext ctx, Rect r, string text, bool active)
    {
        ctx.FillRectangle(active ? BtnActive : BtnBg, r);
        ctx.DrawRectangle(new Pen(BtnBorder, 1), r);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, active ? BtnActiveTxt : BtnTxt);
        ctx.DrawText(ft, new Point(
            r.X + (r.Width  - ft.Width)  * 0.5,
            r.Y + (r.Height - ft.Height) * 0.5));
    }

    private static void RenderGrid(DrawingContext ctx, double w, double gh, double gy)
    {
        var gp = new Pen(GridBrush, 1);
        for (var i = 1; i < 4; i++)
        {
            ctx.DrawLine(gp, new Point(w * i / 4.0, gy),      new Point(w * i / 4.0, gy + gh));
            ctx.DrawLine(gp, new Point(0,  gy + gh * i / 4.0), new Point(w, gy + gh * i / 4.0));
        }
        // reference diagonal (linear)
        ctx.DrawLine(new Pen(RefBrush, 1), new Point(0, gy + gh), new Point(w, gy));
    }

    private void RenderCurve(DrawingContext ctx, double w, double gh, double gy)
    {
        const int Steps = 100;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(ToCanvas(0, Eval(0), w, gh, gy), isFilled: false);
            for (var i = 1; i <= Steps; i++)
                gc.LineTo(ToCanvas(i / (float)Steps, Eval(i / (float)Steps), w, gh, gy));
        }
        ctx.DrawGeometry(null, new Pen(CurveBrush, 2), geo);
    }

    private void RenderBezierHandles(DrawingContext ctx, double w, double gh, double gy)
    {
        var p0 = ToCanvas(0,   0,   w, gh, gy);
        var p1 = ToCanvas(_x1, _y1, w, gh, gy);
        var p2 = ToCanvas(_x2, _y2, w, gh, gy);
        var p3 = ToCanvas(1,   1,   w, gh, gy);

        // tangent lines
        var tanPen = new Pen(TangentBrush, 1, dashStyle: DashStyle.Dash);
        ctx.DrawLine(tanPen, p0, p1);
        ctx.DrawLine(tanPen, p3, p2);

        // anchors (filled)
        ctx.DrawEllipse(AnchorBrush, null, p0, 4, 4);
        ctx.DrawEllipse(AnchorBrush, null, p3, 4, 4);

        // handles (hollow squares)
        const double Hr = 5;
        var hp = new Pen(HandleBrush, 1.5);
        ctx.DrawRectangle(null, hp, new Rect(p1.X - Hr, p1.Y - Hr, Hr * 2, Hr * 2));
        ctx.DrawRectangle(null, hp, new Rect(p2.X - Hr, p2.Y - Hr, Hr * 2, Hr * 2));
    }

    private void RenderLabel(DrawingContext ctx, double gh, double gy)
    {
        var text = _kind switch
        {
            ResponseCurveKind.Linear => "linear",
            ResponseCurveKind.Power  => $"γ = {_gamma:0.00}  (drag to reshape)",
            ResponseCurveKind.Bezier => $"bezier  p1=({_x1:0.00},{_y1:0.00})  p2=({_x2:0.00},{_y2:0.00})",
            _                        => ""
        };
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 8.5, LabelBrush);
        ctx.DrawText(ft, new Point(4, gy + 3));
    }

    // ── Math ──────────────────────────────────────────────────────────────────

    private float Eval(float x) => _kind switch
    {
        ResponseCurveKind.Linear => x,
        ResponseCurveKind.Bezier => BezierY(Math.Clamp(x, 0, 1)),
        _                        => MathF.Pow(Math.Clamp(x, 0, 1), Math.Max(0.01f, _gamma))
    };

    private float BezierY(float inputX)
    {
        // Binary search: find t such that CubicBezier(t, 0, x1, x2, 1) ≈ inputX
        var lo = 0f; var hi = 1f;
        for (var i = 0; i < 14; i++)
        {
            var mid = (lo + hi) * 0.5f;
            if (Cubic(mid, 0, _x1, _x2, 1) < inputX) lo = mid; else hi = mid;
        }
        return Cubic((lo + hi) * 0.5f, 0, _y1, _y2, 1);
    }

    private static float Cubic(float t, float p0, float p1, float p2, float p3)
    {
        var inv = 1 - t;
        return inv*inv*inv*p0 + 3*inv*inv*t*p1 + 3*inv*t*t*p2 + t*t*t*p3;
    }

    private static Point ToCanvas(float x, float y, double w, double gh, double gy)
        => new(x * w, gy + (1 - y) * gh);

    // ── Interaction ───────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);

        // Mode button clicks
        if (_btnLinear.Contains(pos)) { SwitchKind(ResponseCurveKind.Linear); e.Handled = true; return; }
        if (_btnPower.Contains(pos))  { SwitchKind(ResponseCurveKind.Power);  e.Handled = true; return; }
        if (_btnBezier.Contains(pos)) { SwitchKind(ResponseCurveKind.Bezier); e.Handled = true; return; }

        var gw = Bounds.Width; var gh = Bounds.Height - TopPad; var gy = TopPad;

        if (_kind == ResponseCurveKind.Bezier)
        {
            var h1 = ToCanvas(_x1, _y1, gw, gh, gy);
            var h2 = ToCanvas(_x2, _y2, gw, gh, gy);
            if (Dist(pos, h1) <= HitRadius)
            {
                _drag = DragTarget.H1; _dragOrigin = pos; _dragA = _x1; _dragB = _y1;
                e.Pointer.Capture(this); e.Handled = true; return;
            }
            if (Dist(pos, h2) <= HitRadius)
            {
                _drag = DragTarget.H2; _dragOrigin = pos; _dragA = _x2; _dragB = _y2;
                e.Pointer.Capture(this); e.Handled = true; return;
            }
        }
        else if (_kind == ResponseCurveKind.Power)
        {
            _drag = DragTarget.Gamma; _dragOrigin = pos; _dragA = _gamma;
            e.Pointer.Capture(this); e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragTarget.None) return;

        var pos = e.GetPosition(this);
        var gw  = Bounds.Width;
        var gh  = Bounds.Height - TopPad;
        var dx  = (float)((pos.X - _dragOrigin.X) / gw);
        var dy  = (float)((pos.Y - _dragOrigin.Y) / gh);

        switch (_drag)
        {
            case DragTarget.Gamma:
                _gamma = Math.Clamp(_dragA + dx * 3.9f, 0.1f, 4f);
                break;
            case DragTarget.H1:
                _x1 = Math.Clamp(_dragA + dx, 0, 1);
                _y1 = Math.Clamp(_dragB - dy, 0, 1);
                break;
            case DragTarget.H2:
                _x2 = Math.Clamp(_dragA + dx, 0, 1);
                _y2 = Math.Clamp(_dragB - dy, 0, 1);
                break;
        }

        InvalidateVisual();
        Emit();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = DragTarget.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    private void SwitchKind(ResponseCurveKind kind)
    {
        _kind = kind;
        UpdateCursor();
        InvalidateVisual();
        Emit();
    }

    private void UpdateCursor()
    {
        Cursor = _kind == ResponseCurveKind.Power
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Arrow);
    }

    private void Emit()
        => CurveChanged?.Invoke(this, new CurveChangedArgs(_kind, _gamma, _x1, _y1, _x2, _y2));

    private static double Dist(Point a, Point b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
