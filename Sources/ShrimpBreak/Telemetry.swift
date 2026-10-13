import AppKit

/// 익명 사용 통계. 사용자가 동의한 경우에만 구글 시트(Apps Script 웹 앱)로 보낸다.
///
/// - 보내는 주소는 빌드할 때 Info.plist 의 ShrimpTelemetryURL 로 들어간다 (telemetry/endpoint.txt).
///   주소가 없으면 이 기능 전체가 꺼진다 (동의 창도 뜨지 않음).
/// - 매 행동마다 보내지 않는다. 하루 요약 한 줄을 모아 두었다가 최대 시간당 한 번 덮어쓰고,
///   날짜가 바뀌면 전날 최종본을 보낸다. 보내지 못한 것은 대기열에 남겨 다음 기회에 다시 보낸다.
/// - 이름·이메일·컴퓨터 이름은 보내지 않는다. id 는 동의할 때 만든 무작위 값이다.
@MainActor
final class Telemetry {
    enum QuickAction { case restart, sit, stand }

    private enum Consent: String { case unknown, granted, denied }

    private enum SegmentKind: String, Codable { case sat, stood, paused }

    private struct Segment {
        let kind: SegmentKind
        let startedAt: Date
    }

    private static let sharedKey = "shrimp-v1"
    private static let maxQueuedEvents = 200
    private static let eventsPerRequest = 20
    private static let dailyUpsertInterval: TimeInterval = 60 * 60

    #if SHRIMP_DEV_TOOLS
    private static let buildKind = "dev"
    #else
    private static let buildKind = "release"
    #endif

    private let endpoint: URL?
    private let defaults = UserDefaults.standard
    private var day: DayStats
    private var queue: [QueuedEvent]
    private var segment: Segment?
    private var lastPhaseKind: String?
    private var alertStartedAt: Date?
    private var lastDailyUpsertAt: Date?
    private var isSending = false
    private var quitCompletion: (@MainActor () -> Void)?

    init() {
        let url = (Bundle.main.object(forInfoDictionaryKey: "ShrimpTelemetryURL") as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        endpoint = url.flatMap { $0.hasPrefix("https://script.google.com/") ? URL(string: $0) : nil }
        day = Self.load(DayStats.self, key: "telemetryDay") ?? DayStats(date: Self.today())
        queue = Self.load([QueuedEvent].self, key: "telemetryQueue") ?? []
    }

    /// 보낼 주소가 들어간 빌드인지 (메뉴에 토글을 보여줄지).
    var isAvailable: Bool { endpoint != nil }
    var isEnabled: Bool { isAvailable && consent == .granted }

    private var consent: Consent {
        get { Consent(rawValue: defaults.string(forKey: "telemetryConsent") ?? "") ?? .unknown }
        set { defaults.set(newValue.rawValue, forKey: "telemetryConsent") }
    }

    private var installID: String? {
        get { defaults.string(forKey: "telemetryID") }
        set { defaults.set(newValue, forKey: "telemetryID") }
    }

    // MARK: - 시작 · 동의

    func start() {
        guard isAvailable else { return }
        switch consent {
        case .unknown:
            // 메뉴바 아이콘이 먼저 자리 잡은 뒤에 묻는다.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
                MainActor.assumeIsolated { self?.askConsent() }
            }
        case .granted:
            rollOverIfNeeded()
            upsertToday(force: true)
        case .denied:
            break
        }
    }

    /// 메뉴의 "익명 사용 통계 보내기" 토글.
    func setEnabled(_ isOn: Bool) {
        guard isAvailable, isOn != isEnabled else { return }
        if isOn {
            grant(source: "menu", choice: "turned_on")
        } else {
            revoke()
        }
    }

