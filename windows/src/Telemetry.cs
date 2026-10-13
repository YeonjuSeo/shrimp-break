using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>
    /// 익명 사용 통계 (Mac 버전 Telemetry 와 같은 규칙). 사용자가 동의한 경우에만 구글 시트(Apps Script 웹 앱)로 보낸다.
    ///
    /// - 보내는 주소는 빌드할 때 exe 안에 들어간다 (telemetry/endpoint.txt). 없으면 기능 전체가 꺼진다.
    /// - 하루 요약 한 줄을 모아 두었다가 최대 시간당 한 번 덮어쓰고, 날짜가 바뀌면 전날 최종본을 보낸다.
    ///   보내지 못한 것은 대기열에 남겨 다음 기회에 다시 보낸다.
    /// - 이름·이메일·컴퓨터 이름은 보내지 않는다. id 는 동의할 때 만든 무작위 값이다.
    /// 저장 위치: %LOCALAPPDATA%\ShrimpBreak\ (동의 여부·id 는 레지스트리)
    /// </summary>
    sealed class Telemetry
    {
        public enum QuickAction { Restart, Sit, Stand }

        enum SegmentKind { Sat, Stood, Paused }

        const string SharedKey = "shrimp-v1";
        const int MaxQueuedEvents = 200;
        const int EventsPerRequest = 20;
        static readonly TimeSpan DailyUpsertInterval = TimeSpan.FromHours(1);

#if SHRIMP_DEV_TOOLS
        const string BuildKind = "dev";
#else
        const string BuildKind = "release";
#endif

        static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShrimpBreak");
        static readonly string DayPath = Path.Combine(Folder, "telemetry-day.txt");
        static readonly string QueuePath = Path.Combine(Folder, "telemetry-queue.txt");
        static readonly string SettingsPath = Path.Combine(Folder, "telemetry-settings.txt");
        static readonly string ErrorsPath = Path.Combine(Folder, "telemetry-errors.txt");

        readonly Uri endpoint;
        DayStats day;
        List<QueuedEvent> queue;
        SegmentKind? segment;
        DateTime segmentStartedAt;
        Phase? lastPhase;
        DateTime? alertStartedAt;
        DateTime? lastDailyUpsertAt;
        bool isSending;
        Action quitCompletion;

        public Telemetry()
        {
            endpoint = LoadEndpoint();
            day = DayStats.Load(DayPath) ?? new DayStats(Today());
            queue = QueuedEvent.LoadAll(QueuePath);
        }

        /// <summary>보낼 주소가 들어간 빌드인지 (메뉴에 토글을 보여줄지).</summary>
        public bool IsAvailable => endpoint != null;
        public bool IsEnabled => IsAvailable && Settings.TelemetryConsent == "granted";

        /// <summary>예상 못 한 오류 횟수를 센다 (Program.ReportCrash 에서 호출). 실패해도 무시.</summary>
        public static void RecordError()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(ErrorsPath, "1");
            }
            catch
            {
                // 오류 기록 중의 오류는 무시한다.
            }
        }

        // ── 시작 · 동의 ─────────────────────────────────────────

        public void Start()
        {
            if (!IsAvailable) return;
            switch (Settings.TelemetryConsent)
            {
                case "granted":
                    RollOverIfNeeded();
                    UpsertToday(force: true);
                    break;
                case "denied":
                    break;
                default:
                    // 트레이 아이콘이 먼저 자리 잡은 뒤에 묻는다.
                    var timer = new Timer { Interval = 1500 };
                    timer.Tick += (_, __) =>
                    {
                        timer.Dispose();
                        AskConsent();
                    };
                    timer.Start();
                    break;
            }
        }

        /// <summary>메뉴의 "익명 사용 통계 보내기" 토글.</summary>
        public void SetEnabled(bool isOn)
        {
            if (!IsAvailable || isOn == IsEnabled) return;
            if (isOn) Grant("menu", "turned_on");
            else Revoke();
        }

        void AskConsent()
        {
            const string message =
                "새우 브레이크 익명 통계를 보내도 될까요?\n\n" +
                "더 좋은 기본값을 정하는 데 참고하려고 해요.\n\n" +
                "• 보내는 것: 타이머 설정, 하루 동안 새우가 나온 횟수와 몇 마리에서 일어났는지, " +
                "앉아/서 있던 시간, OS·앱 버전 등\n" +
                "• 보내지 않는 것: 이름, 이메일, 컴퓨터 이름, 다른 앱 정보\n\n" +
                "선택 결과(보내기/안 보내기)는 한 번 기록돼요. " +
                "트레이 메뉴의 \"익명 사용 통계 보내기\"에서 언제든 바꿀 수 있어요.\n\n" +
                "[예] 보내기    [아니요] 안 보내기";
            DialogResult answer;
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen })
            {
                answer = MessageBox.Show(owner, message, "새우 브레이크", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            }
            if (answer == DialogResult.Yes)
            {
                Grant("first_run", "granted");
            }
            else
            {
                Settings.TelemetryConsent = "denied";
                // 고유 ID 없이 "안 보냄" 한 건만 보낸다. 이후로는 아무것도 보내지 않는다.
                Send(new List<Event> { AnonymousConsentEvent() }, _ => { });
            }
        }

        void Grant(string source, string choice)
        {
            Settings.TelemetryConsent = "granted";
            Settings.TelemetryId = Guid.NewGuid().ToString();
            day = new DayStats(Today());
            SettingsSnapshot.Current().Save(SettingsPath);
            Enqueue(BaseEvent("consent").With("choice", choice).With("source", source));
            Enqueue(BaseEvent("install").Merge(SettingsSnapshot.Current().Fields()));
            UpsertToday(force: true);
        }

        /// <summary>끄기: "끔"을 마지막으로 보내고, id·모아 둔 기록을 지운다.</summary>
        void Revoke()
        {
            Enqueue(BaseEvent("consent").With("choice", "turned_off").With("source", "menu"));
            var pending = queue.Select(item => item.Json).ToList();
            Settings.TelemetryConsent = "denied";
            Settings.TelemetryId = null;
            queue = new List<QueuedEvent>();
            segment = null;
            day = new DayStats(Today());
            foreach (var path in new[] { DayPath, QueuePath, SettingsPath, ErrorsPath })
            {
                try { File.Delete(path); } catch { /* 없으면 그만 */ }
            }
            SendRaw(pending, _ => { });
        }

        // ── 기록 ──────────────────────────────────────────────

        /// <summary>스케줄러 상태가 바뀔 때마다 호출. 알림·일시정지·자리 비움 횟수, 앉은/선/일시정지 시간, 설정 변경을 센다.</summary>
        public void Observe(BreakScheduler scheduler)
        {
            if (!IsEnabled) return;
            RollOverIfNeeded();

            var phase = scheduler.Phase;
            if (phase != lastPhase)
            {
                if (phase == Phase.Alerting)
                {
                    day.Alerts++;
                    alertStartedAt = DateTime.UtcNow;
                }
                else if (phase == Phase.Paused) day.Pauses++;
                else if (phase == Phase.Away) day.AwayCount++;
                lastPhase = phase;
            }

            // 알림 중에는 Posture 가 이미 다음 자세로 넘어가 있지만, 사용자는 아직 이전 자세다.
            var posture = phase == Phase.Alerting ? Toggled(scheduler.Posture) : scheduler.Posture;
            SegmentKind? kind = null;
            if (phase == Phase.Counting || phase == Phase.Alerting) kind = posture == Posture.Sitting ? SegmentKind.Sat : SegmentKind.Stood;
            else if (phase == Phase.Paused) kind = SegmentKind.Paused;
            if (kind != segment)
            {
                CloseSegment();
                segment = kind;
                segmentStartedAt = DateTime.UtcNow;
            }

            RecordSettingsIfChanged();
            Save();
        }

        public void RecordShrimpCount(int count)
        {
            if (!IsEnabled) return;
            RollOverIfNeeded();
            day.MaxShrimps = Math.Max(day.MaxShrimps, count);
            Save();
        }

        public void RecordAcknowledge(int shrimpCount, bool clickedTempura)
        {
            if (!IsEnabled) return;
            RollOverIfNeeded();
            day.Acknowledged++;
            day.ShrimpsAtClickSum += shrimpCount;
            if (clickedTempura) day.TempuraClicks++;
            else day.ShrimpClicks++;
            if (alertStartedAt.HasValue)
            {
                var seconds = (DateTime.UtcNow - alertStartedAt.Value).TotalSeconds;
                day.ReactionSecondsSum += seconds;
                day.MaxReactionSeconds = Math.Max(day.MaxReactionSeconds, seconds);
            }
            alertStartedAt = null;
            day.AckHours[DateTime.Now.Hour]++;
            Save();
            UpsertToday(force: false);
        }

        public void RecordQuickAction(QuickAction action)
        {
            if (!IsEnabled) return;
            RollOverIfNeeded();
            if (action == QuickAction.Restart) day.QuickRestart++;
            else if (action == QuickAction.Sit) day.QuickSit++;
            else day.QuickStand++;
            Save();
        }

        /// <summary>자리 비움 판정처럼 스케줄러를 거치지 않는 설정 변경 후에도 호출.</summary>
        public void RecordSettingsIfChanged()
        {
            if (!IsEnabled) return;
            var current = SettingsSnapshot.Current();
            var previous = SettingsSnapshot.Load(SettingsPath);
            if (previous == null)
            {
                current.Save(SettingsPath);
                return;
            }
            var changes = current.Changes(previous);
            if (changes.Count == 0) return;
            foreach (var (field, from, to) in changes)
            {
                Enqueue(BaseEvent("settings").With("hour", DateTime.Now.Hour).With("field", field)
                                             .With("from", from).With("to", to));
            }
            current.Save(SettingsPath);
        }

        /// <summary>종료 직전: 오늘 요약을 보내고 끝나면(또는 2초 뒤) completion.</summary>
        public void FlushBeforeQuit(Action completion)
        {
            if (!IsEnabled)
            {
                completion();
                return;
            }
            CloseSegment();
            Save();
            EnqueueToday();
            quitCompletion = completion;
            var timeout = new Timer { Interval = 2000 };
            timeout.Tick += (_, __) =>
            {
                timeout.Dispose();
                FinishQuit();
            };
            timeout.Start();
            Flush(FinishQuit);
        }

        void FinishQuit()
        {
            var completion = quitCompletion;
            quitCompletion = null;
            completion?.Invoke();
        }

        // ── 하루 요약 ─────────────────────────────────────────

        void RollOverIfNeeded()
        {
            var today = Today();
            if (day.Date == today) return;
            CloseSegment();
            Enqueue(DailyEvent(day, null, null, ReadAndResetErrors()));
            day = new DayStats(today);
            Save();
        }

        void UpsertToday(bool force)
        {
            if (!force && lastDailyUpsertAt.HasValue && DateTime.UtcNow - lastDailyUpsertAt.Value < DailyUpsertInterval) return;
            EnqueueToday();
            lastDailyUpsertAt = DateTime.UtcNow;
            Flush(() => { });
        }

        void EnqueueToday()
        {
            var openSeconds = segment.HasValue ? (DateTime.UtcNow - segmentStartedAt).TotalSeconds : 0;
            Enqueue(DailyEvent(day, segment, openSeconds, ReadErrors()));
        }

        void CloseSegment()
        {
            if (!segment.HasValue) return;
            var seconds = (DateTime.UtcNow - segmentStartedAt).TotalSeconds;
            switch (segment.Value)
            {
                case SegmentKind.Sat: day.SatSeconds += seconds; break;
                case SegmentKind.Stood: day.StoodSeconds += seconds; break;
                case SegmentKind.Paused: day.PausedSeconds += seconds; break;
            }
            segment = null;
        }

        Event DailyEvent(DayStats stats, SegmentKind? openKind, double? openSeconds, int errors)
        {
            double sat = stats.SatSeconds, stood = stats.StoodSeconds, paused = stats.PausedSeconds;
            if (openKind.HasValue && openSeconds.HasValue)
            {
                if (openKind == SegmentKind.Sat) sat += openSeconds.Value;
                else if (openKind == SegmentKind.Stood) stood += openSeconds.Value;
                else paused += openSeconds.Value;
            }
            double acknowledged = stats.Acknowledged;
            var e = BaseEvent("daily").Merge(SettingsSnapshot.Current().Fields())
                .With("date", stats.Date)
                .With("alerts", stats.Alerts)
                .With("acknowledged", stats.Acknowledged)
                .With("shrimp_clicks", stats.ShrimpClicks)
                .With("tempura_clicks", stats.TempuraClicks)
                .With("avg_shrimps_at_click", acknowledged > 0 ? Math.Round(stats.ShrimpsAtClickSum / acknowledged, 1) : 0)
                .With("max_shrimps", stats.MaxShrimps)
                .With("avg_reaction_sec", acknowledged > 0 ? Math.Round(stats.ReactionSecondsSum / acknowledged) : 0)
                .With("max_reaction_sec", Math.Round(stats.MaxReactionSeconds))
                .With("sat_minutes", Math.Round(sat / 60))
                .With("stood_minutes", Math.Round(stood / 60))
                .With("pauses", stats.Pauses)
                .With("paused_minutes", Math.Round(paused / 60))
                .With("away_count", stats.AwayCount)
                .With("quick_restart", stats.QuickRestart)
                .With("quick_sit", stats.QuickSit)
                .With("quick_stand", stats.QuickStand)
                .With("errors", errors);
            for (var hour = 0; hour < 24; hour++) e.With($"ack_h{hour:00}", stats.AckHours[hour]);
            return e;
        }

        static int ReadErrors()
        {
            try { return File.Exists(ErrorsPath) ? (int)new FileInfo(ErrorsPath).Length : 0; }
            catch { return 0; }
        }

        static int ReadAndResetErrors()
        {
            var count = ReadErrors();
            try { File.Delete(ErrorsPath); } catch { /* 다음에 다시 */ }
            return count;
        }

        // ── 공통 필드 ─────────────────────────────────────────

        Event BaseEvent(string type)
        {
            var screens = Screen.AllScreens;
            var maxScale = screens.Select(s => NativeMethods.MonitorMetrics(s.Bounds).scale).DefaultIfEmpty(1).Max();
            return new Event()
                .With("type", type)
                .With("id", Settings.TelemetryId ?? "")
                .With("date", Today())
                .With("os", "windows")
                .With("os_version", Environment.OSVersion.Version.ToString())
                .With("app_version", Application.ProductVersion)
                .With("build", BuildKind)
                .With("locale", CultureInfo.CurrentUICulture.Name)
                .With("monitors", screens.Length)
                .With("max_scale", Math.Round(maxScale, 2));
        }

        /// <summary>동의하지 않은 사람용: 매번 새로 만든 일회용 id 로, 선택 결과만.</summary>
        Event AnonymousConsentEvent()
        {
            var e = BaseEvent("consent").With("id", "anon-" + Guid.NewGuid()).With("choice", "denied").With("source", "first_run");
            e.Remove("monitors");
            e.Remove("max_scale");
            return e;
        }

        static Posture Toggled(Posture posture) => posture == Posture.Sitting ? Posture.Standing : Posture.Sitting;

        static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // ── 전송 ──────────────────────────────────────────────

        void Enqueue(Event e)
        {
            // 같은 날의 하루 요약은 최신 것 하나만 남긴다.
            string key = e.Get("type") == "daily" ? "daily|" + e.Get("date") : "";
            if (key.Length > 0) queue.RemoveAll(item => item.Key == key);
            queue.Add(new QueuedEvent(Guid.NewGuid().ToString("N"), key, e.ToJson()));
            if (queue.Count > MaxQueuedEvents) queue.RemoveRange(0, queue.Count - MaxQueuedEvents);
            Save();
            Flush(() => { });
        }

        void Flush(Action completion)
        {
            if (isSending || queue.Count == 0)
            {
                completion();
                return;
            }
            var batch = queue.Take(EventsPerRequest).ToList();
            isSending = true;
            SendRaw(batch.Select(item => item.Json).ToList(), isSent =>
            {
                isSending = false;
                if (isSent)
                {
                    var sent = new HashSet<string>(batch.Select(item => item.Uid));
                    queue.RemoveAll(item => sent.Contains(item.Uid));
                    Save();
                    if (queue.Count > 0)
                    {
                        Flush(completion);
                        return;
                    }
                }
                completion();
            });
        }

        void Send(List<Event> events, Action<bool> completion) => SendRaw(events.Select(e => e.ToJson()).ToList(), completion);

        /// <summary>백그라운드에서 POST 하고, 결과는 UI 스레드로 돌려준다. 오프라인이면 false.</summary>
        void SendRaw(List<string> eventsJson, Action<bool> completion)
        {
            if (endpoint == null || eventsJson.Count == 0)
            {
                completion(false);
                return;
            }
            var body = "{\"key\":" + Json.Quote(SharedKey) + ",\"events\":[" + string.Join(",", eventsJson) + "]}";
            var url = endpoint;
            Task.Run(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "POST";
                    request.ContentType = "text/plain; charset=utf-8";
                    request.Timeout = 15000;
                    request.AllowAutoRedirect = true; // Apps Script 는 결과를 다른 주소로 넘겨서 돌려준다
                    var bytes = Encoding.UTF8.GetBytes(body);
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    using (var response = (HttpWebResponse)request.GetResponse())
                        return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
                }
                catch
                {
                    return false; // 오프라인 등 → 대기열에 남겨 두고 다음에 다시
                }
            }).ContinueWith(task => Ui.Post(() => completion(task.Result)));
        }

        // ── 저장 ──────────────────────────────────────────────

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                day.Save(DayPath);
                QueuedEvent.SaveAll(QueuePath, queue);
            }
            catch
            {
                // 디스크 문제로 통계를 못 남겨도 앱 동작에는 영향 없음.
            }
        }

        static Uri LoadEndpoint()
        {
            using (var stream = typeof(Telemetry).Assembly.GetManifestResourceStream("endpoint.txt"))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream))
                {
                    var text = reader.ReadToEnd().Trim();
                    return text.StartsWith("https://script.google.com/", StringComparison.Ordinal) ? new Uri(text) : null;
                }
            }
        }

        // ── 데이터 ────────────────────────────────────────────

        /// <summary>이벤트 한 건 (평평한 키-값). 값은 문자열·숫자·참거짓만.</summary>
        sealed class Event
        {
            readonly Dictionary<string, object> values = new Dictionary<string, object>();

            public Event With(string key, object value)
            {
                values[key] = value;
                return this;
            }

            public Event Merge(Dictionary<string, object> other)
            {
                foreach (var pair in other) values[pair.Key] = pair.Value;
                return this;
            }

            public void Remove(string key) => values.Remove(key);

            public string Get(string key) => values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";

            public string ToJson() => "{" + string.Join(",", values.Select(pair => Json.Quote(pair.Key) + ":" + Json.Value(pair.Value))) + "}";
        }

        sealed class QueuedEvent
        {
            public QueuedEvent(string uid, string key, string json)
            {
                Uid = uid;
                Key = key;
                Json = json;
            }

            public string Uid { get; }
            /// <summary>같은 날 하루 요약을 하나로 합치기 위한 키 (그 외 이벤트는 빈 값).</summary>
            public string Key { get; }
            public string Json { get; }

            // 한 줄 = uid \t key \t json (json 은 한 줄로 직렬화되어 탭·줄바꿈이 이스케이프돼 있다)
            public static List<QueuedEvent> LoadAll(string path)
            {
                try
                {
                    if (!File.Exists(path)) return new List<QueuedEvent>();
                    return File.ReadAllLines(path, Encoding.UTF8)
                        .Select(line => line.Split(new[] { '\t' }, 3))
                        .Where(parts => parts.Length == 3)
                        .Select(parts => new QueuedEvent(parts[0], parts[1], parts[2]))
                        .ToList();
                }
                catch
                {
                    return new List<QueuedEvent>();
                }
            }

            public static void SaveAll(string path, List<QueuedEvent> queue)
                => File.WriteAllLines(path, queue.Select(item => item.Uid + "\t" + item.Key + "\t" + item.Json), Encoding.UTF8);
        }

        sealed class SettingsSnapshot
        {
            public bool Separate;
            public int Interval, Sitting, Standing, Away;

            public static SettingsSnapshot Current() => new SettingsSnapshot
            {
                Separate = Settings.UsesSeparatePostureTimers,
                Interval = Settings.IntervalMinutes,
                Sitting = Settings.SittingMinutes,
                Standing = Settings.StandingMinutes,
                Away = Settings.AwayThresholdMinutes,
            };

            public Dictionary<string, object> Fields() => new Dictionary<string, object>
            {
                ["separate_timers"] = Separate,
                ["interval_minutes"] = Interval,
                ["sitting_minutes"] = Sitting,
                ["standing_minutes"] = Standing,
                ["away_threshold"] = Away,
            };

            public List<(string field, string from, string to)> Changes(SettingsSnapshot previous)
            {
                var result = new List<(string, string, string)>();
                void Add(string field, object from, object to)
                {
                    var a = Convert.ToString(from, CultureInfo.InvariantCulture).ToLowerInvariant();
                    var b = Convert.ToString(to, CultureInfo.InvariantCulture).ToLowerInvariant();
                    if (a != b) result.Add((field, a, b));
                }
                Add("separate_timers", previous.Separate, Separate);
                Add("interval_minutes", previous.Interval, Interval);
                Add("sitting_minutes", previous.Sitting, Sitting);
                Add("standing_minutes", previous.Standing, Standing);
                Add("away_threshold", previous.Away, Away);
                return result;
            }

            public void Save(string path)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(path, $"{Separate}|{Interval}|{Sitting}|{Standing}|{Away}");
                }
                catch
                {
                    // 무시
                }
            }

            public static SettingsSnapshot Load(string path)
            {
                try
                {
                    var parts = File.ReadAllText(path).Split('|');
                    return new SettingsSnapshot
                    {
                        Separate = bool.Parse(parts[0]),
                        Interval = int.Parse(parts[1], CultureInfo.InvariantCulture),
                        Sitting = int.Parse(parts[2], CultureInfo.InvariantCulture),
                        Standing = int.Parse(parts[3], CultureInfo.InvariantCulture),
                        Away = int.Parse(parts[4], CultureInfo.InvariantCulture),
                    };
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>하루 동안 모은 숫자. 날짜가 바뀌면 전송하고 새로 시작한다.</summary>
        sealed class DayStats
        {
            public DayStats(string date) => Date = date;

            public string Date;
            public int Alerts, Acknowledged, ShrimpClicks, TempuraClicks, ShrimpsAtClickSum, MaxShrimps;
            public double ReactionSecondsSum, MaxReactionSeconds, SatSeconds, StoodSeconds, PausedSeconds;
            public int Pauses, AwayCount, QuickRestart, QuickSit, QuickStand;
            /// <summary>시간(0~23)별 자세 바꾼 횟수.</summary>
            public int[] AckHours = new int[24];

            // 한 줄 = 이름=값
            public void Save(string path)
            {
                var c = CultureInfo.InvariantCulture;
                File.WriteAllLines(path, new[]
                {
                    "Date=" + Date,
                    "Alerts=" + Alerts, "Acknowledged=" + Acknowledged, "ShrimpClicks=" + ShrimpClicks,
                    "TempuraClicks=" + TempuraClicks, "ShrimpsAtClickSum=" + ShrimpsAtClickSum, "MaxShrimps=" + MaxShrimps,
                    "ReactionSecondsSum=" + ReactionSecondsSum.ToString(c), "MaxReactionSeconds=" + MaxReactionSeconds.ToString(c),
                    "SatSeconds=" + SatSeconds.ToString(c), "StoodSeconds=" + StoodSeconds.ToString(c),
                    "PausedSeconds=" + PausedSeconds.ToString(c),
                    "Pauses=" + Pauses, "AwayCount=" + AwayCount,
                    "QuickRestart=" + QuickRestart, "QuickSit=" + QuickSit, "QuickStand=" + QuickStand,
                    "AckHours=" + string.Join(",", AckHours),
                });
            }

            public static DayStats Load(string path)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    var map = File.ReadAllLines(path)
                        .Select(line => line.Split(new[] { '=' }, 2))
                        .Where(parts => parts.Length == 2)
                        .ToDictionary(parts => parts[0], parts => parts[1]);
                    var c = CultureInfo.InvariantCulture;
                    int I(string key) => map.TryGetValue(key, out var v) ? int.Parse(v, c) : 0;
                    double D(string key) => map.TryGetValue(key, out var v) ? double.Parse(v, c) : 0;
                    var stats = new DayStats(map["Date"])
                    {
                        Alerts = I("Alerts"), Acknowledged = I("Acknowledged"), ShrimpClicks = I("ShrimpClicks"),
                        TempuraClicks = I("TempuraClicks"), ShrimpsAtClickSum = I("ShrimpsAtClickSum"), MaxShrimps = I("MaxShrimps"),
                        ReactionSecondsSum = D("ReactionSecondsSum"), MaxReactionSeconds = D("MaxReactionSeconds"),
                        SatSeconds = D("SatSeconds"), StoodSeconds = D("StoodSeconds"), PausedSeconds = D("PausedSeconds"),
                        Pauses = I("Pauses"), AwayCount = I("AwayCount"),
                        QuickRestart = I("QuickRestart"), QuickSit = I("QuickSit"), QuickStand = I("QuickStand"),
                    };
                    if (map.TryGetValue("AckHours", out var hours))
                    {
                        var parts = hours.Split(',');
                        for (var h = 0; h < 24 && h < parts.Length; h++) stats.AckHours[h] = int.Parse(parts[h], c);
                    }
                    return stats;
                }
                catch
                {
                    return null; // 파일이 깨졌으면 오늘부터 새로
                }
            }
        }

        /// <summary>평평한 JSON 만 쓰는 작은 직렬화 도우미 (.NET Framework 기본 라이브러리에 JSON 이 없어서).</summary>
        static class Json
        {
            public static string Value(object value)
            {
                switch (value)
                {
                    case null: return "null";
                    case bool b: return b ? "true" : "false";
                    case int i: return i.ToString(CultureInfo.InvariantCulture);
                    case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                    default: return Quote(Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }

            public static string Quote(string text)
            {
                var builder = new StringBuilder("\"");
                foreach (var ch in text)
                {
                    switch (ch)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (ch < 0x20) builder.Append("\\u").Append(((int)ch).ToString("x4"));
                            else builder.Append(ch);
                            break;
                    }
                }
                return builder.Append('"').ToString();
            }
        }
    }
}
