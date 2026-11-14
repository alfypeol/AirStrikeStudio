using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Picking (mouse → object) and camera rays
        // ═════════════════════════════════════════════════════════════════
        // Two picking functions:
        //
        //   PickAt(x, y)    — ray-cast against every triangle in the scene,
        //                     return the object under the cursor.
        //   GroundAt(x, y)  — ray-cast against the terrain only, return the
        //                     world point where the cursor hit the ground.
        //
        // Both use GlPickRay.TryBuildRay for the screen → ray math. The
        // camera basis and letterbox viewport are shared with Render()
        // (GlTerrainWindow.cs) via GlPickRay.BuildCameraBasis; if the
        // render projection ever changes, change it there and here.
        //
        // The 4:3 letterbox is enforced by TryBuildRay: clicks outside the
        // letterboxed viewport return no hit / false.
        // ═════════════════════════════════════════════════════════════════

        private readonly Dictionary<GeometryModel3D, int> sceneryIds =
            new Dictionary<GeometryModel3D, int>();

        private int selectedIndex = -1;
        public int SelectedIndex
        {
            get => selectedIndex;
            private set => selectedIndex = value;
        }

        public event EventHandler SelectionChanged;

        public string[] PlacedObjectNames()
        {
            var names = new string[map.Objects.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = map.Types[map.Objects[i].Type];
            return names;
        }

        public string PlacedObjectPosition(int index)
        {
            var p = map.Objects[index];
            return p.X + ", " + p.Y;
        }
        public void SelectAllOfType(int index)
        {
            if (index < 0 || index >= map.Objects.Count) return;

            ushort targetType = map.Objects[index].Type;
            var ids = new List<int>();

            for (int i = 0; i < map.Objects.Count; i++)
            {
                if (map.Objects[i].Type == targetType)
                    ids.Add(i);
            }

            SetObjectSelection(ids.ToArray());

            // Change Status?.Invoke to a simple message box
            MessageBox.Show("Selected " + ids.Count + " objects of type " + map.Types[targetType], "Selection");
        }
        public void SelectListedObject(int index, bool focus)
        {
            if (index < 0 || index >= map.Objects.Count || objectDrag || routeDrag) return;

            CancelPlacement();
            PickGroup(index, (ModifierKeys & Keys.Control) != 0);
            RouteWaypoint = -1;

            // --- ADD THIS BLOCK ---
            // If the parent Studio form is open, add this object to the Recent list
            if (FindForm() is Studio studio)
            {
                string name = map.Types[map.Objects[index].Type];
                studio.RecentObjects.Remove(name);
                studio.RecentObjects.Insert(0, name);
                if (studio.RecentObjects.Count > 5) studio.RecentObjects.RemoveAt(5);
            }
            // ----------------------

            if (focus)
            {
                CenterX = MapWorld.SpawnX(map.Objects[index].X) / 40;
                CenterY = MapWorld.SpawnY(map.Objects[index].Y) / 40;
            }

            NotifySelection();
        }


        public string SelectionDescription
        {
            get
            {
                if (PlacementPending)
                    return (pendingClone >= 0 ? "Duplicate " : "Place ") + pendingName +
                           "\r\nFacing: " + (PendingFacing * 30) + "° (press R or [ / ] to rotate)" +
                           "\r\nClick terrain to place • Right-click or Esc to deselect / cancel" +
                           "\r\nCopies include their entire route.";
                if (SelectedIndex < 0)
                    return "Click an object or orange marker to select it.\r\n" +
                           "Drag to orbit; right-drag to pan.";
                if (SelectedObjects.Length > 1)
                    return SelectedObjects.Length + " objects selected\r\n" +
                           "Drag a selected object to move the group.\r\n" +
                           "Duplicate and Delete apply to the group.\r\n" +
                           "Move waypoints controls route translation.";

                var p = map.Objects[SelectedIndex];
                return map.Types[p.Type] +
                       "\r\nObject #" + SelectedIndex +
                       " • X " + p.X + ", Y " + p.Y +
                       "\r\nInitial facing: " + p.Setting6 * 30 + "°" +
                       "\r\nWaypoints: " + p.Route.Count +
                       "\r\nEdit settings below. Position fields move spawn only.\r\n" +
                       SelectedVisualNote;
            }
        }

        public void SelectAt(int x, int y)
        {
            PickGroup(PickAt(x, y), (ModifierKeys & Keys.Control) != 0);
        }

        /// <summary>
        /// Ray-casts against every triangle in the scene; returns the index of
        /// the object whose part was hit first, or -1.
        /// </summary>
        /// <remarks>
        /// Terrain meshes will be hit by the ray too, but they aren't in
        /// <see cref="sceneryIds"/>, so the lookup returns false and the
        /// candidate becomes -1. That's how non-object geometry is filtered
        /// out. If a future feature adds "selectable terrain regions," this
        /// lookup is where to add them.
        /// </remarks>
        public int PickAt(int x, int y)
        {
            if (!GlPickRay.TryBuildRay(
                    x, y, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance,
                    out Point3D origin, out Vector3D ray))
                return -1;

            double closest = double.MaxValue;
            int selected = -1;

            foreach (var child in scene.Children)
            {
                if (child is not GeometryModel3D g) continue;
                if (g.Material is DiffuseMaterial mat && TerrainViewer.IsWater(mat)) continue;
                if (g.Geometry is not MeshGeometry3D mesh) continue;

                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    double hit = RayTriangle(
                        origin, ray,
                        mesh.Positions[mesh.TriangleIndices[i]],
                        mesh.Positions[mesh.TriangleIndices[i + 1]],
                        mesh.Positions[mesh.TriangleIndices[i + 2]]);

                    if (hit > 1 && hit < closest)
                    {
                        closest = hit;
                        selected = sceneryIds.TryGetValue(g, out int id) ? id : -1;
                    }
                }
            }

            return selected;
        }

        /// <summary>
        /// Ray-casts against the terrain only; returns the world point where
        /// the cursor hit the ground.
        /// </summary>
        /// <remarks>
        /// Uses the chunked ground cache (<see cref="EnsureGroundCache"/>)
        /// to skip triangle tests against chunks whose AABB the ray misses.
        /// Water is excluded from the cache — a ray that visually hits water
        /// still reports the terrain beneath it.
        /// </remarks>
        /// <summary>
        /// Ray-casts against the terrain only; returns the world point where
        /// the cursor hit the ground. If the cursor misses the terrain, it
        /// falls back to an infinite flat plane at Z=0 to allow off-map placement.
        /// </summary>
        public bool GroundAt(int x, int y, out Point3D result)
        {
            result = new Point3D();

            if (!GlPickRay.TryBuildRay(
                    x, y, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance,
                    out Point3D origin, out Vector3D ray))
                return false;

            double closest = double.MaxValue;
            bool found = false;

            EnsureGroundCache();

            foreach (var chunk in groundChunks)
            {
                if (!GroundBox(origin, ray, chunk.Min, chunk.Max, closest)) continue;

                var points = chunk.Points;
                for (int i = 0; i < points.Length; i += 3)
                {
                    double hit = RayTriangle(origin, ray, points[i], points[i + 1], points[i + 2]);
                    if (hit > 1 && hit < closest)
                    {
                        closest = hit;
                        result = origin + ray * hit;
                        found = true;
                    }
                }
            }

            // Fallback: If we missed the terrain, intersect with an infinite Z=0 plane.
            // This allows dragging objects and placing tiles out into the void.
            if (!found)
            {
                if (Math.Abs(ray.Z) > 1e-5)
                {
                    double t = -origin.Z / ray.Z;
                    if (t > 0)
                    {
                        result = origin + ray * t;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Möller–Trumbore ray-triangle intersection. Returns the hit distance,
        /// or -1 if the ray misses. The <c>1e-9</c> epsilon rejects degenerate
        /// triangles without rejecting grazing hits.
        /// </summary>
        public static double RayTriangle(Point3D o, Vector3D d, Point3D a, Point3D b, Point3D c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var cross = Vector3D.CrossProduct(d, e2);
            double det = Vector3D.DotProduct(e1, cross);
            if (Math.Abs(det) < 1e-9) return -1;

            var t = o - a;
            double u = Vector3D.DotProduct(t, cross) / det;
            if (u < 0 || u > 1) return -1;

            var q = Vector3D.CrossProduct(t, e1);
            double v = Vector3D.DotProduct(d, q) / det;
            if (v < 0 || u + v > 1) return -1;

            return Vector3D.DotProduct(e2, q) / det;
        }

        /// <summary>
        /// Bounding box of all parts of all selected objects, offset by the
        /// current drag when a drag is in progress.
        /// </summary>
        private Rect3D SelectionBounds()
        {
            var bounds = Rect3D.Empty;
            var selected = new HashSet<int>(SelectedObjects);

            foreach (var piece in sceneryIds)
            {
                if (!selected.Contains(piece.Value)) continue;

                var box = piece.Key.Bounds;
                if (objectDrag) box.Offset(DragObjectOffset(piece.Value));
                bounds.Union(box);
            }

            return bounds;
        }

        /// <summary>
        /// Draws a yellow wireframe AABB around the selected object's parts.
        /// Only the first part's box is used, since all parts share the same
        /// object index and their union is the object's full box.
        /// </summary>
        private void DrawSelection()
        {
            if (SelectedIndex < 0) return;

            foreach (var pair in sceneryIds)
            {
                if(!SelectedObjects.Contains(pair.Value)) continue;

                var b = SelectionBounds();
                glDisable(GL_TEXTURE_2D);
                glDisable(GL_FOG);
                glColor4f(1, 0.8f, 0.1f, 1);
                glBegin(GL_LINES);

                for (int axis = 0; axis < 3; axis++)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        double[] lo   = { b.X, b.Y, b.Z };
                        double[] size = { b.SizeX, b.SizeY, b.SizeZ };

                        lo[(axis + 1) % 3] += ((i & 1) != 0 ? size[(axis + 1) % 3] : 0);
                        lo[(axis + 2) % 3] += ((i & 2) != 0 ? size[(axis + 2) % 3] : 0);
                        glVertex3f((float)lo[0], (float)lo[1], (float)lo[2]);

                        lo[axis] += size[axis];
                        glVertex3f((float)lo[0], (float)lo[1], (float)lo[2]);
                    }
                }

                glEnd();
                break; // one box per object
            }
        }
    }
}