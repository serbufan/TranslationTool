using static TranslationTool.Services.Ocean.OceanMath;

namespace TranslationTool.Services.Ocean;

/// <summary>
/// ascii-sharks 的 world.ts 移植：小鱼群体（Reynolds boids，Couzin 式分区 +
/// 背后盲区 + 惊慌传染）与捕食它们的鲨鱼（游荡 + 追击 + 分离的转向力，
/// 跟随者脊柱链）。饥饿随时间累积、进食后下降；被吃掉的鱼稍后作为新鱼群
/// 从边缘游回，保持总量稳定。
/// </summary>
public sealed class OceanWorld
{
    public const int SharkNodes = 20;

    public sealed class Fish
    {
        public double X, Y, Vx, Vy;
        public int Species;
        /// <summary>0 平静 → 1 惊慌，会传染给邻居。</summary>
        public double Alarm;
        public bool Alive = true;
        public double RespawnAt;
        /// <summary>摆尾相位。</summary>
        public double Phase;
        /// <summary>平滑后的角速度（rad/s）；急转弯会闪银光。</summary>
        public double Turn;
        /// <summary>0.55 远 → 1 近。</summary>
        public double Depth;
        /// <summary>鱼群旋成饵球时共享的旋转方向。</summary>
        public int MillDir = 1;
    }

    public sealed class Shark
    {
        public int Id;
        /// <summary>相对最长鲨鱼的尺寸。</summary>
        public double Size = 1;
        public double Length;
        /// <summary>脊柱节点，吻部在前：[x0, y0, x1, y1, ...]。</summary>
        public float[] Nodes = new float[SharkNodes * 2];
        public double Heading;
        public double Speed = 30;
        /// <summary>摆尾相位，驱动渲染时的体波。</summary>
        public double Phase;
        public double Turn;
        public double Hunger;
        public double Wander;
        public int OrbitDir = 1;
        public double OrbitRadius;
        public Fish? Target;
        public double RetargetAt;
        /// <summary>0 远/暗 → 1 近/亮。</summary>
        public double Depth = 1;
    }

    public sealed class WorldEvent
    {
        public required string Kind;
        public double X, Y;
    }

    private const double FishSeparationK = 420;
    private const double FishAlignK = 1.4;
    private const double FishCohesionK = 1;
    private const double FishFleeK = 1100;
    private const double BlindCos = -0.6; // 朝向两侧约 127° 内可见

    /// <summary>与原版首页一致：四条同种鲨鱼，处于不同深度。</summary>
    private static readonly double[] DefaultSizes = [1, 0.7, 0.84, 0.77];

    public double Width, Height, Time;
    public List<Fish> FishList = [];
    public List<Shark> Sharks = [];
    public List<WorldEvent> Events = [];

    /// <summary>鱼群的感知半径；空间哈希桶尺寸与其一致。</summary>
    private double _perception = 60;
    private double[] _sepX = [];
    private double[] _sepY = [];
    private double _foreignSep = 40;
    private int _bucketCols, _bucketRows;
    private int[] _bucketHead = [];
    private ushort[] _bucketCount = [];
    private int[] _bucketNext = [];
    private double _lastSpawn;

    public void Resize(double width, double height, double cellW, double cellH)
    {
        bool first = Width == 0;
        Width = width;
        Height = height;
        // 间距按网格单元设定，保证每条鱼落进自己的字符位。
        _sepX = OceanSpecies.All.Select(sp => sp.Spacing.X * cellW).ToArray();
        _sepY = OceanSpecies.All.Select(sp => sp.Spacing.Y * cellH).ToArray();
        _perception = Math.Max(cellH * 3, cellW * 5.5);
        _foreignSep = cellW * 3;

        if (first || FishList.Count == 0)
        {
            Seed();
            return;
        }
        foreach (var f in FishList)
        {
            f.X = Clamp(f.X, 0, width);
            f.Y = Clamp(f.Y, 0, height);
        }
    }

