using System;
using System.Drawing;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    /// <summary>
    /// Camera basis and ray-picking math shared by every function that
    /// converts between screen coordinates and world space.
    /// </summary>
    /// <remarks>
    /// The editor renders into a 4:3 letterboxed viewport. Every function
    /// here must use the same viewport rectangle and the same camera basis
    /// as <c>GlTerrainControl.Render</c> (in GlTerrainWindow.cs). If that
    /// render code ever changes (FOV, letterbox, matrix order), this file
    /// must change in lockstep — that is the point of putting it here
    /// rather than inlining it four times.
    ///
    /// <para>
    /// The camera basis is a right-handed frame: <c>right</c> is screen +X,
    /// <c>up</c> is screen -Y (up on screen), <c>-offset</c> is into the
    /// screen (view direction). The projection is symmetric: half-height is
    /// <c>tan(π/6)</c> (half of a 60° vertical FOV), half-width is
    /// <c>tan(π/6) * 4/3</c> for the 4:3 aspect.
    /// </para>
    /// </remarks>
    public static class GlPickRay
    {
        /// <summary>
        /// Half-height of the view frustum at unit distance, in tangent units.
        /// <c>tan(30°)</c> = <c>1/√3</c>.
        /// </summary>
        private const double HalfFovTan = 0.5773502691896258;

        /// <summary>Aspect ratio of the letterboxed viewport.</summary>
        private const double Aspect43 = 4.0 / 3.0;

        /// <summary>
        /// World-space camera basis: origin, screen-right, screen-up, and the
        /// camera's offset from the orbit target.
        /// </summary>
        public readonly struct CameraBasis
        {
            public readonly Point3D Origin;
            public readonly Vector3D Right;
            public readonly Vector3D Up;
            public readonly Vector3D Offset; // origin = target + Offset

            public CameraBasis(Point3D origin, Vector3D right, Vector3D up, Vector3D offset)
            {
                Origin = origin;
                Right = right;
                Up = up;
                Offset = offset;
            }
        }

        /// <summary>
        /// Screen-space viewport rectangle after 4:3 letterboxing.
        /// </summary>
        public readonly struct Viewport
        {
            public readonly int Left, Top, Width, Height;

            public Viewport(int left, int top, int width, int height)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            /// <summary>True if the given client-space (x, y) is inside the letterbox.</summary>
            public bool Contains(int x, int y) =>
                Width >= 1 && Height >= 1
                && x >= Left && x < Left + Width
                && y >= Top  && y < Top  + Height;
        }

        /// <summary>
        /// Computes the letterboxed viewport for a client size.
        /// </summary>
        public static Viewport ComputeViewport(int clientWidth, int clientHeight)
        {
            int vw = Math.Min(clientWidth, clientHeight * 4 / 3);
            int vh = vw * 3 / 4;
            return new Viewport((clientWidth - vw) / 2, (clientHeight - vh) / 2, vw, vh);
        }

        /// <summary>
        /// Builds the world-space camera basis from the orbit parameters.
        /// </summary>
        public static CameraBasis BuildCameraBasis(
            double centerX, double centerY, double heading, double pitch, double distance)
        {
            double a = heading * Math.PI / 180;
            double p = pitch   * Math.PI / 180;

            var offset = new Vector3D(
                Math.Cos(a) * Math.Cos(p),
                Math.Sin(a) * Math.Cos(p),
                Math.Sin(p));

            var origin = new Point3D(centerX * 40, centerY * 40, 0) + offset * distance;

            var right = new Vector3D(-Math.Sin(a), Math.Cos(a), 0);
            var up    = new Vector3D(
                -Math.Cos(a) * Math.Sin(p),
                -Math.Sin(a) * Math.Sin(p),
                 Math.Cos(p));

            return new CameraBasis(origin, right, up, offset);
        }

        /// <summary>
        /// Builds a normalized world-space ray from a screen position.
        /// Returns false if the position is outside the letterboxed viewport.
        /// </summary>
        public static bool TryBuildRay(
            int x, int y, int clientWidth, int clientHeight,
            double centerX, double centerY, double heading, double pitch, double distance,
            out Point3D origin, out Vector3D direction)
        {
            origin = default;
            direction = default;

            var viewport = ComputeViewport(clientWidth, clientHeight);
            if (!viewport.Contains(x, y)) return false;

            var basis = BuildCameraBasis(centerX, centerY, heading, pitch, distance);

            double ndcX = 2.0 * (x - viewport.Left) / viewport.Width - 1;
            double ndcY = 1 - 2.0 * (y - viewport.Top) / viewport.Height;

            var ray = -basis.Offset
                    + basis.Right * (ndcX * HalfFovTan * Aspect43)
                    + basis.Up    * (ndcY * HalfFovTan);
            ray.Normalize();

            origin = basis.Origin;
            direction = ray;
            return true;
        }

        /// <summary>
        /// Projects a world point back to screen space.
        /// Returns false if the point is behind the near plane or outside
        /// the letterboxed viewport.
        /// </summary>
        public static bool TryProject(
            Point3D world, int clientWidth, int clientHeight,
            double centerX, double centerY, double heading, double pitch, double distance,
            out PointF screen)
        {
            screen = default;

            var basis = BuildCameraBasis(centerX, centerY, heading, pitch, distance);
            var delta = world - basis.Origin;

            double depth = Vector3D.DotProduct(delta, -basis.Offset);
            if (depth <= 1) return false;

            double sx = Vector3D.DotProduct(delta, basis.Right) / (depth * HalfFovTan * Aspect43);
            double sy = Vector3D.DotProduct(delta, basis.Up)    / (depth * HalfFovTan);

            var viewport = ComputeViewport(clientWidth, clientHeight);

            screen = new PointF(
                (float)((clientWidth  - viewport.Width)  / 2 + (sx + 1) * viewport.Width  / 2),
                (float)((clientHeight - viewport.Height) / 2 + (1 - sy) * viewport.Height / 2));

            return Math.Abs(sx) <= 1 && Math.Abs(sy) <= 1;
        }
    }
}