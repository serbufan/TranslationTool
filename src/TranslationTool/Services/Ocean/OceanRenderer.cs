using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static TranslationTool.Services.Ocean.OceanMath;

namespace TranslationTool.Services.Ocean;

/// <summary>
/// ascii-sharks 的 renderer.ts 移植：把海洋世界绘制为 ASCII 字符网格 ——
/// 黑色网格底、青色尾流点、白色鱼群短线、逐格光栅化的鲨鱼（超采样边缘、
/// 明暗肤色、随机闪烁的点缀色）。输出到 WriteableBitmap（Bgra32）。
/// </summary>
public sealed class OceanRenderer : IDisposable
{
    private const int AlphaLevels = 6;
    private static readonly double[] FishAlphas = [0.28, 0.42, 0.58, 0.74, 0.9];

    // 尾流点的青色。
    private const byte WakeR = 110, WakeG = 230, WakeB = 255;

    private static readonly string[] Accents =
    [
        "#e0892c", "#e03a32", "#b44ae0", "#33c8cc", "#3cb54a", "#3150e8", "#d9c14a",
    ];

    // 亮皮肤上用稀疏字形、阴影里用密集字形，纹理与明暗互相强化。
    private const string SkinRamp = ":;t%SX8#@";
    private const string FinGlyphs = "%;tS:";
    private const string RidgeGlyphs = "@8%";
    private const string EdgeHeavy = ":;";
    private const string EdgeLight = ".',`";

    private const int FlagBody = 1;
    private const int FlagEdge = 2;
    private const int FlagFin = 3;
    private const int FlagRidge = 4;
    private const int FlagEye = 5;

    // 亚像元采样偏移，用于抗锯齿轮廓。
    private static readonly float[] Subsamples = [0.25f, 0.3f, 0.75f, 0.3f, 0.25f, 0.72f, 0.75f, 0.72f];

    /// <summary>经典鲨鱼俯视半宽，s ∈ [0,1]（吻 → 尾），占长度比例。</summary>
    private static double ClassicHalfWidth(double s)
    {
        if (s < 0.13) return 0.09 * Math.Pow(Math.Sin(s / 0.13 * Math.PI / 2), 0.95);
        if (s < 0.3) return 0.09 + 0.016 * SmoothStep(0.13, 0.3, s);
        if (s < 0.84) return 0.019 + 0.087 * Math.Pow(1 - (s - 0.3) / 0.54, 1.25);
        double k = (s - 0.84) / 0.16;
        return 0.019 * (1 - k) + 0.012 * Math.Sin(k * Math.PI);
    }

    // classic 形体参数（胸鳍根/尖、腹鳍、尾鳍、背鳍、眼、鳃、肤色乘子）。
    private static readonly (double P0, double P1, double P2, double Reach) Pectoral = (0.15, 0.43, 0.55, 0.26);
    private static readonly (double V0, double V1, double V2, double Vr) Pelvic = (0.57, 0.65, 0.69, 0.085);
    private const double Caudal = 0.085;
    private static readonly (double S0, double S1) Dorsal = (0.33, 0.47);
    private static readonly (double S0, double S1) SecondDorsal = (0.71, 0.76);
    private static readonly (double S0, double S1) EyeRange = (0.1, 0.14);
    private static readonly (double S0, double S1) Gills = (0.19, 0.29);
    private const double TintR = 0.92, TintG = 0.97, TintB = 1.05;

    public WriteableBitmap? Bitmap { get; private set; }
    public double CellWidth => _cw;
    public double CellHeight => _ch;

    private int _pxW, _pxH;
    private int _cw = 12, _ch = 18;
    private int _cols, _rows;

    private int[] _pixels = [];
    private int[] _bgPixels = [];
    private readonly Dictionary<char, byte[]> _glyphs = new();
    private readonly Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private float[] _dotPhase = [];
    private float[] _dotFreq = [];
    private float[] _energy = [];
    private float[] _energyTmp = [];
    private float[] _heat = [];

