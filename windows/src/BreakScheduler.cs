using System;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>루틴. 타이머가 울릴 때마다 다음 루틴으로 넘어간다 (앉기 → 서기 → 움직이기 → 앉기 …, 0분인 루틴은 건너뜀).</summary>
    public enum Routine { Sitting, Standing, Moving }

    static class Routines
    {
        public static readonly Routine[] All = { Routine.Sitting, Routine.Standing, Routine.Moving };

        public static bool IsOn(this Routine routine) => Settings.MinutesFor(routine) > 0;

        /// <summary>시간이 0이 아닌 다음 루틴. 켜진 루틴이 자기 하나뿐이면 자기 자신.</summary>
        public static Routine Next(this Routine routine)
        {
            var candidate = routine;
            do candidate = (Routine)(((int)candidate + 1) % All.Length);
            while (!candidate.IsOn() && candidate != routine);
            return candidate;
        }

        /// <summary>처음(또는 자리에 돌아왔을 때) 시작할 루틴: 앉기부터, 앉기가 꺼져 있으면 그다음 켜진 루틴.</summary>
        public static Routine First => Routine.Sitting.IsOn() ? Routine.Sitting : Routine.Sitting.Next();

        public static string Label(this Routine routine)
        {
            switch (routine)
            {
                case Routine.Sitting: return "앉기";
                case Routine.Standing: return "서기";
                default: return "움직이기";
            }
        }
    }

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

        readonly Timer deadlineTimer = new Timer();
        readonly Timer escalationTimer = new Timer();
        readonly Timer awayGraceTimer = new Timer();
        DateTime? deadline;
        /// <summary>일시정지 시점에 남아 있던 시간. null 이면 재개 시 간격 전체로 새로 시작.</summary>
        TimeSpan? pausedRemaining;

        public BreakScheduler()
        {
            deadlineTimer.Tick += (_, __) => Fire();
            escalationTimer.Tick += (_, __) =>
            {
                if (!awayDuringAwayTurnSince.HasValue) Spawn?.Invoke(); // 자리에 없을 땐 쌓지 않는다
            };
            awayGraceTimer.Tick += (_, __) => HandleAwayGraceExpired();
        }

        public bool IsUserPaused { get; private set; }
        public bool IsAway { get; private set; }
        /// <summary>
        /// 서기·움직이기 차례 중에 자리를 비운 시각 (무시하고 타이머는 계속). 이 동안은 새우를 더 늘리지 않는다.
        /// 상한(Settings.AwayGrace)보다 오래 비우면 진짜 자리 비움으로 넘긴다 (퇴근 등).
        /// </summary>
        DateTime? awayDuringAwayTurnSince;
        TimeSpan awayGrace;

        public bool IsAlerting { get; private set; }
        /// <summary>다음에 할(카운트다운 중이면 지금 하는) 루틴. 알림 중에는 이미 다음 루틴으로 넘어가 있다.</summary>
        public Routine Routine { get; private set; } = Routines.First;
        /// <summary>알림이 울리기 직전까지 하던 루틴.</summary>
        Routine previousRoutine = Routine.Sitting;

        /// <summary>사용자가 지금 하고 있는 루틴. 알림 중에는 새우를 클릭하기 전이므로 아직 이전 루틴이다.</summary>
        public Routine Activity => IsAlerting ? previousRoutine : Routine;

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

        /// <summary>이번 차례(지금 루틴)의 길이.</summary>
        TimeSpan Interval => TimeSpan.FromMinutes(Settings.MinutesFor(Routine));
        bool IsActive => !IsUserPaused && !IsAway;

        public void Start() => RestartCycle();

        /// <summary>새우를 클릭함 = 자세를 바꿨다. 새우를 치우고 다음 주기를 시작한다.</summary>
        public void Acknowledge() => RestartCycle();

        /// <summary>
        /// 메뉴에서 직접 차례를 시작한다. target 이 null 이면 지금 루틴 차례를 처음부터 다시 센다.
        /// 일시정지 중이었다면 풀고 바로 시작한다. 새우가 나와 있었다면 치운다. 꺼진(0분) 루틴은 시작하지 않는다.
        /// </summary>
        public void StartTurn(Routine? target)
        {
            if (target.HasValue)
            {
                if (!target.Value.IsOn()) return;
                Routine = target.Value;
            }
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

        /// <summary>
        /// 루틴 하나의 시간. 0 이면 그 루틴은 건너뛴다.
        /// 범위를 벗어나거나 켜진 마지막 루틴을 0으로 만들려 하면 바꾸지 않고 false.
        /// </summary>
        public bool SetMinutes(Routine target, int minutes)
        {
            if (minutes < Settings.MinRoutineMinutes || minutes > Settings.MaxRoutineMinutes) return false;
            var othersOn = false;
            foreach (var routine in Routines.All) othersOn |= routine != target && routine.IsOn();
            if (minutes == 0 && !othersOn) return false;
            ApplyTimerChange(() => Settings.SetMinutes(target, minutes));
            return true;
        }

        /// <summary>프리셋(10분·20분…): 켜진 루틴을 모두 같은 시간으로. 꺼진(0분) 루틴은 그대로 둔다.</summary>
        public void ApplyPreset(int minutes) => ApplyTimerChange(() =>
        {
            foreach (var routine in Routines.All)
                if (routine.IsOn()) Settings.SetMinutes(routine, minutes);
        });

        /// <summary>새우 증식 간격(초). 새우가 나와 있는 중이면 지금부터 새 간격으로 늘린다.</summary>
        public void SetEscalationSeconds(int seconds)
        {
            Settings.EscalationSeconds = seconds;
            if (IsAlerting) StartEscalation();
            Changed?.Invoke();
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
        /// 단, 서기·움직이기 차례에는 자리를 뜨는 게 정상이므로 무시하고 타이머를 계속 돌린다.
        /// (그렇지 않으면 서 있는 동안 입력이 없어 자리 비움 → 돌아오는 순간 새우 없이 앉는 차례로 넘어가 버린다.)
        /// 그래도 그 차례 길이의 2배(최소 10분)보다 오래 비우면 퇴근 등으로 보고 진짜 자리 비움으로 넘긴다.
        /// </summary>
        public void SetAway(bool away)
        {
            if (away)
            {
                if (IsAway) return;
                if (!IsUserPaused && Activity != Routine.Sitting)
                {
                    StartAwayGrace();
                    return;
                }
                EnterAway();
                return;
            }
            if (awayDuringAwayTurnSince.HasValue)
            {
                var since = awayDuringAwayTurnSince.Value;
                ClearAwayDuringAwayTurn();
                // 절전 중에는 상한 타이머가 돌지 못하므로, 돌아온 시점에 한 번 더 확인한다.
                if (DateTime.UtcNow - since < awayGrace) return;
                EnterAway();
            }
            if (!IsAway) return;
            ReturnFromAway();
        }

        public void Dispose()
        {
            deadlineTimer.Dispose();
            escalationTimer.Dispose();
            awayGraceTimer.Dispose();
        }

        /// <summary>서기·움직이기 차례에 자리를 비웠다 → 바로 멈추지 않고 상한까지 기다린다.</summary>
        void StartAwayGrace()
        {
            if (awayDuringAwayTurnSince.HasValue) return;
            awayDuringAwayTurnSince = DateTime.UtcNow;
            awayGrace = Settings.AwayGrace(Activity);
            awayGraceTimer.Stop();
            awayGraceTimer.Interval = (int)awayGrace.TotalMilliseconds;
            awayGraceTimer.Start();
        }

        /// <summary>서기·움직이기 차례의 자리 비움이 상한을 넘었다 → 진짜 자리 비움으로 넘긴다 (새우 정리, 타이머 정지).</summary>
        void HandleAwayGraceExpired()
        {
            if (!awayDuringAwayTurnSince.HasValue) return;
            ClearAwayDuringAwayTurn();
            EnterAway();
        }

        void ClearAwayDuringAwayTurn()
        {
            awayDuringAwayTurnSince = null;
            awayGraceTimer.Stop();
        }

        void EnterAway()
        {
            IsAway = true;
            StopAll();
            Changed?.Invoke();
        }

        /// <summary>자리에 다시 앉은 것으로 보고 첫 루틴부터 다시 센다.</summary>
        void ReturnFromAway()
        {
            IsAway = false;
            Routine = Routines.First;
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

        /// <summary>
        /// 타이머 설정을 바꾼다. 이번 차례의 길이가 바뀌었을 때만 카운트다운을 새로 시작하고,
        /// 다음 차례에만 영향을 주는 변경(예: 앉아 있는 중에 서 있는 시간 변경)은 진행 중인 카운트다운을 유지한다.
        /// 지금(알림 중이면 다음) 루틴이 0분으로 꺼졌으면 그다음 켜진 루틴으로 넘어간다.
        /// </summary>
        void ApplyTimerChange(Action change)
        {
            var routineBefore = Routine;
            var intervalBefore = Interval;
            change();
            if (!Routine.IsOn()) Routine = Routine.Next();
            // 알림 중이면 다음 차례는 새우를 클릭할 때 새 값으로 시작한다 (설정 변경이 클릭을 대신하지 않도록).
            if ((Routine == routineBefore && Interval == intervalBefore) || IsAlerting)
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
            previousRoutine = Routine;
            Routine = Routine.Next();
            Spawn?.Invoke();

            StartEscalation();
            Changed?.Invoke();
        }

        /// <summary>클릭할 때까지 설정한 간격(3~60초)마다 한 마리씩 늘린다.</summary>
        void StartEscalation()
        {
            escalationTimer.Stop();
            escalationTimer.Interval = Settings.EscalationSeconds * 1000;
            escalationTimer.Start();
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
