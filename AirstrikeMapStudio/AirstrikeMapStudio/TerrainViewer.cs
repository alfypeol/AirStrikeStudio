using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

// ── Disambiguation ────────────────────────────────────────────────────
// This file imports both the WPF and the GDI+ type namespaces indirectly
// (WPF via the using directives above, GDI+ via System.Drawing.Bitmap in
// the BlendedGround/Texture helpers). These aliases tell the compiler
// which one "Color", "Point", etc. mean: WPF everywhere, since the file
// is a WPF control.
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using PixelFormat = System.Windows.Media.PixelFormat;
using Bitmap = System.Drawing.Bitmap;
using Rectangle = System.Drawing.Rectangle;

using F = System.Windows.Forms;


namespace AirstrikeStudio
{
    /// <summary>
    /// WPF-side terrain preview. Produces the <see cref="Model3DGroup"/> that
    /// <see cref="GlTerrainControl"/> converts into a GL display list, and
    /// provides the pure terrain math (<see cref="GridHeight"/>,
    /// <see cref="AtlasRectangle"/>) that the OpenGL side calls back into.
    /// </summary>
    /// <remarks>
    /// This file is where the bit-exact reverse engineering lives.
    /// Every constant that looks arbitrary is derived from a specific
    /// AirStrike3D.exe address; the comment on each constant cites it.
    /// If you are tempted to simplify any of:
    ///   <see cref="GridHeight"/>,
    ///   <see cref="BlendChannel"/>,
    ///   <see cref="AtlasRectangle"/>,
    ///   the tile rotation in <see cref="AddTile"/>,
    /// please verify against a real map first. Small changes here make every
    /// existing .hsc render differently.
    /// </remarks>
    public sealed class TerrainViewer : F.Form
    {
        // ═════════════════════════════════════════════════════════════════
        // Water tagging
        // ═════════════════════════════════════════════════════════════════
        // Water is detected by an attached WPF property, not by a dictionary.
        // If you refactor this into a Tag or an instance field, remember
        // GlTerrainControl.BuildScene queries IsWater(mat) on the materials
        // it iterates. The water path there is: skip depth write, apply the
        // wave matrix, and do NOT polygon-offset. Get it wrong and water
        // renders opaque and static.
        // ═════════════════════════════════════════════════════════════════

        private static readonly DependencyProperty WaterSurfaceProperty =
            DependencyProperty.RegisterAttached(
                "WaterSurface", typeof(bool), typeof(TerrainViewer),
                new PropertyMetadata(false));

        public static bool IsWater(Material material)
        {
            return material != null && (bool)material.GetValue(WaterSurfaceProperty);
        }

        // ═════════════════════════════════════════════════════════════════
        // Bit-exact terrain math constants
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Engine terrain block height in cells. Used by the tile-rotation
        /// decoder and the ground texture blender. Engine: block size is 32
        /// rows; see 0x41699E and 0x4185B0.
        /// </summary>
        private const int TerrainBlockRows = 32;

        /// <summary>
        /// Height-to-texture-band divisor. 255 / 3 = 85; engine uses 86.
        /// Do not "correct" to 85. Engine: 0x41699E.
        /// </summary>
        private const float HeightToBandDivisor = 86f;

        /// <summary>
        /// Tile atlas is 64×64 per tile. Engine: 0x416DF6..0x416E64.
        /// </summary>
        private const int TileAtlasCellSize = 64;

        /// <summary>
        /// Number of tiles per atlas row, derived from atlas width.
        /// Not a constant; computed in <see cref="AtlasRectangle"/>.
        /// </summary>

        /// <summary>
        /// 16.16 fixed point denominator. Engine: 0x4185B0.
        /// </summary>
        private const long FixedPoint16_16 = 65536L;

        /// <summary>
        /// Default water opacity when the mission omits the trailing value.
        /// </summary>
        private const double DefaultWaterOpacity = 0.4;

        // ═════════════════════════════════════════════════════════════════
        // Instance state
        // ═════════════════════════════════════════════════════════════════

        private readonly Map map;
        private readonly string mapName;
        private readonly string dataFolder;
        private readonly Grid surface = new Grid();
        private readonly Viewport3D view = new Viewport3D();
        private readonly PerspectiveCamera camera = new PerspectiveCamera();
        private readonly ModelVisual3D visual = new ModelVisual3D();

        private readonly F.NumericUpDown row = new F.NumericUpDown();
        private readonly F.NumericUpDown low = new F.NumericUpDown();
        private readonly F.NumericUpDown high = new F.NumericUpDown();
        private readonly F.CheckBox whole = new F.CheckBox();
        private readonly F.CheckBox tiles = new F.CheckBox { Text = "Tile overlays", Checked = true, AutoSize = true };
        private readonly F.Label note = new F.Label { Dock = F.DockStyle.Bottom, Height = 66 };
        private readonly F.CheckBox water = new F.CheckBox { Text = "Water", AutoSize = true };
        private readonly F.NumericUpDown waterHeight = new F.NumericUpDown { Minimum = -2000, Maximum = 2000, Width = 65 };
        private readonly F.ComboBox ground = new F.ComboBox { DropDownStyle = F.ComboBoxStyle.DropDownList, Width = 150 };
        private readonly F.CheckBox detail = new F.CheckBox { Text = "Ground detail", Checked = true, AutoSize = true };
        private readonly F.Label location = new F.Label { Dock = F.DockStyle.Bottom, Height = 38 };
        private readonly F.CheckBox comparison = new F.CheckBox { Text = "4:3 comparison", AutoSize = true };
        private readonly F.NumericUpDown centerX = new F.NumericUpDown { DecimalPlaces = 2, Minimum = -10000, Maximum = 10000, Width = 75 };
        private readonly F.NumericUpDown centerY = new F.NumericUpDown { DecimalPlaces = 2, Minimum = -10000, Maximum = 10000, Width = 75 };
        private readonly F.CheckBox missionLight = new F.CheckBox { Text = "Mission light (preview)", Checked = true, AutoSize = true };

