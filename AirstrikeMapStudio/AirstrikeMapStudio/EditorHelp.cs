using System;
using System.Drawing;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// The F1 help dialog. The <see cref="Guide"/> text is user-facing and
    /// should be edited only when the editor's behavior changes.
    /// </summary>
    public static class EditorHelp
    {
        public const string Guide = @"AIRSTRIKE MAP STUDIO — 3D WORKSPACE

GET STARTED
Open a map, or create a new map with an environment preset. Use Save As for a separate mission file. Mission settings control the mission assignment and environment; their save is separate from map saving.

CAMERA AND SELECTION
Window size/maximized state, sidebar width and route display preferences are saved when the 3D workspace closes. View & camera contains Reset view preferences. Editing tools always start in selection mode. Preferences are stored in editor-preferences.txt beside the editor; mission fog and map camera position still use their normal defaults.
Only one editing tool is active at a time. The bottom status line describes the active tool. Escape cancels an unfinished action and returns to selection/navigation; text and property editors retain their own Escape behavior. Leaving Terrain or Tiles & roads turns its brush off; other tools remain active until changed or cancelled.
Drag to orbit; right-drag to pan; wheel to zoom. View & camera contains Fit map and Focus selection. Ctrl+F frames the selection. The Map overview tab shows objects and view center; click it to navigate.
The Object list reports selected objects hidden by its filters. Clear filters reveals all rows without changing the selection. Group actions still include filtered-out selected objects.
Click an object to select it. Ctrl-click toggles group membership. Enable Box select and drag a rectangle; Ctrl-drag toggles enclosed model centers. Hidden objects can be included. Alt-click opens overlapping model choices.
Turn Box select off before dragging objects. Terrain, tile and placement tools take priority over selection tools.

OBJECTS AND GROUPS
Objects & selection contains placement, movement, duplication (including Clone +2,+2), deletion, Box select, Rotate group, Align / space, and Shared settings.
The interactive 3D Transform Gizmo appears when an object is selected: Red arrow = move along X, Green arrow = move along Y, Gold/Cyan ring = rotate facing in 30° increments, Center diamond = planar move. Drag any handle to interact. Toggle with the '3D Gizmo' toolbar checkbox or View menu.
Align / space aligns spawn centers on X or Y to the active object, or distributes three or more objects between their outermost coordinates. Whole-cell spacing can differ by one cell. Move waypoints controls route translation. Alignment may overlap objects; Ctrl+Z restores the whole operation.
Add object arms placement; click terrain to place it. Duplicate copies the selection; click a destination. Escape cancels pending placement.
Move: drag object moves selected objects together. Move waypoints in the Routes tab controls whether routes follow. Numeric Position fields change spawn only.
Move to / Duplicate to use the primary object's destination and preserve group spacing. Their route checkbox is independent for that operation.
Rotate group offers a pivot and optional route rotation. Grid rounding can slightly change spacing. Route segments keep their shape between snapped endpoints; undo restores exact original data.
Shared settings changes only checked fields. Route behavior skips objects without waypoints. Replace object retains route, facing, drops and script override.

ROUTES
Routes contains Edit route, Move waypoints, Rotate route, Copy/Paste route, and Align final approach. Drop-item table is directly accessible from the document toolbar. Waypoint rows and precise settings remain in the right panel.
Show routes for chooses Active object, Selected group or All objects. The Route checkbox in View & camera hides all routes. The active route is mint with waypoint numbers; other routes use repeating comparison colors and have no editing handles. Select a single object to edit its route. Dashed lines connect spawn to the first waypoint. Routes are hidden while dragging objects.
Select a waypoint row for handles. Edit route enables dragging points and handles. Handle A (arrival) is cyan, Handle B (departure) is coral pink. By default, handles move independently without affecting the opposite side; hold Shift or Alt to mirror. Use Free WP / Mirror WP or the 'Independent handles' checkbox to toggle constraints. Changing waypoint X/Y shifts both handles.
Rotate route turns waypoints and handles around spawn, first waypoint or route center, with an option to maintain object facing.
Align final approach changes the last arrival handle using the preceding segment. It does not set spawn facing or wait.
Wait interpretation depends on scripts. Stored Mode meaning is not fully verified. A route preview is geometry, not gameplay simulation.

DROPS AND SCRIPTS
Drop-item table shows entry usage. Replacement changes all users; removal remaps references to your chosen entry. 0 means no map override, not necessarily no drop.
Script override takes priority over the default definition. Clearing it restores the default selection. Existence checks do not prove behavior or attachment compatibility.

TERRAIN AND TILES
Terrain supports raise/lower, smooth, and level/flatten with radius, strength and soft-edge controls. Level can sample the starting height or use a stored target.
Tiles & roads supports painting, rotation and erasing. Release a stroke to commit one undo step. Escape cancels the current stroke.

WATER OPACITY
Mission settings -> Water opacity accepts 0 (transparent) through 1 (opaque), with dot or comma decimal input. Save mission updates the preview and levels.txt. Water height and ship placement remain independent of opacity; View & camera -> Animate water (preview) enables a small vertical oscillation and texture movement. Uncheck to pause at the current phase. It pauses during editing and when hidden/minimized; saved heights and object positions do not change. The preview clock is independent of the game.

SAVE AND RECOVERY
Assets & export -> Test mission temporarily assigns the saved open map to a chosen mission. Optionally use the open map's environment. The map must be inside the selected game data folder. Launch game after assigning starts AirStrike3D.exe after saving and assigning successfully. Uncheck it to launch manually. If the game is already running, reload the chosen mission there. Launching does not automatically select the mission. The assignment persists after closing/restarting the editor. Open Test mission again and choose Restore previous assignment when finished. External changes to levels.txt prevent automatic restore; the session record and backups are retained. This operation is separate from map undo/redo.
Assets & export provides game data folder selection, scrollable asset diagnostics and waypoint CSV export for one selected routed object. Switching assets rebuilds the preview while retaining the map, unsaved edits, undo history and camera. It does not copy game assets or change the mission assignment. CSV exports stored route values; it does not simulate movement.
Inspect selected model shows resolved model, texture and script paths, missing files and static assembly reports for one selected object. Open 3D preview rotates and zooms the model; Attachments toggles supported static parts. Inspection never applies map edits. Script overrides are reported but their runtime effects are not simulated.
Existing map files receive backups when overwritten. Check map reports structural, asset and route concerns; it cannot guarantee gameplay.
Recovery copies save every 60 seconds while edits are unsaved. Restart offers restoration as an unsaved map. Save As stores the recovered map. Declined recovery copies remain available; normal save or explicit discard clears the current recovery copy. Up to one minute of edits may not be recovered.

SHORTCUTS IN 3D
Ctrl+N  New map
Ctrl+O  Open map
Ctrl+S  Save
Ctrl+Shift+S  Save As
Ctrl+Z  Undo map edit
Ctrl+Y / Ctrl+Shift+Z  Redo map edit
Ctrl+F  Focus selection
Ctrl+T  Move selection to coordinates
Ctrl+D  Duplicate selection to coordinates
Ctrl+Shift+D  Duplicate selection with a +2,+2 offset
Ctrl+Shift+C  Copy selected object's route
Ctrl+Shift+V  Paste route onto selected object
Ctrl+Shift+R  Rotate selected route(s)
F4 / Ctrl+Shift+E  Script Studio (.scr editor, decompiler, assembler)
F5  Check map
F1  This guide
Escape / Right-Click tap  Cancel placement, disarm tool, or deselect object
Text fields may handle their own editing shortcuts. Finish or cancel text edits before using map shortcuts.
Undo/redo, focus-selection and numeric move/duplicate shortcuts are reserved for the focused text, numeric, dropdown or property editor. Click the viewport to use those shortcuts on the map.

PREVIEW LIMITS
Lighting, water and some attachments remain approximate. Runtime animation, aiming and script state are not simulated. 
Test saved missions in-game..";

        public static void Show(IWin32Window owner)
        {
            using (var form = new Form
            {
                Text = "Map editor help and shortcuts",
                Width = 760,
                Height = 680,
                MinimumSize = new Size(500, 400),
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var text = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Font = new Font("Segoe UI", 10),
                    Text = Guide.Replace("\n", "\r\n"),
                };
                var close = new Button
                {
                    Text = "Close",
                    Dock = DockStyle.Bottom,
                    Height = 32,
                    DialogResult = DialogResult.Cancel,
                };
                form.Controls.Add(text);
                form.Controls.Add(close);
                form.CancelButton = close;
                form.ShowDialog(owner);
            }
        }

        public static void ShowAbout(IWin32Window owner)
        {
            using (var form = new Form
            {
                Text = "About AirStrike Map Studio",
                Width = 590,
                Height = 520,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(245, 247, 250),
            })
            {
                var titlePanel = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 85,
                    BackColor = Color.FromArgb(28, 35, 48),
                };
                var appTitle = new Label
                {
                    Text = "AirStrike 3D Map Studio & Script Editor",
                    Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                    ForeColor = Color.White,
                    AutoSize = true,
                    Location = new Point(18, 16),
                };
                var appVersion = new Label
                {
                    Text = "Version 1.2 — Dual-Engine Edition (x86)",
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                    ForeColor = Color.FromArgb(170, 195, 230),
                    AutoSize = true,
                    Location = new Point(20, 46),
                };
                titlePanel.Controls.Add(appTitle);
                titlePanel.Controls.Add(appVersion);

                var bottomPanel = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 52,
                    BackColor = Color.FromArgb(235, 238, 242),
                };
                var btnClose = new Button
                {
                    Text = "Close",
                    Width = 90,
                    Height = 32,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Location = new Point(form.ClientSize.Width - 105, 10),
                    DialogResult = DialogResult.OK,
                };
                var btnGithub = new Button
                {
                    Text = "Open GitHub Repo",
                    Width = 145,
                    Height = 32,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                    Location = new Point(15, 10),
                };
                btnGithub.Click += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/alfypeol/AirStrikeStudio") { UseShellExecute = true }); } catch (Exception logEx) { AppLog.Warn(logEx); }
                };
                bottomPanel.Controls.Add(btnClose);
                bottomPanel.Controls.Add(btnGithub);

                var content = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(250, 252, 255),
                    Font = new Font("Segoe UI", 9.25f),
                    Padding = new Padding(12),
                    Text =
