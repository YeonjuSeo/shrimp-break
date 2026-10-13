import AppKit
import ServiceManagement

/// 메뉴바 아이콘과 메뉴.
@MainActor
final class MenuBarController: NSObject, NSMenuDelegate {
    /// 시간을 고르는 하위 메뉴가 어떤 타이머를 바꾸는지.
    private enum TimerTarget: Int {
        case single, sitting, standing

        var label: String {
            switch self {
            case .single: return "알림 간격"
            case .sitting: return "앉아 있는 시간"
            case .standing: return "서 있는 시간"
            }
        }

        @MainActor var minutes: Int {
            switch self {
            case .single: return Settings.intervalMinutes
            case .sitting: return Settings.sittingMinutes
            case .standing: return Settings.standingMinutes
            }
        }
    }

    private let scheduler: BreakScheduler
    private let presence: PresenceMonitor
    private let overlay: ShrimpOverlay
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    /// 메뉴바의 "남은 분" 표시용. 카운트다운 중에만 1분에 한 번 깨어난다.
    private var titleTimer: Timer?
    private let sittingIcon = MenuBarController.loadIcon("MenuBarSitting")
    private let standingIcon = MenuBarController.loadIcon("MenuBarStanding")

    // 메뉴가 열려 있는 동안 그 자리에서 바꿔 줄 항목들 (스위치를 눌러도 메뉴가 닫히지 않으므로).
    private weak var openMenu: NSMenu?
    private var statusMenuItem: NSMenuItem?
    private var startSittingItem: NSMenuItem?
    private var startStandingItem: NSMenuItem?
    private var separateToggleItem: NSMenuItem?
    private var durationItems: [NSMenuItem] = []
    /// 시간 항목을 만들 때의 설정값. 바뀌었을 때만 다시 만든다 (열려 있는 하위 메뉴가 닫히지 않도록).
    private var durationSignature = ""

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
        updateOpenMenu()
    }

    // MARK: - NSMenuDelegate

    func menuWillOpen(_ menu: NSMenu) {
        openMenu = menu
    }

    func menuDidClose(_ menu: NSMenu) {
        openMenu = nil
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let status = NSMenuItem(title: statusText(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        statusMenuItem = status
        menu.addItem(.separator())

        menu.addItem(item("타이머 초기화 (지금 차례 처음부터)", #selector(handleRestartTurn), key: "r"))
        let startSitting = item(startTitle(.sitting), #selector(handleStartSitting))
        let startStanding = item(startTitle(.standing), #selector(handleStartStanding))
        menu.addItem(startSitting)
        menu.addItem(startStanding)
        startSittingItem = startSitting
        startStandingItem = startStanding
        menu.addItem(.separator())

        let pause = item("일시정지", #selector(handleTogglePause), key: "p")
        pause.state = scheduler.isUserPaused ? .on : .off
        menu.addItem(pause)

        #if SHRIMP_DEV_TOOLS
        // 본인 확인용 빌드에만 들어간다 (scripts/make_app.sh 의 DEV_TOOLS=1). 공유용 zip 에는 없다.
        let summon = item("지금 새우 부르기 (테스트)", #selector(handleSummonNow), key: "t")
        summon.isEnabled = scheduler.phase != .paused && scheduler.phase != .away
        menu.addItem(summon)
        #endif
        menu.addItem(.separator())

        // 스위치 행: 눌러도 메뉴가 닫히지 않고, 아래 시간 항목이 바로 바뀐다.
        let separate = NSMenuItem()
        let toggleView = SwitchMenuItemView(title: "앉기·서기 시간 따로 설정", isOn: Settings.usesSeparatePostureTimers)
        toggleView.onChange = { [weak self] isOn in self?.handleToggleSeparateTimers(isOn) }
        separate.view = toggleView
        menu.addItem(separate)
        separateToggleItem = separate
        durationItems = makeDurationItems()
        durationSignature = Self.currentDurationSignature()
        durationItems.forEach(menu.addItem)
        menu.addItem(.separator())

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

    private func makeDurationItems() -> [NSMenuItem] {
        Settings.usesSeparatePostureTimers
            ? [durationMenuItem(for: .sitting), durationMenuItem(for: .standing)]
            : [durationMenuItem(for: .single)]
    }

    /// 메뉴가 열린 채로 상태·설정이 바뀌었을 때 해당 항목만 그 자리에서 갱신한다.
    private func updateOpenMenu() {
        guard let menu = openMenu else { return }
        statusMenuItem?.title = statusText()
        startSittingItem?.title = startTitle(.sitting)
        startStandingItem?.title = startTitle(.standing)

        guard let toggle = separateToggleItem, durationSignature != Self.currentDurationSignature() else { return }
        durationSignature = Self.currentDurationSignature()
        durationItems.forEach { if $0.menu === menu { menu.removeItem($0) } }
        let insertAt = menu.index(of: toggle) + 1
        durationItems = makeDurationItems()
        for (offset, item) in durationItems.enumerated() {
            menu.insertItem(item, at: insertAt + offset)
        }
    }

    private static func currentDurationSignature() -> String {
        "\(Settings.usesSeparatePostureTimers)/\(Settings.intervalMinutes)/\(Settings.sittingMinutes)/\(Settings.standingMinutes)"
    }

    private func durationMenuItem(for target: TimerTarget) -> NSMenuItem {
        let current = target.minutes
        let submenu = NSMenu()
        for minutes in Settings.presetIntervals {
            let option = item(Self.durationText(minutes), #selector(handleSelectDuration(_:)))
            option.tag = minutes
            option.representedObject = target.rawValue
            option.state = minutes == current ? .on : .off
            submenu.addItem(option)
        }
        submenu.addItem(.separator())
        let isCustom = !Settings.presetIntervals.contains(current)
        let custom = item(isCustom ? "직접 입력… (\(Self.durationText(current)))" : "직접 입력…",
                          #selector(handleCustomDuration(_:)))
        custom.representedObject = target.rawValue
        custom.state = isCustom ? .on : .off
        submenu.addItem(custom)

        let parent = NSMenuItem(title: "\(target.label): \(Self.durationText(current))", action: nil, keyEquivalent: "")
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

    #if SHRIMP_DEV_TOOLS
    @objc private func handleSummonNow() {
        if scheduler.phase == .alerting { overlay.spawn() } else { scheduler.triggerNow() }
    }
    #endif

    private func handleToggleSeparateTimers(_ isOn: Bool) {
        scheduler.setSeparatePostureTimers(isOn) // → onChange → refresh → 열린 메뉴 갱신
    }

    @objc private func handleRestartTurn() {
        scheduler.startTurn(nil)
    }

    @objc private func handleStartSitting() {
        scheduler.startTurn(.sitting)
    }

    @objc private func handleStartStanding() {
        scheduler.startTurn(.standing)
    }

    @objc private func handleSelectDuration(_ sender: NSMenuItem) {
        guard let target = Self.timerTarget(of: sender) else { return }
        apply(minutes: sender.tag, to: target)
    }

    @objc private func handleCustomDuration(_ sender: NSMenuItem) {
        guard let target = Self.timerTarget(of: sender) else { return }
        let alert = NSAlert()
        alert.messageText = "\(target.label) 직접 입력"
        alert.informativeText = switch target {
        case .single: "몇 분마다 새우를 부를까요? (1 ~ 480분)"
        case .sitting: "몇 분 앉아 있다가 일어날까요? (1 ~ 480분)"
        case .standing: "몇 분 서 있다가 앉을까요? (1 ~ 480분)"
        }
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 200, height: 24))
        field.stringValue = "\(target.minutes)"
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
        apply(minutes: minutes, to: target)
    }

    private func apply(minutes: Int, to target: TimerTarget) {
        switch target {
        case .single: scheduler.setInterval(minutes: minutes)
        case .sitting: scheduler.setMinutes(minutes, for: .sitting)
        case .standing: scheduler.setMinutes(minutes, for: .standing)
        }
    }

    private static func timerTarget(of item: NSMenuItem) -> TimerTarget? {
        (item.representedObject as? Int).flatMap(TimerTarget.init(rawValue:))
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

    private func startTitle(_ posture: BreakScheduler.Posture) -> String {
        let minutes = Self.durationText(Settings.minutes(for: posture))
        return posture == .sitting ? "앉기 타이머 바로 시작 (\(minutes))" : "서기 타이머 바로 시작 (\(minutes))"
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

/// 메뉴 안에 넣는 "글자 + 스위치" 행. 일반 메뉴 항목과 달리 눌러도 메뉴가 닫히지 않는다.
/// 글자 쪽을 눌러도 스위치가 바뀐다.
private final class SwitchMenuItemView: NSView {
    var onChange: (Bool) -> Void = { _ in }

    private let label: NSTextField
    private let toggle = NSSwitch()

    init(title: String, isOn: Bool) {
        label = NSTextField(labelWithString: title)
        super.init(frame: NSRect(x: 0, y: 0, width: 280, height: 26))
        autoresizingMask = [.width]

        label.font = .menuFont(ofSize: 0)
        label.textColor = .labelColor
        toggle.controlSize = .small
        toggle.state = isOn ? .on : .off
        toggle.target = self
        toggle.action = #selector(handleSwitch)

        for view in [label, toggle] as [NSView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            label.centerYAnchor.constraint(equalTo: centerYAnchor),
            toggle.leadingAnchor.constraint(greaterThanOrEqualTo: label.trailingAnchor, constant: 16),
            toggle.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            toggle.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func mouseUp(with event: NSEvent) {
        toggle.state = toggle.state == .on ? .off : .on
        handleSwitch()
    }

    @objc private func handleSwitch() {
        onChange(toggle.state == .on)
    }
}
