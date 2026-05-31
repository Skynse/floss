namespace Floss.App.Brushes.Engine;

public readonly struct StrokePoint
{
    public readonly float X, Y;
    public readonly float Pressure;
    public readonly float TiltX, TiltY, Twist;
    public readonly float DrawingAngle;      // radians, direction of stroke motion
    public readonly float Speed;             // normalized 0–1
    public readonly float TotalDistance;     // cumulative pixels since stroke start
    public readonly int DabSeqNo;            // dab index since stroke start
    public readonly float Random;            // per-dab random [0,1]
    public readonly float StrokeRandom;        // per-stroke random [0,1]
    public readonly long TimeMicros;           // sample timestamp for dtime calc

    // MyPaint-style filtered inputs for dynamics
    public readonly float NormSpeed1Slow;    // filtered speed 1 (log-mapped ready)
    public readonly float NormSpeed2Slow;    // filtered speed 2
    public readonly float Direction;         // direction angle, 0–180 degrees
    public readonly float DirectionAngle;    // direction angle, 0–360 degrees
    public readonly float Stroke;            // stroke progress 0–1
    public readonly float CustomInput;       // custom input 0–1

    public StrokePoint(
        float x, float y, float pressure,
        float tiltX, float tiltY, float twist,
        float drawingAngle, float speed,
        float totalDistance, int dabSeqNo,
        float random, float strokeRandom,
        long timeMicros = 0,
        float normSpeed1Slow = 0, float normSpeed2Slow = 0,
        float direction = 0, float directionAngle = 0,
        float stroke = 0, float customInput = 0)
    {
        X = x; Y = y; Pressure = pressure;
        TiltX = tiltX; TiltY = tiltY; Twist = twist;
        DrawingAngle = drawingAngle; Speed = speed;
        TotalDistance = totalDistance; DabSeqNo = dabSeqNo;
        Random = random; StrokeRandom = strokeRandom;
        TimeMicros = timeMicros;
        NormSpeed1Slow = normSpeed1Slow;
        NormSpeed2Slow = normSpeed2Slow;
        Direction = direction;
        DirectionAngle = directionAngle;
        Stroke = stroke;
        CustomInput = customInput;
    }
}
