using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShrimpBreak
{
    /// <summary>
    /// 작업 표시줄 알림 영역(트레이) 아이콘. 앉은 사람 / 선 사람 / 움직이는 사람 실루엣을
    /// 작업 표시줄 색(밝음·어두움)에 맞춰 흰색 또는 검정으로 칠한다.
    /// 상태 표시: 알림 중 = 빨간 점, 일시정지·자리 비움 = 흐리게.
    /// </summary>
    sealed class TrayIcons : IDisposable
    {
        public enum Badge { None, Alert, Dimmed }

        readonly Image sitting = TrimTransparent(Resources.LoadImage("sitting.png"));
        readonly Image standing = TrimTransparent(Resources.LoadImage("standing.png"));
        readonly Image moving = TrimTransparent(Resources.LoadImage("moving.png"));
        /// <summary>새 아이콘을 만들어 돌려준다. 돌려받은 쪽이 다 쓰면 Dispose 한다.</summary>
        public Icon Make(Routine routine, Badge badge)
        {
            var side = SystemInformation.SmallIconSize.Width; // 화면 배율에 맞는 트레이 아이콘 크기
            using (var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    var silhouette = routine == Routine.Sitting ? sitting : routine == Routine.Standing ? standing : moving;
                    var tint = IsTaskbarLight() ? Color.Black : Color.White;
                    var alpha = badge == Badge.Dimmed ? 0.45f : 1f;
                    DrawTinted(g, silhouette, new Rectangle(0, 0, side, side), tint, alpha);

                    if (badge == Badge.Alert)
                    {
                        var dot = side * 0.42f;
                        using (var brush = new SolidBrush(Color.FromArgb(235, 64, 52)))
                            g.FillEllipse(brush, side - dot, side - dot, dot, dot);
                    }
                }

                var hIcon = bitmap.GetHicon();
                var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                NativeMethods.DestroyIcon(hIcon);
                return icon;
            }
        }

        public void Dispose()
        {
            sitting.Dispose();
            standing.Dispose();
            moving.Dispose();
        }

        /// <summary>검정 실루엣(투명 배경)을 원하는 색으로, 비율을 유지해 가운데에 그린다.</summary>
        static void DrawTinted(Graphics g, Image image, Rectangle box, Color tint, float alpha)
        {
            // 원본의 RGB 는 버리고 알파만 남겨 tint 색으로 칠한다.
            var matrix = new ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, alpha, 0 },
                new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
            });
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(matrix);
                var fit = Math.Min(box.Width / (double)image.Width, box.Height / (double)image.Height);
                var width = (int)Math.Round(image.Width * fit);
                var height = (int)Math.Round(image.Height * fit);
                var target = new Rectangle(box.X + (box.Width - width) / 2, box.Y + (box.Height - height) / 2, width, height);
                g.DrawImage(image, target, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        /// <summary>원본 둘레의 투명 여백을 잘라낸다 (16px 아이콘에서 실루엣을 최대한 크게).</summary>
        static Image TrimTransparent(Image source)
        {
            using (var bitmap = new Bitmap(source))
            {
                int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
                for (var y = 0; y < bitmap.Height; y++)
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        if (bitmap.GetPixel(x, y).A <= 8) continue;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                if (maxX < 0) return source;
                var trimmed = bitmap.Clone(Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), PixelFormat.Format32bppArgb);
                source.Dispose();
                return trimmed;
            }
        }

        /// <summary>작업 표시줄이 밝은 테마인지 (Windows 설정 › 개인 설정 › 색).</summary>
        static bool IsTaskbarLight()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
    }
}
