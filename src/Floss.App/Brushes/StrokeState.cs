namespace Floss.App.Brushes;

public struct StrokeState
{
    public float LastX;
    public float LastY;
    public float LastPressure;
    public float LastTiltX;
    public float LastTiltY;
    public float DistanceLeftover;
    public float NextStampDistance;
    public double TimeLeftoverMs;
    public float TotalDistance;   // cumulative pixels painted in this stroke
    public int DabSeqNo;        // dabs placed since stroke start
    public float DrawingAngle;    // radians, direction of last stroke segment

    public StrokeState(float x, float y, float pressure, float tiltX, float tiltY)
    {
        LastX = x;
        LastY = y;
        LastPressure = pressure;
        LastTiltX = tiltX;
        LastTiltY = tiltY;
        DistanceLeftover = 0;
        NextStampDistance = 0;
        TimeLeftoverMs = 0;
        TotalDistance = 0;
        DabSeqNo = 0;
        DrawingAngle = 0;
    }
}
