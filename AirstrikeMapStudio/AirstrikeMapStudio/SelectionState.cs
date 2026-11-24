using System.Collections.Generic;
using System.Linq;

namespace AirstrikeStudio
{
    /// <summary>
    /// Plain, UI-free selection state and group-bounds validation extracted
    /// from <see cref="GlTerrainControl"/> so selection rules can be unit-tested.
    /// </summary>
    public sealed class SelectionState
    {
        private readonly HashSet<int> group = new HashSet<int>();

        public int SelectedIndex { get; private set; } = -1;

        public int[] SelectedObjects =>
            group.Count > 0
                ? group.OrderBy(i => i).ToArray()
                : SelectedIndex >= 0
                    ? new[] { SelectedIndex }
                    : new int[0];

        public void SetSelection(IEnumerable<int> indices, int objectCount)
        {
            group.Clear();
            if (indices != null)
            {
                foreach (int i in indices)
                    if (i >= 0 && i < objectCount)
                        group.Add(i);
            }

            SelectedIndex = group.Count == 0 ? -1 : group.Last();
        }

        public void Pick(int index, bool toggle, int objectCount)
        {
            var ids = SelectedObjects.ToList();
            if (toggle)
            {
                if (!ids.Remove(index) && index >= 0 && index < objectCount)
                    ids.Add(index);
            }
            else
            {
                ids.Clear();
                if (index >= 0 && index < objectCount)
                    ids.Add(index);
            }

            SetSelection(ids, objectCount);
        }

        public static bool CanShiftGroup(IReadOnlyList<Placement> objects, IEnumerable<int> ids, int dx, int dy)
        {
            if (objects == null || ids == null) return false;
            foreach (int i in ids)
            {
                if (i < 0 || i >= objects.Count) return false;
                int nx = objects[i].X + dx;
                int ny = objects[i].Y + dy;
                if (nx < short.MinValue || nx > short.MaxValue || ny < short.MinValue || ny > short.MaxValue)
                    return false;
            }
            return true;
        }
    }
}
