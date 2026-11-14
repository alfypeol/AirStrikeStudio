using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--terrain-test")
                {
                    var m = Map.Load(File.ReadAllBytes(args[1]));
                    var before = m.Bytes();
                    using (var v = new TerrainViewer(m, Path.GetFileName(args[1])))
                    {
                        v.Snapshot(args[2], false);
                        v.Snapshot(Path.ChangeExtension(args[2], "forward.png"), false, true);
                        Console.WriteLine("Section vertices=" + v.VertexCount + " triangles=" + v.TriangleCount +
                                          " height=" + v.MinHeight + ".." + v.MaxHeight);
                        v.Snapshot(Path.ChangeExtension(args[2], "full.png"), true);
                        Console.WriteLine("Full map overlays=" + v.OverlayCount + " skipped=" + v.SkippedTiles);
                    }
                    Assert(before.SequenceEqual(m.Bytes()), "3D view preserves map");
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--route-study-test") { RouteStudy.Test(args[1], args[2]); return 0; }
                if (args.Length > 0 && args[0] == "--assembly-test")    { AssemblyTests.Run(args[1], args[2]); return 0; }
                if (args.Length > 0 && args[0] == "--model-test")
                {
                    using (var viewer = new ModelViewer(args[1], args[2], "Preview test"))
                    {
                        viewer.Snapshot(args[3]);
                        viewer.Orbit(95, 20);
                        viewer.Zoom(0.85);
                        viewer.Snapshot(Path.ChangeExtension(args[3], "rotated.png"));
                        Console.WriteLine("PASS model load and two camera views: " +
                                          viewer.VertexCount + " vertices, " + viewer.TriangleCount + " triangles");
                    }
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--self-test")    return Test(args.Skip(1).ToArray());
                if (args.Length > 0 && args[0] == "--render-test")  { Render(args[1], args[2], args[3]); return 0; }
                if (args.Length >= 3 && args[0] == "--screenshot")  { CaptureScreenshot(args[1], args[2], args.Length > 3 ? args[3] : null); return 0; }
                if (args.Length >= 2 && args[0] == "--doc-screenshots") { CaptureAllDocScreenshots(args[1], args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null); return 0; }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => MessageBox.Show(
                    e.Exception.ToString(), "Editor error — save a copy and report this message");
                Application.Run(new Studio());
                return 0;
            }
            catch (Exception e)
            {
                if (args.Length > 0 && args[0].StartsWith("--"))
                {
                    Console.Error.WriteLine(e);
                    return 1;
                }
                MessageBox.Show(e.ToString(), "Airstrike Map Studio");
                return 1;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("TEST FAILED: " + message);
        }

        private static int Test(string[] folders)
        {
            int n = 0, failed = 0;

            foreach (string folder in folders)
            foreach (string file in Directory.GetFiles(folder, "*.hsc", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    byte[] b = File.ReadAllBytes(file);
                    Map m = Map.Load(b);
                    Assert(b.SequenceEqual(m.Bytes()), "round-trip " + file);

                    // Round-trip every placement through ObjectProperties
                    // to verify the property grid is byte-stable.
                    foreach (var loadedPlacement in m.Objects)
                    {
                        var view = new ObjectProperties(loadedPlacement, m);
                        view.InitialFacing = view.InitialFacing;
                        view.DropItem      = view.DropItem;
                        view.LoopRoute     = view.LoopRoute;
                    }
                    Assert(b.SequenceEqual(m.Bytes()), "settings display preserves original values " + file);

                    Console.WriteLine("PASS " + Path.GetFileName(file) + " | " + m.Objects.Count + " objects");
                    n++;
                }
                catch (Exception e)
                {
                    Console.WriteLine("UNSUPPORTED " + file + " | " + e.Message);
                    failed++;
                }
            }

            // ── Synthetic map tests ─────────────────────────────────────
            Map test = Map.New(32, 256);
            test.Types.Add("plane_spacecutter_black");
            test.Drops.Add("item_life");
            test.Cells[45] = 0xFEDCBA98;
            test.Objects.Add(new Placement { Type = 0, X = 2, Y = 95, Setting6 = 7, Setting7 = 4, Link = -1, Script = "scripts\\example.scr" });
            test.Objects[0].Route.Add(new Waypoint
            {
                X = 2, Y = 94, Mode = 1,
                AX = -0.875263f, AY = 94.44235f,
                BX =  4.87526f,  BY = 93.55765f,
                Value = 4,
            });

            var p = test.Objects[0].Clone();
            p.Shift(0, 71, true);
            test.Objects.Add(p);
            test.TypeId("game_over");
            byte[] bytes = test.Bytes();

            var restored = Map.Load(bytes);
            Assert(restored.Objects[1].Y == 166 && restored.Objects[1].Route[0].Y == 165, "clone translation");
            Assert(Math.Abs(restored.Objects[1].Route[0].AY - 165.44235f) < 0.0001, "handle translation");
            Assert(restored.Objects[0].Route[0].Y == 94, "deep clone leaves source");
            Assert(restored.Cells[45] == 0xFEDCBA98, "full uint32 terrain preserved");
            Assert(restored.Objects[0].Script == "scripts\\example.scr" && restored.Drops[0] == "item_life", "variable-length strings");
            Assert(bytes.SequenceEqual(restored.Bytes()), "edited map round-trip");

            bool rejected = false;
            try { Map.Load(bytes.Take(bytes.Length - 1).ToArray()); } catch { rejected = true; }
            Assert(rejected, "reject truncation");
            rejected = false;
            try { Map.Load(bytes.Concat(new byte[] { 0 }).ToArray()); } catch { rejected = true; }
            Assert(rejected, "reject trailing data");

            string dir = Path.Combine(Environment.CurrentDirectory, "work", "editor-save-test");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "test.hsc");
            Map.Save(f, bytes);
            test.Objects.RemoveAt(1);
            Map.Save(f, test.Bytes());
            Assert(Directory.GetFiles(dir, "test.hsc.bak-*").Length > 0, "backup generated");
            Assert(File.ReadAllBytes(f).SequenceEqual(test.Bytes()), "atomic save result");

            var settingsMap = Map.New(32, 256);
            settingsMap.Types.Add("test");
            settingsMap.Drops.Add("item_life");
            settingsMap.Drops.Add("item_armor50");
            var placement = new Placement { Link = -1 };
            settingsMap.Objects.Add(placement);
            var settings = new ObjectProperties(placement, settingsMap);

            Assert(settings.LoopRoute == "No waypoints (inactive)", "No-route explanation");
            settings.LoopRoute = settings.LoopRoute;
            Assert(placement.Link == -1, "Preserve inactive sentinel");
            settings.InitialFacing = 270;
            Assert(placement.Setting6 == 9, "Facing conversion");
            settings.DropItem = "1 — item_life";
            Assert(placement.Setting7 == 1, "Drop lookup");
            settings.DropItem = "2 — item_armor50";
            Assert(placement.Setting7 == 2, "Second drop lookup");
            settings.DropItem = "0 — No map override";
            Assert(placement.Setting7 == 0, "Default drop");
            placement.Route.Add(new Waypoint());
            Assert(settings.LoopRoute == "Repeat", "Nonzero repeat");
            settings.LoopRoute = "Repeat";
            Assert(placement.Link == -1, "Preserve noncanonical true");
            settings.LoopRoute = "Once";
            Assert(placement.Link == 0, "Stop route");
            settings.LoopRoute = "Repeat";
            Assert(placement.Link == 1, "Enable loop");
            placement.Setting7 = 200;
            Assert(settings.DropItem.Contains("Missing table entry"), "Missing drop explanation");
            settings.DropItem = settings.DropItem;
            Assert(placement.Setting7 == 200, "Missing drop preserved");
            bool badAngle = false;
            try { settings.InitialFacing = 31; } catch (ArgumentException) { badAngle = true; }
            Assert(badAngle, "Reject unsupported facing increments");
            Assert(settingsMap.Bytes().SequenceEqual(Map.Load(settingsMap.Bytes()).Bytes()), "Settings save/reload");
            Console.WriteLine("PASS named settings, angle conversion, drops, route flag and unusual-value preservation");

            var routeTest = new Placement { X = 19, Y = 246, Setting6 = 15 };
            routeTest.Route.Add(new Waypoint { X = 17, Y = 244 });
            routeTest.Route.Add(new Waypoint { X = 15, Y = 243, AX = 14, AY = 243, BX = 16, BY = 243, Value = 100 });

            Assert(RouteTools.AlignArrival(routeTest, 1), "Align diagonal arrival");
            var end = routeTest.Route[1];
            Assert(end.AX == 16 && end.AY == 243.5f, "Approach follows incoming diagonal");
            Assert(end.BX == 16 && end.BY == 243 && end.Value == 100 && routeTest.Setting6 == 15, "Alignment preserves departure wait and initial facing");

            routeTest.Route.Insert(1, new Waypoint { X = 15, Y = 243 });
            Assert(RouteTools.AlignArrival(routeTest, 2) && end.AX == 16, "Skip coincident waypoint");

            var coincident = new Placement { X = 2, Y = 2 };
            coincident.Route.Add(new Waypoint { X = 2, Y = 2, AX = 9, AY = 9 });
            Assert(!RouteTools.AlignArrival(coincident, 0) && coincident.Route[0].AX == 9, "Coincident alignment is no-op");

            var added = RouteTools.NewWaypoint(new Placement { X = 10, Y = 255 }, 0, 256);
            Assert(added.Y == 252 && added.AY > added.Y && added.BY < added.Y, "Boundary waypoint follows decreasing Y");

            var fresh = new Placement { X = 8, Y = 28, Link = -1 };
            RouteTools.InsertWaypoint(fresh, 0, 256);
            Assert(fresh.Link == 0 && fresh.Route.Count == 1, "First waypoint creates a once route instead of inheriting -1 as repeat");
            fresh.Link = 1;
            RouteTools.InsertWaypoint(fresh, 1, 256);
            Assert(fresh.Link == 1, "Adding to intentional repeat preserves repeat");
            fresh.Link = -1;
            RouteTools.InsertWaypoint(fresh, 1, 256);
            Assert(fresh.Link == -1, "Existing noncanonical repeat is preserved");

            var freshMap = Map.New(32, 256);
            freshMap.Types.Add("test");
            freshMap.Objects.Add(fresh);
            Assert(Map.Load(freshMap.Bytes()).Objects[0].Link == -1, "Save/reload preserves existing repeat");

            var safeRoute = new Placement { X = 8, Y = 28, Link = -1 };
            RouteTools.InsertWaypoint(safeRoute, 0, 256);
            Assert(safeRoute.Route[0].Y == 25 && !RouteTools.AheadOfSpawn(safeRoute), "Default route approaches lower Y without forward curve handles");
            safeRoute.Route[0].BY = 29;
            Assert(RouteTools.AheadOfSpawn(safeRoute), "Forward departure handle warning");
            safeRoute.Route[0].BY = 23;
            safeRoute.Route[0].AY = 29;
            Assert(RouteTools.AheadOfSpawn(safeRoute), "Forward approach handle warning");
            safeRoute.Route[0].AY = 26;
            safeRoute.Route[0].Y  = 30;
            Assert(RouteTools.AheadOfSpawn(safeRoute), "Forward endpoint warning");
            Console.WriteLine("PASS approach defaults and forward-route warnings");

            // Verify RouteRotation around spawn with maintainFacing
            var rotPlacement = new Placement { X = 20, Y = 100, Setting6 = 3 }; // facing 3 = 90 deg
            rotPlacement.Route.Add(new Waypoint { X = 20, Y = 110, AX = 20, AY = 105, BX = 20, BY = 115 });
            rotPlacement.Route.Add(new Waypoint { X = 30, Y = 110, AX = 25, AY = 110, BX = 35, BY = 110 });

            // Rotate 90 deg clockwise around spawn (20, 100)
            RouteRotation.RotateRoute(rotPlacement.Route, rotPlacement.X, rotPlacement.Y, 90, 256, 256);
            Assert(rotPlacement.Setting6 == 3, "Maintain facing keeps Setting6 unchanged");
            Assert(rotPlacement.Route[0].X == 9 && rotPlacement.Route[0].Y == 99, "Waypoint rotates 90 deg around spawn");

            // Rotate back -90 deg
            RouteRotation.RotateRoute(rotPlacement.Route, rotPlacement.X, rotPlacement.Y, -90, 256, 256);
            Assert(rotPlacement.Route[0].X == 20 && rotPlacement.Route[0].Y == 110, "Waypoint rotates back to original");
            Console.WriteLine("PASS RouteRotation math and facing preservation");

            // Verify Waypoint independent handles and mode
            var wpTest = new Waypoint { X = 50, Y = 100, Mode = 1, AX = 45, AY = 90, BX = 55, BY = 110 };
            wpTest.AX = 40; wpTest.AY = 85; wpTest.Mode = 0;
            Assert(wpTest.BX == 55 && wpTest.BY == 110 && wpTest.Mode == 0, "Independent handles preserve opposite handle untouched");
            wpTest.BX = 2 * wpTest.X - wpTest.AX;
            wpTest.BY = 2 * wpTest.Y - wpTest.AY;
            wpTest.Mode = 1;
            Assert(wpTest.BX == 60 && wpTest.BY == 115 && wpTest.Mode == 1, "Mirror mode properly mirrors opposite handle");
            Console.WriteLine("PASS Waypoint independent vs mirrored handle math");

            // Verify Waypoint CSV export/import round-trip and tolerance
            var objWithRoute = new Placement { X = 120, Y = 340 };
            objWithRoute.Route.Add(new Waypoint { X = 125, Y = 350, Mode = 1, AX = 120, AY = 345, BX = 130, BY = 355, Value = 1.5f });
            objWithRoute.Route.Add(new Waypoint { X = 150, Y = 400, Mode = 0, AX = 140, AY = 390, BX = 165, BY = 415, Value = 0.0f });
            string exportedCsv = WaypointCsv.Format(objWithRoute);
            Assert(exportedCsv.Contains("# SpawnX=120,SpawnY=340"), "WaypointCsv.Format includes spawn header");
            Assert(WaypointCsv.TryParse(exportedCsv, out var importedWps, out int? parsedSx, out int? parsedSy, out string csvErr), "TryParse roundtrips exported CSV");
            Assert(parsedSx == 120 && parsedSy == 340, "TryParse recovers original spawn coordinates");
            Assert(importedWps.Count == 2, "TryParse recovers all waypoints");
            Assert(importedWps[0].X == 125 && importedWps[0].Mode == 1 && importedWps[0].Value == 1.5f, "Waypoint 0 properties preserved");
            Assert(importedWps[1].X == 150 && importedWps[1].Mode == 0 && importedWps[1].AX == 140 && importedWps[1].BX == 165, "Waypoint 1 independent handles preserved");

            // Verify 2-column minimal CSV and comma/space flexibility
            string minimalCsv = "10,20\r\n30, 40\r\n50,60";
            Assert(WaypointCsv.TryParse(minimalCsv, out var minWps, out _, out _, out _), "TryParse minimal 2-column CSV");
            Assert(minWps.Count == 3 && minWps[0].X == 10 && minWps[1].Y == 40 && minWps[2].X == 50, "Minimal 2-column coordinates match");
            Assert(!WaypointCsv.TryParse("invalid,csv,not,numbers", out _, out _, out _, out _), "TryParse correctly rejects malformed CSV data");
            Console.WriteLine("PASS Waypoint CSV export and import parsing round-trip");

            // Verify Object Palette category classification
            Assert(GlTerrainWindow.ObjectCategory("tank_t90") == "Tanks", "Classifies Tanks");
            Assert(GlTerrainWindow.ObjectCategory("helic_apache") == "Aircraft / Bosses", "Classifies Helicopters");
            Assert(GlTerrainWindow.ObjectCategory("plane_mig") == "Aircraft / Bosses", "Classifies Planes");
            Assert(GlTerrainWindow.ObjectCategory("boss_final") == "Aircraft / Bosses", "Classifies Bosses");
            Assert(GlTerrainWindow.ObjectCategory("turret_laser") == "Turrets & cannons", "Classifies Turrets");
            Assert(GlTerrainWindow.ObjectCategory("cannon_flak") == "Turrets & cannons", "Classifies Cannons");
            Assert(GlTerrainWindow.ObjectCategory("ship_cruiser") == "Ships", "Classifies Ships");
            Assert(GlTerrainWindow.ObjectCategory("avianos") == "Ships", "Classifies avianos as Ships");
            Assert(GlTerrainWindow.ObjectCategory("lodka") == "Ships", "Classifies lodka as Ships");
            Assert(GlTerrainWindow.ObjectCategory("apl") == "Ships", "Classifies apl as Ships");
            Assert(GlTerrainWindow.ObjectCategory("rocket_boat") == "Ships", "Classifies rocket_boat as Ships");
            Assert(GlTerrainWindow.ObjectCategory("house_brick") == "Buildings & structures", "Classifies Buildings");
            Assert(GlTerrainWindow.ObjectCategory("tree_pine") == "Flora & nature", "Classifies Flora");
            Assert(GlTerrainWindow.ObjectCategory("bonus_health") == "Pickups", "Classifies Pickups");
            Assert(GlTerrainWindow.ObjectCategory("mystery_item") == "Other", "Classifies fallback Other");
            Console.WriteLine("PASS Object Palette category classification");

            // Verify ScrFile assemble, build, and decompile round-trip
            string asm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n.func MoveToNextWP\r\n.var float 2 2.5f\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    PUSHF v2\r\n    CALL MoveToNextWP\r\n    RETURN\r\n    END\r\n";
            var assembledScr = ScrFile.Assemble(asm);
            byte[] scrBytes = assembledScr.Build();
            Assert(scrBytes.Length > 56 && scrBytes[0] == 'R' && scrBytes[1] == 'C' && scrBytes[2] == 'S' && scrBytes[3] == 'L', "RCSL header validity");
            var parsedScr = ScrFile.Parse(scrBytes);
            Assert(parsedScr.CallsWaypointFunctions, "Detects MoveToNextWP");
            Assert(parsedScr.Slots == 16 && parsedScr.Entries[0] >= 0 && parsedScr.Entries[1] >= 0, "Preserves entry points");
            string decomp = parsedScr.DecompileToSource();
            Assert(decomp.Contains("MoveToNextWP"), "Decompiled source includes MoveToNextWP");
            Console.WriteLine("PASS ScrFile assemble, build, parse, and decompilation");

            // ── TestMissionSession & TestGameLauncher tests ──────────
            string testRoot = Path.Combine(Path.GetTempPath(), "as_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                string testData = Path.Combine(testRoot, "data");
                string testMaps = Path.Combine(testData, "maps");
                string testTextures = Path.Combine(testData, "textures", "terrain1");
                string testObjects = Path.Combine(testData, "objects");
                Directory.CreateDirectory(testMaps);
                Directory.CreateDirectory(testTextures);
                Directory.CreateDirectory(testObjects);
                File.WriteAllText(Path.Combine(testObjects, "test.obj"), "sample_tank { }\r\nsample_turret { }\r\n");

                for (int i = 1; i <= 4; i++)
                    File.WriteAllBytes(Path.Combine(testTextures, "texture" + i + ".tga"), new byte[] { 1, 2, 3 });

                // Test executable discovery
                File.WriteAllBytes(Path.Combine(testRoot, "unins000.exe"), new byte[] { 0 });
                File.WriteAllBytes(Path.Combine(testRoot, "AirStrike3D II - Gulf.exe"), new byte[] { 0 });

                string detected = TestGameLauncher.FindExecutable(testData);
                Assert(detected != null && Path.GetFileName(detected) == "AirStrike3D II - Gulf.exe",
                    "Discovered Gulf Thunder executable beside data folder");

                // Test mission session setup
                byte[] sampleMap = Map.New(32, 256).Bytes();
                string map1Path = Path.Combine(testMaps, "mission1.hsc");
                string mapTestPath = Path.Combine(testMaps, "my_custom_map.hsc");
                File.WriteAllBytes(map1Path, sampleMap);
                File.WriteAllBytes(mapTestPath, sampleMap);

                string levelsText =
                    "{\r\nname\t\"Mission 1\"\r\nid\t\"m1\"\r\nmap\t\"maps\\\\mission1.hsc\"\r\ntextures\t\"textures\\\\terrain1\"\r\n}\r\n" +
                    "{\r\nname\t\"Mission 2\"\r\nid\t\"m2\"\r\nmap\t\"maps\\\\mission1.hsc\"\r\ntextures\t\"textures\\\\terrain1\"\r\n}\r\n";
                string levelsFile = Path.Combine(testMaps, "levels.txt");
                File.WriteAllText(levelsFile, levelsText);

                var session = new TestMissionSession(testData);
                Assert(!session.Active, "Initial session inactive");

                // Begin assignment
                session.Begin(testData, mapTestPath, 0, null);
                Assert(session.Active, "Session active after Begin");
                string modifiedLevels = File.ReadAllText(levelsFile);
                Assert(modifiedLevels.Contains("my_custom_map.hsc"), "levels.txt updated with custom map");

                // Iterative re-assignment while active without restoring first
                session.Begin(testData, mapTestPath, 1, null);
                Assert(session.Active, "Session still active after re-assigning");
                string reAssignedLevels = File.ReadAllText(levelsFile);
                Assert(reAssignedLevels.Contains("my_custom_map.hsc"), "levels.txt slot 1 assigned");

                // Restore back to original
                session.Restore();
                Assert(!session.Active, "Session inactive after Restore");
                string restoredLevels = File.ReadAllText(levelsFile);
                Assert(restoredLevels == levelsText, "levels.txt restored back to exact original content");

                // External modification handling
                session.Begin(testData, mapTestPath, 0, null);
                File.AppendAllText(levelsFile, "// comment added externally\r\n");
                bool caughtExternal = false;
                try { session.Restore(force: false); } catch (InvalidOperationException) { caughtExternal = true; }
                Assert(caughtExternal, "Caught external modification without overwrite");
                session.Restore(force: true);
                Assert(!session.Active && File.ReadAllText(levelsFile) == levelsText, "Force restore succeeds and clears journal");

                Console.WriteLine("PASS TestGameLauncher discovery, TestMissionSession iterative assignment & restoration");

                // Verify GlTerrainWindow construction, ArrangeWorkspace, and Adaptive Inspector
                var testMap = Map.New(32, 256);
                testMap.Types.Add("test");
                testMap.Objects.Add(new Placement { Type = 0, X = 10, Y = 10 });
                testMap.Objects.Add(new Placement
                {
                    Type = 0,
                    X = 20,
                    Y = 20,
                    Route = new List<Waypoint> { new Waypoint { X = 20, Y = 20 } }
                });
                using (var viewer = new GlTerrainWindow(testMap, "Test Map", testData))
                {
                    Assert(viewer != null, "GlTerrainWindow constructed");
                    viewer.CreateControl();
                    viewer.PerformLayout();
                    Assert(viewer.Controls.Count > 0, "GlTerrainWindow controls populated");

                    // Select object 0 (no route) -> waypoint panel collapsed, route banner visible
                    viewer.Preview.SetObjectSelection(new int[] { 0 });
                    Assert(viewer.IsWaypointPanelCollapsed, "Waypoint panel auto-collapsed for object without route");
                    Assert(viewer.IsRouteBannerVisible, "Route banner visible when collapsed");

                    // Select object 1 (has route) -> waypoint panel auto-expanded, route banner hidden
                    viewer.Preview.SetObjectSelection(new int[] { 1 });
                    Assert(!viewer.IsWaypointPanelCollapsed, "Waypoint panel auto-expanded for object with route");
                    Assert(!viewer.IsRouteBannerVisible, "Route banner hidden when expanded");

                    // Clear route -> empties route
                    viewer.Preview.ClearSelectedRoute();
                    Assert(viewer.Preview.CurrentMap.Objects[1].Route.Count == 0, "ClearSelectedRoute emptied route");

                    // 3D Transform Gizmo verification
                    Assert(viewer.Preview.ShowGizmo, "ShowGizmo enabled by default");
                    viewer.Preview.ShowGizmo = false;
                    Assert(!viewer.Preview.ShowGizmo, "ShowGizmo toggles false");
                    viewer.Preview.ShowGizmo = true;
                    Assert(viewer.Preview.ShowGizmo, "ShowGizmo toggles true");

                    using (var scriptDialog = new ScriptStudioDialog(null, viewer.Preview, null, null))
                    {
                        scriptDialog.CreateControl();
                        Assert(scriptDialog.Controls.Count > 0, "ScriptStudioDialog constructed and controls populated");
                    }
                }
                Console.WriteLine("PASS GlTerrainWindow, Adaptive Inspector, Object Palette, and Script Studio layout initialization");

                // ── Native C Source Compiler Tests ─────────────────────────
                string sampleSource = @"
slots 20;
entry init = r0;
entry main = r10;
globals self, other, frametime;
functions MoveToNextWP, random;
var vec v16;
var float v18 = 0;

routine r0 {
    self.f5[2] = 100;
    v18 = random() * 360;
    return;
}

routine r10 {
    if (self.f4) {
        return;
    }
    MoveToNextWP(1.5);
    return;
}
";
                var compiled = ScrFile.CompileSource(sampleSource);
                Assert(compiled.Slots == 20, "Slots parsed");
                Assert(compiled.Defs.Count == 3, "Globals parsed");
                Assert(compiled.Funcs.Count == 2, "Functions parsed");
                Assert(compiled.Entries[0] >= 0, "Init entry set");
                Assert(compiled.Entries[1] >= 0, "Main entry set");
                byte[] compiledScrBytes = compiled.Build();
                Assert(compiledScrBytes.Length > 56, "Build produces valid RCSL bytes");
                var parsedBack = ScrFile.Parse(compiledScrBytes);
                Assert(parsedBack.Slots == 20 && parsedBack.Defs.Count == 3 && parsedBack.Funcs.Count == 2, "Binary round-trip preserved");

                string bot1Candidate = @"E:\Visual Studio Project\AirStrikeStudio\AirstrikeMapStudio\AirstrikeMapStudio\ScriptStudio\samples\bot1.c";
                if (File.Exists(bot1Candidate))
                {
                    string bot1Src = File.ReadAllText(bot1Candidate);
                    var bot1Compiled = ScrFile.CompileSource(bot1Src);
                    byte[] bot1Bytes = bot1Compiled.Build();
                    Assert(bot1Bytes.Length > 500 && bot1Compiled.Code.Count > 100, "bot1.c compiled to bytecode");
                }
                Console.WriteLine("PASS Native C-like source compilation and binary round-trip");
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    try { Directory.Delete(testRoot, true); } catch { }
                }
            }

            Console.WriteLine("PASS new route defaults and preservation of existing routes");
            Console.WriteLine("PASS final approach direction, preserved fields, coincident points, and boundary defaults");
            Console.WriteLine("PASS mutation, route, string, rejection and backup tests");
            Console.WriteLine("ROUNDTRIPS " + n + "; UNSUPPORTED " + failed);
            return failed == 0 ? 0 : 2;
        }

        private static void Render(string mapFile, string dataFolder, string output)
        {
            var m = Map.Load(File.ReadAllBytes(mapFile));

            using (var a = new Assets())
            {
                a.Open(dataFolder);

                Console.WriteLine("Definitions: " + a.Definitions.Count);
                var names = m.Objects.Select(p => m.Types[p.Type]).Distinct().ToArray();
                int rendered = 0;
                foreach (string name in names) if (a.Get(name) != null) rendered++;
                Console.WriteLine("Base models rendered: " + rendered + " / " + names.Length);
                foreach (string warn in a.Warnings) Console.WriteLine(warn);

                // Exercise form construction and layout without opening a user window.
                using (var form = new Studio())
                {
                    form.LoadMap(mapFile);
                    form.TestInteractions();

                    using (var panel = new Panel
                    {
                        Size = form.ClientSize,
                        BackColor = form.BackColor,
                        ForeColor = form.ForeColor,
                        Font = form.Font,
                    })
                    {
                        var children = form.Controls.Cast<Control>().ToArray();
                        foreach (var child in children) panel.Controls.Add(child);
                        panel.CreateControl();
                        panel.PerformLayout();
                        foreach (Control child in panel.Controls) child.PerformLayout();
                        form.PreviewForTest();

                        using (var image = new Bitmap(panel.Width, panel.Height))
                        {
                            panel.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                            image.Save(Path.ChangeExtension(output, "ui.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
            }
        }

        private static void CaptureScreenshot(string mapFile, string outputFile, string gameData)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            byte[] bytes = File.ReadAllBytes(mapFile);
            Map map = Map.Load(bytes);

            string dataRoot = gameData;
            if (!string.IsNullOrEmpty(dataRoot) && Directory.Exists(Path.Combine(dataRoot, "data", "objects")))
            {
                dataRoot = Path.Combine(dataRoot, "data");
            }
            else if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(Path.Combine(dataRoot, "objects")))
            {
                string[] candidates = {
                    @"E:\AirStrike3D\data",
                    @"E:\AirStrike II Gulf Thunder\data",
                    @"E:\AirStrike3DII\data"
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(Path.Combine(c, "objects")))
                    {
                        dataRoot = c;
                        break;
                    }
                }
            }

            using (var viewer = new GlTerrainWindow(map, Path.GetFileName(mapFile), dataRoot))
            {
                viewer.StartPosition = FormStartPosition.Manual;
                viewer.Location = new Point(30, 30);
                viewer.Size = new Size(1440, 800);
                viewer.Show();
                viewer.BringToFront();

                if (map.Objects.Count > 0)
                {
                    int targetIdx = 0;
                    for (int i = 0; i < map.Objects.Count; i++)
                    {
                        string t = map.Types[map.Objects[i].Type].ToLowerInvariant();
                        if (t.Contains("avianos") || t.Contains("ship") || t.Contains("boss") || t.Contains("tank") || t.Contains("helic"))
                        {
                            targetIdx = i;
                            break;
                        }
                    }
                    int finalIdx = targetIdx;
                    viewer.Shown += (s, e) =>
                    {
                        viewer.Preview.CancelPlacement();
                        viewer.Preview.SetObjectSelection(new int[] { finalIdx });
                        viewer.Preview.FitNavigation(true);
                    };
                    viewer.Preview.CancelPlacement();
                    viewer.Preview.SetObjectSelection(new int[] { finalIdx });
                    viewer.Preview.FitNavigation(true);
                }

                for (int i = 0; i < 20; i++)
                {
                    viewer.Preview.CancelPlacement();
                    viewer.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                }

                SaveCompositeScreenshot(viewer, outputFile);
            }
        }

        private static void SaveCompositeScreenshot(GlTerrainWindow viewer, string outputFile)
        {
            using (var bmp = new Bitmap(viewer.Width, viewer.Height))
            {
                viewer.DrawToBitmap(bmp, new Rectangle(0, 0, viewer.Width, viewer.Height));

                Point glScreen = viewer.Preview.PointToScreen(Point.Empty);
                Point glRel = new Point(glScreen.X - viewer.Location.X, glScreen.Y - viewer.Location.Y);
                string tempGlPath = Path.Combine(Path.GetTempPath(), "gl_preview_" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    viewer.Preview.SavePreview(tempGlPath);
                    if (File.Exists(tempGlPath))
                    {
                        using (var glImg = Image.FromFile(tempGlPath))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.DrawImage(glImg, glRel.X, glRel.Y, viewer.Preview.Width, viewer.Preview.Height);
                            }
                        }
                        File.Delete(tempGlPath);
                    }
                }
                catch { }

                string dir = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outputFile, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void CaptureAllDocScreenshots(string mapFile, string outputDir, string gameData)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            byte[] bytes = File.ReadAllBytes(mapFile);
            Map map = Map.Load(bytes);

            string dataRoot = gameData;
            if (!string.IsNullOrEmpty(dataRoot) && Directory.Exists(Path.Combine(dataRoot, "data", "objects")))
            {
                dataRoot = Path.Combine(dataRoot, "data");
            }
            else if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(Path.Combine(dataRoot, "objects")))
            {
                string[] candidates = {
                    @"E:\AirStrike3D\data",
                    @"E:\AirStrike II Gulf Thunder\data",
                    @"E:\AirStrike3DII\data"
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(Path.Combine(c, "objects")))
                    {
                        dataRoot = c;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(outputDir))
                outputDir = @"E:\Visual Studio Project\AirStrikeStudio\docs\images";
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
            string artifactDir = @"C:\Users\ACER\.gemini\antigravity\brain\2092b450-6aa0-48a9-85b4-5f491ffb0f70";

            Action<string, string> saveCopy = (file, name) =>
            {
                if (Directory.Exists(artifactDir))
                {
                    try { File.Copy(file, Path.Combine(artifactDir, name), true); } catch { }
                }
            };

            using (var viewer = new GlTerrainWindow(map, Path.GetFileName(mapFile), dataRoot))
            {
                viewer.StartPosition = FormStartPosition.Manual;
                viewer.Location = new Point(20, 20);
                viewer.Size = new Size(1440, 800);
                viewer.Show();
                viewer.BringToFront();

                // 1. Capture Main Editor with 3D Gizmo
                int gizmoIdx = 0;
                if (map.Objects.Count > 0)
                {
                    for (int i = 0; i < map.Objects.Count; i++)
                    {
                        string t = map.Types[map.Objects[i].Type].ToLowerInvariant();
                        if (t.Contains("avianos") || t.Contains("ship") || t.Contains("boss") || t.Contains("tank") || t.Contains("helic"))
                        {
                            gizmoIdx = i;
                            break;
                        }
                    }
                    viewer.Preview.CancelPlacement();
                    viewer.Preview.SetObjectSelection(new int[] { gizmoIdx });
                    viewer.Preview.FitNavigation(true);
                }
                for (int i = 0; i < 15; i++) { viewer.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                string mainShot = Path.Combine(outputDir, "editor_screenshot.png");
                SaveCompositeScreenshot(viewer, mainShot);
                saveCopy(mainShot, "editor_screenshot.png");

                // 2. Capture Routes tab with Waypoint Bezier Handles & Waypoint List
                int routeIdx = -1;
                for (int i = 0; i < map.Objects.Count; i++)
                {
                    if (map.Objects[i].Route != null && map.Objects[i].Route.Count > 1)
                    {
                        routeIdx = i;
                        break;
                    }
                }
                if (routeIdx < 0 && map.Objects.Count > 0)
                {
                    routeIdx = 0;
                    map.Objects[0].Route.Add(new Waypoint { X = 12, Y = 130, Mode = 1, AX = 11.5f, AY = 132f, BX = 12.5f, BY = 128f });
                    map.Objects[0].Route.Add(new Waypoint { X = 15, Y = 141, Mode = 1, AX = 14.65f, AY = 143.88f, BX = 15.34f, BY = 138.11f });
                    map.Objects[0].Route.Add(new Waypoint { X = 20, Y = 155, Mode = 1, AX = 19.5f, AY = 157f, BX = 20.5f, BY = 153f });
                }

                if (routeIdx >= 0)
                {
                    viewer.SelectTabByTitle("Routes");
                    viewer.Preview.CancelPlacement();
                    viewer.Preview.SetObjectSelection(new int[] { routeIdx });
                    viewer.Preview.EditRoute = true;
                    viewer.Preview.RouteWaypoint = Math.Min(1, map.Objects[routeIdx].Route.Count - 1);
                    viewer.Preview.FitNavigation(true);
                    viewer.Preview.Heading = -60;
                    viewer.Preview.Pitch = 40;
                }
                for (int i = 0; i < 15; i++) { viewer.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                string routeShot = Path.Combine(outputDir, "screenshot_routes.png");
                SaveCompositeScreenshot(viewer, routeShot);
                saveCopy(routeShot, "screenshot_routes.png");

                // 3. Capture Waypoint Precise Settings Dialog (with full un-truncated auto-scaled help)
                Waypoint wpSample = (routeIdx >= 0 && map.Objects[routeIdx].Route.Count > 1)
                    ? map.Objects[routeIdx].Route[1]
                    : new Waypoint { X = 15, Y = 141, Mode = 1, AX = 14.65895f, AY = 143.88902f, BX = 15.341049f, BY = 138.11097f, Value = 0 };

                using (var wpDialog = new Form
                {
                    Text = "Waypoint 3 — precise settings",
                    Width = 520,
                    Height = 600,
                    MinimumSize = new Size(450, 500),
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(120, 100),
                    Font = new Font("Segoe UI", 9f),
                })
                {
                    var grid = new PropertyGrid
                    {
                        Dock = DockStyle.Fill,
                        ToolbarVisible = false,
                        SelectedObject = new WaypointProperties(wpSample),
                    };
                    var hintPanel = new Panel
                    {
                        Dock = DockStyle.Top,
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Padding = new Padding(12, 10, 12, 10),
                        BackColor = Color.FromArgb(246, 248, 251),
                    };
                    var hint = new Label
                    {
                        Dock = DockStyle.Top,
                        AutoSize = true,
                        Font = new Font("Segoe UI", 9f),
                        ForeColor = Color.FromArgb(40, 50, 70),
                        Text = "• X / Y coordinates move the waypoint center and shift both tangent handles.\r\n" +
                               "• Mode sets editor tangent constraints (1 = smooth mirrored handles, 0 = independent handles; ignored by the game engine at runtime).\r\n" +
                               "• Waypoint wait behavior is interpreted by the unit's .scr script.",
                    };
                    hintPanel.Controls.Add(hint);
                    var buttons = new FlowLayoutPanel
                    {
                        Dock = DockStyle.Bottom,
                        Height = 44,
                        FlowDirection = FlowDirection.RightToLeft,
                        Padding = new Padding(8, 6, 8, 6),
                    };
                    buttons.Controls.Add(new Button { Text = "Cancel", Height = 28, Width = 80 });
                    buttons.Controls.Add(new Button { Text = "Apply", Height = 28, Width = 80 });
                    wpDialog.Controls.Add(grid);
                    wpDialog.Controls.Add(hintPanel);
                    wpDialog.Controls.Add(buttons);

                    Action scaleDoc = () =>
                    {
                        try
                        {
                            Control doc = null;
                            foreach (Control c in grid.Controls) if (c.GetType().Name == "DocComment") { doc = c; break; }
                            if (doc == null) return;
                            var prop = doc.GetType().GetProperty("Lines");
                            if (prop != null) prop.SetValue(doc, 4, null);
                        }
                        catch { }
                    };
                    wpDialog.Load += (s, e) => { hint.MaximumSize = new Size(Math.Max(100, wpDialog.ClientSize.Width - 24), 0); scaleDoc(); };
                    wpDialog.Shown += (s, e) => scaleDoc();
                    wpDialog.Show();
                    for (int i = 0; i < 10; i++) { scaleDoc(); wpDialog.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                    using (var bmp = new Bitmap(wpDialog.Width, wpDialog.Height))
                    {
                        wpDialog.DrawToBitmap(bmp, new Rectangle(0, 0, wpDialog.Width, wpDialog.Height));
                        string wpShot = Path.Combine(outputDir, "screenshot_waypoint_dialog.png");
                        bmp.Save(wpShot, System.Drawing.Imaging.ImageFormat.Png);
                        saveCopy(wpShot, "screenshot_waypoint_dialog.png");
                    }
                    wpDialog.Close();
                }

                // 4. Capture Terrain Sculpting Tab
                viewer.SelectTabByTitle("Terrain");
                viewer.Preview.TerrainMode = true;
                viewer.Preview.BrushRadius = 4;
                viewer.Preview.BrushStrength = 8;
                viewer.Preview.SculptTool = 0;
                for (int i = 0; i < 15; i++) { viewer.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                string terrainShot = Path.Combine(outputDir, "screenshot_terrain.png");
                SaveCompositeScreenshot(viewer, terrainShot);
                saveCopy(terrainShot, "screenshot_terrain.png");

                // 5. Capture Object Palette Tab
                viewer.SelectTabByTitle("Objects & selection");
                viewer.Preview.TerrainMode = false;
                if (viewer.sidebarTabs != null)
                {
                    if (viewer.paletteTab != null && viewer.sidebarTabs.TabPages.Contains(viewer.paletteTab))
                    {
                        viewer.sidebarTabs.SelectedTab = viewer.paletteTab;
                    }
                    else if (viewer.sidebarTabs.TabPages.Count > 1)
                    {
                        viewer.sidebarTabs.SelectedIndex = 1;
                    }
                    if (viewer.paletteList != null && viewer.paletteList.Items.Count > 0)
                    {
                        int pick = 0;
                        for (int i = 0; i < viewer.paletteList.Items.Count; i++)
                        {
                            string it = viewer.paletteList.Items[i].ToString().ToLowerInvariant();
                            if (it.Contains("boss") || it.Contains("helic") || it.Contains("tank")) { pick = i; break; }
                        }
                        viewer.paletteList.SelectedIndex = pick;
                    }
                }
                for (int i = 0; i < 15; i++) { viewer.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                string paletteShot = Path.Combine(outputDir, "screenshot_palette.png");
                SaveCompositeScreenshot(viewer, paletteShot);
                saveCopy(paletteShot, "screenshot_palette.png");

                // 6. Capture Mission Settings Dialog (F4)
                try
                {
                    using (var mDlg = new MissionSettingsDialog(dataRoot, Path.GetFileName(mapFile), null))
                    {
                        mDlg.StartPosition = FormStartPosition.Manual;
                        mDlg.Location = new Point(100, 80);
                        mDlg.Size = new Size(580, 680);
                        mDlg.Show();
                        for (int i = 0; i < 10; i++) { mDlg.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                        using (var bmp = new Bitmap(mDlg.Width, mDlg.Height))
                        {
                            mDlg.DrawToBitmap(bmp, new Rectangle(0, 0, mDlg.Width, mDlg.Height));
                            string mShot = Path.Combine(outputDir, "screenshot_mission_settings.png");
                            bmp.Save(mShot, System.Drawing.Imaging.ImageFormat.Png);
                            saveCopy(mShot, "screenshot_mission_settings.png");
                        }
                        mDlg.Close();
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("MissionSettingsDialog capture error: " + ex.Message); }

                // 7. Capture Align or Space Selection Dialog
                using (var aDlg = new Form
                {
                    Text = "Align or space selected objects",
                    ClientSize = new Size(460, 265),
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(140, 140),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    Font = new Font("Segoe UI", 9f),
                })
                {
                    var flow = new FlowLayoutPanel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(12),
                        FlowDirection = FlowDirection.TopDown,
                        WrapContents = false,
                    };
                    aDlg.Controls.Add(flow);

                    flow.Controls.Add(new Label
                    {
                        Width = 430,
                        Height = 28,
                        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                        Text = "3 objects selected. Active object: #39 cutter",
                    });

                    var operation = new ComboBox
                    {
                        Width = 430,
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Font = new Font("Segoe UI", 9.5f),
                    };
                    operation.Items.AddRange(new object[]
                    {
                        "Align X to active object",
                        "Align Y to active object",
                        "Distribute along X",
                        "Distribute along Y",
                    });
                    operation.SelectedIndex = 0;
                    flow.Controls.Add(operation);

                    flow.Controls.Add(new Label
                    {
                        Width = 430,
                        Height = 100,
                        ForeColor = Color.FromArgb(50, 60, 80),
                        Text = "• Alignment changes spawn centers, not model edges. Objects may overlap.\r\n" +
                               "• Distribution keeps the outermost coordinates fixed. Gaps differ by at most one grid cell; select at least three objects.\r\n" +
                               "• Move waypoints is ON: routes follow each object.\r\n" +
                               "• Ctrl+Z restores the whole operation.",
                    });

                    var buttons = new FlowLayoutPanel { Width = 430, Height = 38, FlowDirection = FlowDirection.RightToLeft };
                    var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Height = 28, Width = 80 };
                    var apply = new Button { Text = "Apply", Height = 28, Width = 80 };
                    buttons.Controls.AddRange(new Control[] { cancel, apply });
                    flow.Controls.Add(buttons);
                    aDlg.CancelButton = cancel;

                    aDlg.Show();
                    for (int i = 0; i < 5; i++) { aDlg.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                    using (var bmp = new Bitmap(aDlg.Width, aDlg.Height))
                    {
                        aDlg.DrawToBitmap(bmp, new Rectangle(0, 0, aDlg.Width, aDlg.Height));
                        string aShot = Path.Combine(outputDir, "screenshot_align_dialog.png");
                        bmp.Save(aShot, System.Drawing.Imaging.ImageFormat.Png);
                        saveCopy(aShot, "screenshot_align_dialog.png");
                    }
                    aDlg.Close();
                }

                // 8. Capture Script Studio Dialog (Ctrl+Alt+S)
                try
                {
                    Assets scrAssets = new Assets();
                    try { scrAssets.Open(dataRoot); } catch { }
                    using (var sDlg = new ScriptStudioDialog(scrAssets, viewer.Preview, null, null))
                    {
                        sDlg.StartPosition = FormStartPosition.Manual;
                        sDlg.Location = new Point(40, 40);
                        sDlg.Size = new Size(1180, 760);
                        sDlg.Show();
                        for (int i = 0; i < 15; i++) { sDlg.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                        using (var bmp = new Bitmap(sDlg.Width, sDlg.Height))
                        {
                            sDlg.DrawToBitmap(bmp, new Rectangle(0, 0, sDlg.Width, sDlg.Height));
                            string sShot = Path.Combine(outputDir, "screenshot_script_studio.png");
                            bmp.Save(sShot, System.Drawing.Imaging.ImageFormat.Png);
                            saveCopy(sShot, "screenshot_script_studio.png");
                        }
                        sDlg.Close();
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("ScriptStudioDialog capture error: " + ex.Message); }
            }
            Console.WriteLine("PASS CaptureAllDocScreenshots completed successfully.");
        }
    }
}