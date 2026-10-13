using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShrimpBreak
{
    /// <summary>
    /// "사용자가 지금 자리에 앉아 PC 를 쓰고 있는가"를 판단한다 (Mac 버전 PresenceMonitor 와 같은 규칙).
    ///
    /// 앱(Slack·Claude 등) 실행 여부 대신 OS 신호만 본다:
    /// - 절전 / 화면 꺼짐 / 화면 잠금 / 원격·사용자 전환 → 즉시 자리 비움
    /// - 키보드·마우스 입력이 N분 이상 없음 → 자리 비움 (일어났다고 간주)
    /// 관리자 권한 없이 동작한다.
    /// </summary>
    sealed class PresenceMonitor : IDisposable
    {
        enum Reason { SystemSleep, DisplayOff, Locked, SessionInactive, Idle }

        /// <summary>자리에 있는 동안의 무입력 체크 주기. 분 단위 판정이라 30초면 충분하다.</summary>
        static readonly TimeSpan PresentPollInterval = TimeSpan.FromSeconds(30);
        /// <summary>자리 비움 상태에서 복귀를 감지하는 주기.</summary>
        static readonly TimeSpan AwayPollInterval = TimeSpan.FromSeconds(3);

        readonly HashSet<Reason> reasons = new HashSet<Reason>();
        readonly Timer idleTimer = new Timer();
        DisplayStateWindow displayWindow;

        public event Action<bool> AwayChanged;

        public bool IsAway => reasons.Count > 0;

        public void Start()
        {
            SystemEvents.SessionSwitch += HandleSessionSwitch;
            SystemEvents.PowerModeChanged += HandlePowerModeChanged;
            displayWindow = new DisplayStateWindow(isOn => Set(Reason.DisplayOff, !isOn));
            idleTimer.Tick += (_, __) => CheckIdle();
            RescheduleIdlePolling();
        }

        /// <summary>자리 비움 판정 시간 설정이 바뀌었을 때 호출.</summary>
        public void SettingsChanged()
        {
            if (Settings.AwayThresholdMinutes == 0) Set(Reason.Idle, false);
            RescheduleIdlePolling();
        }

        public void Dispose()
        {
            SystemEvents.SessionSwitch -= HandleSessionSwitch;
            SystemEvents.PowerModeChanged -= HandlePowerModeChanged;
            displayWindow?.Dispose();
            idleTimer.Dispose();
        }

        // SystemEvents 는 다른 스레드에서 올 수 있어 UI 스레드로 넘긴다.
        void HandleSessionSwitch(object sender, SessionSwitchEventArgs e) => Ui.Post(() =>
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock: Set(Reason.Locked, true); break;
                case SessionSwitchReason.SessionUnlock: Set(Reason.Locked, false); break;
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect: Set(Reason.SessionInactive, true); break;
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect: Set(Reason.SessionInactive, false); break;
            }
        });

        void HandlePowerModeChanged(object sender, PowerModeChangedEventArgs e) => Ui.Post(() =>
        {
            if (e.Mode == PowerModes.Suspend) Set(Reason.SystemSleep, true);
            else if (e.Mode == PowerModes.Resume) Set(Reason.SystemSleep, false);
        });

        void Set(Reason reason, bool active)
        {
            var wasAway = IsAway;
            var changed = active ? reasons.Add(reason) : reasons.Remove(reason);
            if (!changed) return;
            RescheduleIdlePolling();
            if (wasAway != IsAway) AwayChanged?.Invoke(IsAway);
        }

        void RescheduleIdlePolling()
        {
            idleTimer.Stop();
            // 절전·잠금 중에는 무입력 체크가 의미 없으므로 깨어나지 않는다.
            var hardAway = reasons.Count > (reasons.Contains(Reason.Idle) ? 1 : 0);
            if (Settings.AwayThresholdMinutes <= 0 || hardAway) return;

            var interval = reasons.Contains(Reason.Idle) ? AwayPollInterval : PresentPollInterval;
            idleTimer.Interval = (int)interval.TotalMilliseconds;
            idleTimer.Start();
        }

        void CheckIdle()
        {
            var idle = NativeMethods.IdleTime();
            var threshold = TimeSpan.FromMinutes(Settings.AwayThresholdMinutes);

            if (reasons.Contains(Reason.Idle))
            {
                // 최근 몇 초 안에 입력이 있었으면 돌아온 것.
                if (idle < AwayPollInterval + TimeSpan.FromSeconds(2)) Set(Reason.Idle, false);
            }
            else if (idle >= threshold)
            {
                Set(Reason.Idle, true);
            }
        }

        /// <summary>모니터 켜짐/꺼짐 알림을 받기 위한 보이지 않는 창.</summary>
        sealed class DisplayStateWindow : NativeWindow, IDisposable
        {
            readonly Action<bool> onDisplayChanged;
            IntPtr notification;

            public DisplayStateWindow(Action<bool> onDisplayChanged)
            {
                this.onDisplayChanged = onDisplayChanged;
                CreateHandle(new CreateParams());
                var guid = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
                notification = NativeMethods.RegisterPowerSettingNotification(Handle, ref guid, 0);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeMethods.WM_POWERBROADCAST && (int)m.WParam == NativeMethods.PBT_POWERSETTINGCHANGE)
                {
                    var setting = Marshal.PtrToStructure<NativeMethods.POWERBROADCAST_SETTING>(m.LParam);
                    if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
                    {
                        // 0 = 꺼짐, 1 = 켜짐, 2 = 어두워짐(켜진 것으로 본다)
                        onDisplayChanged(setting.Data != 0);
                    }
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (notification != IntPtr.Zero) NativeMethods.UnregisterPowerSettingNotification(notification);
                notification = IntPtr.Zero;
                DestroyHandle();
            }
        }
    }
}
