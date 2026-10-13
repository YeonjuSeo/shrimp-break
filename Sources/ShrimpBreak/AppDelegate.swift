import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let scheduler = BreakScheduler()
    private let presence = PresenceMonitor()
    private let overlay = ShrimpOverlay()
    private let telemetry = Telemetry()
    private var menuBar: MenuBarController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        scheduler.onSpawn = { [overlay, telemetry] in
            overlay.spawn()
            telemetry.recordShrimpCount(overlay.count)
        }
        scheduler.onClear = { [overlay] in overlay.clearAll() }
        // overlay·scheduler 가 자기 자신을 붙잡는 순환 참조가 되지 않도록 weak 로 잡는다.
        overlay.onShrimpClicked = { [weak overlay, scheduler, telemetry] clickedTempura in
            telemetry.recordAcknowledge(shrimpCount: overlay?.count ?? 0, clickedTempura: clickedTempura)
            scheduler.acknowledge()
        }
        presence.onAwayChanged = { [scheduler] away in scheduler.setAway(away) }

        let menuBar = MenuBarController(scheduler: scheduler, presence: presence, overlay: overlay, telemetry: telemetry)
        scheduler.onChange = { [weak menuBar, weak scheduler, telemetry] in
            menuBar?.refresh()
            if let scheduler { telemetry.observe(scheduler) }
        }
        self.menuBar = menuBar

        presence.start()
        scheduler.start()
        telemetry.start()
    }

    /// 종료 직전에 오늘 통계를 보낼 시간을 잠깐(최대 2초) 준다.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard telemetry.isEnabled else { return .terminateNow }
        telemetry.flushBeforeQuit { NSApp.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
}
