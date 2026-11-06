namespace TranslationTool.Services.Ocean;

/// <summary>小鱼物种参数（species.ts 移植）。</summary>
public sealed class OceanSpecies
{
    public required string Name { get; init; }
    public double Cruise { get; init; }
    public double MaxSpeed { get; init; }
    public double MinSpeed { get; init; }
    /// <summary>新生鱼群选取该物种的相对权重。</summary>
    public double Weight { get; init; }
    public (int Lo, int Hi) SchoolSize { get; init; }
    /// <summary>个体间距，单位为网格单元 (x, y)，保证每条鱼独占一个字符位。</summary>
    public (double X, double Y) Spacing { get; init; }
    public double Align { get; init; }
    public double Cohesion { get; init; }
    public double Jitter { get; init; }
    /// <summary>绕局部中心的切向拉力，>0 时鱼群旋成饵球。</summary>
    public double Mill { get; init; }
    /// <summary>阻尼垂直速度：真实的鱼大多水平巡游。</summary>
    public double VerticalDrag { get; init; }

    public static readonly OceanSpecies[] All =
    [
        new() { Name = "sardine", Cruise = 36, MaxSpeed = 64, MinSpeed = 18, Weight = 3,
                SchoolSize = (18, 28), Spacing = (1.8, 1.1),
                Align = 1.6, Cohesion = 1.6, Jitter = 40, Mill = 70, VerticalDrag = 0.5 },
        new() { Name = "anchovy", Cruise = 46, MaxSpeed = 78, MinSpeed = 22, Weight = 3,
                SchoolSize = (20, 32), Spacing = (1.5, 1),
                Align = 3.2, Cohesion = 0.45, Jitter = 30, Mill = 0, VerticalDrag = 1.4 },
        new() { Name = "silverside", Cruise = 40, MaxSpeed = 70, MinSpeed = 20, Weight = 3,
                SchoolSize = (12, 20), Spacing = (2, 1.1),
                Align = 2.4, Cohesion = 1.1, Jitter = 35, Mill = 0, VerticalDrag = 1.6 },
        new() { Name = "mackerel", Cruise = 32, MaxSpeed = 60, MinSpeed = 16, Weight = 2,
                SchoolSize = (5, 9), Spacing = (2.6, 1.3),
                Align = 2, Cohesion = 0.9, Jitter = 25, Mill = 0, VerticalDrag = 2 },
        new() { Name = "goby", Cruise = 26, MaxSpeed = 56, MinSpeed = 10, Weight = 1,
                SchoolSize = (8, 12), Spacing = (1.4, 1.1),
                Align = 0.35, Cohesion = 1.4, Jitter = 110, Mill = 0, VerticalDrag = 0.3 },
    ];

    private static readonly double TotalWeight = All.Sum(s => s.Weight);

    public static int Pick()
    {
        double r = Random.Shared.NextDouble() * TotalWeight;
        for (int i = 0; i < All.Length; i++)
        {
            r -= All[i].Weight;
            if (r <= 0) return i;
        }
        return 0;
    }
}
