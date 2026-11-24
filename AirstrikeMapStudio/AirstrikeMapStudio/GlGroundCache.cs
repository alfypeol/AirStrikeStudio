using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Terrain raycast acceleration structure
        // ═════════════════════════════════════════════════════════════════
        // GroundAt() needs to find the exact terrain point under the cursor
        // on every mouse move. Testing every triangle in the terrain mesh is
        // 60k+ triangle tests per event; at 30 Hz that's 2M tests/sec, which
        // stalls the UI. Instead, the terrain is chopped into small chunks
        // of triangles with a bounding box each; a ray is first tested
        // against each box (cheap), and only triangles inside boxes it hits
        // are tested.
        //
        // The cache is invalidated when the scene object identity changes
        // (i.e. after a sculpt or map reload). It is NOT invalidated by
        // object movement; terrain geometry never moves after a sculpt.
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// One chunk of the terrain mesh: an array of triangle vertices
        /// (three per triangle) plus the axis-aligned bounding box that
        /// encloses them.
        /// </summary>
        private sealed class GroundChunk
        {
            public Point3D[] Points;
            public Point3D Min;
            public Point3D Max;
        }

        private Model3DGroup groundCacheScene;
        private readonly List<GroundChunk> groundChunks = new List<GroundChunk>();

        /// <summary>
        /// Number of triangle indices per chunk. Must be a multiple of 3 (it
        /// encodes complete triangles). 192 = 64 triangles = 21.33 cells.
        /// Arbitrary but tuned: smaller chunks mean more box tests per ray;
        /// larger chunks mean more triangle tests per box hit.
        /// </summary>
        private const int IndicesPerGroundChunk = 192;

        /// <summary>
        /// Raycasts against the terrain mesh first; if the cursor is outside
        /// the map boundaries (where no terrain triangles exist), falls back
        /// to intersecting the infinite horizontal plane Z = 0 so objects and
        /// waypoints can be dragged and placed freely outside the map.
        /// </summary>
        internal bool GroundOrPlaneAt(int screenX, int screenY, out Point3D hit, double fallbackZ = 0.0)
        {
            if (GroundAt(screenX, screenY, out hit))
                return true;

            hit = new Point3D();
            var vp = ViewportRect(ClientSize.Width, ClientSize.Height);
            if (vp.Width <= 0 || vp.Height <= 0) return false;

            double sx = (screenX - vp.X) * 2.0 / vp.Width - 1.0;
            double sy = 1.0 - (screenY - vp.Y) * 2.0 / vp.Height;

            double a = Heading * Math.PI / 180.0;
            double p = Pitch * Math.PI / 180.0;

            var offset = new Vector3D(
                Math.Cos(a) * Math.Cos(p),
                Math.Sin(a) * Math.Cos(p),
                Math.Sin(p));
            var eye = new Point3D(CenterX * 40.0, CenterY * 40.0, 0.0) + offset * Distance;
            var forward = -offset;
            var right = new Vector3D(-Math.Sin(a), Math.Cos(a), 0.0);
            var up = new Vector3D(
                -Math.Cos(a) * Math.Sin(p),
                -Math.Sin(a) * Math.Sin(p),
                 Math.Cos(p));

            double aspect = vp.Width > 0 && vp.Height > 0 ? (double)vp.Width / vp.Height : AspectRatio43;
            var dir = forward
                    + right * (sx * VerticalHalfFovTan * aspect)
                    + up * (sy * VerticalHalfFovTan);

            if (Math.Abs(dir.Z) < 1e-6) return false;
            double t = (fallbackZ - eye.Z) / dir.Z;
            if (t <= 0 || t > FarPlane * 2) return false;

            hit = eye + dir * t;
            return true;
        }

        /// <summary>
        /// Rebuilds the ground cache if the scene has changed since the last
        /// build. Called lazily by <see cref="GroundAt"/>.
        /// </summary>
        /// <remarks>
        /// Only the heightmap and road-tile meshes go into the cache.
        /// Skybox quads and water planes are excluded because:
        ///   • Skyboxes are not walkable.
        ///   • Water is drawn without depth writes, animates, and sits above
        ///     the terrain; including it makes GroundAt return the water
        ///     surface instead of the ground below, and objects would float.
        /// </remarks>
        private void EnsureGroundCache()
        {
            if (groundCacheScene == scene) return;

            groundChunks.Clear();

            foreach (var child in scene.Children)
            {
                var geometryModel = child as GeometryModel3D;
                if (geometryModel == null) continue;

                // Skip scenery objects (trees, vehicles, etc.).
                if (sceneryIds.ContainsKey(geometryModel)) continue;

                var diffuse = geometryModel.Material as DiffuseMaterial;
                var imageBrush = diffuse?.Brush as System.Windows.Media.ImageBrush;
                if (TerrainViewer.IsWater(diffuse)) continue;

                var mesh = geometryModel.Geometry as MeshGeometry3D;
                if (mesh == null || mesh.Positions.Count <= 4) continue;

                for (int start = 0; start < mesh.TriangleIndices.Count; start += IndicesPerGroundChunk)
                {
                    int count = Math.Min(IndicesPerGroundChunk, mesh.TriangleIndices.Count - start);

                    var chunk = new GroundChunk
                    {
                        Points = new Point3D[count],
                        Min = new Point3D(double.MaxValue, double.MaxValue, double.MaxValue),
                        Max = new Point3D(double.MinValue, double.MinValue, double.MinValue),
                    };

                    for (int i = 0; i < count; i++)
                    {
                        var v = mesh.Positions[mesh.TriangleIndices[start + i]];
                        chunk.Points[i] = v;
                        chunk.Min = new Point3D(
                            Math.Min(chunk.Min.X, v.X),
                            Math.Min(chunk.Min.Y, v.Y),
                            Math.Min(chunk.Min.Z, v.Z));
                        chunk.Max = new Point3D(
                            Math.Max(chunk.Max.X, v.X),
                            Math.Max(chunk.Max.Y, v.Y),
                            Math.Max(chunk.Max.Z, v.Z));
                    }

                    groundChunks.Add(chunk);
                }
            }

            groundCacheScene = scene;
        }

        /// <summary>
        /// Ray-vs-AABB slab test. Returns true if the ray from
        /// <paramref name="origin"/> in direction <paramref name="direction"/>
        /// intersects the box [lo, hi] anywhere within distance
        /// <paramref name="limit"/>.
        /// </summary>
        private static bool GroundBox(Point3D origin, Vector3D direction,
                                      Point3D lo, Point3D hi, double limit)
        {
            return ScenePickIndex.RayBox(origin, direction, lo, hi, limit);
        }
    }
}