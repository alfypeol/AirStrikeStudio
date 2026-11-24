using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        [DllImport("opengl32.dll")]
        private static extern void glScissor(int x, int y, int width, int height);

        public int RouteDisplayScope;
        public bool ShowRoute = true;
        public int RouteWaypoint = -1;
        public event EventHandler RouteScopeChanged;

        public void SetRouteDisplayScope(int scope)
        {
            scope = Math.Max(0, Math.Min(2, scope));
            if (RouteDisplayScope == scope) return;
            RouteDisplayScope = scope;
            RouteScopeChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }

        // --- ROUTE PREVIEW ANIMATION ---
        public bool PreviewRoute = false;
        private double routeAnimTime = 0;
        // -------------------------------

        private readonly Dictionary<int, List<Point3D[]>> contextRoutes =
            new Dictionary<int, List<Point3D[]>>();
        private Map cachedRouteMap;
        private int drawingRouteOwner = -1;
        private int routeOwner = -1;

        private Dictionary<string, double> routeHeights = new Dictionary<string, double>();

        public int[] VisibleRouteObjects()
        {
            var ids = new List<int>();
            if (!ShowRoute) return ids.ToArray();

            var selected = new HashSet<int>(SelectedObjects);
            for (int i = 0; i < map.Objects.Count; i++)
            {
                if (map.Objects[i].Route.Count == 0) continue;
                bool visible =
                    RouteDisplayScope == 2
                    || (RouteDisplayScope == 1 && selected.Contains(i))
                    || (RouteDisplayScope == 0 && i == SelectedIndex);
                if (visible) ids.Add(i);
            }
            return ids.ToArray();
        }

        private List<Point3D[]> ContextRoute(int index)
        {
            if (!ReferenceEquals(cachedRouteMap, map))
            {
                contextRoutes.Clear();
                cachedRouteMap = map;
            }
            if (contextRoutes.TryGetValue(index, out var cached)) return cached;

            var result = new List<Point3D[]>();
            var owner = map.Objects[index];

            drawingRouteOwner = index;
            try
            {
                for (int i = 0; i + 1 < owner.Route.Count; i++)
                {
                    var a = owner.Route[i];
                    var b = owner.Route[i + 1];
                    int steps = RouteSamples(a, b);
                    var points = new Point3D[steps + 1];

                    for (int j = 0; j <= steps; j++)
                    {
                        double t = j / (double)steps;
                        double u = 1 - t;
                        double x = u * u * u * a.X + 3 * u * u * t * a.BX
                                 + 3 * u * t * t * b.AX + t * t * t * b.X;
                        double y = u * u * u * a.Y + 3 * u * u * t * a.BY
                                 + 3 * u * t * t * b.AY + t * t * t * b.Y;
                        points[j] = new Point3D(
                            x, y,
                            RouteFinite(x) && RouteFinite(y) ? RouteZ(x, y) : 0);
                    }
                    result.Add(points);
                }

                var first = owner.Route[0];
                result.Insert(0, new[]
                {
                    new Point3D(owner.X, owner.Y - 1, RouteZ(owner.X, owner.Y - 1)),
                    new Point3D(first.X,  first.Y,    RouteZ(first.X,  first.Y)),
                });
            }
            finally
            {
                drawingRouteOwner = -1;
            }

            contextRoutes[index] = result;
            return result;
        }

        private void DrawContextRoutes()
        {
            Color[] colors =
            {
                Color.CornflowerBlue, Color.Orange, Color.Orchid,
                Color.Yellow,         Color.Salmon, Color.LightSkyBlue,
            };

            foreach (int index in VisibleRouteObjects())
            {
                if (index == SelectedIndex) continue;

                Color color = colors[index % colors.Length];
                glColor4f(color.R / 255f, color.G / 255f, color.B / 255f, 1);

                var segments = ContextRoute(index);
                for (int k = 0; k < segments.Count; k++)
                {
                    var points = segments[k];
                    PointF previous;
                    bool valid = RouteScreen(points[0].X, points[0].Y, out previous, points[0].Z);

                    for (int j = 1; j < points.Length; j++)
                    {
                        PointF next;
                        bool ok = RouteScreen(points[j].X, points[j].Y, out next, points[j].Z);

                        if (valid && ok)
                        {
                            if (k == 0)
                            {
                                for (int d = 0; d < 20; d += 2)
                                {
                                    RouteLine(
                                        new PointF(
                                            previous.X + (next.X - previous.X) * d / 20f,
                                            previous.Y + (next.Y - previous.Y) * d / 20f),
                                        new PointF(
                                            previous.X + (next.X - previous.X) * (d + 1) / 20f,
                                            previous.Y + (next.Y - previous.Y) * (d + 1) / 20f));
                                }
                            }
                            else
                            {
                                RouteLine(previous, next);
                            }

                            if (j == Math.Max(1, points.Length / 2))
                                RouteArrow(previous, next);
                        }

                        previous = next;
                        valid = ok;
                    }
                }

                // Draw small waypoint markers along context routes
                var owner = map.Objects[index];
                foreach (var wp in owner.Route)
                {
                    if (RouteScreen(wp.X, wp.Y, out PointF wpScreen))
                        RouteBox(wpScreen, 2.5f);
                }
            }
        }

        public string[] RouteRows()
        {
            if (routeOwner != SelectedIndex)
            {
                routeOwner = SelectedIndex;
                RouteWaypoint = -1;
            }

            if (SelectedIndex < 0) return new string[0];

            var result = new List<string>();
            var p = map.Objects[SelectedIndex];
            for (int i = 0; i < p.Route.Count; i++)
            {
                var w = p.Route[i];
                string modeDesc = w.Mode == 0 ? "Free" : "Smooth";
                result.Add(
                    (i + 1) + "  (" + w.X + ", " + w.Y + ")  wait=" +
                    w.Value.ToString("G", CultureInfo.InvariantCulture) +
                    "  [" + modeDesc + "]");
            }
            return result.ToArray();
        }

        private static bool RouteFinite(double v) => RouteMath.IsFinite(v);

        private double RouteZ(double x, double y)
        {
            int owner = drawingRouteOwner >= 0 ? drawingRouteOwner : SelectedIndex;

            if (owner >= 0
                && objectWaterZ.HasValue
                && waterObjects.Contains(map.Types[map.Objects[owner].Type]))
                return objectWaterZ.Value + 5;

            return SurfaceZ(x + 0.5, y + 0.5) + 5;
        }

        private double SurfaceZ(double x, double y)
        {
            x = Math.Max(0, Math.Min(map.Width, x));
            y = Math.Max(0, Math.Min(map.Height, y));

            int ix = Math.Min(map.Width - 1, (int)Math.Floor(x));
            int iy = Math.Min(map.Height - 1, (int)Math.Floor(y));

            if (!routeHeights.TryGetValue(ix + "," + iy, out double a)
                || !routeHeights.TryGetValue((ix + 1) + "," + iy, out double b)
                || !routeHeights.TryGetValue(ix + "," + (iy + 1), out double c)
                || !routeHeights.TryGetValue((ix + 1) + "," + (iy + 1), out double d))
                return 0;

            double tx = x - ix, ty = y - iy;
            return RouteMath.InterpolateSurfaceZ(a, b, c, d, tx, ty);
        }

        private static int RouteSamples(Waypoint a, Waypoint b) => RouteMath.SegmentSampleCount(a, b);

        private bool RouteScreen(double x, double y, out PointF screen, double? height = null)
        {
            screen = new PointF();
            if (!RouteFinite(x) || !RouteFinite(y)) return false;

            double a = Heading * Math.PI / 180;
            double p = Pitch * Math.PI / 180;

            var offset = new Vector3D(
                Math.Cos(a) * Math.Cos(p),
                Math.Sin(a) * Math.Cos(p),
                Math.Sin(p));
            var eye = new Point3D(CenterX * 40, CenterY * 40, 0) + offset * Distance;
            var delta = new Point3D(
                MapWorld.RouteX(x),
                MapWorld.RouteY(y),
                height ?? RouteZ(x, y)) - eye;

            double depth = Vector3D.DotProduct(delta, -offset);
            if (depth < 1) return false;

            var right = new Vector3D(-Math.Sin(a), Math.Cos(a), 0);
            var up = new Vector3D(
                -Math.Cos(a) * Math.Sin(p),
                -Math.Sin(a) * Math.Sin(p),
                 Math.Cos(p));

            int vw = Math.Max(1, ClientSize.Width);
            int vh = Math.Max(1, ClientSize.Height);
            double aspect = (double)vw / vh;

            double sx = Vector3D.DotProduct(delta, right) / (depth * Math.Tan(Math.PI / 6) * aspect);
            double sy = Vector3D.DotProduct(delta, up) / (depth * Math.Tan(Math.PI / 6));

            screen = new PointF(
                (float)((sx + 1) * vw / 2),
                (float)((1 - sy) * vh / 2));

            return Math.Abs(screen.X) < 1e6 && Math.Abs(screen.Y) < 1e6;
        }

        private static void RouteLine(PointF a, PointF b)
        {
            glBegin(GL_LINES);
            glVertex3f(a.X, a.Y, 0);
            glVertex3f(b.X, b.Y, 0);
            glEnd();
        }

        private static void RouteBox(PointF p, float r)
        {
            glBegin(GL_QUADS);
            glVertex3f(p.X - r, p.Y - r, 0);
            glVertex3f(p.X + r, p.Y - r, 0);
            glVertex3f(p.X + r, p.Y + r, 0);
            glVertex3f(p.X - r, p.Y + r, 0);
            glEnd();
        }

        private static void RouteNumber(int number, PointF p)
        {
            int[] masks = { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };
            float[,] seg =
            {
                {0, 0, 6,  0}, {6,  0, 6,  5}, {6,  5, 6, 10},
                {0,10, 6, 10}, {0,  5, 0, 10}, {0,  0, 0,  5},
                {0, 5, 6,  5},
            };

            float x = p.X + 9, y = p.Y - 21;

            glColor4f(0.03f, 0.06f, 0.08f, 1);
            glBegin(GL_QUADS);
            glVertex3f(x - 3, y - 3, 0);
            glVertex3f(x + number.ToString().Length * 16, y - 3, 0);
            glVertex3f(x + number.ToString().Length * 16, y + 21, 0);
            glVertex3f(x - 3, y + 21, 0);
            glEnd();

            glColor4f(0.5f, 1, 0.85f, 1);
            foreach (char digit in number.ToString())
            {
                int mask = masks[digit - '0'];
                for (int i = 0; i < 7; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    RouteLine(
                        new PointF(x + seg[i, 0] * 1.6f, y + seg[i, 1] * 1.6f),
                        new PointF(x + seg[i, 2] * 1.6f, y + seg[i, 3] * 1.6f));
                }
                x += 16;
            }
        }

        private static void RouteArrow(PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.1) return;

            float ux = (float)(dx / len);
            float uy = (float)(dy / len);

            RouteLine(b, new PointF(b.X - ux * 8 - uy * 4, b.Y - uy * 8 + ux * 4));
            RouteLine(b, new PointF(b.X - ux * 8 + uy * 4, b.Y - uy * 8 - ux * 4));
        }

        private void DrawRouteOverlay()
        {
            if (!ShowRoute || objectDrag) return;
            if (VisibleRouteObjects().Length == 0) return;

            var p = SelectedIndex < 0 ? null : (routeDrag ? routeDraft : map.Objects[SelectedIndex]);

            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);

            glEnable(GL_SCISSOR_TEST);
            glScissor(0, 0, w, h);
            glViewport(0, 0, w, h);
            glMatrixMode(GL_PROJECTION);
            glPushMatrix();
            glLoadIdentity();
            glOrtho(0, w, h, 0, -1, 1);
            glMatrixMode(GL_MODELVIEW);
            glPushMatrix();
            glLoadIdentity();
            glDisable(GL_DEPTH_TEST);
            glDisable(GL_TEXTURE_2D);
            glDisable(GL_FOG);
            glColor4f(0.3f, 1, 0.8f, 1);

            DrawContextRoutes();

            glColor4f(0.3f, 1, 0.8f, 1);
            if (p != null && p.Route.Count > 0)
            {
                PointF spawn, first;
                if (RouteScreen(p.X, p.Y - 1, out spawn) && RouteScreen(p.Route[0].X, p.Route[0].Y, out first))
                {
                    for (int i = 0; i < 20; i += 2)
                    {
                        RouteLine(
                            new PointF(spawn.X + (first.X - spawn.X) * i / 20f,
                                       spawn.Y + (first.Y - spawn.Y) * i / 20f),
                            new PointF(spawn.X + (first.X - spawn.X) * (i + 1) / 20f,
                                       spawn.Y + (first.Y - spawn.Y) * (i + 1) / 20f));
                    }
                    RouteArrow(spawn, first);
                }

                for (int i = 0; i < p.Route.Count; i++)
                {
                    var wp = p.Route[i];
                    PointF at;
                    if (!RouteScreen(wp.X, wp.Y, out at)) continue;

                    glColor4f(0.3f, 1, 0.8f, 1);
                    RouteBox(at, 4);
                    RouteNumber(i + 1, at);

                    if (i + 1 < p.Route.Count)
                    {
                        var n = p.Route[i + 1];
                        PointF prev = at;
                        bool valid = true;
                        int steps = RouteSamples(wp, n);

                        for (int j = 1; j <= steps; j++)
                        {
                            double t = j / (double)steps, u = 1 - t;
                            double bx = u * u * u * wp.X + 3 * u * u * t * wp.BX
                                      + 3 * u * t * t * n.AX + t * t * t * n.X;
                            double by = u * u * u * wp.Y + 3 * u * u * t * wp.BY
                                      + 3 * u * t * t * n.AY + t * t * t * n.Y;
                            PointF next;
                            bool ok = RouteScreen(bx, by, out next);

                            if (ok && valid)
                            {
                                RouteLine(prev, next);
                                if (j == steps / 2) RouteArrow(prev, next);
                            }
                            prev = next;
                            valid = ok;
                        }
                    }

                    if (i == RouteWaypoint)
                    {
                        PointF a, b;
                        // Handle A: Arrival / incoming handle (Cyan)
                        glColor4f(0.3f, 0.95f, 0.85f, 1);
                        if (RouteScreen(wp.AX, wp.AY, out a))
                        {
                            RouteLine(at, a);
                            RouteBox(a, routeHandle == 0 && routeDrag ? 4.5f : 3);
                        }

                        // Handle B: Departure / outgoing handle (Coral pink)
                        glColor4f(1f, 0.45f, 0.65f, 1);
                        if (RouteScreen(wp.BX, wp.BY, out b))
                        {
                            RouteLine(at, b);
                            RouteBox(b, routeHandle == 1 && routeDrag ? 4.5f : 3);
                        }

                        // Center point: Amber for Free (Mode 0), Magenta for Smooth (Mode 1)
                        if (wp.Mode == 0)
                            glColor4f(1f, 0.85f, 0.2f, 1);
                        else
                            glColor4f(1f, 0.4f, 0.9f, 1);
                        RouteBox(at, 7);
                    }
                }

                // --- ROUTE PREVIEW ANIMATION (Fake 3D Sphere) ---
                if (PreviewRoute && p.Route.Count > 1)
                {
                    double totalLength = 0;
                    var lengths = new double[p.Route.Count - 1];

                    for (int i = 0; i < p.Route.Count - 1; i++)
                    {
                        var a = p.Route[i];
                        var b = p.Route[i + 1];
                        int steps = 50;
                        double len = 0;
                        PointF prevPt = RouteScreen3D(a.X, a.Y, RouteZ(a.X, a.Y));
                        for (int j = 1; j <= steps; j++)
                        {
                            double t = j / (double)steps;
                            double u = 1 - t;
                            double x = u * u * u * a.X + 3 * u * u * t * a.BX + 3 * u * t * t * b.AX + t * t * t * b.X;
                            double y = u * u * u * a.Y + 3 * u * u * t * a.BY + 3 * u * t * t * b.AY + t * t * t * b.Y;
                            PointF cur = RouteScreen3D(x, y, RouteZ(x, y));
                            len += Math.Sqrt((cur.X - prevPt.X) * (cur.X - prevPt.X) + (cur.Y - prevPt.Y) * (cur.Y - prevPt.Y));
                            prevPt = cur;
                        }
                        lengths[i] = len;
                        totalLength += len;
                    }

                    double speed = 200.0;
                    double dist = (routeAnimTime * speed) % totalLength;
                    int segment = 0;
                    while (segment < lengths.Length - 1 && dist > lengths[segment])
                    {
                        dist -= lengths[segment];
                        segment++;
                    }

                    double segmentT = lengths[segment] > 0 ? dist / lengths[segment] : 0;
                    var sa = p.Route[segment];
                    var sb = p.Route[segment + 1];
                    double st = segmentT, su = 1 - st;
                    double sx = su * su * su * sa.X + 3 * su * su * st * sa.BX + 3 * su * st * st * sb.AX + st * st * st * sb.X;
                    double sy = su * su * su * sa.Y + 3 * su * su * st * sa.BY + 3 * su * st * st * sb.AY + st * st * st * sb.Y;

                    PointF dotPos = RouteScreen3D(sx, sy, RouteZ(sx, sy));

                    glColor4f(0.1f, 0.1f, 0.1f, 1); // Dark outline
                    RouteBox(dotPos, 12);

                    glColor4f(1.0f, 0.6f, 0.0f, 1); // Orange body
                    RouteBox(dotPos, 9);

                    glColor4f(1.0f, 1.0f, 0.4f, 1); // Yellow highlight
                    RouteBox(new PointF(dotPos.X - 2, dotPos.Y - 2), 5);

                    glColor4f(0.3f, 1, 0.8f, 1);
                }
                // ------------------------------------------------
            }

            // Live click-to-append waypoint preview in Edit Route mode
            if (EditRoute && !routeDrag && routeHover && routeHoverPick < 0
                && p != null && (AppendRouteOnClick || (ModifierKeys & Keys.Shift) != 0))
            {
                double fromX = p.Route.Count > 0 ? p.Route[p.Route.Count - 1].X : p.X;
                double fromY = p.Route.Count > 0 ? p.Route[p.Route.Count - 1].Y : p.Y - 1;
                if (RouteScreen(fromX, fromY, out PointF startPt)
                    && RouteScreen(routeHoverX, routeHoverY, out PointF endPt))
                {
                    glColor4f(1.0f, 0.85f, 0.25f, 0.95f);
                    for (int i = 0; i < 16; i += 2)
                    {
                        RouteLine(
                            new PointF(startPt.X + (endPt.X - startPt.X) * i / 16f,
                                       startPt.Y + (endPt.Y - startPt.Y) * i / 16f),
                            new PointF(startPt.X + (endPt.X - startPt.X) * (i + 1) / 16f,
                                       startPt.Y + (endPt.Y - startPt.Y) * (i + 1) / 16f));
                    }
                    RouteArrow(startPt, endPt);
                    RouteBox(endPt, 5);
                    RouteNumber(p.Route.Count + 1, endPt);
                }
            }

            glPopMatrix();
            glMatrixMode(GL_PROJECTION);
            glPopMatrix();
            glMatrixMode(GL_MODELVIEW);
            glEnable(GL_DEPTH_TEST);
            glDisable(GL_SCISSOR_TEST);
        }

        private PointF RouteScreen3D(double x, double y, double z)
        {
            double a = Heading * Math.PI / 180;
            double p = Pitch * Math.PI / 180;

            var offset = new Vector3D(
                Math.Cos(a) * Math.Cos(p),
                Math.Sin(a) * Math.Cos(p),
                Math.Sin(p));
            var eye = new Point3D(CenterX * 40, CenterY * 40, 0) + offset * Distance;
            var delta = new Point3D(MapWorld.RouteX(x), MapWorld.RouteY(y), z) - eye;
            double depth = Vector3D.DotProduct(delta, -offset);
            if (depth < 1) return new PointF();

            var right = new Vector3D(-Math.Sin(a), Math.Cos(a), 0);
            var up = new Vector3D(
                -Math.Cos(a) * Math.Sin(p),
                -Math.Sin(a) * Math.Sin(p),
                 Math.Cos(p));

            int vw = Math.Max(1, ClientSize.Width);
            int vh = Math.Max(1, ClientSize.Height);
            double aspect = (double)vw / vh;

            double sx = Vector3D.DotProduct(delta, right) / (depth * Math.Tan(Math.PI / 6) * aspect);
            double sy = Vector3D.DotProduct(delta, up) / (depth * Math.Tan(Math.PI / 6));

            return new PointF(
                (float)((sx + 1) * vw / 2),
                (float)((1 - sy) * vh / 2));
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private readonly ListBox routeList = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        private bool updatingRoutes;
        private Label routeHeaderTitle;
        private Button routeCollapseBtn;
        private Button routeClearBtn;

        private void SetupRouteList(Panel panel)
        {
            var routePanel = new Panel { Dock = DockStyle.Bottom, Height = 195 };

            var routeHeaderBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(240, 243, 248),
            };
            routeHeaderTitle = new Label
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 50, 70),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Waypoints",
            };
            routeCollapseBtn = new Button
            {
                Dock = DockStyle.Right,
                Text = "Hide ▲",
                AutoSize = true,
                MinimumSize = new Size(58, 25),
                Font = new Font("Segoe UI", 8f),
                Margin = new Padding(1),
            };
            routeClearBtn = new Button
            {
                Dock = DockStyle.Right,
                Text = "Clear Route",
                AutoSize = true,
                MinimumSize = new Size(76, 25),
                Font = new Font("Segoe UI", 8f),
                Margin = new Padding(1),
            };

            routeCollapseBtn.Click += (s, e) =>
            {
                userForcedRouteCollapse = true;
                SetWaypointPanelCollapsed(true);
            };

            routeClearBtn.Click += (s, e) =>
            {
                if (Preview.SelectedObjects.Length > 1)
                {
                    if (MessageBox.Show(this, "Clear waypoints for all selected objects?", "Clear Route", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        Preview.ClearSelectedRoute();
                    }
                }
                else
                {
                    if (MessageBox.Show(this, "Clear all waypoints from this object's route?", "Clear Route", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        Preview.ClearSelectedRoute();
                    }
                }
            };

            routeHeaderBar.Controls.Add(routeHeaderTitle);
            routeHeaderBar.Controls.Add(routeClearBtn);
            routeHeaderBar.Controls.Add(routeCollapseBtn);
            routePanel.Controls.Add(routeHeaderBar);

            var routeHint = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = SystemColors.GrayText,
                Text = "Select row for handles • Drag points / pink handles in 3D",
            };

            var toolsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                WrapContents = false,
                Padding = new Padding(2, 2, 2, 2),
                Margin = Padding.Empty,
            };

            var add = new Button { Text = "+ WP", AutoSize = true, MinimumSize = new Size(42, 26), Margin = new Padding(1), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var remove = new Button { Text = "− WP", AutoSize = true, MinimumSize = new Size(42, 26), Margin = new Padding(1), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var up = new Button { Text = "↑", Width = 28, Height = 26, Margin = new Padding(1), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var down = new Button { Text = "↓", Width = 28, Height = 26, Margin = new Padding(1), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var precise = new Button { Text = "Edit…", AutoSize = true, MinimumSize = new Size(48, 26), Margin = new Padding(1), Font = new Font("Segoe UI", 8.5f) };
            var routeToolsBtn = new Button { Text = "Route Tools ▾", AutoSize = true, MinimumSize = new Size(96, 26), Margin = new Padding(1), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };

            var tip = new ToolTip();
            tip.SetToolTip(add, "Add new waypoint to route");
            tip.SetToolTip(remove, "Remove selected waypoint");
            tip.SetToolTip(up, "Move selected waypoint earlier in sequence");
            tip.SetToolTip(down, "Move selected waypoint later in sequence");
            tip.SetToolTip(precise, "Edit waypoint coordinates and mode (or double-click row)");
            tip.SetToolTip(routeToolsBtn, "Additional route tools: curves, timing, tangent handles, export/import");

            var routeMenu = new ContextMenuStrip();

            var menuWaitSelected = new ToolStripMenuItem("Set Selected Point Wait…");
            var menuWaitAll = new ToolStripMenuItem("Set All Waypoints Wait…");

            var menuFree = new ToolStripMenuItem("Tangent Handles: Free (Independent)");
            var menuMirror = new ToolStripMenuItem("Tangent Handles: Mirrored (Symmetrical)");

            var menuSmooth = new ToolStripMenuItem("Smooth Bezier Curve");
            var menuSmoothSharp = new ToolStripMenuItem("Smooth Bezier Curve (Sharp Corners)");
            var menuReverse = new ToolStripMenuItem("Reverse Route Direction");
            var menuRotate = new ToolStripMenuItem("Rotate Route…");

            var menuExportCsv = new ToolStripMenuItem("Export Route to CSV…");
            var menuImportCsv = new ToolStripMenuItem("Import Route from CSV…");

            routeMenu.Items.Add(menuWaitSelected);
            routeMenu.Items.Add(menuWaitAll);
            routeMenu.Items.Add(new ToolStripSeparator());
            routeMenu.Items.Add(menuFree);
            routeMenu.Items.Add(menuMirror);
            routeMenu.Items.Add(new ToolStripSeparator());
            routeMenu.Items.Add(menuSmooth);
            routeMenu.Items.Add(menuSmoothSharp);
            routeMenu.Items.Add(menuReverse);
            routeMenu.Items.Add(menuRotate);
            routeMenu.Items.Add(new ToolStripSeparator());
            routeMenu.Items.Add(menuExportCsv);
            routeMenu.Items.Add(menuImportCsv);

            routeToolsBtn.Click += (s, e) =>
            {
                routeMenu.Show(routeToolsBtn, new Point(0, routeToolsBtn.Height));
            };
            routeList.ContextMenuStrip = routeMenu;

            routeMenu.Opening += (s, e) =>
            {
                if (Preview.RouteWaypoint >= 0)
                {
                    var draft = Preview.SelectedDraft();
                    if (draft != null && draft.Route != null && Preview.RouteWaypoint < draft.Route.Count)
                    {
                        var wp = draft.Route[Preview.RouteWaypoint];
                        menuFree.Checked = (wp.Mode == 0);
                        menuMirror.Checked = (wp.Mode == 1);
                    }
                }
            };

            precise.Click += (s, e) => EditRoutePoint();
            routeList.DoubleClick += (s, e) => EditRoutePoint();

            menuFree.Click += (s, e) =>
            {
                Preview.SetCurrentWaypointMode(0);
                routeList.Items.Clear();
                routeList.Items.AddRange(Preview.RouteRows());
            };
            menuMirror.Click += (s, e) =>
            {
                Preview.SetCurrentWaypointMode(1);
                routeList.Items.Clear();
                routeList.Items.AddRange(Preview.RouteRows());
            };
            menuSmooth.Click += (s, e) => Preview.SmoothSelectedRoute(false);
            menuSmoothSharp.Click += (s, e) => Preview.SmoothSelectedRoute(true);
            menuReverse.Click += (s, e) => Preview.ReverseSelectedRoute();
            menuRotate.Click += (s, e) => RotateRouteDialog();
            menuWaitAll.Click += (s, e) => BulkRouteWaitDialog();
            menuExportCsv.Click += (s, e) => TriggerExportRouteCsv();
            menuImportCsv.Click += (s, e) => TriggerImportRouteCsv();

            Action openWaitDialog = () =>
            {
                if (Preview.RouteWaypoint < 0) return;

                using (var dialog = new Form
                {
                    Text = "Waypoint wait — interpretation depends on script",
                    Width = 430,
                    Height = 150,
                    StartPosition = FormStartPosition.CenterParent,
                })
                {
                    var input = new TextBox
                    {
                        Left = 15,
                        Top = 15,
                        Width = 380,
                        Text = Preview.CurrentRouteWait()
                            .ToString(System.Globalization.CultureInfo.CurrentCulture),
                    };
                    var apply = new Button
                    {
                        Text = "Apply",
                        Left = 300,
                        Top = 50,
                        DialogResult = DialogResult.OK,
                    };
                    dialog.Controls.Add(input);
                    dialog.Controls.Add(apply);
                    dialog.AcceptButton = apply;

                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        float value;
                        if (!float.TryParse(input.Text, out value)
                            || float.IsNaN(value) || float.IsInfinity(value) || value < 0)
                        {
                            MessageBox.Show(this, "Enter a finite, non-negative wait value.");
                        }
                        else
                        {
                            Preview.SetRouteWait(value);
                        }
                    }
                }
            };
            menuWaitSelected.Click += (s, e) => openWaitDialog();

            toolsPanel.Controls.AddRange(new Control[]
            {
                add, remove, up, down, precise, routeToolsBtn
            });

            routePanel.Controls.Add(routeHeaderBar);
            routePanel.Controls.Add(routeHint);
            routePanel.Controls.Add(toolsPanel);
            routePanel.Controls.Add(routeList);

            routeHeaderBar.SendToBack();
            routeHint.SendToBack();
            toolsPanel.SendToBack();
            routeList.BringToFront();

            panel.Controls.Add(routePanel);

            add.Click += (s, e) => Preview.AddRouteWaypoint();
            remove.Click += (s, e) => Preview.DeleteRouteWaypoint();
            up.Click += (s, e) => Preview.ReorderRouteWaypoint(-1);
            down.Click += (s, e) => Preview.ReorderRouteWaypoint(+1);

            Action updateTools = () =>
            {
                bool selected = Preview.SelectedObjects.Length == 1;
                bool anySelected = Preview.SelectedObjects.Length >= 1;
                int count = routeList.Items.Count;
                bool hasPoint = selected
                             && Preview.RouteWaypoint >= 0
                             && Preview.RouteWaypoint < count;

                if (routeHeaderTitle != null && !routeHeaderTitle.IsDisposed)
                    routeHeaderTitle.Text = count > 0 ? $"Waypoints ({count} pts)" : "Waypoints (None)";
                if (routeClearBtn != null && !routeClearBtn.IsDisposed)
                    routeClearBtn.Enabled = (selected && count > 0) || Preview.SelectedObjects.Length > 1;

                add.Enabled = selected;
                remove.Enabled = precise.Enabled = hasPoint;
                up.Enabled = hasPoint && Preview.RouteWaypoint > 0;
                down.Enabled = hasPoint && Preview.RouteWaypoint + 1 < count;
                routeToolsBtn.Enabled = anySelected;

                menuWaitSelected.Enabled = hasPoint;
                menuWaitAll.Enabled = (selected && count > 0) || Preview.SelectedObjects.Length > 1;
                menuFree.Enabled = menuMirror.Enabled = hasPoint;
                menuSmooth.Enabled = menuSmoothSharp.Enabled = (selected && count > 0) || Preview.SelectedObjects.Length > 1;
                menuReverse.Enabled = (selected && count >= 2) || Preview.SelectedObjects.Length > 1;
                menuRotate.Enabled = Preview.HasRoutableSelection();
                menuExportCsv.Enabled = (selected && count > 0) || Preview.SelectedObjects.Length > 1;
                menuImportCsv.Enabled = anySelected;
            };

            routeList.SelectedIndexChanged += (s, e) => updateTools();
            Preview.SelectionChanged += (s, e) => updateTools();
            updateTools();

            routeList.SelectedIndexChanged += (s, e) =>
            {
                if (updatingRoutes) return;
                Preview.RouteWaypoint = routeList.SelectedIndex;
                Preview.Changed();
                updateTools();
            };

            Preview.SelectionChanged += (s, e) =>
            {
                updatingRoutes = true;
                routeList.Items.Clear();
                routeList.Items.AddRange(Preview.RouteRows());
                if (Preview.RouteWaypoint >= 0 && Preview.RouteWaypoint < routeList.Items.Count)
                    routeList.SelectedIndex = Preview.RouteWaypoint;
                updatingRoutes = false;
            };
        }

        public void BulkRouteWaitDialog()
        {
            if (Preview.SelectedObjects.Length == 0) return;

            using (var dialog = new Form
            {
                Text = "Bulk edit waypoint wait values",
                Width = 420,
                Height = 205,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var modeSet = new RadioButton
                {
                    Text = "Set all waypoint waits to exact value:",
                    Left = 16,
                    Top = 16,
                    Width = 360,
                    Checked = true,
                };
                var modeScale = new RadioButton
                {
                    Text = "Multiply all waypoint waits by factor (e.g. 0.5 or 1.5):",
                    Left = 16,
                    Top = 42,
                    Width = 360,
                };
                var input = new TextBox
                {
                    Left = 16,
                    Top = 74,
                    Width = 370,
                    Text = "0",
                };
                var cancel = new Button
                {
                    Text = "Cancel",
                    Left = 220,
                    Top = 115,
                    Width = 80,
                    DialogResult = DialogResult.Cancel,
                };
                var apply = new Button
                {
                    Text = "Apply",
                    Left = 306,
                    Top = 115,
                    Width = 80,
                };

                modeScale.CheckedChanged += (s, e) =>
                {
                    if (modeScale.Checked && input.Text == "0") input.Text = "1";
                };

                apply.Click += (s, e) =>
                {
                    if (!float.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float val)
                        && !float.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                    {
                        MessageBox.Show(dialog, "Enter a valid number.");
                        return;
                    }
                    try
                    {
                        Preview.ApplyBulkWait(val, modeScale.Checked);
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, "Bulk wait edit");
                    }
                };

                dialog.Controls.AddRange(new Control[] { modeSet, modeScale, input, apply, cancel });
                dialog.AcceptButton = apply;
                dialog.CancelButton = cancel;
                dialog.ShowDialog(this);
            }
        }

        public void RotateRouteDialog()
        {
            if (!Preview.HasRoutableSelection()) return;

            int selCount = Preview.SelectedObjects.Length;
            var single = selCount == 1 ? Preview.SelectedDraft() : null;

            using (var dialog = new Form
            {
                Text = selCount > 1 ? "Rotate routes for selected objects" : "Rotate object waypoint route",
                Width = 470,
                Height = 340,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var layout = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14),
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                };
                dialog.Controls.Add(layout);

                layout.Controls.Add(new Label { Text = "Rotation angle (degrees, positive = clockwise):", AutoSize = true });

                var angleBox = new ComboBox
                {
                    Width = 415,
                    DropDownStyle = ComboBoxStyle.DropDown,
                };
                int[] angles = { 30, 45, 60, 90, 120, 135, 150, 180, -30, -45, -60, -90, -120, -135, -150 };
                foreach (int a in angles)
                    angleBox.Items.Add(a.ToString() + "°");
                angleBox.SelectedIndex = 3; // 90°
                layout.Controls.Add(angleBox);

                layout.Controls.Add(new Label { Text = "Rotation pivot:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });

                var pivotBox = new ComboBox
                {
                    Width = 415,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };

                if (selCount <= 1)
                {
                    pivotBox.Items.Add(string.Format("Object spawn position ({0}, {1})", single != null ? single.X : 0, single != null ? single.Y : 0));
                    if (single != null && single.Route.Count > 0)
                        pivotBox.Items.Add(string.Format("First waypoint ({0}, {1})", single.Route[0].X, single.Route[0].Y));
                    else
                        pivotBox.Items.Add("First waypoint");
                    pivotBox.Items.Add("Route center (average waypoint)");
                    pivotBox.SelectedIndex = 0;
                }
                else
                {
                    pivotBox.Items.Add("Each object's own spawn position (local rotation)");
                    pivotBox.Items.Add("Group center (average spawn of selected objects)");
                    pivotBox.Items.Add(string.Format("Primary object #{0} spawn", Preview.SelectedIndex));
                    pivotBox.SelectedIndex = 0;
                }
                layout.Controls.Add(pivotBox);

                var maintainFacing = new CheckBox
                {
                    Text = "Maintain object facing (keep original facing direction)",
                    Checked = true,
                    AutoSize = true,
                    Font = new Font(dialog.Font, FontStyle.Bold),
                    Padding = new Padding(0, 8, 0, 0),
                };
                layout.Controls.Add(maintainFacing);

                layout.Controls.Add(new Label
                {
                    Width = 415,
                    Height = 44,
                    ForeColor = Color.DimGray,
                    Text = "When checked, waypoints and curve handles rotate while the object's spawn facing stays locked in its original direction. Uncheck to rotate facing too.",
                });

                var buttonPanel = new FlowLayoutPanel
                {
                    Width = 415,
                    Height = 40,
                    FlowDirection = FlowDirection.RightToLeft,
                };

                var cancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
                var apply = new Button { Text = "Apply", Width = 80 };
                buttonPanel.Controls.Add(cancel);
                buttonPanel.Controls.Add(apply);
                layout.Controls.Add(buttonPanel);

                dialog.AcceptButton = apply;
                dialog.CancelButton = cancel;

                apply.Click += (s, e) =>
                {
                    string text = angleBox.Text.Replace("°", "").Trim();
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double deg)
                        && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out deg))
                    {
                        MessageBox.Show(dialog, "Please enter a valid rotation angle in degrees.", "Invalid angle");
                        return;
                    }

                    RoutePivot pivot;
                    if (selCount <= 1)
                    {
                        if (pivotBox.SelectedIndex == 1) pivot = RoutePivot.FirstWaypoint;
                        else if (pivotBox.SelectedIndex == 2) pivot = RoutePivot.RouteCenter;
                        else pivot = RoutePivot.SpawnPosition;
                    }
                    else
                    {
                        if (pivotBox.SelectedIndex == 1) pivot = RoutePivot.GroupCenter;
                        else if (pivotBox.SelectedIndex == 2) pivot = RoutePivot.PrimarySpawn;
                        else pivot = RoutePivot.SpawnPosition;
                    }

                    try
                    {
                        if (Preview.RotateSelectedRoute(deg, pivot, maintainFacing.Checked))
                        {
                            dialog.DialogResult = DialogResult.OK;
                            dialog.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, "Cannot rotate route");
                    }
                };

                dialog.ShowDialog(this);
            }
        }
    }
}