    private byte[] _sharkFlag = [];
    private float[] _sharkShade = [];
    private uint[] _sharkKey = [];
    private float[] _sharkDepth = [];
    private readonly float[] _spine = new float[OceanWorld.SharkNodes * 2];
    private readonly float[] _arc = new float[OceanWorld.SharkNodes];
    private float _arcTotal = 1;
    private double _finTipLeft, _finTipRight;
    private int _hitFlag;
    private double _hitShade, _hitS, _hitV, _hitDist;

    /// <summary>本帧有鱼的格子（帧戳）；点绘制时跳过。</summary>
    private uint[] _fishStamp = [];
    private uint _frameId;
    private readonly List<int>[] _fishBuckets = FishAlphas.Select(_ => new List<int>()).ToArray();

    public void Dispose()
    {
        Bitmap = null;
        _glyphs.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>width/height 为设备像素。</summary>
    public void Resize(int width, int height)
    {
        int prevCols = _cols, prevRows = _rows;
        _pxW = Math.Max(1, width);
        _pxH = Math.Max(1, height);
        _cw = (int)Clamp(Math.Round(_pxW / 124.0), 8, 13);
        _ch = (int)Math.Round(_cw * 1.45);
        _cols = (int)Math.Ceiling(_pxW / (double)_cw);
        _rows = (int)Math.Ceiling(_pxH / (double)_ch);

        int n = _cols * _rows;
        if (_cols != prevCols || _rows != prevRows)
        {
            _dotPhase = new float[n];
            _dotFreq = new float[n];
            _energy = new float[n];
            _energyTmp = new float[n];
            _heat = new float[n];
            _sharkFlag = new byte[n];
            _sharkShade = new float[n];
            _sharkKey = new uint[n];
            _sharkDepth = new float[n];
            _fishStamp = new uint[n];
        }

        for (int i = 0; i < n; i++)
        {
            int col = i % _cols;
            int row = i / _cols;
            _dotPhase[i] = (float)(Hash3(col, row, 23) * Math.PI * 2);
            _dotFreq[i] = (float)(0.3 + Hash3(col, row, 37) * 1.1);
        }

        PaintBackground();
        BuildGlyphAtlas();

        _pixels = new int[_pxW * _pxH];
        Bitmap ??= new WriteableBitmap(_pxW, _pxH, 96, 96, PixelFormats.Bgra32, null);
        if (Bitmap.PixelWidth != _pxW || Bitmap.PixelHeight != _pxH)
        {
            Bitmap = new WriteableBitmap(_pxW, _pxH, 96, 96, PixelFormats.Bgra32, null);
        }
    }

    /// <summary>黑底 + 暗灰网格线，与原版 paintBackground/paintGrid 一致。</summary>
    private void PaintBackground()
    {
        _bgPixels = new int[_pxW * _pxH];
        const int line = 14; // rgba(255,255,255,0.055) 在黑底上 ≈ 14
        Array.Fill(_bgPixels, Rgb(0, 0, 0));
        for (int c = 0; c <= (int)Math.Ceiling(_pxW / (double)_cw); c++)
        {
            int x = (int)Math.Round(c * (double)_cw);
            if (x >= _pxW) break;
            for (int y = 0; y < _pxH; y++) _bgPixels[y * _pxW + x] = Rgb(line, line, line);
        }
        for (int r = 0; r <= (int)Math.Ceiling(_pxH / (double)_ch); r++)
        {
            int y = (int)Math.Round(r * (double)_ch);
            if (y >= _pxH) break;
            int off = y * _pxW;
            for (int x = 0; x < _pxW; x++) _bgPixels[off + x] = Rgb(line, line, line);
        }
    }

    /// <summary>把字符渲染为 alpha 遮罩，绘制时按颜色混合。</summary>
    private void BuildGlyphAtlas()
    {
        _glyphs.Clear();
        const string chars = ":;t%SX8#@'.`,o-*·";
        double fontSize = Math.Round(_ch * 0.74);
        foreach (char c in chars)
        {
            var ft = new FormattedText(
                c.ToString(),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                fontSize,
                Brushes.White,
                null,
                1.0);

            var rtb = new RenderTargetBitmap(_cw, _ch, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, _cw, _ch));
                // 水平居中 + 垂直中线对齐（对应 canvas 的 center/middle）。
                double ox = (_cw - ft.Width) / 2;
                double oy = _ch / 2.0 + (ft.Baseline - ft.Height / 2.0);
                dc.DrawText(ft, new Point(ox, oy));
            }
            rtb.Render(dv);

            int stride = _cw * 4;
            var buf = new byte[stride * _ch];
            rtb.CopyPixels(buf, stride, 0);
            // 黑底白字：用亮度通道作字形遮罩（A 通道因底色不透明恒为 255，不可用）。
            var alpha = new byte[_cw * _ch];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = buf[i * 4];
            _glyphs[c] = alpha;
        }
    }

    public void Render(OceanWorld world, double dt)
    {
        if (Bitmap is null || _cols == 0) return;
        double t = world.Time;

        ConsumeEvents(world);
        RasterizeSharks(world.Sharks, t);
        DepositWake(world);
        Diffuse(dt);

        Array.Copy(_bgPixels, _pixels, _pixels.Length);

        MarkFish(world);
        DrawDots(t);
        DrawHeat();
        DrawFish();
        DrawSharks(t);

        Bitmap.WritePixels(new Int32Rect(0, 0, _pxW, _pxH), _pixels, _pxW * 4, 0);
    }

    // ── 模拟 → 场 ──────────────────────────────────────────────────

    private int CellIndex(double x, double y)
    {
        int c = (int)(x / _cw);
        int r = (int)(y / _ch);
        if (c < 0 || r < 0 || c >= _cols || r >= _rows) return -1;
        return r * _cols + c;
    }

    private void Stamp(float[] field, double x, double y, int radius, double amount)
    {
        int c0 = (int)(x / _cw);
        int r0 = (int)(y / _ch);
        for (int r = r0 - radius; r <= r0 + radius; r++)
        {
            for (int c = c0 - radius; c <= c0 + radius; c++)
            {
                if (c < 0 || r < 0 || c >= _cols || r >= _rows) continue;
                double d = Math.Sqrt((c - c0) * (c - c0) + (r - r0) * (r - r0) * 1.96);
                if (d > radius + 0.5) continue;
                int i = r * _cols + c;
                field[i] = (float)Math.Min(1.6, field[i] + amount * (1 - d / (radius + 1)));
            }
        }
    }

    private void ConsumeEvents(OceanWorld world)
    {
        foreach (var e in world.Events)
        {
            if (e.Kind == "eat")
            {
                Stamp(_heat, e.X, e.Y, 2, 1);
                Stamp(_energy, e.X, e.Y, 3, 0.9);
            }
        }
        world.Events.Clear();
    }

    private void DepositWake(OceanWorld world)
    {
        foreach (var f in world.FishList)
        {
            if (!f.Alive) continue;
            int i = CellIndex(f.X, f.Y);
            if (i >= 0) _energy[i] = (float)Math.Min(1.6, _energy[i] + 0.012 + f.Alarm * 0.03);
        }
        foreach (var sh in world.Sharks)
        {
            // 尾部扫水量最大。
            for (int k = OceanWorld.SharkNodes - 6; k < OceanWorld.SharkNodes; k++)
            {
                int i = CellIndex(sh.Nodes[k * 2], sh.Nodes[k * 2 + 1]);
                if (i >= 0) _energy[i] = (float)Math.Min(1.6, _energy[i] + 0.05 + sh.Speed * 0.0012);
            }
        }
    }

    /// <summary>模糊 + 衰减，让尾流像涟漪一样扩散、消退。</summary>
    private void Diffuse(double dt)
    {
        int cols = _cols, rows = _rows;
        var energy = _energy;
        var tmp = _energyTmp;
        var heat = _heat;
        double decay = Math.Exp(-dt * 1.5);
        for (int r = 0; r < rows; r++)
        {
            int rowOff = r * cols;
            for (int c = 0; c < cols; c++)
            {
                int i = rowOff + c;
                float l = c > 0 ? energy[i - 1] : energy[i];
                float rt = c < cols - 1 ? energy[i + 1] : energy[i];
                float u = r > 0 ? energy[i - cols] : energy[i];
                float d = r < rows - 1 ? energy[i + cols] : energy[i];
                tmp[i] = (energy[i] * 4 + l + rt + u + d) * 0.125f * (float)decay;
            }
        }
        _energy = tmp;
        _energyTmp = energy;
        double heatDecay = Math.Exp(-dt * 2.2);
        for (int i = 0; i < heat.Length; i++) heat[i] *= (float)heatDecay;
    }

    // ── 鲨鱼 ──────────────────────────────────────────────────────

    /// <summary>
    /// 把一个点对当前鲨鱼（脊柱/弧长已就绪）分类。
    /// 体坐标：s 沿脊柱（0 吻 → 1 尾），v 横向。结果写入 hit* 字段。
    /// </summary>
    private bool SampleShark(double px, double py, double length)
    {
        var spine = _spine;
        var arc = _arc;
        const int n = OceanWorld.SharkNodes;
        double best = double.PositiveInfinity;
        double bestS = 0, bestNx = 0, bestNy = 0;
        int bestSign = 1;

        for (int k = 0; k < n - 1; k++)
        {
            double ax = spine[k * 2];
            double ay = spine[k * 2 + 1];
            double sx = spine[(k + 1) * 2] - ax;
            double sy = spine[(k + 1) * 2 + 1] - ay;
            double len2 = sx * sx + sy * sy;
            if (len2 < 1) len2 = 1;
            double u = Clamp(((px - ax) * sx + (py - ay) * sy) / len2, 0, 1);
            double qx = ax + sx * u;
            double qy = ay + sy * u;
            double d2 = (px - qx) * (px - qx) + (py - qy) * (py - qy);
            if (d2 < best)
            {
                best = d2;
                double len = Math.Sqrt(len2);
                bestS = (arc[k] + u * len) / _arcTotal;
                bestSign = sx * (py - ay) - sy * (px - ax) >= 0 ? 1 : -1;
                bestNx = -sy / len * bestSign;
                bestNy = sx / len * bestSign;
            }
        }

        double a = Math.Sqrt(best);
        double s = bestS;
        double u2 = s * length;
        double w = ClassicHalfWidth(s) * length;
        _hitDist = a;
        _hitS = s;
        _hitV = a * bestSign;

        if (a <= w)
        {
            double across = a / (w < 1 ? 1 : w);
            // 圆背左上受光；侧腹渐入阴影。
            double facing = (bestNx * -0.6 + bestNy * -0.8) * across;
            double shade = 0.58 - across * across * 0.42 + facing * 0.26;
            int flag = FlagBody;
            if (s > Dorsal.S0 && s < Dorsal.S1 && across < 0.3)
            {
                // 第一背鳍，迎光。
                flag = FlagRidge;
                shade = 1;
            }
            else if (s > SecondDorsal.S0 && s < SecondDorsal.S1 && across < 0.4)
            {
                shade = Math.Max(shade, 0.82);
            }
            else if (s > EyeRange.S0 && s < EyeRange.S1 && across > 0.6)
            {
                flag = FlagEye;
                shade = 0;
            }
            else if (s > Gills.S0 && s < Gills.S1 && across > 0.58)
            {
                // 五道鳃裂。
                if (Math.Floor((s - Gills.S0) / (Gills.S1 - Gills.S0) * 5) % 2 == 0) shade *= 0.45;
            }
            else if (s < 0.05)
            {
                // 苍白的吻尖。
                shade = Math.Max(shade, 0.7);
            }
            _hitFlag = flag;
            _hitShade = shade;
            return true;
        }

        double tip = bestSign > 0 ? _finTipRight : _finTipLeft;
        if (InTriangle(u2, a, length * Pectoral.P0, 0, length * Pectoral.P1, 0, length * Pectoral.P2, tip) &&
            // 镰刀形鳍的凹后缘。
            !(InTriangle(u2, a, length * Pectoral.P1, 0, length * (Pectoral.P1 + (Pectoral.P2 - Pectoral.P1) * 0.33), tip * 0.55, length * Pectoral.P2, tip)))
        {
            _hitFlag = FlagFin;
            _hitShade = 0.38 + 0.22 * (1 - Clamp(a / tip, 0, 1));
            return true;
        }

        if (InTriangle(u2, a, length * Pelvic.V0, 0, length * Pelvic.V1, 0, length * Pelvic.V2, length * Pelvic.Vr) ||
            // 叉形尾鳍。
            (Caudal > 0 && InTriangle(u2, a, length * 0.86, 0, length * 0.95, 0, length, length * Caudal)))
        {
            _hitFlag = FlagFin;
            _hitShade = 0.36;
            return true;
        }
        return false;
    }

    private static bool InTriangle(
        double px, double py,
        double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>
    /// 用模拟脊柱构建平滑摆动的渲染脊柱，然后对包围盒内每个格子做超采样。
    /// 完全覆盖的格子成为实心皮肤；部分覆盖成为边缘字形。
    /// </summary>
    private void RasterizeSharks(List<OceanWorld.Shark> sharks, double t)
    {
        var sharkFlag = _sharkFlag;
        Array.Clear(sharkFlag);

        foreach (var sh in sharks)
        {
            double l = sh.Length;
            const int n = OceanWorld.SharkNodes;
            var nodes = sh.Nodes;
            double swim = Clamp(sh.Speed / 60, 0.4, 1.4);

            // 沿体向尾部增强的行波。
            for (int k = 0; k < n; k++)
            {
                double s = k / (double)(n - 1);
                int a = Math.Max(0, k - 1);
                int b = Math.Min(n - 1, k + 1);
                double tx = nodes[a * 2] - nodes[b * 2];
                double ty = nodes[a * 2 + 1] - nodes[b * 2 + 1];
                double tl = Math.Sqrt(tx * tx + ty * ty);
                if (tl < 1) tl = 1;
                tx /= tl;
                ty /= tl;
                double amp = l * 0.05 * (0.1 + 0.9 * Math.Pow(s, 1.8)) * swim;
                double off = amp * Math.Sin(sh.Phase - s * 5.2);
                _spine[k * 2] = (float)(nodes[k * 2] - ty * off);
                _spine[k * 2 + 1] = (float)(nodes[k * 2 + 1] + tx * off);
            }
            _arc[0] = 0;
            for (int k = 1; k < n; k++)
            {
                double dx = _spine[k * 2] - _spine[(k - 1) * 2];
                double dy = _spine[k * 2 + 1] - _spine[(k - 1) * 2 + 1];
                _arc[k] = _arc[k - 1] + (float)Math.Sqrt(dx * dx + dy * dy);
            }
            _arcTotal = _arc[n - 1];
            if (_arcTotal < 1) _arcTotal = 1;

            // 转弯侧倾：内侧鳍收缩，外侧鳍展开。
            double bank = Clamp(sh.Turn * 0.45, -0.35, 0.35);
            double flex = 1 + 0.06 * Math.Sin(sh.Phase * 0.5);
            double reachFin = Pectoral.Reach;
            _finTipRight = l * reachFin * flex * (1 - bank);
            _finTipLeft = l * reachFin * flex * (1 + bank);

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int k = 0; k < n; k++)
            {
                minX = Math.Min(minX, _spine[k * 2]);
                maxX = Math.Max(maxX, _spine[k * 2]);
                minY = Math.Min(minY, _spine[k * 2 + 1]);
                maxY = Math.Max(maxY, _spine[k * 2 + 1]);
            }
            double reach = l * 0.36;
            int c0 = Math.Max(0, (int)((minX - reach) / _cw));
            int c1 = Math.Min(_cols - 1, (int)((maxX + reach) / _cw));
            int r0 = Math.Max(0, (int)((minY - reach) / _ch));
            int r1 = Math.Min(_rows - 1, (int)((maxY + reach) / _ch));
            if (c0 > c1 || r0 > r1) continue;

            double skipBeyond = reach + Math.Sqrt(_cw * _cw + _ch * _ch);

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    double x = c * (double)_cw;
                    double y = r * (double)_ch;
                    bool centerHit = SampleShark(x + _cw / 2.0, y + _ch / 2.0, l);
                    if (!centerHit && _hitDist > skipBeyond) continue;
                    int flag = _hitFlag;
                    double shade = _hitShade;
                    double s = _hitS;
                    double v = _hitV;

                    int hits = centerHit ? 1 : 0;
                    for (int k = 0; k < Subsamples.Length; k += 2)
                    {
                        if (SampleShark(x + Subsamples[k] * _cw, y + Subsamples[k + 1] * _ch, l)) hits++;
                    }
                    if (hits == 0) continue;
                    double coverage = hits / 5.0;

                    int i = r * _cols + c;
                    // 体锚定的 key，让每个字形跟着皮肤游动。
                    _sharkKey[i] = (uint)(Hash3((int)Math.Round(s * 52), (int)Math.Round(v / (_cw * 0.9)), sh.Id) * 4294967295f);
                    _sharkDepth[i] = (float)sh.Depth;

                    if (centerHit && coverage >= 0.6)
                    {
                        sharkFlag[i] = (byte)flag;
                        double flicker = (Hash3(c, r, (int)Math.Floor(t * 6)) - 0.5) * 0.06;
                        _sharkShade[i] = (float)Clamp(shade + flicker, 0, 1);
                    }
                    else
                    {
                        sharkFlag[i] = FlagEdge;
                        _sharkShade[i] = (float)coverage;
                    }
                }
            }
        }
    }

    private void DrawSharks(double t)
    {
        int n = _cols * _rows;
        for (int i = 0; i < n; i++)
        {
            int flag = _sharkFlag[i];
            if (flag == 0) continue;
            int col = i % _cols;
            int row = i / _cols;
            int x = col * _cw;
            int y = row * _ch;
            uint key = _sharkKey[i];
            int keyIdx = (int)(key & 0x7FFFFFFF); // 取非负值用于索引
            double shade = _sharkShade[i];
            double dim = 0.7 + 0.3 * _sharkDepth[i];

            // 颜色按错峰时钟重掷，皮肤闪烁。
            int tick = (int)Math.Floor(t * 2.4 + key % 997 / 97.0);
            double roll = Hash3((int)key, tick, 7);

            if (flag == FlagEdge)
            {
                // 柔和抗锯齿轮廓：只画浅字形，不画色块。
                string set = shade >= 0.5 ? EdgeHeavy : EdgeLight;
                double g = (70 + shade * 80) * dim;
                int gr = (int)Math.Round(g * TintR);
                int gg = (int)Math.Round(g * TintG);
                int gb = Math.Min(255, (int)Math.Round(g * TintB + 6));
                string accentHex = Accents[keyIdx % Accents.Length];
                byte ar = roll < 0.07 ? AccentByte(accentHex, 0) : (byte)Clamp(gr, 0, 255);
                byte ag = roll < 0.07 ? AccentByte(accentHex, 1) : (byte)Clamp(gg, 0, 255);
                byte ab = roll < 0.07 ? AccentByte(accentHex, 2) : (byte)Clamp(gb, 0, 255);
                char glyph = set[(keyIdx >>> 8) % set.Length];
                BlitGlyph(glyph, x, y, ar, ag, ab, 255);
                continue;
            }

            int level;
            char ch2;
            if (flag == FlagRidge)
            {
                level = 226;
                ch2 = RidgeGlyphs[(keyIdx >>> 8) % RidgeGlyphs.Length];
            }
            else if (flag == FlagEye)
            {
                level = 12;
                ch2 = 'o';
            }
            else if (flag == FlagFin)
            {
                level = (int)(40 + shade * 120);
                ch2 = FinGlyphs[(keyIdx >>> 8) % FinGlyphs.Length];
            }
            else
            {
                level = (int)(46 + shade * 160);
                int jitter = (keyIdx >>> 4) % 3 - 1;
                ch2 = SkinRamp[(int)Clamp(Math.Floor((1 - shade) * 8.99) + jitter, 0, SkinRamp.Length - 1)];
            }
            level = (int)(level * dim);
            // 按肤色着色；classic 是冷水下的蓝灰背。
            byte br = (byte)Math.Min(255, (int)Math.Round(level * TintR));
            byte bg = (byte)Math.Min(255, (int)Math.Round(level * TintG));
            byte bb = (byte)Math.Min(255, (int)Math.Round(level * TintB + 4));
            FillRect(x + 1, y + 1, _cw - 1, _ch - 1, br, bg, bb);

            byte cr, cg, cb;
            if (flag == FlagEye) (cr, cg, cb) = (0x9a, 0xa0, 0xad);
            else if (roll < 0.09)
            {
                var accent = Accents[keyIdx % Accents.Length];
                cr = AccentByte(accent, 0);
                cg = AccentByte(accent, 1);
                cb = AccentByte(accent, 2);
            }
            else if (roll < 0.13) (cr, cg, cb) = (255, 255, 255);
            else if (shade > 0.55) (cr, cg, cb) = (0x0e, 0x0f, 0x13);
            else (cr, cg, cb) = (0x1b, 0x1d, 0x24);
            BlitGlyph(ch2, x, y, cr, cg, cb, 255);
        }
    }

    private static byte AccentByte(string hex, int index)
    {
        // "#rrggbb"
        return Convert.ToByte(hex.Substring(1 + index * 2, 2), 16);
    }

    // ── 鱼、点、热度 ──────────────────────────────────────────────

    /// <summary>
    /// 把每条鱼吸附到格子并按亮度分桶：远处的暗、近处的亮，转弯或惊慌的鱼闪光。
    /// 两条鱼落进同一格时，较近者先占。
    /// </summary>
    private void MarkFish(OceanWorld world)
    {
        var fishStamp = _fishStamp;
        var fishBuckets = _fishBuckets;
        var sharkFlag = _sharkFlag;
        uint frame = ++_frameId;
        foreach (var bucket in fishBuckets) bucket.Clear();
        foreach (var f in world.FishList)
        {
            if (!f.Alive) continue;
            int i = CellIndex(f.X, f.Y);
            if (i < 0 || fishStamp[i] == frame || sharkFlag[i] != 0) continue;
            fishStamp[i] = frame;
            double glint = Clamp(Math.Abs(f.Turn) / 3.5, 0, 1);
            double v = 0.3 + 0.4 * (f.Depth - 0.55) / 0.45 + 0.3 * Math.Max(glint, f.Alarm);
            fishBuckets[(int)Clamp(Math.Floor(v * FishAlphas.Length), 0, FishAlphas.Length - 1)].Add(i);
        }
    }

    private void DrawFish()
    {
        for (int b = 0; b < _fishBuckets.Length; b++)
        {
            var bucket = _fishBuckets[b];
            if (bucket.Count == 0) continue;
            byte a = (byte)Math.Round(FishAlphas[b] * 255);
            foreach (int i in bucket)
            {
                int x = i % _cols * _cw;
                int y = i / _cols * _ch;
                BlitGlyph('-', x, y, 230, 238, 255, a);
            }
        }
    }

    private void DrawHeat()
    {
        for (int i = 0; i < _heat.Length; i++)
        {
            float h = _heat[i];
            if (h < 0.08) continue;
            int col = i % _cols;
            int row = i / _cols;
            byte r, g, b2;
            if (h > 0.5)
            {
                r = 255; g = 120; b2 = 60;
            }
            else
            {
                r = 220; g = 40; b2 = 40;
                h *= 1.6f;
            }
            byte a = (byte)Clamp(Math.Round(h * 255), 0, 255);
            BlitGlyph(h > 0.6 ? '*' : '·', col * _cw, row * _ch, r, g, b2, a);
        }
    }

    /// <summary>尾流能量强的格子亮起青色小点。</summary>
    private void DrawDots(double t)
    {
        int n = _cols * _rows;
        int small = Math.Max(2, (int)Math.Round(_cw * 0.19));
        int size = small + 1; // 尾流用大点
        for (int i = 0; i < n; i++)
        {
            float e = _energy[i];
            if (_sharkFlag[i] != 0 || _fishStamp[i] == _frameId || e < 0.45) continue;
            int level = (int)Clamp(Math.Floor((e - 0.45) * 5), 0, AlphaLevels - 1);
            byte a = (byte)Math.Round((level + 1) / (double)AlphaLevels * 255);
            int col = i % _cols;
            int row = i / _cols;
            int x = (int)Math.Round(col * (double)_cw + _cw * 0.66 - size / 2.0);
            int y = (int)Math.Round(row * (double)_ch + _ch * 0.64 - size / 2.0);
            FillRect(x, y, size, size, WakeR, WakeG, WakeB, a);
        }
        _ = t; // 环境点已省略，t 仅用于与原版签名对齐
    }

    // ── 像素原语 ──────────────────────────────────────────────────

    /// <summary>Bgra32 下 int 布局为 0xAARRGGBB（低字节是 B）。</summary>
    private static int Rgb(int r, int g, int b) => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    private void FillRect(int px, int py, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        int c0 = Math.Max(0, px);
        int c1 = Math.Min(_pxW, px + w);
        int r0 = Math.Max(0, py);
        int r1 = Math.Min(_pxH, py + h);
        if (c0 >= c1 || r0 >= r1) return;
        uint color = a >= 255
            ? 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b
            : 0u;
        if (a >= 255)
        {
            for (int y = r0; y < r1; y++)
            {
                int off = y * _pxW;
                for (int x = c0; x < c1; x++) _pixels[off + x] = unchecked((int)color);
            }
        }
        else
        {
            double alpha = a / 255.0;
            for (int y = r0; y < r1; y++)
            {
                int off = y * _pxW;
                for (int x = c0; x < c1; x++)
                {
                    int di = off + x;
                    int dst = _pixels[di];
                    int db = dst & 0xFF, dg = (dst >> 8) & 0xFF, dr = (dst >> 16) & 0xFF;
                    dr = (int)Math.Round(dr + (r - dr) * alpha);
                    dg = (int)Math.Round(dg + (g - dg) * alpha);
                    db = (int)Math.Round(db + (b - db) * alpha);
                    _pixels[di] = unchecked((int)(0xFF000000u | (uint)(dr << 16) | (uint)(dg << 8) | (uint)db));
                }
            }
        }
    }

    /// <summary>把字形遮罩按颜色/alpha 混合到缓冲区（px,py 为格子左上角）。</summary>
    private void BlitGlyph(char c, int px, int py, byte r, byte g, byte b, byte a)
    {
        if (!_glyphs.TryGetValue(c, out var mask)) return;
        if (a == 0) return;
        double alpha = a / 255.0;
        for (int yy = 0; yy < _ch; yy++)
        {
            int dy = py + yy;
            if (dy < 0 || dy >= _pxH) continue;
            int rowOff = yy * _cw;
            int dstRow = dy * _pxW;
            for (int xx = 0; xx < _cw; xx++)
            {
                int m = mask[rowOff + xx];
                if (m == 0) continue;
                int dx = px + xx;
                if (dx < 0 || dx >= _pxW) continue;
                int ma = (int)Math.Round(m * alpha);
                if (ma == 0) continue;
                int di = dstRow + dx;
                int dst = _pixels[di];
                int db = dst & 0xFF, dg = (dst >> 8) & 0xFF, dr = (dst >> 16) & 0xFF;
                dr += (r - dr) * ma / 255;
                dg += (g - dg) * ma / 255;
                db += (b - db) * ma / 255;
                _pixels[di] = unchecked((int)(0xFF000000u | (uint)(dr << 16) | (uint)(dg << 8) | (uint)db));
            }
        }
    }
}
