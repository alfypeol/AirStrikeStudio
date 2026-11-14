using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Object transforms and scene synchronization
        // ═════════════════════════════════════════════════════════════════
        // SyncPlacements is the single entry point for "the map changed;
        // update the scene." It's called after every commit (move, rotate,
        // add, delete, replace, sculpt). Its job is to do the minimum work
        // needed for the given change:
        //
        //   Path A  — terrain heights changed      → RebuildTerrain
        //   Path B  — object count/type/scripted   → ReloadScenery
        //   Path C  — one or more objects moved    → in-place vertex transform
        //
        // Path A is the most expensive (re-tessellates the whole terrain
        // mesh); Path C is the cheapest and most common. The branch order is
        // significant: terrain first, then count/type/script, then positions.
        // Reordering produces redundant work; removing a branch breaks the
        // corresponding edit type.
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Whether moving an object also moves its waypoints.</summary>
        public bool MoveRoutes = true;

        public Func<int, int, int, bool, Map> CommitMove;
        public Func<Map> UndoMove;
        public Func<Map> RedoMove;
        public Func<int, byte, Map> CommitRotation;


        private readonly Dictionary<BitmapSource, uint> textures =
            new Dictionary<BitmapSource, uint>();

        /// <summary>
        /// Terrain height at grid cell (x, y), in world units.
        /// </summary>
        /// <remarks>
        /// Cell (x, y) is centered at sample vertex (x + 0.5, y - 0.5).
        /// The shift reflects how the terrain mesh is built: cell (x, y)'s
        /// four corners are at samples (x, y), (x+1, y), (x, y+1), (x+1, y+1),
        /// so its center is (x + 0.5, y - 0.5) in sample space. Getting the
        /// signs wrong shifts every placement by one cell relative to the
        /// terrain slope.
        /// </remarks>
        private double TerrainZ(int x, int y) => SurfaceZ(x + 0.5, y - 0.5);

        /// <summary>
        /// Moves the selected object (or group) to grid (x, y).
        /// </summary>
        public void MoveSelectedTo(int x, int y)
        {
            if (SelectedObjects.Length > 1)
            {
                MoveGroupTo(x, y);
                return;
            }

            if (CommitMove == null || SelectedIndex < 0) return;

            // FIND THIS LINE AND DELETE/COMMENT IT OUT:
            // if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) return;

            TerrainZ(x, y); // warm the height cache for the new cell

            var current = map.Objects[SelectedIndex];
            if (current.X == x && current.Y == y) return;

            Map updated = CommitMove(SelectedIndex, x, y, MoveRoutes);
            SyncPlacements(updated);
        }

        /// <summary>
        /// Diffs the current scene against <paramref name="updated"/> and
        /// applies the minimum change needed to bring the scene in sync.
        /// </summary>
        public void SyncPlacements(Map updated)
        {
            if (updated == null) return;

            var copy = Map.Load(updated.Bytes());

            // --- ADD THIS BLOCK ---
            // If the map was resized, the cell arrays no longer match in length.
            // Force a full terrain rebuild and return early to avoid index errors.
            if (copy.Width != map.Width || copy.Height != map.Height)
            {
                RebuildTerrain(copy);
                Changed();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            // ----------------------

            // ── Path A: did any cell height change? ─────────────────────
            bool terrainChanged = false;
            for (int i = 0; i < map.Cells.Length; i++)
            // ... rest of the method
            {
                if (map.Cells[i] != copy.Cells[i])
                {
                    terrainChanged = true;
                    break;
                }
            }

            if (terrainChanged)
            {
                RebuildTerrain(copy);
                Changed();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // ── Path B: did the object count, types, or scripts change? ──
            bool reload = copy.Objects.Count != map.Objects.Count;
            if (!reload)
            {
                for (int i = 0; i < copy.Objects.Count; i++)
                {
                    if (copy.Objects[i].Type != map.Objects[i].Type
                        || copy.Objects[i].Script != map.Objects[i].Script)
                    {
                        reload = true;
                        break;
                    }
                }
            }

            if (reload)
            {
                int savedSelection = SelectedIndex;
                ReloadScenery(copy);
                if (savedSelection >= 0 && savedSelection < copy.Objects.Count)
                    SelectedIndex = savedSelection;
            }

            // ── Path C: in-place vertex transforms for moved objects ─────
            // Every part's mesh is transformed directly (baked) rather than
            // wrapped in a WPF transform. This is required because the GL
            // display list path iterates vertex positions, not transforms.
            foreach (var pair in sceneryIds)
            {
                var before = map.Objects[pair.Value];
                var after  = copy.Objects[pair.Value];

                if (before.X == after.X
                    && before.Y == after.Y
                    && before.Setting6 == after.Setting6)
                    continue;

                double dz = ObjectZ(after,  after.X,  after.Y)
                          - ObjectZ(before, before.X, before.Y);

                var mesh = (MeshGeometry3D)pair.Key.Geometry;
                var delta = new Vector3D(
                    (after.X - before.X) * 40,
                    (after.Y - before.Y) * 40,
                    dz);

                double angle = (after.Setting6 - before.Setting6) * Math.PI / 6;
                double c = Math.Cos(angle);
                double s = Math.Sin(angle);

                for (int i = 0; i < mesh.Positions.Count; i++)
                {
                    var v = mesh.Positions[i];
                    double x = v.X - MapWorld.SpawnX(before.X);
                    double y = v.Y - MapWorld.SpawnY(before.Y);
                    mesh.Positions[i] = new Point3D(
                        MapWorld.SpawnX(after.X) + c * x - s * y,
                        MapWorld.SpawnY(after.Y) + s * x + c * y,
                        v.Z + delta.Z);
                }
            }

            map = copy;

            if (glContext != IntPtr.Zero)
            {
                wglMakeCurrent(deviceContext, glContext);
                BuildSceneryList();
            }

            Changed();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Converts a screen position to a grid position via ray-cast, then
        /// moves the selection to it.
        /// </summary>
        private void MoveAt(int x, int y)
        {
            if (GroundAt(x, y, out Point3D hit))
                MoveSelectedTo(MapWorld.SpawnGridX(hit.X), MapWorld.SpawnGridY(hit.Y));
        }

        /// <summary>
        /// Rotates the selected object by <paramref name="steps"/> × 30°.
        /// </summary>
        /// <remarks>
        /// <paramref name="steps"/> may be negative (rotate left).
        /// The <c>((facing + steps) % 12 + 12) % 12</c> wrap handles both
        /// directions and is required: <c>(-1) % 12 == -1</c> in C#,
        /// which would underflow a <c>byte</c> cast if not corrected.
        /// Group rotation is handled by RotateGroup in GlGroups.cs.
        /// </remarks>
        public void RotateSelected(int steps)
        {
            if (SelectedObjects.Length > 1) return;
            if (objectDrag || CommitRotation == null || SelectedIndex < 0) return;

            var p = map.Objects[SelectedIndex];
            byte facing = (byte)(((p.Setting6 + steps) % 12 + 12) % 12);
            if (facing == p.Setting6) return;

            SyncPlacements(CommitRotation(SelectedIndex, facing));
        }

        /// <summary>
        /// Undo/redo hook. Guards against in-progress strokes so the undo
        /// stack can't desync from the scratch buffers.
        /// </summary>
        public void HistoryMove(bool redo)
        {
            if (objectDrag || routeDrag || sculptCells != null || tileDraft != null) return;

            CancelPlacement();
            SetObjectSelection(new int[0]);

            var action = redo ? RedoMove : UndoMove;
            if (action != null)
                SyncPlacements(action());
        }
    }
}