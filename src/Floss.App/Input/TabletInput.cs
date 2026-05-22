using System;
using Avalonia.Input;

namespace Floss.App.Input;

/// <summary>
/// Normalizes Avalonia pointer packets for graphics tablets.
/// Windows XP-Pen/Huion/Wacom setups vary: Windows Ink may report <see cref="PointerType.Pen"/>
/// with zero pressure on the down event, while "Windows Ink off" often falls back to mouse
/// events with no pressure at all until Avalonia's Wintab backend lands.
/// </summary>
internal static class TabletInput
{
    /// <summary>Lightest pressure sent to the brush engine when the pen is down but the driver reports 0.</summary>
    private const float PenContactPressureFloor = 0.01f;

    public static CanvasInputSource SourceFromPointer(PointerPoint point)
    {
        if (point.Properties.IsEraser)
            return CanvasInputSource.Eraser;

        // Some tablet drivers report the pen as a generic mouse but still send
        // pressure/tilt data. Detect that and treat it as a pen so dynamics work.
        if (point.Pointer.Type == PointerType.Mouse && HasTabletAxisData(point.Properties))
            return CanvasInputSource.Pen;

        return point.Pointer.Type switch
        {
            PointerType.Pen => CanvasInputSource.Pen,
            PointerType.Touch => CanvasInputSource.Touch,
            PointerType.Mouse => CanvasInputSource.Mouse,
            _ => CanvasInputSource.Unknown
        };
    }

    public static (CanvasInputSource Source, double Pressure) GetSampleInfo(PointerPoint point, CanvasInputPhase phase)
    {
        var props = point.Properties;
        var source = SourceFromPointer(point);
        var pressure = props.Pressure;
        if (double.IsNaN(pressure)) pressure = 0;

        if (source == CanvasInputSource.Mouse && props.IsLeftButtonPressed)
        {
            if (!HasTabletAxisData(props) && pressure <= 0)
                pressure = 1;
        }
        else if (source is CanvasInputSource.Pen or CanvasInputSource.Eraser)
        {
            if (pressure < 0) pressure = 0;
            // Windows Ink frequently reports 0 on the first down packet even though the
            // tip is on the surface. Keep the stroke alive until real values arrive.
            if (pressure <= 0 && (phase == CanvasInputPhase.Down || PenTipDown(point)))
                pressure = PenContactPressureFloor;
        }
        else if (source == CanvasInputSource.Touch && pressure < 0)
        {
            pressure = 0;
        }

        return (source, Math.Clamp(pressure, 0, 1));
    }

    /// <summary>True when this pointer should start or continue a paint stroke.</summary>
    public static bool IsPaintContact(PointerPoint point)
    {
        var props = point.Properties;
        if (props.IsEraser)
            return PenTipDown(point) || props.Pressure > 0;

        return point.Pointer.Type switch
        {
            PointerType.Pen => PenTipDown(point) || props.Pressure > 0,
            PointerType.Touch => props.IsLeftButtonPressed || props.Pressure > 0,
            PointerType.Mouse => props.IsLeftButtonPressed || props.Pressure > 0 || HasTabletAxisData(props),
            _ => false
        };
    }

    /// <summary>Resolve the canvas action for a pointer-down event.</summary>
    public static CanvasAction ResolveButtonAction(PointerPoint pt)
    {
        var props = pt.Properties;

        if (props.IsEraser)
        {
            if (props.IsMiddleButtonPressed)
                return (CanvasAction)App.Shortcuts.MiddleButtonAction;
            if (props.IsRightButtonPressed)
                return (CanvasAction)App.Shortcuts.RightButtonAction;
            return CanvasAction.PrimaryTool;
        }

        if (pt.Pointer.Type == PointerType.Pen)
        {
            if (props.IsMiddleButtonPressed)
                return (CanvasAction)App.Shortcuts.MiddleButtonAction;
            if (props.IsRightButtonPressed)
                return (CanvasAction)App.Shortcuts.RightButtonAction;
            // Pen down must not wait for pressure &gt; 0 — many drivers send that on move only.
            return CanvasAction.PrimaryTool;
        }

        if (pt.Pointer.Type == PointerType.Touch)
            return props.IsLeftButtonPressed || props.Pressure > 0
                ? CanvasAction.PrimaryTool
                : CanvasAction.None;

        if (props.IsMiddleButtonPressed)
            return (CanvasAction)App.Shortcuts.MiddleButtonAction;
        if (props.IsRightButtonPressed)
            return (CanvasAction)App.Shortcuts.RightButtonAction;
        if (props.IsLeftButtonPressed || props.Pressure > 0)
            return CanvasAction.PrimaryTool;
        return CanvasAction.None;
    }

    private static bool PenTipDown(PointerPoint point)
        => point.Pointer.Type == PointerType.Pen
           && !point.Properties.IsEraser
           && (point.Properties.IsLeftButtonPressed || point.Properties.IsBarrelButtonPressed);

    private static bool HasTabletAxisData(PointerPointProperties props)
        => props.Pressure > 0 || props.XTilt != 0 || props.YTilt != 0 || props.Twist != 0;
}
