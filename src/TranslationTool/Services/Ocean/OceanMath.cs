namespace TranslationTool.Services.Ocean;

/// <summary>ascii-sharks 的 math.ts 移植。</summary>
public static class OceanMath
{
    public const double TAU = Math.PI * 2;

    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    public static double Rand(double lo, double hi) => lo + Random.Shared.NextDouble() * (hi - lo);

    public static int RandInt(int lo, int hi) => (int)Math.Floor(Rand(lo, hi + 1));

    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>从 a 到 b 的最短带符号角度差，范围 (-PI, PI]。</summary>
    public static double AngleDiff(double a, double b)
    {
        double d = (b - a) % TAU;
        if (d > Math.PI) d -= TAU;
        if (d < -Math.PI) d += TAU;
        return d;
    }

    /// <summary>廉价整数哈希 → [0,1)。确定性，纹理稳定。</summary>
    public static float Hash3(int x, int y, int z)
    {
        unchecked
        {
            int h = (x * 374761393) ^ (y * 668265263) ^ (z * 1442695041);
            h = (h ^ (int)((uint)h >> 13)) * 1274126177;
            h ^= (int)((uint)h >> 16);
            return (uint)h / 4294967296f;
        }
    }

    private const double K1 = 1 / 260.0;
    private const double K2 = 1 / 310.0;
    private const double K3 = 1 / 190.0;
    private const double CurrentScale = 3400;

    /// <summary>无散度洋流：慢漂移流函数 ψ(x,y,t) 的旋度，把鱼群推动而不聚堆。</summary>
    public static void Current(double x, double y, double t, ref double ox, ref double oy)
    {
        double a = x * K1 + t * 0.07;
        double b = y * K2 - t * 0.05;
        double c = (x + y) * K3 + t * 0.11;
        ox = (-K2 * Math.Sin(a) * Math.Sin(b) + 0.5 * K3 * Math.Cos(c)) * CurrentScale;
        oy = (-K1 * Math.Cos(a) * Math.Cos(b) - 0.5 * K3 * Math.Cos(c)) * CurrentScale;
    }
}
