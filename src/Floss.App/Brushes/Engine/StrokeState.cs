namespace Floss.App.Brushes.Engine;

public struct StrokeState
{
    public float LastX;
    public float LastY;
    public float LastPressure;
    public float LastTiltX;
    public float LastTiltY;
    public float PrevFromX;
    public float PrevFromY;
    public float DistanceLeftover;
    public float NextStampDistance;
    public double TimeLeftoverMs;
    public float TotalDistance;   // cumulative pixels painted in this stroke
    public int DabSeqNo;        // dabs placed since stroke start
    public float DrawingAngle;    // radians, direction of last stroke segment

    // pressure-dynamic stateful brush engine
    public float ActualX;          // slow-tracked position
    public float ActualY;
    public float NormDxSlow;       // filtered velocity vector (x)
    public float NormDySlow;       // filtered velocity vector (y)
    public float NormSpeed1Slow;   // filtered speed 1
    public float NormSpeed2Slow;   // filtered speed 2
    public float DirectionDx;      // filtered direction vector (180° ambiguity)
    public float DirectionDy;
    public float DirectionAngleDx; // filtered direction vector (360°)
    public float DirectionAngleDy;
    public float Stroke;           // stroke progress 0..1
    public bool StrokeStarted;
    public float CustomInput;
    public float PrevDabX;
    public float PrevDabY;
    public long PrevDabTimeMicros;

    public StrokeState(float x, float y, float pressure, float tiltX, float tiltY)
    {
        LastX = x;
        LastY = y;
        LastPressure = pressure;
        LastTiltX = tiltX;
        LastTiltY = tiltY;
        PrevFromX = x;
        PrevFromY = y;
        DistanceLeftover = 0;
        NextStampDistance = 0;
        TimeLeftoverMs = 0;
        TotalDistance = 0;
        DabSeqNo = 0;
        DrawingAngle = 0;

        ActualX = x;
        ActualY = y;
        NormDxSlow = 0;
        NormDySlow = 0;
        NormSpeed1Slow = 0;
        NormSpeed2Slow = 0;
        DirectionDx = 0;
        DirectionDy = 0;
        DirectionAngleDx = 0;
        DirectionAngleDy = 0;
        Stroke = 0;
        StrokeStarted = false;
        CustomInput = 0;
        PrevDabX = x;
        PrevDabY = y;
        PrevDabTimeMicros = 0;
    }
}
