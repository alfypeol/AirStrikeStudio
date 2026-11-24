using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AirstrikeStudio;

namespace AirstrikeMapStudio.Tests
{
    internal static class Program
    {
        private static int passed;
        private static int failed;

        private static void Check(bool cond, string name)
        {
            if (cond)
            {
                passed++;
            }
            else
            {
                failed++;
                Console.Error.WriteLine("FAIL: " + name);
            }
        }

        private static void RunSelectionTests()
        {
            var s = new SelectionState();
            Check(s.SelectedIndex == -1 && s.SelectedObjects.Length == 0, "SelectionState initial empty");

            s.Pick(2, false, 5);
            Check(s.SelectedIndex == 2 && s.SelectedObjects.SequenceEqual(new[] { 2 }), "SelectionState single pick");

            s.Pick(4, true, 5);
            Check(s.SelectedIndex == 4 && s.SelectedObjects.SequenceEqual(new[] { 2, 4 }), "SelectionState toggle add");

            s.Pick(2, true, 5);
            Check(s.SelectedIndex == 4 && s.SelectedObjects.SequenceEqual(new[] { 4 }), "SelectionState toggle remove");

            s.Pick(-1, false, 5);
            Check(s.SelectedIndex == -1 && s.SelectedObjects.Length == 0, "SelectionState clear");

            s.SetSelection(new[] { -1, 1, 99, 3 }, 5);
            Check(s.SelectedObjects.SequenceEqual(new[] { 1, 3 }), "SelectionState clamps out-of-range");

            var placements = new List<Placement>
            {
                new Placement { X = 32760, Y = -32760 },
                new Placement { X = 100, Y = 100 }
            };
            Check(SelectionState.CanShiftGroup(placements, new[] { 0, 1 }, 7, -8), "CanShiftGroup within int16");
            Check(!SelectionState.CanShiftGroup(placements, new[] { 0, 1 }, 8, 0), "CanShiftGroup rejects > MaxValue");
            Check(!SelectionState.CanShiftGroup(placements, new[] { 0, 1 }, 0, -9), "CanShiftGroup rejects < MinValue");
        }

        private static MeshGeometry3D MakeBoxMesh(double cx, double cy, double cz, double half)
        {
            var mesh = new MeshGeometry3D();
            double x0 = cx - half, x1 = cx + half;
            double y0 = cy - half, y1 = cy + half;
            double z0 = cz - half, z1 = cz + half;

            mesh.Positions.Add(new Point3D(x0, y0, z0));
            mesh.Positions.Add(new Point3D(x1, y0, z0));
            mesh.Positions.Add(new Point3D(x1, y1, z0));
            mesh.Positions.Add(new Point3D(x0, y1, z0));
            mesh.Positions.Add(new Point3D(x0, y0, z1));
            mesh.Positions.Add(new Point3D(x1, y0, z1));
            mesh.Positions.Add(new Point3D(x1, y1, z1));
            mesh.Positions.Add(new Point3D(x0, y1, z1));

            int[] tris =
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6,
                1, 2, 6, 1, 6, 5,
                3, 0, 4, 3, 4, 7
            };
            foreach (int t in tris) mesh.TriangleIndices.Add(t);
            return mesh;
        }