    private func askConsent() {
        let alert = NSAlert()
        alert.messageText = "새우 브레이크 익명 통계를 보내도 될까요?"
        alert.informativeText = """
        더 좋은 기본값을 정하는 데 참고하려고 해요.

        • 보내는 것: 타이머 설정, 하루 동안 새우가 나온 횟수와 몇 마리에서 일어났는지, \
        앉아/서 있던 시간, OS·앱 버전 등
        • 보내지 않는 것: 이름, 이메일, 컴퓨터 이름, 다른 앱 정보

        선택 결과(보내기/안 보내기)는 한 번 기록돼요. 메뉴의 "익명 사용 통계 보내기"에서 언제든 바꿀 수 있어요.
        """
        alert.addButton(withTitle: "보내기")
        alert.addButton(withTitle: "안 보내기")
        NSApp.activate()
        if alert.runModal() == .alertFirstButtonReturn {
            grant(source: "first_run", choice: "granted")
        } else {
            consent = .denied
            // 고유 ID 없이 "안 보냄" 한 건만 보낸다. 이후로는 아무것도 보내지 않는다.
            send([anonymousConsentEvent()]) { _ in }
        }
    }

    private func grant(source: String, choice: String) {
        consent = .granted
        let id = UUID().uuidString.lowercased()
        installID = id
        day = DayStats(date: Self.today())
        saveSettingsSnapshot(currentSettings())
        enqueue(baseEvent(type: "consent").merging(["choice": .string(choice), "source": .string(source)]) { $1 })
        enqueue(baseEvent(type: "install").merging(settingsFields(currentSettings())) { $1 })
        upsertToday(force: true)
    }

    /// 끄기: "끔"을 마지막으로 보내고, id·모아 둔 기록을 지운다.
    private func revoke() {
        enqueue(baseEvent(type: "consent").merging(["choice": .string("turned_off"), "source": .string("menu")]) { $1 })
        let pending = queue
        consent = .denied
        installID = nil
        queue = []
        segment = nil
        day = DayStats(date: Self.today())
        ["telemetryQueue", "telemetryDay", "telemetrySettings"].forEach(defaults.removeObject(forKey:))
        send(pending.map(\.body)) { _ in }
    }

    // MARK: - 기록

    /// 스케줄러 상태가 바뀔 때마다 호출. 알림·일시정지·자리 비움 횟수, 앉은/선/일시정지 시간, 설정 변경을 센다.
    func observe(_ scheduler: BreakScheduler) {
        guard isEnabled else { return }
        rollOverIfNeeded()

        let phaseKind: String
        switch scheduler.phase {
        case .counting: phaseKind = "counting"
        case .alerting: phaseKind = "alerting"
        case .paused: phaseKind = "paused"
        case .away: phaseKind = "away"
        }
        if phaseKind != lastPhaseKind {
            switch phaseKind {
            case "alerting":
                day.alerts += 1
                alertStartedAt = Date()
            case "paused": day.pauses += 1
            case "away": day.awayCount += 1
            default: break
            }
            lastPhaseKind = phaseKind
        }

        // 알림 중에는 posture 가 이미 다음 자세로 넘어가 있지만, 사용자는 아직 이전 자세다.
        let posture = phaseKind == "alerting" ? scheduler.posture.toggled : scheduler.posture
        let kind: SegmentKind? = switch phaseKind {
        case "counting", "alerting": posture == .sitting ? .sat : .stood
        case "paused": .paused
        default: nil
        }
        if kind != segment?.kind {
            closeSegment()
            segment = kind.map { Segment(kind: $0, startedAt: Date()) }
        }

        recordSettingsIfChanged()
        save()
    }

    func recordShrimpCount(_ count: Int) {
        guard isEnabled else { return }
        rollOverIfNeeded()
        day.maxShrimps = max(day.maxShrimps, count)
        save()
    }

