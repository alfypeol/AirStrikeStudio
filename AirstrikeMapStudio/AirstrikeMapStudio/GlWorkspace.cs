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
        private SplitContainer inspectorDetailsSplit;
        private Panel routeBanner;
        private Button routeBannerAddBtn;
        private bool userForcedRouteCollapse;
        private int lastInspectedIndex = -1;

        private Label selectionHeaderTitle;
        private Label selectionHeaderSub;
        private Button headerDupBtn;
        private Button headerDelBtn;
        private Button headerDeselBtn;
        private Button headerScriptBtn;
        private Button headerModelBtn;
        private CheckBox toolbarGizmoCheck;
        private CheckBox toolbarGridSnapCheck;

        /// <summary>
        /// Rebuilds the 3D workspace layout: removes the flat toolbar,
        /// creates the tab strip, splits the client into tabs / workspace /
        /// status, and adds the object browser to the right-hand panel.
        /// </summary>
        private void ArrangeWorkspace(FlowLayoutPanel oldBar, Panel inspector)
        {
            SuspendLayout();

            // Detach the controls from the old toolbar so they can be
            // reparented into the new tabs.
            var tools = new Control[oldBar.Controls.Count];
            oldBar.Controls.CopyTo(tools, 0);
            Controls.Remove(oldBar);

            var tabs = new TabControl { Dock = DockStyle.Top, Height = 100 };
            var editing = new TabPage("Objects & selection");
            var viewing = new TabPage("View & camera");
            tabs.TabPages.Add(editing);
            documentTabs = tabs;

            var editFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(5),
            };
            var viewFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(5),
            };
            editing.Controls.Add(editFlow);
            viewing.Controls.Add(viewFlow);

            // Copy each old-toolbar control into either editFlow or viewFlow.
            // The pivot is the "Center X / Y" label (start of camera controls).
            // Some specific controls are pinned to viewFlow regardless.
            bool camera = false;
            foreach (var c in tools)
            {
                if (c is Label && c.Text == "Center X / Y") camera = true;

                bool view = c.Text == "Mission fog"
                         || c.Text == "Route"
                         || camera;

                c.Margin = new Padding(4, 3, 4, 3);
                (view ? viewFlow : editFlow).Controls.Add(c);
            }
            oldBar.Dispose();

            var helpButton = new Button { Text = "Help (F1)", AutoSize = true };
            helpButton.Click += (s, e) => EditorHelp.Show(this);
            viewFlow.Controls.Add(helpButton);
            SetupNavigation(viewFlow);
            SetupPrecisionTools(editFlow);
            SetupArrangementTools(editFlow);

            var shared = new Button { Text = "Shared settings…", AutoSize = true, Enabled = false };
            shared.Click += (s, e) => EditSharedSettings();
            Preview.SelectionChanged += (s, e) =>
                shared.Enabled = Preview.SelectedObjects.Length > 1 && !Preview.PlacementPending;
            editFlow.Controls.Add(shared);

            // ── Add duplicate and route buttons ──
            var instantClone = new Button { Text = "Clone +2,+2", AutoSize = true, Enabled = false };
            instantClone.Click += (s, e) => TriggerDuplicateOffset();
            Preview.SelectionChanged += (s, e) =>
                instantClone.Enabled = Preview.SelectedObjects.Length > 0 && !Preview.PlacementPending;
            editFlow.Controls.Add(instantClone);

            var copyRoute = new Button { Text = "Copy Route", AutoSize = true, Enabled = false };
            copyRoute.Click += (s, e) => TriggerCopyRoute();
            Preview.SelectionChanged += (s, e) => {
                var draft = Preview.SelectedDraft();
                copyRoute.Enabled = Preview.SelectedObjects.Length == 1 && draft != null && draft.Route.Count > 0;
            };
            editFlow.Controls.Add(copyRoute);

            var pasteRoute = new Button { Text = "Paste Route", AutoSize = true, Enabled = false };
            pasteRoute.Click += (s, e) => TriggerPasteRoute();
            Preview.SelectionChanged += (s, e) =>
                pasteRoute.Enabled = Preview.SelectedObjects.Length == 1 && !Preview.PlacementPending;
            editFlow.Controls.Add(pasteRoute);

            var rotateRoute = new Button { Text = "Rotate route…", AutoSize = true, Enabled = false };
            rotateRoute.Click += (s, e) => RotateRouteDialog();
            Preview.SelectionChanged += (s, e) =>
                rotateRoute.Enabled = Preview.HasRoutableSelection() && !Preview.PlacementPending;
            editFlow.Controls.Add(rotateRoute);

            var groupRotate = new Button { Text = "Rotate group…", AutoSize = true, Enabled = false };
            groupRotate.Click += (s, e) => RotateGroupDialog();
            Preview.SelectionChanged += (s, e) =>
                groupRotate.Enabled = Preview.SelectedObjects.Length > 1 && !Preview.PlacementPending;
            editFlow.Controls.Add(groupRotate);

            var boxMode = new CheckBox { Text = "Box select", AutoSize = true };
            RegisterEditingCheck(boxMode, EditingMode.Box);
            editFlow.Controls.Add(boxMode);

            editFlow.Controls.Add(new Label
            {
                Text = "Ctrl: toggle group • Alt-click: overlapping objects • Use Delete / Ctrl+D to delete / duplicate",
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 0),
            });

            OrganizeEditingTools(tabs, editFlow);
            SetupSculptTab(tabs);
            SetupTileTab(tabs);
            tabs.TabPages.Add(viewing);

            Controls.Remove(Preview);
            Controls.Remove(inspector);

            var workspace = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Size = new Size(Math.Max(700, ClientSize.Width), Math.Max(350, ClientSize.Height - 145)),
                SplitterWidth = 7,
                FixedPanel = FixedPanel.Panel2,
            };
            workspace.Panel1MinSize = 300;
            workspace.Panel2MinSize = 270;
            workspace.SplitterDistance = Math.Max(300, workspace.Width - 430);
            workspace.Panel1.Controls.Add(Preview);
            inspector.Dock = DockStyle.Fill;
            workspace.Panel2.Controls.Add(inspector);
            SetupObjectBrowser(workspace.Panel2, inspector);

            // Move the waypoint panel out of the inspector and into the
            // bottom half of a nested horizontal splitter, so the property
            // grid and the waypoint list share the right sidebar.
            var routePanel = (Panel)routeList.Parent;
            inspector.Controls.Clear();

            // Compact 2-line selection header
            var selectionHeader = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 62,
                RowCount = 2,
                ColumnCount = 2,
                Padding = new Padding(6, 4, 6, 2),
                Margin = Padding.Empty,
                BackColor = Color.FromArgb(246, 248, 251),
            };
            selectionHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            selectionHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            selectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            selectionHeaderTitle = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "No Selection",
            };

            var row0Tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                FlowDirection = FlowDirection.LeftToRight,
            };
            headerDupBtn = new Button { Text = "Clone", AutoSize = true, MinimumSize = new Size(54, 25), Margin = new Padding(1), Font = new Font("Segoe UI", 8.25f), Enabled = false };
            headerDelBtn = new Button { Text = "Del", AutoSize = true, MinimumSize = new Size(40, 25), Margin = new Padding(1), Font = new Font("Segoe UI", 8.25f), Enabled = false };
            headerDeselBtn = new Button { Text = "✕", Width = 28, Height = 25, Margin = new Padding(1), Font = new Font("Segoe UI", 8.25f, FontStyle.Bold), Enabled = false };

            row0Tools.Controls.AddRange(new Control[] { headerDupBtn, headerDelBtn, headerDeselBtn });
            selectionHeader.Controls.Add(selectionHeaderTitle, 0, 0);
            selectionHeader.Controls.Add(row0Tools, 1, 0);

            selectionHeaderSub = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8f),
                ForeColor = SystemColors.GrayText,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Click an object in 3D view to inspect or edit.",
            };

            var row1Tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                FlowDirection = FlowDirection.LeftToRight,
            };
            headerScriptBtn = new Button { Text = "Script…", AutoSize = true, MinimumSize = new Size(58, 24), Margin = new Padding(1), Font = new Font("Segoe UI", 8f), Enabled = false };
            headerModelBtn = new Button { Text = "Model…", AutoSize = true, MinimumSize = new Size(58, 24), Margin = new Padding(1), Font = new Font("Segoe UI", 8f), Enabled = false };

            row1Tools.Controls.AddRange(new Control[] { headerScriptBtn, headerModelBtn });
            selectionHeader.Controls.Add(selectionHeaderSub, 0, 1);
            selectionHeader.Controls.Add(row1Tools, 1, 1);

            var tip = new ToolTip();
            tip.SetToolTip(headerDupBtn, "Clone / duplicate selected object(s) (Ctrl+D)");
            tip.SetToolTip(headerDelBtn, "Delete selected object(s) (Del)");
            tip.SetToolTip(headerDeselBtn, "Deselect or cancel current placement (Esc)");
            tip.SetToolTip(headerScriptBtn, "Open script and definition inspector");
            tip.SetToolTip(headerModelBtn, "Open object model in AS3DModelTool");

            headerDupBtn.Click += (s, e) => { Preview.ArmDuplicate(); Preview.Focus(); };
            headerDelBtn.Click += (s, e) => Preview.DeleteSelected();
            headerDeselBtn.Click += (s, e) =>
            {
                Preview.CancelPlacement();
                Preview.SetObjectSelection(new int[0]);
                Preview.Focus();
            };
            headerScriptBtn.Click += (s, e) => OpenScriptInspector();
            headerModelBtn.Click += (s, e) => LaunchModelTool();

            var details = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Size = new Size(360, Math.Max(340, ClientSize.Height - 270)),
                SplitterWidth = 7,
                FixedPanel = FixedPanel.Panel2,
            };
            details.Panel1MinSize = 80;
            details.Panel2MinSize = 120;
            inspectorDetailsSplit = details;

            routeBanner = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(240, 244, 250),
                Visible = false,
            };
            routeBannerAddBtn = new Button
            {
                Dock = DockStyle.Fill,
                Text = "＋ Add Waypoint Route",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 80, 160),
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.System,
            };
            routeBannerAddBtn.Click += (s, e) =>
            {
                if (Preview.SelectedIndex < 0) return;
                userForcedRouteCollapse = false;
                var draft = Preview.SelectedDraft();
                if (draft.Route == null || draft.Route.Count == 0)
                {
                    Preview.AddRouteWaypoint();
                }
                SetWaypointPanelCollapsed(false);
            };
            routeBanner.Controls.Add(routeBannerAddBtn);

            details.Panel1.Controls.Add(routeBanner);
            details.Panel1.Controls.Add(objectProperties);
            routeBanner.SendToBack();
            routePanel.Dock = DockStyle.Fill;
            details.Panel2.Controls.Add(routePanel);

            details.SizeChanged += (s, e) =>
            {
                if (!details.Panel2Collapsed)
                {
                    int totalHeight = details.Height;
                    int minP1 = details.Panel1MinSize;
                    int minP2 = details.Panel2MinSize;
                    if (totalHeight > minP1 + minP2)
                    {
                        int desired = Math.Max(minP1, totalHeight - 200);
                        int maxAllowed = totalHeight - minP2;
                        if (desired > minP1 && desired < maxAllowed)
                            details.SplitterDistance = desired;
                    }
                }
            };

            inspector.Controls.Add(selectionHeader);
            inspector.Controls.Add(details);
            selectionHeader.SendToBack();

            Preview.SelectionChanged += (s, e) =>
            {
                if (Preview.SelectedIndex != lastInspectedIndex || Preview.SelectedObjects.Length > 1)
                {
                    userForcedRouteCollapse = false;
                    lastInspectedIndex = Preview.SelectedIndex;
                }
                UpdateAdaptiveInspector();
            };
            UpdateAdaptiveInspector();

            Controls.Clear();

            BuildMainMenu();

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            tabs.Dock = DockStyle.Fill;
            tabs.Margin = workspace.Margin = Padding.Empty;

            documentPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 1,
                Margin = Padding.Empty,
                Visible = true,
            };

            documentBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Visible = true,
            };
            documentPanel.Controls.Add(documentBar, 0, 0);

            var statusStrip = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(8, 2, 8, 2),
                BackColor = Color.FromArgb(243, 244, 246),
            };
            statusStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            statusStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            status.Font = new Font("Segoe UI", 8.5f);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.Margin = Padding.Empty;

            documentStatus = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(71, 85, 105),
                Margin = Padding.Empty,
            };

            statusStrip.Controls.Add(status, 0, 0);
            statusStrip.Controls.Add(documentStatus, 1, 0);

            layout.Controls.Add(documentPanel, 0, 0);
            layout.Controls.Add(tabs, 0, 1);
            layout.Controls.Add(workspace, 0, 2);
            layout.Controls.Add(statusStrip, 0, 3);

            Controls.Add(layout);
            Controls.Add(mainMenuBar);
            AdaptToolbarHeight(layout, tabs);

            MinimumSize = new Size(800, 600);
            ResumeLayout(true);
        }

        internal void SelectTabByTitle(string title)
        {
            if (documentTabs == null) return;
            for (int i = 0; i < documentTabs.TabPages.Count; i++)
            {
                if (documentTabs.TabPages[i].Text.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    documentTabs.SelectedIndex = i;
                    break;
                }
            }
        }

        private void BuildMainMenu()
        {
            mainMenuBar = new MenuStrip
            {
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 9f),
                BackColor = Color.FromArgb(248, 249, 250),
                Padding = new Padding(6, 3, 6, 3),
            };

            // ── File ──────────────────────────────────────────
            menuFile = new ToolStripMenuItem("&File");
            var itemNew = new ToolStripMenuItem("&New Map…", null, (s, e) => NewDocument(), Keys.Control | Keys.N);
            var itemOpen = new ToolStripMenuItem("&Open Map…", null, (s, e) => OpenDocument(), Keys.Control | Keys.O);
            menuRecentMaps = new ToolStripMenuItem("Recent &Maps");
            var itemSave = new ToolStripMenuItem("&Save Map", null, (s, e) => SaveDocument(false), Keys.Control | Keys.S);
            var itemSaveAs = new ToolStripMenuItem("Save Map &As…", null, (s, e) => SaveDocument(true), Keys.Control | Keys.Shift | Keys.S);
            var itemClose = new ToolStripMenuItem("&Close 3D View", null, (s, e) => Close(), Keys.Alt | Keys.F4);

            menuFile.DropDownOpening += (s, e) =>
            {
                menuRecentMaps.DropDownItems.Clear();
                if (RecentMaps == null || RecentMaps.Count == 0)
                {
                    menuRecentMaps.DropDownItems.Add(new ToolStripMenuItem("(No recent maps)") { Enabled = false });
                }
                else
                {
                    int n = 1;
                    foreach (string path in RecentMaps.ToArray())
                    {
                        string captured = path;
                        string fileName = Path.GetFileName(captured);
                        string dirName = Path.GetDirectoryName(captured) ?? "";
                        var entry = new ToolStripMenuItem($"&{n}. {fileName}   ({dirName})")
                        {
                            ToolTipText = captured
                        };
                        entry.Click += (a, b) => OpenDocumentByPath(captured);
                        menuRecentMaps.DropDownItems.Add(entry);
                        n++;
                    }
                    menuRecentMaps.DropDownItems.Add(new ToolStripSeparator());
                    var clearItem = new ToolStripMenuItem("&Clear Recent Maps", null, (a, b) => RecentMaps.Clear());
                    menuRecentMaps.DropDownItems.Add(clearItem);
                }
            };

            menuFile.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemNew, itemOpen, menuRecentMaps, new ToolStripSeparator(), itemSave, itemSaveAs, new ToolStripSeparator(), itemClose
            });

            // ── Edit ──────────────────────────────────────────
            menuEdit = new ToolStripMenuItem("&Edit");
            var itemUndo = new ToolStripMenuItem("&Undo", null, (s, e) => Preview.HistoryMove(false), Keys.Control | Keys.Z);
            var itemRedo = new ToolStripMenuItem("&Redo", null, (s, e) => Preview.HistoryMove(true), Keys.Control | Keys.Y);
            menuDropTable = new ToolStripMenuItem("&Drop-Item Table…", null, (s, e) => EditDropTable());
            var itemShared = new ToolStripMenuItem("Shared &Settings…", null, (s, e) => EditSharedSettings());

            Preview.SelectionChanged += (s, e) =>
            {
                itemShared.Enabled = Preview.SelectedObjects.Length > 1 && !Preview.PlacementPending;
            };
            itemShared.Enabled = false;

            menuEdit.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemUndo, itemRedo, new ToolStripSeparator(), itemShared, menuDropTable
            });

            // ── Map ───────────────────────────────────────────
            menuMap = new ToolStripMenuItem("&Map");
            menuMissionSettings = new ToolStripMenuItem("&Mission Settings…", null, (s, e) => { }, Keys.F4);
            menuResizeMap = new ToolStripMenuItem("&Resize Map…");
            menuMapChecks = new ToolStripMenuItem("&Check Map / Diagnostics…", null, (s, e) => { }, Keys.F5);

            menuMap.DropDownItems.AddRange(new ToolStripItem[]
            {
                menuMissionSettings, menuResizeMap, new ToolStripSeparator(), menuMapChecks
            });

            // ── Tools ─────────────────────────────────────────
            menuTools = new ToolStripMenuItem("&Tools");
            menuTestMission = new ToolStripMenuItem("&Test Mission (F6)", null, (s, e) => { }, Keys.F6);
            menuVerifyMap = new ToolStripMenuItem("&Verify Map Integrity [Headless] (F7)", null, (s, e) => { }, Keys.F7);
            var itemScriptStudio = new ToolStripMenuItem("&Script Studio…", null, (s, e) => OpenScriptInspector()) { ShortcutKeyDisplayString = "Ctrl+Alt+S" };
            var itemModelInspector = new ToolStripMenuItem("&Model Inspector…", null, (s, e) => LaunchModelTool());

            menuTools.DropDownItems.AddRange(new ToolStripItem[]
            {
                menuTestMission, menuVerifyMap, new ToolStripSeparator(), itemScriptStudio, itemModelInspector
            });

            // ── View ──────────────────────────────────────────
            menuView = new ToolStripMenuItem("&View");
            var itemFit = new ToolStripMenuItem("&Focus Selection", null, (s, e) => Preview.FitNavigation(true), Keys.Control | Keys.F);
            var itemFitAll = new ToolStripMenuItem("Fit &Entire Map", null, (s, e) => Preview.FitNavigation(false));
            var itemTopDown = new ToolStripMenuItem("&Top-Down Camera", null, (s, e) => { Preview.Pitch = 85; Preview.Heading = -90; Preview.Invalidate(); });
            var itemResetCam = new ToolStripMenuItem("&Reset Camera Angle", null, (s, e) => { Preview.Pitch = 45; Preview.Heading = -90; Preview.Distance = 270 / Math.Sin(Math.PI / 4); Preview.Invalidate(); }) { ShortcutKeyDisplayString = "Home" };
            var itemToggleGrid = new ToolStripMenuItem("Toggle &Grid Snap", null, (s, e) =>
            {
                bool next = !Preview.GridSnap;
                Preview.GridSnap = next;
                if (toolbarGridSnapCheck != null && toolbarGridSnapCheck.Checked != next)
                    toolbarGridSnapCheck.Checked = next;
                ((ToolStripMenuItem)s).Checked = next;
                Preview.Invalidate();
            }) { Checked = Preview.GridSnap };
            var itemToggleRoute = new ToolStripMenuItem("Toggle &Route Display", null, (s, e) => { Preview.SetRouteDisplayScope((Preview.RouteDisplayScope + 1) % 3); });
            var itemToggleGizmo = new ToolStripMenuItem("Show 3D &Transform Gizmo", null, (s, e) =>
            {
                bool next = !Preview.ShowGizmo;
                Preview.ShowGizmo = next;
                if (toolbarGizmoCheck != null && toolbarGizmoCheck.Checked != next)
                    toolbarGizmoCheck.Checked = next;
                ((ToolStripMenuItem)s).Checked = next;
                Preview.Invalidate();
            }) { Checked = Preview.ShowGizmo };
            menuView.DropDownOpening += (s, e) =>
            {
                itemToggleGrid.Checked = Preview.GridSnap;
                itemToggleGizmo.Checked = Preview.ShowGizmo;
            };

            menuView.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemFit, itemFitAll, new ToolStripSeparator(), itemTopDown, itemResetCam, new ToolStripSeparator(), itemToggleGrid, itemToggleRoute, itemToggleGizmo, new ToolStripSeparator()
            });

            // Mode shortcuts in View menu
            var itemModeObj = new ToolStripMenuItem("Mode: Objects && Selection", null, (s, e) => SelectTabByTitle("Objects & selection"));
            var itemModeWp = new ToolStripMenuItem("Mode: Routes && Waypoints", null, (s, e) => SelectTabByTitle("Routes"));
            var itemModeTerr = new ToolStripMenuItem("Mode: Terrain Sculpting", null, (s, e) => SelectTabByTitle("Terrain"));
            var itemModeTile = new ToolStripMenuItem("Mode: Tiles && Roads", null, (s, e) => SelectTabByTitle("Tiles & roads"));
            var itemModeCam = new ToolStripMenuItem("Mode: View && Camera", null, (s, e) => SelectTabByTitle("View & camera"));
            var itemModeAssets = new ToolStripMenuItem("Mode: Assets && Export", null, (s, e) => SelectTabByTitle("Assets & export"));

            menuView.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemModeObj, itemModeWp, itemModeTerr, itemModeTile, itemModeCam, itemModeAssets
            });

            // ── Help ──────────────────────────────────────────
            menuHelp = new ToolStripMenuItem("&Help");
            var itemHelp = new ToolStripMenuItem("&Keyboard Shortcuts & Guide", null, (s, e) => EditorHelp.Show(this), Keys.F1);
            var itemAbout = new ToolStripMenuItem("&About AirStrike 3D Map Studio", null, (s, e) => EditorHelp.ShowAbout(this));

            menuHelp.DropDownItems.AddRange(new ToolStripItem[] { itemHelp, itemAbout });

            // ── Quick Action Right-Aligned Buttons ────────────
            menuQuickTest = new ToolStripMenuItem("▶ Test Mission (F6)")
            {
                Alignment = ToolStripItemAlignment.Right,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(220, 252, 231),
                ForeColor = Color.FromArgb(22, 101, 52),
                Margin = new Padding(4, 0, 8, 0),
            };

            menuQuickSave = new ToolStripMenuItem("💾 Save (Ctrl+S)")
            {
                Alignment = ToolStripItemAlignment.Right,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59),
                Margin = new Padding(4, 0, 4, 0),
            };
            menuQuickSave.Click += (s, e) => SaveDocument(false);

            mainMenuBar.Items.AddRange(new ToolStripItem[]
            {
                menuFile, menuEdit, menuMap, menuTools, menuView, menuHelp, menuQuickTest, menuQuickSave
            });

            MainMenuStrip = mainMenuBar;
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private TextBox objectSearch;
        private ComboBox objectCategory;
        private ListView placedObjects;
        private Label objectCount;
        private bool updatingObjects;

        internal TabControl sidebarTabs;
        internal TabPage paletteTab;
        private TextBox paletteSearch;
        private ComboBox paletteCategory;
        internal ListBox paletteList;
        private Label paletteCount;
        private PictureBox palettePreview;
        private Label paletteDetails;
        private Button palettePlaceBtn;
        private Button paletteReplaceBtn;
        private Button paletteCancelBtn;
        private Button palette3dBtn;
        private CheckBox paletteAutoArm;

        internal static string ObjectCategory(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.StartsWith("tank")) return "Tanks";
            if (n.StartsWith("helic") || n.StartsWith("plane") || n.StartsWith("boss")) return "Aircraft / Bosses";
            if (n.StartsWith("item") || n.StartsWith("bonus")) return "Pickups";
            if (n.StartsWith("ship") || n.StartsWith("cutter") || n.StartsWith("boat") || n.StartsWith("sub")
                || n.Contains("boat") || n.StartsWith("avianos") || n.StartsWith("lodka") || n.StartsWith("apl") || n.StartsWith("kater"))
                return "Ships";
            if (n.StartsWith("jeep") || n.StartsWith("truck") || n.StartsWith("rocketlauncher")
                || n.StartsWith("btr") || n.StartsWith("bike") || n.StartsWith("tyagach")
                || n.StartsWith("rl") || n.StartsWith("transport"))
                return "Vehicles / launchers";
            if (n.StartsWith("cannon") || n.StartsWith("turret") || n.StartsWith("gun") || n.StartsWith("pvo"))
                return "Turrets & cannons";
            if (n.StartsWith("dom") || n.StartsWith("build") || n.StartsWith("house")
                || n.StartsWith("tower") || n.StartsWith("factory") || n.StartsWith("hangar")
                || n.StartsWith("bridge") || n.StartsWith("zabor") || n.StartsWith("wall") || n.StartsWith("ruin"))
                return "Buildings & structures";
            if (n.StartsWith("tree") || n.StartsWith("palm") || n.StartsWith("bush")
                || n.StartsWith("flora") || n.StartsWith("stone") || n.StartsWith("rock") || n.StartsWith("kaktus"))
                return "Flora & nature";
            return "Other";
        }

        /// <summary>
        /// Builds the "Object list" tab in the right-hand panel: search box,
        /// category filter, placed-object list, and status count.
        /// </summary>
        private void SetupObjectBrowser(Control host, Panel inspector)
        {
            host.Controls.Remove(inspector);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            sidebarTabs = tabs;
            var properties = new TabPage("Inspector");
            var objects = new TabPage("Objects");
            properties.Controls.Add(inspector);
            tabs.TabPages.Add(properties);

            SetupObjectPalette(tabs);

            tabs.TabPages.Add(objects);

            SetupMinimap(tabs);
            host.Controls.Add(tabs);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            objects.Controls.Add(layout);

            var searchHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(2, 2, 2, 2) };
            var clearFilters = new Button
            {
                Text = "Clear filters",
                Dock = DockStyle.Right,
                AutoSize = true,
                MinimumSize = new Size(88, 25),
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 8.5f),
            };
            searchHeader.Controls.Add(new Label
            {
                Text = "Search name or #number",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5f),
            });
            searchHeader.Controls.Add(clearFilters);
            clearFilters.Click += (s, e) =>
            {
                objectSearch.Clear();
                objectCategory.SelectedIndex = 0;
            };
            layout.Controls.Add(searchHeader, 0, 0);

            objectSearch = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(objectSearch, 0, 1);

            objectCategory = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            objectCategory.Items.AddRange(new object[]
            {
                "All objects", "Tanks", "Aircraft / Bosses", "Vehicles / launchers",
                "Turrets & cannons", "Ships", "Buildings & structures", "Flora & nature",
                "Pickups", "Other",
            });
            objectCategory.SelectedIndex = 0;
            layout.Controls.Add(objectCategory, 0, 2);

            placedObjects = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
            };
            placedObjects.Columns.Add("#", 42);
            placedObjects.Columns.Add("Object", 170);
            placedObjects.Columns.Add("X, Y", 70);
            layout.Controls.Add(placedObjects, 0, 3);

            objectCount = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                MinimumSize = new Size(0, 42),
            };
            layout.Controls.Add(objectCount, 0, 4);

            var objectSearchTimer = new Timer { Interval = 120 };
            objectSearchTimer.Tick += (s, e) =>
            {
                objectSearchTimer.Stop();
                RefreshObjectBrowser();
            };
            Disposed += (s, e) => objectSearchTimer.Dispose();

            objectSearch.TextChanged += (s, e) =>
            {
                objectSearchTimer.Stop();
                objectSearchTimer.Start();
            };
            objectSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down)
                {
                    if (objectSearchTimer.Enabled) { objectSearchTimer.Stop(); RefreshObjectBrowser(); }
                    if (placedObjects.Items.Count > 0)
                    {
                        placedObjects.Focus();
                        if (placedObjects.SelectedItems.Count == 0)
                            placedObjects.Items[0].Selected = true;
                        e.Handled = true;
                    }
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    if (objectSearchTimer.Enabled) { objectSearchTimer.Stop(); RefreshObjectBrowser(); }
                    if (placedObjects.Items.Count > 0)
                    {
                        int id = (int)placedObjects.Items[0].Tag;
                        Preview.SelectListedObject(id, focus: true);
                        Preview.Focus();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    objectSearchTimer.Stop();
                    objectSearch.Clear();
                    RefreshObjectBrowser();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            objectCategory.SelectedIndexChanged += (s, e) => RefreshObjectBrowser();

            Action selectRows = () =>
            {
                if (updatingObjects) return;
                var ids = new System.Collections.Generic.List<int>();
                foreach (ListViewItem row in placedObjects.SelectedItems)
                    ids.Add((int)row.Tag);
                Preview.SetObjectSelection(ids.ToArray());
            };
            placedObjects.MouseUp += (s, e) => selectRows();
            placedObjects.KeyUp += (s, e) =>
            {
                if (e.KeyCode != Keys.ControlKey && e.KeyCode != Keys.ShiftKey)
                    selectRows();
            };
            placedObjects.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && placedObjects.SelectedItems.Count > 0)
                {
                    Preview.DeleteSelected();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter && placedObjects.SelectedItems.Count > 0)
                {
                    Preview.SelectListedObject((int)placedObjects.SelectedItems[0].Tag, true);
                    tabs.SelectedTab = properties;
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            placedObjects.DoubleClick += (s, e) =>
            {
                if (placedObjects.SelectedItems.Count > 0)
                {
                    Preview.SelectListedObject((int)placedObjects.SelectedItems[0].Tag, true);
                    tabs.SelectedTab = properties;
                }
            };
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedTab == objects) RefreshObjectBrowser();
            };
            Preview.SelectionChanged += (s, e) => RefreshObjectBrowser();

            placedObjects.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && placedObjects.SelectedItems.Count > 0)
                {
                    int firstIdx = (int)placedObjects.SelectedItems[0].Tag;
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("Focus in 3D view", null, (a, b) => Preview.SelectListedObject(firstIdx, true));
                    menu.Items.Add("Inspect properties", null, (a, b) => { Preview.SelectListedObject(firstIdx, false); tabs.SelectedTab = properties; });
                    menu.Items.Add("Select all of this type", null, (a, b) => Preview.SelectAllOfType(firstIdx));
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Duplicate (Ctrl+D)", null, (a, b) => { Preview.ArmDuplicate(); Preview.Focus(); });
                    menu.Items.Add("Delete (Del)", null, (a, b) => Preview.DeleteSelected());
                    menu.Show(placedObjects, e.Location);
                }
            };

            RefreshObjectBrowser();
        }

        private void SetupObjectPalette(TabControl tabs)
        {
            paletteTab = new TabPage("Palette");
            tabs.TabPages.Add(paletteTab);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // Row 0: Header
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // Row 1: Search box
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // Row 2: Category dropdown
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Row 3: ListBox + count
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205)); // Row 4: Preview & Action buttons
            paletteTab.Controls.Add(mainLayout);

            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(2, 2, 2, 2) };
            var browseModal = new Button
            {
                Text = "Dialog…",
                Dock = DockStyle.Right,
                AutoSize = true,
                MinimumSize = new Size(65, 25),
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 8.5f),
            };
            browseModal.Click += (s, e) => { Preview.ChooseScenery(this); Preview.Focus(); };
            var clearFilters = new Button
            {
                Text = "Clear",
                Dock = DockStyle.Right,
                AutoSize = true,
                MinimumSize = new Size(55, 25),
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 8.5f),
            };
            header.Controls.Add(new Label
            {
                Text = "Object Library",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            });
            header.Controls.Add(clearFilters);
            header.Controls.Add(browseModal);
            mainLayout.Controls.Add(header, 0, 0);

            paletteSearch = new TextBox { Dock = DockStyle.Fill };
            mainLayout.Controls.Add(paletteSearch, 0, 1);

            paletteCategory = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            paletteCategory.Items.AddRange(new object[]
            {
                "All categories", "Tanks", "Aircraft / Bosses", "Vehicles / launchers",
                "Turrets & cannons", "Ships", "Buildings & structures", "Flora & nature",
                "Pickups", "Other",
            });
            var sourceFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in inspectorAssets.Definitions.Values)
            {
                if (!string.IsNullOrEmpty(d.Source))
                    sourceFiles.Add(Path.GetFileName(d.Source));
            }
            foreach (string src in sourceFiles)
                paletteCategory.Items.Add("File: " + src);
            paletteCategory.SelectedIndex = 0;
            mainLayout.Controls.Add(paletteCategory, 0, 2);

            var listPanel = new Panel { Dock = DockStyle.Fill };
            paletteList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 9.5f),
            };
            paletteCount = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            listPanel.Controls.Add(paletteList);
            listPanel.Controls.Add(paletteCount);
            mainLayout.Controls.Add(listPanel, 0, 3);

            var bottomPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2) };

            var previewPanel = new Panel { Dock = DockStyle.Fill };
            palettePreview = new PictureBox
            {
                Dock = DockStyle.Left,
                Width = 95,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(24, 30, 38),
                BorderStyle = BorderStyle.FixedSingle,
            };
            paletteDetails = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 2, 2, 2),
                AutoEllipsis = true,
                Text = "Select an object to preview.",
            };
            previewPanel.Controls.Add(paletteDetails);
            previewPanel.Controls.Add(palettePreview);

            paletteAutoArm = new CheckBox
            {
                Text = "Auto-arm placement on select",
                Dock = DockStyle.Bottom,
                Height = 24,
                Checked = true,
                Margin = new Padding(2),
            };

            var btnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 64,
                WrapContents = true,
                Padding = new Padding(2),
            };
            palettePlaceBtn = new Button { Text = "Place on map", AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            paletteReplaceBtn = new Button { Text = "Replace selected", AutoSize = true, Enabled = false };
            paletteCancelBtn = new Button { Text = "Cancel placement", AutoSize = true, Enabled = false };
            palette3dBtn = new Button { Text = "3D View…", AutoSize = true, Enabled = false };
            btnFlow.Controls.AddRange(new Control[] { palettePlaceBtn, paletteReplaceBtn, paletteCancelBtn, palette3dBtn });

            bottomPanel.Controls.Add(previewPanel);
            bottomPanel.Controls.Add(paletteAutoArm);
            bottomPanel.Controls.Add(btnFlow);

            mainLayout.Controls.Add(bottomPanel, 0, 4);

            bool refreshingPalette = false;

            Action updateSelection = () =>
            {
                string selected = paletteList.SelectedItem as string;
                if (string.IsNullOrEmpty(selected))
                {
                    palettePreview.Image = null;
                    paletteDetails.Text = "Select an object to preview model & details.";
                    palettePlaceBtn.Enabled = false;
                    paletteReplaceBtn.Enabled = false;
                    palette3dBtn.Enabled = false;
                    return;
                }

                palettePlaceBtn.Enabled = true;
                paletteReplaceBtn.Enabled = Preview.SelectedObjects.Length > 0;

                var thumb = inspectorAssets.Get(selected);
                palettePreview.Image = thumb?.Image;

                if (inspectorAssets.Definitions.TryGetValue(selected, out var def))
                {
                    string srcFile = string.IsNullOrEmpty(def.Source) ? "(none)" : Path.GetFileName(def.Source);
                    string flags = "";
                    if (def.OnWater) flags += "FL_ONWATER ";
                    if (def.Hidden) flags += "FL_NODRAW ";
                    if (def.Additive) flags += "BLEND_ADD ";
                    if (flags.Length == 0) flags = "(none)";

                    paletteDetails.Text =
                        def.Name + "\r\n" +
                        "Category: " + ObjectCategory(def.Name) + "\r\n" +
                        "File: " + srcFile + " • Model: " + (def.Model.Length > 0 ? def.Model : "(none)") + "\r\n" +
                        "Flags: " + flags;

                    palette3dBtn.Enabled = def.Model.Length > 0 && !def.Hidden && !def.Additive;
                }
                else
                {
                    paletteDetails.Text = selected + "\r\nMap prototype (not found in .obj files).";
                    palette3dBtn.Enabled = false;
                }

                if (paletteAutoArm.Checked && !refreshingPalette && tabs.SelectedTab == paletteTab)
                {
                    try
                    {
                        Preview.ArmPlacement(selected, -1);
                    }
                    catch (Exception ex) { AppLog.Warn(ex); }
                }
            };

            paletteList.SelectedIndexChanged += (s, e) => updateSelection();

            paletteList.DoubleClick += (s, e) =>
            {
                string selected = paletteList.SelectedItem as string;
                if (string.IsNullOrEmpty(selected)) return;
                try
                {
                    Preview.ArmPlacement(selected, -1);
                    Preview.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Cannot place object");
                }
            };

            palettePlaceBtn.Click += (s, e) =>
            {
                string selected = paletteList.SelectedItem as string;
                if (string.IsNullOrEmpty(selected)) return;
                try
                {
                    Preview.ArmPlacement(selected, -1);
                    Preview.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Cannot place object");
                }
            };

            paletteReplaceBtn.Click += (s, e) =>
            {
                string selected = paletteList.SelectedItem as string;
                if (string.IsNullOrEmpty(selected)) return;
                try
                {
                    if (Preview.PlacementPending)
                        Preview.CancelPlacement();
                    Preview.ReplaceSelected(selected);
                    Preview.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Cannot replace object");
                }
            };

            paletteCancelBtn.Click += (s, e) =>
            {
                Preview.CancelPlacement();
                Preview.Focus();
            };

            palette3dBtn.Click += (s, e) =>
            {
                string selected = paletteList.SelectedItem as string;
                if (string.IsNullOrEmpty(selected)) return;
                try
                {
                    using (var viewer = new ModelViewer(inspectorAssets, selected))
                        viewer.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "3D Model Preview");
                }
            };

            Preview.SelectionChanged += (s, e) =>
            {
                paletteCancelBtn.Enabled = Preview.PlacementPending;
                string selected = paletteList.SelectedItem as string;
                paletteReplaceBtn.Enabled = !string.IsNullOrEmpty(selected) && Preview.SelectedObjects.Length > 0;
            };

            Action refreshPalette = () =>
            {
                refreshingPalette = true;
                try
                {
                    string query = paletteSearch.Text.Trim();
                    string cat = (string)paletteCategory.SelectedItem ?? "All categories";

                    var allNames = Preview.SceneryLibrary();
                    paletteList.BeginUpdate();
                    try
                    {
                        paletteList.Items.Clear();
                        int matched = 0;
                        foreach (string name in allNames)
                        {
                            if (cat != "All categories")
                            {
                                if (cat.StartsWith("File: "))
                                {
                                    string targetFile = cat.Substring(6);
                                    if (inspectorAssets.Definitions.TryGetValue(name, out var d))
                                    {
                                        if (!string.Equals(Path.GetFileName(d.Source), targetFile, StringComparison.OrdinalIgnoreCase))
                                            continue;
                                    }
                                    else continue;
                                }
                                else
                                {
                                    if (ObjectCategory(name) != cat)
                                        continue;
                                }
                            }

                            if (query.Length > 0 && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            paletteList.Items.Add(name);
                            matched++;
                        }

                        paletteCount.Text = matched + " / " + allNames.Length + " objects • Double-click or select to place";
                    }
                    finally
                    {
                        paletteList.EndUpdate();
                    }

                    if (paletteList.Items.Count > 0 && paletteList.SelectedIndex < 0)
                        paletteList.SelectedIndex = 0;
                    else
                        updateSelection();
                }
                finally
                {
                    refreshingPalette = false;
                }
            };

            var paletteSearchTimer = new Timer { Interval = 120 };
            paletteSearchTimer.Tick += (s, e) =>
            {
                paletteSearchTimer.Stop();
                refreshPalette();
            };
            Disposed += (s, e) => paletteSearchTimer.Dispose();

            paletteSearch.TextChanged += (s, e) =>
            {
                paletteSearchTimer.Stop();
                paletteSearchTimer.Start();
            };
            paletteSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down)
                {
                    if (paletteSearchTimer.Enabled) { paletteSearchTimer.Stop(); refreshPalette(); }
                    if (paletteList.Items.Count > 0)
                    {
                        paletteList.Focus();
                        if (paletteList.SelectedIndex < 0) paletteList.SelectedIndex = 0;
                        e.Handled = true;
                    }
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    if (paletteSearchTimer.Enabled) { paletteSearchTimer.Stop(); refreshPalette(); }
                    string selected = paletteList.SelectedItem as string;
                    if (!string.IsNullOrEmpty(selected))
                    {
                        Preview.ArmPlacement(selected, -1);
                        Preview.Focus();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    paletteSearchTimer.Stop();
                    paletteSearch.Clear();
                    refreshPalette();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            paletteCategory.SelectedIndexChanged += (s, e) => refreshPalette();
            clearFilters.Click += (s, e) =>
            {
                paletteSearch.Clear();
                paletteCategory.SelectedIndex = 0;
            };

            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedTab == paletteTab && paletteList.Items.Count == 0)
                    refreshPalette();
            };

            Shown += (s, e) => refreshPalette();
        }

        private void RefreshObjectBrowser()
        {
            if (updatingObjects) return;
            updatingObjects = true;
            placedObjects.BeginUpdate();
            try
            {
                var names = Preview.PlacedObjectNames();
                string query = objectSearch.Text.Trim();
                string category = (string)objectCategory.SelectedItem;

                var rows = new System.Collections.Generic.List<ListViewItem>();
                for (int i = 0; i < names.Length; i++)
                {
                    if (category != "All objects" && ObjectCategory(names[i]) != category)
                        continue;
                    if (query.Length > 0
                        && names[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                        && ("#" + i) != query
                        && i.ToString() != query)
                        continue;

                    rows.Add(new ListViewItem(new[]
                    {
                        i.ToString(), names[i], Preview.PlacedObjectPosition(i),
                    }) { Tag = i });
                }

                // Reuse rows during selection-only updates; rebuilding here
                // disrupts double-clicks.
                bool same = rows.Count == placedObjects.Items.Count;
                for (int i = 0; same && i < rows.Count; i++)
                {
                    same = (int)rows[i].Tag == (int)placedObjects.Items[i].Tag
                        && rows[i].SubItems[1].Text == placedObjects.Items[i].SubItems[1].Text
                        && rows[i].SubItems[2].Text == placedObjects.Items[i].SubItems[2].Text;
                }
                if (!same)
                {
                    placedObjects.Items.Clear();
                    placedObjects.Items.AddRange(rows.ToArray());
                }

                int visible = 0;
                ListViewItem reveal = null;
                var selected = Preview.SelectedObjects;
                foreach (ListViewItem row in placedObjects.Items)
                {
                    row.Selected = System.Linq.Enumerable.Contains(selected, (int)row.Tag);
                    if (row.Selected)
                    {
                        visible++;
                        if (reveal == null || (int)row.Tag == Preview.SelectedIndex)
                            reveal = row;
                    }
                }
                if (reveal != null) reveal.EnsureVisible();

                objectCount.Text =
                    rows.Count + " / " + names.Length + " objects • " +
                    selected.Length + " selected" +
                    (selected.Length > visible
                        ? " (" + (selected.Length - visible) + " hidden by filter)"
                        : "") +
                    "\r\nCtrl-click selects a group. Double-click focuses. " +
                    "Categories use names.";
            }
            finally
            {
                placedObjects.EndUpdate();
                updatingObjects = false;
            }
        }

        private bool routeBannerActive;
        internal bool IsWaypointPanelCollapsed => inspectorDetailsSplit != null && inspectorDetailsSplit.Panel2Collapsed;
        internal bool IsRouteBannerVisible => routeBanner != null && (routeBanner.Visible || routeBannerActive);

        private void SetWaypointPanelCollapsed(bool collapsed)
        {
            if (inspectorDetailsSplit == null || inspectorDetailsSplit.IsDisposed) return;

            if (collapsed)
            {
                inspectorDetailsSplit.Panel2Collapsed = true;
                bool hasSelection = Preview.SelectedIndex >= 0 && !Preview.PlacementPending;
                routeBannerActive = hasSelection;
                routeBanner.Visible = hasSelection;
                if (hasSelection)
                {
                    var draft = Preview.SelectedDraft();
                    int routeCount = (draft != null && draft.Route != null)
                        ? draft.Route.Count
                        : 0;
                    routeBannerAddBtn.Text = routeCount > 0
                        ? $"▲ Show Waypoints ({routeCount} pts)"
                        : "＋ Add Waypoint Route";
                }
            }
            else
            {
                routeBannerActive = false;
                routeBanner.Visible = false;
                inspectorDetailsSplit.Panel2Collapsed = false;

                int totalHeight = inspectorDetailsSplit.Height;
                int minP1 = inspectorDetailsSplit.Panel1MinSize;
                int minP2 = inspectorDetailsSplit.Panel2MinSize;
                if (totalHeight > minP1 + minP2)
                {
                    int desired = Math.Max(minP1, totalHeight - 200);
                    int maxAllowed = totalHeight - minP2;
                    if (desired > minP1 && desired < maxAllowed)
                        inspectorDetailsSplit.SplitterDistance = desired;
                }
            }
        }

        private void UpdateAdaptiveInspector()
        {
            UpdateSelectionHeader();

            if (inspectorDetailsSplit == null || inspectorDetailsSplit.IsDisposed) return;

            if (userForcedRouteCollapse)
            {
                SetWaypointPanelCollapsed(true);
                return;
            }

            bool hasRoute = false;
            var currentMap = Preview.CurrentMap;
            if (Preview.SelectedObjects.Length > 1)
            {
                if (currentMap != null)
                {
                    foreach (int idx in Preview.SelectedObjects)
                    {
                        if (idx >= 0 && idx < currentMap.Objects.Count && currentMap.Objects[idx].Route != null && currentMap.Objects[idx].Route.Count > 0)
                        {
                            hasRoute = true;
                            break;
                        }
                    }
                }
            }
            else if (Preview.SelectedIndex >= 0)
            {
                var draft = Preview.SelectedDraft();
                hasRoute = (draft != null && draft.Route != null && draft.Route.Count > 0);
            }

            if (hasRoute || Preview.SelectedObjects.Length > 1)
            {
                SetWaypointPanelCollapsed(false);
            }
            else
            {
                SetWaypointPanelCollapsed(true);
            }
        }

        private void UpdateSelectionHeader()
        {
            if (selectionHeaderTitle == null || selectionHeaderTitle.IsDisposed) return;

            if (Preview.PlacementPending)
            {
                selectionHeaderTitle.Text = (Preview.PendingClone >= 0 ? "Duplicate: " : "Place: ") + Preview.PendingName;
                selectionHeaderSub.Text = $"Facing {Preview.PendingFacing * 30}° • Click to place, Esc to cancel";
                headerDupBtn.Enabled = false;
                headerDelBtn.Enabled = false;
                headerDeselBtn.Enabled = true;
                headerScriptBtn.Enabled = false;
                headerModelBtn.Enabled = false;
                return;
            }

            int selCount = Preview.SelectedObjects.Length;
            if (selCount > 1)
            {
                selectionHeaderTitle.Text = $"{selCount} Objects Selected";
                selectionHeaderSub.Text = "Drag to move group • Shift+Drag to rotate";
                headerDupBtn.Enabled = true;
                headerDelBtn.Enabled = true;
                headerDeselBtn.Enabled = true;
                headerScriptBtn.Enabled = false;
                headerModelBtn.Enabled = false;
                return;
            }

            if (selCount == 1 && Preview.SelectedIndex >= 0)
            {
                int idx = Preview.SelectedIndex;
                var draft = Preview.SelectedDraft();
                var currentMap = Preview.CurrentMap;
                string typeName = (draft != null && currentMap != null && draft.Type >= 0 && draft.Type < currentMap.Types.Count)
                    ? currentMap.Types[draft.Type]
                    : "Object";
                selectionHeaderTitle.Text = $"#{idx} {typeName}";
                int wpCount = (draft != null && draft.Route != null) ? draft.Route.Count : 0;
                int x = draft != null ? draft.X : 0;
                int y = draft != null ? draft.Y : 0;
                int facing = draft != null ? (draft.Setting6 * 30) : 0;
                selectionHeaderSub.Text = $"X:{x} Y:{y} • {facing}° • {wpCount} WP";

                headerDupBtn.Enabled = true;
                headerDelBtn.Enabled = true;
                headerDeselBtn.Enabled = true;
                headerScriptBtn.Enabled = true;
                headerModelBtn.Enabled = true;
                return;
            }

            // No selection
            selectionHeaderTitle.Text = "No Selection";
            selectionHeaderSub.Text = "Click an object in 3D view to inspect or edit.";
            headerDupBtn.Enabled = false;
            headerDelBtn.Enabled = false;
            headerDeselBtn.Enabled = false;
            headerScriptBtn.Enabled = false;
            headerModelBtn.Enabled = false;
        }
    }
}