using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public class RouteSample
    {
        public double Time, Camera, Lower, Upper, X, Y, Radius, Progress, Wait;
        public int State;
    }

    public class RouteRecording
    {
        public List<RouteSample> Samples = new List<RouteSample>();
        public double Activation;

        public static RouteRecording Load(string path)
        {
            var r = new RouteRecording();

            foreach (string line in File.ReadAllLines(path).Skip(1))
            {
                var v = line.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length != 10 || v.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                    throw new InvalidDataException("Invalid recording row.");
                if (r.Samples.Count > 0 && v[0] <= r.Samples.Last().Time)
                    throw new InvalidDataException("Recording time must increase.");

                r.Samples.Add(new RouteSample
                {
                    Time = v[0], Camera = v[1], Lower = v[2], Upper = v[3],
                    X = v[4], Y = v[5], Radius = v[6], State = (int)v[7],
                    Progress = v[8], Wait = v[9],
                });
            }

            if (!r.Samples.Any(x => x.State != 1))
                throw new InvalidDataException("Recording has no activation event.");

            r.Activation = r.Samples.First(x => x.State != 1).Time;
            return r;
        }

        public RouteSample At(double relative)
        {
            double t = relative + Activation;
            if (t < Samples[0].Time || t > Samples.Last().Time) return null;
            return Samples.Last(x => x.Time <= t);
        }
    }

    public class RouteStudy : Form
    {
        private readonly RouteRecording a, b;
        private readonly TrackBar timeline = new TrackBar();
        private readonly Label    time     = new Label();
        private readonly Timer    timer    = new Timer();
        private readonly Button   play     = new Button();
        private readonly StudyCanvas view;
        private double seconds = -2;

        public RouteStudy(string directory)
        {
            a = RouteRecording.Load(Path.Combine(directory, "A.csv"));
            b = RouteRecording.Load(Path.Combine(directory, "B.csv"));

            Text = "Launcher route study - recorded A/B playback";
            ClientSize = new Size(1100, 740);
            MinimumSize = new Size(920, 680);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(20, 28, 37);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10);

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 78,
                Padding = new Padding(15),
                Text = "Recorded gameplay reference | A: spawn Y 28   /   B: spawn Y 32\n" +
                       "These are recorded positions and states, not predictions for your open map. " +
                       "Time is aligned to each launcher's first observed movement.",
            };

            var bottom   = new Panel { Dock = DockStyle.Bottom, Height = 102 };
            var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };

            play.Text   = "Play";
            play.Width  = 90;
            play.Height = 30;
            play.Click += (s, e) => { timer.Enabled = !timer.Enabled; play.Text = timer.Enabled ? "Pause" : "Play"; };
            controls.Controls.Add(play);

            var reset = new Button { Text = "Reset", Width = 90, Height = 30 };
            reset.Click += (s, e) => { timer.Stop(); play.Text = "Play"; timeline.Value = 0; };
            controls.Controls.Add(reset);

            var activation = new Button { Text = "Movement starts", Width = 145, Height = 30 };
            activation.Click += (s, e) => timeline.Value = 200;
            controls.Controls.Add(activation);

            time.AutoSize = true;
            time.Padding  = new Padding(12, 5, 0, 0);
            controls.Controls.Add(time);

            timeline.Dock = DockStyle.Bottom;
            timeline.Height = 50;
            timeline.Minimum = 0;
            timeline.Maximum = 2200;
            timeline.TickFrequency = 100;
            timeline.ValueChanged += (s, e) =>
            {
                seconds = timeline.Value / 100.0 - 2;
                UpdateFrame();
            };
            bottom.Controls.Add(controls);
            bottom.Controls.Add(timeline);

            view = new StudyCanvas(a, b) { Dock = DockStyle.Fill };
            Controls.Add(view);
            Controls.Add(header);
            Controls.Add(bottom);

            timer.Interval = 40;
            timer.Tick += (s, e) =>
            {
                timeline.Value = Math.Min(timeline.Maximum, timeline.Value + 4);
                if (timeline.Value == timeline.Maximum) { timer.Stop(); play.Text = "Play"; }
            };
            UpdateFrame();
        }

        private void UpdateFrame()
        {
            view.Seconds = seconds;
            view.Invalidate();
            time.Text = seconds.ToString("F2") + " s from first recorded movement";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        public void Snapshot(string path, double relative)
        {
            timeline.Value = (int)Math.Round((relative + 2) * 100);
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
                bmp.Save(path);
            }
        }

        public static void Test(string directory, string image)
        {
            var a = RouteRecording.Load(Path.Combine(directory, "A.csv"));
            var b = RouteRecording.Load(Path.Combine(directory, "B.csv"));

            if (a.At(-1).State != 1
                || a.At(0).State  != 2
                || b.At(0).State  != 0
                || b.At(17).State != 0
                || b.At(18).State != 2)
                throw new Exception("Recorded lifecycle regression.");

            if (a.At(-100) != null || b.At(1000) != null)
                throw new Exception("Missing sample handling failed.");

            foreach (var r in new[] { a, b })
            {
                var s = r.At(0);
                bool inside = s.Y - s.Radius >= s.Lower && s.Y - s.Radius <= s.Upper;
                if (inside != (r == b)) throw new Exception("Boundary mismatch.");
            }

            using (var f = new RouteStudy(directory))
            {
                f.Show();
                Application.DoEvents();
                f.Snapshot(image, 0);
                f.Hide();
            }

            Console.WriteLine("PASS A/B samples, aligned playback, missing samples, activation boundaries and snapshot");
        }
    }

    public class StudyCanvas : Control
    {
        private readonly RouteRecording a, b;
        public double Seconds;

        public StudyCanvas(RouteRecording aa, RouteRecording bb)
        {
            a = aa; b = bb;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Draw(e.Graphics, a, new RectangleF(12, 6, Width / 2 - 20, Height - 12), "A - original spawn (8, 28)", 1100);
            Draw(e.Graphics, b, new RectangleF(Width / 2 + 8, 6, Width / 2 - 20, Height - 12), "B - corrected spawn (8, 32)", 1260);
        }

        private void Draw(Graphics g, RouteRecording r, RectangleF box, string title, float spawn)
        {
            using (var background = new SolidBrush(Color.FromArgb(27, 38, 49)))
                g.FillRectangle(background, box);
            g.DrawString(title, Font, Brushes.White, box.X + 12, box.Y + 10);

            RectangleF plot = new RectangleF(box.X + 48, box.Y + 125, box.Width - 66, box.Height - 174);
            Func<double, double, PointF> xy = (x, y) => new PointF(
                plot.Left + (float)((x - 280) / 300) * plot.Width,
                plot.Bottom - (float)((y - 1040) / 280) * plot.Height);

            var sample = r.At(Seconds);
            var saved = g.Save();
            g.SetClip(plot);

            if (sample != null)
            {
                float top = xy(280, sample.Upper + sample.Radius).Y;
                float bot = xy(280, sample.Lower + sample.Radius).Y;
                using (var brush = new SolidBrush(Color.FromArgb(55, 55, 200, 130)))
                    g.FillRectangle(brush, plot.Left, top, plot.Width, bot - top);
                using (var pen = new Pen(Color.MediumAquamarine, 2))
                {
                    g.DrawLine(pen, plot.Left, top, plot.Right, top);
                    g.DrawLine(pen, plot.Left, bot, plot.Right, bot);
                }
            }

            using (var pen = new Pen(Color.FromArgb(55, 190, 210, 230)))
                for (int y = 1040; y <= 1320; y += 40)
                    g.DrawLine(pen, xy(280, y), xy(580, y));

            using (var pen = new Pen(Color.LightSteelBlue, 2))
            {
                g.DrawBezier(pen, xy(380, 1140), xy(449.186708, 1143.16162), xy(440, 1180), xy(500, 1220));
                pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, xy(340, spawn), xy(380, 1140));
            }

            var sp = xy(340, spawn);
            g.DrawEllipse(Pens.Gray, sp.X - 6, sp.Y - 6, 12, 12);
            g.DrawString("spawn", Font, Brushes.LightGray, sp.X + 8, sp.Y);

            foreach (var pt in new[] { xy(380, 1140), xy(500, 1220) })
                g.FillEllipse(Brushes.LightSteelBlue, pt.X - 4, pt.Y - 4, 8, 8);

            if (sample != null)
            {
                var p = xy(sample.X, sample.Y);
                Brush brush = sample.State == 0 ? Brushes.LimeGreen
                            : sample.State == 2 ? Brushes.Salmon
                            : Brushes.Gold;
                g.FillEllipse(brush, p.X - 9, p.Y - 9, 18, 18);
                g.DrawEllipse(Pens.White, p.X - 10, p.Y - 10, 20, 20);
            }
            g.Restore(saved);

            for (int y = 1040; y <= 1320; y += 40)
                g.DrawString(y.ToString(), Font, Brushes.LightSlateGray, box.X + 4, xy(280, y).Y - 8);

            string info = "No recorded sample at this time.";
            if (sample != null)
            {
                string state = sample.State == 0 ? "Active (state 0)"
                             : sample.State == 1 ? "Waiting (state 1)"
                             : "Inactive; Shoot blocked (state 2)";
                info = state + "\nWorld XY: " + sample.X.ToString("F1") + ", " + sample.Y.ToString("F1")
                     + " | wait field: " + sample.Wait.ToString("F1")
                     + "\nCamera Y: " + sample.Camera.ToString("F1")
                     + " | recorded t: " + sample.Time.ToString("F2") + " s";
            }
            g.DrawString(info, Font, Brushes.White, new RectangleF(box.X + 12, box.Y + 42, box.Width - 24, 80));
            g.DrawString(
                "Green: measured Y band. Dashes: spawn-to-route jump.\n" +
                "Dots use recorded samples; active does not guarantee firing.",
                Font, Brushes.LightSteelBlue, new RectangleF(box.X + 10, box.Bottom - 44, box.Width - 20, 44));
        }
    }
}