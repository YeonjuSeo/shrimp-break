using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShrimpBreak
{
    /// <summary>
    /// 화면 위를 통통 튀어다니는 새우들. 여러 모니터를 넘나든다 (이동 규칙은 ShrimpPhysics).
    ///
    /// 성능 메모
    /// - 새우가 한 마리도 없으면 창도 타이머도 없다 → CPU 0.
    /// - 새우마다 작은 투명 창 하나. 매 프레임은 창 위치만 옮기고(복사 없음),
    ///   그림은 방향·기울기가 바뀔 때만 미리 만들어 둔 비트맵으로 바꾼다.
    /// - 애니메이션 중에만 타이머 해상도를 높이고, 끝나면 되돌린다.
    /// </summary>
    sealed class ShrimpOverlay : IDisposable
    {
        const int MaxShrimps = 40;
        const double TempuraChance = 0.20;
        /// <summary>기울기를 몇 도 단위로 나눠 그림을 만들어 둘지. 단위를 키울수록 그림 교체가 줄어 가볍다.</summary>
        const int TiltQuantumDegrees = 4;
        /// <summary>튀는 동안 최대 기울기 (Mac 버전 0.18rad ≈ 10.3°).</summary>
        const double MaxTiltDegrees = 10.3;
        /// <summary>사라질 때 페이드 프레임 수 (약 0.2초).</summary>
        const int FadeFrames = 12;

        readonly Random random = new Random();
        readonly Timer frameTimer = new Timer { Interval = 15 };
        readonly Stopwatch clock = new Stopwatch();
        readonly List<Sprite> shrimps = new List<Sprite>();
        readonly SpriteCache sprites;
        List<StageGeometry> stages;
        double lastSeconds;
        bool isClearing;
        /// <summary>클릭해서 파도가 쓸어가는 중인지, 시작 시각, 모니터별 파도 창.</summary>
        bool isSweeping;
        double sweepStartedAt;
        int sweepFrame;
        readonly List<WaveWindow> waveWindows = new List<WaveWindow>();
        int fadeFrame;
        bool isHighResolutionTimer;

        /// <summary>새우를 클릭했을 때. 인자는 클릭한 것이 새우튀김인지.</summary>
        public event Action<bool> ShrimpClicked;

        public ShrimpOverlay()
        {
            sprites = new SpriteCache(Resources.LoadImage("shrimp.png"), Resources.LoadImage("tempura.png"));
            frameTimer.Tick += (_, __) => Step();
            SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;
        }

        public int Count => isClearing ? 0 : shrimps.Count;

        /// <summary>커서가 있는 화면 바닥에서 새우 한 마리를 내보낸다.</summary>
        public void Spawn()
        {
            if (isClearing) FinishClear();
            if (stages == null) stages = BuildStages();
            if (shrimps.Count >= MaxShrimps || stages.Count == 0) return;

            var cursorScreen = Screen.FromPoint(Cursor.Position).DeviceName;
            var home = stages.FirstOrDefault(s => s.Id == cursorScreen) ?? stages[0];
            var window = new ShrimpWindow();
            var sprite = new Sprite(ShrimpPhysics.Spawn(home, random), random.NextDouble() < TempuraChance, window);
            window.Clicked += () => HandleShrimpClicked(sprite);
            shrimps.Add(sprite);

            Render(sprite, force: true); // 그림을 먼저 올린 뒤 보여줘야 깜빡임이 없다.
            window.Show();
            StartAnimating();
        }

        /// <summary>
        /// 클릭했을 때: 파도가 밀려와 새우들을 태우고 화면 밖으로 쓸어간다 (약 2.9초 뒤 정리).
        /// 진행 중에는 ClearAll·클릭이 무시된다. 계산 규칙은 ShrimpPhysics.cs 의 WaveSweep (테스트 있음).
        /// </summary>
        public void SweepAway()
        {
            if (shrimps.Count == 0 || isClearing) return;
            isClearing = true;
            isSweeping = true;
            sweepStartedAt = clock.Elapsed.TotalSeconds;
            sweepFrame = 0;

            // 새우가 있는 모니터마다 파도 창을 깔고, 새우 창들을 그 위로 다시 올린다.
            foreach (var stage in shrimps.Select(sprite => sprite.Body.Home).Distinct())
            {
                var wave = new WaveWindow(stage, ShrimpPhysics.ShrimpSizeMillimeters * stage.PixelsPerMillimeter);
                wave.Draw(0);
                wave.Show();
                waveWindows.Add(wave);
            }
            foreach (var sprite in shrimps) NativeMethods.BringToTop(sprite.Window.Handle);
            StartAnimating();
        }

        /// <summary>살짝 흐려지며 사라지는 효과 후 정리.</summary>
        public void ClearAll()
        {
            if (shrimps.Count == 0 || isClearing) return;
            isClearing = true;
            fadeFrame = 0;
        }

        public void Dispose()
        {
            SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged;
            FinishClear();
            frameTimer.Dispose();
            sprites.Dispose();
        }

        // ── 애니메이션 ───────────────────────────────────────────

        void StartAnimating()
        {
            if (frameTimer.Enabled) return;
            if (!isHighResolutionTimer)
            {
                NativeMethods.timeBeginPeriod(1);
                isHighResolutionTimer = true;
            }
            clock.Restart();
            lastSeconds = 0;
            frameTimer.Start();
        }

        void StopAnimating()
        {
            frameTimer.Stop();
            clock.Stop();
            if (isHighResolutionTimer)
            {
                NativeMethods.timeEndPeriod(1);
                isHighResolutionTimer = false;
            }
        }

        void Step()
        {
            var now = clock.Elapsed.TotalSeconds;
            // 프레임이 밀려도(멈칫) 새우가 순간이동하지 않도록 dt 상한을 둔다.
            var dt = Math.Min(1.0 / 20, now - lastSeconds);
            lastSeconds = now;

            if (isSweeping)
            {
                StepSweep(now - sweepStartedAt, dt);
                return;
            }
            if (isClearing)
            {
                fadeFrame++;
                var opacity = (byte)Math.Max(0, 255 * (1 - fadeFrame / (double)FadeFrames));
                foreach (var sprite in shrimps) sprite.Window.MoveTo(sprite.TopLeft, opacity);
                if (fadeFrame >= FadeFrames) FinishClear();
                return;
            }

            foreach (var sprite in shrimps) ShrimpPhysics.Move(sprite.Body, dt, stages);
            ShrimpPhysics.Collide(shrimps.Select(sprite => sprite.Body).ToList()); // 새우끼리 부딪히면 튕긴다
            foreach (var sprite in shrimps) Render(sprite, force: false);
        }

        void StepSweep(double t, double dt)
        {
            if (t >= WaveSweep.TotalDuration)
            {
                FinishClear();
                return;
            }
            foreach (var sprite in shrimps)
            {
                if (sprite.Ride == null && WaveSweep.TryPickUp(sprite.Body, t, random, out var ride)) sprite.Ride = ride;
                if (sprite.Ride != null) WaveSweep.Ride(sprite.Body, sprite.Ride, t, dt);
                else ShrimpPhysics.Move(sprite.Body, dt, stages); // 파도가 아직 안 닿은 새우는 계속 튄다
                Render(sprite, force: false);
            }
            // 파도는 초당 30번만 다시 그린다 (새우 위치는 매 프레임).
            if (sweepFrame++ % 2 == 0) foreach (var wave in waveWindows) wave.Draw(t);
        }

        void Render(Sprite sprite, bool force)
        {
            var body = sprite.Body;
            // 진행 방향을 바라보고, 오를 땐 고개를 들고 떨어질 땐 숙인다. 파도를 탈 땐 기우뚱.
            var facing = sprite.Ride != null || body.Vx >= 0 ? 1 : -1;
            var tiltDegrees = sprite.Ride != null
                ? sprite.Ride.Tilt * 180 / Math.PI
                : ShrimpPhysics.Lift(body) * MaxTiltDegrees * facing;
            var key = new SpriteKey(sprite.IsTempura, facing,
                                    (int)(Math.Round(tiltDegrees / TiltQuantumDegrees) * TiltQuantumDegrees), (int)Math.Round(body.Size));
            var bitmap = sprites.Get(key);
            // 그림의 "바닥 중앙"이 새우 몸의 바닥 중앙에 오도록. 화면 y 는 아래가 + 라서 부호를 뒤집는다.
            sprite.TopLeft = new Point((int)Math.Round(body.X + body.Size / 2 - bitmap.Side / 2.0),
                                       (int)Math.Round(-body.Y - (bitmap.Side + body.Size) / 2.0));
            // 파도를 타고 자기 모니터 오른쪽 끝을 완전히 빠져나간 새우는 숨긴다 (이웃 모니터에 나타나지 않도록).
            byte opacity = sprite.Ride != null && body.X >= sprite.Ride.Stage.MaxX ? (byte)0 : (byte)255;
            if (force || !key.Equals(sprite.Key))
            {
                sprite.Window.Present(bitmap.Handle, bitmap.Side, sprite.TopLeft, opacity);
                sprite.Key = key;
            }
            else
            {
                sprite.Window.MoveTo(sprite.TopLeft, opacity);
            }
        }

        void FinishClear()
        {
            StopAnimating();
            foreach (var sprite in shrimps)
            {
                sprite.Window.Close();
                sprite.Window.Dispose();
            }
            shrimps.Clear();
            sprites.Clear(); // 새우가 없을 때는 비트맵도 들고 있지 않는다.
            foreach (var wave in waveWindows)
            {
                wave.Close();
                wave.Dispose();
            }
            waveWindows.Clear();
            isSweeping = false;
            isClearing = false;
        }

        void HandleShrimpClicked(Sprite sprite)
        {
            if (!isClearing) ShrimpClicked?.Invoke(sprite.IsTempura);
        }

        /// <summary>모니터 연결/해제·배치·배율 변경 시 무대를 다시 만들고, 떠 있던 새우 수만큼 다시 내보낸다.</summary>
        void HandleDisplaySettingsChanged(object sender, EventArgs e) => Ui.Post(() =>
        {
            var liveCount = Count;
            FinishClear();
            stages = null;
            for (var i = 0; i < liveCount; i++) Spawn();
        });

        static List<StageGeometry> BuildStages() => Screen.AllScreens.Select(screen =>
        {
            var (scale, pixelsPerMillimeter) = NativeMethods.MonitorMetrics(screen.Bounds);
            return StageGeometry.FromScreen(screen.DeviceName, screen.Bounds.Left, screen.Bounds.Right,
                                            screen.Bounds.Top, screen.WorkingArea.Bottom, scale, pixelsPerMillimeter);
        }).ToList();

        // ── 내부 타입 ───────────────────────────────────────────

        sealed class Sprite
        {
            public Sprite(ShrimpBody body, bool isTempura, ShrimpWindow window)
            {
                Body = body;
                IsTempura = isTempura;
                Window = window;
            }

            public ShrimpBody Body { get; }
            public bool IsTempura { get; }
            public ShrimpWindow Window { get; }
            public SpriteKey Key { get; set; }
            public Point TopLeft { get; set; }
            /// <summary>파도에 올라탔으면 그 상태.</summary>
            public WaveRide Ride { get; set; }
        }

        readonly struct SpriteKey : IEquatable<SpriteKey>
        {
            public SpriteKey(bool isTempura, int facing, int tiltDegrees, int size)
            {
                IsTempura = isTempura;
                Facing = facing;
                TiltDegrees = tiltDegrees;
                Size = size;
            }

            public bool IsTempura { get; }
            public int Facing { get; }
            /// <summary>그림 기울기 (도, 위가 + 인 좌표 기준 시계 반대 방향 +, Mac 버전 tilt 와 같은 뜻).</summary>
            public int TiltDegrees { get; }
            public int Size { get; }

            public bool Equals(SpriteKey other) => IsTempura == other.IsTempura && Facing == other.Facing
                                                   && TiltDegrees == other.TiltDegrees && Size == other.Size;

            public override bool Equals(object obj) => obj is SpriteKey other && Equals(other);

            public override int GetHashCode() => (IsTempura ? 1 : 0) ^ (Facing << 1) ^ ((TiltDegrees + 64) << 3) ^ (Size << 11);
        }

        /// <summary>방향·기울기·크기별 새우 그림 (GDI 비트맵). 새우가 사라지면 비운다.</summary>
        sealed class SpriteCache : IDisposable
        {
            readonly Image shrimp;
            readonly Image tempura;
            readonly Dictionary<SpriteKey, CachedBitmap> cache = new Dictionary<SpriteKey, CachedBitmap>();

            public SpriteCache(Image shrimp, Image tempura)
            {
                this.shrimp = shrimp;
                this.tempura = tempura;
            }

            public CachedBitmap Get(SpriteKey key)
            {
                if (cache.TryGetValue(key, out var cached)) return cached;
                cached = Draw(key);
                cache[key] = cached;
                return cached;
            }

            public void Clear()
            {
                foreach (var cached in cache.Values) NativeMethods.DeleteObject(cached.Handle);
                cache.Clear();
            }

            public void Dispose()
            {
                Clear();
                shrimp.Dispose();
                tempura.Dispose();
            }

            CachedBitmap Draw(SpriteKey key)
            {
                var size = key.Size;
                // 기울여도 잘리지 않도록 여유를 둔 정사각 캔버스.
                var side = (int)Math.Ceiling(size * 1.4);
                using (var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;

                        // 바닥 중앙을 기준으로 기울이고 좌우를 뒤집는다 (Mac: 회전 후 좌우 반전과 같은 순서).
                        // 화면 y 가 아래로 + 라서 회전 방향은 Mac 과 반대 부호.
                        g.TranslateTransform(side / 2f, (side + size) / 2f);
                        g.RotateTransform(-key.TiltDegrees);
                        g.ScaleTransform(key.Facing, 1);

                        var image = key.IsTempura ? tempura : shrimp;
                        var fit = Math.Min(size / (double)image.Width, size / (double)image.Height);
                        var width = (float)(image.Width * fit);
                        var height = (float)(image.Height * fit);
                        g.DrawImage(image, new RectangleF(-width / 2, -size + (size - height) / 2, width, height));
                    }
                    return new CachedBitmap(bitmap.GetHbitmap(Color.FromArgb(0)), side);
                }
            }
        }

        readonly struct CachedBitmap
        {
            public CachedBitmap(IntPtr handle, int side)
            {
                Handle = handle;
                Side = side;
            }

            public IntPtr Handle { get; }
            public int Side { get; }
        }
    }
}
