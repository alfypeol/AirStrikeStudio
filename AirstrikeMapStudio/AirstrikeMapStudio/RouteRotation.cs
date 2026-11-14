using System;
using System.Collections.Generic;

namespace AirstrikeStudio
{
    public enum RoutePivot
    {
        SpawnPosition,
        FirstWaypoint,
        RouteCenter,
        GroupCenter,
        PrimarySpawn
    }

    /// <summary>
    /// Rotates a route (list of Bézier waypoints) around a pivot point,
    /// keeping each object's spawn and its waypoints aligned.
    /// </summary>
    /// <remarks>
    /// Waypoints and spawns both use integer grid coordinates, but they are
    /// offset by one cell on the Y axis. Spawn grid Y <c>y</c> corresponds to
    /// world Y <c>y*40 - 20</c>; waypoint grid Y <c>y</c> corresponds to
    /// world Y <c>y*40 + 20</c>. To rotate a group of objects and their
    /// routes together, the waypoints must be temporarily shifted onto the
    /// same world-line as the spawns, rotated, then shifted back. The
    /// ±1 dance in <see cref="ApplyAroundSpawns"/> does exactly that.
    ///
    /// Bézier handles are rotated using a per-segment similarity transform
    /// derived from the rotated endpoints. Rotating each handle independently
    /// around its endpoint would produce a different curve, so the two
    /// approach deliberately: ends rotate by the full angle, handles by the
    /// residual angle required to keep them collinear.
    /// </remarks>
    public static class RouteRotation
    {
        /// <summary>
        /// Rotates <paramref name="route"/> around a pivot point given in spawn coordinates.
        /// </summary>
        public static void RotateRoute(
            List<Waypoint> route,
            double pivotX,
            double pivotY,
            double angleDegrees,
            int width,
            int height)
        {
            if (route == null || route.Count == 0) return;
            double rad = angleDegrees * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            Func<double, double, double[]> turn = (x, y) => new[]
            {
                pivotX + (x - pivotX) * cos - (y - pivotY) * sin,
                pivotY + (x - pivotX) * sin + (y - pivotY) * cos
            };

            ApplyAroundSpawns(route, turn, width, height);
        }

        /// <summary>
        /// Rotates <paramref name="route"/> around a pivot, treating
        /// waypoint Y as one higher than spawn Y. Use this when rotating a
        /// group of objects and their routes together.
        /// </summary>
        /// <param name="route">The route to rotate (mutated in place).</param>
        /// <param name="turn">
        /// Rotation function: <c>(x, y) → [x', y']</c>. In practice this
        /// closes over a pivot and a rotation matrix.
        /// </param>
        /// <param name="width">Map width; used to validate rotated points.</param>
        /// <param name="height">Map height; used to validate rotated points.</param>
        public static void ApplyAroundSpawns(
            List<Waypoint> route,
            Func<double, double, double[]> turn,
            int width, int height)
        {
            // Route Y + 1 and spawn Y describe the same world position.
            // Shift onto spawn Y before turning, then shift back.
            Apply(
                route,
                (x, y) => { var at = turn(x, y + 1); return new[] { at[0], at[1] - 1 }; },
                width, height,
                y => (int)Math.Round(y + 1) - 1);
        }

        /// <summary>
        /// Rotates <paramref name="route"/> in place using <paramref name="turn"/>
        /// directly. Use this only when the caller has already accounted for
        /// the spawn/waypoint Y offset (e.g., when rotating in world coordinates).
        /// </summary>
        public static void Apply(
            List<Waypoint> route,
            Func<double, double, double[]> turn,
            int width, int height)
        {
            Apply(route, turn, width, height, y => (int)Math.Round(y));
        }

        /// <summary>
        /// Shared implementation. <paramref name="roundY"/> controls how the
        /// rotated Y coordinate is snapped to a grid cell. The default
        /// (used by <see cref="Apply"/>) rounds directly; the offset overload
        /// (used by <see cref="ApplyAroundSpawns"/>) rounds after shifting.
        /// </summary>
        private static void Apply(
            List<Waypoint> route,
            Func<double, double, double[]> turn,
            int width, int height,
            Func<double, int> roundY)
        {
            if (route == null || route.Count == 0) return;

            // Snapshot before rotation so handle math can reference original
            // coordinates (especially for coincident-endpoint segments).
            var original = route.ConvertAll(w => w.Clone());
            var positions = new double[route.Count][];

            // First pass: rotate endpoints and handles.
            for (int i = 0; i < route.Count; i++)
            {
                var w = route[i];

                var at = positions[i] = turn(w.X, w.Y);
                int x = (int)Math.Round(at[0]);
                int y = roundY(at[1]);

                // Support off-screen waypoints up to standard bounds (-100000..100000)
                const int minCoord = -100000;
                const int maxCoord = 100000;
                if (x < minCoord || y < minCoord || x > maxCoord || y > maxCoord)
                    throw new ArgumentException("Rotation would move a waypoint outside the supported coordinate range.");

                var a = turn(w.AX, w.AY);
                var b = turn(w.BX, w.BY);

                w.X = x;
                w.Y = y;
                // Handles keep their offset from the (snapped) endpoint.
                // The fractional part of the rotation is folded into the
                // handle position rather than discarded.
                w.AX = (float)(a[0] + x - at[0]);
                w.AY = (float)(a[1] + y - at[1]);
                w.BX = (float)(b[0] + x - at[0]);
                w.BY = (float)(b[1] + y - at[1]);
            }

            // Second pass: fix up Bézier handles so segments stay collinear.
            for (int i = 0; i + 1 < route.Count; i++)
            {
                double ox = positions[i + 1][0] - positions[i][0];
                double oy = positions[i + 1][1] - positions[i][1];
                double lengthSquared = ox * ox + oy * oy;

                // Coincident endpoints retain their rotated loop handles.
                if (lengthSquared < 1e-12) continue;

                double nx = route[i + 1].X - route[i].X;
                double ny = route[i + 1].Y - route[i].Y;

                // If grid snapping collapses endpoints, keep the rotated handles from first pass
                if (nx == 0 && ny == 0) continue;

                // Apply one similarity transform to the entire segment, rather
                // than shifting each handle with its nearest endpoint.
                // Collinear handles stay collinear after this transform.
                double real       = (nx * ox + ny * oy) / lengthSquared;
                double imaginary  = (ny * ox - nx * oy) / lengthSquared;

                var b = turn(original[i].BX, original[i].BY);
                var a = turn(original[i + 1].AX, original[i + 1].AY);

                double bx = b[0] - positions[i][0], by = b[1] - positions[i][1];
                double ax = a[0] - positions[i][0], ay = a[1] - positions[i][1];

                route[i].BX = (float)(route[i].X + real * bx - imaginary * by);
                route[i].BY = (float)(route[i].Y + imaginary * bx + real * by);
                route[i + 1].AX = (float)(route[i].X + real * ax - imaginary * ay);
                route[i + 1].AY = (float)(route[i].Y + imaginary * ax + real * ay);
            }
        }
    }
}