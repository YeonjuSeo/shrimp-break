import AppKit
import CoreGraphics

/// "사용자가 지금 자리에 앉아 Mac 을 쓰고 있는가"를 판단한다.
///
/// 앱(Slack·Claude 등) 실행 여부 대신 OS 신호만 본다:
/// - 시스템 잠자기 / 디스플레이 꺼짐 / 화면 잠금 / 사용자 전환 → 즉시 자리 비움
/// - 키보드·마우스 입력이 N분 이상 없음 → 자리 비움 (일어났다고 간주)
/// 별도 권한(손쉬운 사용 등) 없이 동작한다.
@MainActor
final class PresenceMonitor {
    private enum Reason: Hashable {
        case systemSleep, displaySleep, locked, sessionInactive, idle
    }

    var onAwayChanged: (Bool) -> Void = { _ in }

    private var reasons: Set<Reason> = [] {
        didSet {
            let wasAway = !oldValue.isEmpty
            let isAway = !reasons.isEmpty
            if wasAway != isAway { onAwayChanged(isAway) }
        }
    }

    private var idleTimer: Timer?
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []

    /// 자리에 있는 동안의 무입력 체크 주기. 분 단위 판정이라 30초면 충분하다.
    private static let presentPollInterval: TimeInterval = 30
    /// 자리 비움 상태에서 복귀를 감지하는 주기.
    private static let awayPollInterval: TimeInterval = 3

    var isAway: Bool { !reasons.isEmpty }

    func start() {
        let workspace = NSWorkspace.shared.notificationCenter
        observe(workspace, NSWorkspace.willSleepNotification) { $0.reasons.insert(.systemSleep) }
        observe(workspace, NSWorkspace.didWakeNotification) { $0.reasons.remove(.systemSleep) }
        observe(workspace, NSWorkspace.screensDidSleepNotification) { $0.reasons.insert(.displaySleep) }
        observe(workspace, NSWorkspace.screensDidWakeNotification) { $0.reasons.remove(.displaySleep) }
        observe(workspace, NSWorkspace.sessionDidResignActiveNotification) { $0.reasons.insert(.sessionInactive) }
        observe(workspace, NSWorkspace.sessionDidBecomeActiveNotification) { $0.reasons.remove(.sessionInactive) }

        let distributed = DistributedNotificationCenter.default()
        observe(distributed, Notification.Name("com.apple.screenIsLocked")) { $0.reasons.insert(.locked) }
        observe(distributed, Notification.Name("com.apple.screenIsUnlocked")) { $0.reasons.remove(.locked) }

        rescheduleIdlePolling()
    }

    /// 자리 비움 판정 시간 설정이 바뀌었을 때 호출.
    func settingsChanged() {
        if Settings.awayThresholdMinutes == 0 { reasons.remove(.idle) }
        rescheduleIdlePolling()
    }

    // MARK: - Private

    private func observe(_ center: NotificationCenter, _ name: Notification.Name,
                         _ handler: @escaping @MainActor (PresenceMonitor) -> Void) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                handler(self)
                self.rescheduleIdlePolling()
            }
        }
        observers.append((center, token))
    }

    private func rescheduleIdlePolling() {
        idleTimer?.invalidate()
        idleTimer = nil

        // 잠자기·잠금 중에는 무입력 체크가 의미 없으므로 깨어나지 않는다.
        let hardAway = reasons.contains { $0 != .idle }
        guard Settings.awayThresholdMinutes > 0, !hardAway else { return }

        let interval = reasons.contains(.idle) ? Self.awayPollInterval : Self.presentPollInterval
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.checkIdle() }
        }
        timer.tolerance = interval * 0.3
        RunLoop.main.add(timer, forMode: .common)
        idleTimer = timer
    }

    private func checkIdle() {
        let idleSeconds = Self.secondsSinceLastInput()
        let threshold = TimeInterval(Settings.awayThresholdMinutes * 60)

        if reasons.contains(.idle) {
            // 최근 몇 초 안에 입력이 있었으면 돌아온 것.
            if idleSeconds < Self.awayPollInterval + 2 {
                reasons.remove(.idle)
                rescheduleIdlePolling()
            }
        } else if idleSeconds >= threshold {
            reasons.insert(.idle)
            rescheduleIdlePolling()
        }
    }

    /// 마지막 키보드·마우스·트랙패드 입력 이후 경과 시간(초).
    private static func secondsSinceLastInput() -> TimeInterval {
        // kCGAnyInputEventType (~0)
        let anyInput = CGEventType(rawValue: ~0)!
        return CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: anyInput)
    }
}
