namespace Floss.App.MyPaint;

/// <summary>
/// Knuth's ran_array RNG (adapted by Jon Nordby for libmypaint).
/// Ported from rng-double.c.
/// </summary>
public sealed class MyPaintRng
{
    // low quality settings, seems to work for MyPaint
    private const int Quality = 19;
    private const int Tt = 7;
    private const int Kk = 10;
    private const int Ll = 7;

    private readonly double[] _ranU = new double[Kk];
    private readonly double[] _ranfArrBuf = new double[Quality + 1];
    private int _ranfArrIndex = -1;

    public MyPaintRng(long seed)
    {
        SetSeed(seed);
    }

    public void SetSeed(long seed)
    {
        double[] u = new double[Kk + Kk - 1];
        double ulp = 1.0 / (1L << 30) / (1L << 22); // 2^-52
        double ss = 2.0 * ulp * ((seed & 0x3fffffff) + 2);

        for (int j = 0; j < Kk; j++)
        {
            u[j] = ss;
            ss += ss;
            if (ss >= 1.0) ss -= 1.0 - 2 * ulp;
        }
        u[1] += ulp;

        int s = (int)(seed & 0x3fffffff);
        for (int t = Tt - 1; t > 0;)
        {
            for (int j = Kk - 1; j > 0; j--)
            {
                u[j + j] = u[j];
                u[j + j - 1] = 0.0;
            }
            for (int j = Kk + Kk - 2; j >= Kk; j--)
            {
                u[j - (Kk - Ll)] = ModSum(u[j - (Kk - Ll)], u[j]);
                u[j - Kk] = ModSum(u[j - Kk], u[j]);
            }
            if ((s & 1) != 0)
            {
                for (int j = Kk; j > 0; j--) u[j] = u[j - 1];
                u[0] = u[Kk];
                u[Ll] = ModSum(u[Ll], u[Kk]);
            }
            if (s != 0) s >>= 1; else t--;
        }
        for (int j = 0; j < Ll; j++) _ranU[j + Kk - Ll] = u[j];
        for (int j = Ll; j < Kk; j++) _ranU[j - Ll] = u[j];

        for (int j = 0; j < 10; j++)
            GetArray(u, Kk + Kk - 1);

        _ranfArrIndex = -1;
    }

    private static double ModSum(double x, double y) => (x + y) - (int)(x + y);

    private void GetArray(double[] aa, int n)
    {
        for (int j = 0; j < Kk; j++) aa[j] = _ranU[j];
        for (int j = Kk; j < n; j++) aa[j] = ModSum(aa[j - Kk], aa[j - Ll]);
        int i = 0, jj = n;
        for (; i < Ll; i++, jj++) _ranU[i] = ModSum(aa[jj - Kk], aa[jj - Ll]);
        for (; i < Kk; i++, jj++) _ranU[i] = ModSum(aa[jj - Kk], _ranU[i - Ll]);
    }

    public double Next()
    {
        if (_ranfArrIndex < 0 || _ranfArrIndex >= Quality)
        {
            GetArray(_ranfArrBuf, Quality);
            _ranfArrBuf[Kk] = -1.0;
            _ranfArrIndex = 0;
        }
        return _ranfArrBuf[_ranfArrIndex++];
    }
}
