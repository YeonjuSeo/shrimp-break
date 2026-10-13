using System;
using System.Collections.Generic;
using System.Linq;
using ShrimpBreak;

// 새우 이동 규칙 시뮬레이션 테스트. 실패하면 종료 코드 1.
static class Program
{
    const double Dt = 1.0 / 60;
    const double Seconds = 120;
    static int failures;

    static int Main()
    {
        // 1) 실제 사용 중인 배치를 Windows 좌표(아래가 +)로 옮긴 것:
        //    오른쪽 세로 모니터(주 모니터, 작업 표시줄 48px), 왼쪽 가로 모니터(바닥이 381px 더 높음).
        var portrait = StageGeometry.FromScreen("portrait", 0, 1440, 0, 2560 - 48, 1.0, 3.6);
        var landscape = StageGeometry.FromScreen("landscape", -2560, 0, 739, 2179, 1.0, 3.6);
        RunScenario("세로+가로, 바닥 높이 다름", new[] { portrait, landscape }, expectCrossing: true);

        // 2) 같은 높이의 모니터 두 대 나란히 (흔한 사무실 배치), 배율 150%.
        var left = StageGeometry.FromScreen("left", 0, 1920, 0, 1080 - 72, 1.5, 3.6);
        var right = StageGeometry.FromScreen("right", 1920, 3840, 0, 1080 - 72, 1.5, 3.6);
        RunScenario("가로 두 대 같은 높이", new[] { left, right }, expectCrossing: true);

        // 3) 모니터 한 대: 양쪽 벽에서 튕겨야 한다.
        var single = StageGeometry.FromScreen("single", 0, 1920, 0, 1040, 1.0, 3.6);
        RunScenario("모니터 한 대", new[] { single }, expectCrossing: false);

        // 4) 두 모니터 사이에 틈이 있으면 넘어가지 않고 벽처럼 튕긴다.
        var gapA = StageGeometry.FromScreen("gapA", 0, 1920, 0, 1040, 1.0, 3.6);
        var gapB = StageGeometry.FromScreen("gapB", 2000, 3920, 0, 1040, 1.0, 3.6);
        RunScenario("모니터 사이 틈", new[] { gapA, gapB }, expectCrossing: false);

        RunCollisionTests();

        Console.WriteLine(failures == 0 ? "\n✓ 모든 시나리오 통과" : $"\n✗ 실패 {failures}건");
        return failures == 0 ? 0 : 1;
    }

    static void RunScenario(string name, StageGeometry[] stages, bool expectCrossing)
    {
        var random = new Random(42);
        var shrimps = new List<ShrimpBody>();
        foreach (var stage in stages)
            for (var i = 0; i < 100; i++) shrimps.Add(ShrimpPhysics.Spawn(stage, random));

        var crossings = new Dictionary<string, int>();
        var lastHome = shrimps.ToDictionary(s => s, s => s.Home);
        var settled = new HashSet<ShrimpBody>(); // 처음 바닥에 닿은 뒤부터 검사
        var maxBelowFloor = 0.0;
        var outsideFrames = 0;
        var maxPeakAboveFloor = 0.0;

        for (var t = 0.0; t < Seconds; t += Dt)
        {
            foreach (var s in shrimps)
            {
                var wasFalling = s.Vy < 0;
                ShrimpPhysics.Move(s, Dt, stages);
                if (wasFalling && s.Vy > 0) settled.Add(s);

                if (lastHome[s] != s.Home)
                {
                    var key = $"{lastHome[s].Id}→{s.Home.Id}";
                    crossings[key] = crossings.TryGetValue(key, out var c) ? c + 1 : 1;
                    lastHome[s] = s.Home;
                }

                if (!settled.Contains(s)) continue;

                // 몸 중심이 늘 어떤 모니터의 가로 범위 안에 있어야 한다.
                var centerX = s.X + s.Size / 2;
                if (!stages.Any(st => centerX >= st.MinX - 1 && centerX <= st.MaxX + 1)) outsideFrames++;

                // 바닥 아래로 깊이 파고들면 안 된다 (모니터를 건너며 순간적으로 낮아지는 것은 한 프레임 이내).
                maxBelowFloor = Math.Max(maxBelowFloor, s.Home.Floor - s.Y);
                maxPeakAboveFloor = Math.Max(maxPeakAboveFloor, s.Y - s.Home.Floor);
            }
        }

        var crossingText = crossings.Count == 0 ? "없음" : string.Join(", ", crossings.Select(kv => $"{kv.Key} {kv.Value}회"));
        Console.WriteLine($"[{name}] 모니터 넘나들기: {crossingText} / 최대 높이 {maxPeakAboveFloor:F0}px / 바닥 침범 최대 {maxBelowFloor:F0}px");

        Check(name, "몸 중심이 화면 밖으로 나가지 않음", outsideFrames == 0, $"{outsideFrames}프레임 이탈");
        Check(name, "바닥을 거의 뚫지 않음", maxBelowFloor < 60, $"{maxBelowFloor:F0}px");
        if (expectCrossing)
        {
            var directions = crossings.Keys.Count;
            Check(name, "양방향으로 넘나듦", directions >= 2, $"방향 {directions}개");
        }
        else
        {
            Check(name, "모니터를 넘지 않음", crossings.Count == 0, crossingText);
        }
    }

