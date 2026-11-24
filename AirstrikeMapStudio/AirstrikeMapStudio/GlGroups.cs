using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        private int[] pendingGroup;

        public Func<Map, Map> CommitGroup;

        public int[] SelectedObjects => selectionState.SelectedObjects;

        public void SetObjectSelection(int[] indices)
        {
            selectionState.SetSelection(indices, map.Objects.Count);
            RouteWaypoint = -1;
            NotifySelection();
        }

        private void PickGroup(int index, bool toggle)
        {
            selectionState.Pick(index, toggle, map.Objects.Count);
            RouteWaypoint = -1;
            NotifySelection();
        }

        /// <summary>
        /// True if adding (<paramref name="dx"/>, <paramref name="dy"/>) to
        /// every selected object's position keeps all of them within the signed
        /// 16-bit coordinate range (-32768..32767), allowing off-map placement
        /// on all four sides (left, right, top, bottom).
        /// </summary>
        private bool GroupDestination(int[] ids, int dx, int dy)
        {
            return SelectionState.CanShiftGroup(map.Objects, ids, dx, dy);
        }

        public void MoveGroupTo(int x, int y)
        {
            if (CommitGroup == null || SelectedIndex < 0) return;

            int dx = x - map.Objects[SelectedIndex].X;
            int dy = y - map.Objects[SelectedIndex].Y;

            var ids = SelectedObjects;
            if ((dx == 0 && dy == 0) || !GroupDestination(ids, dx, dy)) return;

            foreach (int i in ids)
                ObjectZ(map.Objects[i], map.Objects[i].X + dx, map.Objects[i].Y + dy);

            var draft = map.Clone();
            foreach (int i in ids)
                draft.Objects[i].Shift(dx, dy, MoveRoutes);

            SyncPlacements(CommitGroup(draft));
            SetObjectSelection(ids);
        }

        public void RotateGroup(int steps, bool primaryPivot, bool routes, bool maintainFacing = false)
        {
            var ids = SelectedObjects;
            if (ids.Length < 2 || CommitGroup == null || objectDrag || routeDrag) return;

            steps %= 12;
            if (steps == 0) return;

            double px = primaryPivot
                ? map.Objects[SelectedIndex].X
                : ids.Average(i => (double)map.Objects[i].X);
            double py = primaryPivot
                ? map.Objects[SelectedIndex].Y
                : ids.Average(i => (double)map.Objects[i].Y);

            double angle = steps * Math.PI / 6;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);

            Func<double, double, double[]> turn = (x, y) => new[]
            {
                px + (x - px) * c - (y - py) * s,
                py + (x - px) * s + (y - py) * c,
            };

            var draft = map.Clone();

            foreach (int i in ids)
            {
                var p = draft.Objects[i];
                var at = turn(p.X, p.Y);
                int x = (int)Math.Round(at[0]);
                int y = (int)Math.Round(at[1]);

                if (x < short.MinValue || y < short.MinValue || x > short.MaxValue || y > short.MaxValue)
                    throw new ArgumentException(
                        "Rotation would move an object outside the 16-bit coordinate range (-32768..32767). " +
                        "Choose another pivot or angle.");

                ObjectZ(map.Objects[i], x, y);

                p.X = (short)x;
                p.Y = (short)y;
                if (!maintainFacing)
                {
                    p.Setting6 = (byte)(((p.Setting6 + steps) % 12 + 12) % 12);
                }

                if (routes && p.Route.Count > 0)
                {
                    RouteRotation.ApplyAroundSpawns(p.Route, turn, map.Width, map.Height);
                }
            }

            SyncPlacements(CommitGroup(draft));
            SetObjectSelection(ids);
        }

        public void ArrangeGroup(bool xAxis, bool distribute)
        {
            var ids = SelectedObjects;
            if (ids.Length < 2 || CommitGroup == null || objectDrag || routeDrag
                || PlacementPending || sculptCells != null || tileDraft != null)
                return;

            if (distribute && ids.Length < 3)
                throw new ArgumentException("Select at least three objects to distribute.");

            Func<int, int> coordinate = i => xAxis ? map.Objects[i].X : map.Objects[i].Y;

            var ordered = ids.OrderBy(coordinate).ThenBy(i => i).ToArray();
            int low = coordinate(ordered[0]);
            int high = coordinate(ordered[ordered.Length - 1]);

            if (distribute && high - low < ordered.Length - 1)
                throw new ArgumentException(
                    "There are not enough whole grid cells between the outermost " +
                    "objects. Move the endpoints farther apart first.");

            var draft = map.Clone();

            for (int n = 0; n < ordered.Length; n++)
            {
                int index = ordered[n];
                var source = map.Objects[index];

                int target = distribute
                    ? low + (int)Math.Round(
                        (high - low) * n / (double)(ordered.Length - 1),
                        MidpointRounding.AwayFromZero)
                    : coordinate(SelectedIndex);

                int x = xAxis ? target : source.X;
                int y = xAxis ? source.Y : target;

                if (x < short.MinValue || y < short.MinValue || x > short.MaxValue || y > short.MaxValue)
                    throw new ArgumentException("An arranged object would be outside the 16-bit range (-32768..32767).");

                if (x == source.X && y == source.Y) continue;

                ObjectZ(source, x, y);
                draft.Objects[index].Shift(x - source.X, y - source.Y, MoveRoutes);
            }

            if (draft.Bytes().SequenceEqual(map.Bytes())) return;

            int primary = SelectedIndex;
            SyncPlacements(CommitGroup(draft));

            SetObjectSelection(
                ids.Where(i => i != primary).Concat(new[] { primary }).ToArray());
        }

        private void DeleteGroup()
        {
            var ids = SelectedObjects;
            var draft = map.Clone();

            foreach (int i in ids.OrderByDescending(i => i))
                draft.Objects.RemoveAt(i);

            SetObjectSelection(new int[0]);
            SyncPlacements(CommitGroup(draft));
        }

        private void PlaceGroup(int x, int y)
        {
            var ids = pendingGroup;
            int dx = x - map.Objects[pendingClone].X;
            int dy = y - map.Objects[pendingClone].Y;

            if (!GroupDestination(ids, dx, dy)) return;

            var draft = map.Clone();
            int first = draft.Objects.Count;
            foreach (int i in ids)
            {
                var p = map.Objects[i].Clone();
                p.Shift(dx, dy, route: true);
                draft.Objects.Add(p);
            }

            var result = CommitGroup(draft);
            CancelPlacement();
            SyncPlacements(result);
            SetObjectSelection(Enumerable.Range(first, ids.Length).ToArray());
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private void SetupArrangementTools(FlowLayoutPanel tools)
        {
            var arrange = new Button
            {
                Text = "Align / space…",
                AutoSize = true,
                Enabled = false,
            };
            tools.Controls.Add(arrange);

            Action update = () =>
                arrange.Enabled = Preview.SelectedObjects.Length >= 2 && !Preview.PlacementPending;
            Preview.SelectionChanged += (s, e) => update();
            update();

            arrange.Click += (s, e) =>
            {
                using (var form = new Form
                {
                    Text = "Align or space selected objects",
                    ClientSize = new System.Drawing.Size(460, 265),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    StartPosition = FormStartPosition.CenterParent,
                })
                {
                    var flow = new FlowLayoutPanel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(12),
                        FlowDirection = FlowDirection.TopDown,
                        WrapContents = false,
                    };
                    form.Controls.Add(flow);

                    flow.Controls.Add(new Label
                    {
                        Width = 430,
                        Height = 35,
                        Text = Preview.SelectedObjects.Length + " objects selected. Active object: #" + Preview.SelectedIndex,
                    });

                    var operation = new ComboBox
                    {
                        Width = 420,
                        DropDownStyle = ComboBoxStyle.DropDownList,
                    };
                    operation.Items.AddRange(new object[]
                    {
                        "Align X to active object",
                        "Align Y to active object",
                        "Distribute along X",
                        "Distribute along Y",
                    });
                    operation.SelectedIndex = 0;
                    flow.Controls.Add(operation);

                    flow.Controls.Add(new Label
                    {
                        Width = 430,
                        Height = 100,
                        Text = "Alignment changes spawn centers, not model edges. Objects may overlap.\r\n" +
                               "Distribution keeps the outermost coordinates fixed. Gaps differ by at most one grid cell; select at least three objects.\r\n" +
                               "Move waypoints is " + (Preview.MoveRoutes
                                   ? "ON: routes follow each object."
                                   : "OFF: routes stay in place.") +
                               "\r\nCtrl+Z restores the whole operation.",
                    });

                    var buttons = new FlowLayoutPanel { Width = 420, Height = 35 };
                    var apply = new Button { Text = "Apply" };
                    var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                    buttons.Controls.AddRange(new Control[] { apply, cancel });
                    flow.Controls.Add(buttons);
                    form.CancelButton = cancel;

                    apply.Click += (sender, args) =>
                    {
                        try
                        {
                            Preview.ArrangeGroup(operation.SelectedIndex % 2 == 0, operation.SelectedIndex >= 2);
                            form.Close();
                        }
                        catch (Exception error)
                        {
                            MessageBox.Show(form, error.Message, "Cannot arrange objects");
                        }
                    };

                    form.ShowDialog(this);
                }
            };
        }

        private void RotateGroupDialog()
        {
            if (Preview.SelectedObjects.Length < 2) return;

            using (var form = new Form
            {
                Text = "Rotate selected group",
                Width = 470,
                Height = 280,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
            })
            {
                var layout = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(12),
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                };
                form.Controls.Add(layout);

                var angle = new ComboBox
                {
                    Width = 400,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                for (int n = -6; n <= 6; n++)
                    if (n != 0)
                        angle.Items.Add(n * 30);
                angle.SelectedItem = 30;
                layout.Controls.Add(angle);

                var pivot = new ComboBox
                {
                    Width = 400,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                pivot.Items.Add("Group center (average spawn position)");
                pivot.Items.Add("Primary object #" + Preview.SelectedIndex);
                pivot.SelectedIndex = 0;
                layout.Controls.Add(pivot);

                var routes = new CheckBox
                {
                    Text = "Rotate waypoints and curve handles",
                    Checked = Preview.MoveRoutes,
                    AutoSize = true,
                };
                layout.Controls.Add(routes);

                var maintainFacing = new CheckBox
                {
                    Text = "Maintain object facing (keep original facing directions)",
                    Checked = false,
                    AutoSize = true,
                };
                layout.Controls.Add(maintainFacing);

                layout.Controls.Add(new Label
                {
                    Width = 420,
                    Height = 48,
                    Text = "Angles are in degrees. Positions snap to the map grid, so spacing can change slightly. " +
                           "Check 'Maintain object facing' to preserve aimed directions. Ctrl+Z restores the exact original.",
                });

                var apply = new Button { Text = "Apply" };
                layout.Controls.Add(apply);

                apply.Click += (s, e) =>
                {
                    try
                    {
                        Preview.RotateGroup((int)angle.SelectedItem / 30, pivot.SelectedIndex == 1, routes.Checked, maintainFacing.Checked);
                        form.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "Cannot rotate group");
                    }
                };

                form.ShowDialog(this);
            }
        }
    }
}