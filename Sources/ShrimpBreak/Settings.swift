import Foundation

/// UserDefaults 에 저장되는 사용자 설정.
@MainActor
enum Settings {
    private static let defaults = UserDefaults.standard

    static let presetIntervals = [10, 20, 30, 60]
    static let awayPresets = [3, 5, 10]

    /// 알림 간격(분). 기본 30분.
    static var intervalMinutes: Int {
        get {
            let value = defaults.integer(forKey: "intervalMinutes")
            return value > 0 ? value : 30
        }
        set { defaults.set(newValue, forKey: "intervalMinutes") }
    }

    /// 이 시간(분) 이상 키보드·마우스 입력이 없으면 "자리를 비웠다(=일어났다)"로 본다. 0 이면 끔.
    static var awayThresholdMinutes: Int {
        get {
            guard defaults.object(forKey: "awayThresholdMinutes") != nil else { return 5 }
            return defaults.integer(forKey: "awayThresholdMinutes")
        }
        set { defaults.set(newValue, forKey: "awayThresholdMinutes") }
    }
}
