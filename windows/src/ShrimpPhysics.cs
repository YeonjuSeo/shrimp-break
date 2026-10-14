using System;
using System.Collections.Generic;
using System.Linq;

namespace ShrimpBreak
{
    /// <summary>
    /// 모니터 하나의 영역. 물리 계산은 Mac 버전과 같은 "위가 +" 좌표계를 쓴다
    /// (Windows 화면 좌표의 y 를 뒤집은 값). 그래서 Mac 쪽 이동 규칙을 그대로 옮길 수 있다.
    /// </summary>
    public sealed class StageGeometry
    {
        public StageGeometry(string id, double minX, double maxX, double floor, double top, double scale, double pixelsPerMillimeter)
        {
            Id = id;
            MinX = minX;
            MaxX = maxX;
            Floor = floor;
            Top = top;
            Scale = scale;
            PixelsPerMillimeter = pixelsPerMillimeter;
        }

        public string Id { get; }
        public double MinX { get; }
        public double MaxX { get; }
        /// <summary>바닥 (작업 표시줄 위쪽 끝). 위가 + 인 좌표.</summary>
        public double Floor { get; }
        /// <summary>천장 (화면 위쪽 끝). 위가 + 인 좌표.</summary>
        public double Top { get; }
        /// <summary>Windows 배율 (100% = 1.0). 속도·튀는 높이를 화면 배율에 맞춘다.</summary>
        public double Scale { get; }
        public double PixelsPerMillimeter { get; }

        /// <summary>
        /// Windows 화면 좌표(픽셀, 아래가 +)로부터 만든다.
        /// </summary>
        public static StageGeometry FromScreen(string id, int boundsLeft, int boundsRight, int boundsTop,
                                               int workingBottom, double scale, double pixelsPerMillimeter)
            => new StageGeometry(id, boundsLeft, boundsRight, -workingBottom, -boundsTop, scale, pixelsPerMillimeter);
    }