    func recordAcknowledge(shrimpCount: Int, clickedTempura: Bool) {
        guard isEnabled else { return }
        rollOverIfNeeded()
        day.acknowledged += 1
        day.shrimpsAtClickSum += shrimpCount
        if clickedTempura { day.tempuraClicks += 1 } else { day.shrimpClicks += 1 }
        if let alertStartedAt {
            let seconds = Date().timeIntervalSince(alertStartedAt)
            day.reactionSecondsSum += seconds
            day.maxReactionSeconds = max(day.maxReactionSeconds, seconds)
        }
        alertStartedAt = nil
        day.ackHours[Calendar.current.component(.hour, from: Date()), default: 0] += 1
        save()
        upsertToday(force: false)
    }

    func recordQuickAction(_ action: QuickAction) {
        guard isEnabled else { return }
        rollOverIfNeeded()
        switch action {
        case .restart: day.quickRestart += 1
        case .sit: day.quickSit += 1
        case .stand: day.quickStand += 1
        }
        save()
    }

    /// 자리 비움 판정처럼 스케줄러를 거치지 않는 설정 변경 후 호출.
    func recordSettingsIfChanged() {
        guard isEnabled else { return }
        let current = currentSettings()
        guard let previous = Self.load(SettingsSnapshot.self, key: "telemetrySettings") else {
            saveSettingsSnapshot(current)
            return
        }
        guard current != previous else { return }
        let changes: [(String, String, String)] = [
            ("separate_timers", "\(previous.separate)", "\(current.separate)"),
            ("interval_minutes", "\(previous.interval)", "\(current.interval)"),
            ("sitting_minutes", "\(previous.sitting)", "\(current.sitting)"),
            ("standing_minutes", "\(previous.standing)", "\(current.standing)"),
            ("away_threshold", "\(previous.away)", "\(current.away)"),
        ].filter { $0.1 != $0.2 }
        for (field, from, to) in changes {
            enqueue(baseEvent(type: "settings").merging([
                "hour": .number(Double(Calendar.current.component(.hour, from: Date()))),
                "field": .string(field), "from": .string(from), "to": .string(to),
            ]) { $1 })
        }
        saveSettingsSnapshot(current)
    }

