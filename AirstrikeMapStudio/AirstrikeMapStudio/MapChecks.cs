using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// One issue found by <see cref="MapChecks.Run"/>.
    /// </summary>
    public sealed class MapIssue
    {
        public string Severity;
        public string Message;
        public int ObjectIndex = -1;
        public int WaypointIndex = -1;

        public MapIssue(string severity, string message, int obj = -1, int waypoint = -1)
        {
            Severity = severity;
            Message = message;
            ObjectIndex = obj;
            WaypointIndex = waypoint;
        }
    }

    /// <summary>
    /// The "Check map" validator. Runs structural, asset, and mission
    /// consistency checks against a loaded map, without modifying it.
    /// </summary>
    public static class MapChecks
    {
        public static List<MapIssue> Run(Map map, string dataFolder, string mapPath, string environmentProfile)
        {
            var result = new List<MapIssue>();

            void add(string severity, string message, int obj, int waypoint)
            {
                result.Add(new MapIssue(severity, message, obj, waypoint));
            }

            if (map == null)
            {
                add("Error", "No map is open.", -1, -1);
                return result;
            }

            // First: does the map itself round-trip?
            try
            {
                Map.Load(map.Bytes());
            }
            catch (Exception ex)
            {
                add("Error", "Map cannot be reopened safely: " + ex.Message, -1, -1);
            }

            using (var assets = new Assets())
            {
                try
                {
                    assets.Open(dataFolder);
                }
                catch (Exception ex)
                {
                    add("Error", "Cannot check game assets: " + ex.Message, -1, -1);
                    return result;
                }

                // Load particle-system textures by scanning particles/**.ps.
                // These names (PS_GUNSMOKE, PS_MISIL, ...) are referenced by
                // .obj files' attach lines but aren't in the object definition
                // table; they need their own lookup.
                var particleTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string particleFolder = Path.Combine(dataFolder, "particles");
                if (Directory.Exists(particleFolder))
                {
                    foreach (string ps in Directory.GetFiles(particleFolder, "*.ps", SearchOption.AllDirectories))
                    {
                        string text = Regex.Replace(File.ReadAllText(ps), @"//[^\r\n]*", "");
                        foreach (Match part in Regex.Matches(text, @"(?m)^\s*([\w#.-]+)\s*\{([^{}]*)\}"))
                            particleTextures[part.Groups[1].Value] =
                                MissionPreset.Quoted(part.Groups[2].Value, "texture");
                    }
                }

                // Helper: assert a file exists, throwing an error into the result.
                void file(string relative, string label, int obj)
                {
                    if (string.IsNullOrEmpty(relative)) return;
                    try
                    {
                        if (!File.Exists(assets.Resolve(relative)))
                            add("Error", label + " was not found: " + relative, obj, -1);
                    }
                    catch (Exception ex)
                    {
                        add("Error", label + ": " + ex.Message, obj, -1);
                    }
                }

                for (int i = 0; i < map.Objects.Count; i++)
                {
                    var p = map.Objects[i];
                    int index = i; // capture for the closures below

                    // ── Structure ────────────────────────────────────────
                    if (p.X >= map.Width || p.Y >= map.Height)
                        add("Warning", "Spawn is outside the map grid.", i, -1);

                    // ── Drop entry ───────────────────────────────────────
                    if (p.Setting7 > map.Drops.Count)
                    {
                        add("Error", "Drop-item number refers to a missing table entry.", i, -1);
                    }
                    else if (p.Setting7 > 0 && !assets.Definitions.ContainsKey(map.Drops[p.Setting7 - 1]))
                    {
                        add("Warning", "Drop-item definition was not found: " + map.Drops[p.Setting7 - 1], i, -1);
                    }

                    // ── Per-object script override ───────────────────────
                    file(p.Script, "Override script", i);

                    if (p.Type >= map.Types.Count)
                    {
                        add("Error", "Object type refers to a missing table entry.", i, -1);
                        continue;
                    }

                    // ── Assembly walk ────────────────────────────────────
                    string name = map.Types[p.Type];
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool anyWaypointScript = false;

                    Action<string, string> checkScr = (scrPath, label) =>
                    {
                        if (string.IsNullOrEmpty(scrPath)) return;
                        file(scrPath, label, index);
                        try
                        {
                            string full = assets.Resolve(scrPath);
                            if (File.Exists(full))
                            {
                                var scr = ScrFile.Parse(File.ReadAllBytes(full));
                                if (scr.CallsWaypointFunctions)
                                    anyWaypointScript = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            add("Error", label + " is not a valid RCSL .scr file (" + scrPath + "): " + ex.Message, index, -1);
                        }
                    };

                    if (p.Script.Length > 0)
                        checkScr(p.Script, "Override script");

                    Action<string, bool> inspect = null;
                    inspect = (type, root) =>
                    {
                        if (!seen.Add(type)) return;

                        if (!assets.Definitions.TryGetValue(type, out var def))
                        {
                            // Check particle system names as a fallback.
                            if (particleTextures.TryGetValue(type, out string texture))
                            {
                                file(texture, type + " particle texture", index);
                                return;
                            }

                            add(root ? "Warning" : "Caution",
                                "Definition was not found by the object/particle readers: " + type,
                                index, -1);
                            return;
                        }

                        file(def.Model, type + " model", index);
                        file(def.Skin,  type + " skin",  index);

                        // The root object's definition script is only used if
                        // there's no map override; attachments' scripts are
                        // always loaded.
                        if (!root || p.Script.Length == 0)
                            checkScr(def.Script, type + " script");

                        foreach (var attachment in def.Attachments)
                            inspect(attachment.ObjectName, false);
                    };
                    inspect(name, true);

                    if (p.Route.Count > 0 && !anyWaypointScript)
                    {
                        add("Caution",
                            "Object has " + p.Route.Count + " waypoint(s), but its .scr script does not call MoveToNextWP / lMoveToNextWP.",
                            i, -1);
                    }

                    // ── Waypoints ────────────────────────────────────────
                    for (int j = 0; j < p.Route.Count; j++)
                    {
                        var w = p.Route[j];

                        if (w.X < 0 || w.Y < 0 || w.X >= map.Width || w.Y >= map.Height)
                            add("Warning", "Waypoint " + (j + 1) + " is outside the map grid.", i, j);

                        if (new[] { w.AX, w.AY, w.BX, w.BY, w.Value }
                            .Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                        {
                            add("Error", "Waypoint " + (j + 1) + " contains a non-finite number.", i, j);
                        }
                        else if (w.Value < 0)
                        {
                            add("Caution", "Negative waypoint wait value; behavior depends on the script.", i, j);
                        }
                    }

                    // ── Route geometry warnings ─────────────────────────
                    if (RouteTools.AheadOfSpawn(p))
                        add("Caution",
                            "Route extends ahead of spawn Y. Check encounter activation " +
                            "in-game; this is not a proven error.", i, -1);

                    if (name == "rocketlauncher_mobile_lgreen"
                        && p.Script.Length == 0
                        && p.Route.Count > 0
                        && p.Link == 0)
                    {
                        add("Caution",
                            "This tested launcher disables its weapons after finishing " +
                            "a non-looping route and final wait.", i, p.Route.Count - 1);
                    }
                }

                // ── Mission cross-references ────────────────────────────
                try
                {
                    string levelsPath = Path.Combine(dataFolder, "maps", "levels.txt");
                    if (!File.Exists(levelsPath))
                    {
                        add("Warning", "levels.txt is missing; mission assignment could not be checked.", -1, -1);
                    }
                    else
                    {
                        var doc = new MissionDocument(levelsPath);

                        var matches = doc.Entries.Where(e =>
                        {
                            try
                            {
                                return mapPath.Length > 0
                                    && string.Equals(
                                        MissionSettings.FullPath(dataFolder, MissionPreset.Quoted(e.Block, "map")),
                                        Path.GetFullPath(mapPath),
                                        StringComparison.OrdinalIgnoreCase);
                            }
                            catch { return false; }
                        }).ToList();

                        if (matches.Count == 0)
                            add("Info", "No mission currently points to this map. " +
                                       "Assign it in Mission settings before playing.", -1, -1);

                        if (matches.Count > 1)
                            add("Warning", "Multiple missions use this map; their environments " +
                                          "may differ. Select the intended mission in-game.", -1, -1);

                        foreach (var entry in matches)
                        {
                            var settings = MissionSettings.Read(entry.Block);

                            file(settings.WaterEnabled ? settings.WaterTexture : "",
                                 "Mission water texture", -1);

                            for (int n = 1; n <= 4; n++)
                                file(Path.Combine(settings.TextureFolder, "texture" + n + ".tga"),
                                     "Mission ground texture", -1);

                            if (settings.MaximumHeight <= settings.MinimumHeight)
                                add("Error", "Mission height maximum must exceed its minimum.", -1, -1);

                            if (settings.FogEnabled && settings.FogEnd <= settings.FogStart)
                                add("Error", "Mission fog end must exceed its start.", -1, -1);

                            // Compare the map's stored environment profile to
                            // the mission's. The game uses the mission's.
                            string stored = environmentProfile ?? MissionPreset.ReadProfile(mapPath);
                            if (stored != null)
                            {
                                var saved = MissionSettings.Read(stored);
                                saved.MapFile = settings.MapFile;

                                var expected = settings.Fields();
                                var differences = saved.Fields()
                                    .Where(kv => kv.Key != "map" && kv.Value != expected[kv.Key])
                                    .Select(kv => kv.Key)
                                    .ToArray();

                                if (differences.Length > 0)
                                    add("Warning",
                                        "Saved environment profile differs from " + entry + ": " +
                                        string.Join(", ", differences) +
                                        ". The game uses the mission settings.", -1, -1);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    add("Warning", "Mission settings could not be fully checked: " + ex.Message, -1, -1);
                }
            }

            return result
                .OrderBy(i => i.Severity == "Error"   ? 0
                            : i.Severity == "Warning" ? 1
                            : i.Severity == "Caution" ? 2
                            : 3)
                .ToList();
        }
    }

    /// <summary>
    /// The "Check map" results dialog.
    /// </summary>
    public sealed class MapCheckDialog : Form
    {
        public MapIssue SelectedIssue;

        private readonly ListView list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
        };

        public MapCheckDialog(List<MapIssue> issues, bool saving)
        {
            Text = saving ? "Check map before saving" : "Map checks";
            Width = 920;
            Height = 540;
            MinimumSize = new Size(700, 360);
            StartPosition = FormStartPosition.CenterParent;

            list.Columns.Add("Level", 85);
            list.Columns.Add("Object / waypoint", 135);
            list.Columns.Add("Finding", 650);

            foreach (var issue in issues)
            {
                var row = new ListViewItem(issue.Severity) { Tag = issue };
                row.SubItems.Add(issue.ObjectIndex < 0
                    ? "Map / mission"
                    : "#" + issue.ObjectIndex + (issue.WaypointIndex < 0 ? "" : " / WP " + (issue.WaypointIndex + 1)));
                row.SubItems.Add(issue.Message);
                list.Items.Add(row);
            }

            var summary = new Label
            {
                Dock = DockStyle.Top,
                Height = 55,
                Padding = new Padding(7),
                Text = issues.Count == 0
                    ? "No issues found by these checks. This does not verify gameplay behavior."
                    : string.Join("   ", issues
                        .GroupBy(i => i.Severity)
                        .Select(g => g.Key + ": " + g.Count())
                        .ToArray()) +
                      "\r\nSelect a finding to read it below. Cautions need in-game testing; " +
                      "no automatic changes are made.",
            };

            var detail = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 68,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
            var select = new Button { Text = "Select object", AutoSize = true, Enabled = false };
            var close  = new Button
            {
                Text = saving ? "Cancel save" : "Close",
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
            };

            Action jump = () =>
            {
                if (list.SelectedItems.Count == 0) return;
                var issue = (MapIssue)list.SelectedItems[0].Tag;
                if (issue.ObjectIndex < 0) return;
                SelectedIssue = issue;
                DialogResult = DialogResult.Cancel;
                Close();
            };

            select.Click         += (s, e) => jump();
            list.DoubleClick     += (s, e) => jump();
            list.SelectedIndexChanged += (s, e) =>
            {
                var issue = list.SelectedItems.Count == 0
                    ? null
                    : (MapIssue)list.SelectedItems[0].Tag;
                select.Enabled = issue != null && issue.ObjectIndex >= 0;
                detail.Text    = issue == null ? "" : issue.Message;
            };

            buttons.Controls.Add(select);
            if (saving)
            {
                var save = new Button { Text = "Save anyway", AutoSize = true, DialogResult = DialogResult.OK };
                buttons.Controls.Add(save);
            }
            buttons.Controls.Add(close);

            CancelButton = close;

            Controls.Add(list);
            Controls.Add(detail);
            Controls.Add(buttons);
            Controls.Add(summary);
        }
    }

    public partial class Studio
    {
        private Action<int, int> checkFocus;

        private bool ShowMapChecks(IWin32Window owner, string path, bool saving)
        {
            var issues = MapChecks.Run(map, assets.Root, path, environmentOverride);

            if (saving && !issues.Any(i => i.Severity == "Error" || i.Severity == "Warning"))
                return true;

            using (var dialog = new MapCheckDialog(issues, saving))
            {
                var answer = dialog.ShowDialog(owner);

                if (dialog.SelectedIssue != null)
                {
                    var issue = dialog.SelectedIssue;
                    if (checkFocus != null)
                    {
                        checkFocus(issue.ObjectIndex, issue.WaypointIndex);
                    }
                    else
                    {
                        SelectObject(issue.ObjectIndex, issue.WaypointIndex, false);
                    }
                }

                return answer == DialogResult.OK;
            }
        }
    }

    public sealed partial class GlTerrainControl
    {
        public void FocusCheckIssue(int index, int waypoint)
        {
            if (index < 0 || index >= map.Objects.Count) return;

            CancelPlacement();
            groupSelection.Clear();
            SelectedIndex = index;
            RouteRows();
            RouteWaypoint = waypoint >= 0 && waypoint < map.Objects[index].Route.Count ? waypoint : -1;

            var p = map.Objects[index];
            CenterX = Math.Max(0, Math.Min(map.Width,  p.X));
            CenterY = Math.Max(0, Math.Min(map.Height, p.Y));
            ShowRoute = true;
            NotifySelection();
            Changed();
        }
    }

    public sealed partial class GlTerrainWindow
    {
        public void ConnectMapChecks(Action check)
        {
            runMapChecks = check;
            Action trigger = () => { Validate(); check(); };
            if (menuMapChecks != null) menuMapChecks.Click += (s, e) => trigger();

            var b = new Button { Text = "Check map…", AutoSize = true };
            b.Click += (s, e) => trigger();
            AddDocumentTool(b);
        }
    }
}