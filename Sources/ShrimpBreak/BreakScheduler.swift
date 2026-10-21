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

    /// 이번 차례에 사용자가 할 루틴. 타이머가 울릴 때마다 다음 루틴으로 넘어간다
    /// (앉기 → 서기 → 움직이기 → 앉기 …, 시간이 0인 루틴은 건너뛴다).
    enum Routine: Int, CaseIterable {
        case sitting
        case standing
        case moving

        /// 시간이 0이 아닌 다음 루틴. 켜진 루틴이 자기 하나뿐이면 자기 자신.
        @MainActor var next: Routine {
            var candidate = self
            repeat {
                candidate = Routine(rawValue: (candidate.rawValue + 1) % Routine.allCases.count)!
            } while Settings.minutes(for: candidate) == 0 && candidate != self
            return candidate
        }

        /// 처음(또는 자리에 돌아왔을 때) 시작할 루틴: 앉기부터, 앉기가 꺼져 있으면 그다음 켜진 루틴.
        @MainActor static var first: Routine {
            Settings.minutes(for: .sitting) > 0 ? .sitting : Routine.sitting.next
        }

        @MainActor var isOn: Bool { Settings.minutes(for: self) > 0 }

        var label: String {
            switch self {
            case .sitting: return "앉기"
            case .standing: return "서기"
            case .moving: return "움직이기"
            }
        }
    }

    /// 새우 한 마리를 내보내야 할 때 (알림 시작 + 이후 1분마다).
    var onSpawn: () -> Void = {}
    /// 화면의 새우를 모두 치워야 할 때.
    var onClear: () -> Void = {}
    /// 상태가 바뀌었을 때 (메뉴바 표시 갱신용).
    var onChange: () -> Void = {}

    private(set) var isUserPaused = false
    private(set) var isAway = false
    /// 서기·움직이기 차례 중에 자리를 비운 시각 (무시하고 타이머는 계속). 이 동안은 새우를 더 늘리지 않는다.
    /// 상한(Settings.awayGraceSeconds)보다 오래 비우면 진짜 자리 비움으로 넘긴다 (퇴근 등).
    private var awayDuringAwayTurnSince: Date?
    private var awayGrace: TimeInterval = 0
    private var awayGraceTimer: Timer?
    private(set) var isAlerting = false
    /// 다음에 할(카운트다운 중이면 지금 하는) 루틴. 알림 중에는 이미 다음 루틴으로 넘어가 있다.
    private(set) var routine: Routine = .first
    /// 알림이 울리기 직전까지 하던 루틴.
    private var previousRoutine: Routine = .sitting
    private var deadline: Date?
    /// 일시정지 시점에 남아 있던 시간. nil 이면 재개 시 간격 전체로 새로 시작.
    private var pausedRemaining: TimeInterval?

    private var deadlineTimer: Timer?
    private var escalationTimer: Timer?


    var phase: Phase {
        if isUserPaused { return .paused }
        if isAway { return .away }
        if isAlerting { return .alerting }
        if let deadline { return .counting(deadline: deadline) }
        return .paused
    }

    /// 사용자가 지금 하고 있는 루틴. 알림 중에는 새우를 클릭하기 전이므로 아직 이전 루틴이다.
    var activity: Routine { isAlerting ? previousRoutine : routine }

    /// 이번 차례(지금 루틴)의 길이.
    private var interval: TimeInterval { TimeInterval(Settings.minutes(for: routine) * 60) }
    private var isActive: Bool { !isUserPaused && !isAway }

    func start() {
        restartCycle()
    }

    /// 새우를 클릭함 = 자세를 바꿨다. 새우를 치우고 다음 주기를 시작한다.
    func acknowledge() {
        restartCycle()
    }

    /// 메뉴에서 직접 차례를 시작한다. target 이 nil 이면 지금 루틴 차례를 처음부터 다시 센다.
    /// 일시정지 중이었다면 풀고 바로 시작한다. 새우가 나와 있었다면 치운다. 꺼진(0분) 루틴은 시작하지 않는다.
    func startTurn(_ target: Routine?) {
        if let target {
            guard target.isOn else { return }
            routine = target
        }
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

    /// 루틴 하나의 시간. 0 이면 그 루틴은 건너뛴다.
    /// 범위를 벗어나거나 켜진 마지막 루틴을 0으로 만들려 하면 바꾸지 않고 false.
    @discardableResult
    func setMinutes(_ minutes: Int, for target: Routine) -> Bool {
        guard Settings.routineMinutesRange.contains(minutes) else { return false }
        let othersOn = Routine.allCases.contains { $0 != target && $0.isOn }
        guard minutes > 0 || othersOn else { return false }
        applyTimerChange { Settings.setMinutes(minutes, for: target) }
        return true
    }

    /// 프리셋(10분·20분…): 켜진 루틴을 모두 같은 시간으로. 꺼진(0분) 루틴은 그대로 둔다.
    func applyPreset(minutes: Int) {
        applyTimerChange {
            for routine in Routine.allCases where routine.isOn { Settings.setMinutes(minutes, for: routine) }
        }
    }

    /// 새우 증식 간격(초). 새우가 나와 있는 중이면 지금부터 새 간격으로 늘린다.
    func setEscalationSeconds(_ seconds: Int) {
        Settings.escalationSeconds = seconds
        if isAlerting { startEscalation() }
        onChange()
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
    ///
    /// 단, 서기·움직이기 차례에는 자리를 뜨는 게 정상이므로 무시하고 타이머를 계속 돌린다.
    /// (그렇지 않으면 서 있는 동안 입력이 없어 자리 비움 → 돌아오는 순간 새우 없이 앉는 차례로 넘어가 버린다.)
    /// 그래도 그 차례 길이의 2배(최소 10분)보다 오래 비우면 퇴근 등으로 보고 진짜 자리 비움으로 넘긴다.
    func setAway(_ away: Bool) {
        if away {
            guard !isAway else { return }
            if !isUserPaused, activity != .sitting {
                startAwayGrace()
                return
            }
            enterAway()
            return
        }
        if let since = awayDuringAwayTurnSince {
            clearAwayDuringAwayTurn()
            // 잠자기 중에는 상한 타이머가 돌지 못하므로, 돌아온 시점에 한 번 더 확인한다.
            guard Date().timeIntervalSince(since) >= awayGrace else { return }
            enterAway()
        }
        guard isAway else { return }
        returnFromAway()
    }

    // MARK: - Private

    /// 서기·움직이기 차례에 자리를 비웠다 → 바로 멈추지 않고 상한까지 기다린다.
    private func startAwayGrace() {
        guard awayDuringAwayTurnSince == nil else { return }
        awayDuringAwayTurnSince = Date()
        awayGrace = Settings.awayGraceSeconds(during: activity)
        let timer = Timer(timeInterval: awayGrace, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.handleAwayGraceExpired() }
        }
        timer.tolerance = 10
        RunLoop.main.add(timer, forMode: .common)
        awayGraceTimer = timer
    }

    /// 서기·움직이기 차례의 자리 비움이 상한을 넘었다 → 진짜 자리 비움으로 넘긴다 (새우 정리, 타이머 정지).
    private func handleAwayGraceExpired() {
        guard awayDuringAwayTurnSince != nil else { return }
        clearAwayDuringAwayTurn()
        enterAway()
    }

    private func enterAway() {
        isAway = true
        stopAll()
        onChange()
    }

    /// 자리에 다시 앉은 것으로 보고 첫 루틴부터 다시 센다.
    private func returnFromAway() {
        isAway = false
        routine = .first
        if isUserPaused {
            pausedRemaining = nil
            onChange()
        } else {
            restartCycle()
        }
    }

    private func clearAwayDuringAwayTurn() {
        awayDuringAwayTurnSince = nil
        awayGraceTimer?.invalidate()
        awayGraceTimer = nil
    }

    /// 타이머 설정을 바꾼다. 이번 차례의 길이가 바뀌었을 때만 카운트다운을 새로 시작하고,
    /// 다음 차례에만 영향을 주는 변경(예: 앉아 있는 중에 서 있는 시간 변경)은 진행 중인 카운트다운을 유지한다.
    /// 지금(알림 중이면 다음) 루틴이 0분으로 꺼졌으면 그다음 켜진 루틴으로 넘어간다.
    private func applyTimerChange(_ change: () -> Void) {
        let before = (routine, interval)
        change()
        if !routine.isOn { routine = routine.next }
        // 알림 중이면 다음 차례는 새우를 클릭할 때 새 값으로 시작한다 (설정 변경이 클릭을 대신하지 않도록).
        guard routine != before.0 || interval != before.1, !isAlerting else {
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
        previousRoutine = routine
        routine = routine.next
        onSpawn()
        startEscalation()
        onChange()
    }

    /// 클릭할 때까지 설정한 간격(3~60초)마다 한 마리씩 늘린다.
    private func startEscalation() {
        escalationTimer?.invalidate()
        let interval = TimeInterval(Settings.escalationSeconds)
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.awayDuringAwayTurnSince == nil else { return } // 자리에 없을 땐 쌓지 않는다
                self.onSpawn()
            }
        }
        timer.tolerance = min(1, interval * 0.1)
        RunLoop.main.add(timer, forMode: .common)
        escalationTimer = timer
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
