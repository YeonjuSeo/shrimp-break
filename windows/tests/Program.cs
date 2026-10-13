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

    static void Check(string scenario, string what, bool ok, string detail)
    {
        Console.WriteLine($"   {(ok ? "✓" : "✗")} {what}{(ok ? "" : $" — {detail}")}");
        if (!ok) failures++;
    }
}
