using System;
using System.Windows.Forms;

namespace ShrimpBreak
{
    public enum Posture { Sitting, Standing }

    public enum Phase { Counting, Alerting, Paused, Away }

    /// <summary>
    /// 카운트다운·알림·에스컬레이션 상태 머신 (Mac 버전 BreakScheduler 와 동일한 규칙).
    ///
    /// 성능 메모: 초 단위로 tick 하지 않는다. "마감 시각(deadline)" 하나만 잡고
    /// 그 시각에 한 번 깨어나는 타이머를 건다. 남은 시간은 필요할 때 deadline - now 로 계산한다.
    /// </summary>
    sealed class BreakScheduler : IDisposable
    {
        /// <summary>새우 한 마리를 내보내야 할 때 (알림 시작 + 이후 1분마다).</summary>
        public event Action Spawn;
        /// <summary>화면의 새우를 모두 치워야 할 때.</summary>
        public event Action Clear;
        /// <summary>상태가 바뀌었을 때 (트레이 아이콘 갱신용).</summary>
        public event Action Changed;

        static readonly TimeSpan EscalationInterval = TimeSpan.FromMinutes(1);

        readonly Timer deadlineTimer = new Timer();
        readonly Timer escalationTimer = new Timer { Interval = (int)EscalationInterval.TotalMilliseconds };
        DateTime? deadline;
        /// <summary>일시정지 시점에 남아 있던 시간. null 이면 재개 시 간격 전체로 새로 시작.</summary>
        TimeSpan? pausedRemaining;

        public BreakScheduler()
        {
            deadlineTimer.Tick += (_, __) => Fire();
            escalationTimer.Tick += (_, __) => Spawn?.Invoke();
        }

        public bool IsUserPaused { get; private set; }
        public bool IsAway { get; private set; }
        public bool IsAlerting { get; private set; }
        /// <summary>이번 차례에 사용자가 취할 자세. 타이머가 울릴 때마다 교대된다 (앉음 → 섬 → 앉음 …).</summary>
        public Posture Posture { get; private set; } = Posture.Sitting;

        public Phase Phase
        {
            get
            {
                if (IsUserPaused) return Phase.Paused;
                if (IsAway) return Phase.Away;
                if (IsAlerting) return Phase.Alerting;
                return deadline.HasValue ? Phase.Counting : Phase.Paused;
            }
        }

        public TimeSpan Remaining => deadline.HasValue ? Max(TimeSpan.Zero, deadline.Value - DateTime.UtcNow) : TimeSpan.Zero;

        /// <summary>이번 차례(지금 자세)의 길이.</summary>
        TimeSpan Interval => TimeSpan.FromMinutes(Settings.MinutesFor(Posture));
        bool IsActive => !IsUserPaused && !IsAway;

        public void Start() => RestartCycle();

        /// <summary>새우를 클릭함 = 자세를 바꿨다. 새우를 치우고 다음 주기를 시작한다.</summary>
        public void Acknowledge() => RestartCycle();

        /// <summary>
        /// 메뉴에서 직접 차례를 시작한다. target 이 null 이면 지금 자세 차례를 처음부터 다시 센다.
        /// 일시정지 중이었다면 풀고 바로 시작한다. 새우가 나와 있었다면 치운다.
        /// </summary>
        public void StartTurn(Posture? target)
        {
            if (target.HasValue) Posture = target.Value;
            IsUserPaused = false;
            pausedRemaining = null;
            RestartCycle();
        }

#if SHRIMP_DEV_TOOLS
        /// <summary>테스트용: 지금 바로 알림.</summary>
        public void TriggerNow()
        {
            if (IsActive) Fire();
        }
#endif

        /// <summary>타이머 한 개 모드의 간격.</summary>
        public void SetInterval(int minutes) => ApplyTimerChange(() => Settings.IntervalMinutes = minutes);

        /// <summary>타이머 두 개 모드의 자세별 시간.</summary>
        public void SetMinutes(int minutes, Posture target) => ApplyTimerChange(() =>
        {
            if (target == Posture.Sitting) Settings.SittingMinutes = minutes;
            else Settings.StandingMinutes = minutes;
        });

