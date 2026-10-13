using System.Drawing;
using System.Windows.Forms;

namespace ShrimpBreak
{
    /// <summary>"직접 입력…" 에서 여는 분 단위 입력 창 (1 ~ 480분).</summary>
    sealed class DurationDialog : Form
    {
        readonly NumericUpDown input;

        public DurationDialog(string title, string prompt, int currentMinutes)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            Font = SystemFonts.MessageBoxFont;

            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = $"{prompt} (1 ~ 480분)", AutoSize = true, Margin = new Padding(0, 0, 0, 8) });

            input = new NumericUpDown { Minimum = 1, Maximum = 480, Value = Clamp(currentMinutes), Width = 120 };
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            row.Controls.Add(input);
            row.Controls.Add(new Label { Text = "분", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
            layout.Controls.Add(row);

            var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (_, __) =>
            {
                Activate();
                input.Select(0, input.Text.Length);
                input.Focus();
            };
        }

        public int Minutes => (int)input.Value;

        static int Clamp(int minutes) => minutes < 1 ? 1 : minutes > 480 ? 480 : minutes;
    }
}
