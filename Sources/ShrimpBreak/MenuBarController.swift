import AppKit
import ServiceManagement

/// 메뉴바 아이콘과 메뉴.
@MainActor
final class MenuBarController: NSObject, NSMenuDelegate {
    private let scheduler: BreakScheduler
    private let presence: PresenceMonitor
    private let overlay: ShrimpOverlay
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    /// 메뉴바의 "남은 분" 표시용. 카운트다운 중에만 1분에 한 번 깨어난다.
    private var titleTimer: Timer?
    private let sittingIcon = MenuBarController.loadIcon("MenuBarSitting")
    private let standingIcon = MenuBarController.loadIcon("MenuBarStanding")

    init(scheduler: BreakScheduler, presence: PresenceMonitor, overlay: ShrimpOverlay) {
        self.scheduler = scheduler
        self.presence = presence
        self.overlay = overlay
        super.init()

        statusItem.button?.imagePosition = .imageLeading

        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        refresh()
    }

    func refresh() {
        statusItem.button?.image = scheduler.posture == .sitting ? sittingIcon : standingIcon
        statusItem.button?.title = title()
        updateTitleTimer()
    }

    // MARK: - NSMenuDelegate

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let status = NSMenuItem(title: statusText(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        menu.addItem(.separator())

        let pause = item("일시정지", #selector(handleTogglePause), key: "p")
        pause.state = scheduler.isUserPaused ? .on : .off
        menu.addItem(pause)

        let summon = item("지금 새우 부르기 (테스트)", #selector(handleSummonNow), key: "t")
        summon.isEnabled = scheduler.phase != .paused && scheduler.phase != .away
        menu.addItem(summon)
        menu.addItem(.separator())

        menu.addItem(intervalMenuItem())
        menu.addItem(awayMenuItem())

        let login = item("로그인 시 자동 실행", #selector(handleToggleLaunchAtLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)
        menu.addItem(.separator())

        menu.addItem(item("종료", #selector(handleQuit), key: "q"))
    }

    // MARK: - Menu builders

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    private func intervalMenuItem() -> NSMenuItem {
        let current = Settings.intervalMinutes
        let submenu = NSMenu()
        for minutes in Settings.presetIntervals {
            let option = item(Self.durationText(minutes), #selector(handleSelectInterval(_:)))
            option.tag = minutes
            option.state = minutes == current ? .on : .off
            submenu.addItem(option)
        }
        submenu.addItem(.separator())
        let isCustom = !Settings.presetIntervals.contains(current)
        let custom = item(isCustom ? "직접 입력… (\(Self.durationText(current)))" : "직접 입력…",
                          #selector(handleCustomInterval))
        custom.state = isCustom ? .on : .off
        submenu.addItem(custom)

        let parent = NSMenuItem(title: "알림 간격: \(Self.durationText(current))", action: nil, keyEquivalent: "")
        parent.submenu = submenu
        return parent
    }

    private func awayMenuItem() -> NSMenuItem {
        let current = Settings.awayThresholdMinutes
        let submenu = NSMenu()
        for minutes in Settings.awayPresets {
            let option = item("\(minutes)분 동안 입력 없으면", #selector(handleSelectAway(_:)))
            option.tag = minutes
            option.state = minutes == current ? .on : .off
            submenu.addItem(option)
        }
        let off = item("끄기 (잠금·잠자기만 감지)", #selector(handleSelectAway(_:)))
        off.tag = 0
        off.state = current == 0 ? .on : .off
        submenu.addItem(off)

        let label = current == 0 ? "끔" : "\(current)분 무입력"
        let parent = NSMenuItem(title: "자리 비움 판정: \(label)", action: nil, keyEquivalent: "")
        parent.submenu = submenu
        return parent
    }

    // MARK: - Actions

    @objc private func handleTogglePause() {
        scheduler.setPaused(!scheduler.isUserPaused)
    }

    @objc private func handleSummonNow() {
        if scheduler.phase == .alerting { overlay.spawn() } else { scheduler.triggerNow() }
    }

    @objc private func handleSelectInterval(_ sender: NSMenuItem) {
        scheduler.setInterval(minutes: sender.tag)
    }

    @objc private func handleCustomInterval() {
        let alert = NSAlert()
        alert.messageText = "알림 간격 직접 입력"
        alert.informativeText = "몇 분마다 새우를 부를까요? (1 ~ 480분)"
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 200, height: 24))
        field.stringValue = "\(Settings.intervalMinutes)"
        alert.accessoryView = field
        alert.addButton(withTitle: "확인")
        alert.addButton(withTitle: "취소")
        alert.window.initialFirstResponder = field

        NSApp.activate()
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        let trimmed = field.stringValue.trimmingCharacters(in: .whitespaces)
        guard let minutes = Int(trimmed), (1...480).contains(minutes) else {
            NSSound.beep()
            return
        }
        scheduler.setInterval(minutes: minutes)
    }

    @objc private func handleSelectAway(_ sender: NSMenuItem) {
        Settings.awayThresholdMinutes = sender.tag
        presence.settingsChanged()
        refresh()
    }

    @objc private func handleToggleLaunchAtLogin() {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled {
                try service.unregister()
            } else {
                try service.register()
            }
        } catch {
            let alert = NSAlert(error: error)
            alert.informativeText = "시스템 설정 › 일반 › 로그인 항목에서 직접 추가할 수도 있어요."
            NSApp.activate()
            alert.runModal()
        }
    }

    @objc private func handleQuit() {
        NSApp.terminate(nil)
    }

    /// 템플릿 이미지라 라이트/다크 메뉴바에 맞춰 색이 자동으로 바뀐다.
    private static func loadIcon(_ name: String) -> NSImage? {
        let icon = Bundle.main.image(forResource: name)
        icon?.isTemplate = true
        icon?.size = NSSize(width: 18, height: 18)
        return icon
    }

    // MARK: - Text

    private func title() -> String {
        switch scheduler.phase {
        case .counting(let deadline): return " \(Self.minutesLeft(until: deadline))"
        case .alerting: return " ❗"
        case .paused: return " ⏸"
        case .away: return " 💤"
        }
    }

    private func statusText() -> String {
        switch scheduler.phase {
        case .counting(let deadline):
            let posture = scheduler.posture == .sitting ? "앉아 있는 중" : "서 있는 중"
            return "\(posture) · 다음 새우까지 약 \(Self.minutesLeft(until: deadline))분"
        case .alerting:
            // 타이머가 울리는 순간 이미 다음 자세로 넘어가 있다.
            let action = scheduler.posture == .standing ? "일어나서" : "앉아서"
            return "새우 출동 중! \(overlay.count)마리 — \(action) 새우를 클릭하세요"
        case .paused:
            return "일시정지됨"
        case .away:
            return "자리 비움 — 돌아오면 처음부터 다시 셉니다"
        }
    }

    private func updateTitleTimer() {
        guard case .counting = scheduler.phase else {
            titleTimer?.invalidate()
            titleTimer = nil
            return
        }
        guard titleTimer == nil else { return }
        let timer = Timer(timeInterval: 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        timer.tolerance = 10
        RunLoop.main.add(timer, forMode: .common)
        titleTimer = timer
    }

    private static func minutesLeft(until deadline: Date) -> Int {
        max(1, Int((deadline.timeIntervalSinceNow / 60).rounded(.up)))
    }

    private static func durationText(_ minutes: Int) -> String {
        if minutes % 60 == 0 { return "\(minutes / 60)시간" }
        if minutes > 60 { return "\(minutes / 60)시간 \(minutes % 60)분" }
        return "\(minutes)분"
    }
}
