import AppKit
import ServiceManagement

/// 메뉴바 아이콘과 메뉴.
@MainActor
final class MenuBarController: NSObject, NSMenuDelegate {
    private let scheduler: BreakScheduler
    private let presence: PresenceMonitor
    private let overlay: ShrimpOverlay
    private let telemetry: Telemetry
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    /// 메뉴바의 "남은 분" 표시용. 카운트다운 중에만 1분에 한 번 깨어난다.
    private var titleTimer: Timer?
    private let sittingIcon = MenuBarController.loadIcon("MenuBarSitting")
    private let standingIcon = MenuBarController.loadIcon("MenuBarStanding")
    private let movingIcon = MenuBarController.loadIcon("MenuBarMoving")

    // 메뉴가 열려 있는 동안 그 자리에서 바꿔 줄 항목들 (스위치를 눌러도 메뉴가 닫히지 않으므로).
    private weak var openMenu: NSMenu?
    private var statusMenuItem: NSMenuItem?
    private var startItems: [BreakScheduler.Routine: NSMenuItem] = [:]
    private var routineTimesView: RoutineTimesMenuItemView?

    init(scheduler: BreakScheduler, presence: PresenceMonitor, overlay: ShrimpOverlay, telemetry: Telemetry) {
        self.scheduler = scheduler
        self.presence = presence
        self.overlay = overlay
        self.telemetry = telemetry
        super.init()

        statusItem.button?.imagePosition = .imageLeading

        let menu = NSMenu()
        menu.delegate = self
        // 꺼진(0분) 루틴의 "바로 시작"처럼 직접 정한 활성 상태를 그대로 쓴다.
        menu.autoenablesItems = false
        statusItem.menu = menu
        refresh()
    }

