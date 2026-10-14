using System;
using Microsoft.Win32;

namespace ShrimpBreak
{
    /// <summary>
    /// 사용자 설정. HKCU\Software\ShrimpBreak 레지스트리에 저장한다 (관리자 권한 불필요).
    /// </summary>
    static class Settings
    {
        const string KeyPath = @"Software\ShrimpBreak";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "ShrimpBreak";

        public static readonly int[] PresetIntervals = { 10, 20, 30, 60 };
        public static readonly int[] AwayPresets = { 3, 5, 10 };

        /// <summary>루틴 시간으로 입력할 수 있는 범위(분). 0 = 그 루틴은 건너뛴다.</summary>
        public const int MinRoutineMinutes = 0;
        public const int MaxRoutineMinutes = 480;

        /// <summary>앉아 있는 시간(분). 기본 30분.</summary>
        public static int SittingMinutes
        {
            get => ReadMinutes("SittingMinutes", 30);
            set => Write("SittingMinutes", value);
        }

        /// <summary>서 있는 시간(분). 기본 30분.</summary>
        public static int StandingMinutes
        {
            get => ReadMinutes("StandingMinutes", 30);
            set => Write("StandingMinutes", value);
        }

        /// <summary>움직이는 시간(분). 기본 0분(건너뜀).</summary>
        public static int MovingMinutes
        {
            get => ReadMinutes("MovingMinutes", 0);
            set => Write("MovingMinutes", value);
        }

        /// <summary>루틴 차례의 길이(분). 0 이면 그 루틴은 건너뛴다.</summary>
        public static int MinutesFor(Routine routine)
        {
            switch (routine)
            {
                case Routine.Sitting: return SittingMinutes;
                case Routine.Standing: return StandingMinutes;
                default: return MovingMinutes;
            }
        }

        public static void SetMinutes(Routine routine, int minutes)
        {
            switch (routine)
            {
                case Routine.Sitting: SittingMinutes = minutes; break;
                case Routine.Standing: StandingMinutes = minutes; break;
                default: MovingMinutes = minutes; break;
            }
        }

        /// <summary>
        /// 예전 설정(타이머 한 개 / 앉기·서기 따로)을 앉기·서기·움직이기 시간으로 옮긴다. 처음 한 번만.
        /// 타이머 한 개였으면 그 간격을 앉기·서기 모두에, 따로였으면 각자 값을 그대로. 움직이기는 0(건너뜀).
        /// </summary>
        public static void MigrateIfNeeded()
        {
            if (HasValue("MovingMinutes")) return;
            var interval = HasValue("IntervalMinutes") ? Math.Max(1, ReadInt("IntervalMinutes", 30)) : 30;
            if (ReadInt("UsesSeparatePostureTimers", 0) != 1)
            {
                SittingMinutes = interval;
                StandingMinutes = interval;
            }
            else
            {
                SittingMinutes = HasValue("SittingMinutes") ? Math.Max(1, ReadInt("SittingMinutes", interval)) : interval;
                StandingMinutes = HasValue("StandingMinutes") ? Math.Max(1, ReadInt("StandingMinutes", interval)) : interval;
            }
            MovingMinutes = 0;
            Delete("IntervalMinutes");
            Delete("UsesSeparatePostureTimers");
        }

        /// <summary>새우를 클릭하지 않으면 몇 초마다 한 마리씩 늘어나는지. 3 ~ 60초, 기본 60초.</summary>
        public const int MinEscalationSeconds = 3;
        public const int MaxEscalationSeconds = 60;

        public static int EscalationSeconds
        {
            get
            {
                var value = ReadInt("EscalationSeconds", MaxEscalationSeconds);
                return Math.Min(MaxEscalationSeconds, Math.Max(MinEscalationSeconds, value));
            }
            set => Write("EscalationSeconds", value);
        }

        /// <summary>이 시간(분) 이상 키보드·마우스 입력이 없으면 "자리를 비웠다(=일어났다)"로 본다. 0 이면 끔.</summary>
        public static int AwayThresholdMinutes
        {
            get => ReadInt("AwayThresholdMinutes", 5);
            set => Write("AwayThresholdMinutes", value);
        }

        /// <summary>익명 통계 동의 여부: "granted" / "denied" / null(아직 안 물어봄).</summary>
        public static string TelemetryConsent
        {
            get => ReadString("TelemetryConsent");
            set => WriteString("TelemetryConsent", value);
        }

        /// <summary>익명 통계용 무작위 id. 동의할 때 만들고, 끄면 지운다.</summary>
        public static string TelemetryId
        {
            get => ReadString("TelemetryId");
            set => WriteString("TelemetryId", value);
        }

        /// <summary>Windows 로그인 시 자동 실행 (HKCU Run 키).</summary>
        public static bool LaunchAtLogin
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return key?.GetValue(RunValueName) is string;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (value) key.SetValue(RunValueName, $"\"{System.Windows.Forms.Application.ExecutablePath}\"");
                    else key.DeleteValue(RunValueName, false);
                }
            }
        }

        /// <summary>저장된 값이 없을 때만 fallback (0 은 "건너뜀"이라는 저장된 값이다).</summary>
        static int ReadMinutes(string name, int fallback)
        {
            if (!HasValue(name)) return fallback;
            return Math.Min(MaxRoutineMinutes, Math.Max(MinRoutineMinutes, ReadInt(name, fallback)));
        }

        static bool HasValue(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key?.GetValue(name) != null;
        }

        static void Delete(string name)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.DeleteValue(name, false);
        }

        static int ReadInt(string name, int fallback)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key?.GetValue(name) is int value ? value : fallback;
        }

        static string ReadString(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key?.GetValue(name) as string;
        }

        static void WriteString(string name, string value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (value == null) key.DeleteValue(name, false);
                else key.SetValue(name, value, RegistryValueKind.String);
            }
        }

        static void Write(string name, int value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, value, RegistryValueKind.DWord);
        }
    }
}
