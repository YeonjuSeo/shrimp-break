import Foundation

/// 카운트다운·알림·에스컬레이션 상태 머신.
///
/// 성능 메모: 초 단위로 tick 하지 않는다. "마감 시각(deadline)" 하나만 잡고
/// 그 시각에 한 번 깨어나는 Timer 를 건다. 남은 시간은 필요할 때 deadline - now 로 계산한다.
@MainActor
final class BreakScheduler {
    enum Phase: Equatable {
        case counting(deadline: Date)
        case alerting
        case paused
        case away
    }

    /// 이번 차례에 사용자가 취할 자세. 타이머가 울릴 때마다 교대된다 (앉음 → 섬 → 앉음 …).
    enum Posture {
        case sitting
        case standing

        var toggled: Posture { self == .sitting ? .standing : .sitting }
    }

    /// 새우 한 마리를 내보내야 할 때 (알림 시작 + 이후 1분마다).
    var onSpawn: () -> Void = {}
    /// 화면의 새우를 모두 치워야 할 때.
    var onClear: () -> Void = {}
    /// 상태가 바뀌었을 때 (메뉴바 표시 갱신용).
    var onChange: () -> Void = {}

    private(set) var isUserPaused = false
    private(set) var isAway = false
    private(set) var isAlerting = false
    private(set) var posture: Posture = .sitting
    private var deadline: Date?
    /// 일시정지 시점에 남아 있던 시간. nil 이면 재개 시 간격 전체로 새로 시작.
    private var pausedRemaining: TimeInterval?

    private var deadlineTimer: Timer?
    private var escalationTimer: Timer?

    private static let escalationInterval: TimeInterval = 60

    var phase: Phase {
        if isUserPaused { return .paused }
        if isAway { return .away }
        if isAlerting { return .alerting }
        if let deadline { return .counting(deadline: deadline) }
        return .paused
    }

    /// 이번 차례(지금 자세)의 길이.
    private var interval: TimeInterval { TimeInterval(Settings.minutes(for: posture) * 60) }
    private var isActive: Bool { !isUserPaused && !isAway }

    func start() {
        restartCycle()
    }

    /// 새우를 클릭함 = 자세를 바꿨다. 새우를 치우고 다음 주기를 시작한다.
    func acknowledge() {
        restartCycle()
    }

    /// 메뉴에서 직접 차례를 시작한다. target 이 nil 이면 지금 자세 차례를 처음부터 다시 센다.
    /// 일시정지 중이었다면 풀고 바로 시작한다. 새우가 나와 있었다면 치운다.
    func startTurn(_ target: Posture?) {
        if let target { posture = target }
        isUserPaused = false
        pausedRemaining = nil
        restartCycle()
    }

    #if SHRIMP_DEV_TOOLS
    /// 테스트용: 지금 바로 알림.
    func triggerNow() {
        guard isActive else { return }
        fire()
    }
    #endif

    /// 타이머 한 개 모드의 간격.
    func setInterval(minutes: Int) {
        applyTimerChange { Settings.intervalMinutes = minutes }
    }

    /// 타이머 두 개 모드의 자세별 시간.
    func setMinutes(_ minutes: Int, for target: Posture) {
        applyTimerChange {
            switch target {
            case .sitting: Settings.sittingMinutes = minutes
            case .standing: Settings.standingMinutes = minutes
            }
        }
    }

    /// 타이머 한 개 ↔ 두 개 전환.
    /// - 한 개 → 두 개: 앉음·섬 모두 지금 간격으로 채운다.
    /// - 두 개 → 한 개: 서 있는 시간을 간격으로 쓴다.
    func setSeparatePostureTimers(_ isOn: Bool) {
        guard isOn != Settings.usesSeparatePostureTimers else { return }
        applyTimerChange {
            if isOn {
                Settings.sittingMinutes = Settings.intervalMinutes
                Settings.standingMinutes = Settings.intervalMinutes
            } else {
                Settings.intervalMinutes = Settings.standingMinutes
            }
            Settings.usesSeparatePostureTimers = isOn
        }
    }

    func setPaused(_ paused: Bool) {
        guard paused != isUserPaused else { return }
        if paused {
            pausedRemaining = isAlerting ? nil : deadline.map { max(0, $0.timeIntervalSinceNow) }
            isUserPaused = true
            stopAll()
        } else {
            isUserPaused = false
            if isActive { scheduleDeadline(after: pausedRemaining ?? interval) }
            pausedRemaining = nil
        }
        onChange()
    }

    /// 화면 잠금·잠자기·장시간 무입력 = 자리를 비웠다(= 이미 일어났다).
    /// 돌아오면 자리에 다시 앉은 것으로 보고 주기를 처음부터 다시 센다.
    func setAway(_ away: Bool) {
        guard away != isAway else { return }
        isAway = away
        if !away { posture = .sitting }
        if away {
            stopAll()
        } else if isUserPaused {
            pausedRemaining = nil
        } else {
            restartCycle()
            return
        }
        onChange()
    }

    // MARK: - Private

    /// 타이머 설정을 바꾼다. 이번 차례의 길이가 바뀌었을 때만 카운트다운을 새로 시작하고,
    /// 다음 차례에만 영향을 주는 변경(예: 앉아 있는 중에 서 있는 시간 변경)은 진행 중인 카운트다운을 유지한다.
    private func applyTimerChange(_ change: () -> Void) {
        let before = interval
        change()
        // 알림 중이면 다음 차례는 새우를 클릭할 때 새 값으로 시작한다 (설정 변경이 클릭을 대신하지 않도록).
        guard interval != before, !isAlerting else {
            onChange()
            return
        }
        if isUserPaused {
            pausedRemaining = nil
            onChange()
        } else {
            restartCycle()
        }
    }

    private func restartCycle() {
        stopAll()
        if isActive { scheduleDeadline(after: interval) }
        onChange()
    }

    private func scheduleDeadline(after seconds: TimeInterval) {
        let date = Date().addingTimeInterval(seconds)
        deadline = date
        let timer = Timer(fire: date, interval: 0, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.fire() }
        }
        // 시스템이 다른 wake-up 과 묶어서 처리할 수 있도록 여유를 준다 (전력 절약).
        timer.tolerance = 2
        RunLoop.main.add(timer, forMode: .common)
        deadlineTimer = timer
        onChange()
    }

    private func fire() {
        deadlineTimer?.invalidate()
        deadlineTimer = nil
        deadline = nil
        isAlerting = true
        posture = posture.toggled
        onSpawn()

        escalationTimer?.invalidate()
        let timer = Timer(timeInterval: Self.escalationInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.onSpawn() }
        }
        timer.tolerance = 1
        RunLoop.main.add(timer, forMode: .common)
        escalationTimer = timer
        onChange()
    }

    private func stopAll() {
        deadlineTimer?.invalidate()
        deadlineTimer = nil
        escalationTimer?.invalidate()
        escalationTimer = nil
        deadline = nil
        if isAlerting {
            isAlerting = false
            onClear()
        }
    }
}
