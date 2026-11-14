using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace AirstrikeStudio
{
    /// <summary>
    /// Property-grid adapter for a <see cref="Placement"/>. Exposes the
    /// object's editable fields (script, facing, drop, route behavior) plus
    /// read-only diagnostic strings (script source, file check, route
    /// warnings) to the WinForms <see cref="System.Windows.Forms.PropertyGrid"/>.
    /// </summary>
    /// <remarks>
    /// All <c>Description</c> attributes on the public properties are
    /// user-facing help text shown in the PropertyGrid's description pane.
    /// They are preserved verbatim from the original tool; do not reword
    /// without re-verifying against the in-editor help expectations.
    /// </remarks>
    public class ObjectProperties
    {
        private readonly Placement p;
        private readonly Map map;
        private readonly Assets assets;

        public ObjectProperties(Placement obj, Map owner)
            : this(obj, owner, null) { }

        public ObjectProperties(Placement obj, Map owner, Assets loadedAssets)
        {
            p = obj;
            map = owner;
            assets = loadedAssets;
        }

        private Definition Definition
        {
            get
            {
                if (assets != null && assets.Definitions.TryGetValue(map.Types[p.Type], out var d))
                    return d;
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Identity
        // ═════════════════════════════════════════════════════════════════

        [Category("Identity"), DisplayName("Object type"),
         Description("Object prototype name on this map. Use Replace object… to change.")]
        public string ObjectType => map.Types[p.Type];

        [Category("Identity"), DisplayName("Category"),
         Description("Editor category used for filtering in the Object list and Add object dialog.")]
        public string CategoryName => GlTerrainWindow.ObjectCategory(map.Types[p.Type]);

        [Category("Identity"), DisplayName("Definition file (.obj)"),
         Description("The .obj file in the game's objects folder where this definition was found.")]
        public string DefinitionFile =>
            Definition != null && !string.IsNullOrEmpty(Definition.Source)
                ? System.IO.Path.GetFileName(Definition.Source)
                : "Not found in .obj files";

        // ═════════════════════════════════════════════════════════════════
        // Script
        // ═════════════════════════════════════════════════════════════════

        [Category("Script"), DisplayName("Default script"),
         Description("Read from the loaded object definition. " +
                     "Attachments can run additional scripts of their own.")]
        public string DefaultScript
        {
            get
            {
                if (Definition == null) return "Unknown: load the game data folder";
                if (Definition.Script.Length == 0) return "No script in loaded definition";
                return Definition.Script;
            }
        }

        [Category("Script"), DisplayName("Effective script"),
         Description("The map override takes priority over the object's " +
                     "default script. This reports the selected path, not " +
                     "proof that the engine has loaded it.")]
        public string EffectiveScript =>
            p.Script.Length > 0 ? p.Script : DefaultScript;

        [Category("Script"), DisplayName("Script source"),
         Description("Confirmed map selection rule. Replacing an object " +
                     "preserves its override; clear that override to use the " +
                     "replacement object's default script.")]
        public string ScriptSource =>
            p.Script.Length > 0 ? "Map override (retained when replacing object)" : "Object definition";

        [Category("Script"), DisplayName("Script file check"),
         Description("Checks existence within the loaded data folder. It " +
                     "does not validate script behavior or attachment scripts.")]
        public string ScriptFileCheck
        {
            get
            {
                if (assets == null || (Definition == null && p.Script.Length == 0))
                    return "Unknown: load matching game data";

                string path = p.Script.Length > 0 ? p.Script : Definition.Script;
                if (path.Length == 0) return "No script selected";

                try
                {
                    string full = assets.Resolve(path);
                    if (!System.IO.File.Exists(full))
                        return "Missing file in loaded data folder";
                    var scr = ScrFile.Parse(System.IO.File.ReadAllBytes(full));
                    return "Valid RCSL (" + scr.Code.Count + " instructions, " + scr.ActiveEntryRoles() + ")";
                }
                catch
                {
                    return "Path outside data folder or invalid";
                }
            }
        }

        [Category("Script"), DisplayName("Script route support"),
         Description("Parses the RCSL .scr bytecode to check whether the script calls MoveToNextWP / lMoveToNextWP / GetWaypointDelay.")]
        public string ScriptRouteSupport
        {
            get
            {
                if (assets == null || (Definition == null && p.Script.Length == 0))
                    return "Unknown: load matching game data";

                string path = p.Script.Length > 0 ? p.Script : Definition.Script;
                if (path.Length == 0) return "No script selected";

                try
                {
                    string full = assets.Resolve(path);
                    if (!System.IO.File.Exists(full)) return "Missing .scr file";
                    var scr = ScrFile.Parse(System.IO.File.ReadAllBytes(full));
                    if (scr.CallsWaypointFunctions)
                        return "Yes (script calls MoveToNextWP / lMoveToNextWP)";
                    return p.Route.Count > 0
                        ? "Warning: script does NOT call MoveToNextWP (waypoints may be ignored!)"
                        : "No (stationary / non-waypoint script)";
                }
                catch (Exception ex)
                {
                    return "Parse error: " + ex.Message;
                }
            }
        }

        [Category("Script"), DisplayName("Engine functions called"),
         Description("Engine functions imported and invoked by the effective .scr script.")]
        public string ScriptFunctions
        {
            get
            {
                if (assets == null || (Definition == null && p.Script.Length == 0))
                    return "Unknown";

                string path = p.Script.Length > 0 ? p.Script : Definition.Script;
                if (path.Length == 0) return "None";

                try
                {
                    string full = assets.Resolve(path);
                    if (!System.IO.File.Exists(full)) return "Missing .scr file";
                    var scr = ScrFile.Parse(System.IO.File.ReadAllBytes(full));
                    return scr.Funcs.Count > 0 ? string.Join(", ", scr.Funcs) : "None";
                }
                catch
                {
                    return "Invalid .scr";
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Route guidance
        // ═════════════════════════════════════════════════════════════════

        [Category("Route guidance"), DisplayName("Wait timing evidence"),
         Description("Gameplay traces verified seconds, initial waiting at " +
                     "spawn, and skipping the first wait on repeat wrap for " +
                     "the tested rocketlauncher_mobile_lgreen scripts. This " +
                     "inspector does not compare script hashes; modified " +
                     "scripts and other families need their own verification.")]
        public string WaitEvidence
        {
            get
            {
                if (p.Route.Count == 0) return "No map waypoints";
                if (p.Script.Length == 0 && map.Types[p.Type] == "rocketlauncher_mobile_lgreen")
                    return "Confirmed in launcher study; current script not verified";
                return "Unknown for this script / object family";
            }
        }

        [Category("Route guidance"), DisplayName("Initial wait value"),
         Description("First waypoint's stored wait. In the tested launcher, " +
                     "this counts down at spawn after activation, before " +
                     "movement. Other scripts may handle it differently.")]
        public string InitialWait =>
            p.Route.Count == 0
                ? "Not applicable"
                : p.Route[0].Value.ToString(CultureInfo.CurrentCulture);

        [Category("Route guidance"), DisplayName("Attack behavior"),
         Description("Waypoints and wait values do not enable attacks. " +
                     "Firing depends on the effective script, attachment " +
                     "scripts and engine activation. A turret tracking the " +
                     "player does not prove the firing script is active.")]
        public string AttackBehavior => "Controlled by scripts and encounter activation";

        // ═════════════════════════════════════════════════════════════════
        // Position
        // ═════════════════════════════════════════════════════════════════

        [Category("Position"),
         Description("Moving here changes spawn only. Use Translate to move " +
                     "the route with it.")]
        public short X
        {
            get => p.X;
            set => p.X = value;
        }

        [Category("Position")]
        public short Y
        {
            get => p.Y;
            set => p.Y = value;
        }

        // ═════════════════════════════════════════════════════════════════
        // Script override
        // ═════════════════════════════════════════════════════════════════

        [Category("Script"), DisplayName("Script override"),
         TypeConverter(typeof(ScriptChoices)),
         RefreshProperties(RefreshProperties.All),
         Description("Empty uses the object's default script. An override " +
                     "takes priority and is retained when replacing the " +
                     "object. Script code is not edited by this app.")]
        public string ScriptOverride
        {
            get => p.Script;
            set => p.Script = (value == "(Use default script)" ? "" : value) ?? "";
        }

        // ═════════════════════════════════════════════════════════════════
        // Behavior
        // ═════════════════════════════════════════════════════════════════

        [Category("Behavior"), DisplayName("Initial facing (°)"),
         TypeConverter(typeof(FacingChoices)),
         Description("Spawn angle in 30-degree steps. Waypoints or scripts " +
                     "can override this angle. The OpenGL preview shows this " +
                     "initial facing; runtime scripts may change it.")]
        public int InitialFacing
        {
            get => p.Setting6 * 30;
            set
            {
                if (value < 0 || value > 7650 || value % 30 != 0)
                    throw new ArgumentException("Choose an angle in 30-degree steps.");
                p.Setting6 = (byte)(value / 30);
            }
        }

        [Category("Behavior"), DisplayName("Drop item"),
         TypeConverter(typeof(DropChoices)),
         Description("Object released when destroyed. Names come from this " +
                     "map's drop table. No map override retains the object's " +
                     "default behavior; it does not force all drops off.")]
        public string DropItem
        {
            get => DropLabel(p.Setting7);
            set
            {
                if (value == DropItem) return;

                for (int i = 0; i <= Math.Min(255, map.Drops.Count); i++)
                {
                    if (value == DropLabel(i))
                    {
                        p.Setting7 = (byte)i;
                        return;
                    }
                }

                throw new ArgumentException("Choose an entry from this map's drop list.");
            }
        }

        [Category("Behavior"), DisplayName("Loop route"),
         TypeConverter(typeof(RouteChoices)),
         Description("Once finishes the waypoint route; Repeat loops it. " +
                     "This setting does not add waypoints or start movement. " +
                     "With no waypoints it is inactive and the stored value " +
                     "is preserved.")]
        public string LoopRoute
        {
            get
            {
                if (p.Route.Count == 0) return "No waypoints (inactive)";
                return p.Link == 0 ? "Once" : "Repeat";
            }
            set
            {
                if (value == LoopRoute) return;
                if (p.Route.Count == 0)
                    throw new ArgumentException("Add waypoints before choosing route behavior.");

                if (value == "Once")
                {
                    p.Link = 0;
                }
                else if (value == "Repeat")
                {
                    // Only promote 0 → 1. A non-canonical Link (2, 3, ...)
                    // must be preserved, not silently coerced.
                    if (p.Link == 0) p.Link = 1;
                }
                else
                {
                    throw new ArgumentException("Choose Once or Repeat.");
                }
            }
        }

        [Category("Behavior"), DisplayName("Route caution"),
         Description("Enemies can permanently stop shooting after leaving " +
                     "the engine's active encounter area. A route ahead of " +
                     "its spawn Y can cause this immediately. This is a " +
                     "geometric warning, not a full simulation; scripts, " +
                     "speed and camera timing also matter. Existing routes " +
                     "are never changed automatically.")]
        public string RouteCaution =>
            p.Route.Count == 0
                ? "No waypoints"
                : RouteTools.AheadOfSpawn(p)
                    ? "Warning: waypoint/handle ahead of spawn Y"
                    : "OK (route does not exceed spawn Y)";

        // ═════════════════════════════════════════════════════════════════
        // Helpers used by the type converters
        // ═════════════════════════════════════════════════════════════════

        public string[] ScriptOptions()
        {
            var scripts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in map.Objects)
                if (!string.IsNullOrEmpty(obj.Script))
                    scripts.Add(obj.Script);

            if (assets != null)
            {
                foreach (var def in assets.Definitions.Values)
                    if (!string.IsNullOrEmpty(def.Script))
                        scripts.Add(def.Script);

                try
                {
                    string scriptsDir = System.IO.Path.Combine(assets.Root, "scripts");
                    if (System.IO.Directory.Exists(scriptsDir))
                    {
                        foreach (string file in System.IO.Directory.GetFiles(scriptsDir, "*.*", System.IO.SearchOption.AllDirectories))
                        {
                            string rel = file.Substring(assets.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar).Length + 1)
                                             .Replace(System.IO.Path.DirectorySeparatorChar, '/');
                            scripts.Add(rel);
                        }
                    }
                }
                catch { }
            }

            var result = new List<string> { "(Use default script)" };
            result.AddRange(scripts);
            return result.ToArray();
        }

        private string DropLabel(int index)
        {
            if (index == 0) return "0 — No map override";
            return index <= map.Drops.Count
                ? index + " — " + map.Drops[index - 1]
                : index + " — Missing table entry (preserved)";
        }

        public string[] DropOptions()
        {
            var items = new List<string>();
            for (int i = 0; i <= Math.Min(255, map.Drops.Count); i++)
                items.Add(DropLabel(i));

            // If the current value is out of range, keep it visible in the
            // dropdown so the user can see and change it without losing it.
            if (p.Setting7 > map.Drops.Count)
                items.Add(DropItem);

            return items.ToArray();
        }

        public string[] RouteOptions()
        {
            return p.Route.Count == 0
                ? new[] { LoopRoute }
                : new[] { "Once", "Repeat" };
        }
    }

    /// <summary>
    /// Type converter that offers the twelve 30° facing steps plus the
    /// current value if it isn't a standard step. Exclusive: the dropdown
    /// doesn't allow free text entry.
    /// </summary>
    public class FacingChoices : Int32Converter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            var choices = new List<int>();
            for (int i = 0; i < 12; i++)
                choices.Add(i * 30);

            var p = c?.Instance as ObjectProperties;
            if (p != null && !choices.Contains(p.InitialFacing))
                choices.Add(p.InitialFacing);

            return new StandardValuesCollection(choices);
        }
    }

    /// <summary>
    /// Type converter that offers the map's drop-table entries plus a
    /// "No map override" option. Exclusive.
    /// </summary>
    public class DropChoices : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            var p = c?.Instance as ObjectProperties;
            return new StandardValuesCollection(p == null ? new string[0] : p.DropOptions());
        }
    }

    /// <summary>
    /// Type converter that offers "Once" / "Repeat" (or the inactive text
    /// when there are no waypoints). Exclusive.
    /// </summary>
    public class RouteChoices : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            var p = c?.Instance as ObjectProperties;
            return new StandardValuesCollection(p == null ? new string[0] : p.RouteOptions());
        }
    }

    /// <summary>
    /// Type converter that offers all known script paths from definitions,
    /// the map, and the game's scripts folder, while still allowing custom text.
    /// </summary>
    public class ScriptChoices : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => false;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            var p = c?.Instance as ObjectProperties;
            return new StandardValuesCollection(p == null ? new string[0] : p.ScriptOptions());
        }
    }
}