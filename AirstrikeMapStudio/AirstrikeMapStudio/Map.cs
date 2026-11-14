using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace AirstrikeStudio
{
    public class Waypoint
    {
        public int X;
        public int Y;
        public int Mode;
        public float AX;
        public float AY;
        public float BX;
        public float BY;
        public float Value;

        public Waypoint Clone() => (Waypoint)MemberwiseClone();

        public void Shift(int x, int y)
        {
            X += x;
            Y += y;
            AX += x;
            AY += y;
            BX += x;
            BY += y;
        }
    }

    public class Placement
    {
        public ushort Type;

        /// <summary>
        /// Grid X/Y stored as signed 16-bit integers (-32768..32767) in .hsc
        /// so objects can be placed outside all four map edges (including
        /// negative X and Y) while remaining byte-for-byte identical for all
        /// standard 0..32767 coordinates.
        /// </summary>
        public short X;
        public short Y;

        public byte Setting6;
        public byte Setting7;

        public byte Facing
        {
            get => Setting6;
            set => Setting6 = value;
        }

        public byte DropIndex
        {
            get => Setting7;
            set => Setting7 = value;
        }

        public short Link;
        public string Script = "";
        public List<Waypoint> Route = new List<Waypoint>();

        public Placement Clone()
        {
            var p = (Placement)MemberwiseClone();
            p.Route = new List<Waypoint>();
            foreach (var w in Route)
                p.Route.Add(w.Clone());
            return p;
        }

        public void Shift(int dx, int dy, bool route)
        {
            X = checked((short)(X + dx));
            Y = checked((short)(Y + dy));
            if (route)
                foreach (var w in Route)
                    w.Shift(dx, dy);
        }
    }

    public static class RouteTools
    {
        public static bool AheadOfSpawn(Placement p)
        {
            foreach (var w in p.Route)
                if (w.Y > p.Y || w.AY > p.Y || w.BY > p.Y)
                    return true;
            return false;
        }

        public static void InsertWaypoint(Placement p, int index, int height)
        {
            var w = NewWaypoint(p, index, height);
            if (p.Route.Count == 0)
                p.Link = 0;
            p.Route.Insert(index, w);
        }

        public static bool AlignArrival(Placement p, int index)
        {
            if (index < 0 || index >= p.Route.Count) return false;

            var w = p.Route[index];
            int x = p.X, y = p.Y;

            for (int i = index - 1; i >= 0; i--)
            {
                if (p.Route[i].X != w.X || p.Route[i].Y != w.Y)
                {
                    x = p.Route[i].X;
                    y = p.Route[i].Y;
                    break;
                }
            }

            if (x == w.X && y == w.Y) return false;

            w.AX = w.X + (x - w.X) * 0.5f;
            w.AY = w.Y + (y - w.Y) * 0.5f;
            return true;
        }

        public static Waypoint NewWaypoint(Placement p, int index, int height)
        {
            int x = index > 0 ? p.Route[index - 1].X : p.X;
            int oldY = index > 0 ? p.Route[index - 1].Y : p.Y;

            // Allow waypoints to be created at any negative or positive coordinate
            int y = oldY - 3;

            return new Waypoint
            {
                X = x,
                Y = y,
                Mode = 1,
                AX = x,
                AY = (oldY + y) * 0.5f,
                BX = x,
                BY = y + (y - oldY) * 0.5f,
            };
        }

        public static Waypoint WaypointAt(Placement p, int index, int x, int y)
        {
            int prevX = index > 0 ? p.Route[index - 1].X : p.X;
            int prevY = index > 0 ? p.Route[index - 1].Y : p.Y;
            float dx = x - prevX;
            float dy = y - prevY;

            return new Waypoint
            {
                X = x,
                Y = y,
                Mode = 1,
                AX = x - dx * 0.35f,
                AY = y - dy * 0.35f,
                BX = x + dx * 0.35f,
                BY = y + dy * 0.35f,
            };
        }

        public static void Reverse(Placement p)
        {
            if (p == null || p.Route.Count < 2) return;
            p.Route.Reverse();
            foreach (var w in p.Route)
            {
                float tx = w.AX, ty = w.AY;
                w.AX = w.BX;
                w.AY = w.BY;
                w.BX = tx;
                w.BY = ty;
            }
        }

        public static void AutoSmooth(Placement p, bool straight = false)
        {
            if (p == null || p.Route.Count == 0) return;

            for (int i = 0; i < p.Route.Count; i++)
            {
                var w = p.Route[i];
                if (straight)
                {
                    w.AX = w.BX = w.X;
                    w.AY = w.BY = w.Y;
                    w.Mode = 0;
                    continue;
                }

                double prevX = i > 0 ? p.Route[i - 1].X : p.X;
                double prevY = i > 0 ? p.Route[i - 1].Y : p.Y;
                double nextX = i + 1 < p.Route.Count ? p.Route[i + 1].X : w.X + (w.X - prevX);
                double nextY = i + 1 < p.Route.Count ? p.Route[i + 1].Y : w.Y + (w.Y - prevY);

                float tx = (float)((nextX - prevX) * 0.25);
                float ty = (float)((nextY - prevY) * 0.25);
                w.AX = w.X - tx;
                w.AY = w.Y - ty;
                w.BX = w.X + tx;
                w.BY = w.Y + ty;
                w.Mode = 1;
            }
        }
    }

    public class Map
    {
        public const int MinMapDimension = 16;
        public const int MaxMapWidth = 512;
        public const int MaxMapHeight = 1024;

        private const uint HmapMagic = 0x50414D48u; // "HMAP" little-endian
        private const uint HmapVersion = 2u;
        private const int MaxObjects = 200000;
        private const int MaxTypes = 65535;
        private const int MaxDrops = 65535;
        private const int MaxGridCells = 4_000_000;

        public int Width;
        public int Height;
        public List<string> Types = new List<string>();
        public List<string> Drops = new List<string>();
        public uint[] Cells;
        public List<Placement> Objects = new List<Placement>();

        private static readonly Encoding Latin = Encoding.GetEncoding(28591);

        private static int Count(BinaryReader r, int max)
        {
            uint n = r.ReadUInt32();
            if (n > max)
                throw new InvalidDataException("Invalid or unsupported count: " + n);
            return (int)n;
        }

        private static string ReadName(BinaryReader r, bool optional)
        {
            int n = r.ReadByte();
            if (n == 0)
            {
                if (optional) return "";
                throw new InvalidDataException("Empty name entry.");
            }

            byte[] b = r.ReadBytes(n);
            if (b.Length != n || b[n - 1] != 0)
                throw new InvalidDataException("Invalid name length or terminator.");

            return Latin.GetString(b, 0, n - 1);
        }

        private static void WriteName(BinaryWriter w, string s, bool optional)
        {
            if (optional && s.Length == 0)
            {
                w.Write((byte)0);
                return;
            }

            if (s.IndexOf('\0') >= 0)
                throw new InvalidDataException("Names cannot contain a null character.");
            foreach (char c in s)
                if (c > 255)
                    throw new InvalidDataException("Names must use single-byte characters.");

            byte[] b = Latin.GetBytes(s);
            if (b.Length > 254)
                throw new InvalidDataException("Name too long (maximum 254 bytes).");

            w.Write((byte)(b.Length + 1));
            w.Write(b);
            w.Write((byte)0);
        }

        public static Map Load(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
            {
                if (r.ReadUInt32() != HmapMagic || r.ReadUInt32() != HmapVersion)
                    throw new InvalidDataException(
                        "Only HMAP version 2 is supported. No file was changed.");

                var m = new Map();
                m.Width = Count(r, 4096);
                m.Height = Count(r, 4096);

                int objectCount = Count(r, MaxObjects);
                int typeCount = Count(r, MaxTypes);
                int dropCount = Count(r, MaxDrops);

                if (m.Width < 1 || m.Height < 1 || (long)m.Width * m.Height > MaxGridCells)
                    throw new InvalidDataException("Unsupported grid dimensions.");

                for (int i = 0; i < typeCount; i++)
                    m.Types.Add(ReadName(r, optional: false));
                for (int i = 0; i < dropCount; i++)
                    m.Drops.Add(ReadName(r, optional: false));

                m.Cells = new uint[m.Width * m.Height];
                for (int i = 0; i < m.Cells.Length; i++)
                    m.Cells[i] = r.ReadUInt32();

                for (int i = 0; i < objectCount; i++)
                {
                    var p = new Placement();
                    p.Type = r.ReadUInt16();
                    p.X = r.ReadInt16();
                    p.Y = r.ReadInt16();
                    p.Setting6 = r.ReadByte();
                    p.Setting7 = r.ReadByte();

                    int routeCount = r.ReadUInt16();
                    p.Script = ReadName(r, optional: true);
                    p.Link = r.ReadInt16();

                    if (p.Type >= typeCount)
                        throw new InvalidDataException("Object refers to a missing prototype.");

                    for (int j = 0; j < routeCount; j++)
                    {
                        p.Route.Add(new Waypoint
                        {
                            X = r.ReadInt32(),
                            Y = r.ReadInt32(),
                            Mode = r.ReadInt32(),
                            AX = r.ReadSingle(),
                            AY = r.ReadSingle(),
                            BX = r.ReadSingle(),
                            BY = r.ReadSingle(),
                            Value = r.ReadSingle(),
                        });
                    }

                    m.Objects.Add(p);
                }

                if (ms.Position != ms.Length)
                    throw new InvalidDataException(
                        "Unknown trailing data. This file is not safe to edit with this version.");

                return m;
            }
        }

        public byte[] Bytes()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(HmapMagic);
                w.Write(HmapVersion);
                w.Write((uint)Width);
                w.Write((uint)Height);
                w.Write((uint)Objects.Count);
                w.Write((uint)Types.Count);
                w.Write((uint)Drops.Count);

                foreach (string s in Types)
                    WriteName(w, s, optional: false);
                foreach (string s in Drops)
                    WriteName(w, s, optional: false);

                foreach (uint c in Cells)
                    w.Write(c);

                foreach (var p in Objects)
                {
                    if (p.Route.Count > 65535)
                        throw new InvalidDataException("Too many waypoints.");

                    w.Write(p.Type);
                    w.Write(p.X);
                    w.Write(p.Y);
                    w.Write(p.Setting6);
                    w.Write(p.Setting7);
                    w.Write((ushort)p.Route.Count);
                    WriteName(w, p.Script, optional: true);
                    w.Write(p.Link);

                    foreach (var v in p.Route)
                    {
                        w.Write(v.X);
                        w.Write(v.Y);
                        w.Write(v.Mode);
                        w.Write(v.AX);
                        w.Write(v.AY);
                        w.Write(v.BX);
                        w.Write(v.BY);
                        w.Write(v.Value);
                    }
                }

                w.Flush();
                return ms.ToArray();
            }
        }

        public ushort TypeId(string name)
        {
            int n = Types.IndexOf(name);
            if (n < 0)
            {
                if (Types.Count >= MaxTypes)
                    throw new InvalidDataException("Prototype limit reached.");
                Types.Add(name);
                n = Types.Count - 1;
            }
            return (ushort)n;
        }

        public void Resize(int newWidth, int newHeight, byte fillHeight)
        {
            if (newWidth < MinMapDimension || newWidth > MaxMapWidth ||
                newHeight < MinMapDimension || newHeight > MaxMapHeight)
                throw new ArgumentException("New dimensions are outside allowed bounds.");

            if (newWidth == Width && newHeight == Height) return;

            var newCells = new uint[newWidth * newHeight];

            for (int i = 0; i < newCells.Length; i++)
                newCells[i] = fillHeight;

            int copyWidth = Math.Min(Width, newWidth);
            int copyHeight = Math.Min(Height, newHeight);

            for (int y = 0; y < copyHeight; y++)
            {
                for (int x = 0; x < copyWidth; x++)
                {
                    newCells[y * newWidth + x] = Cells[y * Width + x];
                }
            }

            Width = newWidth;
            Height = newHeight;
            Cells = newCells;
        }

        public static Map CreateBlank(Map template, int width, int height, byte startingHeight)
        {
            if (width < MinMapDimension || width > MaxMapWidth)
                throw new ArgumentException(
                    "Width must be " + MinMapDimension + "–" + MaxMapWidth + ".");
            if (height < MinMapDimension || height > MaxMapHeight)
                throw new ArgumentException(
                    "Height must be " + MinMapDimension + "–" + MaxMapHeight + ".");
            if ((long)width * height > MaxGridCells)
                throw new ArgumentException("Map is too large.");

            var m = New(width, height);
            m.Types.AddRange(template.Types);
            m.Drops.AddRange(template.Drops);

            for (int i = 0; i < m.Cells.Length; i++)
                m.Cells[i] = startingHeight;

            return Load(m.Bytes());
        }

        public static Map New(int width, int height)
        {
            return new Map
            {
                Width = width,
                Height = height,
                Cells = new uint[width * height],
            };
        }

        public string Validate()
        {
            var issues = new List<string>();

            for (int i = 0; i < Objects.Count; i++)
            {
                var p = Objects[i];

                for (int j = 0; j < p.Route.Count; j++)
                {
                    var w = p.Route[j];

                    foreach (float f in new[] { w.AX, w.AY, w.BX, w.BY, w.Value })
                        if (float.IsNaN(f) || float.IsInfinity(f))
                            issues.Add("Non-finite waypoint value at object " + i);
                }
            }

            return issues.Count == 0
                ? "No structural issues found."
                : string.Join("\r\n", issues.ToArray());
        }

        public static void Save(string path, byte[] data)
        {
            Load(data);

            string full = Path.GetFullPath(path);
            string temp = full + ".studio-" + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllBytes(temp, data);

                if (File.Exists(full))
                {
                    string backup = full + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                    File.Replace(temp, full, backup);
                }
                else
                {
                    File.Move(temp, full);
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    public class WaypointProperties
    {
        private readonly Waypoint w;

        public WaypointProperties(Waypoint wp) { w = wp; }

        [Category("Position"),
         Description("Grid X coordinate of the waypoint center (1 grid cell = 40 world units). " +
                     "Changing X shifts both the approach (A) and departure (B) curve handles by the same delta.")]
        public int X
        {
            get => w.X;
            set { int d = value - w.X; w.Shift(d, 0); }
        }

        [Category("Position"),
         Description("Grid Y coordinate of the waypoint center (1 grid cell = 40 world units). " +
                     "Changing Y shifts both the approach (A) and departure (B) curve handles by the same delta.")]
        public int Y
        {
            get => w.Y;
            set { int d = value - w.Y; w.Shift(0, d); }
        }

        [Category("Route curve"), DisplayName("Approach handle X"),
         Description("Incoming cubic Bézier control point X (+0x0C in the 32-byte waypoint struct). " +
                     "Controls the arrival curve from the previous waypoint into this waypoint.")]
        public float HandleAX { get => w.AX; set => w.AX = value; }

        [Category("Route curve"), DisplayName("Approach handle Y"),
         Description("Incoming cubic Bézier control point Y (+0x10 in the 32-byte waypoint struct). " +
                     "Controls the arrival curve from the previous waypoint into this waypoint.")]
        public float HandleAY { get => w.AY; set => w.AY = value; }

        [Category("Route curve"), DisplayName("Departure handle X"),
         Description("Outgoing cubic Bézier control point X (+0x14 in the 32-byte waypoint struct). " +
                     "Controls the departure curve leaving this waypoint toward the next waypoint.")]
        public float HandleBX { get => w.BX; set => w.BX = value; }

        [Category("Route curve"), DisplayName("Departure handle Y"),
         Description("Outgoing cubic Bézier control point Y (+0x18 in the 32-byte waypoint struct). " +
                     "Controls the departure curve leaving this waypoint toward the next waypoint.")]
        public float HandleBY { get => w.BY; set => w.BY = value; }

        [Category("Raw settings"),
         Description("Editor tangent handle mode stored at offset +0x08 of the 32-byte waypoint record. " +
                     "Confirmed via binary analysis of AirStrike3D.exe (0x40955A–0x409725 and 0x405F20–0x40609E): " +
                     "the runtime game engine never reads this field—it only evaluates X, Y, AX, AY, BX, BY, and Wait. " +
                     "Across 8,995 stock waypoints: Mode 1 (80%) is Smooth/Mirrored handles ((A - W) = -(B - W)), " +
                     "and Mode 0 (20%) is Free/Corner handles (independent incoming/outgoing tangents). " +
                     "When Mode is 1, dragging one handle in the 3D editor mirrors the opposite handle (hold Alt to break).")]
        public int Mode { get => w.Mode; set => w.Mode = value; }

        [Category("Waypoint behavior"), DisplayName("Waypoint wait time"),
         Description("Used as a wait timer by rocket-launcher scripts. 0 means " +
                     "no wait; stock launchers often use 100 at the last " +
                     "waypoint. This is not an attack-enable switch; turret " +
                     "aiming and weapon scripts still control firing. Seconds " +
                     "were confirmed for the tested rocketlauncher_mobile_lgreen " +
                     "scripts. The first wait runs at spawn and is skipped when " +
                     "the route wraps; other scripts remain unverified. A " +
                     "completed non-looping launcher route can deactivate its " +
                     "weapons.")]
        public float FinalValue { get => w.Value; set => w.Value = value; }
    }
}