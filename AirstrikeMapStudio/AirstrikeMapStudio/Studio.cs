using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Text;

namespace AirstrikeStudio
{
    public partial class Studio : Form
    {
        // ── Core map state ──────────────────────────────────────────────
        Map map;
        Assets assets = new Assets();

        private int testSelected = -1;

        string file = "";
        bool dirty;
        byte[] saved;
        List<byte[]> undo = new List<byte[]>(), redo = new List<byte[]>();

        // ── UI controls ─────────────────────────────────────────────────
        ToolStripStatusLabel status = new ToolStripStatusLabel();
        Label mapInfo = new Label();
        public List<string> RecentObjects = new List<string>();

        // ── Constructor ─────────────────────────────────────────────────

        public Studio()
        {
            Text = "Airstrike Map Studio (3D Only)";
            try
            {
                string iconPath = System.IO.Path.Combine(Application.StartupPath, "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch { }
            Width = 800;
            Height = 600;
            MinimumSize = new Size(600, 400);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(24, 31, 40);
            ForeColor = Color.FromArgb(224, 232, 242);
            KeyPreview = true;

            // ── Menu ────────────────────────────────────────────────────
            var menu = new MenuStrip();

            var fm = new ToolStripMenuItem("File");
            AddMenu(fm, "New map…", Keys.Control | Keys.N, NewMap);
            AddMenu(fm, "Open map…", Keys.Control | Keys.O, OpenMap);
            AddMenu(fm, "Save", Keys.Control | Keys.S, () => Save(false));
            AddMenu(fm, "Save as…", Keys.Control | Keys.Shift | Keys.S, () => Save(true));
            fm.DropDownItems.Add(new ToolStripSeparator());
            AddMenu(fm, "Mission settings…", Keys.None, () => EditMissionSettings(this));
            AddMenu(fm, "Resize map…", Keys.None, ResizeMap);
            AddMenu(fm, "Choose game data folder…", Keys.None, PickAssets);
            fm.DropDownItems.Add(new ToolStripSeparator());
            AddMenu(fm, "Exit", Keys.Alt | Keys.F4, () => Close());
            menu.Items.Add(fm);

            var tm = new ToolStripMenuItem("Tools");
            AddMenu(tm, "Check map…", Keys.F5, ValidateMap);
            AddMenu(tm, "Script Studio (.scr editor)…", Keys.F4, () => OpenScriptStudioStandalone());
            AddMenu(tm, "Test mission…", Keys.F6, () => OpenTestMission(this));
            AddMenu(tm, "Verify map integrity (headless)…", Keys.F7, () => VerifyCurrentMapHeadless(this));
            menu.Items.Add(tm);

            var hm = new ToolStripMenuItem("Help");
            AddMenu(hm, "Controls and known limits", Keys.F1, Help);
            menu.Items.Add(hm);

            MainMenuStrip = menu;

            // ── Launcher UI ─────────────────────────────────────────────
            var launcherPanel = new Panel { Dock = DockStyle.Fill };

            var titleLabel = new Label
            {
                Text = "Airstrike Map Studio",
                Font = new Font("Segoe UI", 26, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(40, 40)
            };

            var subLabel = new Label
            {
                Text = "3D Editor Workspace",
                Font = new Font("Segoe UI", 14),
                ForeColor = Color.LightSteelBlue,
                AutoSize = true,
                Location = new Point(44, 90)
            };

            var openButton = new Button
            {
                Text = "Open Map in 3D Editor",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Size = new Size(240, 45),
                Location = new Point(44, 150)
            };
            openButton.Click += (s, e) => OpenMap();

            var newButton = new Button
            {
                Text = "Create New Map",
                Font = new Font("Segoe UI", 12),
                Size = new Size(240, 45),
                Location = new Point(44, 210)
            };
            newButton.Click += (s, e) => NewMap();

            var scriptStudioButton = new Button
            {
                Text = "Script Studio (.scr)",
                Font = new Font("Segoe UI", 12),
                Size = new Size(240, 45),
                Location = new Point(44, 270)
            };
            scriptStudioButton.Click += (s, e) => OpenScriptStudioStandalone();

            mapInfo.AutoSize = true;
            mapInfo.Location = new Point(44, 335);
            mapInfo.Text = "No map currently loaded.";
            mapInfo.ForeColor = Color.LightGray;

            launcherPanel.Controls.Add(titleLabel);
            launcherPanel.Controls.Add(subLabel);
            launcherPanel.Controls.Add(openButton);
            launcherPanel.Controls.Add(newButton);
            launcherPanel.Controls.Add(scriptStudioButton);
            launcherPanel.Controls.Add(mapInfo);

            var bottom = new StatusStrip();
            bottom.Items.Add(status);
            status.Text = "Ready";

            Controls.Add(launcherPanel);
            Controls.Add(menu);
            Controls.Add(bottom);

            SetupRecovery();
            FormClosing += (s, e) => { if (!CanLeave()) e.Cancel = true; else ClearRecovery(); };
            FormClosed += (s, e) => { assets.Dispose(); };

            Shown += (s, e) =>
            {
                if (OfferRecovery()) return;
                if (Environment.GetCommandLineArgs().Length == 2
                    && File.Exists(Environment.GetCommandLineArgs()[1]))
                    OpenMapIn3D(Environment.GetCommandLineArgs()[1]);
            };
        }

        static void AddMenu(ToolStripMenuItem parent, string label, Keys shortcut, Action action)
        {
            var item = new ToolStripMenuItem(label);
            item.ShortcutKeys = shortcut;
            item.Click += (s, e) => Run(action);
            parent.DropDownItems.Add(item);
        }

        static void Run(Action a)
        {
            try { a(); }
            catch (Exception e) { MessageBox.Show(e.Message, "Airstrike Map Studio", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        static bool Equal(byte[] a, byte[] b) => a != null && b != null && a.SequenceEqual(b);

        bool CanLeave()
        {
            if (!dirty) { ClearRecovery(); return true; }
            var result = MessageBox.Show(
                "Save your map changes before continuing?",
                "Unsaved changes",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) return false;
            if (result == DialogResult.No) { ClearRecovery(); return true; }
            try { return Save(false); }
            catch (Exception e) { MessageBox.Show(e.Message); return false; }
        }

        void ResizeMap()
        {
            if (map == null) return;

            using (var dialog = new ResizeMapDialog(map.Width, map.Height))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (dialog.NewWidth == map.Width && dialog.NewHeight == map.Height) return;

                Edit(() =>
                {
                    map.Resize(dialog.NewWidth, dialog.NewHeight, dialog.FillHeight);
                });

                if (ActiveForm is GlTerrainWindow glWindow)
                {
                    glWindow.Preview.SyncPlacements(Map.Load(map.Bytes()));
                }

                mapInfo.Text = "Loaded: " + (file.Length == 0 ? "Untitled" : Path.GetFileName(file)) +
                               " (" + map.Width + "x" + map.Height + ")";
                status.Text = "Map resized to " + map.Width + "x" + map.Height;
            }
        }

        void OpenMap()
        {
            if (!CanLeave()) return;
            using (var d = new OpenFileDialog { Filter = "Airstrike maps (*.hsc)|*.hsc|All files (*.*)|*.*" })
                if (d.ShowDialog() == DialogResult.OK)
                    OpenMapIn3D(d.FileName);
        }

        void OpenMapIn3D(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (!Equal(bytes, Map.Load(bytes).Bytes()))
                throw new InvalidDataException("Map does not round-trip exactly.");

            LoadMap(path);
            OpenGlWorkspace();
        }

        public void LoadMap(string path)
        {
            Run(() =>
            {
                byte[] b = File.ReadAllBytes(path);
                var m = Map.Load(b);
                if (!Equal(b, m.Bytes()))
                    throw new InvalidDataException("This file does not round-trip exactly. Editing has been refused.");
                string profile = MissionPreset.ReadProfile(path);
                map = m;
                environmentOverride = profile;
                file = path;
                saved = b;
                dirty = false;
                undo.Clear();
                redo.Clear();

                var parent = Directory.GetParent(path);
                if (parent != null && parent.Parent != null
                    && Directory.Exists(Path.Combine(parent.Parent.FullName, "objects")))
                    LoadAssets(parent.Parent.FullName);

                RefreshAll(true);
                mapInfo.Text = "Loaded: " + Path.GetFileName(file) + " (" + map.Width + "x" + map.Height + ")";
            });
        }

        void NewMap()
        {
            if (CreateNewDocument(this))
            {
                mapInfo.Text = "New map created (Unsaved)";
                OpenGlWorkspace();
            }
        }

        bool Save(bool asNew)
        {
            if (map == null) return false;
            string path = file;
            if (asNew || path.Length == 0)
            {
                using (var d = new SaveFileDialog
                {
                    Filter = "Airstrike map (*.hsc)|*.hsc",
                    FileName = path.Length == 0 ? "new-map.hsc" : Path.GetFileName(path),
                    OverwritePrompt = true,
                })
                {
                    if (d.ShowDialog() != DialogResult.OK) return false;
                    path = d.FileName;
                }
            }

            if (!ShowMapChecks(Form.ActiveForm ?? this, path, true)) return false;
            byte[] data = map.Bytes();
            Map.Save(path, data);
            if (environmentOverride != null) MissionPreset.SaveProfile(path, environmentOverride);
            file = path;
            saved = data;
            dirty = false;
            ClearRecovery();
            UpdateTitle();
            status.Text = "Saved " + path;

            mapInfo.Text = "Loaded: " + Path.GetFileName(file) + " (" + map.Width + "x" + map.Height + ")";
            return true;
        }

        void PickAssets()
        {
            using (var d = new FolderBrowserDialog
            {
                Description = "Select the game's data folder (contains objects, models, maps, textures)",
            })
                if (d.ShowDialog() == DialogResult.OK)
                {
                    LoadAssets(d.SelectedPath);
                    RefreshAll(true);
                }
        }

        void LoadAssets(string path)
        {
            assets.Open(path);
        }

        void OpenScriptStudioStandalone(string initialScript = null)
        {
            if (string.IsNullOrEmpty(assets.Root) || !Directory.Exists(assets.Root))
            {
                PickAssets();
                if (string.IsNullOrEmpty(assets.Root) || !Directory.Exists(assets.Root)) return;
            }
            using (var dlg = new ScriptStudioDialog(assets, null, null, initialScript))
            {
                dlg.ShowDialog(this);
            }
        }

        void OpenGlWorkspace()
        {
            if (map == null) return;
            double[] restoreView = null;
            while (true)
            {
                string next = null;
                bool created = false;
                using (var viewer = new GlTerrainWindow(map, file, assets.Root, environmentOverride))
                {
                    viewer.RecentObjects = RecentObjects;
                    if (restoreView != null)
                    {
                        viewer.Preview.CenterX = restoreView[0];
                        viewer.Preview.CenterY = restoreView[1];
                        viewer.Preview.Heading = restoreView[2];
                        viewer.Preview.Pitch = restoreView[3];
                        viewer.Preview.Distance = restoreView[4];
                        restoreView = null;
                    }

                    ConnectGlMoves(viewer.Preview);

                    viewer.ConnectDocument(
                        asNew => Save(asNew),
                        () => (dirty ? "● " : "") + (file.Length == 0 ? "Untitled" : Path.GetFileName(file)),
                        () =>
                        {
                            using (var dialog = new OpenFileDialog { Filter = "Airstrike maps (*.hsc)|*.hsc" })
                            {
                                if (dialog.ShowDialog(viewer) != DialogResult.OK) return null;
                                byte[] bytes = File.ReadAllBytes(dialog.FileName);
                                if (!Equal(bytes, Map.Load(bytes).Bytes()))
                                    throw new InvalidDataException("Map does not round-trip exactly.");
                                if (!CanLeave()) return null;
                                return dialog.FileName;
                            }
                        });

                    viewer.ConnectAssetTools(assets.Root, ChangeWorkspaceAssets, WorkspaceAssetReport);
                    viewer.ConnectTestMission(
                        () => OpenTestMission(viewer),
                        () => new TestMissionSession(assets.Root).Active,
                        () => VerifyCurrentMapHeadless(viewer));
                    viewer.ConnectDocumentState(() => dirty);
                    viewer.ConnectRecoveryStatus(() => recoveryStatus);
                    viewer.ConnectNewDocument(() => CreateNewDocument(viewer));
                    viewer.ConnectMissionSettings(() => EditMissionSettings(viewer));
                    viewer.ConnectResizeMap(() =>
                    {
                        using (var dialog = new ResizeMapDialog(map.Width, map.Height))
                        {
                            if (dialog.ShowDialog(viewer) != DialogResult.OK) return false;
                            if (dialog.NewWidth == map.Width && dialog.NewHeight == map.Height) return false;

                            Edit(() =>
                            {
                                map.Resize(dialog.NewWidth, dialog.NewHeight, dialog.FillHeight);
                            });
                            mapInfo.Text = "Loaded: " + (file.Length == 0 ? "Untitled" : Path.GetFileName(file)) +
                                           " (" + map.Width + "x" + map.Height + ")";
                            status.Text = "Map resized to " + map.Width + "x" + map.Height;
                            return true;
                        }
                    });
                    viewer.ConnectDropTable();

                    checkFocus = viewer.Preview.FocusCheckIssue;
                    viewer.ConnectMapChecks(() => ShowMapChecks(viewer, file, false));
                    viewer.ConnectWorkspacePreferences(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "editor-preferences.txt"));

                    try { viewer.ShowDialog(this); }
                    finally { checkFocus = null; }

                    next = viewer.NextMapPath;
                    created = viewer.NextMapCreated;

                    if (viewer.MissionSettingsChanged || viewer.AssetFolderChanged || viewer.MapResized)
                        restoreView = new[]
                        {
                            viewer.Preview.CenterX,
                            viewer.Preview.CenterY,
                            viewer.Preview.Heading,
                            viewer.Preview.Pitch,
                            viewer.Preview.Distance,
                        };
                }
                if (created) continue;
                if (next == null) break;
                LoadMap(next);
            }
        }

        void ConnectGlMoves(GlTerrainControl preview)
        {
            int moveUndo = undo.Count, moveRedo = redo.Count;

            preview.CommitGroup = draft =>
            {
                Edit(() => { map = Map.Load(draft.Bytes()); testSelected = -1; });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitTerrain = cells =>
            {
                Edit(() => { map.Cells = (uint[])cells.Clone(); });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitReplace = (index, name) =>
            {
                Edit(() => { map.Objects[index].Type = map.TypeId(name); testSelected = index; });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitMove = (index, x, y, routes) =>
            {
                Edit(() =>
                {
                    var target = map.Objects[index];
                    target.Shift(x - target.X, y - target.Y, routes);
                    testSelected = index;
                });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitRotation = (index, facing) =>
            {
                Edit(() => { map.Objects[index].Setting6 = facing; testSelected = index; });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitCreate = (name, clone, x, y) =>
            {
                Bounds(x, y);
                Edit(() =>
                {
                    Placement created;
                    if (clone >= 0)
                    {
                        created = map.Objects[clone].Clone();
                        created.Shift(x - created.X, y - created.Y, true);
                        created.Setting6 = preview.PendingFacing;
                    }
                    else
                    {
                        created = new Placement
                        {
                            Type = map.TypeId(name),
                            X = (short)x,
                            Y = (short)y,
                            Setting6 = preview.PendingFacing,
                            Link = -1,
                        };
                    }
                    map.Objects.Add(created);
                    testSelected = map.Objects.Count - 1;

                    RecentObjects.Remove(name);
                    RecentObjects.Insert(0, name);
                    if (RecentObjects.Count > 5) RecentObjects.RemoveAt(5);
                });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitDelete = index =>
            {
                Edit(() => { map.Objects.RemoveAt(index); testSelected = -1; });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.CommitProperties = (index, draft) =>
            {
                Bounds(draft.X, draft.Y);
                Edit(() => { map.Objects[index] = draft.Clone(); testSelected = index; });
                moveUndo = Math.Min(80, moveUndo + 1);
                moveRedo = 0;
                return map;
            };
            preview.UndoMove = () => { if (moveUndo > 0) { Undo(); moveUndo--; moveRedo++; } return map; };
            preview.RedoMove = () => { if (moveRedo > 0) { Redo(); moveRedo--; moveUndo++; } return map; };
        }

        void PushUndo(byte[] b)
        {
            undo.Add(b);
            if (undo.Count > 80) undo.RemoveAt(0);
            redo.Clear();
        }

        void Edit(Action a)
        {
            if (map == null) return;
            byte[] before = map.Bytes();
            try
            {
                a();
                map.Bytes();   // validate
            }
            catch
            {
                map = Map.Load(before);
                RefreshAll(false);
                throw;
            }
            if (!Equal(before, map.Bytes()))
            {
                PushUndo(before);
                dirty = true;
            }
            RefreshAll(false);
        }

        void Undo()
        {
            if (map == null || undo.Count == 0) return;
            redo.Add(map.Bytes());
            byte[] b = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            Restore(b);
        }

        void Redo()
        {
            if (map == null || redo.Count == 0) return;
            undo.Add(map.Bytes());
            byte[] b = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            Restore(b);
        }

        void Restore(byte[] b)
        {
            map = Map.Load(b);
            dirty = !Equal(saved, b);
            testSelected = Math.Min(testSelected, map.Objects.Count - 1);
            RefreshAll(true);
        }

        void UpdateTitle()
        {
            Text = (dirty ? "● " : "") + (file.Length > 0 ? Path.GetFileName(file) : "Untitled") + " — Airstrike Map Studio (3D Only)";
        }

        void RefreshAll(bool full)
        {
            UpdateTitle();
        }

        void SelectObject(int i, int w, bool focus)
        {
            if (map == null) return;
            testSelected = i >= 0 && i < map.Objects.Count ? i : -1;
        }

        Placement Selected()
        {
            return map != null && testSelected >= 0 && testSelected < map.Objects.Count
                ? map.Objects[testSelected]
                : null;
        }

        new void Bounds(int x, int y)
        {
            if (x < short.MinValue || x > short.MaxValue || y < short.MinValue || y > short.MaxValue)
                throw new Exception("Position must be within the signed 16-bit limits (-32768 to 32767).");
        }

        void ExportRoute()
        {
            var p = Selected();
            if (p == null) return;
            using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "waypoints.csv" })
                if (d.ShowDialog() == DialogResult.OK)
                    File.WriteAllText(d.FileName, WaypointCsv.Format(p));
        }

        void ValidateMap()
        {
            if (map == null) return;
            ShowMapChecks(this, file, false);
        }

        void Help()
        {
            EditorHelp.Show(this);
        }

        public void PreviewForTest()
        {
            if (map == null) return;
            int i = map.Objects.FindIndex(p => map.Types[p.Type] == "tank_flame" && p.X == 22 && p.Y == 189);
            if (i < 0) i = map.Objects.FindIndex(p => p.Route.Count > 0);
            if (i >= 0)
            {
                SelectObject(i, -1, false);
            }
        }

        public void TestInteractions()
        {
            if (map == null) throw new Exception("No map for UI test.");
            byte[] original = map.Bytes();
            int i = map.Objects.FindIndex(p => p.Route.Count > 0);
            if (i < 0) return;

            {
                var draft = map.Objects[i].Clone();
                int last = draft.Route.Count - 1;
                if (RouteTools.AlignArrival(draft, last))
                {
                    Edit(() => map.Objects[i] = draft);
                    if (Equal(map.Bytes(), original))
                        throw new Exception("Align approach did not modify anything.");
                    Undo();
                    if (!Equal(map.Bytes(), original))
                        throw new Exception("Align approach undo failed.");
                }
            }

            SelectObject(i, -1, false);
            int count = map.Objects[i].Route.Count;
            Edit(() =>
            {
                RouteTools.InsertWaypoint(map.Objects[i], map.Objects[i].Route.Count, map.Height);
            });
            if (map.Objects[i].Route.Count != count + 1) throw new Exception("Add waypoint failed.");
            Undo();
            if (!Equal(map.Bytes(), original)) throw new Exception("UI undo failed.");
            Redo();
            if (map.Objects[i].Route.Count != count + 1) throw new Exception("UI redo failed.");
            Undo();

            SelectObject(i, 0, false);
            var wp = map.Objects[i].Route[0];
            int oldX = wp.X;
            float oldAX = wp.AX;
            new WaypointProperties(wp).X = oldX + 1;
            if (wp.AX != oldAX + 1) throw new Exception("Waypoint property translation failed.");

            map = Map.Load(original);
            SelectObject(i, -1, false);

            Edit(() =>
            {
                var p = map.Objects[i].Clone();
                p.Shift(1, 0, true);
                map.Objects.Add(p);
            });
            if (map.Objects.Count != Map.Load(original).Objects.Count + 1) throw new Exception("UI clone failed.");
            Undo();
            if (!Equal(map.Bytes(), original)) throw new Exception("Clone undo failed.");

            Edit(() =>
            {
                var named = new ObjectProperties(map.Objects[i], map);
                named.InitialFacing = 90;
                named.LoopRoute = "Once";
                named.DropItem = "0 — No map override";
            });
            Undo();
            if (!Equal(map.Bytes(), original)) throw new Exception("Named settings undo failed.");

            var replacementName = map.Types[map.Objects[i].Type] == "tank_flame"
                ? "rocketlauncher_mobile_lgreen"
                : "tank_flame";
            SelectObject(i, -1, false);
            Edit(() => map.Objects[i].Type = map.TypeId(replacementName));
            byte[] replaced = map.Bytes();
            if (map.Types[map.Objects[i].Type] != replacementName || map.Objects[i].Route.Count != count)
                throw new Exception("Replacement or route preservation failed.");

            var expected = Map.Load(original);
            expected.Objects[i].Type = expected.TypeId(replacementName);
            if (!Equal(replaced, expected.Bytes())) throw new Exception("Replacement changed placement data beyond type.");

            Undo();
            if (!Equal(map.Bytes(), original)) throw new Exception("Replacement undo failed.");
            Redo();
            if (!Equal(map.Bytes(), replaced)) throw new Exception("Replacement redo failed.");
            Undo();

            Console.WriteLine("PASS object replacement preserves all other bytes, undo and redo");
            dirty = false;
            Console.WriteLine("PASS named settings undo; UI waypoint add, undo/redo, property translation and clone undo");
        }

        public static string[] Ask(string title, string[] labels, string[] values, bool multiline = false)
        {
            using (var f = new Form
            {
                Text = title,
                Width = 520,
                Height = multiline ? 430 : 160 + labels.Length * 50,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = new Font("Segoe UI", 9),
            })
            {
                var boxes = new List<TextBox>();
                int y = 15;
                for (int i = 0; i < labels.Length; i++)
                {
                    f.Controls.Add(new Label { Text = labels[i], Left = 16, Top = y, Width = 470, Height = 23 });
                    y += 24;
                    var t = new TextBox
                    {
                        Text = values[i],
                        Left = 16,
                        Top = y,
                        Width = 470,
                        Height = multiline ? 260 : 25,
                        Multiline = multiline,
                        ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                    };
                    f.Controls.Add(t);
                    boxes.Add(t);
                    y += multiline ? 275 : 38;
                }
                var ok = new Button { Text = "Apply", Left = 310, Top = y, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 400, Top = y, DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                if (f.ShowDialog() != DialogResult.OK) return null;
                return boxes.Select(x => x.Text).ToArray();
            }
        }
    }
}