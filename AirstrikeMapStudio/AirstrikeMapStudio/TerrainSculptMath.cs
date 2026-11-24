using System;

namespace AirstrikeStudio
{
    /// <summary>
    /// Pure, UI-free terrain brush math extracted from <see cref="GlTerrainControl"/>
    /// so sculpt invariants (softness falloff, monotone stroke weights, tile-bit
    /// preservation, flatten/smooth/noise) can be unit-tested directly.
    /// </summary>
    public static class TerrainSculptMath
    {
        public static double BrushWeight(double distance, double radius, double softness)
        {
            if (distance >= radius) return 0;
            if (softness <= 0) return 1;
            return Math.Min(1, Math.Max(0, (radius - distance) / (radius * softness)));
        }

        public static void SculptToolSample(
            uint[] original, uint[] draft, double[] weights,
            int width, int height,
            double x, double y,
            double radius, int amount,
            int tool, int target, double softness)
        {
            if (double.IsNaN(softness) || softness < 0 || softness > 1)
                throw new ArgumentException("Invalid brush softness.");
            if (radius <= 0
                || original.Length != width * height
                || draft.Length != original.Length
                || weights.Length != original.Length)
                throw new ArgumentException("Invalid brush geometry.");

            int iyMin = Math.Max(0, (int)Math.Floor((y - radius) * (height - 1) / height));
            int iyMax = Math.Min(height - 1, (int)Math.Ceiling((y + radius) * (height - 1) / height));
            int ixMin = Math.Max(0, (int)Math.Floor((x - radius) * (width - 1) / width));
            int ixMax = Math.Min(width - 1, (int)Math.Ceiling((x + radius) * (width - 1) / width));

            for (int iy = iyMin; iy <= iyMax; iy++)
            {
                for (int ix = ixMin; ix <= ixMax; ix++)
                {
                    double gx = width == 1 ? 0 : ix * width / (double)(width - 1);
                    double gy = height == 1 ? 0 : iy * height / (double)(height - 1);
                    double distance = Math.Sqrt((gx - x) * (gx - x) + (gy - y) * (gy - y));
                    double weight = BrushWeight(distance, radius, softness);

                    int at = iy * width + ix;
                    if (weight <= weights[at]) continue;
                    weights[at] = weight;

                    double delta = amount;
                    if (tool == 3)
                    {
                        uint h = (uint)(ix * 374761393 + iy * 668265263 + target * 1442695041);
                        h = (h ^ (h >> 13)) * 1274126177u;
                        h ^= h >> 16;
                        double n = ((h & 0xFFFF) / 32767.5) - 1.0;
                        delta = Math.Abs(amount) * n;
                    }
                    else if (tool != 0)
                    {
                        double desired = target;
                        if (tool == 2)
                        {
                            double sum = 0;
                            int count = 0;
                            int syMin = Math.Max(0, iy - 1), syMax = Math.Min(height - 1, iy + 1);
                            int sxMin = Math.Max(0, ix - 1), sxMax = Math.Min(width - 1, ix + 1);
                            for (int sy = syMin; sy <= syMax; sy++)
                                for (int sx = sxMin; sx <= sxMax; sx++)
                                {
                                    sum += original[sy * width + sx] & 0xFF;
                                    count++;
                                }
                            desired = sum / count;
                        }
                        double targetDelta = desired - (original[at] & 0xFF);
                        double limit = Math.Abs(amount);
                        delta = Math.Max(-limit, Math.Min(limit, targetDelta));
                    }

                    int value = Math.Max(0, Math.Min(255,
                        (int)(original[at] & 0xFF) + (int)Math.Round(delta * weight)));

                    draft[at] = (original[at] & 0xFFFFFF00u) | (uint)value;
                }
            }
        }

        public static void SculptSample(
            uint[] original, uint[] draft, double[] weights,
            int width, int height,
            double x, double y,
            double radius, int amount)
        {
            SculptToolSample(original, draft, weights, width, height, x, y, radius, amount, 0, 0, 1);
        }

        public static void SculptToolSample(
            uint[] original, uint[] draft, double[] weights,
            int width, int height,
            double x, double y,
            double radius, int amount,
            int tool, int target)
        {
            SculptToolSample(original, draft, weights, width, height, x, y, radius, amount, tool, target, 1);
        }
    }
}
