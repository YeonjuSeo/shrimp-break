using System;
using System.Reflection;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>
    /// 작업 표시줄 알림 영역(트레이) 아이콘과 메뉴 (Mac 버전 MenuBarController 에 해당).
    /// 트레이 아이콘 옆에는 글자를 붙일 수 없어서, 남은 시간은 마우스를 올리면 나오는 툴팁으로 보여준다.
    /// </summary>
    sealed class TrayController : IDisposable
    {
        /// <summary>시간을 고르는 하위 메뉴가 어떤 타이머를 바꾸는지.</summary>
        enum TimerTarget { Single, Sitting, Standing }

        readonly BreakScheduler scheduler;
        readonly PresenceMonitor presence;
        readonly ShrimpOverlay overlay;
        readonly Telemetry telemetry;
        readonly TrayIcons icons = new TrayIcons();
        readonly NotifyIcon notifyIcon = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        /// <summary>툴팁의 "남은 분" 갱신용. 카운트다운 중에만 1분에 한 번 깨어난다.</summary>
        readonly Timer tooltipTimer = new Timer { Interval = 60_000 };
        System.Drawing.Icon shownIcon;

        // 메뉴가 열려 있는 동안 그 자리에서 바꿔 줄 항목들.
        ToolStripMenuItem statusItem;
        ToolStripMenuItem startSittingItem;
        ToolStripMenuItem startStandingItem;
        ToolStripMenuItem separateToggleItem;
        ToolStripItem[] durationItems = new ToolStripItem[0];
        /// <summary>시간 항목을 만들 때의 설정값. 바뀌었을 때만 다시 만든다 (열려 있는 하위 메뉴가 닫히지 않도록).</summary>
        string durationSignature = "";

        public TrayController(BreakScheduler scheduler, PresenceMonitor presence, ShrimpOverlay overlay, Telemetry telemetry)
        {
            this.scheduler = scheduler;
            this.presence = presence;
            this.overlay = overlay;
            this.telemetry = telemetry;

            // 항목이 비어 있으면 WinForms 가 열기를 취소해 두므로, 채운 뒤 취소를 풀어 준다.
            menu.Opening += (_, e) =>
            {
                BuildMenu();
                e.Cancel = false;
            };
            // "따로 설정" 토글은 눌러도 메뉴를 닫지 않는다 → 아래 시간 항목이 그 자리에서 바뀐다.
            menu.Closing += (_, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && separateToggleItem?.Selected == true)
                    e.Cancel = true;
            };
            BuildMenu();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.MouseUp += HandleTrayMouseUp;
            tooltipTimer.Tick += (_, __) => Refresh();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;

            Refresh();
            notifyIcon.Visible = true;
        }

        public void Refresh()
        {
            var badge = scheduler.Phase == Phase.Alerting ? TrayIcons.Badge.Alert
                : scheduler.Phase == Phase.Paused || scheduler.Phase == Phase.Away ? TrayIcons.Badge.Dimmed
                : TrayIcons.Badge.None;
            // 트레이가 새 아이콘을 받은 뒤에 이전 아이콘을 정리한다.
            var previous = shownIcon;
            shownIcon = icons.Make(scheduler.Posture, badge);
            notifyIcon.Icon = shownIcon;
            previous?.Dispose();
            notifyIcon.Text = TooltipText();

            if (scheduler.Phase == Phase.Counting) tooltipTimer.Start();
            else tooltipTimer.Stop();

            UpdateOpenMenu();
        }

        public void Dispose()
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            shownIcon?.Dispose();
            menu.Dispose();
            tooltipTimer.Dispose();
            icons.Dispose();
        }

        // ── 메뉴 ────────────────────────────────────────────────

        void BuildMenu()
        {
            menu.Items.Clear();

            statusItem = new ToolStripMenuItem(StatusText()) { Enabled = false };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Item("타이머 초기화 (지금 차례 처음부터)", () =>
            {
                telemetry.RecordQuickAction(Telemetry.QuickAction.Restart);
                scheduler.StartTurn(null);
            }));
            startSittingItem = Item(StartText(Posture.Sitting), () =>
            {
                telemetry.RecordQuickAction(Telemetry.QuickAction.Sit);
                scheduler.StartTurn(Posture.Sitting);
            });
            startStandingItem = Item(StartText(Posture.Standing), () =>
            {
                telemetry.RecordQuickAction(Telemetry.QuickAction.Stand);
                scheduler.StartTurn(Posture.Standing);
            });
            menu.Items.Add(startSittingItem);
            menu.Items.Add(startStandingItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Item("일시정지", () => scheduler.SetPaused(!scheduler.IsUserPaused), scheduler.IsUserPaused));
