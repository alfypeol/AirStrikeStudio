using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Box selection and overlap picking
        // ═════════════════════════════════════════════════════════════════
        // Box selection drags a 2D rectangle in screen space and selects every
        // object whose *center* lands inside it. The center is the center of
        // the object's 3D AABB projected to screen, not any single vertex.
        //
        // The screen-space test uses GlPickRay.TryProject (the inverse of
        // TryBuildRay). It shares the letterbox viewport with the ray side.
        //
        // Overlap picking (Alt+click) opens a context menu listing every
        // object under the cursor, closest first. It uses the same ray as
        // PickAt but records hits per-object instead of returning the first.
        // ═════════════════════════════════════════════════════════════════

        private bool boxDragging;
        private bool boxToggle;
        private Point boxStart, boxEnd;

        private Rectangle BoxRect =>
            Rectangle.FromLTRB(
                Math.Min(boxStart.X, boxEnd.X),
                Math.Min(boxStart.Y, boxEnd.Y),
                Math.Max(boxStart.X, boxEnd.X),
                Math.Max(boxStart.Y, boxEnd.Y));

        /// <summary>
        /// Projects the center of an object's 3D AABB to 2D screen space.
        /// Returns false if the object is off-screen, behind the camera, or
        /// outside the letterboxed viewport.
        /// </summary>
        public bool ObjectScreen(int index, out PointF screen)
        {
            screen = new PointF();

            var bounds = Rect3D.Empty;
            foreach (var pair in sceneryIds)
                if (pair.Value == index)
                    bounds.Union(pair.Key.Bounds);

            if (bounds.IsEmpty) return false;

            var world = new Point3D(
                bounds.X + bounds.SizeX / 2,
                bounds.Y + bounds.SizeY / 2,
                bounds.Z + bounds.SizeZ / 2);

            return GlPickRay.TryProject(
                world, ClientSize.Width, ClientSize.Height,
                CenterX, CenterY, Heading, Pitch, Distance,
                out screen);
        }

        /// <summary>
        /// Selects every object whose projected center falls inside
        /// <paramref name="rectangle"/>. When <paramref name="toggle"/> is
        /// true, XORs each hit; otherwise replaces the group.
        /// </summary>
        public void SelectBox(Rectangle rectangle, bool toggle)
        {
            var ids = toggle ? new HashSet<int>(SelectedObjects) : new HashSet<int>();

            for (int i = 0; i < map.Objects.Count; i++)
            {
                if (!ObjectScreen(i, out PointF at)) continue;
                if (!rectangle.Contains(Point.Round(at))) continue;

                if (!toggle || !ids.Remove(i))
                    ids.Add(i);
            }

            SetObjectSelection(ids.ToArray());
        }

        /// <summary>
        /// Draws the box-selection rectangle in screen space. Sets the 2D
        /// ortho projection, draws the quad, then restores 3D state.
        /// </summary>
        private void DrawSelectionBox()
        {
            if (!boxDragging) return;

            int vw = Math.Min(ClientSize.Width, ClientSize.Height * 4 / 3);
            int vh = vw * 3 / 4;

            // Save 3D state.
            glViewport(0, 0, ClientSize.Width, ClientSize.Height);
            glMatrixMode(GL_PROJECTION);
            glPushMatrix();
            glLoadIdentity();
            glOrtho(0, ClientSize.Width, ClientSize.Height, 0, -1, 1);
            glMatrixMode(GL_MODELVIEW);
            glPushMatrix();
            glLoadIdentity();

            glDisable(GL_DEPTH_TEST);
            glDisable(GL_FOG);
            glDisable(GL_TEXTURE_2D);
            glColor4f(1, 0.8f, 0.1f, 1);

            var r = BoxRect;
            glBegin(GL_QUADS);
            glVertex3f(r.Left,  r.Top,    0);
            glVertex3f(r.Right, r.Top,    0);
            glVertex3f(r.Right, r.Bottom, 0);
            glVertex3f(r.Left,  r.Bottom, 0);
            glEnd();

            // Restore 3D state.
            glPopMatrix();
            glMatrixMode(GL_PROJECTION);
            glPopMatrix();
            glMatrixMode(GL_MODELVIEW);
            glEnable(GL_DEPTH_TEST);
            glViewport((ClientSize.Width - vw) / 2, (ClientSize.Height - vh) / 2, vw, vh);
        }

        /// <summary>
        /// Returns all object indices under the cursor, closest first.
        /// </summary>
        private int[] OverlapAt(int x, int y)
        {
            if (!GlPickRay.TryBuildRay(
                    x, y, ClientSize.Width, ClientSize.Height,
                    CenterX, CenterY, Heading, Pitch, Distance,
                    out Point3D origin, out Vector3D ray))
                return new int[0];

            var hits = new Dictionary<int, double>();

            foreach (var child in scene.Children)
            {
                if (child is not GeometryModel3D g) continue;
                if (!sceneryIds.ContainsKey(g)) continue;

                var mat = g.Material as DiffuseMaterial;
                var brush = mat?.Brush as System.Windows.Media.ImageBrush;

                // Skip translucent meshes (water at opacity < 0.99).
                if (brush != null && brush.Opacity < 0.99) continue;

                if (g.Geometry is not MeshGeometry3D mesh) continue;

                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    double hit = RayTriangle(
                        origin, ray,
                        mesh.Positions[mesh.TriangleIndices[i]],
                        mesh.Positions[mesh.TriangleIndices[i + 1]],
                        mesh.Positions[mesh.TriangleIndices[i + 2]]);

                    if (hit > 1)
                    {
                        int id = sceneryIds[g];
                        if (!hits.ContainsKey(id) || hit < hits[id])
                            hits[id] = hit;
                    }
                }
            }

            return hits.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
        }

        /// <summary>
        /// Opens a context menu at the cursor with every object underneath.
        /// Selecting an entry picks that object (Ctrl extends the group).
        /// </summary>
        private void ChooseOverlap(int x, int y)
        {
            var ids = OverlapAt(x, y);
            if (ids.Length == 0) return;

            var menu = new ContextMenuStrip();
            foreach (int i in ids)
            {
                int index = i; // per-iteration capture
                var item = menu.Items.Add("#" + i + " — " + map.Types[map.Objects[i].Type]);
                item.Click += (s, e) => PickGroup(index, (ModifierKeys & Keys.Control) != 0);
            }

            // ContextMenuStrip isn't auto-disposed after Show(); dispose it on close.
            menu.Closed += (s, e) => BeginInvoke(new Action(() => menu.Dispose()));
            menu.Show(this, new Point(x, y));
        }
    }
}