using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Script-specific approximate simulation of a rocketlauncher's route
    /// and activation state. Not a general .scr interpreter.
    /// </summary>
    public class LauncherSimulation
    {
        public Placement Object;
        public List<float> Table = new List<float>();

        public double Time, Camera, CameraSpeed = 42, StartCamera;
        public float  X, Y, Progress, Speed = 70, Wait;
        public int    State = 1, Index;
        public bool   Finished, Accelerating, ScriptsActive = true;

        public LauncherSimulation(Placement p)
        {
            Object = p.Clone();

            if (p.Route.Count < 2)
                throw new Exception("At least two waypoints are needed for this experimental preview.");
            if (p.Route.Count > 256)
                throw new Exception("Preview supports up to 256 waypoints.");

            foreach (var w in p.Route)
                if (new[] { w.AX, w.AY, w.BX, w.BY, w.Value }
                    .Any(v => float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 100000))
                    throw new Exception("Invalid or excessively large route value.");

            Build();

            // Start camera positioned such that the launcher's spawn sits
            // just inside the near edge of the activation band.
            StartCamera = p.Y * 40 - 20 - 72.20034 - 800;
            Reset();
        }

        public PointF Curve(float t)
        {
            int i = Math.Min(Object.Route.Count - 1, (int)t);
            float u = t - i;
            int j = (i + 1) % Object.Route.Count;
            var a = Object.Route[i];
            var b = Object.Route[j];
            float v = 1 - u;

            return new PointF(
                (v * v * v * a.X + 3 * v * v * u * a.BX + 3 * v * u * u * b.AX + u * u * u * b.X) * 40 + 20,
                (v * v * v * a.Y + 3 * v * v * u * a.BY + 3 * v * u * u * b.AY + u * u * u * b.Y) * 40 + 20);
        }

        private void Build()
        {
            Table.Add(0);
            float t = 0, d = 0;
            PointF last = Curve(0);
            int count = Object.Link != 0 ? Object.Route.Count : Object.Route.Count - 1;

            for (int segment = 0; segment < count; segment++)
            {
                int guard = 0;
                while (t < 1)
                {
                    var pt = Curve(segment + t);
                    double dx = pt.X - last.X, dy = pt.Y - last.Y;
                    d += (float)Math.Sqrt(dx * dx + dy * dy);
                    last = pt;
                    t = (float)(t + 0.001);

                    while (d >= 8)
                    {
                        Table.Add(segment + t);
                        d -= 8;
                        if (Table.Count > 200000)
                            throw new Exception("Route is too long for playback.");
                    }

                    if (++guard > 1100)
                        throw new Exception("Route sampling failed.");
                }
                t -= 1;
            }

            if (Table.Count < 3)
                throw new Exception("Route is too short or has coincident points.");
        }

        public PointF At(float progress, out int index)
        {
            float q = progress / 8;
            int i = Math.Min(Table.Count - 2, Math.Max(0, (int)q));
            float t = Table[i] + (Table[i + 1] - Table[i]) * (q - i);
            if (Object.Link == 0)
                t = Math.Min(Object.Route.Count - 1 - 0.000001f, t);
            index = (int)t;
            return Curve(t);
        }

        public void Reset()
        {
            Time = 0;
            Camera = StartCamera;
            X = Object.X * 40 + 20;
            Y = Object.Y * 40 - 20;
            Speed = 70;
            Wait = Math.Max(0, Object.Route[0].Value);
            State = 1;
            Index = 0;
            Progress = 0;
            Finished = false;
            Accelerating = false;
            ScriptsActive = true;
        }

        private bool Inside() =>
            Y - 72.20034 >= Camera + 16 && Y - 72.20034 <= Camera + 800;

        public void Step(float dt)
        {
            Time   += dt;
            Camera += CameraSpeed * dt;

            if (State == 1)
            {
                if (!Inside()) return;
                State = 0;
            }

            if (!ScriptsActive) return;

            if (Wait > 0)
            {
                Wait = Math.Max(0, Wait - dt);
                if (Wait == 0) { Speed = 0; Accelerating = true; }
            }
            else if (Finished)
            {
                if (Accelerating) Speed += 50 * dt;
                ScriptsActive = false;
                Accelerating = false;
            }
            else
            {
                if (Accelerating) { Speed += 50 * dt; if (Speed >= 70) Accelerating = false; }
                Progress += Speed * dt;

                if (Progress / 8 >= Table.Count - 1)
                {
                    if (Object.Link != 0) { Progress = 0; Index = 0; }
                    else                  { Finished = true; Progress = 0; Index = 0;
                                            Wait = Math.Max(0, Object.Route.Last().Value); }
                }

                if (!Finished)
                {
                    var pt = At(Progress, out int next);
                    X = pt.X;
                    Y = pt.Y;
                    if (next != Index)
                    {
                        Index = next;
                        Wait = Math.Max(0, Object.Route[Math.Min(next, Object.Route.Count - 1)].Value);
                    }
                }
            }

            if (State == 0 && !Inside()) State = 2;
        }
    }

    public class LauncherPlayback : Form
    {
        private readonly LauncherSimulation sim;
        private readonly Timer timer = new Timer();
        private readonly Panel plot  = new PlaybackPanel();
        private readonly Label info  = new Label();
        private readonly Button play = new Button();

        public LauncherPlayback(Placement p)
        {
            sim = new LauncherSimulation(p);

            Text = "Experimental launcher route playback";
            ClientSize = new Size(960, 720);
            MinimumSize = new Size(850, 600);
            BackColor = Color.FromArgb(22, 30, 39);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterParent;

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 85,
                Padding = new Padding(12),
                Text = "Launcher script approximation | selected object's route (read-only snapshot)\n" +
                       "Measured launcher radius and scrolling Y band only. No X/Z checks or firing simulation.\n" +
                       "Timing is approximate (up to 0.03 tile error in tested cycles). Once: after the final pause, weapon scripts deactivate.",
            };

            var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 80 };

            play.Text = "Play";
            play.Click += (s, e) => { timer.Enabled = !timer.Enabled; play.Text = timer.Enabled ? "Pause" : "Play"; };
            controls.Controls.Add(play);

            var reset = new Button { Text = "Reset" };
            reset.Click += (s, e) => { timer.Stop(); play.Text = "Play"; sim.Reset(); RefreshFrame(); };
            controls.Controls.Add(reset);

            controls.Controls.Add(new Label { Text = "Camera speed", AutoSize = true });
            var speed = new NumericUpDown { Minimum = 0, Maximum = 200, Value = 42, Width = 65 };
            speed.ValueChanged += (s, e) => { sim.CameraSpeed = (double)speed.Value; sim.Reset(); RefreshFrame(); };
            controls.Controls.Add(speed);

            controls.Controls.Add(new Label { Text = "Start camera Y", AutoSize = true });
            var start = new NumericUpDown { Minimum = -20000, Maximum = 200000, DecimalPlaces = 1, Width = 95,
                                          Value = (decimal)sim.StartCamera };
            start.ValueChanged += (s, e) => { sim.StartCamera = (double)start.Value; sim.Reset(); RefreshFrame(); };
            controls.Controls.Add(start);

            info.Dock = DockStyle.Bottom;
            info.Height = 65;
            info.Padding = new Padding(12);

            plot.Dock = DockStyle.Fill;
            plot.Paint += Draw;

            Controls.Add(plot);
            Controls.Add(info);
            Controls.Add(controls);
            Controls.Add(header);

            timer.Interval = 20;
            timer.Tick += (s, e) =>
            {
                for (int i = 0; i < 20; i++) sim.Step(0.001f);
                RefreshFrame();
                if (sim.Time >= 180) { timer.Stop(); play.Text = "Play"; }
            };
            RefreshFrame();
        }

        private void RefreshFrame()
        {
            info.Text = string.Format(
                "t={0:F2}s   Speed={1:F2}   Remaining wait={2:F2}s   World XY=({3:F1}, {4:F1})\n{5}",
                sim.Time, sim.Speed, sim.Wait, sim.X, sim.Y,
                !sim.ScriptsActive
                    ? "Scripts deactivated - weapon behavior stops | Camera: " + (sim.State == 2 ? "inactive" : "active")
                    : sim.State == 1
                        ? "Waiting for Y activation"
                        : sim.State == 2
                            ? "Inactive: native Shoot is blocked, even if movement continues"
                            : sim.Finished
                                ? "Route completed - final pause before script deactivation"
                                : "Active: shooting eligibility only; firing is not guaranteed");
            plot.Invalidate();
        }

        private void Draw(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Sample the route into screen points for auto-scaling.
            var pts = new List<PointF> { new PointF(sim.Object.X * 40 + 20, sim.Object.Y * 40 - 20) };
            int segments = sim.Object.Link != 0 ? sim.Object.Route.Count : sim.Object.Route.Count - 1;
            for (int i = 0; i < segments * 100; i++) pts.Add(sim.Curve(i / 100f));

            float xmin = pts.Min(p => p.X) - 80, xmax = pts.Max(p => p.X) + 80;
            float ymin = pts.Min(p => p.Y) - 100, ymax = pts.Max(p => p.Y) + 100;
            float scale = Math.Min((plot.Width - 40) / (xmax - xmin), (plot.Height - 40) / (ymax - ymin));

            Func<float, float, PointF> screen = (x, y) =>
                new PointF(20 + (x - xmin) * scale, plot.Height - 20 - (y - ymin) * scale);

            // Activation band (green fill between two limits).
            float top = screen(0, (float)(sim.Camera + 872.20034)).Y;
            float bot = screen(0, (float)(sim.Camera +  88.20034)).Y;
            using (var brush = new SolidBrush(Color.FromArgb(45, 50, 220, 140)))
                g.FillRectangle(brush, 20, Math.Max(0, top), plot.Width - 40,
                    Math.Max(0, Math.Min(plot.Height, bot) - Math.Max(0, top)));
            using (var pen = new Pen(Color.MediumAquamarine, 2))
            {
                g.DrawLine(pen, 20, top, plot.Width - 20, top);
                g.DrawLine(pen, 20, bot, plot.Width - 20, bot);
            }

            // Route polyline with dashed spawn-to-first.
            using (var pen = new Pen(Color.LightSteelBlue, 2))
            {
                g.DrawLines(pen, pts.Skip(1).Select(p => screen(p.X, p.Y)).ToArray());
                pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, screen(pts[0].X, pts[0].Y), screen(pts[1].X, pts[1].Y));
            }

            // Waypoint labels.
            for (int i = 0; i < sim.Object.Route.Count; i++)
            {
                var w = sim.Object.Route[i];
                var p = screen(w.X * 40 + 20, w.Y * 40 + 20);
                g.DrawString("WP " + (i + 1) + " / wait " + w.Value, Font, Brushes.White, p.X + 7, p.Y + 7);
            }

            // Current position dot.
            var dot = screen(sim.X, sim.Y);
            Brush dotBrush = sim.State == 2 ? Brushes.Salmon : sim.State == 1 ? Brushes.Gold : Brushes.LimeGreen;
            g.FillEllipse(dotBrush, dot.X - 8, dot.Y - 8, 16, 16);

            g.DrawString("Green shading: estimated active Y band | dashed line: spawn-to-route jump",
                         Font, Brushes.White, 12, 12);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        private class PlaybackPanel : Panel
        {
            public PlaybackPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}