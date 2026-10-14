using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>
    /// 새우를 클릭했을 때 모니터 바닥에 깔리는 파도 (곡선 3개 + 아래를 채운 반투명 물).
    /// 클릭은 아래로 그대로 통과하고, 새우 창들보다 아래에 놓인다.
    ///
    /// 성능 메모: 32bpp 비트맵을 한 번만 만들어 두고 그 메모리에 바로 그린 뒤 UpdateLayeredWindow 로 올린다
    /// (프레임마다 큰 비트맵을 새로 만들거나 복사하지 않음). 연출이 끝나면 바로 정리한다.
    /// </summary>
    sealed class WaveWindow : Form
    {
        // Mac 버전과 같은 색: 진한 파랑 → 밝은 하늘색. 물은 반투명, 선은 흰 테두리 위에.
        static readonly Color[] LaneColors =
        {
            Color.FromArgb(13, 84, 191),
            Color.FromArgb(26, 133, 242),
            Color.FromArgb(77, 184, 255),
        };
        const int WaterAlpha = 115; // 0.45

        readonly StageGeometry stage;
        readonly double size;
        readonly int width;
        readonly int height;
        /// <summary>창 위쪽 끝의 높이 (위가 + 인 좌표).</summary>
        readonly double topY;
        IntPtr dib;
        Bitmap canvas;

        public WaveWindow(StageGeometry stage, double size)
        {
            this.stage = stage;
            this.size = size;
            width = (int)Math.Ceiling(stage.MaxX - stage.MinX);
            height = (int)Math.Ceiling(WaveSweep.MaxHeightRatio * size);
            topY = stage.Floor + height;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "ShrimpBreak wave";

            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER)),
                biWidth = width,
                biHeight = -height, // 위에서 아래로
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            canvas = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW
                              | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>t 초일 때의 파도를 그려서 올린다.</summary>
        public void Draw(double t)
        {
            var front = WaveSweep.Front(stage, size, t);
            var end = Math.Min(front, stage.MaxX);
            var lineWidth = (float)Math.Max(4, size * 0.05);

            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingMode = CompositingMode.SourceOver;
                if (end > stage.MinX)
                {
                    var lines = new PointF[WaveSweep.LaneCount][];
                    for (var lane = 0; lane < WaveSweep.LaneCount; lane++) lines[lane] = Curve(lane, t, front, end);

                    // 뒤→앞: 높은 곡선의 물 → 낮은 곡선의 물 → 흰 테두리들 → 색 선들 (아래로 갈수록 진한 물)
                    for (var lane = WaveSweep.LaneCount - 1; lane >= 0; lane--)
                    {
                        var points = lines[lane];
                        var water = new PointF[points.Length + 2];
                        points.CopyTo(water, 0);
                        water[points.Length] = new PointF(points[points.Length - 1].X, height);
                        water[points.Length + 1] = new PointF(0, height);
                        using (var brush = new SolidBrush(Color.FromArgb(WaterAlpha, LaneColors[lane])))
                            g.FillPolygon(brush, water);
                    }
                    using (var outline = new Pen(Color.FromArgb(242, Color.White), lineWidth * 1.8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        foreach (var points in lines) g.DrawLines(outline, points);
                    for (var lane = 0; lane < WaveSweep.LaneCount; lane++)
                        using (var pen = new Pen(LaneColors[lane], lineWidth) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLines(pen, lines[lane]);
                }
            }

            Present((byte)Math.Round(255 * WaveSweep.Opacity(t)));
        }

        PointF[] Curve(int lane, double t, double front, double end)
        {
            var count = Math.Max(2, (int)((end - stage.MinX) / 10) + 2);
            var points = new PointF[count];
            for (var i = 0; i < count; i++)
            {
                var x = Math.Min(end, stage.MinX + i * 10);
                var y = WaveSweep.WaveY(lane, x, t, front, stage, size);
                points[i] = new PointF((float)(x - stage.MinX), (float)(topY - y));
            }
            return points;
        }

        void Present(byte opacity)
        {
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var old = NativeMethods.SelectObject(memDc, dib);
            try
            {
                var size2 = new NativeMethods.SIZE(width, height);
                var source = new NativeMethods.POINT(0, 0);
                var destination = new NativeMethods.POINT((int)Math.Round(stage.MinX), (int)Math.Round(-topY));
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    SourceConstantAlpha = opacity,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA,
                };
                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size2, memDc, ref source, 0,
                                                  ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                NativeMethods.SelectObject(memDc, old);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
                return;
            }
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                canvas?.Dispose();
                canvas = null;
            }
            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
                dib = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }
    }
}