        /// <summary>
        /// 타이머 한 개 ↔ 두 개 전환.
        /// 한 개 → 두 개: 앉음·섬 모두 지금 간격으로 채운다. 두 개 → 한 개: 서 있는 시간을 간격으로 쓴다.
        /// </summary>
        public void SetSeparatePostureTimers(bool isOn)
        {
            if (isOn == Settings.UsesSeparatePostureTimers) return;
            ApplyTimerChange(() =>
            {
                if (isOn)
                {
                    Settings.SittingMinutes = Settings.IntervalMinutes;
                    Settings.StandingMinutes = Settings.IntervalMinutes;
                }
                else
                {
                    Settings.IntervalMinutes = Settings.StandingMinutes;
                }
                Settings.UsesSeparatePostureTimers = isOn;
            });
        }

        public void SetPaused(bool paused)
        {
            if (paused == IsUserPaused) return;
            if (paused)
            {
                pausedRemaining = IsAlerting ? (TimeSpan?)null : (deadline.HasValue ? Remaining : (TimeSpan?)null);
                IsUserPaused = true;
                StopAll();
            }
            else
            {
                IsUserPaused = false;
                if (IsActive) ScheduleDeadline(pausedRemaining ?? Interval);
                pausedRemaining = null;
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// 화면 잠금·절전·장시간 무입력 = 자리를 비웠다(= 이미 일어났다).
        /// 돌아오면 자리에 다시 앉은 것으로 보고 주기를 처음부터 다시 센다.
        /// </summary>
        public void SetAway(bool away)
        {
            if (away == IsAway) return;
            IsAway = away;
            if (!away) Posture = Posture.Sitting;
            if (away)
            {
                StopAll();
            }
            else if (IsUserPaused)
            {
                pausedRemaining = null;
            }
            else
            {
                RestartCycle();
                return;
            }
            Changed?.Invoke();
        }

        public void Dispose()
        {
            deadlineTimer.Dispose();
            escalationTimer.Dispose();
        }

        /// <summary>
        /// 타이머 설정을 바꾼다. 이번 차례의 길이가 바뀌었을 때만 카운트다운을 새로 시작하고,
        /// 다음 차례에만 영향을 주는 변경(예: 앉아 있는 중에 서 있는 시간 변경)은 진행 중인 카운트다운을 유지한다.
        /// </summary>
        void ApplyTimerChange(Action change)
        {
            var before = Interval;
            change();
            // 알림 중이면 다음 차례는 새우를 클릭할 때 새 값으로 시작한다 (설정 변경이 클릭을 대신하지 않도록).
            if (Interval == before || IsAlerting)
            {
                Changed?.Invoke();
                return;
            }
            if (IsUserPaused)
            {
                pausedRemaining = null;
                Changed?.Invoke();
            }
            else
            {
                RestartCycle();
            }
        }

        void RestartCycle()
        {
            StopAll();
            if (IsActive) ScheduleDeadline(Interval);
            Changed?.Invoke();
        }

        void ScheduleDeadline(TimeSpan after)
        {
            deadline = DateTime.UtcNow + after;
            deadlineTimer.Stop();
            deadlineTimer.Interval = Math.Max(1, (int)Math.Min(int.MaxValue, after.TotalMilliseconds));
            deadlineTimer.Start();
            Changed?.Invoke();
        }

        void Fire()
        {
            deadlineTimer.Stop();
            deadline = null;
            IsAlerting = true;
            Posture = Posture == Posture.Sitting ? Posture.Standing : Posture.Sitting;
            Spawn?.Invoke();

            escalationTimer.Stop();
            escalationTimer.Start();
            Changed?.Invoke();
        }

        void StopAll()
        {
            deadlineTimer.Stop();
            escalationTimer.Stop();
            deadline = null;
            if (IsAlerting)
            {
                IsAlerting = false;
                Clear?.Invoke();
            }
        }

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}