        private readonly Dictionary<string, Material> textureMaterials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Material> groundBlocks = new Dictionary<string, Material>();
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        private double waterOpacity = 0.65;
        private string biome = "";
        private string waterPath = "";
        private string environment = "Manual environment";

        private readonly string environmentOverride;
        private Color sunColor = Colors.White;
        private Color ambientColor = Color.FromRgb(110, 110, 110);
        private Vector3D sunDirection = new Vector3D(-1, 1, -2);
        private bool hasSun;

        private bool gameCamera;
        private double yaw = -60, pitch = 55, distance = 2400, cx, cy;
        private Point lastDragPoint;
        private bool dragActive;
        private bool panActive;

        public int VertexCount { get; private set; }
        public int TriangleCount { get; private set; }
        public double MinHeight { get; private set; }
        public double MaxHeight { get; private set; }
        public int OverlayCount { get; private set; }
        public int SkippedTiles { get; private set; }

        public double HeightMinimumSetting => (double)low.Value;
        public double HeightMinimum => (double)low.Value;
        public double HeightMaximum => (double)high.Value;
        public double CameraDistance => distance;
        public string CameraReadout => location.Text;

        public Color SunColor => sunColor;
        public Color AmbientColor => ambientColor;
        public Vector3D SunVector => -sunDirection;

        // ═════════════════════════════════════════════════════════════════
        // Constructors
        // ═════════════════════════════════════════════════════════════════

        public TerrainViewer(Map source, string name)
            : this(source, name, Path.Combine(@"E:\Air Strike 3D", "data")) { }

        public TerrainViewer(Map source, string name, string dataFolder, string environmentOverride = null)
        {
            this.mapName = name;
            this.dataFolder = dataFolder;
            this.environmentOverride = environmentOverride;
            map = Map.Load(source.Bytes());

            Text = name + " — Legacy terrain preview";
            Width = 1100;
            Height = 780;
            MinimumSize = new System.Drawing.Size(700, 500);

            var host = new ElementHost { Dock = F.DockStyle.Fill };
            surface.Background = new SolidColorBrush(Color.FromRgb(20, 28, 38));
            surface.Children.Add(view);
            host.Child = surface;
            Controls.Add(host);

            var bar = new F.FlowLayoutPanel { Dock = F.DockStyle.Top, Height = 42, AutoSize = true };
            Controls.Add(bar);

            BuildToolbar(bar);

            Controls.Add(note);
            Controls.Add(location);

            whole.CheckedChanged += (s, e) => row.Enabled = !whole.Checked;

            view.Camera = camera;
            camera.FieldOfView = 45;
            camera.UpDirection = new Vector3D(0, 0, 1);
            view.Children.Add(visual);
            view.SizeChanged += (s, e) => UpdateProjection();

            surface.Focusable = true;
            surface.MouseEnter += (s, e) => { host.Focus(); surface.Focus(); };
            surface.MouseDown += OnSurfaceMouseDown;
            surface.MouseUp += OnSurfaceMouseUp;
            surface.LostMouseCapture += (s, e) => dragActive = false;
            surface.MouseMove += OnSurfaceMouseMove;

            surface.AddHandler(
                System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                new System.Windows.Input.MouseWheelEventHandler(OnSurfaceMouseWheel),
                true);

            Build();
        }

        private void BuildToolbar(F.FlowLayoutPanel bar)
        {
            var zoomIn = new F.Button { Text = "Zoom +", AutoSize = true };
            var zoomOut = new F.Button { Text = "Zoom −", AutoSize = true };
            zoomIn.Click += (s, e) => Zoom(1 / 1.25);
            zoomOut.Click += (s, e) => Zoom(1.25);
            bar.Controls.Add(zoomIn);
            bar.Controls.Add(zoomOut);

            bar.Controls.Add(new F.Label { Text = "Section Y", AutoSize = true, Padding = new F.Padding(0, 8, 0, 0) });
            row.Maximum = map.Height - 1;
            row.Width = 65;
            bar.Controls.Add(row);

            whole.Text = "Whole map";
            whole.AutoSize = true;
            bar.Controls.Add(whole);
            bar.Controls.Add(tiles);

            foreach (var n in new[] { low, high })
            {
                n.Minimum = -2000;
                n.Maximum = 2000;
                n.Width = 65;
            }
            low.Value = -120;
            high.Value = 130;
            bar.Controls.Add(new F.Label { Text = "Height range", AutoSize = true, Padding = new F.Padding(0, 8, 0, 0) });
            bar.Controls.Add(low);
            bar.Controls.Add(high);

            // Load environment before adding the water controls so water checkbox
            // and water height can reflect the mission.
            LoadEnvironment(mapName, environmentOverride);

            bar.Controls.Add(water);
            bar.Controls.Add(waterHeight);

            ground.Items.Add("Elevation colors");
            if (biome.Length > 0)
            {
                for (int i = 1; i <= 4; i++)
                    ground.Items.Add("Ground texture " + i);
                ground.Items.Add("Blended ground (preview)");
            }
            ground.SelectedIndex = biome.Length > 0 ? 5 : 0;
            bar.Controls.Add(ground);
            bar.Controls.Add(detail);

            var forwardView = new F.Button { Text = "Forward view", AutoSize = true };
            forwardView.Click += (s, e) => ForwardView();
            bar.Controls.Add(forwardView);

            var gameCameraButton = new F.Button { Text = "Game camera (Default)", AutoSize = true };
            gameCameraButton.Click += (s, e) => GameCamera();
            bar.Controls.Add(gameCameraButton);

            var apply = new F.Button { Text = "Apply / reset", AutoSize = true };
            apply.Click += (s, e) =>
            {
                if (high.Value <= low.Value)
                {
                    F.MessageBox.Show("Maximum height must exceed minimum.");
                    return;
                }
                Build();
            };
            bar.Controls.Add(apply);

            bar.Controls.Add(missionLight);

            bar.Controls.Add(comparison);
            comparison.CheckedChanged += (s, e) => FitViewport();
            surface.SizeChanged += (s, e) => FitViewport();

            bar.Controls.Add(new F.Label { Text = "Center X / Y", AutoSize = true, Padding = new F.Padding(0, 8, 0, 0) });
            bar.Controls.Add(centerX);
            bar.Controls.Add(centerY);

            var goCenter = new F.Button { Text = "Go to center", AutoSize = true };
            goCenter.Click += (s, e) => SetViewCenter((double)centerX.Value, (double)centerY.Value);
            bar.Controls.Add(goCenter);
        }

