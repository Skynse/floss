using System;

namespace Floss.App.MyPaint;

/// <summary>
/// Port of mypaint-mapping.c: control-points mapping for brush dynamics.
/// </summary>
public sealed class MyPaintMapping
{
    private sealed class ControlPoints
    {
        public readonly float[] XValues = new float[64];
        public readonly float[] YValues = new float[64];
        public int N;
    }

    private readonly ControlPoints[] _pointsList;
    private int _inputsUsed;

    public float BaseValue { get; set; }
    public int Inputs { get; }

    public MyPaintMapping(int inputs)
    {
        Inputs = inputs;
        _pointsList = new ControlPoints[inputs];
        for (int i = 0; i < inputs; i++)
            _pointsList[i] = new ControlPoints();
    }

    public void SetN(int input, int n)
    {
        if (n == 1) throw new ArgumentException("Cannot build a linear mapping with only one point", nameof(n));
        ControlPoints p = _pointsList[input];
        if (n != 0 && p.N == 0) _inputsUsed++;
        if (n == 0 && p.N != 0) _inputsUsed--;
        p.N = n;
    }

    public int GetN(int input) => _pointsList[input].N;

    public void SetPoint(int input, int index, float x, float y)
    {
        ControlPoints p = _pointsList[input];
        p.XValues[index] = x;
        p.YValues[index] = y;
    }

    public void GetPoint(int input, int index, out float x, out float y)
    {
        ControlPoints p = _pointsList[input];
        x = p.XValues[index];
        y = p.YValues[index];
    }

    public bool IsConstant => _inputsUsed == 0;
    public int InputsUsedN => _inputsUsed;

    public float Calculate(ReadOnlySpan<float> data)
    {
        float result = BaseValue;
        if (_inputsUsed == 0) return result;

        for (int j = 0; j < Inputs; j++)
        {
            ControlPoints p = _pointsList[j];
            if (p.N == 0) continue;

            float x = data[j];
            float x0 = p.XValues[0];
            float y0 = p.YValues[0];
            float x1 = p.XValues[1];
            float y1 = p.YValues[1];

            int i;
            for (i = 2; i < p.N && x > x1; i++)
            {
                x0 = x1;
                y0 = y1;
                x1 = p.XValues[i];
                y1 = p.YValues[i];
            }

            float y = (x0 == x1 || y0 == y1) ? y0 : (y1 * (x - x0) + y0 * (x1 - x)) / (x1 - x0);
            result += y;
        }
        return result;
    }
}
