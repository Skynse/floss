using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Config;

namespace Floss.App.Controls;

using static AppColors;

/// <summary>
/// App-wide scrub bar: solid/gradient track, optional value fill, triangle marker below.
/// Click anywhere on the track band to jump; drag to scrub. Same value API as <see cref="RangeBase"/>.
/// </summary>
public sealed class ScrubSlider : Border
{
    public static readonly StyledProperty<double> MinimumProperty =
        RangeBase.MinimumProperty.AddOwner<ScrubSlider>();

    public static readonly StyledProperty<double> MaximumProperty =
        RangeBase.MaximumProperty.AddOwner<ScrubSlider>();

    public static readonly StyledProperty<double> ValueProperty =
        RangeBase.ValueProperty.AddOwner<ScrubSlider>();

    public static readonly StyledProperty<IBrush?> TrackBackgroundProperty =
        AvaloniaProperty.Register<ScrubSlider, IBrush?>(nameof(TrackBackground));

    public static readonly StyledProperty<bool> ShowValueFillProperty =
        AvaloniaProperty.Register<ScrubSlider, bool>(nameof(ShowValueFill), true);

    private const double TrackHeight = 11;
    private const double MarkerBandHeight = 10;
    private const double MarkerHalfWidth = 5;
    private const double MarkerHeight = 7;

    private readonly Border _track;
    private readonly Border _valueFill;
    private readonly Path _marker;
    private readonly Border _hitPlate;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly TranslateTransform _markerTranslate = new(0, 0);

    private bool _dragging;
    private double _pointerTrackLeft;
    private double _pointerTrackWidth;

    public bool IsScrubbing => _dragging;

    public event EventHandler<double>? ScrubCompleted;

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? TrackBackground
    {
        get => GetValue(TrackBackgroundProperty);
        set => SetValue(TrackBackgroundProperty, value);
    }

    public bool ShowValueFill
    {
        get => GetValue(ShowValueFillProperty);
        set => SetValue(ShowValueFillProperty, value);
    }

    static ScrubSlider()
    {
        MinimumProperty.OverrideDefaultValue<ScrubSlider>(0);
        MaximumProperty.OverrideDefaultValue<ScrubSlider>(100);
        ValueProperty.OverrideDefaultValue<ScrubSlider>(0);
    }

    public ScrubSlider()
    {
        Background = global::Avalonia.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        MinHeight = TrackHeight + MarkerBandHeight + 2;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        // Stretch to track width; visible extent is driven only by ScaleTransform (no Width changes).
        _valueFill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse(SliderFill)),
            CornerRadius = new CornerRadius(2, 0, 0, 2),
            IsHitTestVisible = false,
            RenderTransform = _fillScale,
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative)
        };

        _track = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(2),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Background = DefaultTrackBrush(),
            Child = _valueFill
        };

        _marker = new Path
        {
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Color.Parse("#2a2a2a")),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Data = CreateMarkerGeometry(),
            RenderTransform = _markerTranslate
        };

        var trackStack = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            ]
        };
        Grid.SetRow(_track, 0);
        Grid.SetRow(_marker, 1);
        trackStack.Children.Add(_track);
        trackStack.Children.Add(_marker);

        _hitPlate = new Border
        {
            Background = global::Avalonia.Media.Brushes.Transparent,
            Child = trackStack
        };
        _hitPlate.PointerPressed += OnHitPlatePressed;
        _hitPlate.PointerMoved += OnHitPlateMoved;
        _hitPlate.PointerReleased += OnHitPlateReleased;
        _hitPlate.PointerCaptureLost += OnPointerCaptureLost;

        Child = _hitPlate;
        AttachedToVisualTree += (_, _) => ApplyVisuals(ComputeNormalizedT());
        ApplyTrackBackground();
    }

    private static StreamGeometry CreateMarkerGeometry()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(MarkerHalfWidth, 0), true);
            ctx.LineTo(new Point(0, MarkerHeight));
            ctx.LineTo(new Point(MarkerHalfWidth * 2, MarkerHeight));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    private static SolidColorBrush DefaultTrackBrush()
        => new(Color.Parse(SliderTrack));

    private void ApplyTrackBackground()
    {
        _track.Background = TrackBackground ?? DefaultTrackBrush();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 0.5)
            ApplyVisuals(ComputeNormalizedT());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackBackgroundProperty)
            ApplyTrackBackground();
        else if (change.Property == ShowValueFillProperty
                 || change.Property == ValueProperty
                 || change.Property == MinimumProperty
                 || change.Property == MaximumProperty)
            ApplyVisuals(ComputeNormalizedT());
    }

    private void OnHitPlatePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_hitPlate).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        CachePointerTrack();
        SetValueFromPointer(e.GetPosition(_hitPlate));
        e.Pointer.Capture(_hitPlate);
        e.Handled = true;
    }

    private void OnHitPlateMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        SetValueFromPointer(e.GetPosition(_hitPlate));
        e.Handled = true;
    }

    private void OnHitPlateReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        EndScrub();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, EventArgs e)
    {
        if (!_dragging) return;
        EndScrub();
    }

    private void EndScrub()
    {
        _dragging = false;
        ScrubCompleted?.Invoke(this, Value);
    }

    private void CachePointerTrack()
    {
        _pointerTrackWidth = _track.Bounds.Width;
        _pointerTrackLeft = (_track.TranslatePoint(new Point(0, 0), _hitPlate) ?? new Point(0, 0)).X;
    }

    private void SetValueFromPointer(Point pos)
    {
        var trackW = _pointerTrackWidth > 0 ? _pointerTrackWidth : _track.Bounds.Width;
        if (trackW <= 0) return;

        var x = pos.X - _pointerTrackLeft;
        var t = Math.Clamp(x / trackW, 0, 1);
        ApplyVisuals(t);

        var min = Minimum;
        var max = Maximum;
        var next = min + t * (max - min);
        next = Math.Clamp(next, min, max);

        var range = Math.Max(max - min, double.Epsilon);
        var epsilon = Math.Max(range * 1e-4, 1e-9);
        if (Math.Abs(next - Value) < epsilon)
            return;

        SetCurrentValue(ValueProperty, next);
    }

    private double ComputeNormalizedT()
    {
        var min = Minimum;
        var max = Maximum;
        return Math.Clamp((Value - min) / Math.Max(0.0001, max - min), 0, 1);
    }

    private double TrackWidth()
    {
        var w = _track.Bounds.Width;
        return w > 0 ? w : Bounds.Width;
    }

    private void ApplyVisuals(double t)
    {
        t = Math.Clamp(t, 0, 1);

        if (ShowValueFill)
        {
            _valueFill.IsVisible = true;
            _fillScale.ScaleX = t;
        }
        else
        {
            _valueFill.IsVisible = false;
        }

        var w = TrackWidth();
        if (w <= 0) return;

        // Track can be narrower than the marker during docker squeeze; keep clamp bounds ordered.
        var cxMin = Math.Min(MarkerHalfWidth, w - MarkerHalfWidth);
        var cxMax = Math.Max(MarkerHalfWidth, w - MarkerHalfWidth);
        var cx = Math.Clamp(t * w, cxMin, cxMax);
        _markerTranslate.X = cx - MarkerHalfWidth;
    }
}
