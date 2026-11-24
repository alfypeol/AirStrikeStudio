using System;
using System.Collections.Generic;

namespace AirstrikeStudio
{
    /// <summary>
    /// Pure, UI-free tile cell bit-packing and footprint stamping helpers
    /// extracted from <see cref="GlTerrainControl"/> for unit testing.
    /// </summary>
    public static class TilePaintMath
    {
        public static uint TileValue(uint old, int sheet, int index, int turn, bool erase)
        {
            if (erase)
                return old & 255;

            if (sheet < 1 || sheet > 255 || index < 0 || index >= 128 || turn < 0 || turn > 3)
                throw new ArgumentException("Invalid tile selection.");

            return (old & 255)
                 | ((uint)sheet << 8)
                 | ((uint)index << 16)
                 | ((uint)(turn * 3) << 24);
        }

        public static void StampFootprint(
            uint[] cells, int width, int height,
            int cx, int cy, int size,
            int sheet, int index, int turn, bool erase,
            ICollection<int> touched = null)
        {
            int half = (size - 1) / 2;
            for (int dy = 0; dy < size; dy++)
            {
                int py = cy - half + dy;
                if (py < 0 || py >= height) continue;
                for (int dx = 0; dx < size; dx++)
                {
                    int px = cx - half + dx;
                    if (px < 0 || px >= width) continue;
                    int at = py * width + px;
                    cells[at] = TileValue(cells[at], sheet, index, turn, erase);
                    touched?.Add(at);
                }
            }
        }
    }
}
