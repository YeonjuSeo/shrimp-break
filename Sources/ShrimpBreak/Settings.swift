import Foundation

/// UserDefaults 에 저장되는 사용자 설정.
@MainActor
enum Settings {
    private static let defaults = UserDefaults.standard

    static let presetIntervals = [10, 20, 30, 60]
    static let awayPresets = [3, 5, 10]

    /// 알림 간격(분). 타이머 한 개 모드에서 앉음·섬 모두에 쓴다. 기본 30분.
    static var intervalMinutes: Int {
        get { minutes(forKey: "intervalMinutes", fallback: 30) }
        set { defaults.set(newValue, forKey: "intervalMinutes") }
    }

    /// true 면 앉아 있는 시간·서 있는 시간을 따로 쓴다 (타이머 두 개 모드).
    static var usesSeparatePostureTimers: Bool {
        get { defaults.bool(forKey: "usesSeparatePostureTimers") }
        set { defaults.set(newValue, forKey: "usesSeparatePostureTimers") }
    }

    /// 타이머 두 개 모드의 앉아 있는 시간(분).
    static var sittingMinutes: Int {
        get { minutes(forKey: "sittingMinutes", fallback: intervalMinutes) }
        set { defaults.set(newValue, forKey: "sittingMinutes") }
    }

    /// 타이머 두 개 모드의 서 있는 시간(분).
    static var standingMinutes: Int {
        get { minutes(forKey: "standingMinutes", fallback: intervalMinutes) }
        set { defaults.set(newValue, forKey: "standingMinutes") }
    }

    /// 지금 모드에서 해당 자세 차례의 길이(분).
    static func minutes(for posture: BreakScheduler.Posture) -> Int {
        guard usesSeparatePostureTimers else { return intervalMinutes }
        return posture == .sitting ? sittingMinutes : standingMinutes
    }

    /// 새우를 클릭하지 않으면 몇 초마다 한 마리씩 늘어나는지. 3 ~ 60초, 기본 60초.
    static let escalationRange = 3...60
    static var escalationSeconds: Int {
        get {
            let value = defaults.integer(forKey: "escalationSeconds")
            return value > 0 ? min(max(value, escalationRange.lowerBound), escalationRange.upperBound) : 60
        }
        set { defaults.set(newValue, forKey: "escalationSeconds") }
    }

    /// 이 시간(분) 이상 키보드·마우스 입력이 없으면 "자리를 비웠다(=일어났다)"로 본다. 0 이면 끔.
    static var awayThresholdMinutes: Int {
        get {
            guard defaults.object(forKey: "awayThresholdMinutes") != nil else { return 5 }
            return defaults.integer(forKey: "awayThresholdMinutes")
        }
        set { defaults.set(newValue, forKey: "awayThresholdMinutes") }
    }

    private static func minutes(forKey key: String, fallback: Int) -> Int {
        let value = defaults.integer(forKey: key)
        return value > 0 ? value : fallback
    }
}
