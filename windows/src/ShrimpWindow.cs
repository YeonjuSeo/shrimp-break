using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>
    /// 새우 한 마리를 그리는 작은 투명 창.
    /// 픽셀 단위 투명도(레이어드 창)라서 새우의 투명한 부분은 Windows 가 알아서 클릭을 아래 창으로 통과시킨다.
    /// 포커스를 빼앗지 않고, 작업 표시줄·Alt+Tab 에도 나타나지 않는다.
    /// </summary>
    sealed class ShrimpWindow : Form
    {
        public event Action Clicked;

        public ShrimpWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Cursor = Cursors.Hand;
            Text = "ShrimpBreak";
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
                              | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>그림을 바꾸고 위치를 옮긴다. hBitmap 은 미리 만들어 둔 32bpp(알파 포함) 비트맵.</summary>
        public void Present(IntPtr hBitmap, int side, Point topLeft, byte opacity)
        {
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var old = NativeMethods.SelectObject(memDc, hBitmap);
            try
            {
                var size = new NativeMethods.SIZE(side, side);
                var source = new NativeMethods.POINT(0, 0);
                var destination = new NativeMethods.POINT(topLeft.X, topLeft.Y);
                var blend = Blend(opacity);
                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memDc, ref source, 0,
                                                  ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                NativeMethods.SelectObject(memDc, old);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>그림은 그대로 두고 위치·투명도만 바꾼다 (복사 없음 → 가볍다).</summary>
        public void MoveTo(Point topLeft, byte opacity)
        {
            var destination = new NativeMethods.POINT(topLeft.X, topLeft.Y);
            var blend = Blend(opacity);
            NativeMethods.UpdateLayeredWindowPosition(Handle, IntPtr.Zero, ref destination, IntPtr.Zero, IntPtr.Zero,
                                                      IntPtr.Zero, 0, ref blend, NativeMethods.ULW_ALPHA);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Clicked?.Invoke();
        }

        protected override void WndProc(ref Message m)
        {
            // 클릭해도 이 창이 활성화(포커스)되지 않도록.
            if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
                return;
            }
            // 배율이 다른 모니터로 넘어갈 때 Windows/WinForms 가 창 크기를 바꾸지 않도록 무시한다.
            // (새우 크기·위치는 직접 계산해서 그린다.)
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        static NativeMethods.BLENDFUNCTION Blend(byte opacity) => new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = opacity,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };
    }
}