    func refresh() {
        statusItem.button?.image = switch scheduler.routine {
        case .sitting: sittingIcon
        case .standing: standingIcon
        case .moving: movingIcon
        }
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
        // 입력하다가 Return 없이 메뉴를 닫아도 입력한 값을 적용한다.
        routineTimesView?.commitEditing()
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let status = NSMenuItem(title: statusText(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        statusMenuItem = status
        menu.addItem(.separator())

        menu.addItem(item("타이머 초기화 (지금 차례 처음부터)", #selector(handleRestartTurn), key: "r"))
        startItems = [:]
        for routine in BreakScheduler.Routine.allCases {
            let start = item(startTitle(routine), #selector(handleStartRoutine(_:)))
            start.tag = routine.rawValue
            start.isEnabled = routine.isOn
            menu.addItem(start)
            startItems[routine] = start
        }
        menu.addItem(.separator())

        let pause = item("일시정지", #selector(handleTogglePause), key: "p")
        pause.state = scheduler.isUserPaused ? .on : .off
        menu.addItem(pause)

        #if SHRIMP_DEV_TOOLS
        // 본인 확인용 빌드에만 들어간다 (scripts/make_app.sh 의 DEV_TOOLS=1). 공유용 zip 에는 없다.
        let summon = item("지금 새우 부르기 (테스트)", #selector(handleSummonNow), key: "t")
        summon.isEnabled = scheduler.phase != .paused && scheduler.phase != .away
        menu.addItem(summon)
        // 새우를 직접 클릭하지 않고 파도 연출을 확인하는 용도 (클릭과 같은 흐름)
        let wave = item("지금 파도 부르기 (테스트)", #selector(handleWaveNow), key: "w")
        wave.isEnabled = scheduler.phase == .alerting
        menu.addItem(wave)
        #endif
        menu.addItem(.separator())

        // 루틴 시간 행: 프리셋 라디오 + 앉기·서기·움직이기 입력칸. 눌러도 메뉴가 닫히지 않는다.
        let times = NSMenuItem()
        let timesView = RoutineTimesMenuItemView()
        timesView.onPreset = { [weak self] minutes in self?.scheduler.applyPreset(minutes: minutes) }
        timesView.onCommit = { [weak self] routine, minutes in
            self?.scheduler.setMinutes(minutes, for: routine) ?? false
        }
        times.view = timesView
        menu.addItem(times)
        routineTimesView = timesView
        menu.addItem(.separator())

        // 슬라이더 행: 끄는 동안 숫자가 바뀌고, 손을 뗄 때 적용된다 (메뉴는 닫히지 않음).
        let escalation = NSMenuItem()
        let sliderView = SliderMenuItemView(value: Settings.escalationSeconds, range: Settings.escalationRange) {
            "새우 증식 속도: \(Self.escalationText($0)) 한 마리"
        }
        sliderView.onCommit = { [weak self] seconds in self?.scheduler.setEscalationSeconds(seconds) }
        escalation.view = sliderView
        menu.addItem(escalation)
        menu.addItem(.separator())

        menu.addItem(awayMenuItem())

        let login = item("로그인 시 자동 실행", #selector(handleToggleLaunchAtLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)
        if telemetry.isAvailable {
            let stats = item("익명 사용 통계 보내기", #selector(handleToggleTelemetry))
            stats.state = telemetry.isEnabled ? .on : .off
            menu.addItem(stats)
        }
        menu.addItem(.separator())

        menu.addItem(item("종료", #selector(handleQuit), key: "q"))
    }

    // MARK: - Menu builders

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    /// 메뉴가 열린 채로 상태·설정이 바뀌었을 때 해당 항목만 그 자리에서 갱신한다.
    private func updateOpenMenu() {
        guard openMenu != nil else { return }
        statusMenuItem?.title = statusText()
        for (routine, item) in startItems {
            item.title = startTitle(routine)
            item.isEnabled = routine.isOn
        }
        routineTimesView?.refresh()
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

    @objc private func handleWaveNow() {
        overlay.sweepAway()
        scheduler.acknowledge()
    }
    #endif

    @objc private func handleRestartTurn() {
        telemetry.recordQuickAction(.restart)
        scheduler.startTurn(nil)
    }

    @objc private func handleStartRoutine(_ sender: NSMenuItem) {
        guard let routine = BreakScheduler.Routine(rawValue: sender.tag) else { return }
        let action: Telemetry.QuickAction = switch routine {
        case .sitting: .sit
        case .standing: .stand
        case .moving: .move
        }
        telemetry.recordQuickAction(action)
        scheduler.startTurn(routine)
    }

    @objc private func handleToggleTelemetry() {
        telemetry.setEnabled(!telemetry.isEnabled)
    }

    @objc private func handleSelectAway(_ sender: NSMenuItem) {
        Settings.awayThresholdMinutes = sender.tag
        presence.settingsChanged()
        telemetry.recordSettingsIfChanged()
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
            let doing = switch scheduler.routine {
            case .sitting: "앉아 있는 중"
            case .standing: "서 있는 중"
            case .moving: "움직이는 중"
            }
            return "\(doing) · 다음 새우까지 약 \(Self.minutesLeft(until: deadline))분"
        case .alerting:
            // 타이머가 울리는 순간 이미 다음 루틴으로 넘어가 있다.
            let action = switch scheduler.routine {
            case .sitting: "앉아서"
            case .standing: "일어나서"
            case .moving: "일어나 움직이며"
            }
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

    private func startTitle(_ routine: BreakScheduler.Routine) -> String {
        let minutes = Settings.minutes(for: routine)
        let length = minutes > 0 ? Self.durationText(minutes) : "꺼짐"
        return "\(routine.label) 타이머 바로 시작 (\(length))"
    }

    private static func escalationText(_ seconds: Int) -> String {
        seconds >= 60 ? "1분마다" : "\(seconds)초마다"
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

/// 메뉴 안에 넣는 "글자 + 슬라이더" 행. 끄는 동안 글자의 숫자가 바뀌고, 마우스를 뗄 때 onCommit 이 한 번 불린다
/// (끄는 내내 설정을 저장·통계 기록하지 않도록).
private final class SliderMenuItemView: NSView {
    var onCommit: (Int) -> Void = { _ in }

    private let label = NSTextField(labelWithString: "")
    private let slider: NSSlider
    private let text: (Int) -> String
    private var committedValue: Int

    init(value: Int, range: ClosedRange<Int>, text: @escaping (Int) -> String) {
        self.text = text
        committedValue = value
        slider = NSSlider(value: Double(value), minValue: Double(range.lowerBound), maxValue: Double(range.upperBound),
                          target: nil, action: nil)
        super.init(frame: NSRect(x: 0, y: 0, width: 280, height: 46))
        autoresizingMask = [.width]

        label.font = .menuFont(ofSize: 0)
        label.textColor = .labelColor
        slider.controlSize = .small
        slider.isContinuous = true
        slider.target = self
        slider.action = #selector(handleSlide)

        let minLabel = Self.caption("\(range.lowerBound)초")
        let maxLabel = Self.caption("1분")
        for view in [label, slider, minLabel, maxLabel] as [NSView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            label.topAnchor.constraint(equalTo: topAnchor, constant: 4),
            minLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            minLabel.centerYAnchor.constraint(equalTo: slider.centerYAnchor),
            slider.leadingAnchor.constraint(equalTo: minLabel.trailingAnchor, constant: 6),
            slider.trailingAnchor.constraint(equalTo: maxLabel.leadingAnchor, constant: -6),
            slider.topAnchor.constraint(equalTo: label.bottomAnchor, constant: 4),
            maxLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            maxLabel.centerYAnchor.constraint(equalTo: slider.centerYAnchor),
        ])
        label.stringValue = text(value)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    @objc private func handleSlide() {
        let value = Int(slider.doubleValue.rounded())
        label.stringValue = text(value)
        // 끄는 중에는 글자만 바꾸고, 마우스를 뗄 때 한 번만 적용한다.
        guard NSApp.currentEvent?.type == .leftMouseUp, value != committedValue else { return }
        committedValue = value
        onCommit(value)
    }

    private static func caption(_ text: String) -> NSTextField {
        let field = NSTextField(labelWithString: text)
        field.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        field.textColor = .secondaryLabelColor
        return field
    }
}

/// 메뉴 안에 넣는 루틴 시간 행: 위쪽 프리셋 라디오(10분·20분·30분·1시간·직접 입력) + 앉기·서기·움직이기 입력칸.
/// - 프리셋은 켜진(0분이 아닌) 루틴을 모두 그 시간으로 바꾼다 (onPreset).
/// - "직접 입력"을 누르거나 입력칸을 누르면 바로 입력할 수 있다. Return·다른 칸으로 이동·메뉴 닫기 때 적용된다 (onCommit).
/// - 눌러도 메뉴가 닫히지 않는다.
private final class RoutineTimesMenuItemView: NSView, NSTextFieldDelegate {
    var onPreset: (Int) -> Void = { _ in }
    /// 입력한 값을 적용한다. 받아들이지 않으면(범위 밖, 켜진 마지막 루틴을 0으로) false → 원래 값으로 되돌린다.
    var onCommit: (BreakScheduler.Routine, Int) -> Bool = { _, _ in false }

    private var presetButtons: [NSButton] = []
    private var customButton: NSButton!
    private var fields: [BreakScheduler.Routine: RoutineField] = [:]
    /// "직접 입력"을 골랐거나 입력칸을 눌렀으면, 값이 프리셋과 같아도 "직접 입력"에 표시를 유지한다.
    private var isCustomChosen = false

    init() {
        super.init(frame: NSRect(x: 0, y: 0, width: 300, height: 10))
        autoresizingMask = [.width]

        let caption = NSTextField(labelWithString: "루틴 시간 (0분이면 건너뛰기)")
        caption.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        caption.textColor = .secondaryLabelColor

        presetButtons = Settings.presetIntervals.map { minutes in
            radio(minutes % 60 == 0 ? "\(minutes / 60)시간" : "\(minutes)분", tag: minutes)
        }
        customButton = radio("직접 입력", tag: 0)
        let radios = NSStackView(views: presetButtons + [customButton])
        radios.spacing = 8

        var rows: [NSView] = [caption, radios]
        for routine in BreakScheduler.Routine.allCases {
            let label = NSTextField(labelWithString: routine.label)
            label.font = .menuFont(ofSize: 0)
            label.widthAnchor.constraint(equalToConstant: 64).isActive = true
            let field = RoutineField(string: "")
            field.tag = routine.rawValue
            field.alignment = .right
            field.delegate = self
            field.target = self
            field.action = #selector(handleFieldReturn(_:))
            field.onClick = { [weak self] in self?.chooseCustom() }
            field.widthAnchor.constraint(equalToConstant: 56).isActive = true
            fields[routine] = field
            let unit = NSTextField(labelWithString: "분")
            unit.font = .menuFont(ofSize: 0)
            rows.append(NSStackView(views: [label, field, unit]))
        }

        let stack = NSStackView(views: rows)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.edgeInsets = NSEdgeInsets(top: 4, left: 14, bottom: 6, right: 14)
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor),
            stack.topAnchor.constraint(equalTo: topAnchor),
        ])
        let size = stack.fittingSize
        setFrameSize(NSSize(width: max(300, size.width), height: size.height))
        refresh()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    /// 저장된 시간으로 다시 표시한다 (입력 중인 칸은 건드리지 않는다).
    func refresh() {
        for (routine, field) in fields where field.currentEditor() == nil {
            field.stringValue = "\(Settings.minutes(for: routine))"
        }
        let onValues = Set(BreakScheduler.Routine.allCases.filter(\.isOn).map(Settings.minutes(for:)))
        let preset = !isCustomChosen && onValues.count == 1 ? onValues.first : nil
        for button in presetButtons { button.state = button.tag == preset ? .on : .off }
        customButton.state = presetButtons.contains { $0.state == .on } ? .off : .on
    }

    /// 입력 중인 칸이 있으면 그 값을 적용한다 (메뉴가 닫힐 때).
    func commitEditing() {
        for field in fields.values where field.currentEditor() != nil { commit(field) }
    }

    // MARK: - Actions

    @objc private func handleRadio(_ sender: NSButton) {
        guard sender.tag > 0 else {
            chooseCustom()
            if let first = fields[.sitting] {
                window?.makeFirstResponder(first)
                first.selectText(nil)
            }
            return
        }
        isCustomChosen = false
        // 입력 중인 칸이 있으면 그 값을 먼저 적용하고 입력을 끝낸다 (그래야 프리셋 값으로 다시 표시된다).
        commitEditing()
        window?.makeFirstResponder(nil)
        onPreset(sender.tag) // → 스케줄러 변경 → 메뉴 갱신 → refresh
        refresh()
    }

    @objc private func handleFieldReturn(_ sender: NSTextField) {
        commit(sender)
    }

    func controlTextDidEndEditing(_ notification: Notification) {
        guard let field = notification.object as? NSTextField else { return }
        commit(field)
    }

    private func chooseCustom() {
        isCustomChosen = true
        refresh()
    }

    private func commit(_ field: NSTextField) {
        guard let routine = BreakScheduler.Routine(rawValue: field.tag) else { return }
        let text = field.stringValue.trimmingCharacters(in: .whitespaces)
        let current = Settings.minutes(for: routine)
        if let minutes = Int(text), minutes == current { return }
        if let minutes = Int(text), onCommit(routine, minutes) { return }
        // 숫자가 아니거나 받아들일 수 없는 값 → 알리고 원래 값으로.
        NSSound.beep()
        field.stringValue = "\(current)"
    }

    private func radio(_ title: String, tag: Int) -> NSButton {
        let button = NSButton(radioButtonWithTitle: title, target: self, action: #selector(handleRadio(_:)))
        button.tag = tag
        button.controlSize = .small
        return button
    }
}

/// 마우스로 눌렀을 때를 알려 주는 입력칸 ("직접 입력"으로 표시를 바꾸는 용도).
/// 메뉴가 열릴 때 저절로 포커스를 받는 경우와 구분하려고 포커스가 아니라 마우스 클릭을 본다.
private final class RoutineField: NSTextField {
    var onClick: () -> Void = {}

    override func mouseDown(with event: NSEvent) {
        onClick()
        super.mouseDown(with: event)
    }
}