    /// 앱 종료 직전: 오늘 요약을 보내고 끝나면(또는 2초 뒤) completion.
    func flushBeforeQuit(_ completion: @escaping @MainActor () -> Void) {
        guard isEnabled else { return completion() }
        closeSegment()
        save()
        enqueueToday()
        quitCompletion = completion
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            MainActor.assumeIsolated { self?.finishQuit() }
        }
        flush { [weak self] in self?.finishQuit() }
    }

    private func finishQuit() {
        let completion = quitCompletion
        quitCompletion = nil
        completion?()
    }

    // MARK: - 하루 요약

    private func rollOverIfNeeded() {
        let today = Self.today()
        guard day.date != today else { return }
        closeSegment()
        enqueue(dailyEvent(for: day, openSegmentSeconds: nil))
        day = DayStats(date: today)
        save()
    }

    private func upsertToday(force: Bool) {
        if !force, let last = lastDailyUpsertAt, Date().timeIntervalSince(last) < Self.dailyUpsertInterval { return }
        enqueueToday()
        lastDailyUpsertAt = Date()
        flush {}
    }

    private func enqueueToday() {
        let openSeconds = segment.map { (kind: $0.kind, seconds: Date().timeIntervalSince($0.startedAt)) }
        enqueue(dailyEvent(for: day, openSegmentSeconds: openSeconds))
    }

    private func closeSegment() {
        guard let segment else { return }
        let seconds = Date().timeIntervalSince(segment.startedAt)
        switch segment.kind {
        case .sat: day.satSeconds += seconds
        case .stood: day.stoodSeconds += seconds
        case .paused: day.pausedSeconds += seconds
        }
        self.segment = nil
    }

    private func dailyEvent(for stats: DayStats, openSegmentSeconds: (kind: SegmentKind, seconds: TimeInterval)?) -> Event {
        var sat = stats.satSeconds, stood = stats.stoodSeconds, paused = stats.pausedSeconds
        if let open = openSegmentSeconds {
            switch open.kind {
            case .sat: sat += open.seconds
            case .stood: stood += open.seconds
            case .paused: paused += open.seconds
            }
        }
        let acknowledged = Double(stats.acknowledged)
        var event = baseEvent(type: "daily").merging(settingsFields(currentSettings())) { $1 }
        event["date"] = .string(stats.date)
        let numbers: [String: Double] = [
            "alerts": Double(stats.alerts),
            "acknowledged": acknowledged,
            "shrimp_clicks": Double(stats.shrimpClicks),
            "tempura_clicks": Double(stats.tempuraClicks),
            "avg_shrimps_at_click": acknowledged > 0 ? Self.round1(Double(stats.shrimpsAtClickSum) / acknowledged) : 0,
            "max_shrimps": Double(stats.maxShrimps),
            "avg_reaction_sec": acknowledged > 0 ? (stats.reactionSecondsSum / acknowledged).rounded() : 0,
            "max_reaction_sec": stats.maxReactionSeconds.rounded(),
            "sat_minutes": (sat / 60).rounded(),
            "stood_minutes": (stood / 60).rounded(),
            "pauses": Double(stats.pauses),
            "paused_minutes": (paused / 60).rounded(),
            "away_count": Double(stats.awayCount),
            "quick_restart": Double(stats.quickRestart),
            "quick_sit": Double(stats.quickSit),
            "quick_stand": Double(stats.quickStand),
            "errors": 0,
        ]
        numbers.forEach { event[$0.key] = .number($0.value) }
        for hour in 0..<24 {
            event[String(format: "ack_h%02d", hour)] = .number(Double(stats.ackHours[hour, default: 0]))
        }
        return event
    }

    // MARK: - 공통 필드

    private func baseEvent(type: String) -> Event {
        let screens = NSScreen.screens
        let version = ProcessInfo.processInfo.operatingSystemVersion
        return [
            "type": .string(type),
            "id": .string(installID ?? ""),
            "date": .string(Self.today()),
            "os": .string("mac"),
            "os_version": .string("\(version.majorVersion).\(version.minorVersion).\(version.patchVersion)"),
            "app_version": .string(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""),
            "build": .string(Self.buildKind),
            "locale": .string(Locale.current.identifier),
            "monitors": .number(Double(screens.count)),
            "max_scale": .number(Double(screens.map(\.backingScaleFactor).max() ?? 1)),
        ]
    }

    /// 동의하지 않은 사람용: 매번 새로 만든 일회용 id 로, 선택 결과만.
    private func anonymousConsentEvent() -> Event {
        var event = baseEvent(type: "consent")
        event["id"] = .string("anon-" + UUID().uuidString.lowercased())
        event["choice"] = .string("denied")
        event["source"] = .string("first_run")
        event["monitors"] = nil
        event["max_scale"] = nil
        return event
    }

    private func currentSettings() -> SettingsSnapshot {
        SettingsSnapshot(separate: Settings.usesSeparatePostureTimers, interval: Settings.intervalMinutes,
                         sitting: Settings.sittingMinutes, standing: Settings.standingMinutes,
                         away: Settings.awayThresholdMinutes)
    }

    private func settingsFields(_ settings: SettingsSnapshot) -> Event {
        [
            "separate_timers": .bool(settings.separate),
            "interval_minutes": .number(Double(settings.interval)),
            "sitting_minutes": .number(Double(settings.sitting)),
            "standing_minutes": .number(Double(settings.standing)),
            "away_threshold": .number(Double(settings.away)),
        ]
    }

    // MARK: - 전송

    private func enqueue(_ event: Event) {
        // 같은 날의 하루 요약은 최신 것 하나만 남긴다.
        let key: String? = event["type"] == .string("daily") ? "daily|\(event["date"].map { "\($0)" } ?? "")" : nil
        if let key { queue.removeAll { $0.key == key } }
        queue.append(QueuedEvent(uid: UUID().uuidString, key: key, body: event))
        if queue.count > Self.maxQueuedEvents { queue.removeFirst(queue.count - Self.maxQueuedEvents) }
        save()
        flush {}
    }

    private func flush(_ completion: @escaping @MainActor () -> Void) {
        guard !isSending, !queue.isEmpty else { return completion() }
        let batch = Array(queue.prefix(Self.eventsPerRequest))
        isSending = true
        send(batch.map(\.body)) { [weak self] isSent in
            guard let self else { return completion() }
            self.isSending = false
            if isSent {
                let sent = Set(batch.map(\.uid))
                self.queue.removeAll { sent.contains($0.uid) }
                self.save()
                if !self.queue.isEmpty { return self.flush(completion) }
            }
            completion()
        }
    }

    private func send(_ events: [Event], completion: @escaping @MainActor (Bool) -> Void) {
        guard let endpoint, !events.isEmpty,
              let body = try? JSONEncoder().encode(RequestBody(key: Self.sharedKey, events: events)) else {
            return completion(false)
        }
        var request = URLRequest(url: endpoint, timeoutInterval: 15)
        request.httpMethod = "POST"
        request.setValue("text/plain;charset=utf-8", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        Task { @MainActor in
            do {
                let (_, response) = try await URLSession.shared.data(for: request)
                let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                completion((200..<300).contains(status))
            } catch {
                completion(false) // 오프라인 등 → 대기열에 남겨 두고 다음에 다시
            }
        }
    }

    // MARK: - 저장

    private func save() {
        Self.store(day, key: "telemetryDay")
        Self.store(queue, key: "telemetryQueue")
    }

    private func saveSettingsSnapshot(_ settings: SettingsSnapshot) {
        Self.store(settings, key: "telemetrySettings")
    }

    private static func store<T: Encodable>(_ value: T, key: String) {
        if let data = try? JSONEncoder().encode(value) { UserDefaults.standard.set(data, forKey: key) }
    }

    private static func load<T: Decodable>(_ type: T.Type, key: String) -> T? {
        UserDefaults.standard.data(forKey: key).flatMap { try? JSONDecoder().decode(type, from: $0) }
    }

    private static func today() -> String {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from: Date())
    }

    private static func round1(_ value: Double) -> Double { (value * 10).rounded() / 10 }
}

