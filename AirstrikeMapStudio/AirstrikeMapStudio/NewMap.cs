using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// One <c>{ ... }</c> block from <c>maps\levels.txt</c>, exposed as a
    /// selectable mission preset for the "New map" dialog.
    /// </summary>
    public sealed class MissionPreset
    {
        public string Label;      // "name — textures", for the dropdown
        public string Block;      // the raw { ... } block
        public string MapPath;    // resolved absolute path to the .hsc

        public override string ToString() => Label;

        /// <summary>
        /// Returns the quoted value of <paramref name="key"/> in <paramref name="block"/>,
        /// or "" if the key is absent.
        /// </summary>
        public static string Quoted(string block, string key)
        {
            var m = Regex.Match(block, "(?m)^[ \\t]*" + key + "[ \\t]+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// Returns the numeric value of <paramref name="key"/> in <paramref name="block"/>,
        /// or <paramref name="fallback"/> if absent or unparseable.
        /// </summary>
        public static double Number(string block, string key, double fallback)
        {
            var m = Regex.Match(block, @"(?m)^[ \t]*" + key + @"[ \t]+([-+0-9.]+)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                                CultureInfo.InvariantCulture, out v)
                ? v
                : fallback;
        }

        /// <summary>
        /// Parses <c>maps\levels.txt</c> and returns one preset per mission
        /// block whose <c>map</c> field points at an existing .hsc file
        /// inside the data folder.
        /// </summary>
        public static List<MissionPreset> Load(string dataFolder)
        {
            var result = new List<MissionPreset>();

            string path = Path.Combine(dataFolder, "maps", "levels.txt");
            if (!File.Exists(path)) return result;

            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"(?s)\{[^{}]*\}"))
            {
                string relative = Quoted(m.Value, "map");
                if (relative.Length == 0) continue;

                string full = Path.GetFullPath(Path.Combine(dataFolder, relative));
                if (!full.StartsWith(Path.GetFullPath(dataFolder).TrimEnd('\\') + "\\",
                                     StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(full))
                    continue;

                result.Add(new MissionPreset
                {
                    Label   = Quoted(m.Value, "name") + " — " + Quoted(m.Value, "textures"),
                    Block   = m.Value,
                    MapPath = full,
                });
            }

            return result;
        }

        /// <summary>
        /// Reads the editor's saved <c>.environment.txt</c> profile for a
        /// map, if it exists.
        /// </summary>
        public static string ReadProfile(string path)
        {
            return path.Length > 0 && File.Exists(path + ".environment.txt")
                ? File.ReadAllText(path + ".environment.txt")
                : null;
        }

        /// <summary>
        /// Resolves the environment block for a map. Tries, in order:
        ///   1. A single matching mission in levels.txt (by filename).
        ///   2. The supplied fallback block.
        ///   3. The editor's saved <c>.environment.txt</c> profile.
        /// Returns null if none are available.
        /// </summary>
        public static string Resolve(string dataFolder, string name, string supplied)
        {
            var matches = Load(dataFolder)
                .Where(p => string.Equals(Path.GetFileName(p.MapPath),
                                          Path.GetFileName(name),
                                          StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1) return matches[0].Block;
            if (supplied != null) return supplied;
            if (matches.Count > 1) return ReadProfile(name) ?? matches[0].Block;
            return ReadProfile(name);
        }

        /// <summary>
        /// Extracts the environment-relevant lines from a mission block
        /// (<c>textures</c>, <c>hmin</c>, <c>hmax</c>, <c>fog</c>, <c>sun</c>,
        /// <c>water</c>, <c>night</c>) and wraps them in a new <c>{ ... }</c>.
        /// </summary>
        public static string EnvironmentOnly(string block)
        {
            var lines = new List<string>();
            foreach (string line in block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (Regex.IsMatch(line, @"^\s*(textures|hmin|hmax|fog|sun|water|night)\b"))
                    lines.Add(line.Trim());

            return "{\r\n" + string.Join("\r\n", lines.ToArray()) + "\r\n}";
        }

        /// <summary>
        /// Saves the environment block as <c>&lt;map&gt;.environment.txt</c>
        /// beside the map, with an explanatory header. Not what the game
        /// reads; the game reads the mission's block from levels.txt.
        /// </summary>
        public static void SaveProfile(string path, string block)
        {
            string full = path + ".environment.txt";
            string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";

            string text =
                "// Editor environment. The game reads these settings from its mission in levels.txt.\r\n" +
                "// Copy the settings inside these braces into the mission used to play this map.\r\n" +
                EnvironmentOnly(block) + "\r\n";

            try
            {
                File.WriteAllText(temp, text);
                if (File.Exists(full))
                    File.Replace(temp, full, full + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                else
                    File.Move(temp, full);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// The "New map" dialog. Offers a mission preset (for its environment),
    /// a map size, and a starting terrain height.
    /// </summary>
    /// <remarks>
    /// The map's on-disk format already stores arbitrary width and height as
    /// u32, so no format change is needed for arbitrary sizes. The engine,
    /// however, was designed around a 32-column grid, and larger maps may not
    /// load or render correctly in-game. The dialog warns when the chosen
    /// size exceeds the stock envelope but does not block it.
    /// </remarks>
    public sealed class NewMapDialog : Form
    {
        // Stock envelope that the shipped missions use. Exceeding it produces
        // an advisory warning but is not blocked.
        private const int StockWidth  = 32;
        private const int StockHeight = 256;

        private readonly ComboBox      sizePreset  = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly NumericUpDown width       = new NumericUpDown { Minimum = Map.MinMapDimension, Maximum = Map.MaxMapWidth,  Value = 32,  Width = 90 };
        private readonly NumericUpDown height      = new NumericUpDown { Minimum = Map.MinMapDimension, Maximum = Map.MaxMapHeight, Value = 256, Width = 90 };
        private readonly ComboBox      preset      = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 550 };
        private readonly NumericUpDown startHeight = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 128, Width = 90 };
        private readonly Label         detail      = new Label { AutoSize = false, Width = 550, Height = 95 };

        public Map CreatedMap;
        public string EnvironmentBlock;

        public NewMapDialog(string dataFolder)
        {
            Text = "New map";
            ClientSize = new Size(600, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(15),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            Controls.Add(flow);

            // ── Size presets + numeric inputs ───────────────────────────
            flow.Controls.Add(new Label { Text = "Quick size presets", AutoSize = true });
            sizePreset.Items.Add("32 × 256 — standard mission");
            sizePreset.Items.Add("32 × 128 — short map");
            sizePreset.Items.Add("Custom…");
            sizePreset.SelectedIndex = 0;
            flow.Controls.Add(sizePreset);

            flow.Controls.Add(new Label { Text = "Map dimensions (cells)", AutoSize = true });

            var dims = new FlowLayoutPanel { Width = 550, Height = 32, WrapContents = false };
            dims.Controls.Add(new Label { Text = "Width", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            dims.Controls.Add(width);
            dims.Controls.Add(new Label { Text = "Height", AutoSize = true, Padding = new Padding(16, 6, 0, 0) });
            dims.Controls.Add(height);
            flow.Controls.Add(dims);

            // ── Environment preset ──────────────────────────────────────
            flow.Controls.Add(new Label { Text = "Environment from mission", AutoSize = true });
            foreach (var p in MissionPreset.Load(dataFolder))
                preset.Items.Add(p);
            flow.Controls.Add(preset);

            // ── Starting terrain height ─────────────────────────────────
            flow.Controls.Add(new Label { Text = "Starting terrain height (0–255)", AutoSize = true });
            flow.Controls.Add(startHeight);

            // ── Detail/warning label ────────────────────────────────────
            flow.Controls.Add(detail);

            // ── Footer note + buttons ───────────────────────────────────
            flow.Controls.Add(new Label
            {
                Text = "Creates flat terrain with no objects or roads. Environment settings are saved beside\n" +
                       "the map. To match in-game, use those settings in the mission in levels.txt.",
                AutoSize = true,
            });

            var buttons = new FlowLayoutPanel { Width = 550, Height = 35 };
            var create = new Button { Text = "Create map", AutoSize = true, Enabled = preset.Items.Count > 0 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(create);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(buttons);

            AcceptButton = create;
            CancelButton = cancel;

            Action updateDetail = () =>
            {
                var p = preset.SelectedItem as MissionPreset;
                if (p == null)
                {
                    detail.Text = "No mission templates found. Choose the game's data folder first.";
                    return;
                }

                double lo = MissionPreset.Number(p.Block, "hmin", -120);
                double hi = MissionPreset.Number(p.Block, "hmax",  130);

                string sizeWarning =
                    (width.Value > StockWidth || height.Value > StockHeight)
                        ? "\r\nLarge map: the stock engine is designed around " +
                          StockWidth + " × " + StockHeight + "; larger maps may " +
                          "not load or render correctly in-game."
                        : "";

                detail.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "World height: {0:F1}  (mission range: {1} to {2})\n" +
                    "Water and lighting are copied from the selected mission.{3}",
                    lo + (double)startHeight.Value / 255 * (hi - lo),
                    lo, hi,
                    sizeWarning);
            };

            preset.SelectedIndexChanged += (s, e) => updateDetail();
            startHeight.ValueChanged   += (s, e) => updateDetail();

            // Size preset -> numeric inputs.
            sizePreset.SelectedIndexChanged += (s, e) =>
            {
                switch (sizePreset.SelectedIndex)
                {
                    case 0: width.Value = 32;  height.Value = 256; break;
                    case 1: width.Value = 32;  height.Value = 128; break;
                    // case 2 (Custom…) leaves the numerics alone.
                }
                updateDetail();
            };

            // If the user tweaks a numeric, switch the preset to "Custom…"
            // without re-triggering the preset handler.
            EventHandler toCustom = (s, e) =>
            {
                if (sizePreset.SelectedIndex != 2)
                    sizePreset.SelectedIndex = 2;
            };
            width.ValueChanged  += toCustom;
            height.ValueChanged += toCustom;

            if (preset.Items.Count > 0) preset.SelectedIndex = 0;
            else updateDetail();

            create.Click += (s, e) =>
            {
                try
                {
                    var p = (MissionPreset)preset.SelectedItem;
                    int w = (int)width.Value;
                    int h = (int)height.Value;

                    CreatedMap = Map.CreateBlank(
                        Map.Load(File.ReadAllBytes(p.MapPath)),
                        w, h, (byte)startHeight.Value);
                    EnvironmentBlock = MissionPreset.EnvironmentOnly(p.Block);
                    DialogResult = DialogResult.OK;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not create map");
                }
            };
        }
    }

    public partial class Studio
    {
        private string environmentOverride;

        private bool CreateNewDocument(IWin32Window owner)
        {
            using (var dialog = new NewMapDialog(assets.Root))
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                if (!CanLeave()) return false;
                AdoptNewMap(dialog.CreatedMap, dialog.EnvironmentBlock);
                return true;
            }
        }

        private void AdoptNewMap(Map created, string environment)
        {
            map = created;
            environmentOverride = environment;
            file = "";
            saved = null;
            dirty = true;
            undo.Clear();
            redo.Clear();
            testSelected = -1;
            RefreshAll(true);
            SelectObject(-1, -1, false);
        }
    }
}