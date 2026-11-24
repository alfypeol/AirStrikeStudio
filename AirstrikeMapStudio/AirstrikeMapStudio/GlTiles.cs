using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Tile painting state
        // ═════════════════════════════════════════════════════════════════
        // The tile brush works like the terrain sculptor: a stroke is one
        // mouse-down → mouse-up. During a stroke, edits happen on a scratch
        // copy; on commit the touched cells are diffed against the real map
        // and synced. Undo is per-stroke, same as terrain.
        //
        // A "cell" here stores four things in its 32 bits:
        //   bits  0– 7 : terrain height (0–255)
        //   bits  8–15 : tile sheet index (1–255; 0 = no tile)
        //   bits 16–23 : tile index in the sheet's atlas (0–127)
        //   bits 24–31 : tile rotation code (0, 3, 6, 9 only; see TileValue)
        // The terrain sculptor only touches bits 0–7; tile painting only
        // touches bits 8–31. The two never clobber each other.
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Currently selected tile sheet index (1–255).</summary>
        public int TileSheet = 1;

        /// <summary>Currently selected tile index in the sheet's atlas (0–127).</summary>
        public int TileIndex = 0;

        /// <summary>Currently selected tile rotation, 0–3 (0 = 0°, 1 = 90°, 2 = 180°, 3 = 270°).</summary>
        public int TileTurn = 0;

        /// <summary>When true, the brush erases tiles (clears bits 8–31) instead of painting.</summary>
        public bool EraseTile;

        /// <summary>Tile brush width/height in cells (1–5).</summary>
        public int TileBrushSize = 1;

        /// <summary>
        /// Tile paint mode: 0 = Brush (freehand), 1 = Line / road strip,
        /// 2 = Rectangle fill, 3 = Flood fill (bucket).
        /// </summary>
        public int TilePaintMode = 0;

        /// <summary>When true, automatically rotates the tile to follow the drag direction.</summary>
        public bool AutoRotateTiles = false;

        public event EventHandler TileSettingsChanged;

        public void RotateTileBrush(int steps = 1)
        {
            TileTurn = ((TileTurn + steps) % 4 + 4) % 4;
            TileSettingsChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }

        public void AdjustTileBrushSize(int delta)
        {
            TileBrushSize = Math.Max(1, Math.Min(5, TileBrushSize + delta));
            TileSettingsChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }

        /// <summary>
        /// Samples the tile under the given screen coordinates (Alt+Click in Tile mode).
        /// If the cell has a tile, selects its sheet, tile index, and rotation and disables
        /// Erase mode; if the cell is empty, enables Erase mode.
        /// </summary>
        public bool SampleTileAt(int screenX, int screenY)
        {
            if (!GroundAt(screenX, screenY, out Point3D hit)) return false;
            int tx = (int)Math.Floor(hit.X / 40);
            int ty = (int)Math.Floor(hit.Y / 40);
            if (tx < 0 || ty < 0 || tx >= map.Width || ty >= map.Height) return false;

            uint cell = map.Cells[ty * map.Width + tx];
            int sheet = (int)((cell >> 8) & 255);
            int index = (int)((cell >> 16) & 255);
            int code  = (int)((cell >> 24) & 255);

            if (sheet == 0)
            {
                EraseTile = true;
            }
            else
            {
                EraseTile = false;
                TileSheet = sheet;
                TileIndex = index;
                TileTurn  = (code / 3) & 3;
            }

            HoverTile(screenX, screenY);
            TileSettingsChanged?.Invoke(this, EventArgs.Empty);
            Changed();
            return true;
        }

        // ── Stroke-scope state ──
        private uint[] tileDraft;                                  // scratch copy of map.Cells
        private readonly HashSet<int> tileTouched = new HashSet<int>(); // flat cell indices edited this stroke
        private int strokeSheet, strokeIndex, strokeTurn;          // locked at BeginTileStroke
        private bool strokeErase;
        private int strokeBrushSize = 1;
        private int strokePaintMode = 0;
        private int strokeStartX, strokeStartY;
        private bool strokeStartValid;
        private int previousTileX, previousTileY;                  // for interpolating between two mouse-move events
        private bool previousTile;                                  // true if previousTileX/Y are valid

        // ── Cursor preview state ──
        private bool tileHover;
        private int tileX, tileY;
        private long lastTilePaint;           // input-clock ms of last paint, for throttle
        private uint cursorTexture;           // GL texture id of the tile being previewed
        private int cursorSheet = -1;        // sheet of the texture in cursorTexture, or -1 if none
        private int cursorIndex = -1;        // index of the texture in cursorTexture, or -1 if none

        // ═════════════════════════════════════════════════════════════════
        // Bit-exact pack/unpack
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns a new cell value with the tile bits replaced.
        /// </summary>
        /// <remarks>
        /// Bit layout:
        ///   bits  0– 7 : preserved from <paramref name="old"/> (height)
        ///   bits  8–15 : <paramref name="sheet"/>
        ///   bits 16–23 : <paramref name="index"/>
        ///   bits 24–31 : <paramref name="turn"/> × 3
        ///
        /// <c>turn × 3</c>, not <c>turn × 4</c> or <c>turn &lt;&lt; 4</c>.
        /// Rotation codes are 0, 3, 6, 9 for turns 0, 1, 2, 3 respectively.
        /// <see cref="TerrainViewer.AddTile"/> rejects any other code as
        /// unsupported, so producing anything else silently drops the tile.
        /// </remarks>
        public static uint TileValue(uint old, int sheet, int index, int turn, bool erase)
            => TilePaintMath.TileValue(old, sheet, index, turn, erase);

        // ═════════════════════════════════════════════════════════════════
        // Stroke lifecycle
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Starts a new tile stroke. Snapshots the brush selection for the
        /// whole stroke, so changing the sheet or index mid-drag doesn't
        /// affect the cells already painted.
        /// </summary>
        public void BeginTileStroke()
        {
            // Finish any in-progress stroke first; the two must not coexist.
            EndTileStroke(commit: false);
            if (CommitTerrain == null) return;

            if (!EraseTile)
            {
                // Validate the tile exists in this sheet before committing to a stroke.
                using (var bitmap = Assets.Tga(Path.Combine(assetRoot, "tiles", "tiles" + TileSheet + ".tga")))
                {
                    if (TerrainViewer.AtlasRectangle(bitmap.Width, bitmap.Height, TileIndex).IsEmpty)
                        throw new ArgumentException("Tile is outside this sheet.");
                }
            }

            strokeSheet = TileSheet;
            strokeIndex = TileIndex;
            strokeTurn = TileTurn;
            strokeErase = EraseTile;
            strokeBrushSize = Math.Max(1, Math.Min(5, TileBrushSize));
            strokePaintMode = TilePaintMode;
            strokeStartValid = false;

            tileDraft = (uint[])map.Cells.Clone();
            previousTile = false;
        }

        private void StampTileFootprint(int cx, int cy, int size, int turn)
        {
            TilePaintMath.StampFootprint(
                tileDraft, map.Width, map.Height,
                cx, cy, size, strokeSheet, strokeIndex, turn, strokeErase, tileTouched);
        }

        private void FloodFillTiles(int startX, int startY)
        {
            if (startX < 0 || startY < 0 || startX >= map.Width || startY >= map.Height) return;
            uint targetSig = map.Cells[startY * map.Width + startX] & 0x00FFFF00u; // match (sheet, index)
            uint replacementSample = TileValue(0, strokeSheet, strokeIndex, strokeTurn, strokeErase) & 0x00FFFF00u;
            if (targetSig == replacementSample && !strokeErase)
            {
                // Still allow re-orienting if only rotation differs, otherwise avoid no-op BFS
                int at0 = startY * map.Width + startX;
                if (TileValue(map.Cells[at0], strokeSheet, strokeIndex, strokeTurn, strokeErase) == map.Cells[at0])
                    return;
            }

            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            int root = startY * map.Width + startX;
            visited.Add(root);
            queue.Enqueue(root);

            const int MaxFloodCells = 4096;
            int[] nx = { 1, -1, 0, 0 };
            int[] ny = { 0, 0, 1, -1 };

            while (queue.Count > 0 && visited.Count <= MaxFloodCells)
            {
                int cur = queue.Dequeue();
                tileDraft[cur] = TileValue(tileDraft[cur], strokeSheet, strokeIndex, strokeTurn, strokeErase);
                tileTouched.Add(cur);

                int cx = cur % map.Width;
                int cy = cur / map.Width;
                for (int d = 0; d < 4; d++)
                {
                    int px = cx + nx[d];
                    int py = cy + ny[d];
                    if (px < 0 || py < 0 || px >= map.Width || py >= map.Height) continue;
                    int next = py * map.Width + px;
                    if (visited.Contains(next)) continue;
                    if ((map.Cells[next] & 0x00FFFF00u) != targetSig) continue;
                    visited.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        /// <summary>
        /// Paints from the last cell to (<paramref name="x"/>, <paramref name="y"/>)
        /// inclusive, interpolating along the way so fast drags don't skip cells.
        /// Also supports Line (mode 1), Rectangle (mode 2), and Flood Fill (mode 3).
        /// </summary>
        public void PaintTileStroke(int x, int y)
        {
            if (tileDraft == null) return;

            if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
            {
                previousTile = false;
                return;
            }

            if (!strokeStartValid)
            {
                strokeStartX = x;
                strokeStartY = y;
                strokeStartValid = true;
            }

            if (strokePaintMode == 3)
            {
                // Flood fill: run once at the clicked cell
                if (tileTouched.Count == 0)
                {
                    FloodFillTiles(x, y);
                    Invalidate();
                }
                return;
            }

            if (strokePaintMode == 1)
            {
                // Line / road strip preview from (strokeStartX, strokeStartY) to (x, y)
                foreach (int oldAt in tileTouched)
                    tileDraft[oldAt] = map.Cells[oldAt];
                tileTouched.Clear();

                int dx = x - strokeStartX;
                int dy = y - strokeStartY;
                if (AutoRotateTiles && (dx != 0 || dy != 0))
                {
                    strokeTurn = Math.Abs(dx) >= Math.Abs(dy)
                        ? (dx > 0 ? 1 : 3)
                        : (dy > 0 ? 2 : 0);
                }

                int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
                for (int n = 0; n <= steps; n++)
                {
                    int px = steps == 0 ? x : (int)Math.Round(strokeStartX + dx * (double)n / steps);
                    int py = steps == 0 ? y : (int)Math.Round(strokeStartY + dy * (double)n / steps);
                    StampTileFootprint(px, py, strokeBrushSize, strokeTurn);
                }
                Invalidate();
                return;
            }

            if (strokePaintMode == 2)
            {
                // Rectangle fill preview from (strokeStartX, strokeStartY) to (x, y)
                foreach (int oldAt in tileTouched)
                    tileDraft[oldAt] = map.Cells[oldAt];
                tileTouched.Clear();

                int xMin = Math.Max(0, Math.Min(strokeStartX, x));
                int xMax = Math.Min(map.Width - 1, Math.Max(strokeStartX, x));
                int yMin = Math.Max(0, Math.Min(strokeStartY, y));
                int yMax = Math.Min(map.Height - 1, Math.Max(strokeStartY, y));

                for (int py = yMin; py <= yMax; py++)
                {
                    for (int px = xMin; px <= xMax; px++)
                    {
                        int at = py * map.Width + px;
                        tileDraft[at] = TileValue(tileDraft[at], strokeSheet, strokeIndex, strokeTurn, strokeErase);
                        tileTouched.Add(at);
                    }
                }
                Invalidate();
                return;
            }

            // Mode 0: Freehand brush
            int x0 = previousTile ? previousTileX : x;
            int y0 = previousTile ? previousTileY : y;
            if (AutoRotateTiles && previousTile && (x != x0 || y != y0))
            {
                int dx = x - x0;
                int dy = y - y0;
                strokeTurn = Math.Abs(dx) >= Math.Abs(dy)
                    ? (dx > 0 ? 1 : 3)
                    : (dy > 0 ? 2 : 0);
                TileTurn = strokeTurn;
                TileSettingsChanged?.Invoke(this, EventArgs.Empty);
            }

            int freeSteps = Math.Max(Math.Abs(x - x0), Math.Abs(y - y0));
            for (int n = 0; n <= freeSteps; n++)
            {
                int px = freeSteps == 0 ? x : (int)Math.Round(x0 + (x - x0) * (double)n / freeSteps);
                int py = freeSteps == 0 ? y : (int)Math.Round(y0 + (y - y0) * (double)n / freeSteps);
                StampTileFootprint(px, py, strokeBrushSize, strokeTurn);
            }

            previousTileX = x;
            previousTileY = y;
            previousTile = true;
            Invalidate();
        }

        /// <summary>
        /// Ends the current tile stroke. When <paramref name="commit"/> is true
        /// and any touched cell changed, syncs the map through <see cref="CommitTerrain"/>.
        /// Always clears the draft and invalidates, even if nothing changed.
        /// </summary>
        public void EndTileStroke(bool commit)
        {
            var cells = tileDraft;
            tileDraft = null;
            previousTile = false;

            bool changed = false;
            if (cells != null)
            {
                foreach (int at in tileTouched)
                {
                    if (cells[at] != map.Cells[at]) { changed = true; break; }
                }
            }
            tileTouched.Clear();

            if (commit && changed && CommitTerrain != null)
                SyncPlacements(CommitTerrain(cells));

            Invalidate();
        }

        /// <summary>Convenience: one-shot tile apply at a single cell.</summary>
        public void ApplyTile(int x, int y)
        {
            BeginTileStroke();
            PaintTileStroke(x, y);
            EndTileStroke(commit: true);
        }

        // ═════════════════════════════════════════════════════════════════
        // Hover
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates <see cref="tileHover"/> and the hover cell coordinates.
        /// Called during mouse movement and once on mouse-down.
        /// </summary>
        private void HoverTile(int x, int y)
        {
            tileHover = GroundAt(x, y, out Point3D hit);
            if (tileHover)
            {
                tileX = (int)Math.Floor(hit.X / 40);
                tileY = (int)Math.Floor(hit.Y / 40);
                tileHover = tileX >= 0 && tileY >= 0 && tileX < map.Width && tileY < map.Height;
            }
            Invalidate();
        }

        /// <summary>
        /// Applies the brush at the current hover cell. Called from mouse-move.
        /// </summary>
        private void StampTile()
        {
            if (tileHover)
                PaintTileStroke(tileX, tileY);
            else
                previousTile = false;
        }

        // ═════════════════════════════════════════════════════════════════
        // Cursor texture upload
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Uploads the current tile as a GL texture for the preview overlay.
        /// Early-returns if the same tile is already uploaded.
        /// </summary>
        private void EnsureTileTexture(int sheet, int index)
        {
            if (cursorTexture != 0 && cursorSheet == sheet && cursorIndex == index) return;

            string path = Path.Combine(assetRoot, "tiles", "tiles" + sheet + ".tga");
            using (var atlas = Assets.Tga(path))
            {
                var rect = TerrainViewer.AtlasRectangle(atlas.Width, atlas.Height, index);
                if (rect.IsEmpty) return;

                using (var image = atlas.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    var data = image.LockBits(
                        new Rectangle(0, 0, image.Width, image.Height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    byte[] pixels = new byte[image.Width * image.Height * 4];
                    try
                    {
                        for (int y = 0; y < image.Height; y++)
                        {
                            Marshal.Copy(
                                IntPtr.Add(data.Scan0, y * data.Stride),
                                pixels, y * image.Width * 4, image.Width * 4);
                        }
                    }
                    finally
                    {
                        image.UnlockBits(data);
                    }

                    if (cursorTexture == 0) glGenTextures(1, out cursorTexture);
                    glBindTexture(GL_TEXTURE_2D, cursorTexture);
                    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
                    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
                    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP);
                    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP);
                    glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA,
                                 image.Width, image.Height, 0,
                                 GL_BGRA, GL_UNSIGNED_BYTE, pixels);

                    cursorSheet = sheet;
                    cursorIndex = index;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Cursor rendering
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws one tile-shaped quad at the given cell.
        /// </summary>
        /// <remarks>
        /// V coordinate is <c>1 - dy[uv]</c> because WPF bitmaps use top-origin
        /// UVs and OpenGL uses bottom-origin. The rotation permutation
        /// <c>(i - turn + 4) % 4</c> rotates the UV corners in 90° steps; the
        /// <c>+4</c> prevents negative modulo when <c>turn = 3, i = 0</c>.
        /// The Z comes from the terrain height cache plus a small lift so the
        /// preview doesn't z-fight the terrain it's floating above.
        /// </remarks>
        private void DrawTileCell(int x, int y, int turn, bool erase, float alpha)
        {
            int[] dx = { 0, 1, 1, 0 };
            int[] dy = { 0, 0, 1, 1 };

            if (erase)
                glDisable(GL_TEXTURE_2D);
            else
            {
                glEnable(GL_TEXTURE_2D);
                glBindTexture(GL_TEXTURE_2D, cursorTexture);
            }

            glColor4f(1, erase ? 0 : 1, 1, alpha);
            glBegin(GL_QUADS);

            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
            {
                int uv = (i - turn + 4) % 4;
                glTexCoord2d(dx[uv], 1 - dy[uv]);

                routeHeights.TryGetValue((x + dx[i]) + "," + (y + dy[i]), out double z);
                glVertex3f((x + dx[i]) * 40, (y + dy[i]) * 40, (float)(z + 0.4));
            }

            glEnd();
        }

        /// <summary>
        /// Draws the tile cursor preview: a translucent overlay for the cells
        /// already painted this stroke, plus a more opaque one for the cell
        /// under the cursor. Also draws a wireframe square around the hover cell.
        /// </summary>
        private void DrawTileCursor()
        {
            if (!TileMode) return;

            bool erase = tileDraft != null ? strokeErase : EraseTile;
            int turn = tileDraft != null ? strokeTurn : TileTurn;

            if (!erase)
                EnsureTileTexture(tileDraft != null ? strokeSheet : TileSheet,
                                  tileDraft != null ? strokeIndex : TileIndex);

            glDisable(GL_FOG);
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
            glDepthMask(0);
            glEnable(GL_POLYGON_OFFSET_FILL);
            glPolygonOffset(-2, -2);

            foreach (int at in tileTouched)
                DrawTileCell(at % map.Width, at / map.Width, turn, erase, 0.85f);

            int size = Math.Max(1, Math.Min(5, TileBrushSize));
            int half = (size - 1) / 2;
            if (tileHover)
            {
                for (int dy = 0; dy < size; dy++)
                {
                    int py = tileY - half + dy;
                    if (py < 0 || py >= map.Height) continue;
                    for (int dx = 0; dx < size; dx++)
                    {
                        int px = tileX - half + dx;
                        if (px < 0 || px >= map.Width) continue;
                        DrawTileCell(px, py, turn, erase, 0.65f);
                    }
                }
            }

            glDisable(GL_POLYGON_OFFSET_FILL);
            glDisable(GL_TEXTURE_2D);
            glDisable(GL_BLEND);
            glDepthMask(1);

            if (!tileHover) return;

            glDisable(GL_DEPTH_TEST);
            glColor4f(erase ? 1 : 0, erase ? 0 : 1, 1, 1);
            glBegin(GL_LINES);

            int x0 = Math.Max(0, tileX - half);
            int y0 = Math.Max(0, tileY - half);
            int x1 = Math.Min(map.Width, tileX - half + size);
            int y1 = Math.Min(map.Height, tileY - half + size);
            int[] bx = { x0, x1, x1, x0 };
            int[] by = { y0, y0, y1, y1 };
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                routeHeights.TryGetValue(bx[i] + "," + by[i], out double zA);
                routeHeights.TryGetValue(bx[j] + "," + by[j], out double zB);
                glVertex3f(bx[i] * 40, by[i] * 40, (float)(zA + 2));
                glVertex3f(bx[j] * 40, by[j] * 40, (float)(zB + 2));
            }

            glEnd();
            glEnable(GL_DEPTH_TEST);
        }

        // ═════════════════════════════════════════════════════════════════
        // Tile picker dialog
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the tile picker. On confirm, mutates <see cref="TileSheet"/>
        /// and <see cref="TileIndex"/>; does not touch stroke state.
        /// </summary>
        public void PickTile(IWin32Window owner)
        {
            string tilesDir = Path.Combine(assetRoot, "tiles");
            if (!Directory.Exists(tilesDir))
            {
                MessageBox.Show(owner, "No 'tiles' folder found.", "Tile picker");
                return;
            }

            var sheetFiles = Directory.GetFiles(tilesDir, "tiles*.tga");
            if (sheetFiles.Length == 0)
            {
                MessageBox.Show(owner, "No tile sheets found in the 'tiles' folder.", "Tile picker");
                return;
            }

            using (var form = new Form
            {
                Text = "Choose road / ground tile",
                Width = 700,
                Height = 560,
                StartPosition = FormStartPosition.CenterParent,
            })
            using (var images = new ImageList
            {
                ImageSize = new Size(64, 64),
                ColorDepth = ColorDepth.Depth32Bit,
            })
            {
                var sheets = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (var path in sheetFiles)
                {
                    if (int.TryParse(Path.GetFileNameWithoutExtension(path).Substring(5), out int id) && id > 0 && id <= 255)
                        sheets.Items.Add(id);
                }

                var list = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.LargeIcon,
                    MultiSelect = false,
                    LargeImageList = images,
                };
                var ok = new Button { Text = "Use selected tile", Dock = DockStyle.Bottom, Height = 32 };

                sheets.SelectedIndexChanged += (s, e) =>
                {
                    if (sheets.SelectedIndex == -1) return;

                    // Clear list FIRST to avoid WinForms redraw crashes
                    list.Items.Clear();
                    images.Images.Clear();

                    try
                    {
                        string atlasPath = Path.Combine(tilesDir, "tiles" + sheets.SelectedItem + ".tga");
                        using (var atlas = Assets.Tga(atlasPath))
                        {
                            for (int i = 0; i < 128; i++)
                            {
                                var rect = TerrainViewer.AtlasRectangle(atlas.Width, atlas.Height, i);
                                if (rect.IsEmpty) break;

                                // Create a safe, clean 32bpp bitmap instead of passing the raw TGA clone
                                var thumb = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                using (var g = Graphics.FromImage(thumb))
                                {
                                    g.DrawImage(atlas, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
                                }

                                images.Images.Add(thumb);
                                list.Items.Add(new ListViewItem("Tile " + i, i) { Tag = i });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, "Could not load tile sheet: " + ex.Message, "Unsupported Tile Sheet");
                    }
                };

                Action choose = () =>
                {
                    if (list.SelectedItems.Count == 0) return;
                    TileSheet = (int)sheets.SelectedItem;
                    TileIndex = (int)list.SelectedItems[0].Tag;
                    form.DialogResult = DialogResult.OK;
                };
                ok.Click += (s, e) => choose();
                list.DoubleClick += (s, e) => choose();

                form.Controls.Add(list);
                form.Controls.Add(sheets);
                form.Controls.Add(ok);

                if (sheets.Items.Count > 0)
                {
                    int initialIndex = sheets.Items.IndexOf(TileSheet);
                    sheets.SelectedIndex = initialIndex >= 0 ? initialIndex : 0;
                }

                form.ShowDialog(owner);
            }
        }
    }

    /// <summary>
    /// The Tiles &amp; roads tab of the OpenGL workspace.
    /// </summary>
    public sealed partial class GlTerrainWindow
    {
        private void SetupTileTab(TabControl tabs)
        {
            var tab = new TabPage("Tiles & roads");
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
            tab.Controls.Add(flow);
            tabs.TabPages.Add(tab);

            var active = new CheckBox { Text = "Place tiles", AutoSize = true };
            var modeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
            modeBox.Items.AddRange(new object[]
            {
                "Brush (freehand)",
                "Line / road strip",
                "Rectangle fill",
                "Flood fill (bucket)",
            });
            modeBox.SelectedIndex = 0;

            var sizeUpDown = new NumericUpDown { Minimum = 1, Maximum = 5, Value = 1, Width = 44 };
            var autoRotate = new CheckBox { Text = "Auto-rotate with drag", AutoSize = true };
            var erase = new CheckBox { Text = "Erase", AutoSize = true };
            var pick = new Button { Text = "Choose tile…", AutoSize = true };
            var rotate = new Button { Text = "Rotate 90°", AutoSize = true };
            var info = new Label { AutoSize = true, Padding = new Padding(0, 5, 0, 0) };

            Action refreshLabel = () =>
            {
                if (erase.Checked != Preview.EraseTile)
                    erase.Checked = Preview.EraseTile;
                int clampedSize = Math.Max(1, Math.Min(5, Preview.TileBrushSize));
                if ((int)sizeUpDown.Value != clampedSize)
                    sizeUpDown.Value = clampedSize;
                info.Text = "Sheet " + Preview.TileSheet
                          + ", tile " + Preview.TileIndex
                          + ", rot " + (Preview.TileTurn * 90) + "°"
                          + " (" + clampedSize + "×" + clampedSize + ")";
            };
            Preview.TileSettingsChanged += (s, e) => refreshLabel();

            modeBox.SelectedIndexChanged += (s, e) =>
            {
                Preview.TilePaintMode = modeBox.SelectedIndex;
                sizeUpDown.Enabled = modeBox.SelectedIndex <= 1;
                autoRotate.Enabled = modeBox.SelectedIndex <= 1;
            };
            sizeUpDown.ValueChanged += (s, e) =>
            {
                Preview.TileBrushSize = (int)sizeUpDown.Value;
                refreshLabel();
                Preview.Changed();
            };
            autoRotate.CheckedChanged += (s, e) =>
            {
                Preview.AutoRotateTiles = autoRotate.Checked;
            };

            pick.Click += (s, e) =>
            {
                try { Preview.PickTile(this); refreshLabel(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Tile picker"); }
            };
            rotate.Click += (s, e) =>
            {
                Preview.RotateTileBrush(1);
                Preview.Focus();
            };

            RegisterEditingCheck(active, EditingMode.Tiles);

            erase.CheckedChanged += (s, e) =>
            {
                Preview.EraseTile = erase.Checked;
                Preview.Changed();
            };

            // Leaving the tab exits tile mode so the tile cursor stops rendering.
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedTab != tab) active.Checked = false;
            };

            // Escape also exits tile mode.
            Preview.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) active.Checked = false;
            };

            flow.Controls.Add(active);
            flow.Controls.Add(modeBox);
            flow.Controls.Add(new Label { Text = "Size", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            flow.Controls.Add(sizeUpDown);
            flow.Controls.Add(pick);
            flow.Controls.Add(rotate);
            flow.Controls.Add(autoRotate);
            flow.Controls.Add(erase);
            flow.Controls.Add(info);

            var undo = new Button { Text = "Undo", AutoSize = true };
            undo.Click += (s, e) => Preview.HistoryMove(false);
            flow.Controls.Add(undo);

            var redo = new Button { Text = "Redo", AutoSize = true };
            redo.Click += (s, e) => Preview.HistoryMove(true);
            flow.Controls.Add(redo);

            flow.Controls.Add(new Label
            {
                Text = "Drag to paint / erase • Alt+click: sample tile • R: rotate 90° • " +
                       "Escape cancels • Right-drag pans.",
                AutoSize = true,
            });

            refreshLabel();
        }
    }
}