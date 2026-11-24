using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    /// <summary>
    /// Pure 2D XY spatial grid and ray-AABB intersection helpers used by
    /// scene picking and terrain ground raycasting.
    /// </summary>
    public sealed class SpatialGrid2D<T>
    {
        private readonly double cellSize;
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        private readonly List<(T Item, Rect3D Bounds)> items = new List<(T, Rect3D)>();

        public SpatialGrid2D(double cellSize = 256.0)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            this.cellSize = cellSize;
        }

        public int Count => items.Count;

        public void Clear()
        {
            cells.Clear();
            items.Clear();
        }

        public void Insert(T item, Rect3D bounds)
        {
            if (bounds.IsEmpty) return;
            int idx = items.Count;
            items.Add((item, bounds));

            int minCx = CellCoord(bounds.X);
            int maxCx = CellCoord(bounds.X + bounds.SizeX);
            int minCy = CellCoord(bounds.Y);
            int maxCy = CellCoord(bounds.Y + bounds.SizeY);

            // Clamp huge meshes (e.g. full terrain) so they don't flood thousands of cells;
            // they are still stored in a fallback bucket or tested via AABB.
            if ((long)(maxCx - minCx + 1) * (maxCy - minCy + 1) > 64)
            {
                AddToCell(long.MaxValue, idx);
                return;
            }

            for (int cx = minCx; cx <= maxCx; cx++)
                for (int cy = minCy; cy <= maxCy; cy++)
                    AddToCell(Pack(cx, cy), idx);
        }

        /// <summary>
        /// Enumerates candidate items whose grid cell (or fallback bucket)
        /// could be intersected by the ray.
        /// </summary>
        public void QueryRay(Point3D origin, Vector3D direction, double maxDist, List<T> results)
        {
            results.Clear();
            if (items.Count == 0) return;

            var seen = new HashSet<int>();

            if (cells.TryGetValue(long.MaxValue, out var large))
            {
                foreach (int idx in large)
                    if (seen.Add(idx))
                        results.Add(items[idx].Item);
            }

            // Compute ray XY span up to a reasonable scene bound (fallback to all cells if huge).
            double span = double.IsInfinity(maxDist) || maxDist > 1e6 ? 1e6 : maxDist;
            double endX = origin.X + direction.X * span;
            double endY = origin.Y + direction.Y * span;

            int x0 = CellCoord(origin.X);
            int y0 = CellCoord(origin.Y);
            int x1 = CellCoord(endX);
            int y1 = CellCoord(endY);

            // Amanatides-Woo 2D grid traversal along the ray in XY.
            int stepX = direction.X > 0 ? 1 : (direction.X < 0 ? -1 : 0);
            int stepY = direction.Y > 0 ? 1 : (direction.Y < 0 ? -1 : 0);

            double tDeltaX = stepX != 0 ? Math.Abs(cellSize / direction.X) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? Math.Abs(cellSize / direction.Y) : double.PositiveInfinity;

            double nextBoundaryX = (x0 + (stepX > 0 ? 1 : 0)) * cellSize;
            double nextBoundaryY = (y0 + (stepY > 0 ? 1 : 0)) * cellSize;

            double tMaxX = stepX != 0 ? (nextBoundaryX - origin.X) / direction.X : double.PositiveInfinity;
            double tMaxY = stepY != 0 ? (nextBoundaryY - origin.Y) / direction.Y : double.PositiveInfinity;

            int cx = x0, cy = y0;
            int maxSteps = Math.Abs(x1 - x0) + Math.Abs(y1 - y0) + 4;
            if (maxSteps > 2048) maxSteps = 2048;

            for (int step = 0; step <= maxSteps; step++)
            {
                if (cells.TryGetValue(Pack(cx, cy), out var bucket))
                {
                    foreach (int idx in bucket)
                        if (seen.Add(idx))
                            results.Add(items[idx].Item);
                }

                if (cx == x1 && cy == y1) break;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX > span) break;
                    cx += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    if (tMaxY > span) break;
                    cy += stepY;
                    tMaxY += tDeltaY;
                }
            }
        }

        private int CellCoord(double v) => (int)Math.Floor(v / cellSize);

        private static long Pack(int x, int y) => ((long)x << 32) ^ (uint)y;

        private void AddToCell(long key, int itemIndex)
        {
            if (!cells.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                cells[key] = list;
            }
            list.Add(itemIndex);
        }
    }

    /// <summary>
    /// Cached spatial index over a WPF <see cref="Model3DGroup"/> scene for
    /// fast <c>PickAt</c> and <c>OverlapAt</c> ray queries.
    /// </summary>
    public sealed class ScenePickIndex
    {
        public sealed class Entry
        {
            public GeometryModel3D Model;
            public MeshGeometry3D Mesh;
            public Rect3D Bounds;
            public int SceneryId;
        }

        private readonly SpatialGrid2D<Entry> grid = new SpatialGrid2D<Entry>(256.0);
        private readonly List<Entry> candidates = new List<Entry>();
        private Model3DGroup cachedScene;
        private int cachedChildCount = -1;
        private int cachedSceneryCount = -1;

        public void Invalidate()
        {
            cachedScene = null;
            cachedChildCount = -1;
            cachedSceneryCount = -1;
            grid.Clear();
        }

        public static bool RayBox(Point3D origin, Vector3D direction, Rect3D bounds, double limit)
        {
            if (bounds.IsEmpty) return false;
            return RayBox(
                origin, direction,
                new Point3D(bounds.X, bounds.Y, bounds.Z),
                new Point3D(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ),
                limit);
        }

        public static bool RayBox(Point3D origin, Vector3D direction, Point3D lo, Point3D hi, double limit)
        {
            double near = 0;
            double far = limit;
            return Slab(origin.X, direction.X, lo.X, hi.X, ref near, ref far)
                && Slab(origin.Y, direction.Y, lo.Y, hi.Y, ref near, ref far)
                && Slab(origin.Z, direction.Z, lo.Z, hi.Z, ref near, ref far);
        }

        private static bool Slab(double origin, double direction, double lo, double hi, ref double near, ref double far)
        {
            const double Epsilon = 1e-8;
            if (Math.Abs(direction) < 1e-12)
                return origin >= lo - Epsilon && origin <= hi + Epsilon;

            double a = (lo - origin) / direction;
            double b = (hi - origin) / direction;
            if (a > b) { double t = a; a = b; b = t; }

            near = Math.Max(near, a - Epsilon);
            far = Math.Min(far, b + Epsilon);
            return near <= far;
        }

        public int PickClosest(
            Model3DGroup scene,
            Dictionary<GeometryModel3D, int> sceneryIds,
            Point3D origin,
            Vector3D ray)
        {
            EnsureBuilt(scene, sceneryIds);
            grid.QueryRay(origin, ray, double.MaxValue, candidates);

            double closest = double.MaxValue;
            int selected = -1;

            foreach (var entry in candidates)
            {
                if (!RayBox(origin, ray, entry.Bounds, closest)) continue;

                var mesh = entry.Mesh;
                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    double hit = GlTerrainControl.RayTriangle(
                        origin, ray,
                        mesh.Positions[mesh.TriangleIndices[i]],
                        mesh.Positions[mesh.TriangleIndices[i + 1]],
                        mesh.Positions[mesh.TriangleIndices[i + 2]]);

                    if (hit > 1 && hit < closest)
                    {
                        closest = hit;
                        selected = entry.SceneryId;
                    }
                }
            }

            return selected;
        }

        public int[] PickOverlap(
            Model3DGroup scene,
            Dictionary<GeometryModel3D, int> sceneryIds,
            Point3D origin,
            Vector3D ray)
        {
            EnsureBuilt(scene, sceneryIds);
            grid.QueryRay(origin, ray, double.MaxValue, candidates);

            var hits = new Dictionary<int, double>();
            foreach (var entry in candidates)
            {
                if (entry.SceneryId < 0) continue;
                if (!RayBox(origin, ray, entry.Bounds, double.MaxValue)) continue;

                var mesh = entry.Mesh;
                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    double hit = GlTerrainControl.RayTriangle(
                        origin, ray,
                        mesh.Positions[mesh.TriangleIndices[i]],
                        mesh.Positions[mesh.TriangleIndices[i + 1]],
                        mesh.Positions[mesh.TriangleIndices[i + 2]]);

                    if (hit > 1)
                    {
                        int id = entry.SceneryId;
                        if (!hits.ContainsKey(id) || hit < hits[id])
                            hits[id] = hit;
                    }
                }
            }

            if (hits.Count == 0) return new int[0];
            var sorted = new List<KeyValuePair<int, double>>(hits);
            sorted.Sort((a, b) => a.Value.CompareTo(b.Value));
            var result = new int[sorted.Count];
            for (int i = 0; i < sorted.Count; i++) result[i] = sorted[i].Key;
            return result;
        }

        private void EnsureBuilt(Model3DGroup scene, Dictionary<GeometryModel3D, int> sceneryIds)
        {
            if (scene == null)
            {
                Invalidate();
                return;
            }

            if (ReferenceEquals(cachedScene, scene)
                && cachedChildCount == scene.Children.Count
                && cachedSceneryCount == sceneryIds.Count)
                return;

            grid.Clear();
            foreach (var child in scene.Children)
            {
                if (child is not GeometryModel3D g) continue;
                if (g.Material is DiffuseMaterial mat && TerrainViewer.IsWater(mat)) continue;
                if (g.Geometry is not MeshGeometry3D mesh) continue;

                var bounds = g.Bounds;
                if (bounds.IsEmpty) bounds = mesh.Bounds;

                int id = sceneryIds.TryGetValue(g, out int foundId) ? foundId : -1;
                grid.Insert(new Entry
                {
                    Model = g,
                    Mesh = mesh,
                    Bounds = bounds,
                    SceneryId = id
                }, bounds);
            }

            cachedScene = scene;
            cachedChildCount = scene.Children.Count;
            cachedSceneryCount = sceneryIds.Count;
        }
    }
}
