using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Helpers for editing a map's drop table without corrupting the
    /// per-object drop references.
    /// </summary>
    public static class DropTableTools
    {
        /// <summary>
        /// Removes one entry from the drop table and remaps every object that
        /// referenced it (or a later entry) so that all references remain
        /// valid and consistent.
        /// </summary>
        /// <param name="map">The map to modify in place.</param>
        /// <param name="index">Zero-based index of the entry to remove.</param>
        /// <param name="replacement">
        /// What to assign to objects that referenced the removed entry:
        /// 0 = no map override, or a 1-based entry number in the new table.
        /// Must not equal <c>index + 1</c> (the removed entry).
        /// </param>
        public static void Remove(Map map, int index, int replacement)
        {
            if (index < 0 || index >= map.Drops.Count
                || replacement < 0 || replacement > Math.Min(255, map.Drops.Count)
                || replacement == index + 1)
            {
                throw new ArgumentException("Choose another entry or No map override.");
            }

            foreach (var p in map.Objects)
            {
                int value = p.Setting7;
                if (value == index + 1) value = replacement;
                if (value > index + 1 && value <= map.Drops.Count) value--;
                p.Setting7 = (byte)value;
            }

            map.Drops.RemoveAt(index);
        }
    }

    public sealed partial class GlTerrainControl
    {
        /// <summary>Returns a fresh copy of the current map, for the drop dialog.</summary>
        public Map DropTableDraft() => Map.Load(map.Bytes());

        /// <summary>Commits a modified drop table, via <see cref="CommitGroup"/>.</summary>
        public void ApplyDropTable(Map draft)
        {
            if (CommitGroup == null) return;
            if (draft.Bytes().SequenceEqual(map.Bytes())) return;
            SyncPlacements(CommitGroup(draft));
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private void EditDropTable()
        {
            var draft = Preview.DropTableDraft();

            using (var dialog = new Form
            {
                Text = "Drop-item table",
                Width = 650,
                Height = 540,
                MinimumSize = new Size(540, 460),
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 5,
                    Padding = new Padding(8),
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                // Rows: description, list (fills), edit controls, removal
                // controls, buttons.
                foreach (int h in new[] { 55, 0, 64, 48, 38 })
                    layout.RowStyles.Add(h == 0
                        ? new RowStyle(SizeType.Percent, 100)
                        : new RowStyle(SizeType.Absolute, h));
                dialog.Controls.Add(layout);

                layout.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "Objects reference numbered entries. Replacing an entry " +
                           "changes the reward for every user of that entry. Removing an " +
                           "entry remaps its users and preserves other valid references. " +
                           "0 means no map override; scripts may still provide drops.",
                }, 0, 0);

                var list = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    FullRowSelect = true,
                    MultiSelect = false,
                    HideSelection = false,
                };
                list.Columns.Add("Entry", 50);
                list.Columns.Add("Pickup", 200);
                list.Columns.Add("Used by object #", 320);
                layout.Controls.Add(list, 0, 1);

                var edit = new FlowLayoutPanel { Dock = DockStyle.Fill };
                var picker = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
                picker.Items.AddRange(
                    inspectorAssets.Definitions.Keys
                        .Where(n => n.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(n => n)
                        .ToArray());
                if (picker.Items.Count > 0) picker.SelectedIndex = 0;
                var add     = new Button { Text = "Add" };
                var replace = new Button { Text = "Replace" };
                edit.Controls.AddRange(new Control[]
                {
                    new Label { Text = "Pickup", AutoSize = true },
                    picker, add, replace,
                });
                layout.Controls.Add(edit, 0, 2);

                var removal = new FlowLayoutPanel { Dock = DockStyle.Fill };
                var target = new ComboBox { Width = 270, DropDownStyle = ComboBoxStyle.DropDownList };
                var remove = new Button { Text = "Remove", AutoSize = true };
                removal.Controls.AddRange(new Control[]
                {
                    new Label { Text = "Reassign users to", AutoSize = true },
                    target, remove,
                });
                layout.Controls.Add(removal, 0, 3);

                var bottom = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                var apply  = new Button { Text = "Apply" };
                bottom.Controls.AddRange(new Control[] { cancel, apply });
                layout.Controls.Add(bottom, 0, 4);
                dialog.CancelButton = cancel;

                Action refresh = () =>
                {
                    // Table list.
                    list.Items.Clear();
                    for (int i = 0; i < draft.Drops.Count; i++)
                    {
                        int entry = i + 1;
                        string users = string.Join(", ",
                            draft.Objects
                                .Select((p, j) => new { p, j })
                                .Where(a => a.p.Setting7 == entry)
                                .Select(a => "#" + a.j)
                                .ToArray());

                        list.Items.Add(new ListViewItem(new[]
                        {
                            entry.ToString(),
                            draft.Drops[i],
                            users.Length == 0
                                ? "Unused"
                                : draft.Objects.Count(p => p.Setting7 == entry) + " objects: " + users,
                        }));
                    }

                    // Target combobox for removal.
                    target.Items.Clear();
                    target.Items.Add("0 — No map override");
                    for (int i = 0; i < Math.Min(255, draft.Drops.Count); i++)
                        target.Items.Add((i + 1) + " — " + draft.Drops[i]);
                    target.SelectedIndex = 0;

                    remove.Enabled = false;
                    replace.Enabled = false;
                    add.Enabled = draft.Drops.Count < 255 && picker.Items.Count > 0;
                };

                list.SelectedIndexChanged += (s, e) =>
                {
                    remove.Enabled = replace.Enabled = list.SelectedIndices.Count > 0;
                };

                add.Click += (s, e) =>
                {
                    if (picker.SelectedItem == null) return;
                    draft.Drops.Add((string)picker.SelectedItem);
                    refresh();
                };

                replace.Click += (s, e) =>
                {
                    if (list.SelectedIndices.Count == 0 || picker.SelectedItem == null) return;
                    draft.Drops[list.SelectedIndices[0]] = (string)picker.SelectedItem;
                    refresh();
                };

                remove.Click += (s, e) =>
                {
                    if (list.SelectedIndices.Count == 0) return;
                    try
                    {
                        DropTableTools.Remove(draft, list.SelectedIndices[0], target.SelectedIndex);
                        refresh();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, "Cannot remove entry");
                    }
                };

                apply.Click += (s, e) =>
                {
                    try
                    {
                        Preview.ApplyDropTable(draft);
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, "Could not apply drop table");
                    }
                };

                refresh();
                dialog.ShowDialog(this);
            }
        }
    }
}