using System;

namespace AirstrikeStudio
{
    /// <summary>
    /// Pure, UI-free route spline and surface-height interpolation helpers
    /// extracted from <see cref="GlTerrainControl"/> for unit testing.
    /// </summary>
    public static class RouteMath
    {
        public static bool IsFinite(double v) =>
            !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e7;

        public static int SegmentSampleCount(Waypoint a, Waypoint b)
        {
            double length =
                Math.Sqrt(Math.Pow(a.BX - a.X, 2) + Math.Pow(a.BY - a.Y, 2)) +
                Math.Sqrt(Math.Pow(b.AX - a.BX, 2) + Math.Pow(b.AY - a.BY, 2)) +
                Math.Sqrt(Math.Pow(b.X - b.AX, 2) + Math.Pow(b.Y - b.AY, 2));

            return IsFinite(length)
                ? (int)Math.Max(32, Math.Min(512, Math.Ceiling(length * 8)))
                : 32;
        }

        public static void EvaluateBezier(Waypoint a, Waypoint b, double t, out double x, out double y)
        {
            double u = 1.0 - t;
            double uu = u * u;
            double tt = t * t;
            double uuu = uu * u;
            double ttt = tt * t;

            x = uuu * a.X + 3.0 * uu * t * a.BX + 3.0 * u * tt * b.AX + ttt * b.X;
            y = uuu * a.Y + 3.0 * uu * t * a.BY + 3.0 * u * tt * b.AY + ttt * b.Y;
        }

        public static double InterpolateSurfaceZ(double a, double b, double c, double d, double tx, double ty)
        {
            return ty <= tx
                ? a + (b - a) * tx + (d - b) * ty
                : a + (d - c) * tx + (c - a) * ty;
        }
    }
}
