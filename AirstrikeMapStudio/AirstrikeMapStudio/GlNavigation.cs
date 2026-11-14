using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        public Size NavigationMapSize => new Size(map.Width, map.Height);

        /// <summary>
        /// Centers the view on grid position (<paramref name="x"/>, <paramref name="y"/>).
        /// No-op while a drag or stroke is in progress.
        /// </summary>
        public void NavigateTo(double x, double y)
        {
            if (objectDrag || routeDrag || sculptCells != null || tileDraft != null || boxDragging)
                return;
            CenterX = Math.Max(0, Math.Min(map.Width  - 1, x));
            CenterY = Math.Max(0, Math.Min(map.Height - 1, y));
            Changed();
        }

        /// <summary>
        /// Frames either the entire scene or the current selection.
        /// </summary>
        public void FitNavigation(bool selection)
        {
            if (objectDrag || routeDrag || sculptCells != null || tileDraft != null || boxDragging)
                return;

            Rect3D bounds = Rect3D.Empty;

            if (selection)
            {
                var ids = SelectedObjects;
                if (ids.Length == 0) return;

                // Use model bounds where available, plus spawn point as a
                // fallback for markers (which have no geometry).
                foreach (var part in sceneryIds)
                    if (ids.Contains(part.Value)) bounds.Union(part.Key.Bounds);
                foreach (int id in ids)
                {
                    var p = map.Objects[id];
                    bounds.Union(new Point3D(MapWorld.SpawnX(p.X), MapWorld.SpawnY(p.Y), 0));
                }
            }
            else
            {
                bounds = scene.Bounds;
            }

            if (bounds.IsEmpty) return;

            CenterX = (bounds.X + bounds.SizeX / 2) / 40;
            CenterY = (bounds.Y + bounds.SizeY / 2) / 40;

            // The orbit target uses Z=0. Enclose the bounds around that target.
            double z = Math.Max(Math.Abs(bounds.Z), Math.Abs(bounds.Z + bounds.SizeZ));
            double radius = Math.Sqrt(bounds.SizeX * bounds.SizeX / 4
                                    + bounds.SizeY * bounds.SizeY / 4
                                    + z * z);
            Distance = Math.Max(100, radius / Math.Sin(Math.PI / 6) * 1.1);

            if (!selection) { Heading = -90; Pitch = 80; }

            Changed();
        }

        private static Brush MinimapCategoryBrush(string category)
        {
            switch (category)
            {
                case "Tanks":
                case "Turrets & cannons":
                    return Brushes. Tomato;
                case "Aircraft / Bosses":
                    return Brushes.DeepSkyBlue;
                case "Vehicles / launchers":
                    return Brushes.Orange;
                case "Pickups":
                    return Brushes.LimeGreen;
                case "Ships":
                    return Brushes.DodgerBlue;
                case "Buildings & structures":
                    return Brushes. RosyBrown;
                default:
                    return Brushes.SlateGray;
            }
        }

        /// <summary>
        /// Selects the placed object closest to the given map grid coordinates
        /// (used by Right-Click on the minimap).
        /// </summary>
        public bool SelectNearestOnMinimap(double mapX, double mapY, bool toggle)
        {
            int best = -1;
            double bestDistSq = 9.0; // within 3 grid cells
            for (int i = 0; i < map.Objects.Count; i++)
            {
                double dx = map.Objects[i].X - mapX;
                double dy = map.Objects[i].Y - mapY;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDistSq)
                {
                    bestDistSq = d2;
                    best = i;
                }
            }
            if (best < 0) return false;
            PickGroup(best, toggle);
            return true;
        }

        /// <summary>
        /// Renders the minimap: terrain elevation/water shading, color-coded
        /// dots per object category (gold with white outline if selected),
        /// selected route path, and camera footprint + crosshair.
        /// </summary>
        public void DrawNavigation(Graphics graphics, Rectangle area)
        {
            graphics.Clear(Color.FromArgb(20, 28, 36));
            if (area.Width <= 0 || area.Height <= 0 || map.Width <= 0 || map.Height <= 0) return;

            // 1. Coarse terrain height & water backdrop
            int stepX = Math.Max(1, map.Width / 64);
            int stepY = Math.Max(1, map.Height / 256);
            int waterStored = -1;
            if (objectWaterZ.HasValue && terrainProvider.HeightMaximum > terrainProvider.HeightMinimum)
            {
                waterStored = (int)Math.Round(
                    (objectWaterZ.Value - terrainProvider.HeightMinimum)
                    / (terrainProvider.HeightMaximum - terrainProvider.HeightMinimum) * 255.0);
            }

            for (int y = 0; y < map.Height; y += stepY)
            {
                float py0 = area.Bottom - (y + stepY) / (float)map.Height * area.Height;
                float py1 = area.Bottom - y / (float)map.Height * area.Height;
                float cellH = Math.Max(1f, py1 - py0);

                for (int x = 0; x < map.Width; x += stepX)
                {
                    int h = (int)(map.Cells[y * map.Width + x] & 0xFF);
                    uint tileSheet = (map.Cells[y * map.Width + x] >> 8) & 0xFF;
                    Color c;
                    if (waterStored >= 0 && h < waterStored)
                    {
                        int depth = Math.Max(18, 55 - (waterStored - h) / 4);
                        c = Color.FromArgb(18, depth, depth + 35);
                    }
                    else if (tileSheet > 0)
                    {
                        int v = 55 + h * 45 / 255;
                        c = Color.FromArgb(v + 12, v + 8, v);
                    }
                    else
                    {
                        int v = 28 + h * 52 / 255;
                        c = Color.FromArgb(v, v + 8, v + 4);
                    }

                    float px0 = area.Left + x / (float)map.Width * area.Width;
                    float px1 = area.Left + (x + stepX) / (float)map.Width * area.Width;
                    using (var b = new SolidBrush(c))
                        graphics.FillRectangle(b, px0, py0, Math.Max(1f, px1 - px0), cellH);
                }
            }

            graphics.DrawRectangle(Pens.SlateGray, area);

            var selected = new HashSet<int>(SelectedObjects);

            // 2. Draw non-selected objects color-coded by category
            for (int i = 0; i < map.Objects.Count; i++)
            {
                if (selected.Contains(i)) continue;
                var p = map.Objects[i];
                float x = area.Left + p.X / (float)map.Width  * area.Width;
                float y = area.Bottom - p.Y / (float)map.Height * area.Height;
                string cat = GlTerrainWindow.ObjectCategory(map.Types[p.Type]);
                graphics.FillRectangle(MinimapCategoryBrush(cat), x - 2, y - 2, 4, 4);
            }

            // 3. Draw selected objects and their routes on top
            foreach (int i in selected)
            {
                if (i < 0 || i >= map.Objects.Count) continue;
                var p = map.Objects[i];
                float sx = area.Left + p.X / (float)map.Width  * area.Width;
                float sy = area.Bottom - p.Y / (float)map.Height * area.Height;

                if (p.Route.Count > 0)
                {
                    float prevX = sx, prevY = sy;
                    foreach (var wp in p.Route)
                    {
                        float wx = area.Left + wp.X / (float)map.Width  * area.Width;
                        float wy = area.Bottom - wp.Y / (float)map.Height * area.Height;
                        graphics.DrawLine(Pens.MediumSpringGreen, prevX, prevY, wx, wy);
                        graphics.FillRectangle(Brushes.MediumSpringGreen, wx - 1.5f, wy - 1.5f, 3, 3);
                        prevX = wx;
                        prevY = wy;
                    }
                }

                graphics.FillRectangle(Brushes.Gold, sx - 3, sy - 3, 6, 6);
                graphics.DrawRectangle(Pens.White, sx - 3, sy - 3, 6, 6);
            }

            // 4. Camera footprint & center crosshair
            float cx = area.Left + (float)(CenterX / map.Width  * area.Width);
            float cy = area.Bottom - (float)(CenterY / map.Height * area.Height);
            float halfSpanX = Math.Max(4f, (float)(Distance / 40.0 * 0.55 / map.Width * area.Width));
            float halfSpanY = Math.Max(4f, (float)(Distance / 40.0 * 0.45 / map.Height * area.Height));
            using (var footprintPen = new Pen(Color.FromArgb(140, 102, 255, 204), 1f))
            {
                graphics.DrawRectangle(footprintPen, cx - halfSpanX, cy - halfSpanY, halfSpanX * 2, halfSpanY * 2);
            }
            graphics.DrawEllipse(Pens.Aquamarine, cx - 5, cy - 5, 10, 10);
            graphics.DrawLine(Pens.Aquamarine, cx - 9, cy, cx + 9, cy);
            graphics.DrawLine(Pens.Aquamarine, cx, cy - 9, cx, cy + 9);
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private void SetupNavigation(FlowLayoutPanel tools)
        {
            var waterAnimation = new CheckBox { Text = "Animate water (preview)", AutoSize = true };
            waterAnimation.CheckedChanged += (s, e) => Preview.AnimateWater = waterAnimation.Checked;
            tools.Controls.Add(waterAnimation);

            var fit = new Button { Text = "Fit map", AutoSize = true };
            var focus = new Button { Text = "Focus selection (Ctrl+F)", AutoSize = true };
            fit.Click   += (s, e) => Preview.FitNavigation(false);
            focus.Click += (s, e) => Preview.FitNavigation(true);
            tools.Controls.Add(fit);
            tools.Controls.Add(focus);
        }

        private void SetupMinimap(TabControl tabs)
        {
            var page = new TabPage("Map overview");
            var surface = new Panel { Dock = DockStyle.Fill };
            typeof(Panel).InvokeMember(
                "DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, surface, new object[] { true });

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(4),
                Text = "Left-drag: pan camera • Right-click: select object\r\n" +
                       "Gold: selected • Red: tanks/turrets • Blue: air/ships • Green: items",
            };

            Func<Rectangle> area = () =>
            {
                Size size = Preview.NavigationMapSize;
                double scale = Math.Min(
                    Math.Max(1, surface.Width  - 24) / (double)size.Width,
                    Math.Max(1, surface.Height - 24) / (double)size.Height);
                int width  = Math.Max(1, (int)(size.Width  * scale));
                int height = Math.Max(1, (int)(size.Height * scale));
                return new Rectangle((surface.Width - width) / 2, (surface.Height - height) / 2, width, height);
            };

            bool draggingMinimap = false;
            Action<MouseEventArgs> panFromMouse = e =>
            {
                Rectangle rect = area();
                if (rect.Width <= 0 || rect.Height <= 0) return;
                Size size = Preview.NavigationMapSize;
                double mx = (e.X - rect.Left) * size.Width / (double)rect.Width;
                double my = (rect.Bottom - e.Y) * size.Height / (double)rect.Height;
                Preview.NavigateTo(mx, my);
            };

            surface.Paint  += (s, e) => Preview.DrawNavigation(e.Graphics, area());
            surface.Resize += (s, e) => surface.Invalidate();

            surface.MouseDown += (s, e) =>
            {
                Rectangle rect = area();
                if (!rect.Contains(e.Location)) return;

                if (e.Button == MouseButtons.Left)
                {
                    draggingMinimap = true;
                    panFromMouse(e);
                }
                else if (e.Button == MouseButtons.Right)
                {
                    Size size = Preview.NavigationMapSize;
                    double mx = (e.X - rect.Left) * size.Width / (double)rect.Width;
                    double my = (rect.Bottom - e.Y) * size.Height / (double)rect.Height;
                    Preview.SelectNearestOnMinimap(mx, my, (ModifierKeys & Keys.Control) != 0);
                }
            };
            surface.MouseMove += (s, e) =>
            {
                if (draggingMinimap && (e.Button & MouseButtons.Left) != 0)
                    panFromMouse(e);
            };
            surface.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    draggingMinimap = false;
            };

            Preview.ViewChanged      += (s, e) => surface.Invalidate();
            Preview.SelectionChanged += (s, e) => surface.Invalidate();

            page.Controls.Add(surface);
            page.Controls.Add(hint);
            tabs.TabPages.Add(page);
        }
    }
}