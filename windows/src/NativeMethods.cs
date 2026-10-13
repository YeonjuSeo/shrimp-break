using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ShrimpBreak
{
    /// <summary>WinForms 로 안 되는 Win32 호출 모음.</summary>
    static class NativeMethods
    {
        // ── 창 스타일 ─────────────────────────────────────────────
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_DPICHANGED = 0x02E0;
        public const int MA_NOACTIVATE = 3;

        // ── 레이어드 창 (픽셀 단위 투명도) ───────────────────────────
        public const int ULW_ALPHA = 0x02;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;
            public SIZE(int width, int height) { Width = width; Height = height; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
                                                      IntPtr hdcSrc, ref POINT pptSrc, int crKey,
                                                      ref BLENDFUNCTION pblend, int dwFlags);

        /// <summary>그림은 그대로 두고 위치·투명도만 바꿀 때 (hdcSrc = NULL).</summary>
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "UpdateLayeredWindow")]
        public static extern bool UpdateLayeredWindowPosition(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, IntPtr psize,
                                                              IntPtr hdcSrc, IntPtr pptSrc, int crKey,
                                                              ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // ── 마지막 입력 시각 (자리 비움 판정) ─────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>마지막 키보드·마우스 입력 이후 경과 시간.</summary>
        public static TimeSpan IdleTime()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            // TickCount 는 약 49.7일마다 한 바퀴 돈다 → 부호 없는 뺄셈으로 처리.
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }

        // ── 모니터 DPI (새우를 실제 5cm 로) ─────────────────────────
        const int MDT_EFFECTIVE_DPI = 0;
        const int MDT_RAW_DPI = 2;
        const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        /// <summary>(배율, 실제 1mm 당 픽셀). 알 수 없으면 96dpi 기준으로 대신한다.</summary>
        public static (double scale, double pixelsPerMillimeter) MonitorMetrics(Rectangle bounds)
        {
            var center = new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
            double effective = 96, raw = 0;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var ex, out _) == 0 && ex > 0) effective = ex;
                if (GetDpiForMonitor(monitor, MDT_RAW_DPI, out var rx, out _) == 0 && rx > 0) raw = rx;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            // 실제 DPI 를 모르는 모니터(일부 프로젝터·가상 화면)는 화면 배율 기준으로 근사한다.
            // 비정상적인 값(너무 작거나 큼)도 같은 방식으로 대신한다.
            var dpiForSize = raw >= 60 && raw <= 600 ? raw : effective;
            return (effective / 96.0, dpiForSize / 25.4);
        }

        // ── 화면 꺼짐 알림 ────────────────────────────────────────
        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_POWERSETTINGCHANGE = 0x8013;
        public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        // ── 애니메이션 중에만 타이머 해상도를 높인다 ─────────────────
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uMilliseconds);
    }
}