@"Created by alfypeol under the MIT License.

Dual-Engine 3D level editor, real-time terrain sculptor, Bézier flight route authoring suite, and RCSL bytecode script decompiler/compiler for classic helicopter combat games.

─────────────────────────────────────────────────────────────
ACKNOWLEDGEMENTS & CREDITS

• OpenAirStrike (https://github.com/propaint/openairstrike)
  Created by @propaint.
  A modern, from-scratch C++17 engine reimplementation of AirStrike 3D.
  Powers Map Studio's instant headless map integrity verification (F7)
  and direct CLI test runner (F6). Web demo: https://propaint.github.io/openairstrike/

• DivoGames
  Original creators of the classic arcade helicopter combat trilogy:
  - AirStrike 3D (2002)
  - AirStrike II (2003)
  - AirStrike II: Gulf Thunder (2004)

• Google ANGLE Project
  Direct3D 11 translation layer providing seamless hardware-accelerated
  OpenGL ES rendering pipeline across Windows systems.
"
                };

                form.Controls.Add(content);
                form.Controls.Add(bottomPanel);
                form.Controls.Add(titlePanel);
                form.AcceptButton = btnClose;
                form.CancelButton = btnClose;
                form.ShowDialog(owner);
            }
        }
    }

    /// <summary>
    /// One field shared across the <see cref="GlTerrainWindow"/> partials:
    /// the F5 "Check map" callback, wired by
    /// <see cref="GlTerrainWindow.ConnectMapChecks"/>.
    /// </summary>
    public sealed partial class GlTerrainWindow
    {
        System.Action runMapChecks;
    }
}