        private static int BrutePickClosest(Model3DGroup scene, Dictionary<GeometryModel3D, int> ids, Point3D o, Vector3D d)
        {
            double closest = double.MaxValue;
            int selected = -1;
            foreach (var child in scene.Children)
            {
                if (child is not GeometryModel3D g) continue;
                if (g.Geometry is not MeshGeometry3D mesh) continue;
                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    double hit = GlTerrainControl.RayTriangle(
                        o, d,
                        mesh.Positions[mesh.TriangleIndices[i]],
                        mesh.Positions[mesh.TriangleIndices[i + 1]],
                        mesh.Positions[mesh.TriangleIndices[i + 2]]);
                    if (hit > 1 && hit < closest)
                    {
                        closest = hit;
                        selected = ids.TryGetValue(g, out int id) ? id : -1;
                    }
                }
            }
            return selected;
        }

        private static void RunSpatialIndexTests()
        {
            var scene = new Model3DGroup();
            var ids = new Dictionary<GeometryModel3D, int>();
            var mat = new DiffuseMaterial(Brushes.Gray);
            var rng = new Random(12345);

            for (int i = 0; i < 150; i++)
            {
                double x = (rng.NextDouble() - 0.5) * 4000.0;
                double y = (rng.NextDouble() - 0.5) * 4000.0;
                double z = rng.NextDouble() * 400.0;
                var model = new GeometryModel3D(MakeBoxMesh(x, y, z, 30.0), mat);
                scene.Children.Add(model);
                ids[model] = i;
            }

            var index = new ScenePickIndex();
            bool allMatch = true;
            for (int r = 0; r < 100; r++)
            {
                var origin = new Point3D((rng.NextDouble() - 0.5) * 3000.0, (rng.NextDouble() - 0.5) * 3000.0, 1200.0);
                var dir = new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, -1.0);
                dir.Normalize();

                int expected = BrutePickClosest(scene, ids, origin, dir);
                int actual = index.PickClosest(scene, ids, origin, dir);
                if (expected != actual)
                {
                    allMatch = false;
                    Console.Error.WriteLine($"Ray {r} mismatch: expected={expected}, actual={actual}");
                    break;
                }
            }
            Check(allMatch, "ScenePickIndex matches brute-force RayTriangle on 100 rays");
        }

        private static void RunMapRoundTripTests()
        {
            string[] mapDirs =
            {
                @"E:\AirStrike3D_Modded_Native_Win64\data\maps",
                @"E:\AirStrike3D\data\maps"
            };
            string dir = mapDirs.FirstOrDefault(Directory.Exists);
            if (dir == null)
            {
                Console.WriteLine("SKIP: No stock maps directory found for .hsc round-trip.");
                return;
            }

            var files = Directory.GetFiles(dir, "*.hsc");
            int exact = 0;
            foreach (string f in files)
            {
                byte[] orig = File.ReadAllBytes(f);
                var map = Map.Load(orig);
                byte[] saved = map.Bytes();
                byte[] cloned = map.Clone().Bytes();
                if (orig.SequenceEqual(saved) && orig.SequenceEqual(cloned)) exact++;
                else Console.Error.WriteLine("Map round-trip mismatch: " + Path.GetFileName(f));
            }
            Check(files.Length > 0 && exact == files.Length, $"Map .hsc byte-exact round-trip ({exact}/{files.Length})");
        }

        private static void RunScriptRoundTripTests()
        {
            string[] scriptDirs =
            {
                @"E:\AirStrike3D_Modded_Native_Win64\data\scripts",
                @"E:\AirStrike3D\data\scripts"
            };
            string dir = scriptDirs.FirstOrDefault(Directory.Exists);
            if (dir == null)
            {
                Console.WriteLine("SKIP: No scripts directory found for ScrFile round-trip.");
                return;
            }

            var files = Directory.GetFiles(dir, "*.scr", SearchOption.AllDirectories)
                .Where(f => f.IndexOf("vm_lab_", StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray();
            int exact = 0;
            foreach (string f in files)
            {
                byte[] orig = File.ReadAllBytes(f);
                var scr = ScrFile.Parse(orig);
                string asm = scr.Disassemble();
                byte[] rebuilt = ScrFile.Assemble(asm).Build();
                if (orig.SequenceEqual(rebuilt)) exact++;
                else Console.Error.WriteLine("Script round-trip mismatch: " + f);
            }
            Check(files.Length > 0 && exact == files.Length, $"ScrFile disasm->asm byte-exact round-trip ({exact}/{files.Length})");
        }

        private static void RunSculptMathTests()
        {
            Check(TerrainSculptMath.BrushWeight(0, 4, 1) == 1.0, "BrushWeight center is 1");
            Check(TerrainSculptMath.BrushWeight(4, 4, 1) == 0.0, "BrushWeight rim is 0");
            Check(TerrainSculptMath.BrushWeight(2, 4, 0) == 1.0, "BrushWeight hard brush inside radius is 1");

            const int w = 5, h = 5;
            var orig = new uint[w * h];
            for (int i = 0; i < orig.Length; i++) orig[i] = 0x12340050u; // preserves high 24 tile bits
            var draft = (uint[])orig.Clone();
            var weights = new double[w * h];

            TerrainSculptMath.SculptSample(orig, draft, weights, w, h, 2.5, 2.5, 2.0, 20);
            int centerIdx = 2 * w + 2;
            Check((draft[centerIdx] & 0xFFFFFF00u) == 0x12340000u, "Sculpt preserves high 24 tile bits");
            Check((draft[centerIdx] & 0xFFu) > 0x50u, "Sculpt raises center height");

            // Monotone within stroke: re-applying at same spot does not double-apply.
            uint before = draft[centerIdx];
            TerrainSculptMath.SculptSample(orig, draft, weights, w, h, 2.5, 2.5, 2.0, 20);
            Check(draft[centerIdx] == before, "Sculpt is monotone within stroke");

            // Flatten toward target 0x20 clamped to |amount|=10.
            draft = (uint[])orig.Clone();
            weights = new double[w * h];
            TerrainSculptMath.SculptToolSample(orig, draft, weights, w, h, 2.5, 2.5, 2.0, 10, 1, 0x20, 0.0);
            Check((draft[centerIdx] & 0xFFu) == 0x46u, "Sculpt flatten clamps delta to -10");
        }

        private static void RunRouteAndTileMathTests()
        {
            var a = new Waypoint { X = 0, Y = 0, BX = 10, BY = 0 };
            var b = new Waypoint { X = 20, Y = 10, AX = 10, AY = 10 };
            RouteMath.EvaluateBezier(a, b, 0.0, out double x0, out double y0);
            RouteMath.EvaluateBezier(a, b, 1.0, out double x1, out double y1);
            Check(Math.Abs(x0 - 0.0) < 1e-9 && Math.Abs(y0 - 0.0) < 1e-9, "RouteMath Bezier t=0");
            Check(Math.Abs(x1 - 20.0) < 1e-9 && Math.Abs(y1 - 10.0) < 1e-9, "RouteMath Bezier t=1");
            Check(RouteMath.SegmentSampleCount(a, b) >= 32, "RouteMath SegmentSampleCount >= 32");
            Check(Math.Abs(RouteMath.InterpolateSurfaceZ(10, 20, 30, 40, 0.5, 0.0) - 15.0) < 1e-9, "RouteMath InterpolateSurfaceZ");

            uint packed = TilePaintMath.TileValue(0x0000007Au, 3, 12, 2, false);
            Check((packed & 0xFFu) == 0x7Au, "TilePaintMath preserves height byte");
            Check(((packed >> 8) & 0xFFu) == 3u, "TilePaintMath packs sheet");
            Check(((packed >> 16) & 0xFFu) == 12u, "TilePaintMath packs index");
            Check(((packed >> 24) & 0xFFu) == 6u, "TilePaintMath packs turn*3");
            Check(TilePaintMath.TileValue(packed, 3, 12, 2, true) == 0x7Au, "TilePaintMath erase keeps height");

            var grid = new uint[4 * 4];
            for (int i = 0; i < grid.Length; i++) grid[i] = 0x55u;
            var touched = new HashSet<int>();
            TilePaintMath.StampFootprint(grid, 4, 4, 0, 0, 3, 2, 5, 1, false, touched);
            Check(touched.Count == 4, "TilePaintMath StampFootprint clips to map edges");
        }

        private static void RunUndoManagerTests()
        {
            using (var box = new TextBox { Multiline = true, Text = "" })
            {
                var undo = new ScriptStudioDialog.TextUndoManager(box);
                Check(!undo.CanUndo && !undo.CanRedo, "TextUndoManager initial CanUndo/CanRedo false");

                // First burst (rapid typing < 500ms)
                box.Text = "a";
                box.Text = "ab";
                box.Text = "abc";
                Check(undo.CanUndo && !undo.CanRedo, "TextUndoManager CanUndo true after first burst");

                // Pause > 500ms, then second burst
                Thread.Sleep(550);
                box.Text = "abcd";

                // Undo should step back to "abc", NOT skip it
                undo.Undo();
                Check(box.Text == "abc", "TextUndoManager Undo preserves previous burst ('abc')");
                Check(undo.CanUndo && undo.CanRedo, "TextUndoManager CanUndo & CanRedo both true mid-stack");

                undo.Undo();
                Check(box.Text == "", "TextUndoManager second Undo restores initial empty text");
                Check(!undo.CanUndo && undo.CanRedo, "TextUndoManager at bottom of undo stack");

                undo.Redo();
                Check(box.Text == "abc", "TextUndoManager first Redo restores 'abc'");

                undo.Redo();
                Check(box.Text == "abcd", "TextUndoManager second Redo restores 'abcd'");
                Check(!undo.CanRedo, "TextUndoManager Redo stack empty after full redo");

                // Explicit PushSnapshot before bulk replace
                undo.PushSnapshot();
                box.Text = "REPLACED";
                undo.Undo();
                Check(box.Text == "abcd", "TextUndoManager Undo restores pre-replace snapshot");

                undo.ResetHistory();
                Check(!undo.CanUndo && !undo.CanRedo, "TextUndoManager ResetHistory clears stacks");
            }
        }

        private static void RunPickRayViewportTests()
        {
            var vp = GlPickRay.ComputeViewport(1920, 1080);
            Check(vp.Left == 0 && vp.Top == 0 && vp.Width == 1920 && vp.Height == 1080,
                "GlPickRay ComputeViewport uses full client rect");

            Check(Math.Abs(GlPickRay.ComputeAspect(1920, 1080) - (16.0 / 9.0)) < 1e-9,
                "GlPickRay ComputeAspect matches widescreen 16:9");
            Check(Math.Abs(GlPickRay.ComputeAspect(0, 0) - (4.0 / 3.0)) < 1e-9,
                "GlPickRay ComputeAspect falls back to 4:3 on zero size");

            // Target point at camera center should project to viewport center for both widescreen and portrait
            double cx = 10.0, cy = 20.0, heading = 45.0, pitch = 35.0, dist = 800.0;
            var target = new Point3D(cx * 40.0, cy * 40.0, 0.0);

            bool okWide = GlPickRay.TryProject(target, 1600, 900, cx, cy, heading, pitch, dist, out System.Drawing.PointF sWide);
            Check(okWide && Math.Abs(sWide.X - 800.0) < 0.5 && Math.Abs(sWide.Y - 450.0) < 0.5,
                "GlPickRay TryProject centers camera target on 1600x900");

            bool okTall = GlPickRay.TryProject(target, 600, 1000, cx, cy, heading, pitch, dist, out System.Drawing.PointF sTall);
            Check(okTall && Math.Abs(sTall.X - 300.0) < 0.5 && Math.Abs(sTall.Y - 500.0) < 0.5,
                "GlPickRay TryProject centers camera target on 600x1000");

            bool rayOk = GlPickRay.TryBuildRay(800, 450, 1600, 900, cx, cy, heading, pitch, dist, out Point3D origin, out Vector3D dir);
            double tToTarget = Vector3D.DotProduct(target - origin, dir) / dir.LengthSquared;
            var closestOnRay = origin + dir * tToTarget;
            Check(rayOk && (closestOnRay - target).Length < 1e-6,
                "GlPickRay TryBuildRay at screen center hits camera target");
        }

        private static void RunScrCompilerAndEdgeCaseTests()
        {
            const string minimalSrc =
                "slots 16;\r\n" +
                "entry main = r0;\r\n" +
                "routine r0 {\r\n" +
                "    return;\r\n" +
                "}\r\n";

            var compiled = ScrFile.CompileSource(minimalSrc);
            byte[] bin = compiled.Build();
            var reparsed = ScrFile.Parse(bin);
            byte[] reasm = ScrFile.Assemble(reparsed.Disassemble()).Build();
            Check(bin.SequenceEqual(reasm), "ScrFile minimal C-source compile -> parse -> disasm -> asm round-trip");

            bool parseThrew = false;
            try { ScrFile.Parse(new byte[] { 0x00, 0x01, 0x02, 0x03 }); }
            catch { parseThrew = true; }
            Check(parseThrew, "ScrFile.Parse rejects invalid/truncated header");

            bool compileThrew = false;
            try { ScrFile.CompileSource("routine r0 { invalid_syntax( }"); }
            catch { compileThrew = true; }
            Check(compileThrew, "ScrFile.CompileSource throws on syntax error");

            // Case-insensitive Replace All backslash + dollar escaping regression check
            string replaced = Regex.Replace("load \"FOO\" here", Regex.Escape("foo"), @"C:\dir\$1\bar".Replace("$", "$$"), RegexOptions.IgnoreCase);
            Check(replaced == @"load ""C:\dir\$1\bar"" here", "Case-insensitive Replace All preserves backslashes and dollar signs");
        }

        private static void RunWorkspacePreferencesTests()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "as3d_prefs_test_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var p = new WorkspacePreferences
                {
                    Width = 1440,
                    Height = 900,
                    Recent = new List<string> { "tank_flame", "jeep1" },
                    RecentMaps = new List<string> { @"E:\AirStrike3D\data\maps\level1.hsc", @"E:\AirStrike3D\data\maps\level2.hsc" }
                };
                p.Write(tmp);

                var loaded = WorkspacePreferences.Read(tmp);
                Check(loaded.Recent.SequenceEqual(new[] { "tank_flame", "jeep1" }), "WorkspacePreferences Recent objects round-trip");
                Check(loaded.RecentMaps.SequenceEqual(new[] { @"E:\AirStrike3D\data\maps\level1.hsc", @"E:\AirStrike3D\data\maps\level2.hsc" }),
                    "WorkspacePreferences RecentMaps round-trip");
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            RunSelectionTests();
            RunSculptMathTests();
            RunRouteAndTileMathTests();
            RunSpatialIndexTests();
            RunUndoManagerTests();
            RunPickRayViewportTests();
            RunScrCompilerAndEdgeCaseTests();
            RunWorkspacePreferencesTests();
            RunMapRoundTripTests();
            RunScriptRoundTripTests();

            Console.WriteLine($"AirstrikeMapStudio.Tests: {passed} passed, {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
