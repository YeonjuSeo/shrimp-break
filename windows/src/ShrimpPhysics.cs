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
}
