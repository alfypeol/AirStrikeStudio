using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Object dragging
        // ═════════════════════════════════════════════════════════════════

        private bool objectDrag;
        private readonly Dictionary<int, uint> dragLists = new Dictionary<int, uint>();
        private Point3D dragAnchor;      // world hit position at drag start
        private Vector3D dragOffset;     // current accumulated delta, world space
        private int dragX, dragY;       // current grid position, valid when dragValid
        private bool dragValid;

        private void BeginObjectDrag(int x, int y)
        {
            int hitIndex = PickAt(x, y);

            if ((ModifierKeys & Keys.Control) != 0)
            {
                PickGroup(hitIndex, toggle: true);
                return;
            }

            if (!SelectedObjects.Contains(hitIndex))
                PickGroup(hitIndex, toggle: false);

            if (SelectedIndex < 0 || CommitMove == null) return;
            if (!GroundAt(x, y, out dragAnchor)) return;

            var selectedMeshes = new HashSet<Model3D>();
            foreach (var pair in sceneryIds)
                if (SelectedObjects.Contains(pair.Value))
                    selectedMeshes.Add(pair.Key);

            if (selectedMeshes.Count == 0) return;

            foreach (int id in SelectedObjects)
            {
                uint list = BuildObjectList(id);
                if (list != 0)
                    dragLists.Add(id, list);
            }
            BuildSceneryList(selectedMeshes);

            objectDrag = true;
            dragOffset = new Vector3D();
            var placement = map.Objects[SelectedIndex];
            dragX = placement.X;
            dragY = placement.Y;
            dragValid = true;
        }

        private void UpdateObjectDrag(int x, int y)
        {
            if (!GroundAt(x, y, out Point3D hit))
            {
                dragValid = false;
                return;
            }

            var p = map.Objects[SelectedIndex];

            int nx, ny;
            if (GridSnap)
            {
                nx = Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)Math.Round(hit.X / 40)));
                ny = Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)Math.Round(hit.Y / 40)));
            }
            else
            {
                nx = Math.Max(short.MinValue, Math.Min(short.MaxValue, p.X + (int)Math.Round((hit.X - dragAnchor.X) / 40)));
                ny = Math.Max(short.MinValue, Math.Min(short.MaxValue, p.Y + (int)Math.Round((hit.Y - dragAnchor.Y) / 40)));
            }

            dragValid = GroupDestination(SelectedObjects, nx - p.X, ny - p.Y);
            if (!dragValid) return;

            dragX = nx;
            dragY = ny;
            dragOffset = new Vector3D(
                (nx - p.X) * 40,
                (ny - p.Y) * 40,
                ObjectZ(p, nx, ny) - ObjectZ(p, p.X, p.Y));
        }

        private void EndObjectDrag(bool commit)
        {
            if (!objectDrag) return;
            objectDrag = false;

            foreach (uint cached in dragLists.Values)
                glDeleteLists(cached, 1);
            dragLists.Clear();

            try
            {
                if (commit && dragValid && moved)
                    MoveSelectedTo(dragX, dragY);
            }
            finally
            {
                dragOffset = new Vector3D();
                BuildSceneryList();
                Changed();
            }
        }

        private Vector3D DragObjectOffset(int index)
        {
            var p = map.Objects[index];
            int dx = (int)Math.Round(dragOffset.X / 40);
            int dy = (int)Math.Round(dragOffset.Y / 40);
            return new Vector3D(
                dragOffset.X,
                dragOffset.Y,
                ObjectZ(p, p.X + dx, p.Y + dy) - ObjectZ(p, p.X, p.Y));
        }

        private void DrawDraggedObject()
        {
            if (!objectDrag) return;

            foreach (var pair in dragLists)
            {
                var offset = DragObjectOffset(pair.Key);
                glPushMatrix();
                glTranslatef((float)offset.X, (float)offset.Y, (float)offset.Z);
                glCallList(pair.Value);
                glPopMatrix();
            }
        }
    }
}