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

        /// <summary>알림 간격(분). 타이머 한 개 모드에서 앉음·섬 모두에 쓴다. 기본 30분.</summary>
        public static int IntervalMinutes
        {
            get => ReadMinutes("IntervalMinutes", 30);
            set => Write("IntervalMinutes", value);
        }

        /// <summary>true 면 앉아 있는 시간·서 있는 시간을 따로 쓴다 (타이머 두 개 모드).</summary>
        public static bool UsesSeparatePostureTimers
        {
            get => ReadInt("UsesSeparatePostureTimers", 0) == 1;
            set => Write("UsesSeparatePostureTimers", value ? 1 : 0);
        }

        public static int SittingMinutes
        {
            get => ReadMinutes("SittingMinutes", IntervalMinutes);
            set => Write("SittingMinutes", value);
        }

        public static int StandingMinutes
        {
            get => ReadMinutes("StandingMinutes", IntervalMinutes);
            set => Write("StandingMinutes", value);
        }

        /// <summary>지금 모드에서 해당 자세 차례의 길이(분).</summary>
        public static int MinutesFor(Posture posture)
        {
            if (!UsesSeparatePostureTimers) return IntervalMinutes;
            return posture == Posture.Sitting ? SittingMinutes : StandingMinutes;
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

        static int ReadMinutes(string name, int fallback)
        {
            var value = ReadInt(name, 0);
            return value > 0 ? value : fallback;
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
