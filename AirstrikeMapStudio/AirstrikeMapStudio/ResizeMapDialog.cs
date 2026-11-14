using System;
using System.Drawing;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed class ResizeMapDialog : Form
    {
        private readonly NumericUpDown width;
        private readonly NumericUpDown height;
        private readonly NumericUpDown fillHeight;
        private readonly Label warning;

        public int NewWidth => (int)width.Value;
        public int NewHeight => (int)height.Value;
        public byte FillHeight => (byte)fillHeight.Value;

        public ResizeMapDialog(int currentWidth, int currentHeight)
        {
            Text = "Resize map";
            ClientSize = new Size(400, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(15),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            Controls.Add(flow);

            flow.Controls.Add(new Label { Text = "Map dimensions (cells)", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });

            var dims = new FlowLayoutPanel { Width = 350, Height = 32, WrapContents = false };
            dims.Controls.Add(new Label { Text = "Width", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            width = new NumericUpDown { Minimum = Map.MinMapDimension, Maximum = Map.MaxMapWidth, Value = currentWidth, Width = 80 };
            dims.Controls.Add(width);

            dims.Controls.Add(new Label { Text = "Height", AutoSize = true, Padding = new Padding(16, 6, 0, 0) });
            height = new NumericUpDown { Minimum = Map.MinMapDimension, Maximum = Map.MaxMapHeight, Value = currentHeight, Width = 80 };
            dims.Controls.Add(height);
            flow.Controls.Add(dims);

            flow.Controls.Add(new Label { Text = "Height for expanded areas (0–255)", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
            fillHeight = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 128, Width = 80 };
            flow.Controls.Add(fillHeight);

            warning = new Label
            {
                Width = 350,
                Height = 60,
                ForeColor = Color.DarkRed,
                Text = "Warning: Expanding the map adds flat terrain to the right/bottom edges.\n" +
                       "Shrinking the map will delete any terrain and clamp objects outside the new bounds.",
            };
            flow.Controls.Add(warning);

            var buttons = new FlowLayoutPanel { Width = 350, Height = 35, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            var apply = new Button { Text = "Resize", DialogResult = DialogResult.OK };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(apply);
            flow.Controls.Add(buttons);

            AcceptButton = apply;
            CancelButton = cancel;
        }
    }
}