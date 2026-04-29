namespace Floss.App.Brushes;

public struct StrokeState
{
    public float LastX;
    public float LastY;
    public float LastPressure;
    public float LastTiltX;
    public float LastTiltY;
    public float DistanceLeftover;

    public StrokeState(float x, float y, float pressure, float tiltX, float tiltY)
    {
        LastX = x;
        LastY = y;
        LastPressure = pressure;
        LastTiltX = tiltX;
        LastTiltY = tiltY;
        DistanceLeftover = 0;
    }
}
