using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Terrain sculpting — brush state
        // ═════════════════════════════════════════════════════════════════
        // A "stroke" is one mouse-down → mouse-up cycle. During a stroke, all
        // editing happens on a scratch copy of the height cells; the real map
        // is only updated when the stroke commits. This makes each stroke a
        // single undo step, and lets Escape cancel cleanly.
        //
        // A "sample" is a single brush stamp along the stroke path. Between
        // two mouse-move events the brush is interpolated into several
        // samples so that fast drags do not leave gaps in the trail.
        //
        // A "cell" is one height value; one cell = 40×40 world units.
        // ═════════════════════════════════════════════════════════════════

        private readonly TerrainViewer terrainProvider;
        private readonly string terrainName;

        /// <summary>Current brush radius, in map-grid cells.</summary>
        public double BrushRadius = 2;

        /// <summary>Current brush softness, 0 = hard edge, 1 = full-gradient falloff.</summary>
        public double BrushSoftness = 1;

        /// <summary>Current brush strength; stored height changes by up to ±this per stroke.</summary>
        public int BrushStrength = 8;

        /// <summary>When true, the brush lowers terrain instead of raising it.</summary>
        public bool BrushLower;

        /// <summary>
        /// Sculpt tool selection. 0 = raise/lower, 1 = flatten, 2 = smooth,
        /// 3 = noise/roughen, 4 = ramp/slope.
        /// Must match the order in <c>SetupSculptTab</c>.
        /// </summary>
        public int SculptTool;

        /// <summary>When true, flatten samples the target height on mouse-down rather than using <see cref="FlattenHeight"/>.</summary>
        public bool SampleFlatten = true;

        /// <summary>Flatten target height when <see cref="SampleFlatten"/> is off.</summary>
        public int FlattenHeight = 128;

        /// <summary>Fired when brush radius or flatten target changes via hotkey/eyedropper.</summary>
        public event EventHandler TerrainSettingsChanged;

        // ── Ramp tool state (SculptTool == 4) ──
        private bool rampStartActive;
        private double rampStartX, rampStartY;
        private int rampStartHeight;
        private static int noiseStrokeSeed = 1;

        /// <summary>Commit callback, called once per stroke; returns the updated map.</summary>
        public Func<uint[], Map> CommitTerrain;

        /// <summary>Fires ~10×/sec while the pointer is over the terrain, so the status line can update.</summary>
        public event EventHandler TerrainHoverChanged;

        /// <summary>Human-readable readout of the cell under the cursor.</summary>
        public string TerrainReadout = "Move over terrain to read its height.";

        // ── Stroke-scope state (only valid between BeginSculpt and EndSculpt) ──
        private uint[]   sculptCells;      // scratch copy of map.Cells; null when not sculpting
        private double[] sculptWeights;    // per-cell maximum weight reached this stroke; 0.0 when not sculpting
        private bool     sculptHover;      // is the cursor currently over terrain?
        private double   sculptX, sculptY; // current brush position, in grid-cell coordinates
        private double   strokeX, strokeY; // last sample's brush position
        private int      sculptSign;       // +1 raise, -1 lower

        // ── Stroke-scope copies of the tool settings (locked at BeginSculpt) ──
        private double strokeRadius;
        private double strokeSoftness;
        private int    strokeStrength;
        private int    strokeTool;
        private int    strokeTarget;

        // ── Hover throttling ──
        private long lastHeightReadout;

        // ═════════════════════════════════════════════════════════════════
        // Brush math — BIT-EXACT
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Weight of a single cell at distance <paramref name="distance"/>
        /// from the brush center, given a radius and a softness in [0, 1].
        /// </summary>
        /// <remarks>
        /// Falloff is linear in the "soft zone": the outer <paramref name="softness"/>
        /// fraction of the radius. Inside the inner (1 - softness) fraction,
        /// weight is 1. At the radius itself, weight is 0.
        ///
        /// Formula: (radius - distance) / (radius * softness). Note the
        /// divisor is <c>radius * softness</c>, not just <c>softness</c>; the
        /// radius is part of the scale.
        /// </remarks>
        public static double BrushWeight(double distance, double radius, double softness)
            => TerrainSculptMath.BrushWeight(distance, radius, softness);

        public static void SculptToolSample(
            uint[] original, uint[] draft, double[] weights,
            int width, int height,
            double x, double y,
            double radius, int amount,
            int tool, int target, double softness)
            => TerrainSculptMath.SculptToolSample(original, draft, weights, width, height, x, y, radius, amount, tool, target, softness);

        /// <summary>Convenience overload: raise/lower, no softness override.</summary>
        public static void SculptSample(
            uint[] original, uint[] draft, double[] weights,
            int width, int height,
            double x, double y,
            double radius, int amount)
            => TerrainSculptMath.SculptSample(original, draft, weights, width, height, x, y, radius, amount);

        /// <summary>Convenience overload: any tool, default softness.</summary>
        public static void SculptToolSample(
            uint[] original, uint[] draft, double[] weights,
            int width, int height,
            double x, double y,
            double radius, int amount,
            int tool, int target)
            => TerrainSculptMath.SculptToolSample(original, draft, weights, width, height, x, y, radius, amount, tool, target);

        // ═════════════════════════════════════════════════════════════════
        // Stored height → world height
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Maps a stored 0–255 height byte to a world-space Z value.</summary>
        public double StoredHeightToWorld(int storedHeight)
        {
            return terrainProvider.HeightMinimum
                 + storedHeight / 255.0 * (terrainProvider.HeightMaximum - terrainProvider.HeightMinimum);
        }

        public string TerrainName => terrainName;

        public int? WaterStoredHeight
        {
            get
            {
                if (objectWaterZ.HasValue && terrainProvider != null
                    && terrainProvider.HeightMaximum > terrainProvider.HeightMinimum)
                {
                    double range = terrainProvider.HeightMaximum - terrainProvider.HeightMinimum;
                    int val = (int)Math.Round((objectWaterZ.Value - terrainProvider.HeightMinimum) / range * 255.0);
                    return Math.Max(0, Math.Min(255, val));
                }
                return null;
            }
        }

        public void ExportHeightmap(string path)
        {
            int w = map.Width;
            int h = map.Height;
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte b = (byte)(map.Cells[y * w + x] & 0xFF);
                        bmp.SetPixel(x, y, Color.FromArgb(b, b, b));
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public void ImportHeightmap(string path)
        {
            using (var bmp = new Bitmap(path))
            {
                int w = map.Width;
                int h = map.Height;
                Bitmap src = bmp;
                Bitmap resized = null;
                try
                {
                    if (bmp.Width != w || bmp.Height != h)
                    {
                        resized = new Bitmap(bmp, new Size(w, h));
                        src = resized;
                    }

                    uint[] newCells = (uint[])map.Cells.Clone();
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color c = src.GetPixel(x, y);
                            int lum = (int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                            byte val = (byte)Math.Max(0, Math.Min(255, lum));
                            int idx = y * w + x;
                            newCells[idx] = (newCells[idx] & ~0xFFu) | val;
                        }
                    }

                    if (CommitTerrain != null)
                        SyncPlacements(CommitTerrain(newCells));
                    else
                    {
                        var copy = map.Clone();
                        copy.Cells = newCells;
                        SyncPlacements(copy);
                    }
                    Changed();
                }
                finally
                {
                    resized?.Dispose();
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Hover readout
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates <see cref="sculptHover"/>, <see cref="sculptX"/>,
        /// <see cref="sculptY"/>, and <see cref="TerrainReadout"/>. Called
        /// during mouse movement; internally throttled to 10 Hz for the readout
        /// only.
        /// </summary>
        internal void PreviewSculptHover(int x, int y)
        {
            lastHeightReadout = -1000;
            HoverSculpt(x, y);
        }

        private void HoverSculpt(int x, int y)
        {
            sculptHover = GroundAt(x, y, out Point3D hit);
            if (sculptHover)
            {
                sculptX = hit.X / 40;
                sculptY = hit.Y / 40;
            }

            long now = inputClock.ElapsedMilliseconds;
            if (now - lastHeightReadout >= 100)
            {
                lastHeightReadout = now;
                if (sculptHover)
                {
                    int sx = Math.Max(0, Math.Min(map.Width  - 1, (int)Math.Round(sculptX * (map.Width  - 1) / map.Width)));
                    int sy = Math.Max(0, Math.Min(map.Height - 1, (int)Math.Round(sculptY * (map.Height - 1) / map.Height)));
                    TerrainReadout = string.Format(
                        CultureInfo.InvariantCulture,
                        "Cursor world height {0:F1} | nearest stored sample ({1}, {2}): {3}/255{4}",
                        hit.Z, sx, sy, map.Cells[sy * map.Width + sx] & 0xFF,
                        sculptCells != null ? " | stroke pending release" : "");
                }
                else
                {
                    TerrainReadout = "Cursor outside terrain.";
                }
                TerrainHoverChanged?.Invoke(this, EventArgs.Empty);
            }

            Invalidate();
        }

        // ═════════════════════════════════════════════════════════════════
        // Stroke lifecycle
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Samples the terrain height at the given screen coordinates into
        /// <see cref="FlattenHeight"/> (Alt+Click in Terrain mode).
        /// </summary>
        public bool SampleHeightAt(int screenX, int screenY)
        {
            HoverSculpt(screenX, screenY);
            if (!sculptHover) return false;

            int h = TerrainViewer.GridHeight(
                map.Cells, map.Width, map.Height,
                Math.Max(0, Math.Min(map.Width, (int)Math.Round(sculptX))),
                Math.Max(0, Math.Min(map.Height, (int)Math.Round(sculptY))));
            FlattenHeight = Math.Max(0, Math.Min(255, h));
            SampleFlatten = false;
            TerrainSettingsChanged?.Invoke(this, EventArgs.Empty);
            Changed();
            return true;
        }

        public void AdjustTerrainBrushRadius(int delta)
        {
            BrushRadius = Math.Max(1, Math.Min(16, BrushRadius + delta));
            TerrainSettingsChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }

        /// <summary>
        /// Carves a linear ramp/slope from (x0, y0, h0) to (x1, y1, h1) across a corridor
        /// of half-width <paramref name="radius"/> cells, blending with <paramref name="softness"/>.
        /// </summary>
        public static uint[] ApplyRamp(
            uint[] original, int width, int height,
            double x0, double y0, int h0,
            double x1, double y1, int h1,
            double radius, double softness)
        {
            var draft = (uint[])original.Clone();
            double dx = x1 - x0;
            double dy = y1 - y0;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-4 || radius <= 0) return draft;

            for (int iy = 0; iy < height; iy++)
            {
                for (int ix = 0; ix < width; ix++)
                {
                    double gx = width == 1 ? 0 : ix * width / (double)(width - 1);
                    double gy = height == 1 ? 0 : iy * height / (double)(height - 1);

                    // Project (gx, gy) onto segment (x0, y0) -> (x1, y1)
                    double t = ((gx - x0) * dx + (gy - y0) * dy) / lenSq;
                    if (t < 0 || t > 1) continue;

                    double px = x0 + t * dx;
                    double py = y0 + t * dy;
                    double dist = Math.Sqrt((gx - px) * (gx - px) + (gy - py) * (gy - py));
                    if (dist >= radius) continue;

                    double weight = BrushWeight(dist, radius, softness);
                    double rampH = h0 + t * (h1 - h0);
                    int at = iy * width + ix;
                    int oldH = (int)(original[at] & 0xFF);
                    int newH = Math.Max(0, Math.Min(255, (int)Math.Round(oldH + (rampH - oldH) * weight)));
                    draft[at] = (original[at] & 0xFFFFFF00u) | (uint)newH;
                }
            }
            return draft;
        }

        /// <summary>
        /// Starts a new stroke if the cursor is over terrain. Snapshots the
        /// tool settings for the whole stroke, so changing a slider mid-drag
        /// doesn't change the stroke's behavior.
        /// </summary>
        private void BeginSculpt(int x, int y)
        {
            CancelPlacement();
            HoverSculpt(x, y);
            if (!sculptHover || CommitTerrain == null) return;

            if (SculptTool == 4)
            {
                int sampled = TerrainViewer.GridHeight(
                    map.Cells, map.Width, map.Height,
                    Math.Max(0, Math.Min(map.Width, (int)Math.Round(sculptX))),
                    Math.Max(0, Math.Min(map.Height, (int)Math.Round(sculptY))));

                if (!rampStartActive)
                {
                    rampStartActive = true;
                    rampStartX = sculptX;
                    rampStartY = sculptY;
                    rampStartHeight = sampled;
                    Invalidate();
                }
                else
                {
                    var ramped = ApplyRamp(
                        map.Cells, map.Width, map.Height,
                        rampStartX, rampStartY, rampStartHeight,
                        sculptX, sculptY, sampled,
                        BrushRadius, BrushSoftness);
                    rampStartActive = false;
                    if (!ramped.SequenceEqual(map.Cells))
                        SyncPlacements(CommitTerrain(ramped));
                    Invalidate();
                }
                return;
            }

            rampStartActive = false;
            strokeRadius   = BrushRadius;
            strokeSoftness = BrushSoftness;
            strokeStrength = BrushStrength;
            strokeTool     = SculptTool;

            strokeTarget = SculptTool == 3
                ? ++noiseStrokeSeed
                : SampleFlatten
                    ? TerrainViewer.GridHeight(
                        map.Cells, map.Width, map.Height,
                        Math.Max(0, Math.Min(map.Width,  (int)Math.Round(sculptX))),
                        Math.Max(0, Math.Min(map.Height, (int)Math.Round(sculptY))))
                    : FlattenHeight;

            strokeX = sculptX;
            strokeY = sculptY;

            sculptCells   = (uint[])map.Cells.Clone();
            sculptWeights = new double[map.Cells.Length]; // zero-initialized on purpose; see SculptToolSample
            sculptSign    = (BrushLower ? -1 : 1) * ((ModifierKeys & Keys.Shift) != 0 ? -1 : 1);

            StampSculpt();
        }

        /// <summary>
        /// Applies the brush at the current <see cref="sculptX"/>/<see cref="sculptY"/>,
        /// interpolating from the previous stamp position so fast drags do not
        /// leave gaps.
        /// </summary>
        private void StampSculpt()
        {
            if (sculptCells == null || !sculptHover) return;

            double dx = sculptX - strokeX;
            double dy = sculptY - strokeY;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) * 4)); // 4 samples per cell

            for (int i = 1; i <= steps; i++)
            {
                SculptToolSample(
                    map.Cells, sculptCells, sculptWeights,
                    map.Width, map.Height,
                    strokeX + dx * i / steps,
                    strokeY + dy * i / steps,
                    strokeRadius,
                    sculptSign * strokeStrength,
                    strokeTool,
                    strokeTarget,
                    strokeSoftness);
            }

            strokeX = sculptX;
            strokeY = sculptY;
        }

        /// <summary>Called by <see cref="CancelEditingAction"/> and by tile-mode switches.</summary>
        public void EndTerrainStroke()
        {
            rampStartActive = false;
            EndSculpt(commit: false);
        }

        private void EndSculpt(bool commit)
        {
            if (sculptCells == null) return;

            var cells = sculptCells;
            sculptCells = null;
            sculptWeights = null;

            if (commit && !cells.SequenceEqual(map.Cells))
                SyncPlacements(CommitTerrain(cells));

            Invalidate();
        }

        // ═════════════════════════════════════════════════════════════════
        // Scene rebuild
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Replaces the current map with a fresh copy that has new height
        /// cells. Preserves the current selection, rebuilds the WPF scene,
        /// prunes unreferenced GL textures, and rebuilds the GL display list.
        /// </summary>
        /// <remarks>
        /// This deliberately does NOT call <c>BuildScene()</c> unconditionally;
        /// it calls <see cref="PruneTerrainTextures"/> first so the ground
        /// material cache (which may have new bitmaps after a blend change)
        /// stays coherent.
        /// </remarks>
        private void RebuildTerrain(Map copy)
        {
            int previouslySelected = SelectedIndex;

            scene = terrainProvider.UpdateHeightScene(copy.Cells);
            if (glContext != IntPtr.Zero)
                wglMakeCurrent(deviceContext, glContext);

            sceneryIds.Clear();
            SceneryCount = 0;
            SceneryReport.Clear();

            map = copy;
            LoadScenery(assetRoot);
            selectionState.SetSelection(previouslySelected >= 0 ? new[] { previouslySelected } : null, map.Objects.Count);
            pickIndex.Invalidate();

            if (glContext != IntPtr.Zero)
            {
                PruneTerrainTextures();
                BuildScene();
            }
        }

        /// <summary>
        /// Deletes any GL textures no longer referenced by a scene material.
        /// Called after a terrain rebuild; the set of bitmaps in use can change
        /// when ground blending is toggled, so some cached textures become dead.
        /// </summary>
        private void PruneTerrainTextures()
        {
            var used = new HashSet<System.Windows.Media.Imaging.BitmapSource>();
            foreach (var child in scene.Children)
            {
                var g = child as GeometryModel3D;
                var m = g?.Material as DiffuseMaterial;
                var b = m?.Brush as System.Windows.Media.ImageBrush;
                if (b != null && b.ImageSource is System.Windows.Media.Imaging.BitmapSource source)
                    used.Add(source);
            }

            foreach (var key in textures.Keys.ToArray())
            {
                if (used.Contains(key)) continue;
                uint id = textures[key];
                glDeleteTextures(1, ref id);
                textures.Remove(key);
            }
        }

        /// <summary>
        /// Local binding to glDeleteTextures, kept here because only the
        /// terrain sculptor prunes textures. Moving this to GlTerrainWindow
        /// requires moving the DllImport too, or the compiler emits a
        /// duplicate import.
        /// </summary>
        [DllImport("opengl32.dll")]
        private static extern void glDeleteTextures(int n, ref uint texture);

        // ═════════════════════════════════════════════════════════════════
        // Brush ring overlay
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draws the brush preview ring at the cursor.
        /// </summary>
        /// <remarks>
        /// The ring is drawn with 3D coordinates but with depth test disabled,
        /// so it always shows through terrain. That is deliberate: an editor
        /// cursor should not disappear behind a hill.
        /// </remarks>
        private void DrawSculpt()
        {
            if (!TerrainMode || !sculptHover) return;

            glDisable(GL_TEXTURE_2D);
            glDisable(GL_FOG);
            glDisable(GL_DEPTH_TEST);
            glColor4f(1, 0.7f, 0.1f, 1);
            glBegin(GL_LINES);

            for (int i = 0; i < 64; i++)
            {
                double a = i * Math.PI / 32;
                double x = sculptX + BrushRadius * Math.Cos(a);
                double y = sculptY + BrushRadius * Math.Sin(a);

                int gx = Math.Max(0, Math.Min(map.Width, (int)Math.Round(x)));
                int gy = Math.Max(0, Math.Min(map.Height, (int)Math.Round(y)));

                routeHeights.TryGetValue(gx + "," + gy, out double z);
                glVertex3f((float)(x * 40), (float)(y * 40), (float)(z + 3));
            }
            glEnd();

            // If Ramp tool start point is active, draw the ramp corridor preview
            if (SculptTool == 4 && rampStartActive)
            {
                int gx0 = Math.Max(0, Math.Min(map.Width, (int)Math.Round(rampStartX)));
                int gy0 = Math.Max(0, Math.Min(map.Height, (int)Math.Round(rampStartY)));
                int gx1 = Math.Max(0, Math.Min(map.Width, (int)Math.Round(sculptX)));
                int gy1 = Math.Max(0, Math.Min(map.Height, (int)Math.Round(sculptY)));
                routeHeights.TryGetValue(gx0 + "," + gy0, out double z0);
                routeHeights.TryGetValue(gx1 + "," + gy1, out double z1);

                double dx = sculptX - rampStartX;
                double dy = sculptY - rampStartY;
                double len = Math.Sqrt(dx * dx + dy * dy);

                glColor4f(0.2f, 1.0f, 0.5f, 1.0f);
                glBegin(GL_LINES);
                // Center line
                glVertex3f((float)(rampStartX * 40), (float)(rampStartY * 40), (float)(z0 + 4));
                glVertex3f((float)(sculptX * 40), (float)(sculptY * 40), (float)(z1 + 4));

                if (len > 1e-3)
                {
                    double nx = -dy / len * BrushRadius;
                    double ny = dx / len * BrushRadius;
                    // Corridor side edges
                    glVertex3f((float)((rampStartX + nx) * 40), (float)((rampStartY + ny) * 40), (float)(z0 + 4));
                    glVertex3f((float)((sculptX + nx) * 40), (float)((sculptY + ny) * 40), (float)(z1 + 4));

                    glVertex3f((float)((rampStartX - nx) * 40), (float)((rampStartY - ny) * 40), (float)(z0 + 4));
                    glVertex3f((float)((sculptX - nx) * 40), (float)((sculptY - ny) * 40), (float)(z1 + 4));

                    // Start cap
                    glVertex3f((float)((rampStartX - nx) * 40), (float)((rampStartY - ny) * 40), (float)(z0 + 4));
                    glVertex3f((float)((rampStartX + nx) * 40), (float)((rampStartY + ny) * 40), (float)(z0 + 4));

                    // End cap
                    glVertex3f((float)((sculptX - nx) * 40), (float)((sculptY - ny) * 40), (float)(z1 + 4));
                    glVertex3f((float)((sculptX + nx) * 40), (float)((sculptY + ny) * 40), (float)(z1 + 4));
                }
                glEnd();
            }

            glEnable(GL_DEPTH_TEST);
        }
    }

    /// <summary>
    /// The Terrain tab of the OpenGL workspace. Owns the brush slider UI and
    /// keeps the controls in sync with the current brush state.
    /// </summary>
    public sealed partial class GlTerrainWindow
    {
        private void SetupSculptTab(TabControl tabs)
        {
            var tab = new TabPage("Terrain");
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(5) };
            tab.Controls.Add(flow);
            tabs.TabPages.Add(tab);

            var active   = new CheckBox { Text = "Sculpt terrain", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
            var lower    = new CheckBox { Text = "Lower", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
            var tool     = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(3, 2, 4, 2) };
            tool.Items.AddRange(new object[]
            {
                "Raise / lower",
                "Level / flatten",
                "Smooth",
                "Noise / roughen",
                "Ramp / slope (2-click)",
            });
            tool.SelectedIndex = 0;

            var radius   = new NumericUpDown { Minimum = 1,   Maximum = 16,  Value = 2,   Width = 50, Margin = new Padding(1, 2, 6, 2) };
            var strength = new NumericUpDown { Minimum = 1,   Maximum = 255, Value = 8,   Width = 50, Margin = new Padding(1, 2, 6, 2) };
            var softness = new NumericUpDown { Minimum = 0,   Maximum = 100, Value = 100, Increment = 10, Width = 50, Margin = new Padding(1, 2, 6, 2) };

            flow.Controls.Add(active);
            flow.Controls.Add(tool);
            flow.Controls.Add(lower);
            flow.Controls.Add(new Label { Text = "Radius (cells)", AutoSize = true, Padding = new Padding(0, 5, 0, 0), Margin = new Padding(3, 2, 1, 2) });
            flow.Controls.Add(radius);
            flow.Controls.Add(new Label { Text = "Strength", AutoSize = true, Padding = new Padding(0, 5, 0, 0), Margin = new Padding(3, 2, 1, 2) });
            flow.Controls.Add(strength);
            flow.Controls.Add(new Label { Text = "Soft edge %", AutoSize = true, Padding = new Padding(0, 5, 0, 0), Margin = new Padding(3, 2, 1, 2) });
            flow.Controls.Add(softness);

            var sample      = new CheckBox { Text = "Sample start height", Checked = true, AutoSize = true, Visible = false, Margin = new Padding(3, 4, 3, 2) };
            var targetLabel = new Label { Text = "Target (0–255)", AutoSize = true, Visible = false, Padding = new Padding(0, 5, 0, 0), Margin = new Padding(3, 2, 1, 2) };
            var target      = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 128, Width = 55, Visible = false, Enabled = false, Margin = new Padding(1, 2, 6, 2) };
            var worldLabel  = new Label { AutoSize = true, Visible = false, Padding = new Padding(0, 5, 0, 0), Margin = new Padding(3, 2, 1, 2) };

            flow.Controls.Add(sample);
            flow.Controls.Add(targetLabel);
            flow.Controls.Add(target);
            flow.Controls.Add(worldLabel);

            Action updateTargetText = () =>
            {
                worldLabel.Text = sample.Checked
                    ? "Target picked on mouse-down"
                    : string.Format(CultureInfo.InvariantCulture,
                                    "World target: {0:F1}",
                                    Preview.StoredHeightToWorld((int)target.Value));
            };

            tool.SelectedIndexChanged += (s, e) =>
            {
                Preview.EndTerrainStroke();
                Preview.SculptTool = tool.SelectedIndex;
                lower.Enabled = tool.SelectedIndex == 0;
                strength.Enabled = tool.SelectedIndex != 4;
                sample.Visible = target.Visible = targetLabel.Visible = worldLabel.Visible = tool.SelectedIndex == 1;
                updateTargetText();
            };
            sample.CheckedChanged += (s, e) =>
            {
                Preview.SampleFlatten = sample.Checked;
                target.Enabled = !sample.Checked;
                updateTargetText();
            };
            target.ValueChanged += (s, e) =>
            {
                Preview.FlattenHeight = (int)target.Value;
                updateTargetText();
            };
            Preview.TerrainSettingsChanged += (s, e) =>
            {
                int r = Math.Max((int)radius.Minimum, Math.Min((int)radius.Maximum, (int)Math.Round(Preview.BrushRadius)));
                if ((int)radius.Value != r) radius.Value = r;
                if (sample.Checked != Preview.SampleFlatten) sample.Checked = Preview.SampleFlatten;
                int t = Math.Max((int)target.Minimum, Math.Min((int)target.Maximum, Preview.FlattenHeight));
                if ((int)target.Value != t) target.Value = t;
                updateTargetText();
            };

            var undo = new Button { Text = "Undo", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            undo.Click += (s, e) => Preview.HistoryMove(false);
            flow.Controls.Add(undo);

            var redo = new Button { Text = "Redo", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            redo.Click += (s, e) => Preview.HistoryMove(true);
            flow.Controls.Add(redo);

            var presetsBtn = new Button { Text = "Level Presets ▾", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            var presetsMenu = new ContextMenuStrip();
            var presetWater = new ToolStripMenuItem("Water Level (Carve lake / sea)", null, (s, e) =>
            {
                int? wh = Preview.WaterStoredHeight;
                if (wh.HasValue)
                {
                    tool.SelectedIndex = 1;
                    sample.Checked = false;
                    target.Value = wh.Value;
                }
                else
                {
                    MessageBox.Show(this, "This map has no water plane defined.", "Terrain Presets");
                }
            });
            var presetLow = new ToolStripMenuItem("Lowland (Height 64)", null, (s, e) =>
            {
                tool.SelectedIndex = 1;
                sample.Checked = false;
                target.Value = 64;
            });
            var presetMid = new ToolStripMenuItem("Mid-Plains (Height 128)", null, (s, e) =>
            {
                tool.SelectedIndex = 1;
                sample.Checked = false;
                target.Value = 128;
            });
            var presetHigh = new ToolStripMenuItem("Highland / Mountain (Height 192)", null, (s, e) =>
            {
                tool.SelectedIndex = 1;
                sample.Checked = false;
                target.Value = 192;
            });
            var presetFloor = new ToolStripMenuItem("Absolute Min (Height 0)", null, (s, e) =>
            {
                tool.SelectedIndex = 1;
                sample.Checked = false;
                target.Value = 0;
            });
            var presetCeil = new ToolStripMenuItem("Absolute Max (Height 255)", null, (s, e) =>
            {
                tool.SelectedIndex = 1;
                sample.Checked = false;
                target.Value = 255;
            });
            presetsMenu.Items.AddRange(new ToolStripItem[] {
                presetWater, new ToolStripSeparator(), presetLow, presetMid, presetHigh, new ToolStripSeparator(), presetFloor, presetCeil
            });
            presetsBtn.Click += (s, e) => presetsMenu.Show(presetsBtn, 0, presetsBtn.Height);
            flow.Controls.Add(presetsBtn);

            var heightmapBtn = new Button { Text = "Heightmap ▾", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            var heightmapMenu = new ContextMenuStrip();
            var menuExportHm = new ToolStripMenuItem("Export Heightmap (PNG)…", null, (s, e) =>
            {
                using (var sfd = new SaveFileDialog
                {
                    Filter = "PNG Image (*.png)|*.png|Bitmap Image (*.bmp)|*.bmp",
                    Title = "Export Terrain Heightmap",
                    FileName = (string.IsNullOrEmpty(Preview.TerrainName) ? "map" : Preview.TerrainName) + "_heightmap.png"
                })
                {
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        try
                        {
                            Preview.ExportHeightmap(sfd.FileName);
                            MessageBox.Show(this, "Heightmap successfully exported to:\r\n" + sfd.FileName, "Export Heightmap");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, "Export failed: " + ex.Message, "Error");
                        }
                    }
                }
            });
            var menuImportHm = new ToolStripMenuItem("Import Heightmap (PNG / BMP)…", null, (s, e) =>
            {
                using (var ofd = new OpenFileDialog
                {
                    Filter = "Image Files (*.png;*.bmp;*.jpg)|*.png;*.bmp;*.jpg|All Files (*.*)|*.*",
                    Title = "Import Terrain Heightmap"
                })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        if (MessageBox.Show(this,
                            "Importing a heightmap will replace all terrain elevations on the map.\r\nExisting objects and routes will remain.\r\n\r\nContinue?",
                            "Confirm Heightmap Import",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            try
                            {
                                Preview.ImportHeightmap(ofd.FileName);
                                MessageBox.Show(this, "Heightmap successfully applied to terrain.", "Import Heightmap");
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(this, "Import failed: " + ex.Message, "Error");
                            }
                        }
                    }
                }
            });
            heightmapMenu.Items.AddRange(new ToolStripItem[] { menuExportHm, menuImportHm });
            heightmapBtn.Click += (s, e) => heightmapMenu.Show(heightmapBtn, 0, heightmapBtn.Height);
            flow.Controls.Add(heightmapBtn);

            var readout = new Label
            {
                Text = Preview.TerrainReadout,
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(6, 4, 0, 0),
            };
            tab.Controls.Add(readout);
            flow.BringToFront();
            Preview.TerrainHoverChanged += (s, e) => readout.Text = Preview.TerrainReadout;

            var tip = new ToolTip();
            tip.SetToolTip(active,
                "Drag to brush; terrain updates on release. Escape cancels. " +
                "Shift reverses raise/lower. Right-drag pans. One stroke = one undo.");
            tip.SetToolTip(softness,
                "0%: firm, uniform brush. 100%: gradual falloff from centre to edge.");
            tip.SetToolTip(strength,
                "Maximum stored-height change per stroke. Level with strength 255 " +
                "and soft edge 0% reaches the chosen height in one stroke.");
            Disposed += (s, e) => tip.Dispose();

            // Wire the checkbox to the global editing-mode switcher.
            RegisterEditingCheck(active, EditingMode.Terrain);

            lower.CheckedChanged      += (s, e) => Preview.BrushLower    = lower.Checked;
            radius.ValueChanged       += (s, e) => Preview.BrushRadius   = (double)radius.Value;
            strength.ValueChanged     += (s, e) => Preview.BrushStrength = (int)strength.Value;
            softness.ValueChanged     += (s, e) => Preview.BrushSoftness = (double)softness.Value / 100;

            // Leaving the Terrain tab turns the brush off so the user does not
            // accidentally sculpt while intending to place objects.
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedTab != tab) active.Checked = false;
            };

            updateTargetText();
        }
    }
}