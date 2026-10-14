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

    /// <summary>파도에 올라탄 새우의 상태.</summary>
    public sealed class WaveRide
    {
        /// <summary>올라탄 곡선 (0 = 가장 낮은 곡선).</summary>
        public int Lane;
        /// <summary>파도 앞쪽에서 얼마나 뒤에 붙어 가는지 (새우 크기 배수, 음수).</summary>
        public double OffsetRatio;
        /// <summary>새우마다 출렁임이 다르게 보이도록 하는 위상.</summary>
        public double Seed;
        /// <summary>올라탄 모니터 (이 모니터의 파도를 따라간다).</summary>
        public StageGeometry Stage;
        /// <summary>그림 기울기 (라디안, 위가 + 인 좌표 기준으로 시계 반대 방향 +).</summary>
        public double Tilt;
    }

    /// <summary>
    /// 새우를 클릭했을 때 파도가 쓸어가는 연출 (Mac 버전 ShrimpOverlay 의 파도와 같은 규칙).
    /// 좌표는 위가 + 인 좌표. 모니터마다 각자 파도가 왼쪽에서 오른쪽으로 지나간다.
    /// - 곡선 3개가 출렁이고, 맨 앞쪽은 볼록하게 솟아 있다.
    /// - 파도 앞쪽이 새우에 닿으면 새우가 곡선 하나에 올라타 흔들리며 함께 쓸려 간다.
    /// - 앞쪽이 화면 오른쪽 밖까지 지나가면(Duration) 곡선이 옅어지며(FadeDuration) 끝난다.
    /// </summary>
    public static class WaveSweep
    {
        public const double Duration = 1.9;
        public const double FadeDuration = 0.4;
        public const double TotalDuration = Duration + FadeDuration;
        /// <summary>겹겹이 쌓인 물결 띠 수 (0 = 가장 낮고 앞에 있는 띠).</summary>
        public const int LaneCount = 5;

        // 곡선별 높이·진폭·물결 간격(새우 크기 배수)과 출렁임 속도(rad/s)·위상
        // 길고 완만한 물결 띠가 겹겹이 쌓인 모양. 띠마다 높이·파고·간격·출렁임 속도를 달리해 단조롭지 않게.
        static readonly double[] LaneBase = { 0.20, 0.45, 0.72, 1.00, 1.28 };
        static readonly double[] LaneAmplitude = { 0.12, 0.20, 0.16, 0.26, 0.18 };
        static readonly double[] LaneWavelength = { 5.0, 6.5, 4.2, 7.5, 5.8 };
        static readonly double[] LaneSpeed = { 3.2, 2.6, 3.8, 2.2, 3.0 };
        static readonly double[] LanePhase = { 0, 2.1, 4.0, 1.1, 5.2 };
        /// <summary>파도 맨 앞이 솟은 높이 (띠별, 새우 크기 배수)·폭. 뒤쪽 띠일수록 높이 솟는다.</summary>
        static readonly double[] LaneCrest = { 0.20, 0.30, 0.40, 0.50, 0.60 };
        const double CrestWidth = 1.2;

        /// <summary>올라탄 새우가 파도 높이로 옮겨 가는 최대 세로 속도 (초당 새우 크기 배수).</summary>
        public const double MaxSettleSpeedRatio = 8;

        /// <summary>곡선이 차지하는 가장 높은 높이 (바닥 기준, 새우 크기 배수, 선 두께 여유 포함). 파도 창 높이를 정하는 데 쓴다.</summary>
        public static readonly double MaxHeightRatio =
            Enumerable.Range(0, LaneCount).Max(lane => LaneBase[lane] + LaneAmplitude[lane] + LaneCrest[lane]) + 0.15;

        /// <summary>t 초일 때 파도 앞쪽의 x. 화면 왼쪽 밖(새우 1마리)에서 오른쪽 밖(새우 2마리)까지.</summary>
        public static double Front(StageGeometry stage, double size, double t)
        {
            var progress = Math.Min(1, Math.Max(0, t / Duration));
            return stage.MinX - size + (stage.MaxX - stage.MinX + 3 * size) * progress;
        }

        /// <summary>곡선 lane 의 x 위치 높이. x 가 앞쪽보다 뒤(왼쪽)일 때만 의미가 있다.</summary>
        public static double WaveY(int lane, double x, double t, double front, StageGeometry stage, double size)
        {
            var y = stage.Floor + LaneBase[lane] * size
                    + LaneAmplitude[lane] * size * Math.Sin(2 * Math.PI * x / (LaneWavelength[lane] * size) - LaneSpeed[lane] * t + LanePhase[lane]);
            var behind = (front - x) / (CrestWidth * size);
            if (behind >= 0) y += LaneCrest[lane] * size * Math.Exp(-behind * behind);
            return y;
        }

        /// <summary>끝나갈 때 곡선의 불투명도 (1 → 0).</summary>
        public static double Opacity(double t)
        {
            if (t <= Duration) return 1;
            if (t >= TotalDuration) return 0; // 계산 오차 없이 끝에서 정확히 0
            return 1 - (t - Duration) / FadeDuration;
        }

        /// <summary>파도 앞쪽이 새우에 닿았으면 올라태운다. 올라탔으면 true.</summary>
        public static bool TryPickUp(ShrimpBody shrimp, double t, Random random, out WaveRide ride)
        {
            ride = null;
            var stage = shrimp.Home;
            var front = Front(stage, shrimp.Size, t);
            if (front < shrimp.X + shrimp.Size * 0.3) return false;
            // 지금 높이에 가장 가까운 곡선에 올라탄다 (갑자기 위아래로 튀지 않도록).
            var lane = 0;
            var best = double.MaxValue;
            for (var i = 0; i < LaneCount; i++)
            {
                var gap = Math.Abs(WaveY(i, shrimp.X + shrimp.Size / 2, t, front, stage, shrimp.Size) - shrimp.Y);
                if (gap < best) { best = gap; lane = i; }
            }
            ride = new WaveRide
            {
                Lane = lane,
                OffsetRatio = -0.3 - 0.6 * random.NextDouble(),
                Seed = random.NextDouble() * 2 * Math.PI,
                Stage = stage,
            };
            return true;
        }

        /// <summary>올라탄 새우를 파도와 함께 옮긴다. 위아래로 출렁이고 좌우로 기우뚱한다.</summary>
        public static void Ride(ShrimpBody shrimp, WaveRide ride, double t, double dt)
        {
            var size = shrimp.Size;
            var front = Front(ride.Stage, size, t);
            var centerX = front + ride.OffsetRatio * size;
            // 앞쪽에 밀려 오른쪽으로만 간다 (뒤로 끌려가지 않도록).
            shrimp.X = Math.Max(shrimp.X, centerX - size / 2);
            var bob = 0.06 * size * Math.Sin(11 * t + ride.Seed);
            var target = WaveY(ride.Lane, shrimp.X + size / 2, t, front, ride.Stage, size) - 0.25 * size + bob;
            // 처음 올라탈 때 순간이동하지 않도록 부드럽게 따라가되, 높이 떠 있던 새우도
            // 파도 위로 내려앉는 듯 보이도록 세로 속도에 상한(초당 새우 크기의 8배)을 둔다.
            var step = (target - shrimp.Y) * Math.Min(1, dt * 10);
            var maxStep = MaxSettleSpeedRatio * size * dt;
            shrimp.Y += Math.Max(-maxStep, Math.Min(maxStep, step));
            shrimp.Vx = 1; // 오른쪽을 바라본다
            ride.Tilt = 0.35 * Math.Sin(8 * t + ride.Seed);
        }
    }
}
