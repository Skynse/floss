namespace Floss.App.Brushes;

public readonly struct StrokePoint
{
    public readonly float X, Y;
    public readonly float Pressure;
    public readonly float TiltX, TiltY, Twist;
    public readonly float DrawingAngle;   // radians, direction of stroke motion
    public readonly float Speed;          // normalized 0–1
    public readonly float TotalDistance;  // cumulative pixels since stroke start
    public readonly int DabSeqNo;       // dab index since stroke start
    public readonly float Random;         // per-dab random [0,1]
    public readonly float StrokeRandom;   // per-stroke random [0,1]

    public StrokePoint(
        float x, float y, float pressure,
        float tiltX, float tiltY, float twist,
        float drawingAngle, float speed,
        float totalDistance, int dabSeqNo,
        float random, float strokeRandom)
    {
        X = x; Y = y; Pressure = pressure;
        TiltX = tiltX; TiltY = tiltY; Twist = twist;
        DrawingAngle = drawingAngle; Speed = speed;
        TotalDistance = totalDistance; DabSeqNo = dabSeqNo;
        Random = random; StrokeRandom = strokeRandom;
    }
}
