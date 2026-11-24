using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainWindow
    {
        /// <summary>
        /// Rearranges editing and route tools into Objects &amp; selection and Routes tabs.
        /// </summary>
        private void OrganizeEditingTools(TabControl tabs, FlowLayoutPanel source)
        {
            tabs.TabPages[0].Text = "Objects & selection";

            var routes = new TabPage("Routes");

            var routeTools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(5),
            };

            // Route-display scope selector lives in the Routes tab, above the
            // copied controls.
            var scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            scope.Items.AddRange(new object[] { "Active object", "Selected group", "All objects" });
            scope.SelectedIndex = Preview.RouteDisplayScope;
            scope.SelectedIndexChanged += (sender, args) =>
            {
                Preview.SetRouteDisplayScope(scope.SelectedIndex);
            };
            Preview.RouteScopeChanged += (sender, args) =>
            {
                if (scope.SelectedIndex != Preview.RouteDisplayScope)
                    scope.SelectedIndex = Preview.RouteDisplayScope;
            };
            routeTools.Controls.Add(new Label { Text = "Show routes for", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            routeTools.Controls.Add(scope);

            var appendClick = new CheckBox
            {
                Text = "Click ground: +WP",
                Checked = Preview.AppendRouteOnClick,
                AutoSize = true,
                Margin = new Padding(4, 5, 4, 3),
            };
            appendClick.CheckedChanged += (s, e) => Preview.AppendRouteOnClick = appendClick.Checked;
            routeTools.Controls.Add(appendClick);

            var indepHandles = new CheckBox
            {
                Text = "Independent handles (unlocked)",
                Checked = Preview.IndependentHandles,
                AutoSize = true,
                Margin = new Padding(4, 5, 4, 3),
            };
            indepHandles.CheckedChanged += (s, e) => Preview.IndependentHandles = indepHandles.Checked;

            Button rotateBtn = null;
            Button copyBtn = null;
            Button pasteBtn = null;

            ComboBox recentCombo = source.Controls.OfType<ComboBox>().FirstOrDefault();
            Button btnAdd = null;
            Button btnReplace = null;
            Button btnDuplicate = null;
            Button btnDelete = null;
            Button btnDeselect = null;
            Button btnRotateLeft = null;
            Button btnRotateRight = null;
            Button btnMoveTo = null;
            Button btnDupTo = null;
            Button btnAlign = null;
            Button btnShared = null;
            Button btnClone = null;
            Button btnGroupRotate = null;
            CheckBox chkGizmo = null;
            CheckBox chkGridSnap = null;
            CheckBox chkMoveMode = null;
            CheckBox chkBoxMode = null;

            // Move route-specific controls from the flat source into routeTools,
            // and gather object-editing controls for consolidation.
            foreach (Control control in source.Controls.Cast<Control>().ToArray())
            {
                string text = control.Text;
                if (text == "Edit route"
                 || text == "Move waypoints"
                 || text == "Align final approach")
                {
                    routeTools.Controls.Add(control);
                }
                else if (text == "Copy Route")
                {
                    copyBtn = control as Button;
                }
                else if (text == "Paste Route")
                {
                    pasteBtn = control as Button;
                }
                else if (text == "Rotate route…")
                {
                    rotateBtn = control as Button;
                }
                else if (text == "Add object…") btnAdd = control as Button;
                else if (text == "Replace object…") btnReplace = control as Button;
                else if (text == "Duplicate…") btnDuplicate = control as Button;
                else if (text == "Delete") btnDelete = control as Button;
                else if (text == "Deselect") btnDeselect = control as Button;
                else if (text == "Rotate −30°" || text.StartsWith("Rotate -30")) btnRotateLeft = control as Button;
                else if (text == "Rotate +30°" || text.StartsWith("Rotate +30")) btnRotateRight = control as Button;
                else if (text == "Move to…") btnMoveTo = control as Button;
                else if (text == "Duplicate to…") btnDupTo = control as Button;
                else if (text == "Align / space…" || text.StartsWith("Align / space")) btnAlign = control as Button;
                else if (text == "Shared settings…") btnShared = control as Button;
                else if (text == "Clone +2,+2") btnClone = control as Button;
                else if (text == "Rotate group…") btnGroupRotate = control as Button;
                else if (text == "3D Gizmo") chkGizmo = control as CheckBox;
                else if (text == "Snap to Grid") chkGridSnap = control as CheckBox;
                else if (text == "Move: drag object") chkMoveMode = control as CheckBox;
                else if (text == "Box select") chkBoxMode = control as CheckBox;
            }

            // ── Reorganize Objects & selection toolbar into clean consolidated groups ──
            var dupBtn = new Button { Text = "Duplicate ▾", AutoSize = true, Margin = new Padding(4, 3, 4, 3), Enabled = false };
            var dupMenu = new ContextMenuStrip();
            var menuDupInteractive = new ToolStripMenuItem("Interactive Duplicate (Ctrl+D)", null, (s, e) => btnDuplicate?.PerformClick());
            var menuCloneOffset = new ToolStripMenuItem("Clone +2, +2 (Instant)", null, (s, e) => btnClone?.PerformClick());
            var menuDupTo = new ToolStripMenuItem("Duplicate to (Coordinates)…", null, (s, e) => btnDupTo?.PerformClick());
            dupMenu.Items.AddRange(new ToolStripItem[] { menuDupInteractive, menuCloneOffset, menuDupTo });
            dupBtn.Click += (s, e) => dupMenu.Show(dupBtn, 0, dupBtn.Height);
            dupBtn.Disposed += (s, e) => dupMenu.Dispose();

            var transformObjBtn = new Button { Text = "Transform ▾", AutoSize = true, Margin = new Padding(4, 3, 4, 3), Enabled = false };
            var transformObjMenu = new ContextMenuStrip();
            var menuRotLeft = new ToolStripMenuItem("Rotate Left 30° ([ or Shift+R)", null, (s, e) => btnRotateLeft?.PerformClick());
            var menuRotRight = new ToolStripMenuItem("Rotate Right 30° (] or R)", null, (s, e) => btnRotateRight?.PerformClick());
            var menuRotGroup = new ToolStripMenuItem("Rotate Group…", null, (s, e) => btnGroupRotate?.PerformClick());
            transformObjMenu.Items.AddRange(new ToolStripItem[] { menuRotLeft, menuRotRight, new ToolStripSeparator(), menuRotGroup });
            transformObjBtn.Click += (s, e) => transformObjMenu.Show(transformObjBtn, 0, transformObjBtn.Height);
            transformObjBtn.Disposed += (s, e) => transformObjMenu.Dispose();

            var groupBtn = new Button { Text = "Group ▾", AutoSize = true, Margin = new Padding(4, 3, 4, 3), Enabled = false };
            var groupMenu = new ContextMenuStrip();
            var menuAlign = new ToolStripMenuItem("Align / Space Selection…", null, (s, e) => btnAlign?.PerformClick());
            var menuMoveTo = new ToolStripMenuItem("Move Selection to (Coordinates)…", null, (s, e) => btnMoveTo?.PerformClick());
            var menuShared = new ToolStripMenuItem("Shared Settings (Batch Edit)…", null, (s, e) => btnShared?.PerformClick());
            groupMenu.Items.AddRange(new ToolStripItem[] { menuAlign, menuMoveTo, new ToolStripSeparator(), menuShared });
            groupBtn.Click += (s, e) => groupMenu.Show(groupBtn, 0, groupBtn.Height);
            groupBtn.Disposed += (s, e) => groupMenu.Dispose();

            Action updateObjDropdowns = () =>
            {
                bool hasSingle = Preview.SelectedIndex >= 0 && Preview.SelectedObjects.Length == 1;
                bool hasAny = Preview.SelectedIndex >= 0 || Preview.SelectedObjects.Length > 0;
                bool notPending = !Preview.PlacementPending;

                dupBtn.Enabled = hasAny && notPending;
                menuDupInteractive.Enabled = hasAny && notPending;
                menuCloneOffset.Enabled = hasAny && notPending;
                menuDupTo.Enabled = hasAny && notPending;

                transformObjBtn.Enabled = (Preview.PlacementPending || hasAny);
                menuRotLeft.Enabled = menuRotRight.Enabled = (Preview.PlacementPending || hasSingle);
                menuRotGroup.Enabled = Preview.SelectedObjects.Length > 1 && notPending;

                groupBtn.Enabled = hasAny && notPending;
                menuAlign.Enabled = Preview.SelectedObjects.Length >= 2 && notPending;
                menuMoveTo.Enabled = hasAny && notPending;
                menuShared.Enabled = Preview.SelectedObjects.Length > 1 && notPending;
            };
            Preview.SelectionChanged += (s, e) => updateObjDropdowns();
            updateObjDropdowns();

            Func<Label> makeSep = () => new Label
            {
                Text = "|",
                AutoSize = true,
                ForeColor = Color.FromArgb(203, 213, 225),
                Padding = new Padding(4, 5, 4, 0),
                Margin = new Padding(2, 3, 2, 3)
            };

            if (btnReplace != null) btnReplace.Text = "Replace…";
            if (recentCombo != null) recentCombo.Width = 150;
            if (chkGridSnap != null) chkGridSnap.Text = "Grid snap";
            if (chkMoveMode != null) chkMoveMode.Text = "Move drag";
            if (chkBoxMode != null) chkBoxMode.Text = "Box select";

            toolbarGizmoCheck = chkGizmo;
            toolbarGridSnapCheck = chkGridSnap;

            source.Controls.Clear();
            if (recentCombo != null) source.Controls.Add(recentCombo);
            source.Controls.Add(makeSep());
            if (btnAdd != null) source.Controls.Add(btnAdd);
            if (btnReplace != null) source.Controls.Add(btnReplace);
            source.Controls.Add(makeSep());
            source.Controls.Add(dupBtn);
            if (btnDelete != null) source.Controls.Add(btnDelete);
            if (btnDeselect != null) source.Controls.Add(btnDeselect);
            source.Controls.Add(makeSep());
            source.Controls.Add(transformObjBtn);
            source.Controls.Add(groupBtn);
            source.Controls.Add(makeSep());
            if (chkGizmo != null) source.Controls.Add(chkGizmo);
            if (chkGridSnap != null) source.Controls.Add(chkGridSnap);
            if (chkMoveMode != null) source.Controls.Add(chkMoveMode);
            if (chkBoxMode != null) source.Controls.Add(chkBoxMode);

            routeTools.Controls.Add(indepHandles);

            if (copyBtn != null) routeTools.Controls.Add(copyBtn);
            if (pasteBtn != null) routeTools.Controls.Add(pasteBtn);

            var transformBtn = new Button { Text = "Transform ▾", AutoSize = true, Margin = new Padding(4, 3, 4, 3) };
            var transformMenu = new ContextMenuStrip();
            var menuReverse = new ToolStripMenuItem("Reverse Route Direction", null, (s, e) => Preview.ReverseSelectedRoute());
            var menuSmooth = new ToolStripMenuItem("Smooth Bezier Curve", null, (s, e) => Preview.SmoothSelectedRoute((ModifierKeys & Keys.Shift) != 0));
            var menuRotate = new ToolStripMenuItem("Rotate Route…", null, (s, e) => RotateRouteDialog());
            transformMenu.Items.AddRange(new ToolStripItem[] { menuReverse, menuSmooth, menuRotate });
            transformBtn.Click += (s, e) => transformMenu.Show(transformBtn, 0, transformBtn.Height);
            routeTools.Controls.Add(transformBtn);

            var toolsBtn = new Button { Text = "Route Tools ▾", AutoSize = true, Margin = new Padding(4, 3, 4, 3) };
            var toolsMenu = new ContextMenuStrip();
            var menuWaits = new ToolStripMenuItem("Set All Wait Times…", null, (s, e) => BulkRouteWaitDialog());
            var menuExport = new ToolStripMenuItem("Export Route to CSV…", null, (s, e) => TriggerExportRouteCsv());
            var menuImport = new ToolStripMenuItem("Import Route from CSV…", null, (s, e) => TriggerImportRouteCsv());
            toolsMenu.Items.AddRange(new ToolStripItem[] { menuWaits, new ToolStripSeparator(), menuExport, menuImport });
            toolsBtn.Click += (s, e) => toolsMenu.Show(toolsBtn, 0, toolsBtn.Height);
            routeTools.Controls.Add(toolsBtn);

            Action updateRouteDropdowns = () =>
            {
                var draft = Preview.SelectedDraft();
                bool hasRoute = (Preview.SelectedObjects.Length == 1 && draft != null && draft.Route.Count > 0)
                             || Preview.SelectedObjects.Length > 1;
                transformBtn.Enabled = hasRoute && !Preview.PlacementPending;
                toolsBtn.Enabled = (hasRoute || Preview.SelectedObjects.Length >= 1) && !Preview.PlacementPending;
                menuReverse.Enabled = menuSmooth.Enabled = menuWaits.Enabled = hasRoute && !Preview.PlacementPending;
                menuRotate.Enabled = Preview.HasRoutableSelection() && !Preview.PlacementPending;
                menuExport.Enabled = (Preview.SelectedObjects.Length == 1 && draft != null && draft.Route.Count > 0) && !Preview.PlacementPending;
                menuImport.Enabled = Preview.SelectedObjects.Length >= 1 && !Preview.PlacementPending;
            };
            Preview.SelectionChanged += (s, e) => updateRouteDropdowns();
            updateRouteDropdowns();

            routes.Controls.Add(routeTools);
            tabs.TabPages.Add(routes);

            routeTools.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Mint route and waypoint handles: active object. Other colors: comparison only.",
            });

            tabs.Multiline = true;
        }

        /// <summary>
        /// Dynamically sizes the toolbar row to fit the currently-selected
        /// tab's controls, while preserving a minimum usable scene area below.
        /// </summary>
        private void AdaptToolbarHeight(TableLayoutPanel layout, TabControl tabs)
        {
            bool updating = false;

            Action resize = () =>
            {
                if (updating || tabs.SelectedTab == null) return;
                updating = true;
                try
                {
                    int rows = Math.Max(1, tabs.RowCount);
                    int header = rows * Math.Max(20, tabs.ItemSize.Height) + 8;
                    int content = 64;

                    foreach (FlowLayoutPanel flow in tabs.SelectedTab.Controls.OfType<FlowLayoutPanel>())
                    {
                        int available = Math.Max(100, tabs.ClientSize.Width - 24);
                        content = Math.Max(content, flow.GetPreferredSize(new Size(available, 0)).Height + 8);
                    }

                    if (tabs.SelectedTab.Text == "Terrain") content += 26;

                    // Preserve usable scene space. Overflow remains scrollable
                    // within the tool panels.
                    int scaledLimit = (int)Math.Ceiling(180 * Math.Max(1, Font.Height / 13.0));
                    int sceneLimit  = Math.Max(180, ClientSize.Height - 300);

                    // In Option 1, Row 0 is 0px because document tools are hosted in the top MenuStrip.
                    // Row 3 is a compact 26px single-line status bar at the bottom.
                    layout.RowStyles[0].Height = 0;
                    layout.RowStyles[1].Height = Math.Min(Math.Min(scaledLimit, sceneLimit), header + content);
                    layout.RowStyles[3].Height = 26;
                }
                finally { updating = false; }
            };

            tabs.SelectedIndexChanged += (s, e) =>
            {
                resize();
                if (tabs.SelectedTab != null && tabs.SelectedTab.Text == "Routes")
                {
                    userForcedRouteCollapse = false;
                    SetWaypointPanelCollapsed(false);
                    if (sidebarTabs != null && sidebarTabs.TabPages.Count > 0)
                        sidebarTabs.SelectedIndex = 0;
                }
            };
            SizeChanged                += (s, e) => resize();
            FontChanged                += (s, e) => resize();
            Shown                      += (s, e) => resize();
        }
    }
}