    /// <summary>새우 한 마리의 위치·속도. 위치는 몸의 왼쪽 아래, 위가 + 인 좌표.</summary>
    public sealed class ShrimpBody
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Size;
        public double BounceVelocity;
        public double Gravity;
        public StageGeometry Home;
    }

    /// <summary>
    /// 새우 이동 규칙 (Mac 버전 ShrimpOverlay.move 와 동일).
    /// - 바닥: 지금 속한 모니터의 바닥.
    /// - 옆에 이웃 모니터가 있는 경계는 통과한다. 이웃 바닥이 더 높으면 크게 점프해서 올라탄다.
    /// - 이웃이 없는 바깥 끝은 벽 — 반대 방향으로 튕긴다.
    /// </summary>
    public static class ShrimpPhysics
    {
        // Mac 버전 값(pt 기준). Windows 에서는 모니터 배율을 곱해 픽셀로 쓴다.
        public const double ShrimpSizeMillimeters = 50;
        public const double GravityPoints = 1664;
        public const double MinSpeedPoints = 128;
        public const double MaxSpeedPoints = 256;
        public const double MinPeakPoints = 140;
        public const double MaxPeakPoints = 360;
        /// <summary>더 높은 이웃 모니터로 올라탈 때, 그 바닥보다 이만큼 더 높이 뛴다 (새우 크기 대비).</summary>
        public const double ClimbMarginRatio = 0.8;
        /// <summary>이보다 더 높이 올라가야 하는 경계는 벽으로 취급한다 (pt).</summary>
        public const double MaxClimbPoints = 1200;

        public static ShrimpBody Spawn(StageGeometry home, Random random)
        {
            var size = ShrimpSizeMillimeters * home.PixelsPerMillimeter;
            var gravity = GravityPoints * home.Scale;
            var peak = Lerp(MinPeakPoints, MaxPeakPoints, random.NextDouble()) * home.Scale;
            var speed = Lerp(MinSpeedPoints, MaxSpeedPoints, random.NextDouble()) * home.Scale;
            return new ShrimpBody
            {
                Size = size,
                Gravity = gravity,
                Home = home,
                X = Lerp(home.MinX, Math.Max(home.MinX + 1, home.MaxX - size), random.NextDouble()),
                // 바닥 아래(-size)에서 솟아오른다.
                Y = home.Floor - size,
                Vx = speed * (random.Next(2) == 0 ? 1 : -1),
                Vy = Math.Sqrt(2 * gravity * (peak + size)),
                BounceVelocity = Math.Sqrt(2 * gravity * peak),
            };
        }

        public static void Move(ShrimpBody shrimp, double dt, IReadOnlyList<StageGeometry> stages)
        {
            shrimp.Vy -= shrimp.Gravity * dt;
            shrimp.Y += shrimp.Vy * dt;

            var home = shrimp.Home;
            var newX = shrimp.X + shrimp.Vx * dt;
            var towardRight = shrimp.Vx > 0;
            var isLeavingHome = towardRight ? newX + shrimp.Size > home.MaxX : newX < home.MinX;

            if (isLeavingHome)
            {
                // 이미 경계에 몸을 걸친 상태면 높이와 상관없이 끝까지 건너간다 (되돌아가며 튀는 현상 방지).
                var isStraddling = towardRight ? shrimp.X + shrimp.Size > home.MaxX : shrimp.X < home.MinX;
                var next = Neighbor(home, towardRight, shrimp, stages);
                if (next != null)
                {
                    if (shrimp.Y < next.Floor && !isStraddling)
                    {
                        // 이웃 바닥이 더 높다 → 경계에 붙어서 올라탈 만큼 점프.
                        newX = towardRight ? home.MaxX - shrimp.Size : home.MinX;
                        var needed = next.Floor - shrimp.Y + shrimp.Size * ClimbMarginRatio;
                        var reachable = shrimp.Vy > 0 ? shrimp.Vy * shrimp.Vy / (2 * shrimp.Gravity) : 0;
                        if (reachable < needed) shrimp.Vy = Math.Sqrt(2 * shrimp.Gravity * needed);
                    }
                    else
                    {
                        // 몸 중심이 경계를 넘으면 이웃 모니터 소속으로 바뀐다.
                        var centerX = newX + shrimp.Size / 2;
                        if (towardRight ? centerX > home.MaxX : centerX < home.MinX)
                        {
                            home = next;
                            shrimp.Home = next;
                        }
                    }
                }
                else
                {
                    // 바깥 벽 → 반대 방향으로.
                    newX = towardRight ? home.MaxX - shrimp.Size : home.MinX;
                    shrimp.Vx = -shrimp.Vx;
                }
            }
            shrimp.X = newX;

            if (shrimp.Y <= home.Floor && shrimp.Vy < 0)
            {
                shrimp.Y = home.Floor;
                shrimp.Vy = shrimp.BounceVelocity;
            }
        }

        /// <summary>충돌 판정용 원의 반지름 (새우 그림이 정사각 칸을 다 채우지 않아서 조금 작게).</summary>
        public const double CollisionRadiusRatio = 0.38;

        /// <summary>
        /// 새우끼리 부딪히면 튕긴다. 몸을 원으로 보고, 겹친 만큼 서로 밀어낸 뒤
        /// 부딪힌 방향(법선)으로 속도를 주고받는다 (같은 무게의 완전 탄성 충돌).
        /// 충돌로 옆 속도가 거의 0 이 되면 계속 제자리에서만 튀므로 최소 옆 속도를 지킨다.
        /// 최대 40마리라 모든 쌍을 비교해도 프레임당 780번이면 충분히 가볍다.
        /// </summary>
        public static void Collide(IList<ShrimpBody> shrimps)
        {
            for (var i = 0; i < shrimps.Count; i++)
            {
                for (var j = i + 1; j < shrimps.Count; j++)
                {
                    var a = shrimps[i];
                    var b = shrimps[j];
                    var ra = a.Size * CollisionRadiusRatio;
                    var rb = b.Size * CollisionRadiusRatio;
                    var dx = (b.X + b.Size / 2) - (a.X + a.Size / 2);
                    var dy = (b.Y + b.Size / 2) - (a.Y + a.Size / 2);
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    var minDistance = ra + rb;
                    if (distance >= minDistance) continue;

                    // 완전히 겹친 경우엔 아무 방향으로나 떼어 낸다.
                    double nx, ny;
                    if (distance < 1e-6)
                    {
                        nx = 1;
                        ny = 0;
                        distance = 0;
                    }
                    else
                    {
                        nx = dx / distance;
                        ny = dy / distance;
                    }

                    // 1) 겹친 만큼 반씩 밀어낸다.
                    var push = (minDistance - distance) / 2;
                    a.X -= nx * push;
                    a.Y -= ny * push;
                    b.X += nx * push;
                    b.Y += ny * push;

                    // 2) 서로 다가오는 중일 때만 법선 방향 속도를 맞바꾼다.
                    var approaching = (b.Vx - a.Vx) * nx + (b.Vy - a.Vy) * ny;
                    if (approaching < 0)
                    {
                        a.Vx += approaching * nx;
                        a.Vy += approaching * ny;
                        b.Vx -= approaching * nx;
                        b.Vy -= approaching * ny;
                        KeepMinimumSpeed(a);
                        KeepMinimumSpeed(b);
                    }
                }
            }
        }

        static void KeepMinimumSpeed(ShrimpBody shrimp)
        {
            var minimum = MinSpeedPoints * shrimp.Home.Scale;
            if (Math.Abs(shrimp.Vx) < minimum) shrimp.Vx = shrimp.Vx < 0 ? -minimum : minimum;
        }

        /// <summary>-1(떨어지는 중) ~ 1(오르는 중). 그림을 기울이는 데 쓴다.</summary>
        public static double Lift(ShrimpBody shrimp)
            => Math.Max(-1, Math.Min(1, shrimp.Vy / shrimp.BounceVelocity));

        /// <summary>home 의 왼쪽/오른쪽 경계에 맞닿은 모니터 중 새우가 들어갈 수 있는 곳.</summary>
        static StageGeometry Neighbor(StageGeometry home, bool towardRight, ShrimpBody shrimp, IReadOnlyList<StageGeometry> stages)
        {
            var edge = towardRight ? home.MaxX : home.MinX;
            var best = stages
                .Where(stage =>
                {
                    var touches = Math.Abs((towardRight ? stage.MinX : stage.MaxX) - edge) < 1;
                    var overlapsVertically = stage.Top > home.Floor && stage.Floor < home.Top;
                    // 천장이 새우보다 낮은 모니터로는 못 들어간다.
                    return touches && overlapsVertically && stage.Top >= shrimp.Y + shrimp.Size;
                })
                // 지금 높이에서 바로 들어갈 수 있는 곳 우선, 없으면 가장 덜 올라가도 되는 곳.
                .OrderBy(stage => ClimbHeight(stage, shrimp))
                .FirstOrDefault();
            if (best == null || ClimbHeight(best, shrimp) > MaxClimbPoints * home.Scale) return null;
            return best;
        }

        static double ClimbHeight(StageGeometry stage, ShrimpBody shrimp) => Math.Max(0, stage.Floor - shrimp.Y);

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }

    /// <summary>
    /// 파도 하나가 이어서 지나가는 모니터 묶음 = 좌우로 맞닿은 모니터들 (한 "줄").
    /// 위아래로 쌓인 모니터나 사이가 떨어진 모니터는 각자 다른 묶음 → 각자 자기 바닥에서 파도가 지나간다.
    /// </summary>
    public sealed class WaveGroup
    {
        public WaveGroup(IReadOnlyList<StageGeometry> stages, double size)
        {
            Stages = stages;
            Size = size;
            MinX = stages.Min(stage => stage.MinX);
            MaxX = stages.Max(stage => stage.MaxX);
        }

        public IReadOnlyList<StageGeometry> Stages { get; }
        public double MinX { get; }
        public double MaxX { get; }
        /// <summary>물결 모양의 기준 크기 (모니터마다 새우 크기가 조금 달라도 물결이 끊기지 않도록 하나로).</summary>
        public double Size { get; }

        /// <summary>파도 앞쪽이 묶음 왼쪽 끝 조금 앞에서 출발해, 모든 새우가 오른쪽 밖으로 나갈 때까지 걸리는 시간.</summary>
        public double Duration => (MaxX - MinX + (WaveSweep.FrontStartRatio + WaveSweep.FrontEndRatio) * Size)
                                  / (WaveSweep.SpeedRatio * Size);

        /// <summary>x 위치의 모니터 (묶음 바깥이면 가장 가까운 끝 모니터).</summary>
        public StageGeometry StageAt(double x)
        {
            foreach (var stage in Stages)
                if (x >= stage.MinX && x < stage.MaxX) return stage;
            return x < MinX ? Stages.OrderBy(stage => stage.MinX).First() : Stages.OrderBy(stage => stage.MaxX).Last();
        }
    }

    /// <summary>파도에 올라탄 새우의 상태.</summary>
    public sealed class WaveRide
    {
        /// <summary>올라탄 띠 (0 = 가장 낮고 앞에 있는 띠).</summary>
        public int Lane;
        /// <summary>파도 앞쪽에서 얼마나 뒤에 붙어 가는지 (새우 크기 배수, 음수).</summary>
        public double OffsetRatio;
        /// <summary>새우마다 출렁임이 다르게 보이도록 하는 위상.</summary>
        public double Seed;
        /// <summary>올라탄 파도의 모니터 묶음 (이 묶음 끝까지 쓸려 간다).</summary>
        public WaveGroup Group;
        /// <summary>그림 기울기 (라디안, 위가 + 인 좌표 기준으로 시계 반대 방향 +).</summary>
        public double Tilt;
    }

    /// <summary>
    /// 새우를 클릭했을 때 파도가 쓸어가는 연출 (Mac 버전 ShrimpOverlay 의 Wave 와 같은 규칙).
    /// 좌표는 위가 + 인 좌표. 모니터 묶음(좌우로 맞닿은 모니터들)마다 파도 하나가 왼쪽 끝에서 오른쪽 끝까지
    /// 이어서 지나간다. 물결 모양은 이어지고, 높이는 각 모니터 바닥에 붙는다.
    /// - 길고 완만한 물결 띠 5개가 겹겹이 쌓여 출렁이고, 맨 앞쪽은 볼록하게 솟아 있다.
    /// - 파도 앞쪽이 새우에 닿으면 새우가 띠 하나에 올라타 흔들리며 함께 쓸려 간다 (옆 모니터로도 넘어간다).
    /// - 앞쪽이 묶음 오른쪽 밖까지 지나가면 띠가 옅어지며(FadeDuration) 끝난다.
    /// 속도는 "초당 새우 크기 몇 배"로 정한다 → 모니터가 많아 거리가 길어도 같은 빠르기로 보인다.
    /// </summary>
    public static class WaveSweep
    {
        /// <summary>파도 앞쪽이 나아가는 빠르기 (초당 새우 크기 배수). 가로 4K 한 대 ≈ 1.6초.</summary>
        public const double SpeedRatio = 12.5;
        /// <summary>앞쪽이 묶음 왼쪽 끝보다 얼마나 앞(왼쪽)에서 출발하는지 (새우 크기 배수).</summary>
        public const double FrontStartRatio = 0.5;
        /// <summary>앞쪽이 묶음 오른쪽 끝을 얼마나 지나서 멈추는지 (새우 크기 배수). 비탈 뒤에 탄 새우까지 다 빠져나가도록.</summary>
        public const double FrontEndRatio = 4.5;
        public const double FadeDuration = 0.4;
        /// <summary>겹겹이 쌓인 물결 띠 수 (0 = 가장 낮고 앞에 있는 띠).</summary>
        public const int LaneCount = 5;

        // 띠별 높이·파고·물결 간격(새우 크기 배수)과 출렁임 속도(rad/s)·위상.
        // 물결 띠가 겹겹이 쌓인 모양. 띠마다 파고 차이를 크게 두고(잔잔한 띠 ↔ 크게 출렁이는 띠),
        // 짧은 물결을 하나 더 겹쳐 뒤쪽도 평평하지 않게 크고 작은 굴곡이 섞이게 한다.
        static readonly double[] LaneBase = { 0.20, 0.50, 0.80, 1.15, 1.50 };
        static readonly double[] LaneAmplitude = { 0.10, 0.35, 0.18, 0.50, 0.28 };
        static readonly double[] LaneWavelength = { 3.6, 5.2, 2.8, 6.4, 4.4 };
        static readonly double[] LaneSpeed = { 3.2, 2.6, 3.8, 2.2, 3.0 };
        static readonly double[] LanePhase = { 0, 2.1, 4.0, 1.1, 5.2 };
        /// <summary>겹치는 짧은 물결: 진폭은 띠 파고의 40%, 간격은 띠 물결 간격의 47%.</summary>
        const double RippleAmplitudeRatio = 0.4;
        const double RippleWavelengthRatio = 0.47;
        /// <summary>앞쪽 비탈 위 마루가 솟은 높이 (띠별, 새우 크기 배수)·폭. 뒤쪽 물결과 균형이 맞게 너무 높지 않게.</summary>
        static readonly double[] LaneCrest = { 0.15, 0.25, 0.30, 0.40, 0.45 };
        const double CrestWidth = 1.2;
        /// <summary>
        /// 앞쪽 비탈 길이 (띠별, 새우 크기 배수). 맨 앞에서는 높이 0(바닥)이고 이만큼 뒤에서 본래 높이가 된다
        /// → 앞쪽이 수직으로 끊기지 않고 바닥으로 내려앉는다. 띠마다 길이를 달리해 앞쪽도 층층이 겹쳐 보이게.
        /// </summary>
        static readonly double[] LaneRamp = { 1.2, 1.6, 2.0, 2.4, 2.8 };

        /// <summary>올라탄 새우가 파도 높이로 옮겨 가는 최대 세로 속도 (초당 새우 크기 배수). 바닥 높이가 다른 모니터로 넘어갈 때도 부드럽게.</summary>
        public const double MaxSettleSpeedRatio = 8;

        /// <summary>띠가 차지하는 가장 높은 높이 (바닥 기준, 새우 크기 배수, 여유 포함). 파도 창 높이를 정하는 데 쓴다.</summary>
        public static readonly double MaxHeightRatio =
            Enumerable.Range(0, LaneCount).Max(lane => LaneBase[lane] + LaneAmplitude[lane] * (1 + RippleAmplitudeRatio) + LaneCrest[lane]) + 0.15;

        /// <summary>좌우로 맞닿은(그리고 위아래로 겹치는) 모니터끼리 묶는다.</summary>
        public static List<WaveGroup> BuildGroups(IReadOnlyList<StageGeometry> stages, Func<StageGeometry, double> sizeOf)
        {
            bool Touch(StageGeometry a, StageGeometry b) =>
                (Math.Abs(a.MaxX - b.MinX) < 1 || Math.Abs(b.MaxX - a.MinX) < 1) && a.Top > b.Floor && a.Floor < b.Top;

            var groups = new List<WaveGroup>();
            var visited = new HashSet<StageGeometry>();
            foreach (var start in stages)
            {
                if (!visited.Add(start)) continue;
                var members = new List<StageGeometry> { start };
                for (var i = 0; i < members.Count; i++)
                    foreach (var other in stages)
                        if (!visited.Contains(other) && Touch(members[i], other))
                        {
                            visited.Add(other);
                            members.Add(other);
                        }
                groups.Add(new WaveGroup(members, members.Max(sizeOf)));
            }
            return groups;
        }

        public static WaveGroup GroupOf(IEnumerable<WaveGroup> groups, StageGeometry stage)
            => groups.First(group => group.Stages.Contains(stage));

        /// <summary>모든 파도가 끝나는 시각.</summary>
        public static double TotalDuration(IEnumerable<WaveGroup> groups) => groups.Max(group => group.Duration) + FadeDuration;

        /// <summary>t 초일 때 파도 앞쪽의 x.</summary>
        public static double Front(WaveGroup group, double t)
            => Math.Min(group.MaxX + FrontEndRatio * group.Size,
                        group.MinX - FrontStartRatio * group.Size + SpeedRatio * group.Size * Math.Max(0, t));

        /// <summary>
        /// 띠 lane 의 x 위치 높이 (그 자리 모니터의 바닥 floor 기준). x 가 앞쪽보다 뒤(왼쪽)일 때만 의미가 있다.
        /// 앞쪽에서는 바닥(floor)에서 시작해 비탈을 따라 부드럽게 올라간다.
        /// </summary>
        public static double WaveY(int lane, double x, double t, double front, double floor, double size)
        {
            var behind = Math.Max(0, front - x);
            var ramp = LaneRamp[lane] * size;
            var crestOffset = (behind - 0.9 * ramp) / (CrestWidth * size);
            var amplitude = LaneAmplitude[lane] * size;
            var wavelength = LaneWavelength[lane] * size;
            var height = LaneBase[lane] * size
                         + amplitude * Math.Sin(2 * Math.PI * x / wavelength - LaneSpeed[lane] * t + LanePhase[lane])
                         + amplitude * RippleAmplitudeRatio
                           * Math.Sin(2 * Math.PI * x / (wavelength * RippleWavelengthRatio) + 1.6 * LaneSpeed[lane] * t + 2 * LanePhase[lane])
                         + LaneCrest[lane] * size * Math.Exp(-crestOffset * crestOffset);
            var progress = Math.Min(1, behind / ramp);
            var envelope = progress * progress * (3 - 2 * progress); // smoothstep: 맨 앞 0 → 비탈 끝 1
            return floor + height * envelope;
        }

        /// <summary>새우가 앞쪽에서 얼마나 뒤에 타는지 (새우 크기 배수, 음수). 띠의 비탈 위 마루 근처.</summary>
        static double RideOffsetRatio(int lane, Random random) => -LaneRamp[lane] * (0.75 + 0.35 * random.NextDouble());

        /// <summary>끝나갈 때 띠의 불투명도 (1 → 0). 묶음마다 자기 파도가 다 지나간 뒤 옅어진다.</summary>
        public static double Opacity(WaveGroup group, double t)
        {
            if (t <= group.Duration) return 1;
            if (t >= group.Duration + FadeDuration) return 0; // 계산 오차 없이 끝에서 정확히 0
            return 1 - (t - group.Duration) / FadeDuration;
        }

        /// <summary>파도 앞쪽이 새우에 닿았으면 지금 높이에 가장 가까운 띠에 올라태운다. 올라탔으면 true.</summary>
        public static bool TryPickUp(ShrimpBody shrimp, WaveGroup group, double t, Random random, out WaveRide ride)
        {
            ride = null;
            var front = Front(group, t);
            if (front < shrimp.X + shrimp.Size * 0.3) return false;
            var centerX = shrimp.X + shrimp.Size / 2;
            var floor = group.StageAt(centerX).Floor;
            var lane = 0;
            var best = double.MaxValue;
            for (var i = 0; i < LaneCount; i++)
            {
                var gap = Math.Abs(WaveY(i, centerX, t, front, floor, group.Size) - shrimp.Y);
                if (gap < best) { best = gap; lane = i; }
            }
            ride = new WaveRide
            {
                Lane = lane,
                OffsetRatio = RideOffsetRatio(lane, random),
                Seed = random.NextDouble() * 2 * Math.PI,
                Group = group,
            };
            return true;
        }

        /// <summary>올라탄 새우를 파도와 함께 옮긴다. 위아래로 출렁이고 좌우로 기우뚱한다. 옆 모니터로 넘어가면 그 바닥을 따른다.</summary>
        public static void Ride(ShrimpBody shrimp, WaveRide ride, double t, double dt)
        {
            var size = shrimp.Size;
            var group = ride.Group;
            var front = Front(group, t);
            // 앞쪽에 밀려 오른쪽으로만 간다 (뒤로 끌려가지 않도록).
            shrimp.X = Math.Max(shrimp.X, front + ride.OffsetRatio * size - size / 2);
            var centerX = shrimp.X + size / 2;
            var stage = group.StageAt(centerX);
            shrimp.Home = stage;
            var bob = 0.06 * size * Math.Sin(11 * t + ride.Seed);
            var target = WaveY(ride.Lane, centerX, t, front, stage.Floor, group.Size) - 0.25 * size + bob;
            // 순간이동 없이 따라가되, 높이 떠 있던 새우·바닥 높이가 다른 모니터로 넘어간 새우도
            // 파도 위로 내려앉는 듯 보이도록 세로 속도에 상한을 둔다.
            var step = (target - shrimp.Y) * Math.Min(1, dt * 10);
            var maxStep = MaxSettleSpeedRatio * size * dt;
            shrimp.Y += Math.Max(-maxStep, Math.Min(maxStep, step));
            shrimp.Vx = 1; // 오른쪽을 바라본다
            ride.Tilt = 0.35 * Math.Sin(8 * t + ride.Seed);
        }
    }
}