    public void Seed()
    {
        double w = Width, h = Height;
        double minSide = Math.Min(w, h);
        double baseLength = Clamp(minSide * 0.44, 170, 430);
        int sharkCount = DefaultSizes.Length;

        Sharks = [];
        for (int i = 0; i < sharkCount; i++)
        {
            double size = DefaultSizes[i];
            double length = baseLength * size;
            double orbitRadius = minSide * Rand(0.28, 0.4);
            double a = i / (double)sharkCount * TAU + Rand(-0.4, 0.4);
            int orbitDir = i % 2 == 0 ? 1 : -1;
            double x = w / 2 + Math.Cos(a) * orbitRadius * (w / minSide) * 0.9;
            double y = h / 2 + Math.Sin(a) * orbitRadius;
            double heading = a + orbitDir * Math.PI / 2;
            var nodes = new float[SharkNodes * 2];
            double seg = length / (SharkNodes - 1);
            for (int n = 0; n < SharkNodes; n++)
            {
                nodes[n * 2] = (float)(x - Math.Cos(heading) * seg * n);
                nodes[n * 2 + 1] = (float)(y - Math.Sin(heading) * seg * n);
            }
            Sharks.Add(new Shark
            {
                Id = i + 1,
                Size = size,
                Length = length,
                Nodes = nodes,
                Heading = heading,
                Speed = 30,
                Phase = Rand(0, TAU),
                Hunger = Rand(0.25, 0.75),
                OrbitDir = orbitDir,
                OrbitRadius = orbitRadius,
                Depth = size,
            });
        }
        // 小（远）的鲨鱼先画，大鲨鱼从它们身上游过。
        Sharks.Sort((a, b) => a.Length.CompareTo(b.Length));

        int total = (int)Clamp(Math.Round(w * h / 4400), 85, 340);
        FishList = [];
        while (FishList.Count < total)
        {
            int species = OceanSpecies.Pick();
            var sp = OceanSpecies.All[species];
            int size = Math.Min(RandInt(sp.SchoolSize.Lo, sp.SchoolSize.Hi), total - FishList.Count);
            int millDir = Random.Shared.NextDouble() < 0.5 ? 1 : -1;
            double cx = Rand(w * 0.06, w * 0.94);
            double cy = Rand(h * 0.08, h * 0.92);
            // 朝向大多水平：鱼横向巡游远多于竖向。
            double dir = (Random.Shared.NextDouble() < 0.5 ? 0 : Math.PI) + Rand(-0.5, 0.5);
            for (int k = 0; k < size; k++)
            {
                FishList.Add(new Fish
                {
                    X = cx + Rand(-1, 1) * _sepX[species] * Math.Sqrt(size),
                    Y = cy + Rand(-1, 1) * _sepY[species] * Math.Sqrt(size) * 0.6,
                    Vx = Math.Cos(dir) * sp.Cruise,
                    Vy = Math.Sin(dir) * sp.Cruise,
                    Species = species,
                    Phase = Rand(0, TAU),
                    Depth = Rand(0.55, 1),
                    MillDir = millDir,
                });
            }
        }
        _bucketNext = new int[FishList.Count];
    }

    public void Step(double dt)
    {
        Time += dt;
        RebuildHash();
        StepFish(dt);
        StepSharks(dt);
        Respawn();
    }

    private void RebuildHash()
    {
        double size = _perception;
        const int pad = 2;
        _bucketCols = (int)Math.Ceiling(Width / size) + pad * 2;
        _bucketRows = (int)Math.Ceiling(Height / size) + pad * 2;
        int n = _bucketCols * _bucketRows;
        if (_bucketHead.Length != n)
        {
            _bucketHead = new int[n];
            _bucketCount = new ushort[n];
        }
        Array.Fill(_bucketHead, -1);
        Array.Clear(_bucketCount);
        if (_bucketNext.Length != FishList.Count)
        {
            _bucketNext = new int[FishList.Count];
        }
        for (int i = 0; i < FishList.Count; i++)
        {
            var f = FishList[i];
            if (!f.Alive) continue;
            int b = BucketOf(f.X, f.Y);
            _bucketNext[i] = _bucketHead[b];
            _bucketHead[b] = i;
            _bucketCount[b]++;
        }
    }

    private int BucketOf(double x, double y)
    {
        double size = _perception;
        int c = (int)Clamp(Math.Floor(x / size) + 2, 0, _bucketCols - 1);
        int r = (int)Clamp(Math.Floor(y / size) + 2, 0, _bucketRows - 1);
        return r * _bucketCols + c;
    }

