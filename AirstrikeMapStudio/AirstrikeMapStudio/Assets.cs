using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AirstrikeStudio
{
    /// <summary>
    /// One object definition parsed from an .obj file.
    /// </summary>
    public class Definition
    {
        public string Name;
        public string Model = "";
        public string Skin  = "";
        public string Script = "";
        public string Source = "";
        public bool Hidden, Additive, OnWater;
        public List<Attachment> Attachments = new List<Attachment>();
        public List<string> AttachmentWarnings = new List<string>();
    }

    /// <summary>
    /// Rendered model thumbnail with the world-space extent (used by the 2D
    /// map to scale the drawing).
    /// </summary>
    public class ModelImage
    {
        public Bitmap Image;
        public float WorldWidth, WorldHeight;
    }

    /// <summary>
    /// Loads object definitions, decodes TGA textures, and renders model
    /// thumbnails.
    /// </summary>
    /// <remarks>
    /// The MDL parser here is the byte-exact reverse-engineering of the
    /// engine's own MDL v2 header layout:
    ///   +0  magic 0x214C444D ("MDL!")
    ///   +4  version 2
    ///   +76 vertex count
    ///   +80 UV count
    ///   +84 face count
    ///   +88 normal count (unused here)
    ///   +92 tag count (unused here)
    ///   +120 vertices (12 bytes each: XYZ floats)
    ///   +120 + 12nv UVs (8 bytes each: U, V floats)
    ///   +120 + 12nv + 8nu triangles (12 bytes each: 3 uint16 vertex
    ///                                    indices, 3 uint16 UV indices)
    ///
    /// Do not change the offsets or the arithmetic. Any change to the
    /// thumbnail renderer changes every 2D map preview in the app.
    /// </remarks>
    public class Assets : IDisposable
    {
        public string Root = "";
        public Dictionary<string, Definition> Definitions =
            new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, ModelImage> images =
            new Dictionary<string, ModelImage>(StringComparer.OrdinalIgnoreCase);

        public List<string> Warnings = new List<string>();

        // ═════════════════════════════════════════════════════════════════
        // Loading
        // ═════════════════════════════════════════════════════════════════

        public void Open(string directory)
        {
            Dispose();
            Root = directory;
            Definitions.Clear();
            Warnings.Clear();

            string folder = Path.Combine(Root, "objects");
            if (!Directory.Exists(folder))
                throw new IOException(
                    "Choose the game's data folder, which contains objects, " +
                    "models, maps and textures.");

            foreach (string file in Directory.GetFiles(folder, "*.obj", SearchOption.AllDirectories))
            {
                // Strip // line comments before parsing.
                string txt = Regex.Replace(File.ReadAllText(file), @"//[^\r\n]*", "");

                // Match: name { ... }
                foreach (Match m in Regex.Matches(txt, @"(?m)^\s*([\w#.-]+)\s*\{([^{}]*)\}"))
                {
                    var d = new Definition
                    {
                        Name   = m.Groups[1].Value,
                        Source = file,
                    };

                    d.Model  = Field(m.Groups[2].Value, "model");
                    d.Skin   = Field(m.Groups[2].Value, "skin");
                    d.Script = Field(m.Groups[2].Value, "script");

                    d.Hidden   = Regex.IsMatch(m.Groups[2].Value, @"(?m)^\s*flag\s+FL_NODRAW\b");
                    d.Additive = Regex.IsMatch(m.Groups[2].Value, @"(?m)^\s*blend\s+BLEND_ADD\b");
                    d.OnWater  = Regex.IsMatch(m.Groups[2].Value, @"(?m)^\s*flag\s+FL_ONWATER(?:_FLAT|_NORMAL)?\b")
                              || m.Groups[2].Value.IndexOf(@"models\ships\", StringComparison.OrdinalIgnoreCase) >= 0
                              || m.Groups[2].Value.IndexOf(@"models/ships/", StringComparison.OrdinalIgnoreCase) >= 0;

                    d.Attachments = Attachment.Parse(m.Groups[2].Value, d.AttachmentWarnings);

                    Definitions[d.Name] = d;
                }
            }
        }

        private static string Field(string s, string f)
        {
            Match m = Regex.Match(s, @"(?m)^\s*" + f + "\\s+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>
        /// Resolves a relative path against <see cref="Root"/>, rejecting
        /// anything outside the root.
        /// </summary>
        public string Resolve(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(
                Root, relative.Replace('/', Path.DirectorySeparatorChar)));

            string prefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Asset path outside data folder.");

            return path;
        }

        // ═════════════════════════════════════════════════════════════════
        // Thumbnail cache
        // ═════════════════════════════════════════════════════════════════

        public ModelImage Get(string name)
        {
            if (images.TryGetValue(name, out var cached)) return cached;

            ModelImage image = null;
            if (Definitions.TryGetValue(name, out var d) && d.Model.Length > 0 && d.Skin.Length > 0)
            {
                try
                {
                    image = RenderModel(Resolve(d.Model), Resolve(d.Skin));
                }
                catch (Exception e)
                {
                    Warnings.Add(name + ": " + e.Message);
                }
            }

            images[name] = image;
            return image;
        }

        public void Dispose()
        {
            foreach (var v in images.Values)
                if (v != null) v.Image.Dispose();
            images.Clear();
        }

        // ═════════════════════════════════════════════════════════════════
        // TGA decoder
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Decodes a TGA file into a Bitmap. Supports types 1, 2, 3, 9, 10,
        /// 11 with 15, 16, 24, or 32 bit color depth.
        /// </summary>
        /// <remarks>
        /// Header layout (offsets from start):
        ///   +0  ID length
        ///   +1  color map type
        ///   +2  image type
        ///   +3  color map first index
        ///   +5  color map length
        ///   +7  color map entry size
        ///   +12 image width
        ///   +14 image height
        ///   +16 image depth
        ///   +17 image descriptor
        /// Data starts at 18 + ID length.
        /// Bit 5 of the descriptor is the V origin (1 = top, 0 = bottom).
        /// Bit 4 is the U origin (1 = right, 0 = left).
        /// </remarks>
        public static Bitmap Tga(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 18)
                throw new InvalidDataException("Short TGA.");

            int id    = b[0];
            int cm    = b[1];
            int type  = b[2];
            int first = BitConverter.ToUInt16(b, 3);
            int num   = BitConverter.ToUInt16(b, 5);
            int cb    = b[7];
            int w     = BitConverter.ToUInt16(b, 12);
            int h     = BitConverter.ToUInt16(b, 14);
            int depth = b[16];

            if (w < 1 || h < 1 || (long)w * h > 16777216)
                throw new InvalidDataException("Invalid TGA dimensions.");

            int p = 18 + id;
            int[] pal = new int[first + num];

            if (cm == 1)
            {
                for (int i = 0; i < num; i++)
                    pal[first + i] = ColorRead(b, ref p, cb);
            }

            if (type != 1 && type != 2 && type != 3
                && type != 9 && type != 10 && type != 11)
                throw new InvalidDataException("Unsupported TGA type " + type);

            int[] pixels = new int[w * h];
            int n = 0;
            bool rle     = type >= 9;
            bool indexed = type == 1 || type == 9;
            bool gray    = type == 3 || type == 11;

            while (n < pixels.Length)
            {
                int count = 1;
                bool repeat = false;

                if (rle)
                {
                    int packet = b[p++];
                    repeat = (packet & 128) != 0;
                    count  = (packet & 127) + 1;
                }

                if (n + count > pixels.Length)
                    throw new InvalidDataException("Invalid TGA packet.");

                int c = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i == 0 || !repeat)
                    {
                        if (indexed)
                        {
                            int v = depth == 8 ? b[p++] : BitConverter.ToUInt16(b, p);
                            if (depth == 16) p += 2;
                            if (v >= pal.Length)
                                throw new InvalidDataException("Bad palette index.");
                            c = pal[v];
                        }
                        else if (gray)
                        {
                            int v = b[p++];
                            int a = depth == 16 ? b[p++] : 255;
                            c = (a << 24) | (v << 16) | (v << 8) | v;
                        }
                        else
                        {
                            c = ColorRead(b, ref p, depth);
                        }
                    }

                    int x = n % w, y = n / w;
                    if ((b[17] & 32) == 0) y = h - 1 - y;       // V origin flip
                    if ((b[17] & 16) != 0) x = w - 1 - x;       // U origin flip
                    pixels[y * w + x] = c;
                    n++;
                }
            }

            return BitmapFrom(w, h, pixels);
        }

        private static int ColorRead(byte[] b, ref int p, int bits)
        {
            if (bits == 24 || bits == 32)
            {
                int blue  = b[p++];
                int green = b[p++];
                int red   = b[p++];
                int a     = bits == 32 ? b[p++] : 255;
                return (a << 24) | (red << 16) | (green << 8) | blue;
            }

            if (bits == 16 || bits == 15)
            {
                int v = BitConverter.ToUInt16(b, p);
                p += 2;
                return unchecked((int)0xFF000000)
                    | (((v >> 10) & 31) * 255 / 31 << 16)
                    | (((v >>  5) & 31) * 255 / 31 <<  8)
                    | (( v        & 31) * 255 / 31);
            }

            throw new InvalidDataException("Unsupported TGA pixel depth.");
        }

        public static Bitmap BitmapFrom(int w, int h, int[] data)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var lockData = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try { Marshal.Copy(data, 0, lockData.Scan0, data.Length); }
            finally { bmp.UnlockBits(lockData); }

            return bmp;
        }

        // ═════════════════════════════════════════════════════════════════
        // Software rasterizer for thumbnails
        // ═════════════════════════════════════════════════════════════════

        private static float F(byte[] b, int p) => BitConverter.ToSingle(b, p);

        /// <summary>
        /// Renders an MDL v2 file to a 128×128 thumbnail with the given
        /// texture. See the class-level remarks for the exact MDL layout.
        /// </summary>
        public static ModelImage RenderModel(string path, string skin)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 120
                || BitConverter.ToUInt32(b, 0) != 0x214C444D
                || BitConverter.ToInt32(b, 4) != 2)
                throw new InvalidDataException("Unsupported MDL header.");

            int nv = BitConverter.ToInt32(b, 76);
            int nu = BitConverter.ToInt32(b, 80);
            int nf = BitConverter.ToInt32(b, 84);

            if (nv < 1 || nv > 50000
                || nu < 1 || nu > 100000
                || nf < 1 || nf > 100000)
                throw new InvalidDataException("Invalid mesh counts.");

            int uv  = 120 + 12 * nv;
            int tri = uv  +  8 * nu;
            if ((long)tri + 12L * nf > b.Length)
                throw new InvalidDataException("Truncated mesh.");

            float[] x = new float[nv];
            float[] y = new float[nv];
            float[] z = new float[nv];
            float minx = float.MaxValue, miny = float.MaxValue;
            float maxx = float.MinValue, maxy = float.MinValue;

            for (int i = 0; i < nv; i++)
            {
                x[i] =  F(b, 120 + i * 12);
                y[i] = -F(b, 124 + i * 12);     // -Y: inverts the model up-axis
                z[i] =  F(b, 128 + i * 12);

                if (float.IsNaN(x[i]) || float.IsNaN(y[i]) || float.IsNaN(z[i]))
                    throw new InvalidDataException("Invalid vertex.");

                minx = Math.Min(minx, x[i]); maxx = Math.Max(maxx, x[i]);
                miny = Math.Min(miny, y[i]); maxy = Math.Max(maxy, y[i]);
            }

            // Fit into a 128×128 thumbnail with 120 units of usable space
            // (4 px margin on each side).
            const int size = 128;
            float dx = Math.Max(1, maxx - minx);
            float dy = Math.Max(1, maxy - miny);
            float scale = 120 / Math.Max(dx, dy);

            for (int i = 0; i < nv; i++)
            {
                x[i] = (x[i] - (minx + maxx) / 2) * scale + 64;
                y[i] = (y[i] - (miny + maxy) / 2) * scale + 64;
            }

            int[]   pixels = new int[size * size];
            float[] zBuf   = new float[pixels.Length];
            for (int i = 0; i < zBuf.Length; i++) zBuf[i] = float.MinValue;

            using (var tex = Tga(skin))
            {
                var data = tex.LockBits(
                    new Rectangle(0, 0, tex.Width, tex.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                int[] colors = new int[tex.Width * tex.Height];
                try { Marshal.Copy(data.Scan0, colors, 0, colors.Length); }
                finally { tex.UnlockBits(data); }

                for (int i = 0; i < nf; i++)
                {
                    int p  = tri + 12 * i;
                    int a  = BitConverter.ToUInt16(b, p);
                    int bb = BitConverter.ToUInt16(b, p + 2);
                    int c  = BitConverter.ToUInt16(b, p + 4);
                    int ta = BitConverter.ToUInt16(b, p + 6);
                    int tb = BitConverter.ToUInt16(b, p + 8);
                    int tc = BitConverter.ToUInt16(b, p + 10);

                    if (a >= nv || bb >= nv || c >= nv
                        || ta >= nu || tb >= nu || tc >= nu)
                        throw new InvalidDataException("Mesh index out of range.");

                    float det =
                        (y[bb] - y[c]) * (x[a] - x[c]) +
                        (x[c] - x[bb]) * (y[a] - y[c]);

                    if (Math.Abs(det) < 0.001) continue;

                    int left   = Math.Max(0, (int)Math.Floor(Math.Min(x[a], Math.Min(x[bb], x[c]))));
                    int right  = Math.Min(127, (int)Math.Ceiling(Math.Max(x[a], Math.Max(x[bb], x[c]))));
                    int top    = Math.Max(0, (int)Math.Floor(Math.Min(y[a], Math.Min(y[bb], y[c]))));
                    int bottom = Math.Min(127, (int)Math.Ceiling(Math.Max(y[a], Math.Max(y[bb], y[c]))));

                    float ua = F(b, uv + ta * 8), va = F(b, uv + ta * 8 + 4);
                    float ub = F(b, uv + tb * 8), vb = F(b, uv + tb * 8 + 4);
                    float uc = F(b, uv + tc * 8), vc = F(b, uv + tc * 8 + 4);

                    for (int py = top; py <= bottom; py++)
                    for (int px = left; px <= right; px++)
                    {
                        float wa = ((y[bb] - y[c]) * (px + 0.5f - x[c])
                                  + (x[c]  - x[bb]) * (py + 0.5f - y[c])) / det;
                        float wb = ((y[c]  - y[a]) * (px + 0.5f - x[c])
                                  + (x[a]  - x[c]) * (py + 0.5f - y[c])) / det;
                        float wc = 1 - wa - wb;

                        if (wa < 0 || wb < 0 || wc < 0) continue;

                        float zz = wa * z[a] + wb * z[bb] + wc * z[c];
                        int at = py * size + px;
                        if (zz < zBuf[at]) continue;

                        int tx = Math.Max(0, Math.Min(tex.Width  - 1,
                            (int)((wa * ua + wb * ub + wc * uc) * (tex.Width  - 1))));
                        int ty = Math.Max(0, Math.Min(tex.Height - 1,
                            (int)((1 - (wa * va + wb * vb + wc * vc)) * (tex.Height - 1))));

                        int color = colors[ty * tex.Width + tx];
                        if ((uint)color >> 24 < 24) continue;

                        pixels[at] = color;
                        zBuf[at] = zz;
                    }
                }
            }

            return new ModelImage
            {
                Image = BitmapFrom(size, size, pixels),
                WorldWidth  = Math.Max(dx, dy) * 128 / 120,
                WorldHeight = Math.Max(dx, dy) * 128 / 120,
            };
        }
    }
}