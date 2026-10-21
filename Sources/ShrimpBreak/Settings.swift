import Foundation

/// UserDefaults 에 저장되는 사용자 설정.
@MainActor
enum Settings {
    private static let defaults = UserDefaults.standard

    static let presetIntervals = [10, 20, 30, 60]
    static let awayPresets = [3, 5, 10]
    /// 루틴 시간으로 입력할 수 있는 범위(분). 0 = 그 루틴은 건너뛴다.
    static let routineMinutesRange = 0...480

    /// 앉아 있는 시간(분). 기본 30분.
    static var sittingMinutes: Int {
        get { minutes(forKey: "sittingMinutes", fallback: 30) }
        set { defaults.set(newValue, forKey: "sittingMinutes") }
    }

    /// 서 있는 시간(분). 기본 30분.
    static var standingMinutes: Int {
        get { minutes(forKey: "standingMinutes", fallback: 30) }
        set { defaults.set(newValue, forKey: "standingMinutes") }
    }

    /// 움직이는 시간(분). 기본 0분(건너뜀).
    static var movingMinutes: Int {
        get { minutes(forKey: "movingMinutes", fallback: 0) }
        set { defaults.set(newValue, forKey: "movingMinutes") }
    }

    /// 루틴 차례의 길이(분). 0 이면 그 루틴은 건너뛴다.
    static func minutes(for routine: BreakScheduler.Routine) -> Int {
        switch routine {
        case .sitting: return sittingMinutes
        case .standing: return standingMinutes
        case .moving: return movingMinutes
        }
    }

    /// 서기·움직이기 차례에는 자리 비움을 무시하지만, 이 시간보다 오래 비우면 진짜 자리 비움으로 본다.
    /// 그 차례 길이의 2배, 최소 10분 (짧은 차례에서 잠깐 입력이 없었다고 새우 없이 넘어가지 않도록).
    static func awayGraceSeconds(during routine: BreakScheduler.Routine) -> TimeInterval {
        max(TimeInterval(minutes(for: routine) * 60 * 2), 10 * 60)
    }

    static func setMinutes(_ minutes: Int, for routine: BreakScheduler.Routine) {
        switch routine {
        case .sitting: sittingMinutes = minutes
        case .standing: standingMinutes = minutes
        case .moving: movingMinutes = minutes
        }
    }

    /// 예전 설정(타이머 한 개 / 앉기·서기 따로)을 앉기·서기·움직이기 시간으로 옮긴다. 처음 한 번만.
    /// - 타이머 한 개였으면 그 간격을 앉기·서기 모두에, 따로였으면 각자 값을 그대로. 움직이기는 0(건너뜀).
    static func migrateIfNeeded() {
        guard defaults.object(forKey: "movingMinutes") == nil else { return }
        let interval = minutes(forKey: "intervalMinutes", fallback: 30)
        if !defaults.bool(forKey: "usesSeparatePostureTimers") {
            sittingMinutes = interval
            standingMinutes = interval
        } else {
            sittingMinutes = max(1, minutes(forKey: "sittingMinutes", fallback: interval))
            standingMinutes = max(1, minutes(forKey: "standingMinutes", fallback: interval))
        }
        movingMinutes = 0
        defaults.removeObject(forKey: "intervalMinutes")
        defaults.removeObject(forKey: "usesSeparatePostureTimers")
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

    /// 저장된 값이 없을 때만 fallback (0 은 "건너뜀"이라는 저장된 값이다).
    private static func minutes(forKey key: String, fallback: Int) -> Int {
        guard defaults.object(forKey: key) != nil else { return fallback }
        return min(max(defaults.integer(forKey: key), routineMinutesRange.lowerBound), routineMinutesRange.upperBound)
    }
}
