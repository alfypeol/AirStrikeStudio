using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Forms.Integration;
using Forms = System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Read-only 3D viewport for a single model (or a full assembly) with
    /// orbit / zoom / attachment toggle. Never modifies any file.
    /// </summary>
    public sealed class ModelViewer : Forms.Form
    {
        private readonly Viewport3D         viewport = new Viewport3D();
        private readonly Grid               surface  = new Grid();
        private readonly PerspectiveCamera  camera   = new PerspectiveCamera();
        private readonly ModelVisual3D      visual   = new ModelVisual3D();

        private Point3D center;
        private double  radius = 1, yaw = -35, pitch = 25, distance = 3;
        private Point   last;
        private bool    dragging;

        public int VertexCount, TriangleCount;

        private AssemblyScene assembly;
        private readonly Forms.Label   caption     = new Forms.Label();
        private readonly Forms.CheckBox partsToggle;

        public AssemblyScene Assembly => assembly;

        // Assembly constructor: displays the whole attachment tree.
        public ModelViewer(Assets assets, string name) : this(null, null, name)
        {
            assembly = AssemblyScene.Load(assets, name);
            VertexCount   = assembly.Vertices;
            TriangleCount = assembly.Triangles;
            partsToggle.Enabled = true;
            caption.Text = "Drag to rotate • Wheel to zoom • Static parts: " + assembly.PartCount;
            DisplayAssembly();
            Reset();
        }

        // Raw model + texture constructor.
        public ModelViewer(string model, string texture, string name)
        {
            Text = name + " — 3D model preview";
            Width = 920; Height = 720;
            MinimumSize = new System.Drawing.Size(520, 400);
            StartPosition = Forms.FormStartPosition.CenterParent;

            var host = new ElementHost { Dock = Forms.DockStyle.Fill };
            surface.Background = new SolidColorBrush(Color.FromRgb(22, 29, 39));
            surface.Children.Add(viewport);
            host.Child = surface;

            var bar = new Forms.FlowLayoutPanel
            {
                Dock = Forms.DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
                Padding = new Forms.Padding(4),
            };

            var reset = new Forms.Button { Text = "Reset view", AutoSize = true };
            reset.Click += (s, e) => Reset();
            bar.Controls.Add(reset);

            partsToggle = new Forms.CheckBox
            {
                Text = "Attachments",
                Checked = true,
                Enabled = false,
                AutoSize = true,
                Padding = new Forms.Padding(4, 5, 0, 0),
            };
            partsToggle.CheckedChanged += (s, e) => DisplayAssembly();
            bar.Controls.Add(partsToggle);

            var details = new Forms.Button { Text = "Parts report", AutoSize = true };
            details.Click += (s, e) => ShowReport();
            bar.Controls.Add(details);

            caption.Text = "Drag to rotate • Wheel to zoom • Base model only";
            caption.AutoSize = true;
            caption.Padding = new Forms.Padding(8, 6, 0, 0);
            bar.Controls.Add(caption);

            Controls.Add(host);
            Controls.Add(bar);

            camera.FieldOfView = 45;
            camera.UpDirection = new Vector3D(0, 0, 1);
            viewport.Camera = camera;
            viewport.Children.Add(visual);

            if (model != null)
            {
                var part = ReadMesh(model, texture);
                VertexCount = part.Vertices;
                TriangleCount = part.Triangles;
                Display(part.Model);
                Reset();
            }

            surface.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                last = e.GetPosition(surface);
                surface.CaptureMouse();
            };
            surface.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                Point p = e.GetPosition(surface);
                Orbit((p.X - last.X) * 0.5, (p.Y - last.Y) * 0.5);
                last = p;
            };
            surface.MouseLeftButtonUp   += (s, e) => { dragging = false; surface.ReleaseMouseCapture(); };
            surface.LostMouseCapture    += (s, e) => dragging = false;
            surface.MouseWheel          += (s, e) =>
            {
                Zoom(Math.Pow(1.12, -e.Delta / 120.0));
                e.Handled = true;
            };
        }

        private static float F(byte[] b, int p)
        {
            float v = BitConverter.ToSingle(b, p);
            if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 1e8)
                throw new InvalidDataException("Invalid model coordinate.");
            return v;
        }

        /// <summary>
        /// Reads an MDL v2 file into a WPF <see cref="MeshGeometry3D"/> with
        /// the given texture. UVs are flipped (bottom-left → top-left). Each
        /// triangle corner is expanded so seams keep independent UVs.
        /// </summary>
        public static MeshPart ReadMesh(string model, string texture)
        {
            byte[] b = File.ReadAllBytes(model);

            if (b.Length < 120
                || BitConverter.ToUInt32(b, 0) != 0x214C444D
                || BitConverter.ToInt32(b, 4) != 2)
                throw new InvalidDataException("This preview supports MDL! version 2 models.");

            int nv = BitConverter.ToInt32(b, 76);
            int nu = BitConverter.ToInt32(b, 80);
            int nf = BitConverter.ToInt32(b, 84);

            if (nv < 1 || nv > 50000
                || nu < 1 || nu > 100000
                || nf < 1 || nf > 100000)
                throw new InvalidDataException("Invalid mesh counts.");

            long uvLong  = 120L + 12L * nv;
            long triLong = uvLong + 8L * nu;
            if (triLong + 12L * nf > b.Length)
                throw new InvalidDataException("Truncated model geometry.");

            int uv  = (int)uvLong;
            int tri = (int)triLong;

            var vertices = new Point3D[nv];
            double minx = double.MaxValue, miny = minx, minz = minx;
            double maxx = double.MinValue, maxy = maxx, maxz = maxx;

            for (int i = 0; i < nv; i++)
            {
                var p = new Point3D(F(b, 120 + i * 12), F(b, 124 + i * 12), F(b, 128 + i * 12));
                vertices[i] = p;
                minx = Math.Min(minx, p.X); miny = Math.Min(miny, p.Y); minz = Math.Min(minz, p.Z);
                maxx = Math.Max(maxx, p.X); maxy = Math.Max(maxy, p.Y); maxz = Math.Max(maxz, p.Z);
            }

            var mesh = new MeshGeometry3D();

            // Each MDL corner has independent position and UV indices. Expand
            // corners so seams keep their original UVs when passed to WPF's
            // single-index mesh.
            for (int i = 0; i < nf; i++)
            for (int j = 0; j < 3; j++)
            {
                int p  = tri + i * 12;
                int vi = BitConverter.ToUInt16(b, p + j * 2);
                int ti = BitConverter.ToUInt16(b, p + 6 + j * 2);
                if (vi >= nv || ti >= nu)
                    throw new InvalidDataException("Model triangle index is outside its table.");

                // Game UVs use a bottom-left origin; WPF bitmap coordinates
                // use top-left.
                mesh.Positions.Add(vertices[vi]);
                mesh.TextureCoordinates.Add(new Point(F(b, uv + ti * 8), 1 - F(b, uv + ti * 8 + 4)));
                mesh.TriangleIndices.Add(mesh.Positions.Count - 1);
            }

            var bitmap = new BitmapImage();
            using (var image  = Assets.Tga(texture))
            using (var stream = new MemoryStream())
            {
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            var brush = new ImageBrush(bitmap)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
            };
            var material = new DiffuseMaterial(brush);
            var geometry = new GeometryModel3D(mesh, material) { BackMaterial = material };
            geometry.Freeze();

            var part = new MeshPart { Model = geometry, Vertices = nv, Triangles = nf };
            part.ReadTags(b);
            return part;
        }

        private void Display(Model3D model)
        {
            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(150, 150, 150), new Vector3D(-1, 1, -2)));
            group.Children.Add(model);
            group.Freeze();
            visual.Content = group;

            var bounds = model.Bounds;
            center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            radius = Math.Max(0.01, new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2);
        }

        private void DisplayAssembly()
        {
            if (assembly == null) return;
            var group = new Model3DGroup();
            group.Children.Add(assembly.Base);
            if (partsToggle.Checked) group.Children.Add(assembly.Parts);
            Display(group);
            Reset();
        }

        public void SetAttachments(bool visible) { partsToggle.Checked = visible; }

        private void ShowReport()
        {
            using (var f = new Forms.Form
            {
                Text = "Static assembly report",
                Width = 780,
                Height = 540,
                StartPosition = Forms.FormStartPosition.CenterParent,
            })
            {
                var text = new Forms.TextBox
                {
                    Dock = Forms.DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    WordWrap = false,
                    ScrollBars = Forms.ScrollBars.Both,
                };
                text.Text = assembly == null
                    ? "Direct model preview: choose an object from the map or Library to resolve its attachments."
                    : "Static mesh attachments only. Scripts, animation, damage states, sprites, additive lights and night-only parts are not simulated.\r\n\r\n"
                      + string.Join("\r\n", assembly.Report.ToArray());
                f.Controls.Add(text);
                f.ShowDialog(this);
            }
        }

        public void Reset()
        {
            yaw = -35; pitch = 25; distance = radius * 3.4;
            UpdateCamera();
        }

        public void Orbit(double dx, double dy)
        {
            yaw   = (yaw + dx) % 360;
            pitch = Math.Max(-85, Math.Min(85, pitch + dy));
            UpdateCamera();
        }

        public void Zoom(double factor)
        {
            distance = Math.Max(radius * 1.4, Math.Min(radius * 20, distance * factor));
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            double y = yaw   * Math.PI / 180;
            double p = pitch * Math.PI / 180;
            var offset = new Vector3D(
                Math.Sin(y) * Math.Cos(p),
                -Math.Cos(y) * Math.Cos(p),
                Math.Sin(p)) * distance;
            camera.Position       = center + offset;
            camera.LookDirection  = -offset;
            camera.NearPlaneDistance = radius * 0.01;
            camera.FarPlaneDistance  = radius * 50;
        }

        public void Snapshot(string path)
        {
            surface.Width  = 800;
            surface.Height = 600;
            surface.Measure(new Size(800, 600));
            surface.Arrange(new Rect(0, 0, 800, 600));
            surface.UpdateLayout();

            var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);

            surface.Width  = double.NaN;
            surface.Height = double.NaN;
        }
    }
}