    private void StepFish(double dt)
    {
        var fish = FishList;
        var sharks = Sharks;
        double w = Width, h = Height, time = Time;
        double r = _perception;
        double r2 = r * r;
        double foreignSep = _foreignSep;
        double size = _perception;
        int cols = _bucketCols, rows = _bucketRows;
        var head = _bucketHead;
        var next = _bucketNext;
        double calmDecay = Math.Exp(-dt * 0.7);
        double contagion = Math.Min(1, dt * 6);

        for (int i = 0; i < fish.Count; i++)
        {
            var f = fish[i];
            if (!f.Alive) continue;
            var sp = OceanSpecies.All[f.Species];
            // 惊慌的鱼需要更多空间：鱼群在鲨鱼周围闪开，威胁过去后再合拢。
            double spread = 1 + 1.8 * f.Alarm;
            double sepX = _sepX[f.Species] * spread;
            double sepY = _sepY[f.Species] * spread;

            double speed = Math.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
            if (speed < 1) speed = 1;
            double hx = f.Vx / speed;
            double hy = f.Vy / speed;

            double ax = 0, ay = 0, sx = 0, sy = 0, avx = 0, avy = 0, cx = 0, cy = 0;
            int n = 0;
            double neighbourAlarm = 0;

            int bc = (int)Clamp(Math.Floor(f.X / size) + 2, 0, cols - 1);
            int br = (int)Clamp(Math.Floor(f.Y / size) + 2, 0, rows - 1);
            for (int row = Math.Max(0, br - 1); row <= Math.Min(rows - 1, br + 1); row++)
            {
                for (int col = Math.Max(0, bc - 1); col <= Math.Min(cols - 1, bc + 1); col++)
                {
                    int j = head[row * cols + col];
                    while (j != -1)
                    {
                        if (j != i)
                        {
                            var o = fish[j];
                            double dx = o.X - f.X;
                            double dy = o.Y - f.Y;
                            double d2 = dx * dx + dy * dy;
                            if (d2 < r2 && d2 > 1e-6)
                            {
                                double d = Math.Sqrt(d2);
                                // 正后方的盲区。
                                if ((dx * hx + dy * hy) / d > BlindCos)
                                {
                                    // 椭圆个人空间：x 向宽，y 向紧。
                                    double qx = dx / sepX;
                                    double qy = dy / sepY;
                                    double q2 = qx * qx + qy * qy;
                                    if (q2 < 1)
                                    {
                                        double q = Math.Sqrt(q2);
                                        if (q < 1e-3) q = 1e-3;
                                        double k = (1 - q) / q;
                                        sx -= qx * k;
                                        sy -= qy * k;
                                    }
                                    if (o.Species == f.Species)
                                    {
                                        avx += o.Vx;
                                        avy += o.Vy;
                                        cx += dx;
                                        cy += dy;
                                        n++;
                                    }
                                    else if (d < foreignSep)
                                    {
                                        // 不同物种各走各的群。
                                        double k = (1 - d / foreignSep) / d * 0.6;
                                        sx -= dx * k;
                                        sy -= dy * k;
                                    }
                                    if (o.Alarm > neighbourAlarm) neighbourAlarm = o.Alarm;
                                }
                            }
                        }
                        j = next[j];
                    }
                }
            }

            ax += sx * FishSeparationK;
            ay += sy * FishSeparationK;
            if (n > 0)
            {
                double alignK = FishAlignK * sp.Align;
                double cohesionK = FishCohesionK * sp.Cohesion * (1 - 0.8 * f.Alarm);
                ax += (avx / n - f.Vx) * alignK + (cx / n) * cohesionK;
                ay += (avy / n - f.Vy) * alignK + (cy / n) * cohesionK;
                if (sp.Mill > 0)
                {
                    // 绕局部中心转：鱼群旋成饵球。
                    double cl = Math.Sqrt(cx * cx + cy * cy);
                    if (cl < 1) cl = 1;
                    ax += -cy / cl * f.MillDir * sp.Mill * (1 - f.Alarm);
                    ay += cx / cl * f.MillDir * sp.Mill * (1 - f.Alarm);
                }
            }

            // 捕食者：距离按鲨鱼前半身最近点计算，鱼也会避开鲨鱼侧翼。
            double fear = 0;
            foreach (var sh in sharks)
            {
                double fearR = sh.Length * 0.6 + 60;
                double best = double.PositiveInfinity;
                double bx = 0, by = 0;
                for (int k = 0; k <= 12; k += 3)
                {
                    double nx = sh.Nodes[k * 2];
                    double ny = sh.Nodes[k * 2 + 1];
                    double d2 = (f.X - nx) * (f.X - nx) + (f.Y - ny) * (f.Y - ny);
                    if (d2 < best)
                    {
                        best = d2;
                        bx = nx;
                        by = ny;
                    }
                }
                double d = Math.Sqrt(best);
                if (d >= fearR || d < 1e-3) continue;
                double wk = 1 - d / fearR;
                wk *= wk;
                double awayX = (f.X - bx) / d;
                double awayY = (f.Y - by) / d;
                // 喷泉效应：按自己在鲨鱼路径的哪一侧向两边散开。
                double shx = Math.Cos(sh.Heading);
                double shy = Math.Sin(sh.Heading);
                double cross = shx * (f.Y - sh.Nodes[1]) - shy * (f.X - sh.Nodes[0]);
                int side = cross >= 0 ? 1 : -1;
                ax += (awayX * 0.7 - shy * side * 0.9) * FishFleeK * wk;
                ay += (awayY * 0.7 + shx * side * 0.9) * FishFleeK * wk;
                fear = Math.Max(fear, Math.Sqrt(wk));
            }

            // 惊慌：直接威胁设定它，邻居传递（每跳略弱），自身随时间消退。
            double alarm = Math.Max(f.Alarm * calmDecay, fear);
            double relayed = neighbourAlarm * 0.86;
            if (relayed > alarm) alarm += (relayed - alarm) * contagion;
            f.Alarm = alarm;

            // 软墙。
            const double m = 36;
            if (f.X < m) ax += (m - f.X) * 7;
            else if (f.X > w - m) ax -= (f.X - (w - m)) * 7;
            if (f.Y < m) ay += (m - f.Y) * 7;
            else if (f.Y > h - m) ay -= (f.Y - (h - m)) * 7;

            // 环境洋流与个体躁动。
            double curX = 0, curY = 0;
            Current(f.X, f.Y, time, ref curX, ref curY);
            ax += curX * 0.5 + (Random.Shared.NextDouble() - 0.5) * sp.Jitter;
            ay += curY * 0.5 + (Random.Shared.NextDouble() - 0.5) * sp.Jitter;
            // 除逃命外保持水平游动。
            ay -= f.Vy * sp.VerticalDrag * (1 - 0.7 * f.Alarm);

            // 向随惊慌上升的期望速度靠拢。
            double preferred = sp.Cruise * (1 + 1.3 * f.Alarm);
            ax += (preferred - speed) * hx * 1.6;
            ay += (preferred - speed) * hy * 1.6;

            double before = Math.Atan2(f.Vy, f.Vx);
            f.Vx += ax * dt;
            f.Vy += ay * dt;
            double maxSpeed = sp.MaxSpeed * (1 + 1.8 * f.Alarm);
            double s = Math.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
            if (s > maxSpeed)
            {
                f.Vx *= maxSpeed / s;
                f.Vy *= maxSpeed / s;
            }
            else if (s < sp.MinSpeed && s > 1e-3)
            {
                f.Vx *= sp.MinSpeed / s;
                f.Vy *= sp.MinSpeed / s;
            }
            f.Turn += (AngleDiff(before, Math.Atan2(f.Vy, f.Vx)) / dt - f.Turn) * Math.Min(1, dt * 8);
            f.Phase += dt * (7 + Math.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy) * 0.12);
            f.X = Clamp(f.X + f.Vx * dt, -60, w + 60);
            f.Y = Clamp(f.Y + f.Vy * dt, -60, h + 60);
        }
    }

    private void StepSharks(double dt)
    {
        var sharks = Sharks;
        double w = Width, h = Height, time = Time;
        double minSide = Math.Min(w, h);
        double cxW = w / 2, cyW = h / 2;

        foreach (var sh in sharks)
        {
            double l = sh.Length;
            var nodes = sh.Nodes;
            double hx = nodes[0];
            double hy = nodes[1];

            sh.Hunger = Math.Min(1, sh.Hunger + dt * 0.03);

            // 动量。
            double dx = Math.Cos(sh.Heading);
            double dy = Math.Sin(sh.Heading);

            // 游荡：吻部前方圆上缓慢漂移的偏移。
            sh.Wander = Clamp(sh.Wander + (Random.Shared.NextDouble() - 0.5) * dt * 3.2, -1.1, 1.1);
            dx += Math.Cos(sh.Heading + sh.Wander) * 0.45;
            dy += Math.Sin(sh.Heading + sh.Wander) * 0.45;

            // 狩猎。
            if (time >= sh.RetargetAt)
            {
                sh.Target = FindPrey(sh);
                sh.RetargetAt = time + 0.45;
            }
            var target = sh.Target;
            bool hunting = false;
            double closing = 0;
            if (target != null && target.Alive && sh.Hunger > 0.3)
            {
                // 追击：瞄准鱼将要到达的位置。
                double tx = target.X + target.Vx * 0.35 - hx;
                double ty = target.Y + target.Vy * 0.35 - hy;
                double d = Math.Sqrt(tx * tx + ty * ty);
                if (d < 1) d = 1;
                double wk = 1.4 + 2.6 * sh.Hunger;
                dx += tx / d * wk;
                dy += ty / d * wk;
                hunting = true;
                closing = Clamp(1 - d / (l * 0.9), 0, 1);
            }
            else
            {
                // 巡逻：绕标题区懒散画圈。
                double rx = (hx - cxW) / (w / minSide);
                double ry = hy - cyW;
                double r = Math.Sqrt(rx * rx + ry * ry);
                if (r < 1) r = 1;
                double radius = sh.OrbitRadius * (1 + 0.15 * Math.Sin(time * 0.05 + sh.Id));
                double radial = Clamp((radius - r) / radius, -1, 1);
                dx += (-ry / r * sh.OrbitDir + rx / r * radial * 0.9) * 0.55;
                dy += (rx / r * sh.OrbitDir + ry / r * radial * 0.9) * 0.55;
            }

            // 与其他鲨鱼保持距离。
            foreach (var other in sharks)
            {
                if (ReferenceEquals(other, sh)) continue;
                for (int k = 0; k < SharkNodes; k += 4)
                {
                    double ox = hx - other.Nodes[k * 2];
                    double oy = hy - other.Nodes[k * 2 + 1];
                    double d = Math.Sqrt(ox * ox + oy * oy);
                    double range = (l + other.Length) * 0.45;
                    if (d < range && d > 1e-3)
                    {
                        double wk = (1 - d / range) * 3;
                        dx += ox / d * wk;
                        dy += oy / d * wk;
                    }
                }
            }

            // 保持标题区大致干净：一个软椭圆禁游区。
            double ex = (hx - cxW) / (w * 0.3);
            double ey = (hy - cyW) / (h * 0.16);
            double e2 = ex * ex + ey * ey;
            if (e2 < 1)
            {
                double e = Math.Sqrt(e2);
                if (e < 1e-3) e = 1e-3;
                double wk = (1 - e) * (hunting ? 1 : 2.5);
                dx += ex / e * wk;
                dy += ey / e * wk;
            }

            // 吻部一出画面就折返，身体偶尔探出屏幕但永不走远。
            double over = Math.Max(Math.Max(-hx, hx - w), Math.Max(-hy, hy - h));
            if (over > -l * 0.05)
            {
                double wk = Clamp((over + l * 0.05) / (l * 0.35), 0, 1) * 3.5;
                double toX = cxW - hx;
                double toY = cyW - hy;
                double d = Math.Sqrt(toX * toX + toY * toY);
                if (d < 1) d = 1;
                dx += toX / d * wk;
                dy += toY / d * wk;
            }

            double desired = Math.Atan2(dy, dx);
            // 鲨鱼无法原地掉头：转弯半径随速度增大。
            double agility = Math.Min(hunting ? 1.7 + closing : 0.8, sh.Speed / (l * 0.22));
            double maxTurn = agility * dt;
            double turned = Clamp(AngleDiff(sh.Heading, desired), -maxTurn, maxTurn);
            sh.Heading += turned;
            sh.Turn += (turned / dt - sh.Turn) * Math.Min(1, dt * 3);

            double cruise = 30 + l * 0.04;
            double targetSpeed = hunting ? cruise * (1.45 + closing * 1.5) : cruise;
            sh.Speed += (targetSpeed - sh.Speed) * Math.Min(1, dt * 1.1);
            sh.Phase += dt * (2 + sh.Speed / 24);

            nodes[0] += (float)(Math.Cos(sh.Heading) * sh.Speed * dt);
            nodes[1] += (float)(Math.Sin(sh.Heading) * sh.Speed * dt);

            // 跟随者脊柱：每个关节有刚度上限，身体最多弯成 C 形、绝不自我折叠。
            double seg = l / (SharkNodes - 1);
            const double maxBend = 0.16;
            double prevAngle = sh.Heading + Math.PI;
            for (int k = 1; k < SharkNodes; k++)
            {
                double px = nodes[(k - 1) * 2];
                double py = nodes[(k - 1) * 2 + 1];
                double angle = Math.Atan2(nodes[k * 2 + 1] - py, nodes[k * 2] - px);
                angle = prevAngle + Clamp(AngleDiff(prevAngle, angle), -maxBend, maxBend);
                nodes[k * 2] = (float)(px + Math.Cos(angle) * seg);
                nodes[k * 2 + 1] = (float)(py + Math.Sin(angle) * seg);
                prevAngle = angle;
            }

            // 进食。
            if (sh.Hunger > 0.15)
            {
                double mouthR = l * 0.05 + 8;
                double mx = nodes[0];
                double my = nodes[1];
                foreach (var f in FishList)
                {
                    if (!f.Alive) continue;
                    double ddx = f.X - mx;
                    double ddy = f.Y - my;
                    if (ddx * ddx + ddy * ddy < mouthR * mouthR)
                    {
                        f.Alive = false;
                        f.RespawnAt = time + Rand(3, 8);
                        sh.Hunger = Math.Max(0, sh.Hunger - 0.16);
                        Events.Add(new WorldEvent { Kind = "eat", X = f.X, Y = f.Y });
                    }
                }
            }
        }
    }

    /// <summary>在鲨鱼前向锥内挑选猎物，偏好稀疏桶里的近处落单鱼。</summary>
    private Fish? FindPrey(Shark sh)
    {
        double range = sh.Length * 0.9 + 160;
        double hx = sh.Nodes[0];
        double hy = sh.Nodes[1];
        double fx = Math.Cos(sh.Heading);
        double fy = Math.Sin(sh.Heading);
        Fish? best = null;
        double bestScore = double.PositiveInfinity;
        double currentScore = double.PositiveInfinity;

        foreach (var f in FishList)
        {
            if (!f.Alive) continue;
            double dx = f.X - hx;
            double dy = f.Y - hy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > range || d < 1e-3) continue;
            double cos = (dx * fx + dy * fy) / d;
            if (cos < 0.2) continue;
            int crowd = _bucketCount[BucketOf(f.X, f.Y)];
            double score = d * (1 + (1 - cos) * 0.8) * (1 + crowd * 0.07);
            if (score < bestScore)
            {
                bestScore = score;
                best = f;
            }
            if (ReferenceEquals(f, sh.Target)) currentScore = score;
        }
        // 除非明显更优，否则紧咬当前目标。
        if (sh.Target != null && currentScore < bestScore * 1.3) return sh.Target;
        return best;
    }

    private void Respawn()
    {
        double time = Time, w = Width, h = Height;
        List<Fish> due = [];
        foreach (var f in FishList)
        {
            if (!f.Alive && f.RespawnAt <= time) due.Add(f);
        }
        if (due.Count == 0) return;
        if (due.Count < 6 && time - _lastSpawn < 9) return;
        _lastSpawn = time;

        // 新鱼群从随机边缘游入。
        int side = RandInt(0, 3);
        double along = Rand(0.15, 0.85);
        double x = side == 0 ? -20 : side == 1 ? w + 20 : along * w;
        double y = side == 2 ? -20 : side == 3 ? h + 20 : along * h;
        double inward = Math.Atan2(h / 2 - y, w / 2 - x) + Rand(-0.5, 0.5);
        int species = OceanSpecies.Pick();
        var sp = OceanSpecies.All[species];
        int millDir = Random.Shared.NextDouble() < 0.5 ? 1 : -1;
        int take = Math.Min(due.Count, sp.SchoolSize.Hi);
        for (int i = 0; i < take; i++)
        {
            var f = due[i];
            f.Alive = true;
            f.Species = species;
            f.Alarm = 0;
            f.Turn = 0;
            f.MillDir = millDir;
            f.X = x + Rand(-1, 1) * _perception;
            f.Y = y + Rand(-1, 1) * _perception;
            f.Vx = Math.Cos(inward) * sp.Cruise * 1.4;
            f.Vy = Math.Sin(inward) * sp.Cruise * 1.4;
        }
    }
}
