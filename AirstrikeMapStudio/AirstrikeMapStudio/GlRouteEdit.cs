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
        // ═════════════════════════════════════════════════════════════════
        // Route point editing
        // ═════════════════════════════════════════════════════════════════
        // Dragging a waypoint snaps to grid (X, Y rounded). Dragging a
        // Bézier handle does NOT snap (float precision preserved).
        // Both waypoints and handles can extend freely outside map edges
        // (including negative X/Y coordinates).
        // ═════════════════════════════════════════════════════════════════

        private const int MinWaypointCoord = -100000;
        private const int MaxWaypointCoord = 100000;

        private bool routeDrag;
        private bool routeValid;
        private Placement routeDraft;
        private Point3D routeAnchor;
        private int routeHandle = -1;

        /// <summary>
        /// When true (default), dragging one Bézier handle modifies ONLY that side
        /// without moving or mirroring the opposite handle. Hold Shift or Alt to mirror.
        /// When false, dragging one handle mirrors the opposite handle (legacy behavior).
        /// </summary>
        public bool IndependentHandles = true;

        /// <summary>
        /// When true in Edit Route mode, clicking empty ground appends a new
        /// waypoint at the clicked cell. Holding Shift also appends regardless.
        /// </summary>
        public bool AppendRouteOnClick = true;

        private bool routeHover;
        private int routeHoverX, routeHoverY;
        private int routeHoverPick = -1;

        internal void HoverRoute(int screenX, int screenY)
        {
            if (!EditRoute || SelectedObjects.Length != 1 || SelectedIndex < 0)
            {
                if (routeHover) { routeHover = false; Invalidate(); }
                return;
            }

            int pick = PickWaypoint(screenX, screenY);
            if (GroundAt(screenX, screenY, out Point3D hit))
            {
                int gx = Math.Max(MinWaypointCoord, Math.Min(MaxWaypointCoord, (int)Math.Round(hit.X / 40)));
                int gy = Math.Max(MinWaypointCoord, Math.Min(MaxWaypointCoord, (int)Math.Round(hit.Y / 40)));
                if (!routeHover || gx != routeHoverX || gy != routeHoverY || pick != routeHoverPick)
                {
                    routeHover = true;
                    routeHoverX = gx;
                    routeHoverY = gy;
                    routeHoverPick = pick;
                    Invalidate();
                }
            }
            else if (routeHover)
            {
                routeHover = false;
                Invalidate();
            }
        }

        /// <summary>
        /// Appends a waypoint at the ground coordinates under (screenX, screenY)
        /// when in Edit Route mode and clicking away from existing waypoints/handles.
        /// </summary>
        public bool AppendRouteWaypointAtScreen(int screenX, int screenY)
        {
            if (!EditRoute || SelectedObjects.Length != 1 || SelectedIndex < 0 || routeDrag || objectDrag)
                return false;

            if (!GroundAt(screenX, screenY, out Point3D hit))
                return false;

            int gx = Math.Max(MinWaypointCoord, Math.Min(MaxWaypointCoord, (int)Math.Round(hit.X / 40)));
            int gy = Math.Max(MinWaypointCoord, Math.Min(MaxWaypointCoord, (int)Math.Round(hit.Y / 40)));

            var draft = SelectedDraft();
            if (draft == null) return false;

            int insertIdx = draft.Route.Count;
            var wp = RouteTools.WaypointAt(draft, insertIdx, gx, gy);
            if (draft.Route.Count == 0 && draft.Link < 0)
                draft.Link = 0;

            if (insertIdx > 0)
            {
                var prev = draft.Route[insertIdx - 1];
                prev.BX = prev.X + (gx - prev.X) * 0.35f;
                prev.BY = prev.Y + (gy - prev.Y) * 0.35f;
            }

            draft.Route.Add(wp);
            ApplyProperties(draft);
            RouteRows();
            RouteWaypoint = draft.Route.Count - 1;
            NotifySelection();
            return true;
        }

        /// <summary>
        /// Reverses the waypoint order and swaps A/B handles for the selected object(s).
        /// </summary>
        public bool ReverseSelectedRoute()
        {
            var ids = SelectedObjects;
            if (ids.Length == 0 || routeDrag || objectDrag || PlacementPending) return false;

            if (ids.Length > 1 && CommitGroup != null)
            {
                var mapDraft = map.Clone();
                bool any = false;
                foreach (int i in ids)
                {
                    if (mapDraft.Objects[i].Route.Count >= 2)
                    {
                        RouteTools.Reverse(mapDraft.Objects[i]);
                        any = true;
                    }
                }
                if (!any) return false;
                SyncPlacements(CommitGroup(mapDraft));
                SetObjectSelection(ids);
                return true;
            }

            var single = SelectedDraft();
            if (single == null || single.Route.Count < 2) return false;
            RouteTools.Reverse(single);
            ApplyProperties(single);
            if (RouteWaypoint >= 0 && RouteWaypoint < single.Route.Count)
                RouteWaypoint = single.Route.Count - 1 - RouteWaypoint;
            NotifySelection();
            return true;
        }

        /// <summary>
        /// Auto-smooths (or straightens) Bézier handles across the selected object(s)' routes.
        /// </summary>
        public bool SmoothSelectedRoute(bool straight)
        {
            var ids = SelectedObjects;
            if (ids.Length == 0 || routeDrag || objectDrag || PlacementPending) return false;

            if (ids.Length > 1 && CommitGroup != null)
            {
                var mapDraft = map.Clone();
                bool any = false;
                foreach (int i in ids)
                {
                    if (mapDraft.Objects[i].Route.Count > 0)
                    {
                        RouteTools.AutoSmooth(mapDraft.Objects[i], straight);
                        any = true;
                    }
                }
                if (!any) return false;
                SyncPlacements(CommitGroup(mapDraft));
                SetObjectSelection(ids);
                return true;
            }

            var single = SelectedDraft();
            if (single == null || single.Route.Count == 0) return false;
            RouteTools.AutoSmooth(single, straight);
            ApplyProperties(single);
            NotifySelection();
            return true;
        }

        /// <summary>
        /// Returns true if at least one selected object has a route with one or more waypoints.
        /// </summary>
        public bool HasRoutableSelection()
        {
            if (map == null) return false;
            var ids = SelectedObjects;
            if (ids.Length == 0) return false;
            foreach (int i in ids)
            {
                if (i >= 0 && i < map.Objects.Count && map.Objects[i].Route.Count > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Rotates the route(s) of the selected object(s) by <paramref name="angleDegrees"/> around the chosen pivot.
        /// When <paramref name="maintainFacing"/> is true, object initial facing is kept unchanged.
        /// </summary>
        public bool RotateSelectedRoute(double angleDegrees, RoutePivot pivot, bool maintainFacing)
        {
            var ids = SelectedObjects;
            if (ids.Length == 0 || routeDrag || objectDrag || PlacementPending) return false;

            if (ids.Length > 1 && CommitGroup != null)
            {
                var mapDraft = map.Clone();
                bool any = false;

                double groupPivotX = ids.Average(i => (double)mapDraft.Objects[i].X);
                double groupPivotY = ids.Average(i => (double)mapDraft.Objects[i].Y);

                int primaryId = SelectedIndex >= 0 ? SelectedIndex : ids[0];
                double primaryPivotX = mapDraft.Objects[primaryId].X;
                double primaryPivotY = mapDraft.Objects[primaryId].Y;

                int facingSteps = (int)Math.Round(angleDegrees / 30.0);

                foreach (int i in ids)
                {
                    var p = mapDraft.Objects[i];
                    if (p.Route.Count == 0) continue;

                    double px, py;
                    switch (pivot)
                    {
                        case RoutePivot.GroupCenter:
                            px = groupPivotX;
                            py = groupPivotY;
                            break;
                        case RoutePivot.PrimarySpawn:
                            px = primaryPivotX;
                            py = primaryPivotY;
                            break;
                        case RoutePivot.FirstWaypoint:
                            px = p.Route[0].X;
                            py = p.Route[0].Y + 1;
                            break;
                        case RoutePivot.RouteCenter:
                            px = p.Route.Average(w => (double)w.X);
                            py = p.Route.Average(w => (double)(w.Y + 1));
                            break;
                        case RoutePivot.SpawnPosition:
                        default:
                            px = p.X;
                            py = p.Y;
                            break;
                    }

                    RouteRotation.RotateRoute(p.Route, px, py, angleDegrees, map.Width, map.Height);

                    if (!maintainFacing && (facingSteps % 12 != 0))
                    {
                        p.Setting6 = (byte)(((p.Setting6 + facingSteps) % 12 + 12) % 12);
                    }

                    any = true;
                }

                if (!any) return false;
                SyncPlacements(CommitGroup(mapDraft));
                SetObjectSelection(ids);
                return true;
            }

            var single = SelectedDraft();
            if (single == null || single.Route.Count == 0) return false;

            double spx, spy;
            switch (pivot)
            {
                case RoutePivot.FirstWaypoint:
                    spx = single.Route[0].X;
                    spy = single.Route[0].Y + 1;
                    break;
                case RoutePivot.RouteCenter:
                    spx = single.Route.Average(w => (double)w.X);
                    spy = single.Route.Average(w => (double)(w.Y + 1));
                    break;
                case RoutePivot.SpawnPosition:
                default:
                    spx = single.X;
                    spy = single.Y;
                    break;
            }

            RouteRotation.RotateRoute(single.Route, spx, spy, angleDegrees, map.Width, map.Height);

            if (!maintainFacing)
            {
                int facingSteps = (int)Math.Round(angleDegrees / 30.0);
                if (facingSteps % 12 != 0)
                {
                    single.Setting6 = (byte)(((single.Setting6 + facingSteps) % 12 + 12) % 12);
                }
            }

            ApplyProperties(single);
            NotifySelection();
            return true;
        }

        /// <summary>
        /// Sets or scales wait values across all waypoints of the selected object(s).
        /// </summary>
        public bool ApplyBulkWait(float value, bool multiply)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0)
                throw new ArgumentException("Enter a finite, non-negative value.");

            var ids = SelectedObjects;
            if (ids.Length == 0 || routeDrag || objectDrag || PlacementPending) return false;

            if (ids.Length > 1 && CommitGroup != null)
            {
                var mapDraft = map.Clone();
                bool any = false;
                foreach (int i in ids)
                {
                    foreach (var w in mapDraft.Objects[i].Route)
                    {
                        w.Value = multiply ? Math.Max(0f, w.Value * value) : value;
                        any = true;
                    }
                }
                if (!any) return false;
                SyncPlacements(CommitGroup(mapDraft));
                SetObjectSelection(ids);
                return true;
            }

            var single = SelectedDraft();
            if (single == null || single.Route.Count == 0) return false;
            foreach (var w in single.Route)
                w.Value = multiply ? Math.Max(0f, w.Value * value) : value;
            ApplyProperties(single);
            NotifySelection();
            return true;
        }

        /// <summary>
        /// Returns the index of the waypoint under the cursor, or -1.
        /// Threshold is a squared distance of 144 (12 px).
        /// </summary>
        public int PickWaypoint(int x, int y)
        {
            if (!ShowRoute || SelectedIndex < 0) return -1;

            int best = -1;
            double distance = 144;

            var route = map.Objects[SelectedIndex].Route;
            for (int i = 0; i < route.Count; i++)
            {
                if (!RouteScreen(route[i].X, route[i].Y, out PointF p)) continue;
                double d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }

        private void BeginRouteDrag(int x, int y)
        {
            if (SelectedObjects.Length > 1) return;

            int index = PickWaypoint(x, y);
            routeHandle = -1;
            double best = 144;
            PointF point;

            if (index >= 0
                && RouteScreen(map.Objects[SelectedIndex].Route[index].X,
                               map.Objects[SelectedIndex].Route[index].Y, out point))
            {
                best = (point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y);
            }

            if (ShowRoute && SelectedIndex >= 0 && RouteWaypoint >= 0
                && RouteWaypoint < map.Objects[SelectedIndex].Route.Count)
            {
                var w = map.Objects[SelectedIndex].Route[RouteWaypoint];
                for (int handle = 0; handle < 2; handle++)
                {
                    if (!RouteScreen(handle == 0 ? w.AX : w.BX,
                                     handle == 0 ? w.AY : w.BY, out point))
                        continue;

                    double d = (point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y);
                    if (d < best)
                    {
                        best = d;
                        index = RouteWaypoint;
                        routeHandle = handle;
                    }
                }
            }

            if (index < 0) return;

            RouteRows();
            RouteWaypoint = index;
            NotifySelection();

            if (!GroundAt(x, y, out routeAnchor)) return;

            routeDraft = map.Objects[SelectedIndex].Clone();
            routeDrag = true;
            routeValid = true;
        }

        private void UpdateRouteDrag(int x, int y)
        {
            if (!GroundAt(x, y, out Point3D hit))
            {
                routeValid = false;
                return;
            }

            var original = map.Objects[SelectedIndex].Route[RouteWaypoint];
            double dx = (hit.X - routeAnchor.X) / 40;
            double dy = (hit.Y - routeAnchor.Y) / 40;

            routeDraft = map.Objects[SelectedIndex].Clone();
            var w = routeDraft.Route[RouteWaypoint];

            // Handle drag: no snapping.
            if (routeHandle >= 0)
            {
                routeValid = true;
                bool shouldMirror;
                if (IndependentHandles)
                {
                    // By default, independent: moving one handle leaves the other completely untouched!
                    // Hold Alt or Shift if you specifically want to mirror both sides.
                    shouldMirror = (ModifierKeys & (Keys.Alt | Keys.Shift)) != 0;
                }
                else
                {
                    // Locked mode: mirrors unless Alt is held.
                    shouldMirror = (ModifierKeys & Keys.Alt) == 0 && w.Mode == 1;
                }

                if (!shouldMirror)
                    w.Mode = 0; // Mark this waypoint as independent / free handles
                else
                    w.Mode = 1; // Mark as smooth / mirrored

                if (routeHandle == 0)
                {
                    w.AX = original.AX + (float)dx;
                    w.AY = original.AY + (float)dy;
                    if (shouldMirror)
                    {
                        w.BX = 2 * w.X - w.AX;
                        w.BY = 2 * w.Y - w.AY;
                    }
                }
                else
                {
                    w.BX = original.BX + (float)dx;
                    w.BY = original.BY + (float)dy;
                    if (shouldMirror)
                    {
                        w.AX = 2 * w.X - w.BX;
                        w.AY = 2 * w.Y - w.BY;
                    }
                }
                return;
            }

            // Waypoint drag: snap to grid; allow any negative or positive coordinate
            int nx = original.X + (int)Math.Round(dx);
            int ny = original.Y + (int)Math.Round(dy);

            routeValid = nx >= MinWaypointCoord && ny >= MinWaypointCoord
                      && nx <= MaxWaypointCoord && ny <= MaxWaypointCoord;
            if (routeValid)
                w.Shift(nx - original.X, ny - original.Y);
        }

        public void AddRouteWaypoint()
        {
            if (SelectedObjects.Length > 1 || routeDrag || SelectedIndex < 0) return;

            var draft = SelectedDraft();
            int index = RouteWaypoint < 0
                ? draft.Route.Count
                : Math.Min(draft.Route.Count, RouteWaypoint + 1);

            RouteTools.InsertWaypoint(draft, index, map.Height);
            ApplyProperties(draft);
            RouteWaypoint = index;
            NotifySelection();
        }

        public void DeleteRouteWaypoint()
        {
            if (SelectedObjects.Length > 1 || routeDrag || SelectedIndex < 0 || RouteWaypoint < 0)
                return;

            var draft = SelectedDraft();
            if (RouteWaypoint >= draft.Route.Count) return;

            draft.Route.RemoveAt(RouteWaypoint);
            RouteWaypoint = Math.Min(RouteWaypoint, draft.Route.Count - 1);
            ApplyProperties(draft);
            NotifySelection();
        }

        public void ClearSelectedRoute()
        {
            if (routeDrag || SelectedIndex < 0)
                return;

            if (SelectedObjects.Length > 1)
            {
                if (CommitGroup == null) return;
                var ids = SelectedObjects;
                var mapDraft = map.Clone();
                bool any = false;
                foreach (int idx in ids)
                {
                    if (idx >= 0 && idx < mapDraft.Objects.Count)
                    {
                        var obj = mapDraft.Objects[idx];
                        if (obj.Route != null && obj.Route.Count > 0)
                        {
                            obj.Route.Clear();
                            any = true;
                        }
                    }
                }
                if (!any) return;
                RouteWaypoint = -1;
                SyncPlacements(CommitGroup(mapDraft));
                SetObjectSelection(ids);
                Changed();
                return;
            }

            var draft = SelectedDraft();
            if (draft.Route == null || draft.Route.Count == 0) return;

            draft.Route.Clear();
            RouteWaypoint = -1;
            ApplyProperties(draft);
            NotifySelection();
        }

        public void ReorderRouteWaypoint(int direction)
        {
            if (SelectedObjects.Length > 1 || routeDrag || SelectedIndex < 0 || RouteWaypoint < 0)
                return;

            var draft = SelectedDraft();
            int from = RouteWaypoint, to = from + direction;
            if (to < 0 || to >= draft.Route.Count) return;

            var w = draft.Route[from];
            draft.Route.RemoveAt(from);
            draft.Route.Insert(to, w);

            RouteWaypoint = to;
            ApplyProperties(draft);
            NotifySelection();
        }

        /// <summary>
        /// Sets the tangent handle mode for the currently-selected waypoint.
        /// 0 = Free / Independent handles (modifying one side does not affect the other).
        /// 1 = Smooth / Mirrored handles (departure handle mirrors arrival handle).
        /// </summary>
        public void SetCurrentWaypointMode(int mode)
        {
            if (SelectedObjects.Length > 1 || routeDrag || SelectedIndex < 0 || RouteWaypoint < 0)
                return;

            var draft = SelectedDraft();
            if (RouteWaypoint >= draft.Route.Count) return;

            var wp = draft.Route[RouteWaypoint];
            wp.Mode = mode;
            if (mode == 1)
            {
                wp.BX = 2 * wp.X - wp.AX;
                wp.BY = 2 * wp.Y - wp.AY;
            }
            ApplyProperties(draft);
            NotifySelection();
            Changed();
        }

        /// <summary>
        /// Sets the tangent handle mode for all waypoints in the active object's route.
        /// </summary>
        public void SetAllWaypointsMode(int mode)
        {
            if (SelectedObjects.Length > 1 || routeDrag || SelectedIndex < 0)
                return;

            var draft = SelectedDraft();
            if (draft.Route.Count == 0) return;

            foreach (var wp in draft.Route)
            {
                wp.Mode = mode;
                if (mode == 1)
                {
                    wp.BX = 2 * wp.X - wp.AX;
                    wp.BY = 2 * wp.Y - wp.AY;
                }
            }
            ApplyProperties(draft);
            NotifySelection();
            Changed();
        }

        public void SetRouteWait(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0)
                throw new ArgumentException("Enter a finite, non-negative wait value.");
            if (SelectedObjects.Length > 1 || routeDrag || SelectedIndex < 0 || RouteWaypoint < 0)
                return;

            var draft = SelectedDraft();
            if (RouteWaypoint >= draft.Route.Count) return;

            draft.Route[RouteWaypoint].Value = value;
            ApplyProperties(draft);
        }

        public float CurrentRouteWait() =>
            SelectedIndex >= 0 && RouteWaypoint >= 0 && RouteWaypoint < map.Objects[SelectedIndex].Route.Count
                ? map.Objects[SelectedIndex].Route[RouteWaypoint].Value
                : 0;

        private void EndRouteDrag(bool commit)
        {
            if (!routeDrag) return;

            var draft = routeDraft;
            routeDrag = false;
            routeDraft = null;

            try
            {
                if (commit && moved && routeValid) ApplyProperties(draft);
            }
            finally
            {
                Changed();
            }
        }

        public Waypoint RoutePointDraft() =>
            SelectedObjects.Length == 1
            && RouteWaypoint >= 0
            && RouteWaypoint < map.Objects[SelectedIndex].Route.Count
                ? map.Objects[SelectedIndex].Route[RouteWaypoint].Clone()
                : null;

        public void ApplyRoutePoint(Waypoint point)
        {
            if (point == null || RoutePointDraft() == null || routeDrag || objectDrag) return;

            foreach (float v in new[] { point.AX, point.AY, point.BX, point.BY, point.Value })
                if (float.IsNaN(v) || float.IsInfinity(v))
                    throw new ArgumentException("Handles and wait must be finite numbers.");

            if (point.Value < 0)
                throw new ArgumentException("Wait must be non-negative.");

            var draft = SelectedDraft();
            int index = RouteWaypoint;
            draft.Route[index] = point.Clone();
            ApplyProperties(draft);
            RouteWaypoint = index;
            NotifySelection();
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private void EditRoutePoint()
        {
            var draft = Preview.RoutePointDraft();
            if (draft == null) return;

            using (var dialog = new Form
            {
                Text = "Waypoint " + (Preview.RouteWaypoint + 1) + " — precise settings",
                Width = 520,
                Height = 600,
                MinimumSize = new Size(450, 500),
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var grid = new PropertyGrid
                {
                    Dock = DockStyle.Fill,
                    ToolbarVisible = false,
                    SelectedObject = new WaypointProperties(draft),
                };

                var hintPanel = new Panel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(12, 10, 12, 10),
                    BackColor = Color.FromArgb(246, 248, 251),
                };

                var hint = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = Color.FromArgb(40, 50, 70),
                    Text = "• X / Y coordinates move the waypoint center and shift both tangent handles.\r\n" +
                           "• Mode sets editor tangent constraints (1 = smooth mirrored handles, 0 = independent handles; ignored by the game engine at runtime).\r\n" +
                           "• Waypoint wait behavior is interpreted by the unit's .scr script.",
                };
                hintPanel.Controls.Add(hint);

                dialog.Resize += (s, e) =>
                {
                    hint.MaximumSize = new Size(Math.Max(100, dialog.ClientSize.Width - 24), 0);
                };

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 44,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8, 6, 8, 6),
                };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Height = 28, Width = 80 };
                var apply = new Button { Text = "Apply", Height = 28, Width = 80 };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(apply);

                dialog.Controls.Add(grid);
                dialog.Controls.Add(hintPanel);
                dialog.Controls.Add(buttons);
                dialog.CancelButton = cancel;

                // Dynamic Auto-Scaling for PropertyGrid DocComment description panel:
                // Automatically measures the description text of the selected property and
                // sets the exact required lines so no help message is ever truncated.
                Action autoScaleDocComment = () =>
                {
                    try
                    {
                        Control doc = null;
                        foreach (Control c in grid.Controls)
                        {
                            if (c.GetType().Name == "DocComment") { doc = c; break; }
                        }
                        if (doc == null) return;

                        var item = grid.SelectedGridItem;
                        string desc = item?.PropertyDescriptor?.Description ?? "";

                        int textWidth = Math.Max(180, doc.Width - 24);
                        Size sz = TextRenderer.MeasureText(desc, doc.Font, new Size(textWidth, 2000), TextFormatFlags.WordBreak);
                        int neededLines = Math.Max(5, (int)Math.Ceiling(sz.Height / 13.0) + 2);

                        var prop = doc.GetType().GetProperty("Lines");
                        if (prop != null)
                            prop.SetValue(doc, neededLines, null);
                        else
                            doc.Height = Math.Max(85, sz.Height + 40);
                    }
                    catch (Exception logEx) { AppLog.Warn(logEx); }
                };

                grid.SelectedGridItemChanged += (s, e) => autoScaleDocComment();
                dialog.Load += (s, e) =>
                {
                    hint.MaximumSize = new Size(Math.Max(100, dialog.ClientSize.Width - 24), 0);
                    autoScaleDocComment();
                };
                dialog.Shown += (s, e) => autoScaleDocComment();
                dialog.Resize += (s, e) =>
                {
                    hint.MaximumSize = new Size(Math.Max(100, dialog.ClientSize.Width - 24), 0);
                    autoScaleDocComment();
                };

                apply.Click += (s, e) =>
                {
                    if (!dialog.Validate()) return;
                    try
                    {
                        Preview.ApplyRoutePoint(draft);
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, "Invalid waypoint");
                    }
                };

                dialog.ShowDialog(this);
            }
        }
    }
}