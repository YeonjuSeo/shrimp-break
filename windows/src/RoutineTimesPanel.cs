using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>
    /// 트레이 메뉴 안의 루틴 시간 행 (Mac 버전 RoutineTimesMenuItemView 에 해당):
    /// 위쪽 프리셋 라디오(10분·20분·30분·1시간·직접 입력) + 앉기·서기·움직이기 입력칸.
    /// - 프리셋은 켜진(0분이 아닌) 루틴을 모두 그 시간으로 바꾼다.
    /// - "직접 입력"을 누르거나 입력칸을 누르면 바로 입력할 수 있다. Enter·다른 칸으로 이동·메뉴 닫기 때 적용된다.
    /// - 받아들일 수 없는 값(범위 밖, 켜진 마지막 루틴을 0으로)은 알림음과 함께 원래 값으로 되돌린다.
    /// </summary>
    sealed class RoutineTimesPanel
    {
        readonly BreakScheduler scheduler;
        readonly List<RadioButton> presetButtons = new List<RadioButton>();
        readonly RadioButton customButton;
        readonly Dictionary<Routine, TextBox> boxes = new Dictionary<Routine, TextBox>();
        /// <summary>"직접 입력"을 골랐거나 입력칸을 눌렀으면, 값이 프리셋과 같아도 "직접 입력"에 표시를 유지한다.</summary>
        bool isCustomChosen;
        /// <summary>입력칸에서 Enter 를 누른 시각. 같은 Enter 로 메뉴가 닫히지 않게 하는 데 쓴다.</summary>
        DateTime? enterPressedAt;

        public RoutineTimesPanel(ContextMenuStrip menu, BreakScheduler scheduler)
        {
            this.scheduler = scheduler;
            int U(int value) => menu.LogicalToDeviceUnits(value);
            var back = menu.BackColor;
            var panel = new Panel { BackColor = back, Margin = Padding.Empty, Padding = Padding.Empty };

            var caption = new Label
            {
                AutoSize = true,
                Text = "루틴 시간 (0분이면 건너뛰기)",
                ForeColor = SystemColors.GrayText,
                BackColor = back,
                Location = new Point(U(30), U(4)),
            };
            panel.Controls.Add(caption);

            // 라디오 한 줄
            var x = U(30);
            var radioTop = caption.Bottom + U(4);
            foreach (var minutes in Settings.PresetIntervals)
            {
                var button = Radio(minutes % 60 == 0 ? $"{minutes / 60}시간" : $"{minutes}분", minutes, back);
                button.Location = new Point(x, radioTop);
                x = button.Right + U(6);
                presetButtons.Add(button);
                panel.Controls.Add(button);
            }
            customButton = Radio("직접 입력", 0, back);
            customButton.Location = new Point(x, radioTop);
            panel.Controls.Add(customButton);

            // 앉기·서기·움직이기 입력칸
            var top = customButton.Bottom + U(6);
            foreach (var routine in Routines.All)
            {
                var label = new Label { AutoSize = true, Text = routine.Label(), BackColor = back };
                var box = new TextBox
                {
                    Tag = routine,
                    Width = U(56),
                    TextAlign = HorizontalAlignment.Right,
                    MaxLength = 3,
                };
                var unit = new Label { AutoSize = true, Text = "분", BackColor = back };
                box.Location = new Point(U(30) + U(70), top);
                label.Location = new Point(U(30), top + (box.Height - label.PreferredHeight) / 2);
                unit.Location = new Point(box.Right + U(6), label.Top);
                box.MouseDown += (_, __) => ChooseCustom();
                box.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
                box.KeyDown += (_, e) =>
                {
                    if (e.KeyCode != Keys.Enter) return;
                    e.SuppressKeyPress = true;
                    enterPressedAt = DateTime.UtcNow;
                    Commit(box);
                    box.SelectAll();
                };
                box.Leave += (_, __) => Commit(box);
                boxes[routine] = box;
                panel.Controls.AddRange(new Control[] { label, box, unit });
                top = box.Bottom + U(4);
            }

            var width = Math.Max(U(280), customButton.Right + U(12));
            panel.Size = new Size(width, top + U(2));
            Host = new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty };
            Refresh(force: true);
        }

        public ToolStripControlHost Host { get; }

        /// <summary>저장된 시간으로 다시 표시한다. force 가 아니면 입력 중인 칸은 건드리지 않는다.</summary>
        public void Refresh(bool force)
        {
            foreach (var pair in boxes)
                if (force || !pair.Value.Focused) pair.Value.Text = Settings.MinutesFor(pair.Key).ToString();

            var onValues = Routines.All.Where(routine => routine.IsOn()).Select(Settings.MinutesFor).Distinct().ToList();
            int? preset = !isCustomChosen && onValues.Count == 1 ? onValues[0] : (int?)null;
            foreach (var button in presetButtons) button.Checked = (int)button.Tag == preset;
            customButton.Checked = !presetButtons.Any(button => button.Checked);
        }

        /// <summary>입력 중인 칸이 있으면 그 값을 적용한다 (메뉴가 닫힐 때).</summary>
        public void CommitEditing()
        {
            foreach (var box in boxes.Values.Where(box => box.Focused)) Commit(box);
        }

        /// <summary>방금 입력칸에서 Enter 를 눌렀는지 (그 Enter 로 메뉴가 닫히려는 것이면 막는다). 한 번만 true.</summary>
        public bool ConsumeEnter()
        {
            var pressed = enterPressedAt.HasValue && DateTime.UtcNow - enterPressedAt.Value < TimeSpan.FromMilliseconds(500);
            enterPressedAt = null;
            return pressed;
        }

        RadioButton Radio(string text, int minutes, Color back)
        {
            // 상태는 Refresh 가 직접 정한다 (설정값과 어긋나지 않도록 AutoCheck 끔).
            var button = new RadioButton { AutoSize = true, AutoCheck = false, Text = text, Tag = minutes, BackColor = back };
            button.Click += (_, __) => HandleRadio(minutes);
            return button;
        }

        void HandleRadio(int minutes)
        {
            if (minutes == 0)
            {
                ChooseCustom();
                var first = boxes[Routine.Sitting];
                first.Focus();
                first.SelectAll();
                return;
            }
            isCustomChosen = false;
            CommitEditing();
            scheduler.ApplyPreset(minutes); // → Changed → 메뉴 갱신
            Refresh(force: true);
        }

        void ChooseCustom()
        {
            isCustomChosen = true;
            Refresh(force: false);
        }

        void Commit(TextBox box)
        {
            var routine = (Routine)box.Tag;
            var current = Settings.MinutesFor(routine);
            if (int.TryParse(box.Text.Trim(), out var minutes))
            {
                if (minutes == current) return;
                if (scheduler.SetMinutes(routine, minutes)) return;
            }
            // 숫자가 아니거나 받아들일 수 없는 값 → 알리고 원래 값으로.
            SystemSounds.Beep.Play();
            box.Text = current.ToString();
        }
    }
}
