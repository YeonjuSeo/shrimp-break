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

        /// <summary>이 시간(분) 이상 키보드·마우스 입력이 없으면 "자리를 비웠다(=일어났다)"로 본다. 0 이면 끔.</summary>
        public static int AwayThresholdMinutes
        {
            get => ReadInt("AwayThresholdMinutes", 5);
            set => Write("AwayThresholdMinutes", value);
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

        static void Write(string name, int value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, value, RegistryValueKind.DWord);
        }
    }
}
