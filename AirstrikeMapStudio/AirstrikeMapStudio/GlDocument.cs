using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainWindow
    {
        // ═════════════════════════════════════════════════════════════════
        // Document chrome (title, toolbar, shortcuts)
        // ═════════════════════════════════════════════════════════════════
        // The 3D window is a modal document on top of the 2D Studio. When it
        // closes, the caller reads NextMapPath / NextMapCreated / AssetFolderChanged
        // / MissionSettingsChanged to decide what the 2D editor should do.
        // ═════════════════════════════════════════════════════════════════

        private Func<bool> newDocument, documentDirty;
        public bool NextMapCreated { get; private set; }
        public bool MapResized { get; private set; }
        private Func<bool> resizeMapAction;

        public string NextMapPath { get; private set; }

        internal TabControl documentTabs;
        private FlowLayoutPanel documentBar;
        private TableLayoutPanel documentPanel;
        private Button closeButton;
        private Func<bool, bool> saveDocument;
        private Func<string> documentName, openDocument;
        private Label documentStatus;
        private string documentNotice = "";

        private MenuStrip mainMenuBar;
        private ToolStripMenuItem menuFile;
        private ToolStripMenuItem menuEdit;
        private ToolStripMenuItem menuMap;
        private ToolStripMenuItem menuTools;
        private ToolStripMenuItem menuView;
        private ToolStripMenuItem menuHelp;
        private ToolStripMenuItem menuMissionSettings;
        private ToolStripMenuItem menuResizeMap;
        private ToolStripMenuItem menuDropTable;
        private ToolStripMenuItem menuMapChecks;
        private ToolStripMenuItem menuTestMission;
        private ToolStripMenuItem menuQuickSave;
        private ToolStripMenuItem menuQuickTest;

        /// <summary>
        /// Wires a "Resize map…" button into the document toolbar.
        /// </summary>
        public void ConnectResizeMap(Func<bool> resize)
        {
            resizeMapAction = resize;
            Action trigger = () =>
            {
                if (resizeMapAction == null) return;
                try
                {
                    Validate();
                    if (!resizeMapAction()) return;
                    MapResized = true;
                    NextMapCreated = true;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not resize map");
                }
            };
            if (menuResizeMap != null) menuResizeMap.Click += (s, e) => trigger();

            var button = new Button { Text = "Resize map…", AutoSize = true };
            button.Click += (s, e) => trigger();
            AddDocumentTool(button);
        }

        /// <summary>
        /// Wires a "New map…" button into the document toolbar. The button
        /// is inserted before the existing "Return to 2D" button.
        /// </summary>
        public void ConnectNewDocument(Func<bool> create)
        {
            newDocument = create;
            var button = new Button { Text = "New map…", AutoSize = true };
            button.Click += (s, e) => NewDocument();
            documentBar.Controls.Add(button);
            documentBar.Controls.SetChildIndex(button, 0);
        }

        private void NewDocument()
        {
            if (newDocument == null) return;

            try
            {
                Validate();
                if (!newDocument())
                {
                    RefreshDocumentTitle();
                    return;
                }
                NextMapCreated = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not create map");
            }
        }

        /// <summary>
        /// Inserts a new button into the document toolbar, before the
        /// "Close 3D view" button (if it exists), and wires it into the main menu bar.
        /// </summary>
        public void AddDocumentTool(Button button)
        {
            if (documentBar == null || button == null) return;
            documentBar.Controls.Add(button);
            if (closeButton != null)
                documentBar.Controls.SetChildIndex(button, documentBar.Controls.GetChildIndex(closeButton));

            WireToolIntoMenu(button);
        }

        private void WireToolIntoMenu(Button button)
        {
            if (button == null || mainMenuBar == null) return;

            string text = button.Text.Trim();
            if (text != "Open map…" && text != "Save map" && text != "Save map as…" && text != "Close 3D view" && text != "New map…"
             && !text.StartsWith("Mission settings", StringComparison.OrdinalIgnoreCase)
             && !text.StartsWith("Resize map", StringComparison.OrdinalIgnoreCase)
             && !text.StartsWith("Drop-item table", StringComparison.OrdinalIgnoreCase)
             && !text.StartsWith("Drop item", StringComparison.OrdinalIgnoreCase)
             && !text.StartsWith("Check map", StringComparison.OrdinalIgnoreCase)
             && !text.StartsWith("Test mission", StringComparison.OrdinalIgnoreCase))
            {
                Action trigger = () =>
                {
                    try
                    {
                        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?.Invoke(button, new object[] { EventArgs.Empty });
                    }
                    catch { }
                };
                var item = new ToolStripMenuItem(button.Text);
                item.Click += (s, e) => trigger();
                button.TextChanged += (s, e) => item.Text = button.Text;
                button.EnabledChanged += (s, e) => item.Enabled = button.Enabled;
                menuTools.DropDownItems.Add(item);
            }
        }

        /// <summary>
        /// Wires a "Drop-item table…" button into the document toolbar.
        /// </summary>
        public void ConnectDropTable()
        {
            if (menuDropTable != null) menuDropTable.Click += (s, e) => EditDropTable();
            var button = new Button { Text = "Drop-item table…", AutoSize = true };
            button.Click += (s, e) => EditDropTable();
            AddDocumentTool(button);
        }

        public void ConnectDocumentState(Func<bool> dirty)
        {
            documentDirty = dirty;
            RefreshDocumentTitle();
        }

        /// <summary>
        /// Wires the four standard document toolbar buttons.
        /// </summary>
        public void ConnectDocument(Func<bool, bool> save, Func<string> name, Func<string> open)
        {
            saveDocument = save;
            documentName = name;
            openDocument = open;
            documentPanel.Visible = true;

            foreach (string label in new[] { "Open map…", "Save map", "Save map as…", "Close 3D view" })
            {
                var button = new Button { Text = label, AutoSize = true };
                string action = label; // local capture
                button.Click += (s, e) =>
                {
                    if (action == "Open map…") OpenDocument();
                    else if (action == "Close 3D view") Close();
                    else SaveDocument(action == "Save map as…");
                };
                documentBar.Controls.Add(button);

                if (label == "Close 3D view") closeButton = button;
            }

            Preview.SelectionChanged += (s, e) =>
            {
                documentNotice = "";
                RefreshDocumentTitle();
            };

            RefreshDocumentTitle();
        }

        private void RefreshDocumentTitle()
        {
            if (documentName == null) return;

            string name = documentName();
            bool dirty = documentDirty != null ? documentDirty() : name.StartsWith("● ");

            Text = name + " — 3D map editor";
            if (documentStatus != null)
            {
                documentStatus.Text = (dirty ? "● Unsaved changes — " : "Saved — ")
                                    + name.TrimStart('●', ' ')
                                    + (documentNotice.Length > 0 ? "  |  " + documentNotice : "");
                documentStatus.ForeColor = dirty ? Color.DarkRed : Color.FromArgb(71, 85, 105);
            }
            if (menuQuickSave != null)
            {
                menuQuickSave.Text = dirty ? "💾 Save * (Ctrl+S)" : "💾 Save (Ctrl+S)";
            }
        }

        public bool SaveDocument(bool asNew)
        {
            if (saveDocument == null) return false;

            try
            {
                Validate();
                bool result = saveDocument(asNew);
                documentNotice = result ? "Existing files receive a backup." : "Save cancelled.";
                RefreshDocumentTitle();
                return result;
            }
            catch (Exception ex)
            {
                documentNotice = "Save failed; changes remain in the editor.";
                RefreshDocumentTitle();
                MessageBox.Show(this, ex.Message, "Could not save map");
                return false;
            }
        }

        private void OpenDocument()
        {
            if (openDocument == null) return;

            try
            {
                Validate();
                string path = openDocument();
                if (path == null)
                {
                    RefreshDocumentTitle();
                    return;
                }
                NextMapPath = path;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not open map");
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Camera bookmarks (only if a text box isn't focused)
            if (!ValueEditorHasFocus())
            {
                // Save Bookmark (Ctrl + 1 to 5)
                if (keyData >= (Keys.Control | Keys.D1) && keyData <= (Keys.Control | Keys.D5))
                {
                    int slot = keyData - (Keys.Control | Keys.D1);
                    Preview.StoreBookmark(slot);
                    return true;
                }

                // Jump to Bookmark (1 to 5)
                if (keyData >= Keys.D1 && keyData <= Keys.D5)
                {
                    int slot = keyData - Keys.D1;
                    if (Preview.JumpToBookmark(slot)) return true;
                }
            }

            // Delete: remove the current selection. Guarded by Preview.Focused
            // (not just ValueEditorHasFocus) because Delete is destructive and
            // should not fire when a toolbar button or the object list has
            // focus. The viewport grabs focus on any mouse-down, so clicking
            // then pressing Delete is the natural gesture.
            if (keyData == Keys.Delete && Preview.Focused && !ValueEditorHasFocus())
            {
                Preview.DeleteSelected();
                return true;
            }

            // Shortcuts that should be reserved for a focused text / numeric / ...
            bool selectionShortcut =
                keyData == (Keys.Control | Keys.Z)
             || keyData == (Keys.Control | Keys.Y)
             || keyData == (Keys.Control | Keys.Shift | Keys.Z)
             || keyData == (Keys.Control | Keys.F)
             || keyData == (Keys.Control | Keys.T)
             || keyData == (Keys.Control | Keys.D);

            if (selectionShortcut && ValueEditorHasFocus())
                return base.ProcessCmdKey(ref msg, keyData);

            if (keyData == Keys.Escape && HandleEditingEscape())
                return true;

            if (keyData == Keys.F1)
            {
                EditorHelp.Show(this);
                return true;
            }

            if (keyData == Keys.F5 && runMapChecks != null)
            {
                Validate();
                runMapChecks();
                return true;
            }

            if (keyData == Keys.F6 && runTestMission != null)
            {
                Validate();
                runTestMission();
                return true;
            }

            if (keyData == (Keys.Control | Keys.T) || keyData == (Keys.Control | Keys.D))
            {
                NumericTranslateDialog(keyData == (Keys.Control | Keys.D));
                return true;
            }

            if (keyData == (Keys.Control | Keys.F))
            {
                Preview.FitNavigation(true);
                return true;
            }

            if (keyData == (Keys.Control | Keys.Z)
             || keyData == (Keys.Control | Keys.Y)
             || keyData == (Keys.Control | Keys.Shift | Keys.Z))
            {
                Preview.HistoryMove(keyData != (Keys.Control | Keys.Z));
                return true;
            }

            if (keyData == (Keys.Control | Keys.N) && newDocument != null)
            {
                NewDocument();
                return true;
            }

            if (saveDocument != null)
            {
                if (keyData == (Keys.Control | Keys.S))
                {
                    SaveDocument(false);
                    return true;
                }
                if (keyData == (Keys.Control | Keys.Shift | Keys.S))
                {
                    SaveDocument(true);
                    return true;
                }
                if (keyData == (Keys.Control | Keys.O))
                {
                    OpenDocument();
                    return true;
                }
            }
            // --- ADD THIS BLOCK ---
            if (!ValueEditorHasFocus())
            {
                if (keyData == (Keys.Control | Keys.Shift | Keys.D))
                {
                    TriggerDuplicateOffset();
                    return true;
                }

                if (keyData == (Keys.Control | Keys.Shift | Keys.C))
                {
                    TriggerCopyRoute();
                    return true;
                }

                if (keyData == (Keys.Control | Keys.Shift | Keys.V))
                {
                    TriggerPasteRoute();
                    return true;
                }

                if (keyData == (Keys.Control | Keys.Shift | Keys.R))
                {
                    RotateRouteDialog();
                    return true;
                }

                if (keyData == Keys.F4 || keyData == (Keys.Control | Keys.Shift | Keys.E))
                {
                    OpenScriptStudio();
                    return true;
                }
            }
            // ----------------------

            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>
    /// CSV export and import for a placed object's waypoints.
    /// </summary>
    public static class WaypointCsv
    {
        public static string Format(Placement placement)
        {
            var text = new System.Text.StringBuilder();
            if (placement != null)
                text.Append("# AirStrike Waypoint Route\r\n# SpawnX=").Append(placement.X).Append(",SpawnY=").Append(placement.Y).Append("\r\n");
            text.Append("Index,X,Y,Mode,HandleAX,HandleAY,HandleBX,HandleBY,FinalValue\r\n");
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            int index = 0;
            if (placement != null)
            {
                foreach (var point in placement.Route)
                {
                    text.Append(string.Join(",", new[]
                    {
                        (++index).ToString(culture),
                        point.X.ToString(culture),
                        point.Y.ToString(culture),
                        point.Mode.ToString(culture),
                        point.AX.ToString("R", culture),
                        point.AY.ToString("R", culture),
                        point.BX.ToString("R", culture),
                        point.BY.ToString("R", culture),
                        point.Value.ToString("R", culture),
                    })).Append("\r\n");
                }
            }

            return text.ToString();
        }

        public static bool TryParse(
            string csvText,
            out List<Waypoint> waypoints,
            out int? spawnX,
            out int? spawnY,
            out string errorMessage)
        {
            waypoints = new List<Waypoint>();
            spawnX = null;
            spawnY = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(csvText))
            {
                errorMessage = "CSV content is empty.";
                return false;
            }

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var lines = csvText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            int lineNum = 0;
            foreach (var rawLine in lines)
            {
                lineNum++;
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                // Parse metadata comments
                if (line.StartsWith("#") || line.StartsWith("//"))
                {
                    var comment = line.TrimStart('#', '/', ' ');
                    var parts = comment.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var kv = p.Split('=');
                        if (kv.Length == 2)
                        {
                            string k = kv[0].Trim().ToLowerInvariant();
                            if (k == "spawnx" && int.TryParse(kv[1].Trim(), out int sx)) spawnX = sx;
                            if (k == "spawny" && int.TryParse(kv[1].Trim(), out int sy)) spawnY = sy;
                        }
                    }
                    continue;
                }

                // Skip header line
                if (line.StartsWith("Index", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("X", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tokens = line.Split(',').Select(t => t.Trim().Trim('"')).ToArray();
                if (tokens.Length < 2) continue;

                try
                {
                    int offset = 0;
                    if (tokens.Length >= 9 && int.TryParse(tokens[0], out _))
                    {
                        offset = 1; // skip 1-based Index column
                    }

                    int x = int.Parse(tokens[offset + 0], culture);
                    int y = int.Parse(tokens[offset + 1], culture);
                    int mode = 0;
                    float ax = x, ay = y, bx = x, by = y, val = 0;

                    if (tokens.Length >= offset + 3)
                    {
                        if (int.TryParse(tokens[offset + 2], out int m))
                            mode = m;
                    }

                    if (tokens.Length >= offset + 7)
                    {
                        ax = float.Parse(tokens[offset + 3], culture);
                        ay = float.Parse(tokens[offset + 4], culture);
                        bx = float.Parse(tokens[offset + 5], culture);
                        by = float.Parse(tokens[offset + 6], culture);
                    }
                    else if (tokens.Length >= offset + 6)
                    {
                        ax = float.Parse(tokens[offset + 2], culture);
                        ay = float.Parse(tokens[offset + 3], culture);
                        bx = float.Parse(tokens[offset + 4], culture);
                        by = float.Parse(tokens[offset + 5], culture);
                    }

                    if (tokens.Length >= offset + 8)
                    {
                        val = float.Parse(tokens[offset + 7], culture);
                    }

                    waypoints.Add(new Waypoint
                    {
                        X = x,
                        Y = y,
                        Mode = mode,
                        AX = ax,
                        AY = ay,
                        BX = bx,
                        BY = by,
                        Value = val,
                    });
                }
                catch (Exception ex)
                {
                    errorMessage = "Line " + lineNum + ": " + ex.Message;
                    return false;
                }
            }

            if (waypoints.Count == 0)
            {
                errorMessage = "No valid waypoint rows found in CSV.";
                return false;
            }

            return true;
        }
    }

    public sealed partial class GlTerrainWindow
    {
        public bool AssetFolderChanged { get; private set; }

        /// <summary>
        /// Builds the "Assets &amp; export" tab: choose data folder, asset
        /// diagnostics, model inspector, waypoint CSV export.
        /// </summary>
        public void ConnectAssetTools(string root, Func<string, bool> changeFolder, Func<string> diagnostics)
        {
            var tab = new TabPage("Assets & export");
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(5) };
            tab.Controls.Add(flow);
            documentTabs.TabPages.Add(tab);

            var folder = new Button { Text = "Choose game data folder…", AutoSize = true };
            var report = new Button { Text = "Asset diagnostics…", AutoSize = true };
            var export = new Button { Text = "Export waypoints CSV…", AutoSize = true, Enabled = false };
            var import = new Button { Text = "Import waypoints CSV…", AutoSize = true, Enabled = false };

            flow.Controls.AddRange(new Control[] { folder, report, export, import });
            flow.Controls.Add(new Label { Text = "Data: " + root, AutoSize = true });

            Action update = () =>
            {
                var draft = Preview.SelectedDraft();
                bool hasSingle = Preview.SelectedObjects.Length == 1 && !Preview.PlacementPending;
                bool hasAny = Preview.SelectedObjects.Length >= 1 && !Preview.PlacementPending;
                export.Enabled = hasSingle && draft != null && draft.Route.Count > 0;
                import.Enabled = hasAny;
            };
            Preview.SelectionChanged += (s, e) => update();
            update();

            export.Click += (s, e) => TriggerExportRouteCsv();
            import.Click += (s, e) => TriggerImportRouteCsv();

            folder.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog
                {
                    Description = "Choose the game's data folder (objects, models, maps, textures). " +
                                  "Unsaved map edits are retained.",
                    SelectedPath = root,
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    try
                    {
                        Validate();
                        if (!changeFolder(dialog.SelectedPath)) return;
                        AssetFolderChanged = true;
                        NextMapCreated = true;
                        Close();
                    }
                    catch (Exception error)
                    {
                        MessageBox.Show(this, error.Message, "Could not change data folder");
                    }
                }
            };

            report.Click += (s, e) =>
            {
                try
                {
                    string text = diagnostics()
                        + "\r\n\r\n3D preview: " + Preview.SceneryCount + " objects; "
                        + Preview.MarkerCount + " fallback markers.\r\n"
                        + string.Join("\r\n", Preview.SceneryReport.ToArray())
                        + "\r\n\r\nReports loaded assets and preview fallbacks; this is not a " +
                          "gameplay compatibility check. Use Check map for map-wide validation.";

                    using (var form = new Form
                    {
                        Text = "Asset diagnostics",
                        Width = 780,
                        Height = 530,
                        MinimumSize = new Size(500, 300),
                        StartPosition = FormStartPosition.CenterParent,
                    })
                    {
                        form.Controls.Add(new TextBox
                        {
                            Dock = DockStyle.Fill,
                            Multiline = true,
                            ReadOnly = true,
                            ScrollBars = ScrollBars.Both,
                            WordWrap = false,
                            Text = text,
                        });
                        var close = new Button { Text = "Close", Dock = DockStyle.Bottom, DialogResult = DialogResult.Cancel };
                        form.Controls.Add(close);
                        form.CancelButton = close;
                        form.ShowDialog(this);
                    }
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "Asset diagnostics");
                }
            };
        }
    }

    public partial class Studio
    {
        /// <summary>
        /// Swaps the loaded asset folder. On failure, restores the previous
        /// folder and re-throws.
        /// </summary>
        private bool ChangeWorkspaceAssets(string path)
        {
            string previous = assets.Root;

            if (string.Equals(
                    System.IO.Path.GetFullPath(path).TrimEnd('\\'),
                    System.IO.Path.GetFullPath(previous).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // Validate definitions and construct a disposable preview before
            // touching current assets.
            using (var candidate = new Assets()) candidate.Open(path);
            using (var candidate = new GlTerrainWindow(map, file, path, environmentOverride)) { }

            try
            {
                LoadAssets(path);
                RefreshAll(true);
            }
            catch
            {
                LoadAssets(previous);
                RefreshAll(true);
                throw;
            }

            return true;
        }

        private string WorkspaceAssetReport()
        {
            return "Data folder: " + assets.Root + "\r\n"
                 + assets.Definitions.Count + " object definitions loaded.\r\n\r\n"
                 + (assets.Warnings.Count == 0
                        ? "No asset errors recorded by the 2D asset loader."
                        : string.Join("\r\n", assets.Warnings.ToArray()));
        }
    }
}