    /// <summary>새우끼리 충돌: 정면충돌·겹침 해소·최소 속도·여러 마리 오래 돌려도 겹치지 않음.</summary>
    static void RunCollisionTests()
    {
        const string name = "새우끼리 충돌";
        var stage = StageGeometry.FromScreen("single", 0, 1920, 0, 1040, 1.0, 3.6);
        ShrimpBody Body(double x, double vx) => new ShrimpBody
        {
            X = x, Y = stage.Floor, Vx = vx, Vy = 0, Size = 180, Gravity = 1664, BounceVelocity = 700, Home = stage,
        };

        // 정면으로 다가오는 두 마리 → 방향이 바뀐다
        var a = Body(500, 200);
        var b = Body(600, -200);
        ShrimpPhysics.Collide(new List<ShrimpBody> { a, b });
        Console.WriteLine($"[{name}] 정면충돌 후 속도: a {a.Vx:F0}, b {b.Vx:F0}");
        Check(name, "정면충돌하면 서로 반대로 튕김", a.Vx < 0 && b.Vx > 0, $"a {a.Vx}, b {b.Vx}");
        var gap = (b.X + b.Size / 2) - (a.X + a.Size / 2);
        Check(name, "겹친 만큼 밀어냄", gap >= 2 * 180 * ShrimpPhysics.CollisionRadiusRatio - 0.01, $"중심 거리 {gap:F1}");

        // 같은 방향, 앞의 것이 느림 → 뒤의 것이 느려져도 최소 속도는 유지
        var slow = Body(600, 130);
        var fast = Body(500, 250);
        ShrimpPhysics.Collide(new List<ShrimpBody> { fast, slow });
        Check(name, "충돌 뒤에도 최소 옆 속도 유지", Math.Abs(fast.Vx) >= ShrimpPhysics.MinSpeedPoints - 0.01 && Math.Abs(slow.Vx) >= ShrimpPhysics.MinSpeedPoints - 0.01,
              $"fast {fast.Vx:F0}, slow {slow.Vx:F0}");

        // 멀어지는 중이면 속도를 바꾸지 않는다 (겹침만 해소)
        var left = Body(500, -200);
        var right = Body(560, 200);
        ShrimpPhysics.Collide(new List<ShrimpBody> { left, right });
        Check(name, "멀어지는 중이면 속도 그대로", left.Vx == -200 && right.Vx == 200, $"{left.Vx}, {right.Vx}");

        // 40마리를 2분 돌리면서 매 프레임 충돌 처리 → 끝났을 때 깊게 겹친 쌍이 거의 없어야 한다
        var stages = new[] { stage };
        var random = new Random(7);
        var shrimps = Enumerable.Range(0, 40).Select(_ => ShrimpPhysics.Spawn(stage, random)).ToList();
        var maxOverlapRatio = 0.0;
        var maxOutside = 0.0;
        var maxBelowFloor = 0.0;
        for (var t = 0.0; t < Seconds; t += Dt)
        {
            foreach (var s in shrimps) ShrimpPhysics.Move(s, Dt, stages);
            ShrimpPhysics.Collide(shrimps);
            if (t < 5) continue; // 처음 솟아오르는 동안은 제외
            foreach (var s in shrimps)
            {
                maxOutside = Math.Max(maxOutside, Math.Max(stage.MinX - s.X, s.X + s.Size - stage.MaxX));
                maxBelowFloor = Math.Max(maxBelowFloor, stage.Floor - s.Y);
            }
            for (var i = 0; i < shrimps.Count; i++)
                for (var j = i + 1; j < shrimps.Count; j++)
                {
                    var p = shrimps[i];
                    var q = shrimps[j];
                    var d = Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2));
                    var min = (p.Size + q.Size) * ShrimpPhysics.CollisionRadiusRatio;
                    maxOverlapRatio = Math.Max(maxOverlapRatio, (min - d) / min);
                }
        }
        Console.WriteLine($"[{name}] 40마리 2분: 최대 겹침 {maxOverlapRatio:P0}, 벽 밖으로 최대 {maxOutside:F0}px, 바닥 아래로 최대 {maxBelowFloor:F0}px");
        Check(name, "40마리가 서로 깊게 파고들지 않음 (겹침 50% 미만)", maxOverlapRatio < 0.5, $"{maxOverlapRatio:P0}");
        // 밀려난 만큼은 다음 프레임에 벽·바닥 처리로 바로 되돌아온다 → 새우 크기(180px)의 1/4 이내면 눈에 띄지 않는다.
        Check(name, "충돌로 벽 밖으로 크게 밀려나지 않음", maxOutside < 45, $"{maxOutside:F0}px");
        Check(name, "충돌로 바닥 아래로 크게 밀려나지 않음", maxBelowFloor < 45, $"{maxBelowFloor:F0}px");
    }

    static void Check(string scenario, string what, bool ok, string detail)
    {
        Console.WriteLine($"   {(ok ? "✓" : "✗")} {what}{(ok ? "" : $" — {detail}")}");
        if (!ok) failures++;
    }
}
