using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        private string assetRoot;
        private string pendingName;
        private int pendingClone = -1;
        public byte PendingFacing;
        private uint pendingDisplayList;
        private string pendingListName;
        private bool pendingHover;
        private int pendingX, pendingY;

        public Func<int, string, Map> CommitReplace;
        public Func<string, int, int, int, Map> CommitCreate;
        public Func<int, Map> CommitDelete;

        public bool PlacementPending => pendingName != null;
        public string PendingName => pendingName;
        public int PendingClone => pendingClone;

        private static readonly HashSet<string> LibraryFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "buildings.obj",
            "desert_builds.obj",
            "flora.obj",
            "nature.obj",
            "ruins.obj",
            "zabors.obj",
        };

        private string SkinPath(Assets assets, Definition d)
        {
            if (d.Skin.Length > 0) return d.Skin;

            byte[] bytes = File.ReadAllBytes(assets.Resolve(d.Model));
            if (bytes.Length < 120
                || BitConverter.ToUInt32(bytes, 0) != 0x214C444D
                || BitConverter.ToInt32(bytes, 4) != 2)
                throw new InvalidDataException("Unsupported model.");

            int end = 12;
            while (end < 76 && bytes[end] != 0) end++;
            if (end == 76)
                throw new InvalidDataException("Invalid texture path.");

            return System.Text.Encoding.ASCII.GetString(bytes, 12, end - 12);
        }

        public string[] SceneryLibrary()
        {
            var names = new SortedSet<string>(map.Types, StringComparer.OrdinalIgnoreCase);
            var assets = GetAssets();
            foreach (string name in assets.Definitions.Keys)
                names.Add(name);

            var result = new string[names.Count];
            names.CopyTo(result);
            return result;
        }

        public void ArmPlacement(string name, int clone)
        {
            if (objectDrag) return;

            var assets = GetAssets();
            if (!assets.Definitions.ContainsKey(name) && !map.Types.Contains(name))
                throw new InvalidOperationException(
                    "Choose a known object definition or map prototype.");

            ObjectModel(assets, name, out string _);

            if (clone >= map.Objects.Count || clone < -1)
                throw new ArgumentOutOfRangeException(nameof(clone));

            SetEditingMode(EditingMode.Select);
            pendingGroup = null;
            pendingName = name;
            pendingClone = clone;
            if (clone >= 0 && clone < map.Objects.Count)
                PendingFacing = map.Objects[clone].Setting6;
            NotifySelection();
            Changed();
        }

        public void ArmDuplicate()
        {
            if (SelectedIndex < 0) return;

            var ids = SelectedObjects;
            ArmPlacement(map.Types[map.Objects[SelectedIndex].Type], SelectedIndex);

            pendingGroup = ids.Length > 1 ? ids : null;
        }

        public void RotatePending(int steps)
        {
            if (!PlacementPending) return;
            PendingFacing = (byte)(((PendingFacing + steps) % 12 + 12) % 12);
            NotifySelection();
            Changed();
        }

        private void HoverPending(int screenX, int screenY)
        {
            if (!PlacementPending)
            {
                pendingHover = false;
                return;
            }

            if (GroundAt(screenX, screenY, out Point3D hit))
            {
                int gx = MapWorld.SpawnGridX(hit.X);
                int gy = MapWorld.SpawnGridY(hit.Y);
                bool inside = gx >= short.MinValue && gy >= short.MinValue && gx <= short.MaxValue && gy <= short.MaxValue;
                if (inside != pendingHover || gx != pendingX || gy != pendingY)
                {
                    pendingHover = inside;
                    pendingX = gx;
                    pendingY = gy;
                    Invalidate();
                }
            }
            else if (pendingHover)
            {
                pendingHover = false;
                Invalidate();
            }
        }

        private void EnsurePendingDisplayList()
        {
            if (pendingDisplayList != 0 && string.Equals(pendingListName, pendingName, StringComparison.OrdinalIgnoreCase))
                return;

            if (pendingDisplayList != 0)
            {
                glDeleteLists(pendingDisplayList, 1);
                pendingDisplayList = 0;
            }
            pendingListName = pendingName;
            if (string.IsNullOrEmpty(pendingName)) return;

            var assets = GetAssets();
            var baseModel = ObjectModel(assets, pendingName, out string note);
            var parts = ObjectParts(assets, pendingName, baseModel, ref note);
            if (parts == null || parts.Count == 0) return;

            foreach (var part in parts)
                EnsureModelTexture(part.Material as System.Windows.Media.Media3D.DiffuseMaterial);

            pendingDisplayList = glGenLists(1);
            if (pendingDisplayList == 0) return;

            glNewList(pendingDisplayList, GL_COMPILE);
            foreach (var part in parts)
                EmitModel(part, isWater: false);
            glDepthMask(1);
            glDisable(GL_BLEND);
            glDisable(GL_POLYGON_OFFSET_FILL);
            glEndList();
        }

        private double PendingObjectZ(string name, int x, int y)
        {
            if (pendingClone >= 0 && pendingClone < map.Objects.Count)
                return ObjectZ(map.Objects[pendingClone], x, y);

            if (objectWaterZ.HasValue && waterObjects.Contains(name))
                return objectWaterZ.Value;

            var assets = GetAssets();
            var dummy = new Placement { X = (short)x, Y = (short)y };
            string note = "";
            int rule = AircraftRule(assets, dummy, name, ref note);
            if (rule == AircraftHeightFlying) return 100;
            if (rule == AircraftHeightLanding) return TerrainZ(x, y) + 7;

            return TerrainZ(x, y);
        }

        private void DrawPendingPlacement()
        {
            if (!PlacementPending || !pendingHover) return;

            EnsurePendingDisplayList();

            double worldX = MapWorld.SpawnX(pendingX);
            double worldY = MapWorld.SpawnY(pendingY);
            double worldZ = PendingObjectZ(pendingName, pendingX, pendingY);

            if (pendingDisplayList != 0)
            {
                glPushMatrix();
                glTranslatef((float)worldX, (float)worldY, (float)worldZ);
                glRotatef(PendingFacing * 30f, 0f, 0f, 1f);
                glCallList(pendingDisplayList);
                glPopMatrix();
            }

            glDisable(GL_TEXTURE_2D);
            glDisable(GL_FOG);
            glDisable(GL_DEPTH_TEST);

            double angle = PendingFacing * Math.PI / 6.0;
            float ax = (float)(worldX + Math.Cos(angle) * 32.0);
            float ay = (float)(worldY + Math.Sin(angle) * 32.0);
            float gz = (float)(TerrainZ(pendingX, pendingY) + 2.0);

            glColor4f(0.2f, 1.0f, 0.4f, 1.0f);
            glBegin(GL_LINES);
            float x0 = (float)(worldX - 20), x1 = (float)(worldX + 20);
            float y0 = (float)(worldY - 20), y1 = (float)(worldY + 20);
            glVertex3f(x0, y0, gz); glVertex3f(x1, y0, gz);
            glVertex3f(x1, y0, gz); glVertex3f(x1, y1, gz);
            glVertex3f(x1, y1, gz); glVertex3f(x0, y1, gz);
            glVertex3f(x0, y1, gz); glVertex3f(x0, y0, gz);
            glVertex3f((float)worldX, (float)worldY, gz);
            glVertex3f(ax, ay, gz);
            glEnd();

            if (pendingGroup != null && pendingGroup.Length > 1 && SelectedIndex >= 0 && SelectedIndex < map.Objects.Count)
            {
                int anchorX = map.Objects[SelectedIndex].X;
                int anchorY = map.Objects[SelectedIndex].Y;
                glColor4f(0.2f, 0.85f, 1.0f, 0.9f);
                glBegin(GL_LINES);
                foreach (int id in pendingGroup)
                {
                    if (id == SelectedIndex || id < 0 || id >= map.Objects.Count) continue;
                    int gx = pendingX + (map.Objects[id].X - anchorX);
                    int gy = pendingY + (map.Objects[id].Y - anchorY);
                    float cx = (float)MapWorld.SpawnX(gx);
                    float cy = (float)MapWorld.SpawnY(gy);
                    float cz = (float)(TerrainZ(
                        Math.Max(0, Math.Min(map.Width - 1, gx)),
                        Math.Max(0, Math.Min(map.Height - 1, gy))) + 2.0);
                    glVertex3f(cx - 18, cy - 18, cz); glVertex3f(cx + 18, cy - 18, cz);
                    glVertex3f(cx + 18, cy - 18, cz); glVertex3f(cx + 18, cy + 18, cz);
                    glVertex3f(cx + 18, cy + 18, cz); glVertex3f(cx - 18, cy + 18, cz);
                    glVertex3f(cx - 18, cy + 18, cz); glVertex3f(cx - 18, cy - 18, cz);
                }
                glEnd();
            }

            glEnable(GL_DEPTH_TEST);
        }

        public void CancelPlacement()
        {
            pendingName = null;
            pendingClone = -1;
            pendingGroup = null;
            pendingHover = false;
            if (pendingDisplayList != 0)
            {
                glDeleteLists(pendingDisplayList, 1);
                pendingDisplayList = 0;
            }
            pendingListName = null;
            NotifySelection();
            Invalidate();
        }

        public void PlacePendingAt(int x, int y)
        {
            if (!PlacementPending || CommitCreate == null) return;
            if (x < short.MinValue || y < short.MinValue || x > short.MaxValue || y > short.MaxValue) return;

            if (pendingGroup != null && CommitGroup != null)
            {
                PlaceGroup(x, y);
                return;
            }

            TerrainZ(x, y);

            string nameToKeep = pendingName;
            int cloneToKeep = pendingClone;

            Map updated = CommitCreate(pendingName, pendingClone, x, y);

            pendingName = nameToKeep;
            pendingClone = cloneToKeep;

            SyncPlacements(updated);

            groupSelection.Clear();
            SelectedIndex = map.Objects.Count - 1;
            NotifySelection();
        }

        private void PlacePendingScreen(int x, int y)
        {
            if (GroundAt(x, y, out System.Windows.Media.Media3D.Point3D p))
                PlacePendingAt(MapWorld.SpawnGridX(p.X), MapWorld.SpawnGridY(p.Y));
        }

        public void DeleteSelected()
        {
            if (objectDrag || SelectedIndex < 0 || CommitDelete == null) return;

            if (SelectedObjects.Length > 1 && CommitGroup != null)
            {
                int count = SelectedObjects.Length;
                if (count > 20
                    && MessageBox.Show(
                        "Delete " + count + " selected objects?\r\n" +
                        "Ctrl+Z can undo this.",
                        "Delete objects",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                DeleteGroup();
                return;
            }

            groupSelection.Clear();
            Map updated = CommitDelete(SelectedIndex);
            CancelPlacement();
            SyncPlacements(updated);
            SelectedIndex = -1;
            NotifySelection();
        }

        private void ReloadScenery(Map copy)
        {
            for (int i = scene.Children.Count - 1; i >= 0; i--)
                if (scene.Children[i] is System.Windows.Media.Media3D.GeometryModel3D g && sceneryIds.ContainsKey(g))
                    scene.Children.RemoveAt(i);
            sceneryIds.Clear();
            SceneryCount = 0;
            SceneryReport.Clear();

            map = copy;
            SelectedIndex = -1;
            LoadScenery(assetRoot, rebuildTerrainCache: false);
        }

        public void ReplaceSelected(string name)
        {
            if (objectDrag || routeDrag || SelectedIndex < 0 || CommitReplace == null) return;

            var assets = GetAssets();
            if (!assets.Definitions.ContainsKey(name) && !map.Types.Contains(name))
                throw new ArgumentException("Choose a known object.");

            if (map.Types[map.Objects[SelectedIndex].Type] == name) return;

            CancelPlacement();

            if (SelectedObjects.Length > 1 && CommitGroup != null)
            {
                var draft = Map.Load(map.Bytes());
                foreach (int i in SelectedObjects)
                {
                    draft.Objects[i].Type = draft.TypeId(name);
                }
                SyncPlacements(CommitGroup(draft));

                MessageBox.Show("Bulk replaced " + SelectedObjects.Length + " objects with " + name, "Replace");
            }
            else
            {
                SyncPlacements(CommitReplace(SelectedIndex, name));
            }
        }

        public void ChooseScenery(IWin32Window owner) => ChooseScenery(owner, replace: false);

        public void ChooseScenery(IWin32Window owner, bool replace)
        {
            if (replace && SelectedObjects.Length == 0) return;

            using (var form = new Form
            {
                Text = replace ? "Replace object — all definitions" : "Add object — all definitions",
                Width = 680,
                Height = 580,
                MinimumSize = new Size(560, 440),
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var assets = GetAssets();
                var names = SceneryLibrary();

                var filterPanel = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    Height = 62,
                    ColumnCount = 2,
                    RowCount = 2,
                    Padding = new Padding(6, 6, 6, 2),
                };
                filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
                filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                filterPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
                filterPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

                var categoryBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                categoryBox.Items.AddRange(new object[]
                {
                    "All objects",
                    "Tanks",
                    "Aircraft / Bosses",
                    "Vehicles / launchers",
                    "Turrets & cannons",
                    "Ships",
                    "Buildings & structures",
                    "Flora & nature",
                    "Pickups",
                    "Other",
                });

                var sourceFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in assets.Definitions.Values)
                {
                    if (!string.IsNullOrEmpty(d.Source))
                        sourceFiles.Add(Path.GetFileName(d.Source));
                }
                foreach (string src in sourceFiles)
                    categoryBox.Items.Add("File: " + src);
                categoryBox.SelectedIndex = 0;

                var search = new TextBox { Dock = DockStyle.Fill };

                filterPanel.Controls.Add(new Label { Text = "Category / file:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 0);
                filterPanel.Controls.Add(categoryBox, 1, 0);
                filterPanel.Controls.Add(new Label { Text = "Search name:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 1);
                filterPanel.Controls.Add(search, 1, 1);

                var split = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    SplitterDistance = 300,
                    Padding = new Padding(6),
                };

                var listBox = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                var countLabel = new Label { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft };
                split.Panel1.Controls.Add(listBox);
                split.Panel1.Controls.Add(countLabel);

                var previewBox = new PictureBox
                {
                    Dock = DockStyle.Top,
                    Height = 190,
                    BackColor = Color.FromArgb(24, 30, 38),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BorderStyle = BorderStyle.FixedSingle,
                };
                var detailsLabel = new Label
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(4, 8, 4, 4),
                    AutoEllipsis = true,
                };
                var preview3dBtn = new Button
                {
                    Text = "Open 3D Model Preview…",
                    Dock = DockStyle.Bottom,
                    Height = 28,
                    Enabled = false,
                };
                split.Panel2.Controls.Add(detailsLabel);
                split.Panel2.Controls.Add(previewBox);
                split.Panel2.Controls.Add(preview3dBtn);

                var add = new Button
                {
                    Text = replace ? "Replace selected" : "Place selected",
                    Dock = DockStyle.Bottom,
                    Height = 32,
                };

                var hint = new Label
                {
                    Dock = DockStyle.Bottom,
                    Height = 46,
                    Padding = new Padding(6, 4, 6, 4),
                    Text = replace
                        ? "Keeps position, facing, drops, route and script override. " +
                          "A retained override still controls behavior; clear it in " +
                          "properties to use the new default."
                        : "Tip: While placing, move cursor to preview in 3D and press R or [ / ] to rotate.",
                };

                Action updatePreview = () =>
                {
                    string selected = listBox.SelectedItem as string;
                    if (string.IsNullOrEmpty(selected))
                    {
                        previewBox.Image = null;
                        detailsLabel.Text = "Select an object to see its model preview and definition details.";
                        preview3dBtn.Enabled = false;
                        return;
                    }

                    var thumb = assets.Get(selected);
                    previewBox.Image = thumb?.Image;

                    if (assets.Definitions.TryGetValue(selected, out var def))
                    {
                        string srcFile = string.IsNullOrEmpty(def.Source) ? "(none)" : Path.GetFileName(def.Source);
                        string flags = "";
                        if (def.OnWater) flags += "FL_ONWATER ";
                        if (def.Hidden) flags += "FL_NODRAW ";
                        if (def.Additive) flags += "BLEND_ADD ";
                        if (flags.Length == 0) flags = "(none)";

                        detailsLabel.Text =
                            "Name: " + def.Name + "\r\n" +
                            "Category: " + GlTerrainWindow.ObjectCategory(def.Name) + "\r\n" +
                            "Source: " + srcFile + "\r\n" +
                            "Model: " + (def.Model.Length > 0 ? def.Model : "(none)") + "\r\n" +
                            "Skin: " + (def.Skin.Length > 0 ? def.Skin : "(default)") + "\r\n" +
                            "Script: " + (def.Script.Length > 0 ? def.Script : "(none)") + "\r\n" +
                            "Attachments: " + def.Attachments.Count + "\r\n" +
                            "Flags: " + flags;
                        preview3dBtn.Enabled = def.Model.Length > 0 && !def.Hidden && !def.Additive;
                    }
                    else
                    {
                        detailsLabel.Text =
                            "Name: " + selected + "\r\n" +
                            "Map prototype (not found in .obj files).";
                        preview3dBtn.Enabled = false;
                    }
                };

                preview3dBtn.Click += (s, e) =>
                {
                    string selected = listBox.SelectedItem as string;
                    if (string.IsNullOrEmpty(selected)) return;
                    try
                    {
                        using (var viewer = new ModelViewer(assets, selected))
                            viewer.ShowDialog(form);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "3D Model Preview");
                    }
                };

                Action filter = () =>
                {
                    string cat = categoryBox.SelectedItem as string ?? "All objects";
                    string q = search.Text.Trim();
                    listBox.BeginUpdate();
                    try
                    {
                        listBox.Items.Clear();
                        foreach (string name in names)
                        {
                            if (q.Length > 0 && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            if (cat.StartsWith("File: ", StringComparison.Ordinal))
                            {
                                string targetFile = cat.Substring(6);
                                if (!assets.Definitions.TryGetValue(name, out var d)
                                    || !string.Equals(Path.GetFileName(d.Source), targetFile, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }
                            else if (cat != "All objects")
                            {
                                if (GlTerrainWindow.ObjectCategory(name) != cat)
                                    continue;
                            }

                            listBox.Items.Add(name);
                        }
                        countLabel.Text = listBox.Items.Count + " / " + names.Length + " objects";
                        if (listBox.Items.Count > 0)
                            listBox.SelectedIndex = 0;
                        else
                            updatePreview();
                    }
                    finally
                    {
                        listBox.EndUpdate();
                    }
                };

                search.TextChanged += (s, e) => filter();
                categoryBox.SelectedIndexChanged += (s, e) => filter();
                listBox.SelectedIndexChanged += (s, e) => updatePreview();

                Action choose = () =>
                {
                    if (listBox.SelectedItem == null) return;
                    try
                    {
                        if (replace) ReplaceSelected((string)listBox.SelectedItem);
                        else ArmPlacement((string)listBox.SelectedItem, -1);
                        form.DialogResult = DialogResult.OK;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "Cannot place object");
                    }
                };
                add.Click += (s, e) => choose();
                listBox.DoubleClick += (s, e) => choose();

                form.Controls.Add(split);
                form.Controls.Add(filterPanel);
                form.Controls.Add(hint);
                form.Controls.Add(add);
                filter();
                form.ShowDialog(owner);
            }
        }
    }
}