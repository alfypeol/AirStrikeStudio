using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// One <c>{ ... }</c> block in <c>maps\levels.txt</c>.
    /// </summary>
    public sealed class MissionEntry
    {
        /// <summary>Character offset of the opening <c>{</c>.</summary>
        public int Start;

        /// <summary>Length of the block, including both braces.</summary>
        public int Length;

        /// <summary>The raw text of the block, braces included.</summary>
        public string Block;

        public override string ToString() =>
            MissionPreset.Quoted(Block, "name") + " [" + MissionPreset.Quoted(Block, "id") + "]";
    }

    /// <summary>
    /// The editable contents of <c>maps\levels.txt</c>. Read once, mutated
    /// in memory, saved with a backup.
    /// </summary>
    /// <remarks>
    /// Format: a flat sequence of <c>{ ... }</c> blocks, one per mission.
    /// The parser handles <c>//</c> line comments, <c>/* */</c> block
    /// comments, and quoted strings. Nested braces throw.
    ///
    /// <c>Save</c> validates that the file on disk hasn't changed since
    /// <c>Reload</c> (by comparing the original bytes), then writes to a
    /// temp file and does an atomic <c>File.Replace</c> with a
    /// <c>.bak-&lt;timestamp&gt;</c> backup.
    /// </remarks>
    public sealed class MissionDocument
    {
        public readonly string PathName;
        public byte[] Original;
        public string Text;

        private Encoding encoding;
        private bool bom;
        public List<MissionEntry> Entries;

        public MissionDocument(string path)
        {
            PathName = path;
            Reload();
        }

        private void Reload()
        {
            Original = File.ReadAllBytes(PathName);
            bom = Original.Length >= 3 && Original[0] == 239 && Original[1] == 187 && Original[2] == 191;
            int skip = bom ? 3 : 0;

            if (Original.Length >= 2
                && ((Original[0] == 255 && Original[1] == 254)
                 || (Original[0] == 254 && Original[1] == 255)))
                throw new InvalidDataException("UTF-16 mission files are not supported.");

            try
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                Text = encoding.GetString(Original, skip, Original.Length - skip);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.GetEncoding(28591);
                Text = encoding.GetString(Original);
            }

            Entries = Parse(Text);
        }

        /// <summary>
        /// Finds every top-level <c>{ ... }</c> block in <paramref name="text"/>,
        /// skipping matches inside <c>//</c> comments, <c>/* */</c> blocks,
        /// and quoted strings.
        /// </summary>
        public static List<MissionEntry> Parse(string text)
        {
            var result = new List<MissionEntry>();
            int start = -1;
            bool quote = false, line = false, comment = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c    = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (line) { if (c == '\n') line = false; continue; }
                if (comment)
                {
                    if (c == '*' && next == '/') { comment = false; i++; }
                    continue;
                }

                // Comments are only recognized outside strings.
                if (!quote && c == '/' && next == '/') { line = true; i++; continue; }
                if (!quote && c == '/' && next == '*') { comment = true; i++; continue; }

                if (c == '"') { quote = !quote; continue; }
                if (quote) continue;

                if (c == '{')
                {
                    if (start >= 0)
                        throw new InvalidDataException("Nested mission blocks are unsupported.");
                    start = i;
                }
                else if (c == '}')
                {
                    if (start < 0)
                        throw new InvalidDataException("Unexpected closing brace.");
                    result.Add(new MissionEntry { Start = start, Length = i - start + 1, Block = text.Substring(start, i - start + 1) });
                    start = -1;
                }
            }

            if (quote || comment || start >= 0)
                throw new InvalidDataException("Incomplete mission block, string or comment.");

            return result;
        }

        /// <summary>
        /// Returns <paramref name="block"/> with one key's value replaced.
        /// </summary>
        /// <remarks>
        /// <paramref name="value"/> semantics:
        ///   null  → delete the key's line entirely
        ///   ""    → emit the key with no value
        ///   other → emit the key and value with a tab separator
        ///
        /// If the key is not found and <paramref name="value"/> is non-null,
        /// the line is inserted just before the block's closing <c>}</c>.
        /// Trailing <c>//</c> comments on the original line are preserved.
        /// </remarks>
        public static string ReplaceField(string block, string key, string value)
        {
            var pattern = new Regex(@"(?m)^([ \t]*)" + Regex.Escape(key)
                                    + @"([ \t]+[^\r\n]*|[ \t]*)(\r?\n|$)");
            var found = pattern.Matches(block);

            if (found.Count > 1)
                throw new InvalidDataException("Duplicate setting: " + key);

            if (found.Count == 0)
            {
                if (value == null) return block;

                int close = block.LastIndexOf('}');
                string nl = block.Contains("\r\n") ? "\r\n" : "\n";
                return block.Insert(close, "\t" + key + (value.Length == 0 ? "" : "\t" + value) + nl);
            }

            var m = found[0];
            if (value == null) return block.Remove(m.Index, m.Length);

            string content = m.Groups[2].Value;

            // Preserve any trailing // comment.
            bool quoted = false;
            int commentAt = -1;
            for (int i = 0; i + 1 < content.Length; i++)
            {
                if (content[i] == '"') quoted = !quoted;
                if (!quoted && content[i] == '/' && content[i + 1] == '/') { commentAt = i; break; }
            }
            string comment = commentAt < 0 ? "" : " " + content.Substring(commentAt);

            string replacement = m.Groups[1].Value
                               + key
                               + (value.Length == 0 ? "" : "\t" + value)
                               + comment
                               + m.Groups[3].Value;

            return block.Substring(0, m.Index) + replacement + block.Substring(m.Index + m.Length);
        }

        /// <summary>
        /// Returns the full file text with one mission's block replaced by the
        /// result of <see cref="MissionSettings.Fields"/> being applied via
        /// <see cref="ReplaceField"/> for each key that differs.
        /// </summary>
        public string Build(int index, MissionSettings settings)
        {
            if (index < 0 || index >= Entries.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var entry   = Entries[index];
            var before  = MissionSettings.Read(entry.Block).Fields();
            var after   = settings.Fields();
            string block = entry.Block;

            foreach (var kv in after)
                if (kv.Value != before[kv.Key])
                    block = ReplaceField(block, kv.Key, kv.Value);

            return Text.Substring(0, entry.Start) + block + Text.Substring(entry.Start + entry.Length);
        }

        public byte[] EncodeText(string text)
        {
            // Validate before encoding so a corrupt text can't reach disk.
            Parse(text);
            byte[] bytes = encoding.GetBytes(text);
            return bom
                ? new byte[] { 239, 187, 191 }.Concat(bytes).ToArray()
                : bytes;
        }

        /// <summary>
        /// Saves one mission's settings back to disk. Returns the backup path,
        /// or null if nothing changed.
        /// </summary>
        public string Save(int index, MissionSettings settings, string dataFolder)
        {
            settings.Validate(dataFolder);

            string next = Build(index, settings);
            if (next == Text) return null;

            if (!File.ReadAllBytes(PathName).SequenceEqual(Original))
                throw new IOException(
                    "levels.txt changed outside this window. " +
                    "Close and reopen Mission settings before saving.");

            Parse(next); // validate the new structure

            byte[] body  = encoding.GetBytes(next);
            byte[] bytes = bom ? new byte[] { 239, 187, 191 }.Concat(body).ToArray() : body;

            string temp   = PathName + ".studio-" + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = PathName + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Replace(temp, PathName, backup);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            Reload();
            return backup;
        }
    }

    /// <summary>
    /// A <see cref="TypeConverter"/> that accepts either <c>.</c> or <c>,</c>
    /// as the decimal separator, and rejects thousands separators.
    /// </summary>
    public sealed class MissionDecimalConverter : DoubleConverter
    {
        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is not string text)
                return base.ConvertFrom(context, culture, value);

            if (!double.TryParse(
                    text.Trim().Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number)
                || double.IsNaN(number) || double.IsInfinity(number))
                throw new FormatException(
                    "Enter a finite decimal number, such as 0.5 or 0,5 " +
                    "(without thousands separators).");

            return number;
        }
    }

    /// <summary>
    /// The per-mission environment settings (map file, terrain textures,
    /// water, fog, sun, ambient). Parsed from a mission block and written
    /// back via <see cref="MissionDocument.Build"/>.
    /// </summary>
    public sealed class MissionSettings
    {
        [Category("1. Map"), DisplayName("Map file"),
         Description("Path relative to the game's data folder. Use Browse map or Use open map.")]
        public string MapFile { get; set; }

        [Category("2. Terrain"), DisplayName("Texture folder")]
        public string TextureFolder { get; set; }

        [Category("2. Terrain"), DisplayName("Minimum height"), TypeConverter(typeof(MissionDecimalConverter))]
        public double MinimumHeight { get; set; }

        [Category("2. Terrain"), DisplayName("Maximum height"), TypeConverter(typeof(MissionDecimalConverter))]
        public double MaximumHeight { get; set; }

        [Category("3. Water"), DisplayName("Water entry present"), ReadOnly(true)]
        public bool WaterEnabled { get; set; }

        [Category("3. Water"), DisplayName("Water texture")]
        public string WaterTexture { get; set; }

        [Category("3. Water"), DisplayName("Water level"),
         Description("Used when a water entry is present. Choose Water texture to add one."),
         TypeConverter(typeof(MissionDecimalConverter))]
        public double WaterHeight { get; set; }

        [Category("3. Water"), DisplayName("Water opacity (0–1)"),
         TypeConverter(typeof(MissionDecimalConverter)),
         Description("0 is transparent; 1 is opaque. Water height remains separate.")]
        public double WaterOpacity
        {
            get
            {
                if (string.IsNullOrWhiteSpace(WaterTail)) return 0.4;
                return double.Parse(Regex.Match(WaterTail, @"^\S+").Value, CultureInfo.InvariantCulture);
            }
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
                    throw new ArgumentException("Water opacity must be between 0 and 1.");
                WaterTail = string.IsNullOrWhiteSpace(WaterTail)
                    ? N(value)
                    : Regex.Replace(WaterTail, @"^\S+", N(value));
            }
        }

        [Category("4. Fog"), DisplayName("Fog entry present"), ReadOnly(true)]
        public bool FogEnabled { get; set; }

        [Category("4. Fog"), DisplayName("Red (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double FogRed { get; set; }

        [Category("4. Fog"), DisplayName("Green (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double FogGreen { get; set; }

        [Category("4. Fog"), DisplayName("Blue (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double FogBlue { get; set; }

        [Category("4. Fog"), DisplayName("Start distance"), TypeConverter(typeof(MissionDecimalConverter))]
        public double FogStart { get; set; }

        [Category("4. Fog"), DisplayName("End distance"), TypeConverter(typeof(MissionDecimalConverter))]
        public double FogEnd { get; set; }

        [Category("5. Lighting"), DisplayName("Sun red (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double SunRed { get; set; }

        [Category("5. Lighting"), DisplayName("Sun green (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double SunGreen { get; set; }

        [Category("5. Lighting"), DisplayName("Sun blue (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double SunBlue { get; set; }

        [Category("5. Lighting"), DisplayName("Sun direction X"), TypeConverter(typeof(MissionDecimalConverter))]
        public double SunX { get; set; }

        [Category("5. Lighting"), DisplayName("Sun direction Y"), TypeConverter(typeof(MissionDecimalConverter))]
        public double SunY { get; set; }

        [Category("5. Lighting"), DisplayName("Sun direction Z"), TypeConverter(typeof(MissionDecimalConverter))]
        public double SunZ { get; set; }

        [Category("5. Lighting"), DisplayName("Ambient red (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double AmbientRed { get; set; }

        [Category("5. Lighting"), DisplayName("Ambient green (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double AmbientGreen { get; set; }

        [Category("5. Lighting"), DisplayName("Ambient blue (0–1)"), TypeConverter(typeof(MissionDecimalConverter))]
        public double AmbientBlue { get; set; }

        [Category("5. Lighting"), DisplayName("Night mode")]
        public bool Night { get; set; }

        /// <summary>
        /// The trailing tokens after <c>water "texture" height</c>. Contains
        /// the opacity and (rarely) other values or a trailing comment.
        /// Not exposed to the property grid; <see cref="WaterOpacity"/>
        /// reads/writes it.
        /// </summary>
        internal string WaterShineTexture = "";
        internal string WaterTail = "0.4";

        private static string Line(string b, string key)
        {
            var m = Regex.Match(b, @"(?m)^[ \t]*" + key + @"(?:[ \t]+([^\r\n]*))?\r?$");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static double[] Numbers(string b, string key, double[] fallback)
        {
            string line = Line(b, key);
            if (line == null) return fallback;

            line = Regex.Replace(line, @"//.*$", "");
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != fallback.Length)
                throw new InvalidDataException("Unsupported " + key + " setting.");

            return parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        }

        public static MissionSettings Read(string block)
        {
            var m = new MissionSettings
            {
                MapFile       = MissionPreset.Quoted(block, "map"),
                TextureFolder = MissionPreset.Quoted(block, "textures"),
                MinimumHeight = MissionPreset.Number(block, "hmin", -120),
                MaximumHeight = MissionPreset.Number(block, "hmax",  130),
                WaterEnabled  = Line(block, "water") != null,
                WaterTexture  = MissionPreset.Quoted(block, "water"),
                Night         = Line(block, "night") != null,
                FogEnabled    = Line(block, "fog")   != null,
            };

            var w = Regex.Match(Line(block, "water") ?? "",
                    "^\"[^\"]+\"\\s+(?:\"([^\"]+)\"\\s+)?([-+0-9.eE]+)(.*)$");
            if (m.WaterEnabled && !w.Success)
                throw new InvalidDataException("Unsupported water setting.");

            if (w.Success)
            {
                m.WaterShineTexture = w.Groups[1].Value;
                m.WaterHeight = double.Parse(w.Groups[2].Value, CultureInfo.InvariantCulture);
                m.WaterTail = Regex.Replace(w.Groups[3].Value, @"//.*$", "").Trim();
            }

            var f = Numbers(block, "fog", new double[] { 0.4, 0.4, 0.3, 650, 900 });
            m.FogRed   = f[0];
            m.FogGreen = f[1];
            m.FogBlue  = f[2];
            m.FogStart = f[3];
            m.FogEnd   = f[4];

            var s = Numbers(block, "sun", new double[] { 0.9, 0.9, 0.9, -1, -0.5, 1, 0.3, 0.3, 0.1 });
            m.SunRed       = s[0];
            m.SunGreen     = s[1];
            m.SunBlue      = s[2];
            m.SunX         = s[3];
            m.SunY         = s[4];
            m.SunZ         = s[5];
            m.AmbientRed   = s[6];
            m.AmbientGreen = s[7];
            m.AmbientBlue  = s[8];

            return m;
        }

        private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Q(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("A valid relative path is required.");
            return "\"" + s.Replace('/', '\\') + "\"";
        }

        public Dictionary<string, string> Fields()
        {
            return new Dictionary<string, string>
            {
                { "map",      Q(MapFile) },
                { "textures", Q(TextureFolder) },
                { "hmin",     N(MinimumHeight) },
                { "hmax",     N(MaximumHeight) },
                { "water",    WaterEnabled
                    ? Q(WaterTexture)
                      + (string.IsNullOrEmpty(WaterShineTexture) ? "" : " " + Q(WaterShineTexture))
                      + " " + N(WaterHeight)
                      + (WaterTail.Length > 0 ? " " + WaterTail : "")
                    : null },
                { "fog",      FogEnabled
                    ? string.Join(" ", new[] { FogRed, FogGreen, FogBlue, FogStart, FogEnd }.Select(N).ToArray())
                    : null },
                { "sun",      string.Join(" ", new[] { SunRed, SunGreen, SunBlue, SunX, SunY, SunZ, AmbientRed, AmbientGreen, AmbientBlue }.Select(N).ToArray()) },
                { "night",    Night ? "" : null },
            };
        }

        public static string FullPath(string dataFolder, string relative)
        {
            Q(relative);
            if (Path.IsPathRooted(relative))
                throw new ArgumentException("Choose a path inside the game's data folder.");

            string root = Path.GetFullPath(dataFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path is outside the game's data folder.");
            return full;
        }

        public static bool IsInsideDataFolder(string dataFolder, string path)
        {
            if (string.IsNullOrEmpty(dataFolder) || string.IsNullOrEmpty(path)) return false;
            try
            {
                string root = Path.GetFullPath(dataFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string RelativePath(string dataFolder, string path)
        {
            string root = Path.GetFullPath(dataFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a file inside the game's data folder.");
            return full.Substring(root.Length);
        }

        public void Validate(string dataFolder)
        {
            Fields();

            foreach (var p in GetType().GetProperties().Where(p => p.PropertyType == typeof(double)))
            {
                double n = (double)p.GetValue(this, null);
                if (double.IsNaN(n) || double.IsInfinity(n))
                    throw new ArgumentException("Settings must be finite numbers.");
            }

            if (WaterEnabled && (WaterOpacity < 0 || WaterOpacity > 1))
                throw new ArgumentException("Water opacity must be between 0 and 1.");

            if (MinimumHeight < -2000 || MaximumHeight > 2000 || MinimumHeight >= MaximumHeight)
                throw new ArgumentException("Height range must increase and stay within -2000 to 2000.");

            foreach (double n in new[]
            {
                FogRed, FogGreen, FogBlue,
                SunRed, SunGreen, SunBlue,
                AmbientRed, AmbientGreen, AmbientBlue,
            })
                if (n < 0 || n > 1)
                    throw new ArgumentException("Colour values must be between 0 and 1.");

            if (FogEnabled && (FogStart < 0 || FogEnd <= FogStart))
                throw new ArgumentException("Fog end must exceed its start distance.");

            if (SunX * SunX + SunY * SunY + SunZ * SunZ == 0)
                throw new ArgumentException("Sun direction cannot be zero.");

            if (WaterEnabled && (WaterHeight < -2000 || WaterHeight > 2000))
                throw new ArgumentException("Water level must be between -2000 and 2000.");

            Map.Load(File.ReadAllBytes(FullPath(dataFolder, MapFile)));

            string folder = FullPath(dataFolder, TextureFolder);
            for (int i = 1; i <= 4; i++)
                if (!File.Exists(Path.Combine(folder, "texture" + i + ".tga")))
                    throw new FileNotFoundException(
                        "Terrain folder must contain texture1.tga through texture4.tga.");

            if (WaterEnabled && !File.Exists(FullPath(dataFolder, WaterTexture)))
                throw new FileNotFoundException("Water texture was not found.");
        }
    }

    /// <summary>
    /// The Mission settings dialog. Dispatches on a ComboBox of mission
    /// entries and prompts to save before switching.
    /// </summary>
    public sealed class MissionSettingsDialog : Form
    {
        private readonly string dataFolder, openMap, openEnvironment;
        private readonly MissionDocument document;

        private readonly ComboBox missions = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };

        private readonly PropertyGrid grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            ToolbarVisible = false,
            PropertySort = PropertySort.Categorized,
            HelpVisible = true,
        };

        private readonly Label status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Padding = new Padding(8),
        };

        private MissionSettings settings;
        private int index = -1;
        private bool selecting;

        public bool SavedChanges;
        public string OpenMapEnvironment;

        public MissionSettingsDialog(string root, string currentMap, string currentEnvironment)
        {
            dataFolder = root;
            openMap    = currentMap;
            openEnvironment = currentEnvironment;
            document   = new MissionDocument(Path.Combine(dataFolder, "maps", "levels.txt"));

            Text = "Mission settings";
            Width = 820;
            Height = 770;
            MinimumSize = new Size(670, 560);
            StartPosition = FormStartPosition.CenterParent;

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 78,
                Padding = new Padding(5),
                AutoSize = true,
            };

            AddAction(actions, "Use open map", () =>
            {
                if (string.IsNullOrEmpty(openMap) || !File.Exists(openMap))
                    throw new InvalidOperationException(
                        "Save the open map first, then reopen Mission settings.");
                settings.MapFile = MissionSettings.RelativePath(dataFolder, openMap);
                grid.Refresh();
            });

            AddAction(actions, "Browse map…", () =>
            {
                using (var d = new OpenFileDialog
                {
                    Filter = "Airstrike maps (*.hsc)|*.hsc",
                    InitialDirectory = Path.Combine(dataFolder, "maps"),
                })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        settings.MapFile = MissionSettings.RelativePath(dataFolder, d.FileName);
                        grid.Refresh();
                    }
            });

            AddAction(actions, "Use open map environment", () =>
            {
                string b = MissionPreset.Resolve(dataFolder, openMap, openEnvironment);
                if (b == null)
                    throw new InvalidOperationException(
                        "The open map has no saved or matching mission environment.");
                var imported = MissionSettings.Read(b);
                imported.MapFile = settings.MapFile;
                settings = imported;
                grid.SelectedObject = settings;
            });

            AddAction(actions, "Terrain folder…", () =>
            {
                using (var d = new FolderBrowserDialog
                {
                    Description = "Choose a terrain texture folder",
                    SelectedPath = Path.Combine(dataFolder, "textures"),
                })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        settings.TextureFolder = MissionSettings.RelativePath(dataFolder, d.SelectedPath);
                        grid.Refresh();
                    }
            });

            AddAction(actions, "Water texture…", () =>
            {
                using (var d = new OpenFileDialog
                {
                    Filter = "TGA textures (*.tga)|*.tga",
                    InitialDirectory = Path.Combine(dataFolder, "gfx", "water"),
                })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        settings.WaterTexture = MissionSettings.RelativePath(dataFolder, d.FileName);
                        settings.WaterEnabled = true;
                        grid.Refresh();
                    }
            });

            AddAction(actions, "Save mission", () => { SaveSelected(); });
            AddAction(actions, "Close", Close);

            Controls.Add(grid);
            Controls.Add(actions);
            Controls.Add(missions);
            Controls.Add(status);

            foreach (var entry in document.Entries)
                missions.Items.Add(entry);

            missions.SelectedIndexChanged += (s, e) =>
            {
                if (selecting) return;

                int next = missions.SelectedIndex;
                if (!LeaveSelected())
                {
                    selecting = true;
                    missions.SelectedIndex = index;
                    selecting = false;
                    return;
                }
                LoadSelected(next);
            };

            FormClosing += (s, e) =>
            {
                if (!LeaveSelected()) e.Cancel = true;
            };

            status.Text =
                "Save mission updates levels.txt with a backup. " +
                "Map edits must be saved separately. " +
                "Reload the mission in the game after changing settings.";

            // Pre-select the mission whose map is the currently-open map.
            int chosen = 0;
            for (int i = 0; i < document.Entries.Count; i++)
            {
                string relative = MissionPreset.Quoted(document.Entries[i].Block, "map");
                try
                {
                    if (openMap.Length > 0
                        && string.Equals(
                            MissionSettings.FullPath(dataFolder, relative),
                            Path.GetFullPath(openMap),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = i;
                        break;
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }

            if (missions.Items.Count == 0)
                throw new InvalidDataException("No mission blocks found.");

            missions.SelectedIndex = chosen;
        }

        private void AddAction(FlowLayoutPanel panel, string label, Action action)
        {
            var b = new Button { Text = label, AutoSize = true };
            b.Click += (s, e) =>
            {
                try
                {
                    Validate();
                    action();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Mission settings");
                }
            };
            panel.Controls.Add(b);
        }

        private void LoadSelected(int selected)
        {
            if (selected < 0) return;

            try
            {
                var candidate = MissionSettings.Read(document.Entries[selected].Block);
                settings = candidate;
                index    = selected;
                grid.SelectedObject = settings;
            }
            catch (Exception ex)
            {
                selecting = true;
                missions.SelectedIndex = index;
                selecting = false;
                MessageBox.Show(this, ex.Message, "Unsupported mission settings");
            }
        }

        private bool Pending()
        {
            if (index < 0 || settings == null) return false;
            try { return document.Build(index, settings) != document.Text; }
            catch { return true; }
        }

        private bool LeaveSelected()
        {
            if (!Pending()) return true;

            var answer = MessageBox.Show(
                this,
                "Save changes to the selected mission?",
                "Unsaved mission settings",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            return answer == DialogResult.No
                || (answer == DialogResult.Yes && SaveSelected());
        }

        public bool SaveSelected()
        {
            try
            {
                Validate();
                string backup = document.Save(index, settings, dataFolder);

                if (backup == null)
                {
                    status.Text = "No mission changes to save.";
                    return true;
                }

                SavedChanges = true;

                if (openMap.Length > 0
                    && string.Equals(
                        MissionSettings.FullPath(dataFolder, settings.MapFile),
                        Path.GetFullPath(openMap),
                        StringComparison.OrdinalIgnoreCase))
                {
                    OpenMapEnvironment = MissionPreset.EnvironmentOnly(document.Entries[index].Block);
                }

                status.Text = "Mission saved. Backup: " + backup
                            + "\r\nReload the mission in the game to use the changes.";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Mission was not saved");
                return false;
            }
        }
    }

    public partial class Studio
    {
        bool EditMissionSettings(IWin32Window owner)
        {
            using (var dialog = new MissionSettingsDialog(assets.Root, file, environmentOverride))
            {
                dialog.ShowDialog(owner);
                if (dialog.OpenMapEnvironment != null)
                    environmentOverride = dialog.OpenMapEnvironment;
                return dialog.SavedChanges;
            }
        }
    }

    public sealed partial class GlTerrainWindow
    {
        public bool MissionSettingsChanged { get; private set; }

        public void ConnectMissionSettings(Func<bool> edit)
        {
            Action trigger = () =>
            {
                try
                {
                    Validate();
                    if (!edit()) return;
                    MissionSettingsChanged = true;
                    NextMapCreated = true;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Mission settings");
                }
            };
            if (menuMissionSettings != null) menuMissionSettings.Click += (s, e) => trigger();

            var b = new Button { Text = "Mission settings…", AutoSize = true };
            b.Click += (s, e) => trigger();
            AddDocumentTool(b);
        }
    }
}