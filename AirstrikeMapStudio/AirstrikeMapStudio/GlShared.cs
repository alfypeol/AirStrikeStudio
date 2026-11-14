using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        /// <summary>
        /// Applies any of {facing, drop, route repeat} to every selected object.
        /// Null parameters leave that field untouched. Facing is in degrees,
        /// multiple of 30, 0–330.
        /// </summary>
        public void ApplySharedSettings(int? facing, int? drop, bool? repeat)
        {
            var ids = SelectedObjects;
            if (ids.Length < 2 || CommitGroup == null || objectDrag || routeDrag || PlacementPending)
                return;

            if (facing.HasValue && (facing < 0 || facing > 330 || facing % 30 != 0))
                throw new ArgumentException("Facing must be 0–330 in 30-degree steps.");
            if (drop.HasValue && (drop < 0 || drop > Math.Min(255, map.Drops.Count)))
                throw new ArgumentException("Choose an existing drop entry.");

            var draft = Map.Load(map.Bytes());

            foreach (int i in ids)
            {
                var p = draft.Objects[i];
                if (facing.HasValue) p.Setting6 = (byte)(facing.Value / 30);
                if (drop.HasValue)   p.Setting7 = (byte)drop.Value;
                if (repeat.HasValue && p.Route.Count > 0)
                {
                    if (!repeat.Value) p.Link = 0;
                    else if (p.Link == 0) p.Link = 1;
                }
            }

            if (draft.Bytes().SequenceEqual(map.Bytes())) return;
            SyncPlacements(CommitGroup(draft));
            SetObjectSelection(ids);
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private void EditSharedSettings()
        {
            var ids = Preview.SelectedObjects;
            if (ids.Length < 2) return;

            var map = Preview.DropTableDraft();
            var objects = ids.Select(i => map.Objects[i]).ToArray();

            using (var form = new Form
            {
                Text = "Shared settings — " + ids.Length + " objects",
                Width = 580,
                Height = 370,
                MinimumSize = new Size(550, 370),
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(12),
                    ColumnCount = 2,
                    RowCount = 5,
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
                for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                form.Controls.Add(layout);

                var hint = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Only checked settings change. Mixed values remain untouched unless checked. " +
                           "Route behavior applies only to objects with waypoints.",
                };
                layout.Controls.Add(hint, 0, 0);
                layout.SetColumnSpan(hint, 2);

                var facingCheck = new CheckBox { Text = "Set initial facing",   AutoSize = true };
                var dropCheck   = new CheckBox { Text = "Set drop item",        AutoSize = true };
                var routeCheck  = new CheckBox { Text = "Set route behavior",   AutoSize = true };

                var facing = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
                var drop   = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
                var route  = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };

                facing.Items.Add("Choose facing…");
                for (int i = 0; i < 12; i++) facing.Items.Add((i * 30) + "°");
                drop.Items.Add("Choose drop…");
                drop.Items.Add("0 — No map override");
                for (int i = 0; i < Math.Min(255, map.Drops.Count); i++)
                    drop.Items.Add((i + 1) + " — " + map.Drops[i]);
                route.Items.AddRange(new object[] { "Choose route behavior…", "Once", "Repeat" });

                // Pre-select values when the selection is homogeneous.
                facing.SelectedIndex =
                    objects.Select(p => p.Setting6).Distinct().Count() == 1 && objects[0].Setting6 < 12
                        ? objects[0].Setting6 + 1 : 0;
                drop.SelectedIndex =
                    objects.Select(p => p.Setting7).Distinct().Count() == 1 && objects[0].Setting7 <= map.Drops.Count
                        ? objects[0].Setting7 + 1 : 0;

                var routed = objects.Where(p => p.Route.Count > 0).ToArray();
                route.SelectedIndex =
                    routed.Length > 0 && routed.Select(p => p.Link == 0).Distinct().Count() == 1
                        ? (routed[0].Link == 0 ? 1 : 2) : 0;
                routeCheck.Enabled = routed.Length > 0;

                var checks = new[] { facingCheck, dropCheck, routeCheck };
                var inputs = new[] { facing,      drop,      route };

                for (int i = 0; i < 3; i++)
                {
                    int index = i;
                    var panel = new Panel { Dock = DockStyle.Fill };
                    checks[i].Dock = DockStyle.Top;
                    panel.Controls.Add(new Label
                    {
                        Dock = DockStyle.Bottom,
                        Height = 22,
                        Text = inputs[i].SelectedIndex == 0
                            ? (i == 2 && routed.Length == 0 ? "No routes" : "Mixed / preserved values")
                            : "Current shared value",
                    });
                    panel.Controls.Add(checks[i]);
                    layout.Controls.Add(panel, 0, i + 1);
                    layout.Controls.Add(inputs[i], 1, i + 1);
                    checks[i].CheckedChanged += (s, e) => inputs[index].Enabled = checks[index].Checked;
                }

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var cancel  = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                var apply   = new Button { Text = "Apply" };
                buttons.Controls.AddRange(new Control[] { cancel, apply });
                layout.Controls.Add(buttons, 0, 4);
                layout.SetColumnSpan(buttons, 2);
                form.CancelButton = cancel;

                apply.Click += (s, e) =>
                {
                    try
                    {
                        for (int i = 0; i < 3; i++)
                            if (checks[i].Checked && inputs[i].SelectedIndex == 0)
                                throw new ArgumentException("Choose a value for each checked setting.");

                        Preview.ApplySharedSettings(
                            facingCheck.Checked ? (int?)((facing.SelectedIndex - 1) * 30) : null,
                            dropCheck.Checked   ? (int?)(drop.SelectedIndex - 1)           : null,
                            routeCheck.Checked  ? (bool?)(route.SelectedIndex == 2)        : null);
                        form.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "Cannot apply settings");
                    }
                };

                form.ShowDialog(this);
            }
        }
    }
}