// MARK: - 데이터

private typealias Event = [String: JSONValue]

private enum JSONValue: Codable, Equatable, CustomStringConvertible {
    case string(String)
    case number(Double)
    case bool(Bool)

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else { self = .string(try container.decode(String.self)) }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .bool(let value): try container.encode(value)
        }
    }

    var description: String {
        switch self {
        case .string(let value): value
        case .number(let value): "\(value)"
        case .bool(let value): "\(value)"
        }
    }
}

private struct RequestBody: Encodable {
    let key: String
    let events: [Event]
}

private struct QueuedEvent: Codable {
    let uid: String
    /// 같은 날 하루 요약을 하나로 합치기 위한 키 (그 외 이벤트는 nil).
    let key: String?
    let body: Event
}

private struct SettingsSnapshot: Codable, Equatable {
    let separate: Bool
    let interval: Int
    let sitting: Int
    let standing: Int
    let away: Int
}

/// 하루 동안 모은 숫자. 날짜가 바뀌면 전송하고 새로 시작한다.
private struct DayStats: Codable {
    var date: String
    var alerts = 0
    var acknowledged = 0
    var shrimpClicks = 0
    var tempuraClicks = 0
    var shrimpsAtClickSum = 0
    var maxShrimps = 0
    var reactionSecondsSum: Double = 0
    var maxReactionSeconds: Double = 0
    var satSeconds: Double = 0
    var stoodSeconds: Double = 0
    var pausedSeconds: Double = 0
    var pauses = 0
    var awayCount = 0
    var quickRestart = 0
    var quickSit = 0
    var quickStand = 0
    /// 시간(0~23)별 자세 바꾼 횟수.
    var ackHours: [Int: Int] = [:]

    init(date: String) { self.date = date }
}