#if SHRIMP_DEV_TOOLS
            // 본인 확인용 빌드에만 들어간다 (DEV_TOOLS=1). 공유용 zip 에는 없다.
            var summon = Item("지금 새우 부르기 (테스트)", () =>
            {
                if (scheduler.Phase == Phase.Alerting) overlay.Spawn();
                else scheduler.TriggerNow();
            });
            summon.Enabled = scheduler.Phase != Phase.Paused && scheduler.Phase != Phase.Away;
            menu.Items.Add(summon);
#endif
            menu.Items.Add(new ToolStripSeparator());

            separateToggleItem = Item("앉기·서기 시간 따로 설정",
                                      () => scheduler.SetSeparatePostureTimers(!Settings.UsesSeparatePostureTimers));
            ApplyToggleState(separateToggleItem, Settings.UsesSeparatePostureTimers);
            menu.Items.Add(separateToggleItem);
            durationItems = MakeDurationItems();
            durationSignature = CurrentDurationSignature();
            menu.Items.AddRange(durationItems);
            menu.Items.Add(EscalationSlider());
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(AwayMenu());
            menu.Items.Add(Item("Windows 시작 시 자동 실행", HandleToggleLaunchAtLogin, Settings.LaunchAtLogin));
            if (telemetry.IsAvailable)
                menu.Items.Add(Item("익명 사용 통계 보내기", () => telemetry.SetEnabled(!telemetry.IsEnabled), telemetry.IsEnabled));
            menu.Items.Add(new ToolStripSeparator());

            // 종료 전에 오늘 통계를 보낼 시간을 잠깐(최대 2초) 준다.
            menu.Items.Add(Item("종료", () => telemetry.FlushBeforeQuit(Application.Exit)));
        }

        ToolStripItem[] MakeDurationItems() => Settings.UsesSeparatePostureTimers
            ? new ToolStripItem[] { DurationMenu(TimerTarget.Sitting), DurationMenu(TimerTarget.Standing) }
            : new ToolStripItem[] { DurationMenu(TimerTarget.Single) };

        static string CurrentDurationSignature() =>
            $"{Settings.UsesSeparatePostureTimers}/{Settings.IntervalMinutes}/{Settings.SittingMinutes}/{Settings.StandingMinutes}";

        /// <summary>토글처럼 보이도록 체크 표시 + 오른쪽에 켜짐/꺼짐.</summary>
        static void ApplyToggleState(ToolStripMenuItem item, bool isOn)
        {
            item.Checked = isOn;
            item.ShortcutKeyDisplayString = isOn ? "켜짐" : "꺼짐";
            item.ShowShortcutKeys = true;
        }

        /// <summary>메뉴가 열린 채로 상태·설정이 바뀌었을 때 해당 항목만 그 자리에서 갱신한다.</summary>
        void UpdateOpenMenu()
        {
            if (!menu.Visible || separateToggleItem == null) return;
            statusItem.Text = StatusText();
            startSittingItem.Text = StartText(Posture.Sitting);
            startStandingItem.Text = StartText(Posture.Standing);
            ApplyToggleState(separateToggleItem, Settings.UsesSeparatePostureTimers);

            if (durationSignature == CurrentDurationSignature()) return;
            durationSignature = CurrentDurationSignature();
            menu.SuspendLayout();
            foreach (var item in durationItems)
            {
                menu.Items.Remove(item);
                item.Dispose();
            }
            durationItems = MakeDurationItems();
            var insertAt = menu.Items.IndexOf(separateToggleItem) + 1;
            for (var i = 0; i < durationItems.Length; i++) menu.Items.Insert(insertAt + i, durationItems[i]);
            menu.ResumeLayout();
        }

        /// <summary>
        /// "새우 증식 속도" 슬라이더 행 (3초 ~ 1분). 메뉴 안에 컨트롤을 넣은 것이라 눌러도 메뉴가 닫히지 않는다.
        /// 끄는 동안은 글자만 바뀌고, 손을 뗄 때 한 번 적용한다 (끄는 내내 설정 저장·통계 기록을 하지 않도록).
        /// </summary>
        ToolStripControlHost EscalationSlider()
        {
            var panel = new Panel { BackColor = menu.BackColor, Margin = Padding.Empty, Padding = Padding.Empty };
            var label = new Label { AutoSize = true, Text = EscalationText(Settings.EscalationSeconds), BackColor = menu.BackColor };
            var minLabel = new Label { AutoSize = true, Text = $"{Settings.MinEscalationSeconds}초", ForeColor = System.Drawing.SystemColors.GrayText, BackColor = menu.BackColor };
            var maxLabel = new Label { AutoSize = true, Text = "1분", ForeColor = System.Drawing.SystemColors.GrayText, BackColor = menu.BackColor };
            var slider = new TrackBar
            {
                Minimum = Settings.MinEscalationSeconds,
                Maximum = Settings.MaxEscalationSeconds,
                Value = Settings.EscalationSeconds,
                SmallChange = 1,
                LargeChange = 5,
                TickStyle = TickStyle.None,
                AutoSize = false,
                BackColor = menu.BackColor,
            };
            panel.Controls.AddRange(new Control[] { label, minLabel, slider, maxLabel });

            // 화면 배율에 맞춰 배치 (menu.LogicalToDeviceUnits: 100% 기준 값을 지금 배율로).
            int U(int value) => menu.LogicalToDeviceUnits(value);
            var width = U(280);
            label.Location = new System.Drawing.Point(U(30), U(4));
            var sliderTop = label.Bottom + U(2);
            minLabel.Location = new System.Drawing.Point(U(30), sliderTop + U(4));
            maxLabel.Location = new System.Drawing.Point(width - U(12) - maxLabel.PreferredWidth, sliderTop + U(4));
            slider.Location = new System.Drawing.Point(minLabel.Location.X + minLabel.PreferredWidth + U(4), sliderTop);
            slider.Size = new System.Drawing.Size(maxLabel.Location.X - U(4) - slider.Location.X, U(26));
            panel.Size = new System.Drawing.Size(width, slider.Bottom + U(4));

            var committed = Settings.EscalationSeconds;
            void Commit()
            {
                if (slider.Value == committed) return;
                committed = slider.Value;
                scheduler.SetEscalationSeconds(committed);
            }
            slider.Scroll += (_, __) => label.Text = EscalationText(slider.Value);
            slider.MouseUp += (_, __) => Commit();
            slider.KeyUp += (_, __) => Commit();

            return new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        static string EscalationText(int seconds) =>
            "새우 증식 속도: " + (seconds >= 60 ? "1분마다" : $"{seconds}초마다") + " 한 마리";

        static ToolStripMenuItem Item(string text, Action onClick, bool isChecked = false)
            => new ToolStripMenuItem(text, null, (_, __) => onClick()) { Checked = isChecked };

        ToolStripMenuItem DurationMenu(TimerTarget target)
        {
            var current = MinutesOf(target);
            var parent = new ToolStripMenuItem($"{LabelOf(target)}: {DurationText(current)}");
            foreach (var minutes in Settings.PresetIntervals)
            {
                var value = minutes;
                parent.DropDownItems.Add(Item(DurationText(value), () => Apply(value, target), value == current));
            }
            parent.DropDownItems.Add(new ToolStripSeparator());
            var isCustom = Array.IndexOf(Settings.PresetIntervals, current) < 0;
            parent.DropDownItems.Add(Item(isCustom ? $"직접 입력… ({DurationText(current)})" : "직접 입력…",
                                          () => HandleCustomDuration(target), isCustom));
            return parent;
        }

        ToolStripMenuItem AwayMenu()
        {
            var current = Settings.AwayThresholdMinutes;
            var label = current == 0 ? "끔" : $"{current}분 무입력";
            var parent = new ToolStripMenuItem($"자리 비움 판정: {label}");
            foreach (var minutes in Settings.AwayPresets)
            {
                var value = minutes;
                parent.DropDownItems.Add(Item($"{value}분 동안 입력 없으면", () => SetAway(value), value == current));
            }
            parent.DropDownItems.Add(Item("끄기 (잠금·절전만 감지)", () => SetAway(0), current == 0));
            return parent;
        }

        // ── 동작 ────────────────────────────────────────────────

        void HandleTrayMouseUp(object sender, MouseEventArgs e)
        {
            // 왼쪽 클릭에도 메뉴를 연다 (NotifyIcon 은 기본적으로 오른쪽 클릭만 연다).
            if (e.Button != MouseButtons.Left) return;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(notifyIcon, null);
        }

        void HandleCustomDuration(TimerTarget target)
        {
            var prompt = target == TimerTarget.Single ? "몇 분마다 새우를 부를까요?"
                : target == TimerTarget.Sitting ? "몇 분 앉아 있다가 일어날까요?"
                : "몇 분 서 있다가 앉을까요?";
            using (var dialog = new DurationDialog($"{LabelOf(target)} 직접 입력", prompt, MinutesOf(target)))
            {
                if (dialog.ShowDialog() == DialogResult.OK) Apply(dialog.Minutes, target);
            }
        }

        void Apply(int minutes, TimerTarget target)
        {
            switch (target)
            {
                case TimerTarget.Single: scheduler.SetInterval(minutes); break;
                case TimerTarget.Sitting: scheduler.SetMinutes(minutes, Posture.Sitting); break;
                case TimerTarget.Standing: scheduler.SetMinutes(minutes, Posture.Standing); break;
            }
        }

        void SetAway(int minutes)
        {
            Settings.AwayThresholdMinutes = minutes;
            presence.SettingsChanged();
            telemetry.RecordSettingsIfChanged();
            Refresh();
        }

        void HandleToggleLaunchAtLogin()
        {
            try
            {
                Settings.LaunchAtLogin = !Settings.LaunchAtLogin;
            }
            catch (Exception error)
            {
                MessageBox.Show($"자동 실행 설정을 바꾸지 못했어요.\n\n{error.Message}", "새우 브레이크",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>작업 표시줄 밝기 테마가 바뀌면 아이콘 색을 다시 칠한다.</summary>
        void HandleUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General) Ui.Post(Refresh);
        }

        // ── 글자 ────────────────────────────────────────────────

        static string LabelOf(TimerTarget target) => target == TimerTarget.Single ? "알림 간격"
            : target == TimerTarget.Sitting ? "앉아 있는 시간" : "서 있는 시간";

        static int MinutesOf(TimerTarget target) => target == TimerTarget.Single ? Settings.IntervalMinutes
            : target == TimerTarget.Sitting ? Settings.SittingMinutes : Settings.StandingMinutes;

        string StatusText()
        {
            switch (scheduler.Phase)
            {
                case Phase.Counting:
                    return $"{PostureText()} · 다음 새우까지 약 {MinutesLeft()}분";
                case Phase.Alerting:
                    // 타이머가 울리는 순간 이미 다음 자세로 넘어가 있다.
                    var action = scheduler.Posture == Posture.Standing ? "일어나서" : "앉아서";
                    return $"새우 출동 중! {overlay.Count}마리 — {action} 새우를 클릭하세요";
                case Phase.Paused:
                    return "일시정지됨";
                default:
                    return "자리 비움 — 돌아오면 처음부터 다시 셉니다";
            }
        }

        /// <summary>트레이 툴팁. Windows 제한으로 63자를 넘으면 안 된다.</summary>
        string TooltipText()
        {
            string text;
            switch (scheduler.Phase)
            {
                case Phase.Counting: text = $"새우 브레이크 — {PostureText()} · 약 {MinutesLeft()}분 남음"; break;
                case Phase.Alerting:
                    text = scheduler.Posture == Posture.Standing ? "새우 출동! 일어나서 새우를 클릭하세요" : "새우 출동! 앉아서 새우를 클릭하세요";
                    break;
                case Phase.Paused: text = "새우 브레이크 — 일시정지됨"; break;
                default: text = "새우 브레이크 — 자리 비움"; break;
            }
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        static string StartText(Posture posture)
        {
            var minutes = DurationText(Settings.MinutesFor(posture));
            return posture == Posture.Sitting ? $"앉기 타이머 바로 시작 ({minutes})" : $"서기 타이머 바로 시작 ({minutes})";
        }

        string PostureText() => scheduler.Posture == Posture.Sitting ? "앉아 있는 중" : "서 있는 중";

        int MinutesLeft() => Math.Max(1, (int)Math.Ceiling(scheduler.Remaining.TotalMinutes));

        static string DurationText(int minutes)
        {
            if (minutes % 60 == 0) return $"{minutes / 60}시간";
            if (minutes > 60) return $"{minutes / 60}시간 {minutes % 60}분";
            return $"{minutes}분";
        }
    }
}
