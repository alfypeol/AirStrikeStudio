using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // --- Route Clipboard ---
        private static List<Waypoint> routeClipboard = null;
        private static int clipboardSpawnX = 0;
        private static int clipboardSpawnY = 0;

        public void TranslateNumeric(int x, int y, bool clone, bool routes)
        {
            var ids = SelectedObjects;
            if (ids.Length == 0
                || CommitGroup == null
                || objectDrag || routeDrag || PlacementPending)
                return;

            int dx = x - map.Objects[SelectedIndex].X;
            int dy = y - map.Objects[SelectedIndex].Y;

            if (!GroupDestination(ids, dx, dy))
                throw new ArgumentException("Destination would place an object outside the 16-bit coordinate range (-32768..32767).");

            if (!clone && dx == 0 && dy == 0) return;

            foreach (int i in ids)
                ObjectZ(map.Objects[i], map.Objects[i].X + dx, map.Objects[i].Y + dy);

            var draft = Map.Load(map.Bytes());
            int firstNewIndex = draft.Objects.Count;

            foreach (int i in ids)
            {
                var p = clone ? draft.Objects[i].Clone() : draft.Objects[i];
                p.Shift(dx, dy, routes);
                if (clone) draft.Objects.Add(p);
            }

            SyncPlacements(CommitGroup(draft));
            SetObjectSelection(clone
                ? Enumerable.Range(firstNewIndex, ids.Length).ToArray()
                : ids);
        }

        public void DuplicateOffset(int dx, int dy)
        {
            var ids = SelectedObjects;
            if (ids.Length == 0 || CommitGroup == null || objectDrag || routeDrag || PlacementPending)
                return;

            if (!GroupDestination(ids, dx, dy))
                throw new ArgumentException("Offset would place an object outside the 16-bit coordinate range (-32768..32767).");

            var draft = Map.Load(map.Bytes());
            int firstNewIndex = draft.Objects.Count;

            foreach (int i in ids)
            {
                var p = draft.Objects[i].Clone();
                p.Shift(dx, dy, true);
                draft.Objects.Add(p);
            }

            SyncPlacements(CommitGroup(draft));
            SetObjectSelection(Enumerable.Range(firstNewIndex, ids.Length).ToArray());
        }

        public bool CopyRoute()
        {
            if (SelectedObjects.Length != 1 || objectDrag || routeDrag || PlacementPending)
                return false;

            var p = map.Objects[SelectedIndex];
            if (p.Route.Count == 0) return false;

            routeClipboard = new List<Waypoint>();
            foreach (var w in p.Route)
                routeClipboard.Add(w.Clone());

            clipboardSpawnX = p.X;
            clipboardSpawnY = p.Y;

            MessageBox.Show("Copied " + p.Route.Count + " waypoints to clipboard.", "Route Copied");
            return true;
        }

        public bool PasteRoute()
        {
            if (routeClipboard == null)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        if (WaypointCsv.TryParse(text, out var waypoints, out int? sx, out int? sy, out _))
                        {
                            routeClipboard = waypoints;
                            clipboardSpawnX = sx ?? (waypoints.Count > 0 ? waypoints[0].X : 0);
                            clipboardSpawnY = sy ?? (waypoints.Count > 0 ? waypoints[0].Y : 0);
                        }
                    }
                }
                catch { }
            }

            if (routeClipboard == null || SelectedObjects.Length != 1 || objectDrag || routeDrag || PlacementPending)
                return false;

            var p = map.Objects[SelectedIndex];

            int dx = p.X - clipboardSpawnX;
            int dy = p.Y - clipboardSpawnY;

            var draft = p.Clone();
            draft.Route.Clear();

            foreach (var w in routeClipboard)
            {
                var newWp = w.Clone();
                newWp.Shift(dx, dy);
                draft.Route.Add(newWp);
            }

            if (draft.Route.Count > 0 && draft.Link < 0)
                draft.Link = 0;

            ApplyProperties(draft);
            RouteWaypoint = -1;
            NotifySelection();

            MessageBox.Show("Pasted " + draft.Route.Count + " waypoints.", "Route Pasted");
            return true;
        }

        public bool AlignFinalRoute()
        {
            if (SelectedObjects.Length != 1 || objectDrag || routeDrag || PlacementPending)
                return false;

            var draft = SelectedDraft();
            if (draft.Route.Count == 0) return false;

            int last = draft.Route.Count - 1;
            if (!RouteTools.AlignArrival(draft, last)) return false;

            ApplyProperties(draft);
            RouteRows();
            RouteWaypoint = last;
            NotifySelection();
            return true;
        }
    }

    public sealed partial class GlTerrainWindow
    {
        public void TriggerDuplicateOffset()
        {
            try
            {
                Preview.DuplicateOffset(2, 2);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Duplicate Offset");
            }
        }

        public void TriggerCopyRoute()
        {
            Preview.CopyRoute();
        }

        public void TriggerPasteRoute()
        {
            try
            {
                Preview.PasteRoute();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Paste Route");
            }
        }

        public void TriggerExportRouteCsv()
        {
            var p = Preview.SelectedDraft();
            if (p == null || p.Route.Count == 0)
            {
                MessageBox.Show(this, "Select an object with a waypoint route first.", "Export Route CSV");
                return;
            }

            using (var dialog = new SaveFileDialog
            {
                Title = "Export Waypoint Route to CSV",
                Filter = "CSV (*.csv)|*.csv|All Files (*.*)|*.*",
                FileName = "object-" + Preview.SelectedIndex + "-waypoints.csv",
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, WaypointCsv.Format(p));
                    MessageBox.Show(this,
                        "Exported " + p.Route.Count + " waypoints to " + Path.GetFileName(dialog.FileName) + ".",
                        "Export Route Complete");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not export waypoints");
                }
            }
        }

        public void TriggerImportRouteCsv()
        {
            if (Preview.SelectedObjects.Length == 0)
            {
                MessageBox.Show(this, "Select an object first to import waypoints into.", "Import Route CSV");
                return;
            }

            using (var ofd = new OpenFileDialog
            {
                Title = "Import Waypoint Route from CSV",
                Filter = "CSV (*.csv)|*.csv|All Files (*.*)|*.*",
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string text = File.ReadAllText(ofd.FileName);
                    if (!WaypointCsv.TryParse(text, out var waypoints, out int? sx, out int? sy, out string error))
                    {
                        MessageBox.Show(this, "Failed to parse CSV:\r\n" + error, "Invalid CSV");
                        return;
                    }

                    PromptAndImportWaypoints(waypoints, sx, sy, Path.GetFileName(ofd.FileName));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not read CSV file");
                }
            }
        }

        public void PromptAndImportWaypoints(
            List<Waypoint> waypoints,
            int? originSpawnX,
            int? originSpawnY,
            string sourceName)
        {
            if (waypoints == null || waypoints.Count == 0) return;
            var ids = Preview.SelectedObjects;
            if (ids.Length == 0)
            {
                MessageBox.Show(this, "Select an object to import waypoints into.", "Import Waypoints");
                return;
            }

            var draft = Preview.DropTableDraft();
            var primary = draft.Objects[Preview.SelectedIndex];
            string typeName = draft.Types[primary.Type];

            int refX = originSpawnX ?? waypoints[0].X;
            int refY = originSpawnY ?? waypoints[0].Y;

            using (var dialog = new Form
            {
                Text = "Import Waypoint Route — " + waypoints.Count + " waypoints",
                Width = 490,
                Height = 310,
                MinimumSize = new Size(440, 290),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
            })
            {
                var flow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    Padding = new Padding(14),
                    WrapContents = false,
                };

                flow.Controls.Add(new Label
                {
                    Text = "Source: " + sourceName + " (" + waypoints.Count + " waypoints)\r\n" +
                           "Target: #" + Preview.SelectedIndex + " — " + typeName + " (at " + primary.X + ", " + primary.Y + ")",
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 10),
                });

                var radioRelative = new RadioButton
                {
                    Text = "Align relative to object spawn (offset dx=" + (primary.X - refX) + ", dy=" + (primary.Y - refY) + ")",
                    AutoSize = true,
                    Checked = true,
                    Margin = new Padding(0, 2, 0, 2),
                };
                var radioAbsolute = new RadioButton
                {
                    Text = "Keep exact world coordinates from CSV (no offset)",
                    AutoSize = true,
                    Margin = new Padding(0, 2, 0, 6),
                };
                flow.Controls.Add(radioRelative);
                flow.Controls.Add(radioAbsolute);

                var checkAppend = new CheckBox
                {
                    Text = "Append to existing route (uncheck to replace)",
                    AutoSize = true,
                    Checked = false,
                    Margin = new Padding(0, 6, 0, 4),
                };
                flow.Controls.Add(checkAppend);

                CheckBox checkAllSelected = null;
                if (ids.Length > 1)
                {
                    checkAllSelected = new CheckBox
                    {
                        Text = "Apply to all " + ids.Length + " selected objects",
                        AutoSize = true,
                        Checked = true,
                        Margin = new Padding(0, 4, 0, 4),
                    };
                    flow.Controls.Add(checkAllSelected);
                }

                var btnPanel = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.RightToLeft,
                    Width = 440,
                    Height = 40,
                    Margin = new Padding(0, 12, 0, 0),
                };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                var ok = new Button { Text = "Import Route", AutoSize = true, DialogResult = DialogResult.OK };
                btnPanel.Controls.Add(cancel);
                btnPanel.Controls.Add(ok);
                flow.Controls.Add(btnPanel);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Controls.Add(flow);

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                bool relative = radioRelative.Checked;
                bool append = checkAppend.Checked;
                int[] targetIds = (checkAllSelected != null && checkAllSelected.Checked) ? ids : new[] { Preview.SelectedIndex };

                foreach (int id in targetIds)
                {
                    var p = draft.Objects[id];
                    int dx = relative ? p.X - refX : 0;
                    int dy = relative ? p.Y - refY : 0;

                    if (!append) p.Route.Clear();

                    foreach (var w in waypoints)
                    {
                        var copy = w.Clone();
                        copy.Shift(dx, dy);
                        p.Route.Add(copy);
                    }

                    if (p.Route.Count > 0 && p.Link < 0)
                        p.Link = 0;
                }

                Preview.ApplyProperties(draft.Objects[Preview.SelectedIndex]);
                if (targetIds.Length > 1)
                {
                    Preview.SyncPlacements(Preview.CommitGroup(draft));
                    Preview.SetObjectSelection(targetIds);
                }
                else
                {
                    Preview.RouteWaypoint = -1;
                    Preview.NotifySelection();
                }

                MessageBox.Show(this,
                    "Imported " + waypoints.Count + " waypoints into " + targetIds.Length + " object(s).",
                    "Route Import Complete");
            }
        }

        private void NumericTranslateDialog(bool clone)
        {
            var p = Preview.SelectedDraft();
            if (p == null) return;

            using (var form = new Form
            {
                Text = clone ? "Duplicate selection to coordinates" : "Move selection to coordinates",
                Width = 460,
                Height = 280,
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
                    Width = 410,
                    Height = 44,
                    Text = "Destination of primary object #" + Preview.SelectedIndex +
                           " (map grid). Other selected objects keep their relative spacing.",
                });

                var row = new FlowLayoutPanel { Width = 410, Height = 35 };
                var x = new NumericUpDown { Minimum = short.MinValue, Maximum = short.MaxValue, Value = p.X, Width = 110 };
                var y = new NumericUpDown { Minimum = short.MinValue, Maximum = short.MaxValue, Value = p.Y, Width = 110 };
                row.Controls.AddRange(new Control[]
                {
                    new Label { Text = "X", AutoSize = true },
                    x,
                    new Label { Text = "Y", AutoSize = true },
                    y,
                });
                flow.Controls.Add(row);

                var routes = new CheckBox
                {
                    Text = "Move all waypoints and curve handles",
                    Checked = Preview.MoveRoutes,
                    AutoSize = true,
                };
                flow.Controls.Add(routes);

                flow.Controls.Add(new Label
                {
                    Width = 410,
                    Height = 38,
                    Text = "Unchecked: route coordinates stay unchanged. " +
                           "One undo step restores the entire operation.",
                });

                var buttons = new FlowLayoutPanel { Width = 410, Height = 32 };
                var apply = new Button { Text = clone ? "Duplicate" : "Move" };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                buttons.Controls.AddRange(new Control[] { apply, cancel });
                flow.Controls.Add(buttons);
                form.CancelButton = cancel;

                apply.Click += (s, e) =>
                {
                    try
                    {
                        Preview.TranslateNumeric((int)x.Value, (int)y.Value, clone, routes.Checked);
                        form.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "Cannot move selection");
                    }
                };

                form.ShowDialog(this);
            }
        }

        private void SetupPrecisionTools(FlowLayoutPanel flow)
        {
            var move = new Button { Text = "Move to…", AutoSize = true };
            var duplicate = new Button { Text = "Duplicate to…", AutoSize = true };
            var align = new Button { Text = "Align final approach", AutoSize = true };

            move.Click += (s, e) => NumericTranslateDialog(false);
            duplicate.Click += (s, e) => NumericTranslateDialog(true);
            align.Click += (s, e) =>
            {
                if (!Preview.AlignFinalRoute())
                    MessageBox.Show(this,
                        "No approach direction is available. Check the final waypoint " +
                        "and preceding positions.",
                        "Align final approach");
            };

            flow.Controls.AddRange(new Control[] { move, duplicate, align });

            Action update = () =>
            {
                move.Enabled = duplicate.Enabled =
                    Preview.SelectedIndex >= 0 && !Preview.PlacementPending;

                var draft = Preview.SelectedDraft();
                align.Enabled = Preview.SelectedObjects.Length == 1
                             && draft != null
                             && draft.Route.Count > 0
                             && !Preview.PlacementPending;
            };
            Preview.SelectionChanged += (s, e) => update();
            update();
        }
    }
}