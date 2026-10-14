using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ShrimpBreak
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 두 번 실행하면 트레이 아이콘이 두 개 생기지 않도록 한 개만 띄운다.
            using (var mutex = new Mutex(true, @"Local\ShrimpBreak", out var isFirstInstance))
            {
                if (!isFirstInstance) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (_, e) => ReportCrash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

                var uiContext = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(uiContext);
                Ui.Initialize(uiContext);

                using (var app = new ShrimpBreakApp())
                    Application.Run(app);
            }
        }

        /// <summary>
        /// 예상 못 한 오류는 %LOCALAPPDATA%\ShrimpBreak\error.log 에 남긴다 (동료 PC 에서 문제를 받아 보기 위함).
        /// </summary>
        static void ReportCrash(Exception error)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShrimpBreak");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "error.log");
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}\r\n\r\n");
                Telemetry.RecordError();
                MessageBox.Show($"새우 브레이크에 문제가 생겼어요.\n오류 내용을 아래 파일에 저장했어요:\n{path}",
                                "새우 브레이크", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // 오류 기록 중의 오류는 무시한다.
            }
        }
    }

    /// <summary>앱 전체 배선 (Mac 버전 AppDelegate 에 해당). 창 없이 트레이에만 상주한다.</summary>
    sealed class ShrimpBreakApp : ApplicationContext
    {
        readonly BreakScheduler scheduler = new BreakScheduler();
        readonly PresenceMonitor presence = new PresenceMonitor();
        readonly ShrimpOverlay overlay = new ShrimpOverlay();
        readonly Telemetry telemetry = new Telemetry();
        readonly TrayController tray;

        public ShrimpBreakApp()
        {
            scheduler.Spawn += () =>
            {
                overlay.Spawn();
                telemetry.RecordShrimpCount(overlay.Count);
            };
            scheduler.Clear += overlay.ClearAll;
            overlay.ShrimpClicked += clickedTempura =>
            {
                telemetry.RecordAcknowledge(overlay.Count, clickedTempura);
                overlay.SweepAway(); // 파도가 쓸어간다 (이후 scheduler 의 정리 요청은 무시됨)
                scheduler.Acknowledge();
            };
            presence.AwayChanged += scheduler.SetAway;

            tray = new TrayController(scheduler, presence, overlay, telemetry);
            scheduler.Changed += tray.Refresh;
            scheduler.Changed += () => telemetry.Observe(scheduler);

            presence.Start();
            scheduler.Start();
            telemetry.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tray.Dispose();
                overlay.Dispose();
                presence.Dispose();
                scheduler.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
