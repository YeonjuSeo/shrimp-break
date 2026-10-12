import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let scheduler = BreakScheduler()
    private let presence = PresenceMonitor()
    private let overlay = ShrimpOverlay()
    private var menuBar: MenuBarController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        scheduler.onSpawn = { [overlay] in overlay.spawn() }
        scheduler.onClear = { [overlay] in overlay.clearAll() }
        overlay.onShrimpClicked = { [scheduler] in scheduler.acknowledge() }
        presence.onAwayChanged = { [scheduler] away in scheduler.setAway(away) }

        let menuBar = MenuBarController(scheduler: scheduler, presence: presence, overlay: overlay)
        scheduler.onChange = { [weak menuBar] in menuBar?.refresh() }
        self.menuBar = menuBar

        presence.start()
        scheduler.start()
    }
}
