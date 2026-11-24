using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows.Media.Media3D;     // Point3D, Vector3D, MeshGeometry3D, etc.
using Bitmap = System.Drawing.Bitmap;
using Brushes = System.Drawing.Brushes;
// Explicit aliases for the types that System.Drawing and WPF both define.
// WPF owns the "smart" types used with SolidColorBrush etc.; System.Drawing
// owns the GDI+ types used with Bitmap/Graphics.
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace AirstrikeStudio
{
    /// <summary>
    /// OpenGL-backed 3D terrain viewport. Renders the map as a WYSIWYG preview
    /// and hosts all interactive editing (sculpt, paint, place, route, select).
    /// </summary>
    /// <remarks>
    /// This class is split across many partial files:
    ///   <c>GlTerrainWindow.cs</c>   — GL setup, render loop, window shell (this file)
    ///   <c>GlSculpt.cs</c>          — terrain height brush
    ///   <c>GlTiles.cs</c>           — road/ground tile painting
    ///   <c>GlGroundCache.cs</c>     — raycast acceleration structure
    ///   <c>GlScenery.cs</c>         — object model loading and placement
    ///   <c>GlMove.cs</c>            — object/group movement
    ///   <c>GlRoutes.cs</c>          — Bézier waypoint rendering and editing
    ///   <c>GlSelection.cs</c>       — picking and selection box
    ///   <c>GlGroups.cs</c>          — multi-select transforms
    ///   <c>GlActivation.cs</c>      — Y-band preview
    ///   <c>GlModes.cs</c>           — editing mode state machine
    ///   <c>GlDrag.cs</c>            — drag-and-drop
    ///   <c>GlBox.cs</c>             — box selection
    ///   <c>GlLibrary.cs</c>         — placeable object catalog
    ///   <c>GlNavigation.cs</c>      — minimap and fit-to-view
    ///   <c>GlDocument.cs</c>        — document lifecycle (save/load/undo)
    ///   <c>GlAssemblies.cs</c>      — model assembly flattening
    ///   <c>GlToolbarLayout.cs</c>   — UI composition
    ///   <c>GlProperties.cs</c>      — property inspector
    ///   <c>GlRoutes.cs</c>          — (also) route list UI
    ///   <c>GlWorkspace.cs</c>       — workspace layout
    ///   <c>GlPrecision.cs</c>       — numeric move/duplicate
    ///   <c>GlShared.cs</c>          — multi-object shared settings
    ///   <c>GlDrops.cs</c>           — drop table editor
    ///   <c>GlTiles.cs</c>           — (also) tile picker UI
    ///   <c>GlModelInspector.cs</c>  — model report dialog
    ///   <c>GlAircraft.cs</c>        — aircraft starting-height rules
    /// </remarks>

    public sealed partial class GlTerrainControl : Control
    {
        private Map map;
        private System.Windows.Media.Media3D.Model3DGroup scene;
        // ═════════════════════════════════════════════════════════════════
        // P/Invoke — Win32 GDI
        // ═════════════════════════════════════════════════════════════════
        // All these types and imports exist to build a GL-capable HDC by hand.
        // WPF/Forms can't give us one directly, so we call the Win32 pixel-format
        // path exactly as a 2000s-era game would have.
        // ═════════════════════════════════════════════════════════════════

        [StructLayout(LayoutKind.Sequential)]
        private struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize, nVersion;
            public uint dwFlags;
            public byte iPixelType, cColorBits;
            public byte cRedBits, cRedShift, cGreenBits, cGreenShift;
            public byte cBlueBits, cBlueShift, cAlphaBits, cAlphaShift;
            public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
            public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        // ═════════════════════════════════════════════════════════════════
        // P/Invoke — OpenGL 1.1
        // ═════════════════════════════════════════════════════════════════
        // Only OpenGL 1.1 is imported. No shaders, no VBOs, no glBufferData.
        // This deliberately matches what the game itself links against.
        // ═════════════════════════════════════════════════════════════════

        [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
        [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
        [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hglrc);

        [DllImport("opengl32.dll")] private static extern IntPtr glGetString(uint name);
        [DllImport("opengl32.dll")] private static extern uint glGetError();

        [DllImport("opengl32.dll")] private static extern void glPushMatrix();
        [DllImport("opengl32.dll")] private static extern void glPopMatrix();
        [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);
        [DllImport("opengl32.dll")] private static extern void glMatrixMode(uint mode);
        [DllImport("opengl32.dll")] private static extern void glLoadIdentity();
        [DllImport("opengl32.dll")] private static extern void glOrtho(double l, double r, double b, double t, double n, double f);
        [DllImport("opengl32.dll")] private static extern void glFrustum(double l, double r, double b, double t, double n, double f);

        [DllImport("opengl32.dll")] private static extern void glRotatef(float angle, float x, float y, float z);
        [DllImport("opengl32.dll")] private static extern void glTranslatef(float x, float y, float z);

        [DllImport("opengl32.dll")] private static extern void glClearColor(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] private static extern void glEnable(uint cap);
        [DllImport("opengl32.dll")] private static extern void glDisable(uint cap);

        [DllImport("opengl32.dll")] private static extern void glNormal3f(float nx, float ny, float nz);
        [DllImport("opengl32.dll")] private static extern void glFogi(uint pname, int param);
        [DllImport("opengl32.dll")] private static extern void glFogf(uint pname, float param);
        [DllImport("opengl32.dll")] private static extern void glFogfv(uint pname, float[] params_);

        [DllImport("opengl32.dll")] private static extern void glBegin(uint mode);
        [DllImport("opengl32.dll")] private static extern void glEnd();
        [DllImport("opengl32.dll")] private static extern void glColor4f(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] private static extern void glVertex3f(float x, float y, float z);
        [DllImport("opengl32.dll")] private static extern void glVertex3d(double x, double y, double z);
        [DllImport("opengl32.dll")] private static extern void glTexCoord2d(double u, double v);

        [DllImport("opengl32.dll")] private static extern void glPolygonOffset(float factor, float units);
        [DllImport("opengl32.dll")] private static extern void glDepthMask(byte enabled);
        [DllImport("opengl32.dll")] private static extern void glBlendFunc(uint sfactor, uint dfactor);
        [DllImport("opengl32.dll")] private static extern void glAlphaFunc(uint func, float reference);

        [DllImport("opengl32.dll")] private static extern void glGenTextures(int n, out uint texture);
        [DllImport("opengl32.dll")] private static extern void glBindTexture(uint target, uint texture);
        [DllImport("opengl32.dll")] private static extern void glTexParameteri(uint target, uint pname, int param);
        [DllImport("opengl32.dll")] private static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, byte[] pixels);

        [DllImport("opengl32.dll")] private static extern uint glGenLists(int range);
        [DllImport("opengl32.dll")] private static extern void glNewList(uint list, uint mode);
        [DllImport("opengl32.dll")] private static extern void glEndList();
        [DllImport("opengl32.dll")] private static extern void glCallList(uint list);
        [DllImport("opengl32.dll")] private static extern void glDeleteLists(uint list, int range);

        [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, byte[] pixels);
        [DllImport("opengl32.dll")] private static extern void glFinish();

        // ═════════════════════════════════════════════════════════════════
        // GL enum constants
        // ═════════════════════════════════════════════════════════════════
        // Named so the call sites read as GL rather than as hex soup.
        // ═════════════════════════════════════════════════════════════════

        private const uint GL_TEXTURE_2D = 0x0DE1;
        private const uint GL_BLEND = 0x0BE2;
        private const uint GL_DEPTH_TEST = 0x0B71;
        private const uint GL_CULL_FACE = 0x0B44;
        private const uint GL_FOG = 0x0B60;
        private const uint GL_ALPHA_TEST = 0x0BC0;
        private const uint GL_POLYGON_OFFSET_FILL = 0x8037;

        private const uint GL_LINES = 0x0001;
        private const uint GL_QUADS = 0x0007;
        private const uint GL_TRIANGLES = 0x0004;

        private const uint GL_MODELVIEW = 0x1700;
        private const uint GL_PROJECTION = 0x1701;
        private const uint GL_TEXTURE = 0x1702;

        private const uint GL_FOG_MODE = 0x0B65;
        private const uint GL_FOG_START = 0x0B63;
        private const uint GL_FOG_END = 0x0B64;
        private const uint GL_FOG_COLOR = 0x0B66;
        private const uint GL_LINEAR = 0x2601;

        private const uint GL_SRC_ALPHA = 0x0302;
        private const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
        private const uint GL_GREATER = 0x0204;
        private const uint GL_COLOR_BUFFER_BIT = 0x4000;
        private const uint GL_DEPTH_BUFFER_BIT = 0x0100;
        private const uint GL_MODULATE = 0x2100;
        private const uint GL_BGRA = 0x80E1;
        private const uint GL_UNSIGNED_BYTE = 0x1401;
        private const uint GL_RGBA = 0x1908;
        private const uint GL_REPEAT = 0x2901;
        private const uint GL_CLAMP = 0x2900;
        private const uint GL_NEAREST = 0x2600;
        private const uint GL_COMPILE = 0x1300;

        // Texture parameter names (as opposed to the values used above).
        private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
        private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
        private const uint GL_TEXTURE_WRAP_S = 0x2802;
        private const uint GL_TEXTURE_WRAP_T = 0x2803;

        private const uint GL_VENDOR = 0x1F00;
        private const uint GL_RENDERER = 0x1F01;
        private const uint GL_VERSION = 0x1F02;
        private const uint GL_SCISSOR_TEST = 0x0C11;
        private const uint PFD_DOUBLEBUFFER = 0x00000001;
        private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
        private const uint PFD_SUPPORT_OPENGL = 0x00000020;
        private const uint PFD_FLAGS_DOUBLE_GL = PFD_DOUBLEBUFFER | PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL; // 0x25

        // ═════════════════════════════════════════════════════════════════
        // Tunables
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Half vertical field of view, as tangent. tan(30°) = 0.577... = 1/√3.
        /// The horizontal FOV is derived from this and the 4:3 aspect ratio.
        /// </summary>
        private const double VerticalHalfFovTan = 0.5773502691896258;

        /// <summary>4:3 letterbox aspect ratio; see <see cref="ViewportRect"/>.</summary>
        private const double AspectRatio43 = 4.0 / 3.0;

        /// <summary>
        /// Near plane for the perspective projection. Must be ≥ 1 to avoid
        /// terrain z-fighting; must be ≤ 100 to avoid visible near-clipping
        /// when the camera hugs a mountain.
        /// </summary>
        private const double NearPlane = 1.0;

        /// <summary>
        /// Far plane. Sized so a 512×1024 map at maximum zoom-out is still
        /// fully visible. Cannot be increased without precision loss.
        /// </summary>
        private const double FarPlane = 200000.0;

        /// <summary>Water wave vertical amplitude, in world units.</summary>
        private const double WaterWaveAmplitude = 1.0;

        /// <summary>Water wave horizontal amplitude, in world units.</summary>
        private const double WaterWaveDriftAmplitude = 0.4;

        /// <summary>Water wave period scaler; 0.1·π ≈ 1 wave per 20 s.</summary>
        private const double WaterWaveFrequency = Math.PI * 0.1;

        // ═════════════════════════════════════════════════════════════════
        // Instance state — GL context
        // ═════════════════════════════════════════════════════════════════
        // NOTE: this field set is shared across all GlTerrainControl partials.
        // Some fields live in other partial files. Only the fields this file
        // actually touches are declared here; the rest are documented in the
        // partial they belong to.
        // ═════════════════════════════════════════════════════════════════

        private IntPtr deviceContext;       // HDC returned by GetDC(Handle)
        private IntPtr glContext;           // HGLRC returned by wglCreateContext
        private uint displayList;           // glGenLists(1): terrain + tile overlays
        private uint sceneryDisplayList;    // glGenLists(1): placed objects
        private uint waterDisplayList;      // glGenLists(1): water surface
        private uint waterMotionList;       // glGenLists(1): the water wave matrix animation
        private System.Windows.Media.Imaging.BitmapSource fallbackBitmap;
        private string glError;             // once set, every OnPaint throws it (see Render)

        /// <summary>Reported by <c>glGetString(GL_RENDERER)</c> once GL is initialized.</summary>
        public string Renderer { get; private set; } = "Not initialized";

        // ═════════════════════════════════════════════════════════════════
        // Instance state — water animation
        // ═════════════════════════════════════════════════════════════════

        private Timer waterAnimationTimer;
        private bool animateWater;
        private double waterPhase;      // accumulated radians, wraps at 4π
        private long waterLastTickMs; // Environment.TickCount at last phase advance

        /// <summary>
        /// When true, the water surface bobs up and down. Only cosmetic in the
        /// editor; the game does not animate water this way. Turned off while
        /// the viewport is hidden, minimized, or mid-edit to save CPU.
        /// </summary>
        public bool AnimateWater
        {
            get => animateWater;
            set { animateWater = value; waterLastTickMs = Environment.TickCount; Invalidate(); }
        }

        // ═════════════════════════════════════════════════════════════════
        // Instance state — camera and fog
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Whether distance fog is enabled in the render pass.</summary>
        public bool Fog = true;

        /// <summary>Camera orbit target, map-grid X. One tile = 40 world units.</summary>
        public double CenterX = 16;

        /// <summary>Camera orbit target, map-grid Y. One tile = 40 world units.</summary>
        public double CenterY = 52;

        /// <summary>Camera pitch in degrees. 45 = looking down at 45°; 90 = top-down.</summary>
        public double Pitch = 45;

        /// <summary>Camera heading in degrees. -90 = looking north along +Y.</summary>
        public double Heading = -90;

        /// <summary>Camera orbit distance in world units.</summary>
        public double Distance = 381.8376618;

        // --- BOOKMARKS FIX ---
        // Moved this array here so it lives inside GlTerrainControl
        private double[][] bookmarks = new double[5][];

        private readonly float[] fogColor = { 0.4f, 0.4f, 0.3f, 1.0f };
        private float fogStart = 650;
        private float fogEnd = 900;

        /// <summary>
        /// Fired whenever the camera or selection changes and the status bar
        /// or minimap should repaint.
        /// </summary>
        public event EventHandler ViewChanged;
        public bool GridSnap = false;
        // ═════════════════════════════════════════════════════════════════
        // Instance state — lighting
        // ═════════════════════════════════════════════════════════════════
        // Filled from the WPF-side TerrainViewer in the constructor. Used by
        // EmitMeshWithLighting() when baking the scene display list.

        private System.Windows.Media.Color sunColor = System.Windows.Media.Colors.White;
        private System.Windows.Media.Color ambient = System.Windows.Media.Color.FromRgb(110, 110, 110);
        private System.Windows.Media.Media3D.Vector3D sunVector = new System.Windows.Media.Media3D.Vector3D(-1, 1, -2);

        // ═════════════════════════════════════════════════════════════════
        // Constructor
        // ═════════════════════════════════════════════════════════════════

        public GlTerrainControl(Map source, string name, string dataFolder, string environmentOverride = null)
        {
            terrainName = name;
            map = source.Clone();
            terrainProvider = new TerrainViewer(source.Clone(), name, dataFolder, environmentOverride);

            // Pull the baked scene and lighting info from the WPF-side TerrainViewer.
            // We do not re-render terrain; we take its WPF Model3DGroup, convert
            // each mesh to a GL display list, and draw.
            scene = terrainProvider.ExportScene();
            sunColor = terrainProvider.SunColor;
            ambient = terrainProvider.AmbientColor;
            sunVector = terrainProvider.SunVector;

            Disposed += (s, e) => { terrainProvider.Dispose(); cachedAssets?.Dispose(); };

            assetRoot = dataFolder;
            LoadScenery(dataFolder);                        // GlScenery.cs

            CenterX = map.Width / 2.0;
            CenterY = Math.Min(52, map.Height / 2.0);
            Fog = false;

            TryReadFogFromMission(dataFolder, name, environmentOverride);
            SetupWaterAnimation();

            SetStyle(
                ControlStyles.Opaque |
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint,
                true);
            TabStop = true;

            WireMouseAndKeyboard();
            // Route preview animation is driven by the single 33 ms timer in SetupWaterAnimation
            // so water + route animation coalesce into one Invalidate() per frame.
        }

        private bool timersSuspended = false;

        public void PauseTimers()
        {
            timersSuspended = true;
            if (waterAnimationTimer != null && waterAnimationTimer.Enabled)
                waterAnimationTimer.Stop();
        }

        public void ResumeTimers()
        {
            timersSuspended = false;
            if (waterAnimationTimer != null && !waterAnimationTimer.Enabled && (animateWater || PreviewRoute))
                waterAnimationTimer.Start();
        }


        // ═════════════════════════════════════════════════════════════════
        // Fog parsing
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Reads the mission block's <c>fog</c> line, if present, and applies
        /// it to <see cref="fogColor"/>, <see cref="fogStart"/>, <see cref="fogEnd"/>.
        /// </summary>
        /// <remarks>
        /// The same fog data is also parsed by TerrainViewer, but only for its
        /// initial render. This second parse exists because the user can edit
        /// mission settings while the window is open and expect the fog to
        /// change. Keep both parses; do not merge them.
        /// </remarks>
        private void TryReadFogFromMission(string dataFolder, string mapName, string environmentOverride)
        {
            string block = MissionPreset.Resolve(dataFolder, mapName, environmentOverride);
            if (block == null) return;

            var m = Regex.Match(block, @"(?m)^\s*fog\s+([^\r\n]+)");
            if (!m.Success) return;

            string[] parts = m.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return;

            float[] values = new float[5];
            for (int i = 0; i < 5; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out values[i])
                    || float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                    return;
            }
            if (values[4] <= values[3]) return; // end ≤ start: invalid, ignore

            Array.Copy(values, fogColor, 3);
            fogStart = values[3];
            fogEnd = values[4];
            Fog = true;
        }

        // ═════════════════════════════════════════════════════════════════
        // Water animation
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Starts the 30 Hz timer that advances <see cref="waterPhase"/> while
        /// the viewport is visible and idle. Does nothing if the viewport is
        /// hidden, minimized, or mid-edit.
        /// </summary>
        private void SetupWaterAnimation()
        {
            waterAnimationTimer = new Timer { Interval = 33 };
            waterAnimationTimer.Tick += (s, e) =>
            {
                if (timersSuspended) return;
                long now = Environment.TickCount;
                double elapsed = Math.Max(0, Math.Min(0.1, (now - waterLastTickMs) / 1000.0));
                waterLastTickMs = now;

                Form host = FindForm();
                if (!Visible || host == null || host.WindowState == FormWindowState.Minimized)
                    return;

                bool needRepaint = false;
                if (PreviewRoute && SelectedIndex >= 0)
                {
                    routeAnimTime += elapsed > 0 ? elapsed : 0.033;
                    needRepaint = true;
                }
                if (animateWater && !dragging && !PlacementPending && !TerrainMode && !TileMode)
                {
                    waterPhase = (waterPhase + elapsed * WaterWaveFrequency) % (Math.PI * 4);
                    needRepaint = true;
                }
                if (needRepaint) Invalidate();
            };
            waterAnimationTimer.Start();
            Disposed += (s, e) => { waterAnimationTimer.Stop(); waterAnimationTimer.Dispose(); };
        }

        /// <summary>
        /// Rebuilds the display list that pushes/pops the water matrix. Called
        /// once per frame from <see cref="Render"/>, so the wave is stateless
        /// from the scene graph's point of view.
        /// </summary>
        private void UpdateWaterMotion()
        {
            glNewList(waterMotionList, GL_COMPILE);

            // Two-stage transform so waves ripple both horizontally and vertically.
            glMatrixMode(GL_TEXTURE);
            glPushMatrix();
            glTranslatef(
                (float)(WaterWaveDriftAmplitude * Math.Sin(waterPhase)),
                (float)(WaterWaveDriftAmplitude * Math.Sin(waterPhase * 0.5)),
                0);

            glMatrixMode(GL_MODELVIEW);
            glPushMatrix();
            glTranslatef(0, 0, (float)(WaterWaveAmplitude * Math.Sin(waterPhase)));

            glEndList();
        }

        // ═════════════════════════════════════════════════════════════════
        // GL context lifecycle
        // ═════════════════════════════════════════════════════════════════

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                KeepGlRuntimeAlive();

                deviceContext = GetDC(Handle);

                var pfd = new PIXELFORMATDESCRIPTOR
                {
                    nSize = (ushort)Marshal.SizeOf(typeof(PIXELFORMATDESCRIPTOR)),
                    nVersion = 1,
                    dwFlags = PFD_FLAGS_DOUBLE_GL,
                    iPixelType = 0,        // PFD_TYPE_RGBA
                    cColorBits = 32,
                    cDepthBits = 24,
                };

                int format = ChoosePixelFormat(deviceContext, ref pfd);
                if (format == 0 || !SetPixelFormat(deviceContext, format, ref pfd))
                    throw new Exception("OpenGL pixel format unavailable");

                glContext = wglCreateContext(deviceContext);
                if (glContext == IntPtr.Zero || !wglMakeCurrent(deviceContext, glContext))
                    throw new Exception("OpenGL context unavailable");

                textures.Clear();
                cursorTexture = 0;
                displayList = 0;
                sceneryDisplayList = 0;
                waterDisplayList = 0;
                pendingDisplayList = 0;
                pendingListName = null;

                waterMotionList = glGenLists(1);
                if (waterMotionList == 0)
                    throw new Exception("Water animation list unavailable");

                UpdateWaterMotion();
                Renderer = Marshal.PtrToStringAnsi(glGetString(GL_RENDERER)) ?? "Unknown";
                BuildScene();
            }
            catch (Exception ex)
            {
                glError = ex.Message;
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (glContext != IntPtr.Zero)
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(glContext);
                glContext = IntPtr.Zero;
            }
            if (deviceContext != IntPtr.Zero)
            {
                ReleaseDC(Handle, deviceContext);
                deviceContext = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        // ═════════════════════════════════════════════════════════════════
        // Scene construction (called from OnHandleCreated)
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts the WPF <c>Model3DGroup</c> into a single GL display list.
        /// On every repaint, one <c>glCallList</c> emits the whole scene.
        /// </summary>
        /// <remarks>
        /// Every child of <c>scene</c> is either terrain (heightfield, tiles,
        /// water) or a placed object. All of them go through the same
        /// per-vertex-lit emit path:
        ///
        ///   • Water meshes are tessellated before emission (the WPF side
        ///     provides a single quad; the wave displacement is a matrix
        ///     transform, so the mesh must be dense enough to visibly ripple).
        ///   • Water writes no depth and is drawn with the wave matrix active.
        ///   • Non-water meshes use polygon offset only when the original
        ///     WPF mesh had 4 vertices (that's the tile overlay case, which
        ///     needs to sit above the terrain without z-fighting).
        ///   • Submarine darkening applies to terrain under water only; it
        ///     must NOT touch water itself or scenery (whose Z may legitimately
        ///     sit below a water plane, e.g. FL_ONWATER ships).
        ///
        /// Do not re-introduce a bypass for "terrain" or "water": the earlier
        /// refactor broke by drawing a hardcoded desert quad and skipping
        /// everything that wasn't in <c>sceneryIds</c>. That made biome,
        /// tiles, blend, and water all disappear.
        /// </remarks>
        private System.Windows.Media.Imaging.BitmapSource EnsureModelTexture(
            System.Windows.Media.Media3D.DiffuseMaterial material)
        {
            if (material == null) throw new Exception("Unsupported material");

            var bitmap = (material.Brush as System.Windows.Media.ImageBrush)?.ImageSource
                         as System.Windows.Media.Imaging.BitmapSource;

            // If there's no texture (e.g. SolidColorBrush/LinearGradientBrush fallback for missing
            // water/ground), use a generic white 1x1 texture so the renderer
            // doesn't crash. TerrainViewer normally always supplies an
            // ImageBrush; this is a defensive fallback only.
            if (bitmap == null)
            {
                if (fallbackBitmap == null)
                {
                    var dummy = new System.Windows.Media.Imaging.WriteableBitmap(
                        1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    dummy.WritePixels(
                        new System.Windows.Int32Rect(0, 0, 1, 1),
                        new byte[4] { 255, 255, 255, 255 }, 4, 0);
                    dummy.Freeze();
                    fallbackBitmap = dummy;
                }
                bitmap = fallbackBitmap;
                if (!material.IsFrozen)
                {
                    double opacity = material.Brush != null ? material.Brush.Opacity : 1.0;
                    material.Brush = new System.Windows.Media.ImageBrush(bitmap) { Opacity = opacity };
                }
            }

            if (textures.TryGetValue(bitmap, out uint _)) return bitmap;

            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);

            glGenTextures(1, out uint texture);
            glBindTexture(GL_TEXTURE_2D, texture);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
            int wrap = (material.Brush as System.Windows.Media.ImageBrush)?.TileMode == System.Windows.Media.TileMode.Tile
                ? (int)GL_REPEAT : (int)GL_CLAMP;
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, wrap);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, wrap);
            glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA,
                         converted.PixelWidth, converted.PixelHeight, 0,
                         GL_BGRA, GL_UNSIGNED_BYTE, pixels);

            textures[bitmap] = texture;
            return bitmap;
        }

        private void EmitModel(
            System.Windows.Media.Media3D.GeometryModel3D model,
            bool isWater)
        {
            var mesh = (System.Windows.Media.Media3D.MeshGeometry3D)model.Geometry;
            var material = model.Material as System.Windows.Media.Media3D.DiffuseMaterial;
            if (material == null) throw new Exception("Unsupported material");

            var brush = material.Brush as System.Windows.Media.ImageBrush;
            var bitmap = (brush?.ImageSource as System.Windows.Media.Imaging.BitmapSource) ?? fallbackBitmap;
            if (bitmap == null) throw new Exception("Expected bitmap");

            glBindTexture(GL_TEXTURE_2D, textures[bitmap]);
            glEnable(GL_TEXTURE_2D);
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

            if (isWater) glCallList(waterMotionList);

            if (isWater)
                mesh = TessellateWaterMesh(mesh);

            glDepthMask(isWater ? (byte)0 : (byte)1);

            // Only the tile-overlay case (a 4-vertex quad slightly above
            // the terrain) needs polygon offset. Everything else either
            // sits on its own depth or is scenery drawn with full depth.
            if (!isWater && mesh.Positions.Count == 4)
            {
                glEnable(GL_POLYGON_OFFSET_FILL);
                glPolygonOffset(-1, -1);
            }
            else
            {
                glDisable(GL_POLYGON_OFFSET_FILL);
            }

            EmitMeshWithLighting(model, mesh, brush, isWater);

            if (isWater)
            {
                // Unwind the water matrices pushed by UpdateWaterMotion.
                glPopMatrix();
                glMatrixMode(GL_TEXTURE);
                glPopMatrix();
                glMatrixMode(GL_MODELVIEW);
            }
        }

        private void BuildTerrainAndWaterLists()
        {
            if (displayList != 0)
                glDeleteLists(displayList, 1);
            displayList = glGenLists(1);
            if (displayList == 0)
                throw new Exception("Display list unavailable");

            if (waterDisplayList != 0)
                glDeleteLists(waterDisplayList, 1);
            waterDisplayList = glGenLists(1);
            if (waterDisplayList == 0)
                throw new Exception("Water display list unavailable");

            foreach (var child in scene.Children)
            {
                if (child is not System.Windows.Media.Media3D.GeometryModel3D model) continue;
                if (sceneryIds.ContainsKey(model)) continue;
                EnsureModelTexture(model.Material as System.Windows.Media.Media3D.DiffuseMaterial);
            }

            glNewList(displayList, GL_COMPILE);
            foreach (var child in scene.Children)
            {
                if (child is not System.Windows.Media.Media3D.GeometryModel3D model) continue;
                if (sceneryIds.ContainsKey(model)) continue;
                var material = model.Material as System.Windows.Media.Media3D.DiffuseMaterial;
                if (TerrainViewer.IsWater(material)) continue;
                EmitModel(model, isWater: false);
            }
            glDepthMask(1);
            glDisable(GL_BLEND);
            glDisable(GL_POLYGON_OFFSET_FILL);
            glEndList();

            glNewList(waterDisplayList, GL_COMPILE);
            foreach (var child in scene.Children)
            {
                if (child is not System.Windows.Media.Media3D.GeometryModel3D model) continue;
                if (sceneryIds.ContainsKey(model)) continue;
                var material = model.Material as System.Windows.Media.Media3D.DiffuseMaterial;
                if (!TerrainViewer.IsWater(material)) continue;
                EmitModel(model, isWater: true);
            }
            glDepthMask(1);
            glDisable(GL_BLEND);
            glDisable(GL_POLYGON_OFFSET_FILL);
            glEndList();

            uint err = glGetError();
            if (err != 0)
                throw new Exception("OpenGL terrain scene error " + err);
        }

        private void BuildSceneryList(HashSet<System.Windows.Media.Media3D.Model3D> excluded = null)
        {
            if (sceneryDisplayList != 0)
                glDeleteLists(sceneryDisplayList, 1);
            sceneryDisplayList = glGenLists(1);
            if (sceneryDisplayList == 0)
                throw new Exception("Scenery display list unavailable");

            foreach (var model in sceneryIds.Keys)
            {
                if (excluded != null && excluded.Contains(model)) continue;
                EnsureModelTexture(model.Material as System.Windows.Media.Media3D.DiffuseMaterial);
            }

            glNewList(sceneryDisplayList, GL_COMPILE);
            foreach (var model in sceneryIds.Keys)
            {
                if (excluded != null && excluded.Contains(model)) continue;
                EmitModel(model, isWater: false);
            }
            glDepthMask(1);
            glDisable(GL_BLEND);
            glDisable(GL_POLYGON_OFFSET_FILL);
            glEndList();

            uint err = glGetError();
            if (err != 0)
                throw new Exception("OpenGL scenery scene error " + err);
        }

        private uint BuildObjectList(int objectId)
        {
            var models = new List<System.Windows.Media.Media3D.GeometryModel3D>();
            foreach (var pair in sceneryIds)
                if (pair.Value == objectId)
                    models.Add(pair.Key);

            if (models.Count == 0) return 0;

            foreach (var model in models)
                EnsureModelTexture(model.Material as System.Windows.Media.Media3D.DiffuseMaterial);

            uint list = glGenLists(1);
            if (list == 0) return 0;

            glNewList(list, GL_COMPILE);
            foreach (var model in models)
                EmitModel(model, isWater: false);
            glDepthMask(1);
            glDisable(GL_BLEND);
            glDisable(GL_POLYGON_OFFSET_FILL);
            glEndList();
            return list;
        }

        private void BuildScene()
        {
            BuildTerrainAndWaterLists();
            BuildSceneryList();
        }

        /// <summary>
        /// Replaces the WPF water quad with a fully tessellated grid sized to
        /// the map. The wave displacement in <see cref="UpdateWaterMotion"/> is
        /// a matrix transform, so this mesh has to be dense enough to look like
        /// a wave rather than a rigid slab.
        /// </summary>
        private System.Windows.Media.Media3D.MeshGeometry3D TessellateWaterMesh(
            System.Windows.Media.Media3D.MeshGeometry3D original)
        {
            var result = new System.Windows.Media.Media3D.MeshGeometry3D();
            double z = original.Positions[0].Z;

            for (int y = 0; y <= map.Height; y++)
            {
                for (int x = 0; x <= map.Width; x++)
                {
                    result.Positions.Add(new System.Windows.Media.Media3D.Point3D(x * 40, y * 40, z));
                    result.TextureCoordinates.Add(new System.Windows.Point(x / 4.0, y / 4.0));
                }
            }

            int cols = map.Width + 1;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    int a = y * cols + x;
                    int b = a + 1;
                    int c = a + cols;
                    int d = c + 1;
                    foreach (int index in new[] { a, b, d, a, d, c })
                        result.TriangleIndices.Add(index);
                }
            }
            return result;
        }

        /// <summary>
        /// Emits one mesh's triangles with per-vertex Lambert lighting.
        /// </summary>
        /// <remarks>
        /// Submarine darkening is applied when:
        ///   • the mesh is not water,
        ///   • the mesh is not scenery (i.e., it is terrain — the object under
        ///     the water is being viewed through a semi-transparent water plane),
        ///   • the mesh has more than 4 vertices (tile overlays don't need it),
        ///   • the map has a water Z (found by <c>LoadScenery</c>).
        /// If you ever move emission back inside a scenery-only loop, remember
        /// this check becomes dead and the underwater look disappears.
        /// </remarks>
        private void EmitMeshWithLighting(
            System.Windows.Media.Media3D.GeometryModel3D model,
            System.Windows.Media.Media3D.MeshGeometry3D mesh,
            System.Windows.Media.ImageBrush brush,
            bool isWater)
        {
            var normals = ComputeVertexNormals(mesh);

            var sun = sunVector;
            sun.Normalize();

            glBegin(GL_TRIANGLES);
            // Disable culling so we see the back of triangles if the WPF mesh flipped them.
            glDisable(GL_CULL_FACE);
            foreach (int i in mesh.TriangleIndices)
            {
                var n = normals[i];
                if (n.Length > 0) n.Normalize();
                double diffuse = Math.Max(0, System.Windows.Media.Media3D.Vector3D.DotProduct(n, sun));

                double depthLight = 1.0;
                if (!isWater
                    && !sceneryIds.ContainsKey(model)
                    && mesh.Positions.Count > 4
                    && objectWaterZ.HasValue)
                {
                    depthLight = SubmergedLight(
                        mesh.Positions[i].Z,
                        terrainProvider.HeightMinimumSetting,
                        objectWaterZ.Value);
                }

                float r = (float)Math.Min(1, (ambient.R / 255.0 + sunColor.R / 255.0 * diffuse) * depthLight);
                float g = (float)Math.Min(1, (ambient.G / 255.0 + sunColor.G / 255.0 * diffuse) * depthLight);
                float b = (float)Math.Min(1, (ambient.B / 255.0 + sunColor.B / 255.0 * diffuse) * depthLight);
                glColor4f(r, g, b, (float)(brush != null ? brush.Opacity : 1.0));

                var uv = mesh.TextureCoordinates[i];
                glTexCoord2d(uv.X, uv.Y);

                var p = mesh.Positions[i];
                glVertex3f((float)p.X, (float)p.Y, (float)p.Z);
            }
            glEnd();
        }

        private static System.Windows.Media.Media3D.Vector3D[] ComputeVertexNormals(
            System.Windows.Media.Media3D.MeshGeometry3D mesh)
        {
            var normals = new System.Windows.Media.Media3D.Vector3D[mesh.Positions.Count];
            for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
            {
                int a = mesh.TriangleIndices[i];
                int b = mesh.TriangleIndices[i + 1];
                int c = mesh.TriangleIndices[i + 2];
                var n = System.Windows.Media.Media3D.Vector3D.CrossProduct(
                    mesh.Positions[b] - mesh.Positions[a],
                    mesh.Positions[c] - mesh.Positions[a]);
                normals[a] += n;
                normals[b] += n;
                normals[c] += n;
            }
            return normals;
        }

        /// <summary>
        /// Underwater attenuation: terrain deeper than the water surface is
        /// darkened proportionally to depth. This is a visual approximation
        /// of the game's water fog, not a physical model.
        /// </summary>
        public static double SubmergedLight(double z, double minimumZ, double waterZ)
        {
            if (z >= waterZ || waterZ <= minimumZ) return 1.0;
            return Math.Max(0, Math.Min(1, (z - minimumZ) / (waterZ - minimumZ)));
        }

        // ═════════════════════════════════════════════════════════════════
        // Render loop
        // ═════════════════════════════════════════════════════════════════

        private void Render()
        {
            if (glError != null)
                throw new Exception(glError);
            if (glContext == IntPtr.Zero || !wglMakeCurrent(deviceContext, glContext))
                throw new Exception("OpenGL context unavailable");

            int w = Math.Max(1, ClientSize.Width);
            int h = Math.Max(1, ClientSize.Height);

            glClearColor(0.07f, 0.09f, 0.12f, 1.0f);
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

            var vp = ViewportRect(w, h);
            glViewport(vp.X, vp.Y, vp.Width, vp.Height);

            // ── Projection: perspective matching client aspect ───────────
            glMatrixMode(GL_PROJECTION);
            glLoadIdentity();
            double aspect = w > 0 && h > 0 ? (double)w / h : AspectRatio43;
            double top = VerticalHalfFovTan * NearPlane;
            double right = top * aspect;
            glFrustum(-right, right, -top, top, NearPlane, FarPlane);

            // ── Modelview: orbit camera around (CenterX, CenterY, 0) ─────
            glMatrixMode(GL_MODELVIEW);
            glLoadIdentity();
            double headingRad = Heading * Math.PI / 180;
            double pitchRad = Pitch * Math.PI / 180;
            double cx = CenterX * 40 + Math.Cos(headingRad) * Math.Cos(pitchRad) * Distance;
            double cy = CenterY * 40 + Math.Sin(headingRad) * Math.Cos(pitchRad) * Distance;
            double cz = Math.Sin(pitchRad) * Distance;

            glRotatef((float)(Pitch - 90), 1, 0, 0);
            glRotatef((float)(-90 - Heading), 0, 0, 1);
            glTranslatef((float)-cx, (float)-cy, (float)-cz);

            // ── State: depth, fog, blending ─────────────────────────────
            glEnable(GL_DEPTH_TEST);
            glDisable(GL_CULL_FACE);

            if (Fog)
            {
                glEnable(GL_FOG);
                glFogi(GL_FOG_MODE, (int)GL_LINEAR);
                glFogf(GL_FOG_START, fogStart);
                glFogf(GL_FOG_END, fogEnd);
                glFogfv(GL_FOG_COLOR, fogColor);
            }
            else
            {
                glDisable(GL_FOG);
            }

            glEnable(GL_ALPHA_TEST);
            glAlphaFunc(GL_GREATER, 0.1f);

            // Safe rendering pass: prevents material parsing exceptions from crashing OnPaint
            try
            {
                UpdateWaterMotion();
                if (displayList != 0)        glCallList(displayList);
                if (sceneryDisplayList != 0) glCallList(sceneryDisplayList);
                if (waterDisplayList != 0)   glCallList(waterDisplayList);
                DrawDraggedObject();          // GlDrag.cs
                DrawDraggedGizmoObject();     // GlGizmo.cs
                DrawPendingPlacement();       // GlLibrary.cs

                glDisable(GL_ALPHA_TEST);
                DrawSnapGrid();
                DrawSelection();              // GlSelection.cs
                DrawGizmo();                  // GlGizmo.cs
                DrawRouteOverlay();           // GlRoutes.cs
                DrawSculpt();                 // GlSculpt.cs
                DrawTileCursor();             // GlTiles.cs
                DrawSelectionBox();           // GlBox.cs
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Render pass exception bypassed: " + ex.Message);
            }

            uint err = glGetError();
            if (err != 0)
            {
                System.Diagnostics.Debug.WriteLine("OpenGL error code: " + err);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Render();
                SwapBuffers(deviceContext);
            }
            catch (Exception ex)
            {
                glError = ex.ToString();
                e.Graphics.Clear(Color.FromArgb(20, 25, 30));
                e.Graphics.DrawString(
                    "OpenGL preview unavailable: " + glError,
                    Font, Brushes.White,
                    new RectangleF(10, 10, Width - 20, Height - 20));
            }
        }

        /// <summary>
        /// Returns the letterboxed 4:3 viewport inside a client rectangle.
        /// Every screen↔world conversion in the partials must use the same
        /// rectangle that glViewport receives, or picking math drifts.
        /// </summary>
        private void DrawSnapGrid()
        {
            if (!GridSnap || map == null) return;
            int minX = Math.Max(0, (int)Math.Floor(CenterX) - 16);
            int maxX = Math.Min(map.Width, (int)Math.Ceiling(CenterX) + 16);
            int minY = Math.Max(0, (int)Math.Floor(CenterY) - 16);
            int maxY = Math.Min(map.Height, (int)Math.Ceiling(CenterY) + 16);
            if (minX >= maxX || minY >= maxY) return;

            glDisable(GL_TEXTURE_2D);
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
            glColor4f(1f, 1f, 1f, 0.16f);
            glBegin(GL_LINES);
            const float z = 4f;
            for (int x = minX; x <= maxX; x++)
            {
                glVertex3f(x * 40f, minY * 40f, z);
                glVertex3f(x * 40f, maxY * 40f, z);
            }
            for (int y = minY; y <= maxY; y++)
            {
                glVertex3f(minX * 40f, y * 40f, z);
                glVertex3f(maxX * 40f, y * 40f, z);
            }
            glEnd();
            glDisable(GL_BLEND);
        }

        internal Rectangle ViewportRect(int clientWidth, int clientHeight)
        {
            return new Rectangle(0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight));
        }

        // ═════════════════════════════════════════════════════════════════
        // Screenshots
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Renders once and saves the framebuffer as a PNG.
        /// </summary>
        /// <remarks>
        /// glReadPixels returns rows bottom-up; Bitmap expects them top-down.
        /// The loop copies row <c>h-y-1</c> to row <c>y</c>, which is why the
        /// image is not vertically flipped when it lands on disk.
        /// </remarks>
        public void SavePreview(string path)
        {
            Render();
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            byte[] pixels = new byte[w * h * 4];
            glReadPixels(0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, pixels);

            using (var image = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var locked = image.LockBits(
                    new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        pixels[(y * w + x) * 4 + 3] = 255; // force alpha

                    Marshal.Copy(
                        pixels, y * w * 4,
                        IntPtr.Add(locked.Scan0, (h - y - 1) * locked.Stride),
                        w * 4);
                }
                image.UnlockBits(locked);
                image.Save(path, ImageFormat.Png);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Mouse + keyboard wiring
        // ═════════════════════════════════════════════════════════════════

        private Point lastMouse;
        private Point clickStart;
        private bool dragging;
        private bool moved;
        private MouseButtons dragButton;
        private readonly Stopwatch inputClock = Stopwatch.StartNew();
        private long lastDragPaintMs;
        private long lastStatusUpdateMs;

        /// <summary>
        /// Central mouse handler. Delegates to the active editing mode
        /// (GlModes.cs) and to the per-mode handlers (GlDrag, GlSculpt,
        /// GlTiles, GlRoutes, GlBox, GlSelection).
        /// </summary>
        private void WireMouseAndKeyboard()
        {
            MouseDown += OnViewportMouseDown;
            MouseUp += OnViewportMouseUp;
            MouseMove += OnViewportMouseMove;
            MouseWheel += OnViewportMouseWheel;
            MouseCaptureChanged += OnViewportMouseCaptureChanged;
            KeyDown += OnViewportKeyDown;
        }

        private void OnViewportMouseDown(object sender, MouseEventArgs e)
        {
            Focus();
            Capture = true;
            dragging = true;
            dragButton = e.Button;
            lastMouse = e.Location;
            clickStart = e.Location;
            moved = false;

            // If it's the Middle or Right button, we ONLY want to pan.
            // Do not trigger tile painting, sculpting, or selection!
            if (dragButton == MouseButtons.Middle || dragButton == MouseButtons.Right)
                return;

            // 3D Transform Gizmo interaction
            if (dragButton == MouseButtons.Left && IsGizmoEligible)
            {
                var hitPart = HitTestGizmo(e.X, e.Y);
                if (hitPart != GizmoPart.None)
                {
                    BeginGizmoDrag(hitPart, e.X, e.Y);
                    return;
                }
            }

            // Alt+click opens the overlap chooser instead of starting a drag.
            if (dragButton == MouseButtons.Left && !TerrainMode && !TileMode && !PlacementPending
                && (ModifierKeys & Keys.Alt) != 0)
            {
                dragging = false;
                Capture = false;
                ChooseOverlap(e.X, e.Y);
                return;
            }

            // Box selection
            if (BoxSelectMode && dragButton == MouseButtons.Left
                && !TerrainMode && !TileMode && !PlacementPending)
            {
                boxDragging = true;
                boxToggle = (ModifierKeys & Keys.Control) != 0;
                boxStart = boxEnd = e.Location;
                return;
            }

            // Only allow Left-click to interact with tools
            if (dragButton == MouseButtons.Left)
            {
                if (TileMode)
                {
                    if ((ModifierKeys & Keys.Alt) != 0)
                    {
                        dragging = false;
                        Capture = false;
                        SampleTileAt(e.X, e.Y);
                        return;
                    }
                    try
                    {
                        BeginTileStroke();
                        HoverTile(e.X, e.Y);
                        StampTile();
                    }
                    catch (Exception ex)
                    {
                        EndTileStroke(false);
                        MessageBox.Show(this, ex.Message, "Tile painting");
                    }
                }
                else if (TerrainMode)
                {
                    if ((ModifierKeys & Keys.Alt) != 0)
                    {
                        dragging = false;
                        Capture = false;
                        SampleHeightAt(e.X, e.Y);
                        return;
                    }
                    BeginSculpt(e.X, e.Y);
                }
                else if (!PlacementPending)
                {
                    if (EditRoute)
                        BeginRouteDrag(e.X, e.Y);
                    else if (MoveMode)
                        BeginObjectDrag(e.X, e.Y);
                }
            }
        }

        private void OnViewportMouseUp(object sender, MouseEventArgs e)
        {
            if (boxDragging)
            {
                boxEnd = e.Location;
                boxDragging = false;
                SelectBox(BoxRect, boxToggle);
                dragging = false;
                Capture = false;
                Changed();
                return;
            }

            try
            {
                if (gizmoActive != GizmoPart.None)
                {
                    EndGizmoDrag(true);
                    dragging = false;
                    Capture = false;
                    Cursor = Cursors.Default;
                    return;
                }
                else if (tileDraft != null)
                {
                    HoverTile(e.X, e.Y);
                    StampTile();
                    EndTileStroke(true);
                }
                else if (sculptCells != null)
                {
                    HoverSculpt(e.X, e.Y);
                    StampSculpt();
                    EndSculpt(true);
                }
                else if (routeDrag)
                {
                    if (moved) UpdateRouteDrag(e.X, e.Y);
                    EndRouteDrag(true);
                }
                else if (objectDrag)
                {
                    if (moved) UpdateObjectDrag(e.X, e.Y);
                    EndObjectDrag(true);
                }
                else if (dragging && !moved && dragButton == MouseButtons.Left && PlacementPending)
                {
                    PlacePendingScreen(e.X, e.Y);
                }
                else if (dragging && !moved && dragButton == MouseButtons.Left && EditRoute
                         && !TerrainMode && !TileMode)
                {
                    if ((AppendRouteOnClick || (ModifierKeys & Keys.Shift) != 0)
                        && PickWaypoint(e.X, e.Y) < 0)
                    {
                        AppendRouteWaypointAtScreen(e.X, e.Y);
                    }
                }
                else if (dragging && !moved && dragButton == MouseButtons.Left
                        && !MoveMode && !EditRoute && !TerrainMode)
                {
                    SelectAt(e.X, e.Y);

                    // ADD THIS BLOCK:
                    if (SelectedIndex >= 0 && SelectedIndex < map.Objects.Count && FindForm() is GlTerrainWindow window)
                    {
                        string name = map.Types[map.Objects[SelectedIndex].Type];
                        window.RecentObjects.Remove(name);
                        window.RecentObjects.Insert(0, name);
                        if (window.RecentObjects.Count > 5) window.RecentObjects.RemoveAt(5);
                    }
                }
                else if (dragging && !moved && dragButton == MouseButtons.Right)
                {
                    if (PlacementPending)
                    {
                        CancelPlacement();
                    }
                    else if (!TerrainMode && !TileMode)
                    {
                        int hit = PickAt(e.X, e.Y);
                        if (hit >= 0)
                        {
                            if (Array.IndexOf(SelectedObjects, hit) < 0)
                                SetObjectSelection(new[] { hit });
                            ShowObjectContextMenu(e.X, e.Y);
                        }
                        else if (SelectedIndex >= 0 || SelectedObjects.Length > 0)
                        {
                            SetObjectSelection(new int[0]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Move failed");
            }

            dragging = false;
            Capture = false;
            Changed();
            Update();
        }

        private void OnViewportMouseCaptureChanged(object sender, EventArgs e)
        {
            if (Capture) return;
            if (gizmoActive != GizmoPart.None) EndGizmoDrag(false);
            boxDragging = false;
            EndTileStroke(false);
            EndSculpt(false);
            EndRouteDrag(false);
            EndObjectDrag(false);
            dragging = false;
        }

        private void OnViewportKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (gizmoActive != GizmoPart.None)
                {
                    EndGizmoDrag(false);
                    e.Handled = true;
                    return;
                }
                CancelEditingAction();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F && (ModifierKeys & Keys.Control) == 0)
            {
                if (SelectedIndex >= 0)
                {
                    FocusSelected();
                    e.Handled = true;
                }
            }
            else if (e.KeyCode == Keys.G)
            {
                if (SelectedIndex >= 0)
                {
                    SyncPlacements(map.Clone());
                    MessageBox.Show("Recalculated terrain height for selection.", "Ground Snap");
                }
            }
            else if (e.KeyCode == Keys.R && (ModifierKeys & Keys.Control) == 0)
            {
                int dir = (ModifierKeys & Keys.Shift) != 0 ? -1 : 1;
                if (PlacementPending)
                {
                    RotatePending(dir);
                    e.Handled = true;
                }
                else if (TileMode)
                {
                    RotateTileBrush(dir);
                    e.Handled = true;
                }
                else if (SelectedObjects.Length == 1)
                {
                    RotateSelected(dir);
                    e.Handled = true;
                }
            }
            else if (e.KeyCode == Keys.OemOpenBrackets)
            {
                if (PlacementPending) { RotatePending(-1); e.Handled = true; }
                else if (TerrainMode) { AdjustTerrainBrushRadius(-1); e.Handled = true; }
                else if (TileMode) { AdjustTileBrushSize(-1); e.Handled = true; }
                else if (SelectedObjects.Length == 1) { RotateSelected(-1); e.Handled = true; }
            }
            else if (e.KeyCode == Keys.OemCloseBrackets)
            {
                if (PlacementPending) { RotatePending(1); e.Handled = true; }
                else if (TerrainMode) { AdjustTerrainBrushRadius(1); e.Handled = true; }
                else if (TileMode) { AdjustTileBrushSize(1); e.Handled = true; }
                else if (SelectedObjects.Length == 1) { RotateSelected(1); e.Handled = true; }
            }
            else if (e.KeyCode == Keys.I && (ModifierKeys & Keys.Control) == 0)
            {
                if (!TerrainMode && !TileMode && SelectedIndex >= 0)
                {
                    ArmDuplicate();
                    e.Handled = true;
                }
            }
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                if (EditRoute && RouteWaypoint >= 0)
                {
                    DeleteRouteWaypoint();
                    e.Handled = true;
                }
            }
        }

        private void OnViewportMouseMove(object sender, MouseEventArgs e)
        {
            if (gizmoActive != GizmoPart.None)
            {
                UpdateGizmoDrag(e.X, e.Y);
                Invalidate();
                long nowDrag = inputClock.ElapsedMilliseconds;
                if (nowDrag - lastDragPaintMs >= 8)
                {
                    lastDragPaintMs = nowDrag;
                    Update();
                }
                return;
            }

            if (!dragging && IsGizmoEligible)
            {
                var part = HitTestGizmo(e.X, e.Y);
                if (part != gizmoHover)
                {
                    gizmoHover = part;
                    Invalidate();
                }
                switch (part)
                {
                    case GizmoPart.AxisX: Cursor = Cursors.SizeWE; break;
                    case GizmoPart.AxisY: Cursor = Cursors.SizeNS; break;
                    case GizmoPart.Rotation: Cursor = Cursors.Cross; break;
                    case GizmoPart.Center: Cursor = Cursors.SizeAll; break;
                    default: Cursor = Cursors.Default; break;
                }
            }
            else if (!dragging && gizmoHover != GizmoPart.None)
            {
                gizmoHover = GizmoPart.None;
                Cursor = Cursors.Default;
                Invalidate();
            }

            if (boxDragging)
            {
                boxEnd = e.Location;
                Invalidate();
                Update();
                return;
            }

            if (PlacementPending && !dragging)
            {
                HoverPending(e.X, e.Y);
            }
            else if (EditRoute && !dragging)
            {
                HoverRoute(e.X, e.Y);
            }

            if (TileMode)
            {
                long tick = inputClock.ElapsedMilliseconds;
                if (tick - lastTilePaint >= 16)
                {
                    lastTilePaint = tick;
                    HoverTile(e.X, e.Y);
                    if (tileDraft != null) StampTile();
                    Update();
                }
            }

            if (TerrainMode && (!dragging || dragButton == MouseButtons.Left))
            {
                long tick = inputClock.ElapsedMilliseconds;
                if (tick - lastDragPaintMs < 16) return;
                lastDragPaintMs = tick;
                HoverSculpt(e.X, e.Y);
                if (sculptCells != null) StampSculpt();
                Update();
                if (!dragging || sculptCells != null) return;
            }

            if (!dragging) return;
            if (!moved)
            {
                if (Math.Abs(e.X - clickStart.X) + Math.Abs(e.Y - clickStart.Y) < 4)
                    return;
                moved = true;
            }

            double dx = e.X - lastMouse.X;
            double dy = e.Y - lastMouse.Y;

            if (routeDrag) UpdateRouteDrag(e.X, e.Y);
            else if (objectDrag) UpdateObjectDrag(e.X, e.Y);
            else if (dragButton == MouseButtons.Right || dragButton == MouseButtons.Middle)
                PanByPixels(dx, dy);
            else if (!MoveMode && !EditRoute && !TerrainMode && !TileMode)
            {
                // Free orbit: left-drag rotates.
                Heading += dx * 0.4;
                Pitch = Math.Max(10, Math.Min(85, Pitch + dy * 0.4));
            }

            lastMouse = e.Location;
            Invalidate();

            long now = inputClock.ElapsedMilliseconds;
            if (now - lastDragPaintMs >= 8)
            {
                lastDragPaintMs = now;
                Update();
            }
            if (now - lastStatusUpdateMs >= 200)
            {
                lastStatusUpdateMs = now;
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnViewportMouseWheel(object sender, MouseEventArgs e)
        {
            if (boxDragging || objectDrag || routeDrag || sculptCells != null || tileDraft != null)
                return;
            Distance = Math.Max(100, Math.Min(100000, Distance * Math.Pow(1.15, -e.Delta / 120.0)));
            Changed();
        }

        /// <summary>
        /// Pans the camera by a screen-space pixel delta, converting to map-grid
        /// displacement so the point under the cursor moves with the cursor.
        /// </summary>
        public void PanByPixels(double dx, double dy)
        {
            double headingRad = Heading * Math.PI / 180;
            double viewportHeight = Math.Max(1, Math.Min(ClientSize.Height, ClientSize.Width * 3.0 / 4));
            double k = 2 * Distance * VerticalHalfFovTan / viewportHeight / 40;
            double forward = dy / Math.Max(0.1, Math.Sin(Pitch * Math.PI / 180));
            CenterX += (Math.Sin(headingRad) * dx - Math.Cos(headingRad) * forward) * k;
            CenterY += (-Math.Cos(headingRad) * dx - Math.Sin(headingRad) * forward) * k;
        }

        public void StoreBookmark(int slot)
        {
            if (slot < 0 || slot >= 5) return;
            bookmarks[slot] = new double[] { CenterX, CenterY, Heading, Pitch, Distance };
            MessageBox.Show("Saved camera bookmark " + (slot + 1), "Bookmark");
        }

        public bool JumpToBookmark(int slot)
        {
            if (slot < 0 || slot >= 5 || bookmarks[slot] == null) return false;

            CenterX = bookmarks[slot][0];
            CenterY = bookmarks[slot][1];
            Heading = bookmarks[slot][2];
            Pitch = bookmarks[slot][3];
            Distance = bookmarks[slot][4];

            Changed();
            return true;
        }

        /// <summary>Reads one camera bookmark for persistence. Null if unset.</summary>
        /// <remarks>
        /// Returns a clone so a caller (WorkspacePreferences) cannot alias the
        /// live array and cause a future mutation to leak across.
        /// </remarks>
        public double[] GetBookmark(int slot) =>
            slot >= 0 && slot < bookmarks.Length && bookmarks[slot] != null
                ? (double[])bookmarks[slot].Clone()
                : null;

        /// <summary>Restores one camera bookmark from persistence. Null clears it.</summary>
        public void SetBookmark(int slot, double[] value)
        {
            if (slot < 0 || slot >= bookmarks.Length) return;
            bookmarks[slot] = value != null && value.Length == 5 ? (double[])value.Clone() : null;
        }

        /// <summary>Invalidate and fire <see cref="ViewChanged"/>.</summary>
        public void Changed()
        {
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        // ═════════════════════════════════════════════════════════════════
        // Static: keep a GL runtime alive for the whole app session
        // ═════════════════════════════════════════════════════════════════
        // Some drivers (older NVIDIA, all Mesa) crash if the last GL context
        // is destroyed and a new one is created later. To avoid that, we keep
        // one 1×1 hidden window with a live GL context for the entire app
        // lifetime. No scene resources are shared; each viewport still owns
        // and deletes its own context and display lists.
        // ═════════════════════════════════════════════════════════════════

        private static NativeWindow runtimeWindow;
        private static IntPtr runtimeDc;
        private static IntPtr runtimeContext;
        private static bool runtimeAttempted;

        private static void KeepGlRuntimeAlive()
        {
            if (runtimeAttempted) return;
            runtimeAttempted = true;

            try
            {
                runtimeWindow = new NativeWindow();
                runtimeWindow.CreateHandle(new CreateParams
                {
                    Caption = "Map Studio GL lifetime",
                    ClassStyle = 0x20, // CS_OWNDC
                    Width = 1,
                    Height = 1,
                });

                runtimeDc = GetDC(runtimeWindow.Handle);
                var pfd = new PIXELFORMATDESCRIPTOR
                {
                    nSize = (ushort)Marshal.SizeOf(typeof(PIXELFORMATDESCRIPTOR)),
                    nVersion = 1,
                    dwFlags = PFD_FLAGS_DOUBLE_GL,
                    iPixelType = 0,
                    cColorBits = 32,
                    cDepthBits = 24,
                };
                int fmt = ChoosePixelFormat(runtimeDc, ref pfd);
                if (fmt == 0 || !SetPixelFormat(runtimeDc, fmt, ref pfd))
                    throw new Exception("Lifetime pixel format");

                runtimeContext = wglCreateContext(runtimeDc);
                if (runtimeContext == IntPtr.Zero)
                    throw new Exception("Lifetime context");

                Application.ApplicationExit += ReleaseGlRuntime;
            }
            catch
            {
                // Optional mitigation. If it fails, the viewport still works;
                // it just loses crash protection on certain drivers.
                ReleaseGlRuntime(null, EventArgs.Empty);
            }
        }

        private static void ReleaseGlRuntime(object sender, EventArgs e)
        {
            Application.ApplicationExit -= ReleaseGlRuntime;
            if (runtimeContext != IntPtr.Zero) { wglDeleteContext(runtimeContext); runtimeContext = IntPtr.Zero; }
            if (runtimeDc != IntPtr.Zero && runtimeWindow != null) { ReleaseDC(runtimeWindow.Handle, runtimeDc); runtimeDc = IntPtr.Zero; }
            if (runtimeWindow != null) { runtimeWindow.DestroyHandle(); runtimeWindow = null; }
        }
    }

    /// <summary>
    /// Window shell around a <see cref="GlTerrainControl"/>. Owns the toolbar,
    /// the inspector panel, the status bar, and the document chrome.
    /// </summary>
    public sealed partial class GlTerrainWindow : Form
    {
        public List<string> RecentObjects = new List<string>(); // <--- ADD THIS
        public readonly GlTerrainControl Preview;
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 42 };

        public GlTerrainWindow(Map map, string name, string dataFolder, string environmentOverride = null)
        {
            Font = new Font("Segoe UI", 9f);
            Text = name + " — 3D map editor";
            // Set the window icon
            try
            {
                string iconPath = System.IO.Path.Combine(Application.StartupPath, "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }
            Width = 1100;
            Height = 820;
            MinimumSize = new Size(750, 500);

            // Since this is now a 3D-only app, closing this window exits the app.
            FormClosed += (s, e) =>
            {
                // Studio.OpenGlWorkspace reopens this window after New map, Mission
                // settings, Change data folder, or Open map. Those paths set one of
                // these flags and then call Close(). Exiting unconditionally here
                // tore down the message loop before the caller could loop back, so
                // the app quit instead of reloading. Only exit when nothing is queued.
                if (!NextMapCreated
                    && NextMapPath == null
                    && !AssetFolderChanged
                    && !MissionSettingsChanged
                    && !MapResized)
                    Application.Exit();
            };

            Preview = new GlTerrainControl(map, name, dataFolder, environmentOverride)
            {
                Dock = DockStyle.Fill,
            };

            Controls.Add(Preview);
            Controls.Add(status);

            var inspectorPanel = new Panel { Dock = DockStyle.Right, Width = 300 };
            Controls.Add(inspectorPanel);

            SetupRouteList(inspectorPanel);       // GlRoutes.cs
            SetupInspector(inspectorPanel, dataFolder); // GlProperties.cs

            var toolbar = BuildToolbar();
            Controls.Add(toolbar);

            Preview.ViewChanged += (s, e) => UpdateStatus();
            Shown += (s, e) => UpdateStatus();
            Deactivate += (s, e) => Preview.PauseTimers();
            Activated += (s, e) => Preview.ResumeTimers();
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                    Preview.PauseTimers();
                else if (WindowState == FormWindowState.Normal || WindowState == FormWindowState.Maximized)
                    Preview.ResumeTimers();
            };

            ArrangeWorkspace(toolbar, inspectorPanel); // GlWorkspace.cs
        }

        private void LaunchModelTool()
        {
            if (Preview.SelectedIndex < 0) return;

            string typeName = Preview.PlacedObjectNames()[Preview.SelectedIndex];
            if (!inspectorAssets.Definitions.TryGetValue(typeName, out var def) || string.IsNullOrEmpty(def.Model))
            {
                MessageBox.Show(this, "This object has no 3D model (it might be a trigger or effect).", "Model Tool");
                return;
            }

            string fullModelPath = inspectorAssets.Resolve(def.Model);
            if (!File.Exists(fullModelPath))
            {
                MessageBox.Show(this, "Model file not found: " + fullModelPath, "Model Tool");
                return;
            }

            string toolExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AS3DModelTool.exe");
            if (!File.Exists(toolExe))
            {
                MessageBox.Show(this, "AS3DModelTool.exe not found in the editor folder.", "Tool Missing");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = toolExe,
                    Arguments = $"\"{fullModelPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to launch Model Tool: " + ex.Message, "Error");
            }
        }

        /// <summary>
        /// Builds the top toolbar. Every button is a thin adapter over
        /// <see cref="GlTerrainControl"/>; no business logic lives here.
        /// </summary>
        private FlowLayoutPanel BuildToolbar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };

            var recentCombo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            recentCombo.Items.Add("(Recent objects...)");
            recentCombo.SelectedIndex = 0;

            recentCombo.DropDown += (s, e) =>
            {
                recentCombo.Items.Clear();
                recentCombo.Items.Add("(Recent objects...)");
                if (Preview.PlacementPending || Preview.SelectedIndex >= 0 || Preview.SelectedObjects.Length > 0)
                    recentCombo.Items.Add("(Deselect / Cancel placement)");
                foreach (var obj in RecentObjects) // <--- Use the class variable directly
                    recentCombo.Items.Add(obj);
            };

            recentCombo.SelectedIndexChanged += (s, e) =>
            {
                if (recentCombo.SelectedIndex > 0)
                {
                    string item = recentCombo.SelectedItem.ToString();
                    if (item == "(Deselect / Cancel placement)")
                    {
                        Preview.CancelPlacement();
                        Preview.SetObjectSelection(new int[0]);
                    }
                    else
                    {
                        Preview.ArmPlacement(item, -1);
                    }
                    Preview.Focus();
                    recentCombo.SelectedIndex = 0;
                }
            };
            bar.Controls.Add(recentCombo);

            // ... rest of the toolbar

            var gridSnap = new CheckBox { Text = "Snap to Grid", AutoSize = true };
            gridSnap.CheckedChanged += (s, e) => Preview.GridSnap = gridSnap.Checked;
            bar.Controls.Add(gridSnap);

            var gizmoCheck = new CheckBox { Text = "3D Gizmo", Checked = Preview.ShowGizmo, AutoSize = true };
            gizmoCheck.CheckedChanged += (s, e) => { Preview.ShowGizmo = gizmoCheck.Checked; Preview.Invalidate(); };
            bar.Controls.Add(gizmoCheck);

            bar.Controls.Add(new Label { Text = "|", AutoSize = true, Padding = new Padding(8, 5, 8, 0) });

            var fog = new CheckBox { Text = "Mission fog", Checked = Preview.Fog, AutoSize = true };
            fog.CheckedChanged += (s, e) => { Preview.Fog = fog.Checked; Preview.Changed(); };
            bar.Controls.Add(fog);

            var routeToggle = new CheckBox { Text = "Route", Checked = true, AutoSize = true };
            routeToggle.CheckedChanged += (s, e) => { Preview.ShowRoute = routeToggle.Checked; Preview.Changed(); };
            bar.Controls.Add(routeToggle);

            var addObject = new Button { Text = "Add object…", AutoSize = true };
            addObject.Click += (s, e) =>
            {
                if (sidebarTabs != null && paletteTab != null)
                {
                    sidebarTabs.SelectedTab = paletteTab;
                    paletteSearch?.Focus();
                }
                else
                {
                    Preview.ChooseScenery(this);
                    Preview.Focus();
                }
            };
            bar.Controls.Add(addObject);

            var replaceObject = new Button { Text = "Replace object…", AutoSize = true, Enabled = false };
            replaceObject.Click += (s, e) =>
            {
                if (sidebarTabs != null && paletteTab != null)
                {
                    sidebarTabs.SelectedTab = paletteTab;
                    paletteSearch?.Focus();
                }
                else
                {
                    Preview.ChooseScenery(this, true);
                }
            };
            Preview.SelectionChanged += (s, e) =>
                replaceObject.Enabled = Preview.SelectedObjects.Length >= 1 && !Preview.PlacementPending;
            bar.Controls.Add(replaceObject);

            var duplicate = new Button { Text = "Duplicate…", AutoSize = true, Enabled = false };
            duplicate.Click += (s, e) => { Preview.ArmDuplicate(); Preview.Focus(); };
            bar.Controls.Add(duplicate);

            var deleteObject = new Button { Text = "Delete", AutoSize = true, Enabled = false };
            deleteObject.Click += (s, e) => Preview.DeleteSelected();
            bar.Controls.Add(deleteObject);

            var deselectObject = new Button { Text = "Deselect", AutoSize = true, Enabled = false };
            deselectObject.Click += (s, e) =>
            {
                Preview.CancelPlacement();
                Preview.SetObjectSelection(new int[0]);
                Preview.Focus();
            };
            bar.Controls.Add(deselectObject);

            Action updateSelectionButtons = () =>
            {
                duplicate.Enabled = deleteObject.Enabled = Preview.SelectedIndex >= 0;
                deselectObject.Enabled = Preview.SelectedIndex >= 0 || Preview.SelectedObjects.Length > 0 || Preview.PlacementPending;
            };
            Preview.SelectionChanged += (s, e) => updateSelectionButtons();
            Preview.EditingModeChanged += (s, e) => updateSelectionButtons();

            var moveMode = new CheckBox { Text = "Move: drag object", AutoSize = true };
            RegisterEditingCheck(moveMode, EditingMode.Move);
            bar.Controls.Add(moveMode);

            var routeEdit = new CheckBox { Text = "Edit route", AutoSize = true };
            RegisterEditingCheck(routeEdit, EditingMode.Route);
            Preview.EditingModeChanged += (s, e) => { if (Preview.EditRoute) routeToggle.Checked = true; };
            bar.Controls.Add(routeEdit);

            var moveRoutes = new CheckBox { Text = "Move waypoints", Checked = true, AutoSize = true };
            moveRoutes.CheckedChanged += (s, e) => Preview.MoveRoutes = moveRoutes.Checked;
            bar.Controls.Add(moveRoutes);

            var rotateLeft = new Button { Text = "Rotate −30°", AutoSize = true, Enabled = false };
            var rotateRight = new Button { Text = "Rotate +30°", AutoSize = true, Enabled = false };
            rotateLeft.Click += (s, e) =>
            {
                if (Preview.PlacementPending) Preview.RotatePending(-1);
                else Preview.RotateSelected(-1);
                Preview.Focus();
            };
            rotateRight.Click += (s, e) =>
            {
                if (Preview.PlacementPending) Preview.RotatePending(+1);
                else Preview.RotateSelected(+1);
                Preview.Focus();
            };
            bar.Controls.Add(rotateLeft);
            bar.Controls.Add(rotateRight);
            Preview.SelectionChanged += (s, e) =>
                rotateLeft.Enabled = rotateRight.Enabled =
                    Preview.PlacementPending || Preview.SelectedObjects.Length == 1;

            var undo = new Button { Text = "Undo", AutoSize = true };
            undo.Click += (s, e) => Preview.HistoryMove(false);
            bar.Controls.Add(undo);

            var redo = new Button { Text = "Redo", AutoSize = true };
            redo.Click += (s, e) => Preview.HistoryMove(true);
            bar.Controls.Add(redo);

            var cx = new NumericUpDown { DecimalPlaces = 2, Minimum = -10000, Maximum = 10000, Value = (decimal)Preview.CenterX, Width = 80 };
            var cy = new NumericUpDown { DecimalPlaces = 2, Minimum = -10000, Maximum = 10000, Value = (decimal)Preview.CenterY, Width = 80 };
            bar.Controls.Add(new Label { Text = "Center X / Y", AutoSize = true });
            bar.Controls.Add(cx);
            bar.Controls.Add(cy);

            var go = new Button { Text = "Go", AutoSize = true };
            go.Click += (s, e) => { Preview.CenterX = (double)cx.Value; Preview.CenterY = (double)cy.Value; Preview.Changed(); };
            bar.Controls.Add(go);

            var gameCamera = new Button { Text = "Game camera", AutoSize = true };
            gameCamera.Click += (s, e) =>
            {
                Preview.Pitch = 45;
                Preview.Heading = -90;
                Preview.Distance = 270 / Math.Sin(Math.PI / 4);
                Preview.Changed();
            };
            bar.Controls.Add(gameCamera);

            var saveScreenshot = new Button { Text = "Save screenshot", AutoSize = true };
            saveScreenshot.Click += (s, e) =>
            {
                using (var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "opengl-preview.png" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        try { Preview.SavePreview(dialog.FileName); }
                        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
                    }
                }
            };
            bar.Controls.Add(saveScreenshot);

            var previewRoute = new CheckBox { Text = "Preview Route", AutoSize = true };
            previewRoute.CheckedChanged += (s, e) => { Preview.PreviewRoute = previewRoute.Checked; Preview.Changed(); };
            bar.Controls.Add(previewRoute);
            return bar;
        }

        private void UpdateStatus()
        {
            status.Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Preview.EditingInstructions +
                " • {0}\r\nCenter X {1:F2}, Y {2:F2} • Tilt {3:F1}° • Distance {4:F0} • " +
                "Lighting/water approximate; objects: {5}; markers: {6} " +
                "(height/attachments provisional)",
                Preview.Renderer,
                Preview.CenterX,
                Preview.CenterY,
                Preview.Pitch,
                Preview.Distance,
                Preview.SceneryCount,
                Preview.MarkerCount);
        }
    }
}