        // ═════════════════════════════════════════════════════════════════
        // Input handlers
        // ═════════════════════════════════════════════════════════════════

        private void OnSurfaceMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            surface.Focus();
            dragActive = true;
            panActive = e.ChangedButton == System.Windows.Input.MouseButton.Right;
            lastDragPoint = e.GetPosition(surface);
            surface.CaptureMouse();
        }

        private void OnSurfaceMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            dragActive = false;
            surface.ReleaseMouseCapture();
        }

        private void OnSurfaceMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!dragActive) return;

            var p = e.GetPosition(surface);
            double dx = p.X - lastDragPoint.X;
            double dy = p.Y - lastDragPoint.Y;

            if (panActive)
            {
                double a = yaw * Math.PI / 180;
                double k = distance / 1000;
                cx += (-Math.Sin(a) * dx + Math.Cos(a) * dy) * k;
                cy += (Math.Cos(a) * dx + Math.Sin(a) * dy) * k;
            }
            else
            {
                yaw += dx * 0.4;
                pitch = Math.Max(10, Math.Min(85, pitch + dy * 0.4));
            }

            lastDragPoint = p;
            ApplyCamera();
        }

        private void OnSurfaceMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            Zoom(Math.Pow(1.15, -e.Delta / 120.0));
            e.Handled = true;
        }

        // ═════════════════════════════════════════════════════════════════
        // Camera
        // ═════════════════════════════════════════════════════════════════

        public void GameCamera()
        {
            gameCamera = true;
            SetComparison(true);
            yaw = -90;
            pitch = 45;
            distance = 270 / Math.Sin(Math.PI / 4);
            ApplyCamera();
        }

        public void ForwardView()
        {
            gameCamera = false;
            yaw = -90;
            pitch = 55;
            distance = map.Width * 40 * 1.5;
            ApplyCamera();
        }

        public void Zoom(double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return;
            distance = Math.Max(100, Math.Min(100000, distance * factor));
            ApplyCamera();
        }

        public void SetViewCenter(double x, double y)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) return;
            cx = x * 40;
            cy = y * 40;
            ApplyCamera();
        }

        public void SetComparison(bool enabled)
        {
            comparison.Checked = enabled;
            FitViewport();
        }

        private void ApplyCamera()
        {
            double a = yaw * Math.PI / 180;
            double p = pitch * Math.PI / 180;
            UpdateProjection();

            var center = new Point3D(cx, cy, gameCamera ? 0 : (MinHeight + MaxHeight) / 2);
            camera.Position = center + new Vector3D(
                Math.Cos(a) * Math.Cos(p) * distance,
                Math.Sin(a) * Math.Cos(p) * distance,
                Math.Sin(p) * distance);
            camera.LookDirection = center - camera.Position;
            camera.NearPlaneDistance = 1;
            camera.FarPlaneDistance = 200000;

            location.Text = string.Format(
                CultureInfo.InvariantCulture,
                "View center (grid): X {0:F2}, Y {1:F2}  |  Heading {2:F1}°, tilt {3:F1}°, distance {4:F0}\r\n" +
                "Camera (world): X {5:F1}, Y {6:F1}, Z {7:F1}",
                cx / 40, cy / 40, yaw, pitch, distance,
                camera.Position.X, camera.Position.Y, camera.Position.Z)
                + (gameCamera ? " | Game projection: vertical FOV 60° (static)" : "");
        }

        private void FitViewport()
        {
            if (!comparison.Checked)
            {
                view.Width = double.NaN;
                view.Height = double.NaN;
                view.HorizontalAlignment = HorizontalAlignment.Stretch;
                view.VerticalAlignment = VerticalAlignment.Stretch;
                return;
            }
            double w = surface.ActualWidth, h = surface.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double fitted = Math.Min(w, h * 4 / 3);
            view.HorizontalAlignment = HorizontalAlignment.Center;
            view.VerticalAlignment = VerticalAlignment.Center;
            view.Width = fitted;
            view.Height = fitted * 3 / 4;
        }

        private void UpdateProjection()
        {
            double w = view.ActualWidth, h = view.ActualHeight;
            camera.FieldOfView = gameCamera
                ? HorizontalFov(60, w > 0 && h > 0 ? w / h : 4.0 / 3)
                : 45;
        }

        /// <summary>
        /// Converts a vertical field of view to horizontal for a given aspect.
        /// Used only to emulate the game's 60° horizontal FOV in the
        /// "game camera" mode.
        /// </summary>
        public static double HorizontalFov(double verticalDegrees, double aspect)
        {
            return 2 * Math.Atan(Math.Tan(verticalDegrees * Math.PI / 360) * aspect) * 180 / Math.PI;
        }

        // ═════════════════════════════════════════════════════════════════
        // Terrain sampling — BIT-EXACT
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Bilinearly samples the (W × H) height cell grid at grid position
        /// (x, y) in (W+1) × (H+1) vertex space. Matches engine
        /// 0x4170EF..0x417144.
        /// </summary>
        /// <remarks>
        /// Engine uses fixed-point 16.16 for the sample coordinates:
        ///     fx = x * ((width  - 1) * 65536) / width
        ///     fy = y * ((height - 1) * 65536) / height
        /// Note the divisor is <c>width</c>, not <c>width-1</c>. It looks
        /// off-by-one but is what the engine does, and using width-1 shifts
        /// every sample by half a cell, misaligning the entire mesh against
        /// objects that are placed with the engine's own math.
        /// </remarks>
        public static byte GridHeight(uint[] cells, int width, int height, int x, int y)
        {
            if (width < 1 || height < 1 || cells.Length != width * height
                || x < 0 || x > width || y < 0 || y > height)
                throw new ArgumentOutOfRangeException();

            long fx = (long)x * ((width - 1) * FixedPoint16_16 / width);
            long fy = (long)y * ((height - 1) * FixedPoint16_16 / height);

            int ix = (int)(fx >> 16);
            int iy = (int)(fy >> 16);
            int nx = Math.Min(width - 1, ix + 1);
            int ny = Math.Min(height - 1, iy + 1);

            double tx = (fx & 0xFFFF) / 65536.0;
            double ty = (fy & 0xFFFF) / 65536.0;

            int h00 = (int)(cells[iy * width + ix] & 0xFF);
            int h10 = (int)(cells[iy * width + nx] & 0xFF);
            int h01 = (int)(cells[ny * width + ix] & 0xFF);
            int h11 = (int)(cells[ny * width + nx] & 0xFF);

            int a = (int)(h00 * (1 - tx) + h10 * tx);
            int b = (int)(h01 * (1 - tx) + h11 * tx);
            return (byte)(a * (1 - ty) + b * ty);
        }

        private double Z(int x, int y)
        {
            uint raw = GridHeight(map.Cells, map.Width, map.Height, x, y);
            return (double)low.Value + raw / 255.0 * ((double)high.Value - (double)low.Value);
        }

        // ═════════════════════════════════════════════════════════════════
        // Terrain → texture band blending — BIT-EXACT
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Linear blend between two texture bands for a given stored height.
        /// </summary>
        /// <remarks>
        /// Engine 0x41699E: height / 86 gives a fractional band; the
        /// integer part is the lower band and the fractional part is the
        /// blend weight. 86 is 255/3, i.e. three texture bands across the
        /// 0–255 height range.
        /// </remarks>
        public static byte BlendChannel(byte lower, byte upper, byte height)
        {
            float t = height / HeightToBandDivisor;
            float f = t - (float)Math.Floor(t);
            return (byte)(lower * (1 - f) + upper * f);
        }

        private int H(int x, int y)
        {
            return (int)(map.Cells[y * map.Width + x] & 0xFF);
        }

        /// <summary>
        /// Samples the stored height at (px, py) inside a 32-row block.
        /// Engine 0x4185B0 uses 16.16 fixed point with truncation after
        /// each interpolation step; this matches that.
        /// </summary>
        private byte SampleBlock(int block, int px, int py)
        {
            int fx = px * ((map.Width - 1) * 65536 / 255);
            int fy = py * ((TerrainBlockRows - 1) * 65536 / 255);
            int x = fx >> 16;
            int y = fy >> 16;
            float tx = (fx & 0xFFFF) / 65536f;
            float ty = (fy & 0xFFFF) / 65536f;

            int x1 = Math.Min(x + 1, map.Width - 1);
            int y0 = block * TerrainBlockRows + y;
            int y1 = Math.Min(block * TerrainBlockRows + y + 1, block * TerrainBlockRows + TerrainBlockRows - 1);

            int a = (int)(H(x, y0) * (1.0 - tx) + H(x1, y0) * tx);
            int b = (int)(H(x, y1) * (1.0 - tx) + H(x1, y1) * tx);
            return (byte)(a * (1.0 - ty) + b * ty);
        }

        // ═════════════════════════════════════════════════════════════════
        // Scene construction
        // ═════════════════════════════════════════════════════════════════

        private void Build()
        {
            int start = whole.Checked ? 0 : (int)row.Value;
            int end = whole.Checked ? map.Height : Math.Min(map.Height, start + 48);
            if (start == end && start > 0) start--;

            int step = 1;
            int columns = map.Width + 1;
            int rows = (end - start) / step + 1;

            if ((long)rows * columns > 300000)
                throw new InvalidDataException("Terrain section too large.");

            var mesh = new MeshGeometry3D();
            MinHeight = double.MaxValue;
            MaxHeight = double.MinValue;

            for (int j = 0; j < rows; j++)
            {
                for (int x = 0; x <= map.Width; x++)
                {
                    int y = start + j * step;
                    double z = Z(x, y);
                    MinHeight = Math.Min(MinHeight, z);
                    MaxHeight = Math.Max(MaxHeight, z);
                    mesh.Positions.Add(new Point3D(x * 40, y * 40, z));
                    mesh.TextureCoordinates.Add(new Point(x / 4.0, y / 4.0));
                }
            }

            for (int y = 0; y < rows - 1; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    int a = y * columns + x;
                    int b = a + 1;
                    int c = a + columns;
                    int d = c + 1;
                    foreach (int i in new[] { a, b, d, a, d, c })
                        mesh.TriangleIndices.Add(i);
                }
            }

            VertexCount = mesh.Positions.Count;
            TriangleCount = mesh.TriangleIndices.Count / 3;

            // Elevation color gradient — this is only used when
            // ground.SelectedIndex == 0 (Elevation colors).
            var brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(0, 1);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(35, 64, 76), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(123, 132, 89), 0.4));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(192, 168, 114), 0.7));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(234, 223, 194), 1));

            var elevationMaterial = new DiffuseMaterial(brush);
            var group = new Model3DGroup();

            group.Children.Add(new AmbientLight(
                hasSun && missionLight.Checked ? ambientColor : Color.FromRgb(110, 110, 110)));
            group.Children.Add(new DirectionalLight(
                hasSun && missionLight.Checked ? sunColor : Colors.White,
                hasSun && missionLight.Checked ? sunDirection : new Vector3D(-1, 1, -2)));
            group.Children.Add(new GeometryModel3D(mesh, elevationMaterial) { BackMaterial = elevationMaterial });

            string assetWarning = "";
            ApplyGroundMaterial(group, mesh, start, end, columns, rows, step, ref assetWarning);

            OverlayCount = 0;
            SkippedTiles = 0;
            if (tiles.Checked && step == 1)
            {
                for (int y = start; y < end; y++)
                    for (int x = 0; x < map.Width; x++)
                        AddTile(group, x, y);
            }

            if (water.Checked)
                AddWaterPlane(group, start, end, ref assetWarning);

            note.Text =
                "Read-only • Drag: orbit • Right-drag: pan • Wheel: zoom • Apply/reset updates options\r\n" +
                environment + ". Overlays: " + OverlayCount + "; skipped: " + SkippedTiles + assetWarning + "\r\n" +
                "Ground blend + optional detail; alignment/filtering remain provisional. " +
                "Forward view faces along the map. Mission lighting is approximate; " +
                "fog is pending; water is static. Objects and triangle parity remain pending.";

            visual.Content = group;
            cx = map.Width * 20;
            cy = (start + (rows - 1) * step / 2.0) * 40;
            distance = Math.Max(map.Width * 40, rows * step * 40) * 2.1;
            ApplyCamera();
        }

        private void ApplyGroundMaterial(Model3DGroup group, MeshGeometry3D mesh,
                                         int start, int end, int columns, int rows, int step,
                                         ref string assetWarning)
        {
            if (ground.SelectedIndex == 5)
            {
                try
                {
                    var chunks = new List<GeometryModel3D>();
                    for (int y0 = start; y0 < end;)
                    {
                        int block = y0 / TerrainBlockRows;
                        int y1 = Math.Min(end, (block + 1) * TerrainBlockRows);
                        var tex = BlendedGround(block);
                        var part = new MeshGeometry3D();
                        for (int y = y0; y <= y1; y++)
                        {
                            for (int x = 0; x <= map.Width; x++)
                            {
                                part.Positions.Add(mesh.Positions[(y - start) * columns + x]);
                                part.TextureCoordinates.Add(
                                    new Point(x / (double)map.Width, (y - block * TerrainBlockRows) / (double)TerrainBlockRows));
                            }
                        }
                        for (int y = 0; y < y1 - y0; y++)
                        {
                            for (int x = 0; x < map.Width; x++)
                            {
                                int a = y * columns + x;
                                int b = a + 1;
                                int c = a + columns;
                                int d = c + 1;
                                foreach (int i in new[] { a, b, d, a, d, c })
                                    part.TriangleIndices.Add(i);
                            }
                        }
                        chunks.Add(new GeometryModel3D(part, tex) { BackMaterial = tex });
                        y0 = y1;
                    }
                    group.Children.RemoveAt(2);
                    foreach (var chunk in chunks)
                        group.Children.Add(chunk);
                }
                catch (Exception ex)
                {
                    assetWarning = " Blend unavailable: " + ex.Message;
                }
            }
            else if (ground.SelectedIndex > 0)
            {
                try
                {
                    var tex = Texture(Path.Combine(biome, "texture" + ground.SelectedIndex + ".tga"), 1);
                    mesh.TextureCoordinates.Clear();
                    for (int j = 0; j < rows; j++)
                    {
                        for (int x = 0; x <= map.Width; x++)
                            mesh.TextureCoordinates.Add(new Point(x / 4.0, (start + j * step) / 4.0));
                    }
                    var model = (GeometryModel3D)group.Children[2];
                    model.Material = tex;
                    model.BackMaterial = tex;
                }
                catch
                {
                    assetWarning = " Ground texture unavailable.";
                }
            }
        }

        private void AddWaterPlane(Model3DGroup group, int start, int end, ref string assetWarning)
        {
            var mesh = new MeshGeometry3D();
            double z = (double)waterHeight.Value;

            // A single quad; the OpenGL side tessellates this for the wave.
            foreach (var q in new[] { new Point(0, start), new Point(map.Width, start), new Point(map.Width, end), new Point(0, end) })
            {
                mesh.Positions.Add(new Point3D(q.X * 40, q.Y * 40, z));
                mesh.TextureCoordinates.Add(new Point(q.X / 4, q.Y / 4));
            }
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(i);

            Material mat = new DiffuseMaterial(
                new SolidColorBrush(Color.FromArgb((byte)Math.Round(waterOpacity * 255), 45, 100, 135)));

            try
            {
                if (waterPath.Length > 0)
                    mat = Texture(waterPath, waterOpacity);
            }
            catch
            {
                assetWarning += " Water texture unavailable; plain fallback.";
            }

            // Tag so GlTerrainControl.BuildScene can identify the water mesh.
            mat.SetValue(WaterSurfaceProperty, true);

            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        // ═════════════════════════════════════════════════════════════════
        // Ground texture blending
        // ═════════════════════════════════════════════════════════════════

        private string cachedBiomeKey = "";
        private byte[][] cachedBiomeBands;
        private byte[] cachedBiomeDetail;

        private static byte[] ReadTgaPixels256(string path, string errorLabel)
        {
            using (var img = Assets.Tga(path))
            {
                if (img.Width != 256 || img.Height != 256)
                    throw new InvalidDataException(errorLabel);

                var data = img.LockBits(
                    new Rectangle(0, 0, 256, 256),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    byte[] raw = new byte[256 * 256 * 4];
                    for (int y = 0; y < 256; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            IntPtr.Add(data.Scan0, y * data.Stride),
                            raw,
                            y * 256 * 4,
                            256 * 4);
                    }
                    return raw;
                }
                finally
                {
                    img.UnlockBits(data);
                }
            }
        }

        private void EnsureBiomeCache()
        {
            if (cachedBiomeBands != null && cachedBiomeKey == biome) return;

            string root = Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            var bands = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                string full = Path.GetFullPath(Path.Combine(dataFolder, biome, "texture" + (i + 1) + ".tga"));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Asset outside data folder");
                bands[i] = ReadTgaPixels256(full, "Ground textures must be 256×256.");
            }

            cachedBiomeBands = bands;
            cachedBiomeDetail = null;
            cachedBiomeKey = biome;
        }

        private byte[] EnsureDetailCache()
        {
            EnsureBiomeCache();
            if (cachedBiomeDetail == null)
            {
                cachedBiomeDetail = ReadTgaPixels256(
                    Path.Combine(dataFolder, biome, "detail.tga"),
                    "detail.tga must be 256×256.");
            }
            return cachedBiomeDetail;
        }

        private Material BlendedGround(int selectedBlock)
        {
            string key = selectedBlock + ":" + detail.Checked;
            if (groundBlocks.TryGetValue(key, out var cached)) return cached;

            if (map.Width < 2 || map.Height % TerrainBlockRows != 0 || map.Height > 2048)
                throw new InvalidDataException(
                    "Blended ground requires a 32-row block layout, up to 2048 rows.");

            EnsureBiomeCache();
            var bands = cachedBiomeBands;

            int blocks = 1;
            int stride = 256 * 4;
            byte[] pixels = new byte[stride * blocks * 256];

            for (int block = 0; block < blocks; block++)
            {
                for (int y = 0; y < 256; y++)
                {
                    for (int x = 0; x < 256; x++)
                    {
                        byte h = SampleBlock(selectedBlock, x, y);
                        int lo = (int)Math.Floor(h / HeightToBandDivisor);
                        int hi = (int)Math.Ceiling(h / HeightToBandDivisor);
                        int srcOff = (y * 256 + x) * 4;
                        byte[] a = bands[lo];
                        byte[] b = bands[hi];
                        int off = ((block * 256 + y) * 256 + x) * 4;
                        pixels[off]     = BlendChannel(a[srcOff],     b[srcOff],     h);
                        pixels[off + 1] = BlendChannel(a[srcOff + 1], b[srcOff + 1], h);
                        pixels[off + 2] = BlendChannel(a[srcOff + 2], b[srcOff + 2], h);
                        pixels[off + 3] = 255;
                    }
                }
            }

            int outWidth = 256;
            int outHeight = blocks * 256;

            if (detail.Checked)
            {
                byte[] detailBytes = EnsureDetailCache();

                // Bake each 32-row block separately at 1024×1024 to avoid
                // downsampling the tile texture below its native density.
                outWidth = 1024;
                outHeight = blocks * 1024;
                var combined = new byte[outWidth * outHeight * 4];

                for (int y = 0; y < outHeight; y++)
                {
                    for (int x = 0; x < 1024; x++)
                    {
                        double sx = x / 4.0;
                        double sy = (y % 1024) / 4.0;
                        int ix = (int)sx;
                        int iy = (int)sy;
                        double tx = sx - ix;
                        double ty = sy - iy;
                        int block = 0;
                        int detailOff = (((y * 2) % 256) * 256 + (x * 2) % 256) * 4;

                        for (int k = 0; k < 3; k++)
                        {
                            int a = ((block * 256 + iy) * 256 + ix) * 4 + k;
                            int b = ((block * 256 + iy) * 256 + Math.Min(255, ix + 1)) * 4 + k;
                            int cc = ((block * 256 + Math.Min(255, iy + 1)) * 256 + ix) * 4 + k;
                            int dd = ((block * 256 + Math.Min(255, iy + 1)) * 256 + Math.Min(255, ix + 1)) * 4 + k;

                            double value =
                                (pixels[a] * (1 - tx) + pixels[b] * tx) * (1 - ty) +
                                (pixels[cc] * (1 - tx) + pixels[dd] * tx) * ty;

                            // 127.5 = mid-gray; the detail texture is
                            // a modulation map, not a color.
                            double tint = detailBytes[detailOff + k];
                            combined[(y * 1024 + x) * 4 + k] =
                                (byte)Math.Max(0, Math.Min(255, value + tint - 127.5));
                        }
                        combined[(y * 1024 + x) * 4 + 3] = 255;
                    }
                }
                pixels = combined;
                stride = outWidth * 4;
            }

            var bitmap = BitmapSource.Create(outWidth, outHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();

            var result = new DiffuseMaterial(new ImageBrush(bitmap)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                Stretch = Stretch.Fill,
            });
            result.Freeze();
            groundBlocks[key] = result;
            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        // Tiles and tile atlas
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the atlas rectangle for tile <paramref name="index"/> in a
        /// tile sheet of the given dimensions, or <see cref="Rectangle.Empty"/>
        /// if the sheet is malformed or the index is out of range.
        /// </summary>
        /// <remarks>
        /// Engine 0x416DF6..0x416E64: atlas tiles are 64×64 and are laid out
        /// row-major, with <c>columns = width / 64</c>. Indices beyond
        /// <c>columns * rows</c> return empty.
        /// </remarks>
        public static Rectangle AtlasRectangle(int width, int height, int index)
        {
            if (width < TileAtlasCellSize || height < TileAtlasCellSize) return Rectangle.Empty;
            if (width % TileAtlasCellSize != 0 || height % TileAtlasCellSize != 0) return Rectangle.Empty;
            if (index < 0 || index >= 128) return Rectangle.Empty;

            int columns = width / TileAtlasCellSize;
            int rows = height / TileAtlasCellSize;
            if (index >= columns * rows) return Rectangle.Empty;

            return new Rectangle(
                index % columns * TileAtlasCellSize,
                index / columns * TileAtlasCellSize,
                TileAtlasCellSize,
                TileAtlasCellSize);
        }

        private Material TileMaterial(int sheet, int index)
        {
            string key = sheet + ":" + index;
            if (materials.TryGetValue(key, out var cached)) return cached;

            string path = Path.Combine(dataFolder, "tiles", "tiles" + sheet + ".tga");
            if (!File.Exists(path)) return null;

            using (var image = Assets.Tga(path))
            {
                var rect = AtlasRectangle(image.Width, image.Height, index);
                if (rect.IsEmpty) return null;

                using (var tile = image.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var stream = new MemoryStream())
                {
                    tile.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    stream.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var brush = new ImageBrush(bitmap)
                    {
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 1, 1),
                    };
                    var result = new DiffuseMaterial(brush);
                    result.Freeze();
                    materials[key] = result;
                    return result;
                }
            }
        }

        /// <summary>
        /// Adds a single tile overlay quad to the scene.
        /// </summary>
        /// <remarks>
        /// Cell value layout: bits 8..15 = sheet, 16..23 = index, 24..31 =
        /// rotation code. Rotation codes are 0, 3, 6, 9 only; anything else
        /// is skipped. <c>turn = code / 3</c> yields 0..3.
        /// UVs are permuted by <c>(i - turn + 4) % 4</c>. Do not simplify
        /// this to <c>i % 4</c> or to <c>rotate(uvs, 90° × turn)</c>.
        /// </remarks>
        private void AddTile(Model3DGroup group, int x, int y)
        {
            uint raw = map.Cells[y * map.Width + x];
            int sheet = (int)((raw >> 8) & 0xFF);
            int index = (int)((raw >> 16) & 0xFF);

            // Mask out the upper 4 bits where AS2 stores extra blend flags, 
            // keeping only the lower 4 bits for the rotation code.
            int code = (int)((raw >> 24) & 0x0F);

            if (sheet == 0) return;

            // Loosen the strict AS1 check. If an AS2 tile uses an unknown rotation,
            // force it to 0 (unrotated) instead of skipping the tile entirely.
            if (code != 0 && code != 3 && code != 6 && code != 9)
            {
                code = 0;
            }

            Material m;
            try { m = TileMaterial(sheet, index); }
            catch { SkippedTiles++; return; }
            if (m == null) { SkippedTiles++; return; }

            var mesh = new MeshGeometry3D();
            int[] dx = { 0, 1, 1, 0 };
            int[] dy = { 0, 0, 1, 1 };
            Point[] uv = { new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0) };
            int turn = code / 3;

            // ... (Keep the rest of the vertex mapping identical)

            for (int i = 0; i < 4; i++)
            {
                mesh.Positions.Add(new Point3D(
                    (x + dx[i]) * 40,
                    (y + dy[i]) * 40,
                    Z(x + dx[i], y + dy[i]) + 0.15));
                mesh.TextureCoordinates.Add(uv[(i - turn + 4) % 4]);
            }
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
                mesh.TriangleIndices.Add(i);

            group.Children.Add(new GeometryModel3D(mesh, m) { BackMaterial = m });
            OverlayCount++;
        }

        // ═════════════════════════════════════════════════════════════════
        // Texture helpers
        // ═════════════════════════════════════════════════════════════════

        private Material Texture(string relativePath, double opacity)
        {
            string cacheKey = relativePath + "|" + opacity;
            if (textureMaterials.TryGetValue(cacheKey, out var cached)) return cached;

            string full = Path.GetFullPath(Path.Combine(dataFolder,
                relativePath.Replace('\\', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Asset outside data folder.");

            using (var image = Assets.Tga(full))
            using (var stream = new MemoryStream())
            {
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                var material = new DiffuseMaterial(new ImageBrush(bitmap)
                {
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1),
                    TileMode = TileMode.Tile,
                    Opacity = opacity,
                });
                textureMaterials[cacheKey] = material;
                return material;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Environment parsing
        // ═════════════════════════════════════════════════════════════════

        private void LoadEnvironment(string name, string supplied)
        {
            string block = MissionPreset.Resolve(dataFolder, name, supplied);
            if (block == null)
            {
                if (string.Equals(Path.GetFileName(name), "level1.hsc", StringComparison.OrdinalIgnoreCase))
                {
                    biome = "textures/desert";
                    waterPath = "gfx/water/water_lake2.tga";
                    waterHeight.Value = -68;
                    water.Checked = true;
                    environment = "Tutorial preset (saved settings; no current mission match)";
                }
                return;
            }

            double lo = Number(block, "hmin", -120);
            double hi = Number(block, "hmax", 130);
            if (lo >= -2000 && hi <= 2000 && hi > lo)
            {
                low.Value = (decimal)lo;
                high.Value = (decimal)hi;
            }

            var t = Regex.Match(block, "(?m)^\\s*textures\\s+\"([^\"]+)\"");
            if (t.Success) biome = t.Groups[1].Value;

            var w = Regex.Match(block, "(?m)^\\s*water\\s+\"([^\"]+)\"\\s+(?:\"[^\"]+\"\\s+)?([-+0-9.eE]+)");
            if (w.Success && double.TryParse(
                    w.Groups[2].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double waterZ)
                && waterZ >= -2000 && waterZ <= 2000)
            {
                var parsed = MissionSettings.Read(block);
                waterOpacity = parsed.WaterOpacity;
                if (double.IsNaN(waterOpacity) || double.IsInfinity(waterOpacity)
                    || waterOpacity < 0 || waterOpacity > 1)
                    throw new InvalidDataException("Water opacity must be between 0 and 1.");

                waterPath = w.Groups[1].Value;
                waterHeight.Value = (decimal)waterZ;
                water.Checked = true;
            }

            environment = supplied != null
                ? "Saved environment profile; game uses levels.txt"
                : "Environment from matching mission / saved profile";

            var sun = Values(block, "sun", 9);
            if (sun != null)
            {
                sunColor = Rgb(sun[0], sun[1], sun[2]);
                sunDirection = new Vector3D(-sun[3], -sun[4], -sun[5]);
                if (sunDirection.Length > 0)
                {
                    sunDirection.Normalize();
                    hasSun = true;
                }
                ambientColor = Rgb(sun[6], sun[7], sun[8]);
            }
        }

        private static Color Rgb(double r, double g, double b)
        {
            return Color.FromRgb(
                (byte)(Math.Max(0, Math.Min(1, r)) * 255),
                (byte)(Math.Max(0, Math.Min(1, g)) * 255),
                (byte)(Math.Max(0, Math.Min(1, b)) * 255));
        }

        private static double[] Values(string block, string key, int count)
        {
            var m = Regex.Match(block, @"(?m)^\s*" + key + @"[ \t]+([^\r\n]+)");
            if (!m.Success) return null;

            var parts = m.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < count) return null;

            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                    return null;
            }
            return values;
        }

        private static double Number(string block, string key, double fallback)
        {
            var m = Regex.Match(block, @"(?m)^\s*" + key + @"\s+([-+0-9.]+)");
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                               CultureInfo.InvariantCulture, out double n)
                ? n
                : fallback;
        }

        // ═════════════════════════════════════════════════════════════════
        // Incremental updates and snapshots
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rebuilds the scene with a new height grid. Used after sculpting;
        /// only the affected 32-row blocks are invalidated in the cache.
        /// </summary>
        public Model3DGroup UpdateHeightScene(uint[] cells)
        {
            if (cells.Length != map.Cells.Length)
                throw new ArgumentException("Terrain dimensions changed.");

            var changedBlocks = new HashSet<int>();
            for (int i = 0; i < cells.Length; i++)
                if ((cells[i] & 0xFF) != (map.Cells[i] & 0xFF))
                    changedBlocks.Add((i / map.Width) / TerrainBlockRows);

            foreach (int block in changedBlocks)
            {
                groundBlocks.Remove(block + ":True");
                groundBlocks.Remove(block + ":False");
            }

            map.Cells = (uint[])cells.Clone();
            return ExportScene();
        }

        /// <summary>Forces a whole-map rebuild and returns the new scene.</summary>
        public Model3DGroup ExportScene()
        {
            whole.Checked = true;
            Build();
            return (Model3DGroup)visual.Content;
        }

        public void Snapshot(string path, bool full)
        {
            Snapshot(path, full, false);
        }

        public void Snapshot(string path, bool full, bool forward)
        {
            whole.Checked = full;
            Build();
            if (forward) ForwardView();
            CaptureView(path);
        }

        public void CaptureView(string path)
        {
            surface.Width = 1000;
            surface.Height = 700;
            surface.Measure(new Size(1000, 700));
            surface.Arrange(new Rect(0, 0, 1000, 700));
            surface.UpdateLayout();

            var b = new RenderTargetBitmap(1000, 700, 96, 96, PixelFormats.Pbgra32);
            b.Render(surface);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using (var f = File.Create(path))
                enc.Save(f);

            surface.Width = double.NaN;
            surface.Height = double.NaN;
        }
    }
}